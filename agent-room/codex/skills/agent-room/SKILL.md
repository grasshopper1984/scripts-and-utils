---
name: agent-room
description: Join a local Windows AgentRoom with an explicitly selected profile, or use an already joined chat to read room messages, coordinate tickets, and deliver work.
---

Read `adapter.json` at the plugin root (two directories above this skill folder) and use its `executable` absolute path for `room.exe` in the commands below. This file contains an installation path, not credentials. Invoke that executable from PowerShell with `&` and a quoted absolute path; a rebuild at that path is used immediately. If it is missing, report that setup must be repeated from the current AgentRoom installation; do not search for or use an old plugin copy. The adapter uses `CODEX_THREAD_ID` and a saved binding for this chat; do not invent session names or copy another chat's identity. If that variable is missing, use only this chat's `session_id` supplied by its AgentRoom hook with `--session`.

For an explicit join request, run `room.exe codex join --profile FILE` with the profile selected by the user or supplied in AgentRoom's copied handoff. Check the JSON `ok` result and follow its `context`, which holds your role instructions and the full briefing. A profile is a local credential file: pass its path without reading or printing its token. Do not choose a profile for an unrelated chat or automatically inherit a parent chat's profile.

After joining, use the adapter for all room operations:

- `room.exe codex status`, `briefing`, `members`, `inbox`, `tasks`, `threads`, `graph`, or `wait`.
- For other operations, send a JSON object on stdin to `room.exe codex call -`: `{"id":"unique-operation-id","cmd":"operation","args":{}}`. Include a fresh ID for each mutation and reuse that ID with identical input when retrying an uncertain operation. Preserve multiline text with stdin rather than shell-escaped JSON arguments. Windows PowerShell 5.1 pipes text to programs as ASCII: begin the same command with `$OutputEncoding = [Text.UTF8Encoding]::new($false);` or every non-ASCII character reaches the room as `?`. Read the [room command reference](references/room-cli.md) for operation arguments when needed; its generic CLI examples also work through `codex call`, which supplies the profile and session from the saved binding.
- For idle listening, run `wait` with no `timeout_seconds`: the room sets how long it blocks (1500 seconds unless the OP changed it; up to 86400 is accepted). Pass the IDs of notes you have read as `ack` on that call (`{"cmd":"wait","args":{"ack":["12","15"]}}`) instead of acknowledging them one by one. Reserve short timeouts for deliberate inbox checks while doing other work.

The room wait lifetime and shell tool yield interval are separate. With `exec_command` and `write_stdin`:

1. Start one listener with the adapter command followed by `wait`. Use a short initial `yield_time_ms` (for example, 1000; the runtime may clamp it) so the shell tool returns a process `session_id` while `room.exe` keeps waiting. For room scope or other arguments, use `call -` with `{"cmd":"wait","args":{"scope":"room","after":123,"timeout_seconds":1500}}`; replace the example cursor with the last returned cursor. The plain `wait` command uses inbox scope.
2. Poll the returned process `session_id` with `write_stdin`, empty `chars`, and `yield_time_ms` 1500000. Every return to you is a model call, so make one call cover the whole wait. If your tools run through `exec`, give the script the same long yield, or `exec` hands back after 30 seconds and you end up calling `wait` over and over:

   ```js
   // @exec: {"yield_time_ms": 1500000, "max_output_tokens": 12000}
   text(await tools.write_stdin({session_id: 123, chars: "", yield_time_ms: 1500000}))
   ```

   If a call still returns `Script running with cell ID`, call `wait` once with that `cell_id` and `yield_time_ms` 1500000, never in short slices. These calls return as soon as the listener exits, so they do not delay mail. Poll again only when one returns with the listener still running. This numeric shell process ID is separate from the Codex chat identity; never pass it to the adapter's `--session`. Empty output or a shell yield means the same listener is still running, not that the room wait timed out. Keep polling that process; never start a second listener while it is running or restart it every 50-60 seconds.
3. When the process exits, inspect its exit code and complete JSON result. Handle messages and briefing changes, or a normal `timed_out: true`, then start the next wait if participation should continue. Report errors instead of treating them as an empty inbox. If the tool loses the process session, resolve or stop the original listener before replacing it.

Keep the Codex turn active while listening: this CLI process cannot wake a completed turn. If only a synchronous shell tool is available, follow its actual execution limit and state the limitation; never imply background delivery. Short tool checks can still require model turns. Say nothing while listening: a quiet room is normal, and no message is needed between polls. Do not write that you are still listening, available, or waiting, in the chat or the room. Write only when the listener returns something to act on. Use the shell yield interval to stay responsive, not a 50-second `args.timeout_seconds` for an idle listener.

Use the current briefing after resume, compaction, or a reported `briefing_version` change. If the hook supplies a context file instead of inline text, read that entire UTF-8 file in bounded chunks, including the final protocol rules, before any room work. Stop room work and report a failure if the complete file cannot be read. Session hooks restore context only for a previously bound chat. They do not join new chats, reclaim a reset profile, or wake an idle chat.

If joining reports `session_conflict`, stop joining and tell the user to stop the old participant and reset that agent's session in AgentRoom. Do not reset or renew profiles yourself. If a binding's profile was renewed, explicitly join again with the selected profile. `codex forget` removes only this chat's local binding; use it only when the user wants to detach or switch participants, and explain that room ownership remains until the OP resets it.

If setup is requested, the original handoff's `room.exe codex setup` installs/updates this local plugin using the Codex CLI. Hook trust is reviewed by the user in Settings > Hooks or `/hooks`. Never bypass or alter hook trust. If the plugin has not loaded in the current chat, reopen/resume it before relying on automatic restoration. A failed room operation is not a successful join.
