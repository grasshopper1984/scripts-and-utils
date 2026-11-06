using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgentRoom
{
    internal static partial class Tests
    {
        private static void ExpectRoomError(Action action, string code)
        {
            try { action(); Check(false, "Expected " + code); }
            catch (RoomError e) { Check(e.Code == code, "Expected " + code + ", received " + e.Code); }
        }
        private static void ConnectorTests()
        {
            using (var f = new Fixture("connectors"))
            {
                Check(Rows(f.Room.Admin("members")).All(a => (string)a["connector"] == "generic"), "Existing enrollment calls default to generic");
                ExpectRoomError(() => f.Room.Admin("agent.enroll", Json.Obj("agent", "bad", "permission", "worker", "connector", "unknown")), "invalid_argument");
                var profile = Result(f.Room.Admin("agent.enroll", Json.Obj("agent", "native", "permission", "worker", "connector", "codex")));
                Check((string)profile["connector"] == "codex", "Enrollment returns the selected handoff type");
                f.Room.Admin("agent.update", Json.Obj("agent", "native", "permission", "worker", "instructions", "Updated"));
                Check((string)Rows(f.Room.Admin("members")).Single(a => (string)a["id"] == "native")["connector"] == "codex", "Older role-update clients preserve connector preference");
                Check((string)Result(f.Room.Admin("agent.rotate", Json.Obj("agent", "native")))["connector"] == "codex", "Renewed profiles retain their connector preference");
                ExpectRoomError(() => f.Room.Admin("agent.update", Json.Obj("agent", "alice", "permission", "worker", "connector", "invalid")), "invalid_argument");
                f.Room.Admin("agent.update", Json.Obj("agent", "alice", "permission", "worker", "connector", "codex"));
                Result(f.Call("alice", "status"));
                Error(f.Call("alice", "agent.update", Json.Obj("agent", "bob", "permission", "worker", "connector", "codex")), "forbidden");
                string generic = Viewer.ConnectionHandoff(@"C:\synthetic\worker.profile.json", "worker", "generic", "worker-stable-session");
                Check(generic.Contains("\"--mcp\"") && generic.Contains("\"--session\"") && !generic.Contains("codex setup"), "Generic handoff retains MCP transport and session arguments");
                string native = Viewer.ConnectionHandoff(@"C:\synthetic O'Brien\worker.profile.json", "worker", "codex", "worker-stable-session");
                Check(native.Contains("O''Brien") && native.Contains("codex setup") && native.Contains("codex join --profile") && !native.Contains("\"--mcp\"") && !native.Contains("--session worker-"), "Codex handoff quotes paths and does not copy a fabricated session ID");
                using (var dialog = new FieldsDialog("Register agent"))
                {
                    dialog.Add("agent", "Mention handle", "worker"); Viewer.AddAgentFields(dialog, "worker", "generic", "Synthetic role instructions");
                    Check((string)dialog.Values()["connector"] == "Generic (MCP)", "Registration UI selects Generic by default");
                    dialog.Show(); Application.DoEvents();
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "register-connector.png"), ImageFormat.Png); }
                    dialog.Close();
                }
                using (var dialog = new FieldsDialog("Edit agent"))
                {
                    Viewer.AddAgentFields(dialog, "coordinator", "codex", "Synthetic coordinator instructions");
                    Check((string)dialog.Values()["connector"] == "Codex (local)" && (string)dialog.Values()["permission"] == "coordinator", "Editing restores independent connector and role choices");
                    dialog.Show(); Application.DoEvents();
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "edit-connector.png"), ImageFormat.Png); }
                    dialog.Close();
                }
            }
        }
        private static Dictionary<string, object> CodexHookInput(string session, string source = "resume")
        { return Json.Obj("hook_event_name", "SessionStart", "session_id", session, "source", source, "cwd", output); }
        private static void CodexTests()
        {
            CodexContextTests();
            CodexLostReplyTest();
            Check(CodexAdapter.Session("", "chat-a") == "chat-a", "Codex session comes from its runtime environment");
            ExpectRoomError(() => CodexAdapter.Session("chat-b", "chat-a"), "codex_session_mismatch");
            ExpectRoomError(() => CodexAdapter.Session("", null), "codex_session_required");
            ExpectRoomError(() => CodexAdapter.Session("../other", null), "codex_session_required");
            string codexHome = Folder("codex-home O'Brien 中文"), directory = Folder("codex-room"), profileFile = Path.Combine(directory, "native.profile.json");
            var adapter = new CodexAdapter(codexHome);
            Dictionary<string, object> profile, generic;
            using (var room = new RoomService(directory))
            using (var server = new RoomServer(room))
            {
                server.Start();
                profile = Result(room.Admin("agent.enroll", Json.Obj("agent", "native", "permission", "worker", "connector", "codex", "instructions", "Native custom role")));
                generic = Result(room.Admin("agent.enroll", Json.Obj("agent", "generic", "permission", "worker")));
                File.WriteAllText(profileFile, Json.Write(profile));
                Result(Wire.Call(generic, "generic-session", "join", Json.Obj(), "generic-join"));
                Check(adapter.Hook(CodexHookInput("unbound", "startup")) == null && !Directory.Exists(codexHome), "Unbound chats do not create files or join rooms");
                var joined = Result(adapter.Join("chat-a", profileFile));
                Check((string)joined["session"] == "codex:chat-a" && (string)joined["agent"] == "native", "Adapter verifies and binds the actual chat identity");
                string binding = Directory.GetFiles(Path.Combine(codexHome, "agent-room", "bindings")).Single();
                Check(!File.ReadAllText(binding).Contains((string)profile["token"]), "Bindings contain a path and credential fingerprint, never a token");
                long joins = Rows(room.Admin("audit")).Count(a => (string)a["actor"] == "native" && (string)a["action"] == "join");
                Result(adapter.Join("chat-a", profileFile));
                Check(Rows(room.Admin("audit")).Count(a => (string)a["actor"] == "native" && (string)a["action"] == "join") == joins, "Repeated explicit join verifies membership without duplicate enrollment");
                Check(adapter.Hook(CodexHookInput("forked-chat", "fork")) == null, "A fork never inherits the parent's binding");
                ExpectRoomError(() => adapter.Join("chat-b", profileFile), "session_conflict");
                ExpectRoomError(() => adapter.Call("unbound", Json.Obj("cmd", "status")), "codex_not_bound");
                string otherProfile = Path.Combine(directory, "generic.profile.json"); File.WriteAllText(otherProfile, Json.Write(generic));
                ExpectRoomError(() => adapter.Join("chat-a", otherProfile), "codex_binding_conflict");
                var request = Json.Obj("cmd", "post", "id", "native-note", "args", Json.Obj("body", "Hello @generic\n中文", "kind", "note"));
                long message = Convert.ToInt64(Result(adapter.Call("chat-a", request))["message_id"]);
                Check(Convert.ToInt64(Result(adapter.Call("chat-a", request))["message_id"]) == message, "Adapter preserves mutation IDs across retries");
                Check(Rows(Wire.Call(generic, "generic-session", "inbox", Json.Obj(), "")).Count == 1, "Generic participant receives a Codex participant's message");
                Error(adapter.Call("chat-a", Json.Obj("cmd", "post", "args", Json.Obj("body", "No ID"))), "request_id_required");
                Result(Wire.Call(generic, "generic-session", "post", Json.Obj("body", "Reply @native", "kind", "note"), "generic-note"));
                Check(Rows(adapter.Call("chat-a", Json.Obj("cmd", "inbox"))).Count == 1, "Codex participant reads a generic participant's message");
                Check(!(bool)Result(adapter.Call("chat-a", Json.Obj("cmd", "wait", "args", Json.Obj("timeout_seconds", 1))))["timed_out"], "Bound CLI wait receives room messages");
                room.Admin("agent.update", Json.Obj("agent", "native", "permission", "worker", "instructions", "Updated role instructions"));
                Directory.CreateDirectory(room.RolesDirectory); File.WriteAllText(Path.Combine(room.RolesDirectory, "worker.md"), "Updated worker playbook");
                string context = Json.Required(Json.Map(adapter.Hook(CodexHookInput("chat-a", "compact")), "hookSpecificOutput"), "additionalContext", 200000);
                Check(context.Contains("Updated role instructions") && context.Contains("Updated worker playbook"), "Compaction reloads current instructions and playbook, not a cached join result");
                Result(Json.Read(RunCodexChild(codexHome, "chat-a", "status", null)));
                string package = adapter.ExportPackage(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"));
                string plugin = Path.Combine(package, "plugins", "agent-room");
                var hooks = Json.Map(Program.ReadJsonFile(Path.Combine(plugin, "hooks", "hooks.json"), "hooks"), "hooks");
                var startHook = (Dictionary<string, object>)((object[])hooks["SessionStart"])[0];
                Check(Json.Number((Dictionary<string, object>)((object[])startHook["hooks"])[0], "additionalContextLimit", -1) == 0,
                    "Codex does not preview context already bounded by the adapter");
                Check(!File.Exists(Path.Combine(plugin, "room.exe")) &&
                    Json.Required(Program.ReadJsonFile(Path.Combine(plugin, "adapter.json"), "adapter"), "executable") == Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"),
                    "Plugin points to the installation without copying the adapter executable");
                string hookOutput = RunPackagedHook(plugin, codexHome, Json.Write(CodexHookInput("chat-a")));
                Check(Json.Write(Json.Read(hookOutput)).Contains("Updated worker playbook"), "Packaged PowerShell hook preserves stdin and restores the bound briefing");
                Check(RunPackagedHook(plugin, codexHome, Json.Write(CodexHookInput("not-bound"))).Trim() == "", "Packaged hook is quiet for unrelated chats");
                string longPlaybook = new string('\u754c', 14000) + "\nCUSTOM PLAYBOOK END";
                File.WriteAllText(Path.Combine(room.RolesDirectory, "worker.md"), longPlaybook, new UTF8Encoding(false));
                string largeHook = Json.Required(Json.Map(Json.Read(RunPackagedHook(plugin, codexHome, Json.Write(CodexHookInput("chat-a")))), "hookSpecificOutput"), "additionalContext");
                string saved = Directory.GetFiles(Path.Combine(codexHome, "agent-room", "briefings")).Single();
                Check(largeHook.Contains(saved) && File.ReadAllText(saved, Encoding.UTF8).Contains(longPlaybook) &&
                    File.ReadAllText(saved, Encoding.UTF8).EndsWith(RoomService.BuiltInDocument("protocol.md").TrimEnd()),
                    "Packaged hook hands off the full Unicode playbook and final protocol through a context file");
                string blockedHome = Folder("codex-snapshot-failure");
                Result(new CodexAdapter(blockedHome).Join("chat-a", profileFile));
                File.WriteAllText(Path.Combine(blockedHome, "agent-room", "briefings"), "Block snapshot directory creation");
                var blockedHook = Json.Read(RunCodexChild(blockedHome, "chat-a", "hook", Json.Write(CodexHookInput("chat-a"))));
                Check(Json.Required(blockedHook, "systemMessage").Contains("context was not restored") &&
                    Json.Required(Json.Map(blockedHook, "hookSpecificOutput"), "additionalContext").Contains("Do not perform room work"),
                    "A failed snapshot write reports unavailable context instead of silently substituting a partial briefing");
                File.WriteAllText(Path.Combine(room.RolesDirectory, "worker.md"), "Updated worker playbook");
                File.WriteAllText(Path.Combine(plugin, "room.exe"), "stale package copy");
                ExpectRoomError(() => adapter.ExportPackage(Path.Combine(plugin, "room.exe")), "codex_adapter_copy");
                adapter.ExportPackage(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"));
                Check(!File.Exists(Path.Combine(plugin, "room.exe")) && File.Exists(Path.Combine(plugin, "skills", "agent-room", "references", "room-cli.md")),
                    "Setup removes the obsolete package copy and retains the command reference");
                CodexLauncherUpdateTest();
                room.Admin("agent.reset", Json.Obj("agent", "native"));
                ExpectRoomError(() => adapter.Hook(CodexHookInput("chat-a")), "session_required");
                Check((string)Rows(room.Admin("members")).Single(a => (string)a["id"] == "native")["status"] == "not joined", "Resume does not reclaim an OP-reset profile");
                Result(adapter.Join("chat-a", profileFile));
                profile = Result(room.Admin("agent.rotate", Json.Obj("agent", "native"))); File.WriteAllText(profileFile, Json.Write(profile));
                ExpectRoomError(() => adapter.Hook(CodexHookInput("chat-a")), "codex_profile_changed");
                Result(adapter.Join("chat-a", profileFile));
            }
            using (var restarted = new RoomService(directory))
            using (var server = new RoomServer(restarted))
            {
                server.Start();
                Check(adapter.Hook(CodexHookInput("chat-a")) != null, "Persisted membership and binding restore after hub restart");
                Check((string)Rows(restarted.Admin("members")).Single(a => (string)a["id"] == "native")["connector"] == "codex", "Connector preference survives reopening the database");
                Result(Json.Read(RunCodexChild(codexHome, "chat-a", "join --profile " + Quote(profileFile), null)));
                restarted.Admin("agent.revoke", Json.Obj("agent", "native"));
                Error(adapter.Call("chat-a", Json.Obj("cmd", "status")), "unauthorized");
            }
        }
        private static void CodexContextTests()
        {
            string codexHome = Folder("codex-context"), snapshots = Path.Combine(codexHome, "agent-room", "briefings");
            var adapter = new CodexAdapter(codexHome);
            var status = Json.Obj("self", "worker", "instructions", new string('I', 8192));
            string protocol = RoomService.BuiltInDocument("protocol.md").Replace("\r\n", "\n").TrimEnd();
            foreach (string role in RoomService.BriefedRoles)
            {
                string playbook = RoomService.BuiltInDocument(role + ".md").Replace("\r\n", "\n").TrimEnd();
                string context = adapter.HookContext("context-chat", status, Json.Obj("text", playbook + "\n\n---\n\n" + protocol));
                Check(context.Contains((string)status["instructions"]) && context.Contains(playbook) && context.EndsWith(protocol) &&
                    Encoding.UTF8.GetByteCount(context) <= CodexAdapter.HookContextBytes, "Full " + role + " briefing and maximum ASCII instructions fit inline through the final protocol rule");
            }
            Check(!Directory.Exists(snapshots), "Inline briefings need no snapshot file");
            status["instructions"] = "Current role instructions";
            int headerBytes = Encoding.UTF8.GetByteCount(adapter.HookContext("context-chat", status, Json.Obj("text", "X"))) - 1;
            string boundary = new string('A', CodexAdapter.HookContextBytes - headerBytes);
            Check(Encoding.UTF8.GetByteCount(adapter.HookContext("context-chat", status, Json.Obj("text", boundary))) == CodexAdapter.HookContextBytes &&
                !Directory.Exists(snapshots), "The complete context at the exact byte limit remains inline");
            string reference = adapter.HookContext("context-chat", status, Json.Obj("text", boundary + "Z"));
            Check(Encoding.UTF8.GetByteCount(reference) < CodexAdapter.HookContextBytes && reference.Contains("read the ENTIRE") &&
                Directory.GetFiles(snapshots).Length == 1 && File.ReadAllText(Directory.GetFiles(snapshots)[0]).EndsWith(boundary + "Z"),
                "One byte over the limit produces an explicit complete-file handoff without dropping its tail");
            string unicode = new string('\u754c', 12000) + "\nNEVER OMIT THIS FINAL RULE";
            reference = adapter.HookContext("context-chat", status, Json.Obj("text", unicode));
            string file = Directory.GetFiles(snapshots).Single(f => File.ReadAllText(f, Encoding.UTF8).EndsWith(unicode));
            string full = File.ReadAllText(file, Encoding.UTF8);
            Check(reference.Contains(file) && reference.Contains(Json.Hash(full)) && reference.Contains(Encoding.UTF8.GetByteCount(full).ToString()) &&
                full.Contains((string)status["instructions"]), "Unicode size uses UTF-8 bytes and its complete snapshot has a verifiable hash and byte count");
            Check(adapter.HookContext("context-chat", status, Json.Obj("text", unicode)) == reference && Directory.GetFiles(snapshots).Length == 2,
                "Repeated identical context reuses its snapshot");
            adapter.HookContext("context-chat", status, Json.Obj("text", unicode + "\nNEW RULE"));
            Check(Directory.GetFiles(snapshots).Length == 3 && File.ReadAllText(file, Encoding.UTF8) == full,
                "Updated briefings get a new snapshot without changing an earlier handoff");
        }
        private static void CodexLauncherUpdateTest()
        {
            string codexHome = Folder("codex-launcher O'Brien 中文"), install = Folder("live adapter O'Brien 中文");
            Directory.CreateDirectory(install);
            string executable = Path.Combine(install, "adapter.cmd");
            File.WriteAllText(executable, "@echo off\r\necho {\"version\":1}\r\n");
            var adapter = new CodexAdapter(codexHome);
            string package = adapter.ExportPackage(executable), plugin = Path.Combine(package, "plugins", "agent-room");
            // Reproduce Codex's cache: its launcher/metadata remain unchanged across application rebuilds.
            string cached = Folder("cached plugin O'Brien 中文"); Directory.CreateDirectory(Path.Combine(cached, "hooks"));
            foreach (string file in new[] { "adapter.json", "hooks/hooks.json", "hooks/session-start.ps1" })
                File.Copy(Path.Combine(plugin, file), Path.Combine(cached, file));
            string input = Json.Write(CodexHookInput("launcher-chat"));
            Check(Json.Number(Json.Read(RunPackagedHook(cached, codexHome, input)), "version") == 1, "Cached launcher invokes the configured installation");
            File.WriteAllText(executable, "@echo off\r\necho {\"version\":2}\r\n");
            Check(Json.Number(Json.Read(RunPackagedHook(cached, codexHome, input)), "version") == 2, "Cached launcher uses a replaced executable without setup or recopying the plugin");
            File.Delete(executable);
            string failure = RunPackagedHook(cached, codexHome, input);
            Check(Json.Required(Json.Read(failure), "systemMessage").Contains("codex setup") &&
                Json.Required(Json.Map(Json.Read(failure), "hookSpecificOutput"), "additionalContext").Contains("Do not perform room work"),
                "A moved installation produces a visible repair instruction and no stale fallback");
        }
        private static void CodexLostReplyTest()
        {
            string directory = Folder("codex-lost-reply"), codexHome = Folder("codex-retry-home");
            using (var room = new RoomService(directory))
            {
                var profile = Result(room.Admin("agent.enroll", Json.Obj("agent", "retry", "permission", "worker")));
                string file = Path.Combine(directory, "retry.profile.json"); File.WriteAllText(file, Json.Write(profile));
                var joinIds = new List<string>();
                var transport = Task.Run(() => {
                    // Commit the first join and lose its response, as during an abrupt hub disconnect.
                    // Requests: status, join (lost), retry join, verified status, fresh briefing.
                    for (int i = 0; i < 5; i++)
                        using (var pipe = new NamedPipeServerStream(room.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous, 4096, 4096, RoomServer.Security()))
                        {
                            if (!pipe.WaitForConnectionAsync().Wait(7000)) throw new Exception("Retry test transport timed out");
                            var request = Json.Read(Wire.ReadAsync(pipe).GetAwaiter().GetResult());
                            if (Json.Text(request, "cmd") == "join") joinIds.Add(Json.Required(request, "id"));
                            var response = room.Handle(request);
                            if (i != 1) Wire.WriteAsync(pipe, Json.Write(response)).GetAwaiter().GetResult();
                        }
                });
                var joined = Result(new CodexAdapter(codexHome).Join("retry-chat", file));
                Check(transport.Wait(8000), "Fault-injection transport completes");
                Check(joinIds.Count == 2 && joinIds[0] == joinIds[1], "A lost join response retries the identical operation ID");
                Check((string)joined["agent"] == "retry" && Rows(room.Admin("audit")).Count(a => (string)a["action"] == "join") == 1,
                    "Retry verifies membership without committing a second join");
            }
        }
        private static string RunCodexChild(string codexHome, string session, string arguments, string input)
        {
            var start = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"), "codex " + arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            start.EnvironmentVariables["CODEX_HOME"] = codexHome; start.EnvironmentVariables["CODEX_THREAD_ID"] = session;
            return CaptureCodexProcess(start, input);
        }
        private static string RunPackagedHook(string plugin, string codexHome, string input)
        {
            var config = Json.Map(Program.ReadJsonFile(Path.Combine(plugin, "hooks", "hooks.json"), "hooks"), "hooks");
            var group = (Dictionary<string, object>)((object[])config["SessionStart"])[0];
            string command = Json.Required((Dictionary<string, object>)((object[])group["hooks"])[0], "command");
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            start.EnvironmentVariables["CODEX_HOME"] = codexHome; start.EnvironmentVariables["PLUGIN_ROOT"] = plugin;
            return CaptureCodexProcess(start, input);
        }
        private static string CaptureCodexProcess(ProcessStartInfo start, string input)
        {
            using (var process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (input != null) process.StandardInput.Write(input); process.StandardInput.Close();
                if (!process.WaitForExit(20000)) { process.Kill(); throw new Exception("Codex adapter process timed out"); }
                Check(process.ExitCode == 0, "Codex adapter process succeeds: " + stderr.Result);
                return stdout.Result;
            }
        }
    }
}
