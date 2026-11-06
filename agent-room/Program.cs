using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgentRoom
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool gui = Path.GetFileNameWithoutExtension(Application.ExecutablePath).Equals("AgentRoom", StringComparison.OrdinalIgnoreCase);
            try
            {
                var options = new List<string>(args);
                string data = Option(options, "--data", LocalFiles.DefaultDirectory), predecessor = Option(options, "--after", "");
                if (gui || options.Contains("--serve"))
                {
                    // A restarted hub waits for the previous one to release the room's mutex and pipe.
                    WaitForExit(predecessor);
                    string fullPath = Path.GetFullPath(data);
                    if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(fullPath)).DriveType == DriveType.Network)
                        throw new RoomError("local_only", "Room data must be on a local drive.");
                    Directory.CreateDirectory(fullPath);
                    data = LocalFiles.CanonicalDirectory(fullPath);
                    bool created;
                    using (var mutex = new Mutex(true, "Local\\" + LocalFiles.PipeName(data), out created))
                    {
                        if (!created) { if (gui) MessageBox.Show("Agent Room is already running. Open its tray icon.", "Agent Room"); return 1; }
                        try
                        {
                            using (var service = new RoomService(data))
                            using (var server = new RoomServer(service))
                            {
                                server.Start();
                                if (gui)
                                {
                                    Application.EnableVisualStyles();
                                    Application.SetCompatibleTextRenderingDefault(false);
                                    using (var viewer = new Viewer(service, server)) Application.Run(viewer);
                                }
                                else
                                {
                                    Console.WriteLine(Json.Write(Json.Obj("ready", true, "pipe", service.PipeName)));
                                    while (Console.ReadLine() != null) { }
                                }
                            }
                        }
                        finally { mutex.ReleaseMutex(); }
                    }
                    return 0;
                }
                Console.InputEncoding = new UTF8Encoding(false);
                Console.OutputEncoding = new UTF8Encoding(false);
                if (options.Count > 0 && options[0] == "codex") { options.RemoveAt(0); return CodexAdapter.Run(options); }
                if (options.Count > 0 && options[0] == "agy") { options.RemoveAt(0); return AgyAdapter.Run(options); }
                string profilePath = Option(options, "--profile", Environment.GetEnvironmentVariable("AGENT_ROOM_PROFILE") ?? "");
                string session = Option(options, "--session", Environment.GetEnvironmentVariable("AGENT_ROOM_SESSION") ?? "");
                if (options.Count == 0 || options.Contains("--help")) { Help(); return 0; }
                if (profilePath == "" || session == "") throw new RoomError("configuration", "Provide --profile FILE and --session NAME, or AGENT_ROOM_PROFILE and AGENT_ROOM_SESSION.");
                var profile = ReadJsonFile(profilePath, "profile");
                if (options.Count == 1 && options[0] == "--mcp") { Mcp.Run(profile, session); return 0; }
                Dictionary<string, object> request;
                if (options[0] == "call" && options.Count == 2)
                    request = options[1] == "-" ? Json.Read(ReadInput()) : ReadJsonFile(options[1], "request");
                else if (options.Count == 1 && new[] { "join", "status", "members", "feed", "inbox", "tasks", "audit", "graph", "artifacts", "escalations", "briefing", "threads", "wait" }.Contains(options[0]))
                    request = Json.Obj("cmd", options[0], "args", Json.Obj(), "id", Guid.NewGuid().ToString("N"));
                else throw new RoomError("usage", "Use join, briefing, status, members, feed, inbox, wait, threads, tasks, graph, artifacts, audit, escalations, call FILE, or --mcp.");
                string requestId = Json.Text(request, "id", "", 100), cmd = Json.Required(request, "cmd", 40);
                var response = cmd == "wait" ? Waiter.Run(profile, session, Json.Map(request, "args"), CancellationToken.None)
                    : Wire.Call(profile, session, cmd, Json.Map(request, "args"), requestId);
                response["request_id"] = requestId;
                Console.WriteLine(Json.Write(response));
                return (bool)response["ok"] ? 0 : 2;
            }
            catch (Exception e)
            {
                var known = e as RoomError;
                // Name the actual failure; the exception message is local and carries no credentials.
                string message = known == null ? "Agent Room hit an unexpected local error (" + e.GetType().Name + ": " + e.Message + ")." : known.Message;
                if (gui) MessageBox.Show(message, "Agent Room", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else Console.Error.WriteLine(Json.Write(Json.Error(known == null ? "local_error" : known.Code, message)));
                return 1;
            }
        }
        // A harness may still be writing a request file when it starts the connector, so a busy or half-written file
        // is retried for about two seconds before the read is reported with its real cause.
        internal static Dictionary<string, object> ReadJsonFile(string path, string what)
        {
            string failure = "";
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (attempt != 0) Thread.Sleep(100);
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return Json.Read(reader.ReadToEnd());
                }
                catch (FileNotFoundException) { throw new RoomError(what + "_missing", "The " + what + " file does not exist: " + path); }
                catch (DirectoryNotFoundException) { throw new RoomError(what + "_missing", "The " + what + " file's folder does not exist: " + path); }
                catch (UnauthorizedAccessException) { throw new RoomError(what + "_unreadable", "Windows denied access to the " + what + " file: " + path); }
                catch (IOException e) { failure = "another program is still using it (" + e.Message.Trim() + ")"; }
                catch (RoomError e) { if (e.Code != "invalid_request" || e.Message.Contains("size limit")) throw; failure = "it is not a complete JSON object yet"; }
            }
            throw new RoomError(what + "_unreadable", "Could not read the " + what + " file " + path + ": " + failure +
                (what == "request" ? ". Finish writing it before starting room.exe, or pass the request on stdin with `call -`." : "."));
        }
        private static void WaitForExit(string processId)
        {
            int id;
            if (!Int32.TryParse(processId, out id)) return;
            try { using (var previous = System.Diagnostics.Process.GetProcessById(id)) previous.WaitForExit(30000); }
            catch (ArgumentException) { } // Already exited.
        }
        internal static string Option(List<string> args, string name, string fallback)
        {
            int at = args.IndexOf(name);
            if (at < 0) return fallback;
            if (at + 1 >= args.Count) throw new RoomError("usage", name + " requires a value.");
            string value = args[at + 1];
            args.RemoveRange(at, 2);
            return value;
        }
        internal static string ReadInput()
        {
            var text = new StringBuilder();
            char[] buffer = new char[4096];
            int count;
            while ((count = Console.In.Read(buffer, 0, buffer.Length)) != 0)
            {
                text.Append(buffer, 0, count);
                if (text.Length > 65536) throw new RoomError("invalid_request", "Request exceeds 64 KiB.");
            }
            return text.ToString();
        }
        private static void Help()
        {
            Console.WriteLine("Codex integration: room codex setup | join --profile FILE | status | inbox | wait | call FILE|- | forget\nUses CODEX_THREAD_ID (or --session with a verified hook session_id) and a saved local binding.\n");
            Console.WriteLine("Antigravity integration: room agy setup | join --profile FILE --session ID | status | inbox | wait | call FILE|- | forget\nUses --session <ConversationId> (or AGY_CONVERSATION_ID) and a saved local binding.\n");
            Console.WriteLine("Agent Room local connector\n\nroom --profile FILE --session NAME join\nroom --profile FILE --session NAME inbox\nroom --profile FILE --session NAME wait\nroom --profile FILE --session NAME call request.json\nroom --profile FILE --session NAME call -\nroom --profile FILE --session NAME --mcp\n\nRequest: {\"id\":\"unique-operation-id\",\"cmd\":\"post\",\"args\":{\"body\":\"Plan ready for review\",\"kind\":\"note\"}}\nReuse the same id and payload after a connection failure. See README.md for operations.\nProfiles are enrolled in the GUI. wait blocks in this connector until mail arrives; it does not launch models. See PROTOCOL.md for message rules.");
        }
    }

    internal static class Mcp
    {
        private static readonly string[] Operations = { "join", "heartbeat", "status", "members", "feed", "inbox", "wait", "tasks", "audit", "message", "task", "graph", "artifacts", "escalations", "briefing", "threads", "thread", "post", "ack", "task.create", "task.assign", "task.claim", "task.update", "task.depend", "task.undepend", "task.reference", "task.priority", "artifact.register", "moderate", "escalate", "budget.reset" };
        private static readonly object output = new object();
        private static readonly Dictionary<string, CancellationTokenSource> waits = new Dictionary<string, CancellationTokenSource>();
        private static readonly HashSet<string> withdrawn = new HashSet<string>();
        private static string activeWait;
        internal static void Run(Dictionary<string, object> profile, string session)
        {
            bool initialized = false, oversized;
            string line;
            while ((line = ReadLine(out oversized)) != null)
            {
                object id = null;
                // An oversized line is discarded whole; its ID is unknown, so the error reply uses a null ID.
                if (oversized) { Reply(null, null, Json.Obj("code", -32600, "message", "MCP message exceeds 64 KiB.")); continue; }
                try
                {
                    var message = Json.Read(line);
                    if (!message.ContainsKey("id"))
                    {
                        // Notifications have no response; a cancellation stops a pending wait without replying to it.
                        if (Json.Text(message, "method", "", 80) == "notifications/cancelled") Withdraw(Json.Map(message, "params"));
                        continue;
                    }
                    id = message["id"];
                    string method = Json.Required(message, "method", 80);
                    object result;
                    if (method == "initialize")
                    {
                        string requested = Json.Text(Json.Map(message, "params"), "protocolVersion");
                        string version = new[] { "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25" }.Contains(requested) ? requested : "2025-11-25";
                        result = Json.Obj("protocolVersion", version, "capabilities", Json.Obj("tools", Json.Obj("listChanged", false)),
                            "serverInfo", Json.Obj("name", "local-agent-room", "version", "0.3.0"),
                            "instructions", "Join explicitly with room_call cmd=join. The join result contains your role briefing: your playbook followed by the room protocol. Follow it for the rest of the session. If a wait result's room.briefing_version changes, or you lose track of your role, call cmd=briefing to read it again. Use cmd=wait to block until mail arrives instead of polling; it costs no tokens while waiting. Follow PROTOCOL.md: reply with reply_to (result with status for a request, answer for a question) and never reply to notes, receipts, or status. Replying acknowledges the original. Reach the OP only through escalate. Respect moderator holds. The hub cannot stop ongoing edits.");
                        initialized = true;
                    }
                    else if (method == "ping") result = Json.Obj();
                    else if (!initialized) throw new RoomError("not_initialized", "Initialize the MCP connection first.");
                    else if (method == "tools/list")
                    {
                        result = Json.Obj("tools", new[] { Json.Obj("name", "room_call", "description",
                            "Local room operations. join attaches an enrolled identity to this session and returns your role briefing; briefing re-reads it. wait: blocks in the connector until mail arrives (scope inbox|room, after cursor for room, timeout_seconds max 1680, settle_seconds max 60); returns messages, cursor, timed_out, room. Read members/feed(after cursor reads forward)/inbox/tasks/status/audit/artifacts/escalations(state open|unresolved|all). threads: one row per ticket conversation (task_id 0 is the room) with messages, open_asks, last activity. thread: task_id (0 for room), before/after, limit max 50; messages oldest first with open_ask. message: message_id. task: task_id. graph and tasks filters: state(all/unfinished/or a task state),agent,min_priority(0 normal/1 high/2 critical),since/until(exclusive Unix update dates); graph also focus(task ID),limit(max60). Mutations need stable request_id. post: body,kind(note/request/question/result/answer),to(handles; @handle in body also addresses an active participant, other @words stay text),reply_to(message ID; required for answer; a result may reply to a request),status(result only: done/partial/blocked/declined),task_id(inherited from reply_to),task_refs/message_refs/artifact_refs(arrays of string IDs). ack: message_id. task.create: title,objective,acceptance,assigned,parent_id,priority,dependencies/references(string IDs). task.assign (coordinator): task_id,version,assignee (empty to unassign; open unclaimed tickets only). task.claim: task_id,version,optional worktree; workers claim only tickets assigned to them, the coordinator may claim unassigned ones. task.update: task_id,version,state,checkpoint. task.depend: task_id,version,depends_on. task.undepend: task_id,version,depends_on,reason (open tickets only). task.reference: task_id,version,refers_to. task.priority: task_id,version,priority. artifact.register: task_id,title,description,path(absolute file inside configured delivery directory,max256MiB). moderate: action(warn/throttle/mute/unmute/pause/resume),target,reason,seconds(1-900),evidence(string message IDs). escalate (moderator/coordinator): severity(info/warning/urgent),summary,details,task_id,evidence(string message IDs); shows a desktop alert to the OP. budget.reset (moderator, twice per root per day): task_id,reason. heartbeat: status. References provide context; only dependencies block claims. Permissions and moderation are enforced by the hub.",
                            "inputSchema", Json.Obj("type", "object", "properties", Json.Obj(
                                "cmd", Json.Obj("type", "string", "enum", Operations),
                                "args", Json.Obj("type", "object", "additionalProperties", true),
                                "request_id", Json.Obj("type", "string", "description", "Required for mutations. Reuse on a retry with identical input.")),
                                "required", new[] { "cmd" }, "additionalProperties", false)) });
                    }
                    else if (method == "tools/call")
                    {
                        var parameters = Json.Map(message, "params");
                        if (Json.Text(parameters, "name") != "room_call") throw new RoomError("unknown_tool", "Unknown tool.");
                        var arguments = Json.Map(parameters, "arguments");
                        string cmd = Json.Required(arguments, "cmd", 40);
                        if (!Operations.Contains(cmd)) throw new RoomError("unknown_operation", "Operation is not exposed to agents.");
                        if (cmd == "wait") { StartWait(id, profile, session, Json.Map(arguments, "args")); continue; }
                        Dictionary<string, object> response;
                        try { response = Wire.Call(profile, session, cmd, Json.Map(arguments, "args"), Json.Text(arguments, "request_id", "", 100)); }
                        catch (RoomError e) { response = Json.Error(e.Code, e.Message); }
                        result = ToolResult(response);
                    }
                    else { Reply(id, null, Json.Obj("code", -32601, "message", "Method not found.")); continue; }
                    Reply(id, result, null);
                }
                catch (RoomError e) { Reply(id, null, Json.Obj("code", e.Code == "invalid_request" ? -32700 : -32602, "message", e.Message)); }
                catch (Exception) { Reply(id, null, Json.Obj("code", -32603, "message", "Local MCP operation failed.")); }
            }
        }
        private static string ReadLine(out bool oversized)
        {
            var line = new StringBuilder();
            int value;
            oversized = false;
            while ((value = Console.In.Read()) != -1)
            {
                if (value == '\n') return line.ToString();
                if (line.Length >= 65536) { oversized = true; line.Clear(); }
                if (value != '\r' && !oversized) line.Append((char)value);
            }
            return line.Length == 0 && !oversized ? null : line.ToString();
        }
        // A wait runs off the read loop so the agent can keep calling other operations; a newer wait replaces it.
        private static void StartWait(object id, Dictionary<string, object> profile, string session, Dictionary<string, object> args)
        {
            string key = Json.Write(id);
            var source = new CancellationTokenSource();
            CancellationTokenSource previous = null;
            lock (waits)
            {
                if (activeWait != null) waits.TryGetValue(activeWait, out previous);
                waits[key] = source; activeWait = key;
            }
            if (previous != null) previous.Cancel();
            Task.Run(() =>
            {
                Dictionary<string, object> response;
                try { response = Waiter.Run(profile, session, args, source.Token); }
                catch (RoomError e) { response = Json.Error(e.Code, e.Message); }
                catch (Exception) { response = Json.Error("local_error", "The wait could not be completed."); }
                bool cancelledByClient;
                // The source is not disposed: a newer wait may still cancel it, and a timer-free source holds no handle.
                lock (waits) { waits.Remove(key); if (activeWait == key) activeWait = null; cancelledByClient = withdrawn.Remove(key); }
                if (!cancelledByClient) Reply(id, ToolResult(response), null);
            });
        }
        private static void Withdraw(Dictionary<string, object> parameters)
        {
            object requestId;
            if (!parameters.TryGetValue("requestId", out requestId)) return;
            string key = Json.Write(requestId);
            CancellationTokenSource source;
            lock (waits) { if (!waits.TryGetValue(key, out source)) return; withdrawn.Add(key); }
            source.Cancel();
        }
        private static object ToolResult(Dictionary<string, object> response)
        {
            return Json.Obj("content", new[] { Json.Obj("type", "text", "text", Json.Write(response)) }, "isError", !(bool)response["ok"]);
        }
        private static void Reply(object id, object result, object error)
        {
            string text = Json.Write(error == null ? Json.Obj("jsonrpc", "2.0", "id", id, "result", result) :
                Json.Obj("jsonrpc", "2.0", "id", id, "error", error));
            lock (output) Console.WriteLine(text);
        }
    }
}
