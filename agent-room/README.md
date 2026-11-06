# Local Agent Room

A native Windows hub and viewer where independent agent sessions coordinate through durable inboxes, a task DAG, artifact records, and explicit worktree reservations. The hub makes no model calls and opens no TCP listener.

The roles are deliberately lopsided so the OP does little:

- **OP (room operator):** configures the repository and delivery directory, registers and deregisters agents, pauses or resets the room, and answers escalations. The OP does not post in the room; they direct the work by talking to the coordinator in its own session.
- **Coordinator:** the OP's point of contact. Plans and assigns tickets, reviews results, integrates, and takes tickets itself when useful, so it is sometimes busy; the moderator covers for it then.
- **Moderator:** watches the whole room, keeps exchanges moving, restricts misbehaving agents (including the coordinator), tops up conversation budgets, and escalates to the OP. It never takes tickets.
- **Workers:** receive tickets from the coordinator and report back; the hub lets them claim only tickets assigned to them. **Observers** only read.

Each agent receives a **briefing** when it joins: its role playbook from [roles/](roles/) followed by [PROTOCOL.md](PROTOCOL.md), so nothing has to be installed in the agent's session first. Every agent follows the protocol: message kinds, replies, addressing, the `wait` loop, and escalation. The hub does **not** launch agents, perform Git integration, or stop a running shell command. `wait` lets an agent block without spending tokens; whether a finished wait wakes an idle session depends on the harness (it does in Claude Code). Read [DESIGN.md](DESIGN.md) for the architecture and authority boundaries.

## Build and run

Windows 10 or 11 with .NET Framework 4.7.2 or later is required. The build uses the compiler included with Windows. SQLite comes from Windows System32; no package restore or additional database installation is needed. Git is required only for repository/worktree registration.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\bin\AgentRoom.exe
```

Or double-click `launch.cmd`, which builds on first use. Only `AgentRoom.exe` and `room.exe` are needed after building. The GUI closes to the notification area; use its tray menu **Exit hub** to shut down.

### Updating a running room

You can rebuild while the hub and agents are running. The build compiles into `bin\staging` and then swaps each executable in; a copy still in use is renamed to `*.old-<time>` and keeps running, and the next build deletes those copies once nothing uses them. A failed build leaves the current executables in place. Then:

1. **Restart hub** (tray menu, or **Workspace**) starts the new `AgentRoom.exe` and exits the old one. The new hub waits for the old one to release the room before serving it. Room data is kept, so agents keep their sessions, inboxes, and tickets. A `wait` in progress reconnects on its own; any other call fails with `hub_unavailable` for a few seconds and can be retried with the same request ID.
2. Each agent's MCP connector keeps running the old `room.exe` until its harness restarts the server, for example `/mcp` reconnect in Claude Code. A connector keeps working with the new hub as long as the protocol version is unchanged; after a protocol upgrade, the hub rejects an older connector with `protocol_version` until that connector restarts.

Playbook and protocol changes need no rebuild: edit them from **Workspace → Customize playbooks** and agents pick them up at their next briefing.

The default data directory is `%LOCALAPPDATA%\AgentRoom`. A separate room can be opened with `AgentRoom.exe --data <empty-local-directory>`. Its participants, database, and pipe are independent. Use a dedicated directory, not a repository root. The app restricts its data directory to the current Windows user. Back up the entire directory after exiting the hub; copying only a live `.db` file may miss its WAL contents. There is no automatic cloud backup or telemetry.

## First room

1. In **Workspace**, choose **Repository** and select the main checkout. Choose a **Delivery directory** for files agents will deliver.
2. In **Agents**, **Register agent** for the moderator first, then the coordinator, then workers. Choose a stable mention handle, role, connector, and role instructions. A room has one moderator and one coordinator. **Generic (MCP)** is the default connector; choose **Codex (local)** for the Codex adapter described below. Any role can use either connector.
3. Registration saves a local credential profile and copies join instructions for the selected connector. **Copy join instructions** copies them again. Generic instructions contain the original join message and MCP config; Codex instructions contain setup and join commands. Give the handoff only to the intended session. **More** retains **Copy config only** and **Renew profile**. **Edit agent** can change the role or connector; changing the connector does not reset a session or change its permissions.
4. Paste the moderator's instructions into its intended session and join; the response returns its moderator briefing. Start the coordinator and workers the same way. Generic connectors need a distinct session ID for each harness session; Codex uses its chat ID. Do not share one profile/session pair between different agents.
5. Tell the coordinator what you want built. It creates and assigns tickets, addresses workers, takes some tickets itself, and reviews results. Workers claim their assigned tickets, work in their own worktrees, and reply with results. Each ticket's conversation stays in its own thread.

The MCP session value is created once at registration and reused by both copy buttons, including after app restarts. Only **Reset session** or **Renew profile** generates a replacement; copy the updated MCP config after either action. Copying, joining, editing a role or connector preference, and resetting chats do not change that value. Native Codex joins continue to use the actual chat identity.

The GUI shows self-reported status and last contact; it does not infer that a quiet agent has stopped working. **Reset session** permits a replacement session to join but does not stop the old process. **Renew profile** replaces its credential and invalidates the old one. **Deregister** disables the identity permanently; existing worktree reservations remain intact. A room has one coordinator and one moderator: registering a second is refused with the current holder's handle. To replace that agent's session, use **Reset session** on it.

## Views and navigation

The sidebar lists every page. Its foot shows the hub, delivery, moderator, and escalation state, with the room's two emergency controls:

- **Pause room / Resume room** holds every new actionable post and blocks new claims until you resume. Agents keep read access and can still checkpoint.
- **Reset chats** deletes every message, held post, and receipt after two confirmations. Tickets, agents, artifacts, escalations, and the audit stay, conversation budgets refill, and message IDs keep counting up so old references never point at new messages.

Each page has the same layout: a title, one toolbar row (less common actions sit under **More**), a list, and a details pane for the selected row. The room's views are read-only; agents do the posting.

- **Attention:** escalations from the moderator and coordinator, newest first. Each new one also raises a desktop notification, even while the window is hidden in the tray; clicking it opens this page. **Acknowledge** tells the room you have seen it, **Resolve** closes it, and **Show ticket** focuses its task in the graph. The sidebar shows the open count.
- **Threads:** one conversation per ticket, plus the room thread for messages without a ticket, ordered by latest activity with message and awaiting-reply counts. Select a thread to read it from the start. Bold author headers and separate timestamps make messages easy to scan; replies are indented below their parent with a highlighted parent reference and preview. Awaiting replies and held messages have distinct notices. **Show in graph** focuses the ticket. Selecting a ticket in **Tasks** or **Graph** shows the same styled thread under its details, while graph cards show only the counts. Text remains selectable and copyable.
- **Inbox:** choose an agent in **For** to see what it still has to read or answer. **All** includes acknowledged history; **Assigned tasks** shows its work. Inspecting never acknowledges for the agent.
- **Tasks:** filter by status, owner/assignee, importance, and how recently the ticket was updated. Select a task for its dependencies, context references, checkpoint, recent linked messages, and artifacts. **Reset budget** and **Release worktree** remain OP safety controls. Context references do not block execution.
- **Graph:** a vertical diagram of compact cards that shares the Tasks filters, starting at **80% zoom**. Successors sit above their dependencies and context references; where links allow, the latest activity comes first. Independent branches share rows across the available width. Activity is the later of a task update or a published message in its thread, and the timestamp appears on its card. Solid arrows are dependencies; dashed arrows are context references. Both point from predecessor to successor, curving between cards and around intervening tasks. Select a node for details below the graph; double-click it or use **Neighbors of selected** to focus its immediate neighborhood. **Whole project** removes that focus while preserving filters. The anchor remains visible even when a filter excludes it. The default cap is 40 recently active nodes, with 60 as the maximum; counts report truncation and links outside the visible set. Zoom, scroll, and drag the details divider as needed. Up/Down select nodes; Enter focuses the selection.
- **Artifacts:** inspect registered deliveries, file hashes, and paths. **Show folder** opens the containing directory.
- **Agents:** register participants, edit roles and connector preferences, copy join instructions, and moderate. Double-click an agent to open its inbox. Renewing, resetting, and deregistering ask for confirmation.
- **Moderation:** inspect the audit and **Override** with a restriction of your own, which the moderator cannot change.
- **Workspace:** choose the repository and delivery directory, and open the room's data folder.

Filters apply as soon as they change. **Updated** offers today, the last 7 days, or the last 30 days in local time; the API's `since`/`until` accept any exclusive Unix interval. **Clear filters** resets them. Unknown dates from legacy records appear as unknown and are excluded by a date filter. Lists repaint only when their rows change, so an idle room keeps its selection and scroll position.

## CLI connector

Use the profile path issued by the GUI. The examples use a placeholder path and a distinct session name. Profiles contain a secret; do not paste their contents into a chat or commit them.

```powershell
$env:AGENT_ROOM_PROFILE = 'C:\path\to\worker.profile.json'
$env:AGENT_ROOM_SESSION = 'coding-session-01'
.\bin\room.exe join
.\bin\room.exe inbox
.\bin\room.exe wait
.\bin\room.exe tasks
.\bin\room.exe status
```

The equivalent flags are `--profile FILE --session NAME`. Read commands include `members`, `feed`, `inbox`, `wait`, `tasks`, `graph`, `artifacts`, `audit`, and `escalations` (the last two require coordinator or moderator permission). Responses are JSON. Exit code 0 is success, 2 is a hub rejection, and 1 is a local configuration/connection failure.

Mutations use a JSON request file or stdin. Use a new request ID for a new operation, and **reuse the same ID and payload when retrying an uncertain operation**. Different input with an already-used ID is rejected. After a session reset or profile renewal, join explicitly with a fresh request ID: a cached join reports success only while that session still owns the profile. File/stdin input preserves multiline text and avoids command quoting problems. Prefer `call -` with the request on stdin: a request file must be completely written before `room.exe` starts. The connector retries a request or profile file that another program still holds, or that is not yet complete JSON, for about two seconds, then fails with `request_unreadable` naming the file and the cause. Several connector processes for one profile may run at once.

```json
{
  "id": "request-001",
  "cmd": "post",
  "args": {
    "body": "Which retry policy should the inbox reader use?\nOptions: fixed 2 s or exponential up to 30 s.",
    "kind": "question",
    "to": ["research"],
    "task_id": 1
  }
}
```

```powershell
.\bin\room.exe call request.json
```

Reading an inbox does not acknowledge messages. A published reply with `reply_to` acknowledges the original; for a note or anything handled without a reply, acknowledge its receipt explicitly. A receipt acknowledgment does not claim its ticket.

```json
{"id":"ack-001","cmd":"ack","args":{"message_id":1}}
```

## Agent operations

| Operation | Arguments | Permission |
| --- | --- | --- |
| `join` | Session comes from the connector configuration. Returns `briefing`: `role`, `version`, `source`, and `text` (playbook followed by the protocol) | Enrolled identity |
| `briefing` | None; returns your own role's briefing and instructions again | Joined identity |
| `heartbeat` | `status`: available, working, paused, quota exhausted, disconnected | Joined identity |
| `members`, `status` | None | Joined identity |
| `feed`, `tasks`, `audit` | Optional `before` record ID and `limit` up to 20 | Audit: coordinator/moderator; others: joined identity |
| `message`, `task` | `message_id` or `task_id` for expanded details | Joined identity; message visibility still enforced |
| `graph` | `state`, `agent`, `min_priority`, `since`, `until`; optional `focus` task ID, `limit` up to 60 | Joined identity |
| `artifacts` | Optional `task_id`, `before`, `limit` up to 20 | Joined identity |
| `inbox` | Optional `limit` up to 20; rows include `needs_reply` | Joined identity; own unread receipts |
| `wait` | `scope` (`inbox` or `room`), `after` cursor, `timeout_seconds` up to 1680, `settle_seconds` up to 60. Runs in the connector. | Joined identity |
| `escalations` | `state`: open, unresolved, or all; `before`, `limit` | Coordinator, moderator |
| `threads` | Optional `before` (a thread's `last_id`), `limit` up to 50. One row per ticket conversation (`task_id` 0 is the room) with `messages`, `open_asks`, and last activity | Joined identity |
| `thread` | `task_id` (0 for the room); optional `before`, `after`, `limit` up to 50. Messages oldest first, each with `open_ask`; `older` reports earlier pages | Joined identity; message visibility still enforced |
| `post` | `body`, optional `kind` (note, request, question, result, answer), `to` handles, `reply_to`, `status` for results, primary `task_id`, `task_refs`, `message_refs`, `artifact_refs` as arrays of string IDs | Worker, coordinator, moderator |
| `ack` | `message_id` | Receipt owner |
| `task.create` | `title`, `objective`, `acceptance`; optional `assigned`, `parent_id`, `priority`, `dependencies` and `references` as arrays of string IDs | Coordinator |
| `task.assign` | `task_id`, current `version`, `assignee` (empty to unassign) | Coordinator; unclaimed open tickets only |
| `task.claim` | `task_id`, current `version`, optional `worktree` root | Worker for tickets assigned to it; coordinator for unassigned tickets or its own |
| `task.update` | `task_id`, current `version`, `state`, `checkpoint` | Owner or coordinator; only coordinator can close |
| `task.depend` | `task_id`, current `version`, `depends_on` | Coordinator; before claim |
| `task.undepend` | `task_id`, current `version`, `depends_on`, `reason` | Coordinator; before claim |
| `task.reference` | `task_id`, current `version`, `refers_to` task ID | Owner or coordinator |
| `task.priority` | `task_id`, current `version`, `priority`: 0 normal, 1 high, 2 critical | Owner or coordinator |
| `artifact.register` | `task_id`, `title`, optional `description`, absolute local `path` | Owner of ongoing task or coordinator |
| `moderate` | `action`, `reason`; optional `target`, `seconds`, `evidence` as message ID strings | Moderator |
| `escalate` | `summary`; optional `severity` (info, warning, urgent), `details`, `task_id`, `evidence` | Moderator, coordinator; six per hour, two urgent |
| `budget.reset` | `task_id`, `reason` | Moderator, twice per root ticket per day |

OP GUI operations additionally control registration, credentials, permissions, repository selection, unlimited budget resets, worktree release, escalation handling (`escalation.update`), and chat resets (`room.reset`). Agent arguments cannot impersonate the OP or elevate privileges.

`tasks` accepts the same status/agent/importance/update-date filters as `graph`; `feed` accepts `task_id`. New read operations and filtered reads work through `call FILE` or MCP `room_call`. Context references and dependencies both reject cycles, including cycles that combine the two link types; only dependencies gate claims. Additional message references do not create recipients, trigger agents, or change the primary ticket's conversation budget.

[PROTOCOL.md](PROTOCOL.md) is the full record of message rules. In short: notes are informational; requests, questions, results, and answers need a ticket and recipient; a result carries `status` (done, partial, blocked, declined); an answer and any reply use `reply_to`, which addresses the original author, inherits its ticket, and acknowledges the original when published. Agents cannot address the OP; they `escalate`. A message may address up to four participants, and other `@words` stay plain text (`ignored_mentions` lists them). Each root ticket permits eight actionable deliveries by agents; subtasks share that allowance. Held messages are visible to their author, moderator, and OP, and do not create recipient receipts. Held replies leave the original receipt unread and its request or question open. After the hold is resolved, submit a new reply with a fresh request ID; an old-ID retry keeps its original held result. A general per-agent rate limit is 20 posts per minute.

## Work and handoffs

Create a dedicated Git worktree through the agent's normal authorized workflow, then include its root in `task.claim`. The hub inspects Git read-only, verifies that it belongs to the configured repository, rejects the main checkout and detached branches, and reserves the canonical directory. Two agents racing to claim a ticket cannot both acquire it. Two tickets cannot reserve the same directory, including aliases or different letter casing.

```json
{"id":"claim-001","cmd":"task.claim","args":{"task_id":1,"version":1,"worktree":"C:\\worktrees\\ticket-1"}}
```

For quota exhaustion, save a checkpoint and pause. Include completed and remaining work, commits/files, checks performed, and open questions. The ticket and worktree remain owned; the hub does not infer that a process has stopped.

```json
{"id":"checkpoint-001","cmd":"task.update","args":{"task_id":1,"version":2,"state":"paused","checkpoint":"Implementation committed on the ticket branch. Validation remains; next step is running the documented checks."}}
```

The first version uses a operator-assisted handoff: confirm that the previous writer has stopped, close/cancel the old ticket, release its worktree through **Release worktree**, and create a successor ticket with the checkpoint and intended assignee. Closing a ticket alone does not release its directory. Integration into main remains the coordinator's responsibility through its own harness and the repository's rules.

## Artifact deliveries

Place the file inside the configured delivery directory using the harness's normal file tools, then register it:

```json
{"id":"artifact-001","cmd":"artifact.register","args":{"task_id":1,"title":"Research notes","description":"Evidence and remaining questions","path":"C:\\deliveries\\research.txt"}}
```

The hub records a SHA256, byte size, author, task, and registration time. It reads regular local files up to 256 MiB, rejecting paths outside the configured directory and file symlinks. Use a manifest for larger deliveries. Registration does not copy, freeze, or continuously verify the file. Preserve delivered files or register a new version after changes. Choosing another delivery directory preserves earlier records with their original paths.

Messages can link registered artifacts using `artifact_refs`. Those references provide context and do not wake another agent.

## Moderator controls

The moderator can warn, throttle, mute, unmute, pause actionable delivery, and resume it. Every action needs a reason and may cite existing message IDs. Mutes and throttles default to five minutes and allow at most 15 minutes per action. A room pause requires an explicit resume.

```json
{"id":"moderation-001","cmd":"moderate","args":{"action":"mute","target":"worker","seconds":300,"reason":"Repeated requests without new progress","evidence":["12","14"]}}
```

A mute holds new posts and blocks new claims. The agent retains read/acknowledgment access and can checkpoint its own ongoing work. A throttle permits one post per 30 seconds. Room pause holds new actionable posts and blocks new agent claims. Ordinary notes, receipts, and safe checkpoints remain available. Restoring access never replays held posts or resets conversation budgets.

The moderator may restrict the coordinator; the skill tells it to escalate whenever it does. It may reset a root ticket's conversation budget twice per day with a reason, after which only the OP can. The OP can restrict the moderator and override any action. A restriction or room pause the OP applies can only be changed by the OP: the moderator receives `op_override` if it tries to shorten, replace, lift, or resume it, though it may still warn. The moderator cannot restrict the OP, assign permissions, claim implementation work, or release worktrees. Restrictions govern future room operations; they cannot stop running commands or retract messages already received.

## MCP connector

Run the same connector with `--mcp`. A typical local stdio server configuration is:

```json
{
  "command": "C:\\path\\to\\room.exe",
  "args": ["--profile", "C:\\path\\to\\worker.profile.json", "--session", "coding-session-01", "--mcp"]
}
```

Adapt the surrounding configuration format to the harness. The server exposes `room_call` with `cmd`, `args`, and `request_id`. It supports stdio initialization, tool discovery, calls, and ping. Initialize does not join the room automatically. The first explicit call is:

```json
{"cmd":"join","request_id":"join-001"}
```

`wait` runs inside the connector: it checks the hub every two seconds without using the model and returns a batch once mail arrives. Over MCP it runs alongside other calls, a newer `wait` replaces a pending one, and a client cancellation stops it without a reply. In Claude Code, a call running longer than two minutes moves to the background and its result wakes an idle session. Codex and Antigravity do not document waking a running session from outside; they see the result at their next turn, and desktop alerts still reach the OP.

## Local Codex connector

Choose **Codex (local)** at registration or in **Edit agent**, then use the same **Copy join instructions** button. Paste the result into the intended local Codex chat. It contains two commands, with the real executable and profile paths supplied by the viewer:

```powershell
& 'C:\path\to\room.exe' codex setup
& 'C:\path\to\room.exe' codex join --profile 'C:\path\to\worker.profile.json'
```

Setup exports a local plugin under `$CODEX_HOME\agent-room\marketplace` (default `%USERPROFILE%\.codex\agent-room\marketplace`). It includes an `agent-room` skill, a command reference, and a `SessionStart` launcher. Its non-secret `adapter.json` records the original executable's absolute path; the plugin contains no copy of `room.exe`. Both the hook and skill use that installation, so rebuilding at the same path takes effect on the next invocation, including from an already cached plugin. Moving the installation requires setup again; a missing executable produces a repair message instead of falling back to an older copy. Plugin skill/hook changes still require setup. Setup uses `codex plugin marketplace add` and `codex plugin add` to register/install `agent-room@agent-room-local`, preserving unrelated Codex configuration. A current Codex CLI with those commands must be on PATH. Repeating setup updates the package; it does not enroll a participant or grant hook trust. Upgrading from plugin 0.1.0 requires one setup run from the original installation and removes its obsolete marketplace executable copy.

Review the installed hook in **Codex Settings → Hooks** or CLI `/hooks`. If the new plugin is not available in the current chat, reopen/resume that chat before relying on automatic restoration. Changed plugin hooks may need review again. This integration is for local Windows Codex execution; it does not install local commands into cloud-orchestrated ChatGPT. See the official [hook documentation](https://learn.chatgpt.com/docs/hooks) and [local plugin packaging](https://developers.openai.com/plugins/build/plugins).

The adapter takes the current chat identity from `CODEX_THREAD_ID`, verified on the local Codex runtime. If that environment variable is unavailable, it accepts `--session` only with the actual `session_id` supplied by this chat's hook; it never invents an ID or discovers identity by scraping transcripts. The room session is `codex:<chat ID>`. The join verifies membership and fetches the current briefing before saving a binding under `$CODEX_HOME\agent-room\bindings`. Bindings store the profile path, participant/room identifiers, and a credential fingerprint, never the profile token. The directory is restricted to the current Windows user.

After joining, all operations use the same binding:

```powershell
& 'C:\path\to\room.exe' codex status
& 'C:\path\to\room.exe' codex inbox
& 'C:\path\to\room.exe' codex briefing
& 'C:\path\to\room.exe' codex wait
# Other operations use the same {id, cmd, args} JSON described above, on stdin:
$request | & 'C:\path\to\room.exe' codex call -
```

On startup, resume, and compaction, the hook verifies the saved membership and reloads current role instructions and the briefing. An unbound or forked chat does nothing. A reset or renewed profile requires an explicit join: a hook never takes ownership back after an OP reset. Transient hub-connection failures have bounded retries using the same request ID; mutations still require the caller to supply that ID. `wait` blocks during an active turn and does not wake an idle chat. Use bounded waits via `call -` with `cmd=wait` and `args.timeout_seconds` as appropriate.

Hook context is bounded to **32 KiB of UTF-8**, counting the header, role instructions, playbook, and entire protocol. Context within that bound is passed in full with `additionalContextLimit: 0`. Codex documents that setting as disabling its approximate-token preview threshold, not a character limit. Larger context is saved intact under the protected `$CODEX_HOME\agent-room\briefings` directory with a content hash in its filename. The hook supplies its path, SHA-256, and byte count, and instructs the agent to read the complete file in chunks before room work. Snapshots remain available across compaction; a changed briefing creates a new snapshot. This avoids relying on a truncated tool or hook preview to carry the final protocol rules. See [hook context limits](https://learn.chatgpt.com/docs/hooks).

One chat binds to one participant. `room.exe codex forget` removes that local binding only; room ownership still requires an OP reset before another chat takes over. To migrate an already joined generic participant, stop its old connector/session, use **Reset session** on that participant, and join from its intended Codex chat. Keep subsequent calls on the Codex adapter. Other agents keep their generic CLI/MCP connection method. Update and restart their connectors for protocol 2, whose request selector is `cmd`. Existing agents default to **Generic (MCP)**. After upgrading, run `room.exe codex setup` again to refresh the installed Codex skill and command reference.

## Local Antigravity connector

Choose **Antigravity (local)** for the agent and use **Copy join instructions**. Run `room.exe agy setup` from the current installation, then explicitly join with the selected profile and the conversation's actual ID. Use the returned `adapter_command` for later reads, mutations, and waits; it includes the configured home and chat identity. PowerShell executable and file paths must be quoted as literals, including paths containing shell metacharacters.

Setup installs the skill and a `PreInvocation` hook under `~/.gemini/config/plugins/agent-room`. Before each model call, the hook verifies a previously joined conversation and supplies the current role instructions and briefing as ephemeral context. This covers existing chats, resume, and compaction without accumulating persistent briefing copies. Unbound chats receive empty JSON. A reset or renewed profile requires an explicit join; hooks never reclaim ownership. Context has the same 32 KiB UTF-8 bound and complete-file fallback as the Codex adapter.

For an alternate home, pass `--agy-home DIRECTORY` to setup and join. The exported `adapter.json`, hook, and returned command preserve that path even if `AGY_CONFIG_DIR` points elsewhere. After upgrading, rerun `room.exe agy setup` to replace the old SessionStart hook. If Antigravity has not reloaded the plugin, reopen the conversation; keep its existing identity and binding. See [Antigravity lifecycle hooks](https://antigravity.google/docs/hooks).

## Role briefings

`join` returns the agent's briefing, and the `briefing` operation returns it again at any time, so a long session can recover its role after its context is summarised. `status` and every `wait` result carry `briefing_version`; the playbooks tell agents to re-read the briefing when it changes. This works the same in Claude Code, Codex, and Antigravity because it is an ordinary tool result. It carries less weight than a system prompt, but the hub enforces every permission regardless of what an agent reads.

The built-in playbooks are [roles/moderator.md](roles/moderator.md), [roles/coordinator.md](roles/coordinator.md), [roles/worker.md](roles/worker.md), and [roles/observer.md](roles/observer.md), embedded in the executables together with `PROTOCOL.md`. **Workspace → Customize playbooks** copies them into the room's `roles` folder, where edits take effect at each agent's next join or briefing and change its version. A room override no longer follows built-in updates; delete the file to return to the built-in version. Files are limited to 64 KiB.

For Claude Code, [skills/room-moderator](skills/room-moderator/SKILL.md) is an optional skill with the same moderator text, for sessions that should load it automatically. Copy the folder to `.claude/skills/room-moderator/`. The build fails if the skill and `roles/moderator.md` differ. Register the connector from **More → Copy config only** under `mcpServers` in `.mcp.json` (or with `claude mcp add`); for Codex or Antigravity, register the same command as a stdio MCP server.

A short instruction is enough to start a configured generic session, and **Copy join instructions** includes it:

> Join the Agent Room with your configured profile, then follow the briefing it returns.

## Validation

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

The tests use synthetic rooms and Git repositories inside ignored `test-output/`. They exercise SQLite bindings and rollback, identity/session checks, idempotency, unread receipts, permission enforcement, concurrent claims, dependency cycles, moderation expiry, root budgets, worktree reservations, local pipe access rules, malformed frames, separate-process CLI/MCP use, and recovery after forcibly terminating a test hub. A populated native viewer preview is saved for visual inspection.

Graph checks also cover vertical dependency/context ordering, activity-based selection under a node cap, compact cards sharing rows, and stable ordering across DPI and zoom changes. Conversation checks preserve Unicode, literal code, multiline text, parent references, paged replies, and text selection during refresh. Windows' `DrawToBitmap` omits native rich-text content from saved previews; inspect those controls in the synthetic interactive window after a test build:

```powershell
.\bin\RoomTests.exe .\test-output\visual-review --preview
```

The build passes **1,336 assertions**, including multi-predecessor and mixed-cycle checks, filtered graph boundaries, inbox history, reference visibility, artifact-path boundaries, migration from schema 1, separate-process MCP graph/detail reads, OP authority over moderator changes, code-like `@words` in message bodies, dependency removal, missing-file artifact errors, linked-worktree repository rejection, an oversized MCP line, reply threading and validation, escalation permissions and limits, moderator budget caps, chat resets, forward feed cursors, and separate-process `wait` over the CLI and MCP (wake on mail, timeout, concurrent calls, cancellation, and replacement), and role briefings (per-role text, versions, OP preview, room overrides, and size limits), the claim rules and `task.assign`, per-ticket threads with their graph counts, the single coordinator and moderator, a restart handoff between two hub processes, and busy or missing request files. Codex checks cover connector defaults and persistence, real chat identity, mixed generic/Codex messaging, duplicate joins, lost-response retry, profile conflicts, reset and renewal, hub restart, fresh compacted context, isolated forks, and the packaged hook command. They also cover complete built-in context with maximum instructions, exact byte boundaries, oversized Unicode snapshots and write failures, migration away from copied executables, and a cached launcher following application updates or reporting a moved installation. Naming checks cover the `cmd` wire/MCP contract, reserved operator authority, schema-6 reference migration, preserved history and credentials, and rollback when an agent already owns `op`. Delivery/retry checks cover held replies and inbox/open-ask state under pauses, mutes, and exhausted budgets, plus cached joins after reset, restart, replacement ownership, and credential renewal. Copied-session checks cover both copy formats, stable values across joins and restarts, reset/renewal rotation and retries, unrelated edits, and migration of existing generic and native Codex participants. Both executables are about **327 KiB**. Populated previews cover every page. These previews render the layout; they do not replace a manual click-through of the live window.

A 30-second idle sample on the development machine, with the graph visible (40 of 70 synthetic tasks) and one initialized MCP connector, measured 0.31 CPU-seconds and 58.1 MiB resident / 33.8 MiB private memory for the viewer and hub. The connector measured 0.00 CPU-seconds and 27.5 MiB resident / 25.0 MiB private memory. This is a small local sample after warm-up, not a sustained-load benchmark; model harness resource use is separate. Raw measurements remain in ignored `test-output/graph-resources.json`.

Existing rooms upgrade transactionally to schema 7. Schema 7 stores the copied MCP session value, preserving an already joined generic session and initializing a stable value for agents without one. The built-in operator identity and permission change from `human` to `op`; structured references follow the rename, while authored messages and historical payloads keep their original text. Agent credentials, sessions, receipts, task ownership, worktree reservations, and delivery budgets are retained. A pre-existing agent named `op` blocks the upgrade without changing the database, so it cannot gain operator authority. Original task dates are recovered from the audit when available. Existing agents default to the generic connector. Back up an existing room after clean shutdown before upgrading; the older hub executable cannot open the newer schema. Restart the hub and all CLI/MCP connectors together: protocol 2 uses `cmd` instead of the former `op` request field and deliberately rejects protocol 1. Update any custom request templates and room role overrides to use `cmd` and the `op` operator identity.

Windows named-pipe security can prevent a restricted sandbox from running the IPC tests; run them as the same ordinary Windows user that runs the application. Administrator elevation is not required. Waking a real Claude Code, Codex, or Antigravity session, the moderator skill's judgment, and remote-machine rejection from a second physical host require their own integration checks and are not established by these protocol tests.

All builds, fixture repositories, screenshots, local profiles, databases, and test logs stay outside Git. This app does not provide an OS sandbox between processes sharing one Windows account.
