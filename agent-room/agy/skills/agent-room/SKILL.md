---
name: agent-room
description: Join a local Windows AgentRoom with an explicitly selected profile in Antigravity, or use an already joined conversation to read room messages, coordinate tickets, and deliver work.
---

Read `adapter.json` at the plugin root (two directories above this skill folder) and use its `executable` absolute path for `room.exe` and its `home` path for `--agy-home` in every command below. This file contains installation paths, not credentials. Invoke `room.exe` via `run_command` using PowerShell's `&` operator and single-quoted literal paths (double any embedded apostrophe). Request the tool's required permission for local named-pipe access. If either path is missing, report that `room.exe agy setup` must be repeated from the current AgentRoom installation; do not search for or use an old copy.

Pass this conversation's `Conversation ID` (from `<user_information>`) using `--session <ConversationId>`. Do not invent session names or copy another conversation's identity.

For an explicit join request, run:
```powershell
& 'D:\path\to\room.exe' agy --agy-home 'C:\path\to\config' join --profile 'C:\path\to\agent.profile.json' --session '<ConversationId>'
```
Check the JSON `ok` result and follow its role instructions and briefing. A profile is a local credential file: pass its path without reading or printing its token. Do not choose a profile for an unrelated conversation or inherit a parent conversation's profile.

After joining, use the saved binding for all room operations (no `--profile` flag is needed after joining):

- **Direct reads**: use the returned `adapter_command` followed by `status`, `briefing`, `members`, `inbox`, `feed`, `tasks`, `threads`, `graph`, `artifacts`, `audit`, or `escalations`. The command preserves this conversation's identity and configured home.
- **Mutations and parameterized calls**: Write the JSON request (`{"id":"unique-operation-id","cmd":"operation","args":{}}`) to `<appDataDir>\brain\<ConversationId>\scratch\request.json` using `write_file`, then run:
  ```powershell
  & 'D:\path\to\room.exe' agy --agy-home 'C:\path\to\config' --session '<ConversationId>' call 'C:\path\to\scratch\request.json'
  ```
  Include a fresh `id` for each mutation and reuse that `id` with identical input when retrying an uncertain operation. Read the [room command reference](references/room-cli.md) for operation arguments when needed.
- **Listening (`wait`)**: Write `wait.json` (`{"id":"wait-1","cmd":"wait","args":{"scope":"inbox","timeout_seconds":1680}}` for workers/coordinators, or `"scope":"room","after":<cursor>` for the moderator) to `<appDataDir>\brain\<ConversationId>\scratch\wait.json`, then run the returned `adapter_command` followed by `call '<absolute wait.json path>'` via `run_command` with `WaitMsBeforeAsync: 500` and the required local named-pipe permission. Once the background task starts, end your turn; Antigravity will automatically wake the conversation with a `<SYSTEM_MESSAGE>` when mail arrives or the wait times out.

The `PreInvocation` hook verifies the saved binding and supplies current role instructions and the complete briefing as temporary context before each model call, including existing chats and after resume or compaction. If it supplies a context file instead of inline text, read that entire UTF-8 file in bounded chunks, including the final protocol rules, before any room work. Stop room work and report a failure if the complete file cannot be read. Hooks restore context only for a previously bound conversation; they do not join new conversations or reclaim a reset profile.

If joining reports `session_conflict`, stop joining and tell the user to stop the old participant and reset that agent's session in AgentRoom. Do not reset or renew profiles yourself. If a binding's profile was renewed, explicitly join again with the selected profile. Append `forget` to the returned `adapter_command` to remove only this conversation's local binding; use it only when the user wants to detach or switch participants, and explain that room ownership remains until the OP resets it.
