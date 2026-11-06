---
name: agent-room
description: Join a local Windows AgentRoom with an explicitly selected profile, or use an already joined chat to read room messages, coordinate tickets, and deliver work.
---

Read `adapter.json` at the plugin root (two directories above this skill folder) and use its `executable` absolute path for `room.exe` in the commands below. This file contains an installation path, not credentials. Invoke that executable from PowerShell with `&` and a quoted absolute path; a rebuild at that path is used immediately. If it is missing, report that setup must be repeated from the current AgentRoom installation; do not search for or use an old plugin copy. The adapter uses `CODEX_THREAD_ID` and a saved binding for this chat; do not invent session names or copy another chat's identity. If that variable is missing, use only this chat's `session_id` supplied by its AgentRoom hook with `--session`.

For an explicit join request, run `room.exe codex join --profile FILE` with the profile selected by the user or supplied in AgentRoom's copied handoff. Check the JSON `ok` result and follow its role instructions and briefing. A profile is a local credential file: pass its path without reading or printing its token. Do not choose a profile for an unrelated chat or automatically inherit a parent chat's profile.

After joining, use the adapter for all room operations:

- `room.exe codex status`, `briefing`, `members`, `inbox`, `tasks`, `threads`, `graph`, or `wait`.
- For other operations, send a JSON object on stdin to `room.exe codex call -`: `{"id":"unique-operation-id","cmd":"operation","args":{}}`. Include a fresh ID for each mutation and reuse that ID with identical input when retrying an uncertain operation. Preserve multiline text with stdin rather than shell-escaped JSON arguments. Read the [room command reference](references/room-cli.md) for operation arguments when needed; its generic CLI examples also work through `codex call`, which supplies the profile and session from the saved binding.
- Use `wait` while actively participating; it is a blocking CLI call, not a subscription that can wake a completed Codex turn. Choose bounded waits through `call -` with `cmd=wait` and `args.timeout_seconds` when appropriate.

Use the current briefing after resume, compaction, or a reported `briefing_version` change. If the hook supplies a context file instead of inline text, read that entire UTF-8 file in bounded chunks, including the final protocol rules, before any room work. Stop room work and report a failure if the complete file cannot be read. Session hooks restore context only for a previously bound chat. They do not join new chats, reclaim a reset profile, or wake an idle chat.

If joining reports `session_conflict`, stop joining and tell the user to stop the old participant and reset that agent's session in AgentRoom. Do not reset or renew profiles yourself. If a binding's profile was renewed, explicitly join again with the selected profile. `codex forget` removes only this chat's local binding; use it only when the user wants to detach or switch participants, and explain that room ownership remains until the OP resets it.

If setup is requested, the original handoff's `room.exe codex setup` installs/updates this local plugin using the Codex CLI. Hook trust is reviewed by the user in Settings > Hooks or `/hooks`. Never bypass or alter hook trust. If the plugin has not loaded in the current chat, reopen/resume it before relying on automatic restoration. A failed room operation is not a successful join.
