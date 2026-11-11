using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgentRoom
{
    internal static partial class Tests
    {
        private static int assertions;
        private static string output;
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length > 1 && args[1] == "--preview") { Preview(true); return 0; }
                DatabaseTests(); IdentityAndDelivery(); TaskTests(); ModerationTests(); ProjectTests(); MigrationTests(); NamingTests(); ReliabilityTests(); ConnectionSessionTests(); ReviewFixTests(); CoordinationTests(); BriefingTests(); BriefingContractTests(); ResponseLimitTests(); ThreadTests(); CodexTests(); AgyTests(); ViewTests();
                Console.WriteLine("Storage, identity, DAG, migration, and moderation passed: " + assertions + " assertions.");
                TransportTests(); ProcessTests(); WaitLimitTests(); WorktreeTests(); Preview(); ConnectorTests();
                Console.WriteLine("PASS: " + assertions + " assertions. Artifacts: " + output);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        private static void Check(bool result, string message) { assertions++; if (!result) throw new Exception("FAIL: " + message); }
        private static string Folder(string topic) { return Path.Combine(output, topic + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); }
        private static Dictionary<string, object> Result(Dictionary<string, object> response)
        {
            Check((bool)response["ok"], "Operation succeeds: " + ((bool)response["ok"] ? "ok" : Json.Write(response)));
            return (Dictionary<string, object>)response["result"];
        }
        private static List<Dictionary<string, object>> Rows(Dictionary<string, object> response)
        {
            Check((bool)response["ok"], "Read operation succeeds");
            object result = response["result"];
            var direct = result as List<Dictionary<string, object>>;
            return direct ?? ((object[])result).Cast<Dictionary<string, object>>().ToList();
        }
        private static void Error(Dictionary<string, object> response, string code)
        {
            Check(!(bool)response["ok"] && (string)((Dictionary<string, object>)response["error"])["code"] == code, "Expected error " + code + ": " + Json.Write(response));
        }
        private sealed class Fixture : IDisposable
        {
            internal readonly RoomService Room;
            internal readonly string Directory;
            internal long Now = 1800000000;
            internal readonly Dictionary<string, Dictionary<string, object>> Profiles = new Dictionary<string, Dictionary<string, object>>();
            internal Fixture(string topic)
            {
                Directory = Folder(topic); Room = new RoomService(Directory, () => Now);
                Enroll("coordinator", "coordinator"); Enroll("moderator", "moderator"); Enroll("alice", "worker"); Enroll("bob", "worker"); Enroll("observer", "observer");
            }
            internal void Enroll(string name, string permission)
            {
                Profiles.Add(name, Result(Room.Admin("agent.enroll", Json.Obj("agent", name, "permission", permission, "instructions", "Test role"))));
                Result(Call(name, "join"));
            }
            internal Dictionary<string, object> Call(string agent, string cmd, Dictionary<string, object> args = null, string id = null)
            {
                return Room.Handle(Json.Obj("version", RoomService.ProtocolVersion, "token", Profiles[agent]["token"], "session", agent + "-test", "cmd", cmd,
                    "args", args ?? Json.Obj(), "id", id ?? Guid.NewGuid().ToString("N")));
            }
            internal Dictionary<string, object> Ticket(string title = "Fixture ticket", string assigned = "", long parent = 0)
            {
                return Result(Call("coordinator", "task.create", Json.Obj("title", title, "objective", "Produce a fixture result", "acceptance", "Checks pass", "assigned", assigned, "parent_id", parent)));
            }
            public void Dispose() { Room.Dispose(); }
        }
        private static void DatabaseTests()
        {
            string directory = Folder("database"); LocalFiles.ProtectDirectory(directory);
            using (var db = new Database(Path.Combine(directory, "test.db")))
            {
                db.Execute("CREATE TABLE values_test(id INTEGER PRIMARY KEY,value TEXT)");
                string text = "中文 🎹 O'Reilly; DROP TABLE values_test; --\nsecond line";
                db.Execute("INSERT INTO values_test VALUES(?,?)", 1, text);
                Check((string)db.One("SELECT value FROM values_test WHERE id=?", 1)["value"] == text, "UTF8 and SQL metacharacters round trip through bindings");
                db.Execute("BEGIN IMMEDIATE"); db.Execute("INSERT INTO values_test VALUES(?,?)", 2, "rollback"); db.Execute("ROLLBACK");
                Check(db.One("SELECT id FROM values_test WHERE id=2") == null, "Rollback removes uncommitted writes");
                Check((string)db.One("PRAGMA journal_mode")["journal_mode"] == "wal", "SQLite WAL enabled");
                Check(Convert.ToInt64(db.One("PRAGMA synchronous")["synchronous"]) == 2, "Durable FULL synchronous commits");
            }
        }
        private static void IdentityAndDelivery()
        {
            using (var f = new Fixture("delivery"))
            {
                Error(f.Room.Handle(Json.Obj("version", RoomService.ProtocolVersion, "token", "wrong", "session", "x", "cmd", "status")), "unauthorized");
                Error(f.Call("alice", "agent.enroll", Json.Obj("agent", "fake", "permission", "moderator")), "forbidden");
                foreach (var attempt in new[] { Json.Obj("cmd", "agent.enroll", "agent", "lead2", "permission", "coordinator"), Json.Obj("cmd", "agent.update", "agent", "alice", "permission", "moderator") })
                {
                    try { f.Room.Admin((string)attempt["cmd"], Json.Obj("agent", attempt["agent"], "permission", attempt["permission"])); Check(false, "A second " + attempt["permission"] + " was accepted"); }
                    catch (RoomError e) { Check(e.Code == "role_taken" && e.Message.Contains("@" + attempt["permission"]) && e.Message.Contains("Reset session"), "A second " + attempt["permission"] + " is refused with the current holder and the alternative"); }
                }
                Result(f.Room.Admin("agent.update", Json.Obj("agent", "coordinator", "permission", "coordinator", "instructions", "Same role, new text")));
                Error(f.Room.Handle(Json.Obj("version", RoomService.ProtocolVersion, "token", f.Profiles["alice"]["token"], "session", "different", "cmd", "join", "args", Json.Obj(), "id", "other-session")), "session_conflict");
                var args = Json.Obj("body", "Hello @bob and @op. 中文 🎹", "kind", "note");
                long message = Convert.ToInt64(Result(f.Call("alice", "post", args, "post-once"))["message_id"]);
                Check(Convert.ToInt64(Result(f.Call("alice", "post", args, "post-once"))["message_id"]) == message, "Mutation retry returns the same message ID");
                Error(f.Call("alice", "post", Json.Obj("body", "Different input"), "post-once"), "request_id_reused");
                Check(Rows(f.Call("bob", "inbox")).Count == 1, "Mention produces one durable receipt");
                Check(Rows(f.Call("bob", "inbox")).Count == 1, "Reading does not acknowledge");
                Error(f.Call("observer", "ack", Json.Obj("message_id", message)), "not_found");
                Result(f.Call("bob", "ack", Json.Obj("message_id", message)));
                Result(f.Call("bob", "ack", Json.Obj("message_id", message)));
                Check(Rows(f.Call("bob", "inbox")).Count == 0, "Acknowledgment is idempotent and clears only recipient inbox");
                Check(Rows(f.Room.Admin("inbox")).Count == 0, "Agents cannot reach the OP by mention; escalations replace @op");
                Error(f.Call("alice", "post", Json.Obj("body", "hello", "to", new[] { "unknown" })), "unknown_recipient");
                Error(f.Call("observer", "post", Json.Obj("body", "hello")), "forbidden");
                Error(f.Call("alice", "post", Json.Obj("body", new string('x', 8193))), "invalid_argument");
                Check(Rows(f.Call("alice", "feed")).Count == 1, "Failed writes leave no messages behind");
                var old = f.Profiles["bob"];
                f.Profiles["bob"] = Result(f.Room.Admin("agent.rotate", Json.Obj("agent", "bob")));
                Error(f.Room.Handle(Json.Obj("version", RoomService.ProtocolVersion, "token", old["token"], "session", "bob-test", "cmd", "status")), "unauthorized");
                Error(f.Call("bob", "status"), "session_required"); Result(f.Call("bob", "join"));
                Result(f.Room.Admin("agent.revoke", Json.Obj("agent", "bob"))); Error(f.Call("bob", "status"), "unauthorized");
                using (var db = new Database(Path.Combine(f.Directory, "room.db")))
                {
                    string persisted = Json.Write(db.Query("SELECT response FROM requests"));
                    foreach (var profile in f.Profiles.Values) Check(!persisted.Contains((string)profile["token"]), "Credential excluded from idempotency ledger");
                }
            }
        }
        private static void TaskTests()
        {
            using (var f = new Fixture("tasks"))
            {
                // Two sessions racing to claim the same assigned ticket: only one claim can win.
                long ticket = Convert.ToInt64(f.Ticket("Fixture ticket", "alice")["id"]);
                var claim = Json.Obj("task_id", ticket, "version", 1);
                var first = System.Threading.Tasks.Task.Run(() => f.Call("alice", "task.claim", claim));
                var second = System.Threading.Tasks.Task.Run(() => f.Call("alice", "task.claim", claim));
                System.Threading.Tasks.Task.WaitAll(first, second);
                Check((bool)first.Result["ok"] != (bool)second.Result["ok"], "Exactly one competing claim succeeds");
                string owner = "alice", other = "bob";
                Error(f.Call(other, "task.update", Json.Obj("task_id", ticket, "version", 2, "state", "review", "checkpoint", "Not mine")), "forbidden");
                Error(f.Call(owner, "task.update", Json.Obj("task_id", ticket, "version", 1, "state", "review", "checkpoint", "Old version")), "stale_version");
                Error(f.Call(owner, "task.update", Json.Obj("task_id", ticket, "version", 2, "state", "done", "checkpoint", "Self approval")), "forbidden");
                Result(f.Call(owner, "task.update", Json.Obj("task_id", ticket, "version", 2, "state", "review", "checkpoint", "Implemented, tests pass")));
                Result(f.Call("coordinator", "task.update", Json.Obj("task_id", ticket, "version", 3, "state", "done", "checkpoint", "Reviewed")));
                Error(f.Call(owner, "task.update", Json.Obj("task_id", ticket, "version", 4, "state", "active", "checkpoint", "Reopen")), "conflict");
                long a = Convert.ToInt64(f.Ticket("A", "alice")["id"]), b = Convert.ToInt64(f.Ticket("B")["id"]);
                Result(f.Call("coordinator", "task.depend", Json.Obj("task_id", a, "version", 1, "depends_on", b)));
                Error(f.Call("coordinator", "task.depend", Json.Obj("task_id", b, "version", 1, "depends_on", a)), "dependency_cycle");
                Error(f.Call("alice", "task.claim", Json.Obj("task_id", a, "version", 2)), "dependencies_pending");
                Error(f.Call("moderator", "task.claim", Json.Obj("task_id", b, "version", 1)), "forbidden");
                Error(f.Call("moderator", "task.create", Json.Obj("title", "No work")), "forbidden");
                long assigned = Convert.ToInt64(f.Ticket("Assigned", "alice")["id"]);
                Error(f.Call("bob", "task.claim", Json.Obj("task_id", assigned, "version", 1)), "forbidden");
                Result(f.Call("alice", "task.claim", Json.Obj("task_id", assigned, "version", 1)));
                Result(f.Call("alice", "task.update", Json.Obj("task_id", assigned, "version", 2, "state", "paused", "checkpoint", "Quota exhausted. Next: finish tests.")));
                var paused = Rows(f.Call("alice", "tasks")).First(r => Convert.ToInt64(r["id"]) == assigned);
                Check((string)paused["owner"] == "alice" && (string)paused["state"] == "paused", "Quota checkpoint preserves ownership");
                int before = Rows(f.Call("alice", "tasks")).Count;
                Error(f.Call("coordinator", "task.create", Json.Obj("title", "Invalid dependency", "objective", "x", "acceptance", "y", "dependencies", new[] { "999999" })), "not_found");
                Check(Rows(f.Call("alice", "tasks")).Count == before, "Failed dependency creation rolls back ticket creation");
            }
        }
        private static void ModerationTests()
        {
            using (var f = new Fixture("moderation"))
            {
                long ticket = Convert.ToInt64(f.Ticket("Fixture ticket", "alice")["id"]);
                Result(f.Call("alice", "task.claim", Json.Obj("task_id", ticket, "version", 1)));
                Error(f.Call("alice", "moderate", Json.Obj("action", "pause", "reason", "I claim to be OP")), "forbidden");
                Error(f.Call("moderator", "moderate", Json.Obj("action", "mute", "target", "op", "reason", "No")), "forbidden");
                Error(f.Call("moderator", "moderate", Json.Obj("action", "mute", "target", "alice", "seconds", 901, "reason", "Too long")), "invalid_argument");
                Result(f.Call("moderator", "moderate", Json.Obj("action", "mute", "target", "alice", "seconds", 60, "reason", "Repeated requests")));
                Check((string)Result(f.Call("alice", "post", Json.Obj("body", "@bob Held request", "kind", "request", "task_id", ticket)))["visibility"] == "held", "Muted post is retained and held");
                Check(Rows(f.Call("bob", "inbox")).Count == 0, "Muted post creates no peer notification");
                Check(Rows(f.Call("bob", "feed")).All(r => (string)r["visibility"] != "held"), "Held messages hidden from other workers");
                Check(Rows(f.Call("moderator", "feed")).Any(r => (string)r["visibility"] == "held"), "Moderator can inspect held posts");
                Result(f.Call("alice", "task.update", Json.Obj("task_id", ticket, "version", 2, "state", "paused", "checkpoint", "Cooperative pause; worktree remains reserved")));
                f.Now += 61;
                Result(f.Call("alice", "post", Json.Obj("body", "@bob After expiry")));
                Check(Rows(f.Call("bob", "inbox")).Count == 1, "Expiry admits new posts without releasing held messages");
                Result(f.Call("moderator", "moderate", Json.Obj("action", "pause", "reason", "Review the exchange")));
                Check((string)Result(f.Call("bob", "post", Json.Obj("body", "@alice Work", "kind", "question", "task_id", ticket)))["visibility"] == "held", "Pause holds actionable delivery");
                Result(f.Call("alice", "task.update", Json.Obj("task_id", ticket, "version", 3, "state", "paused", "checkpoint", "Still safely paused")));
                long receipt = Convert.ToInt64(Rows(f.Call("bob", "inbox"))[0]["id"]);
                Result(f.Call("bob", "ack", Json.Obj("message_id", receipt)));
                Result(f.Room.Admin("moderate", Json.Obj("action", "resume", "reason", "OP override")));
                long child = Convert.ToInt64(f.Ticket("Child", "", ticket)["id"]);
                for (int i = 0; i < 8 + RoomService.SubtaskDeliveries; i++) Check((string)Result(f.Call("bob", "post", Json.Obj("body", "@alice Question " + i, "kind", "question", "task_id", i % 2 == 0 ? child : ticket)))["visibility"] == "published", "Root budget permits delivery " + i);
                Check((string)Result(f.Call("bob", "post", Json.Obj("body", "@alice Too many", "kind", "question", "task_id", child)))["visibility"] == "held", "Descendants share the root budget");
                Error(f.Call("alice", "budget.reset", Json.Obj("task_id", ticket, "reason", "More")), "forbidden");
                Result(f.Room.Admin("budget.reset", Json.Obj("task_id", child, "reason", "Continue this task")));
                Check((string)Result(f.Call("bob", "post", Json.Obj("body", "@alice Approved continuation", "kind", "question", "task_id", ticket)))["visibility"] == "published", "OP can reset the root budget");
                Result(f.Room.Admin("moderate", Json.Obj("action", "mute", "target", "moderator", "reason", "OP intervention")));
                Error(f.Call("moderator", "moderate", Json.Obj("action", "resume", "reason", "Bypass OP")), "muted");
                Result(f.Room.Admin("moderate", Json.Obj("action", "unmute", "target", "moderator", "reason", "Resume moderation")));
                Result(f.Call("moderator", "moderate", Json.Obj("action", "throttle", "target", "alice", "reason", "Slow down")));
                Error(f.Call("alice", "post", Json.Obj("body", "Too soon")), "rate_limited");
                f.Now += 31; Result(f.Call("alice", "post", Json.Obj("body", "Allowed after cooldown")));
                Check(Rows(f.Call("moderator", "audit")).Any(r => (string)r["action"] == "moderation"), "Moderation actions have a durable audit");
            }
        }
        private static void TransportTests()
        {
            using (var f = new Fixture("transport"))
            using (var server = new RoomServer(f.Room))
            {
                server.Start();
                Result(Wire.Call(f.Profiles["alice"], "alice-test", "status", Json.Obj(), ""));
                var calls = Enumerable.Range(0, 8).Select(i => System.Threading.Tasks.Task.Run(() => Wire.Call(f.Profiles["alice"], "alice-test", "post", Json.Obj("body", "Concurrent " + i), "wire-" + i))).ToArray();
                System.Threading.Tasks.Task.WaitAll(calls);
                Check(calls.All(t => (bool)t.Result["ok"]), "Eight independent pipe clients can write safely");
                using (var malformed = new NamedPipeClientStream(".", f.Room.PipeName, PipeDirection.InOut))
                {
                    malformed.Connect(2000); byte[] bad = BitConverter.GetBytes(1000000); malformed.Write(bad, 0, bad.Length);
                }
                Result(Wire.Call(f.Profiles["alice"], "alice-test", "status", Json.Obj(), ""));
                var acl = RoomServer.Security().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
                Check(acl.Any(rule => rule.AccessControlType == AccessControlType.Deny && rule.IdentityReference.Value == "S-1-5-2"), "Network logons explicitly denied by the pipe ACL");
                Check(acl.Count(rule => rule.AccessControlType == AccessControlType.Allow) == 1 && acl.Single(rule => rule.AccessControlType == AccessControlType.Allow).IdentityReference.Value == WindowsIdentity.GetCurrent().User.Value, "Pipe allows only the current user");
                Check(server.Healthy, "Malformed request does not crash the listener");
            }
        }
        private static Process Child(string arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"), arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            return Process.Start(start);
        }
        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        private static string RunChild(string arguments, string input, int exitCode = 0)
        {
            using (var process = Child(arguments))
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (input != null) process.StandardInput.Write(input);
                process.StandardInput.Close();
                if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Child connector timeout"); }
                Check(process.ExitCode == exitCode, "Connector exit code " + exitCode + " for " + arguments + ": " + stderr.Result);
                return stdout.Result;
            }
        }
        // Keeps stdin open so background MCP waits can answer before the connector sees end of input.
        private static string HoldChild(string arguments, string input, int holdMilliseconds)
        {
            using (var process = Child(arguments))
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                process.StandardInput.Write(input); process.StandardInput.Flush();
                System.Threading.Thread.Sleep(holdMilliseconds);
                process.StandardInput.Close();
                if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Child connector timeout"); }
                Check(process.ExitCode == 0, "Connector exit code 0 for " + arguments + ": " + stderr.Result);
                return stdout.Result;
            }
        }
        private static void ProcessTests()
        {
            string directory = Folder("restart"); Dictionary<string, object> profile; long task;
            using (var service = new RoomService(directory))
            {
                profile = Result(service.Admin("agent.enroll", Json.Obj("agent", "client", "permission", "worker")));
                File.WriteAllText(Path.Combine(directory, "sender.profile.json"), Json.Write(Result(service.Admin("agent.enroll", Json.Obj("agent", "sender", "permission", "coordinator")))), new UTF8Encoding(false));
                task = Convert.ToInt64(Result(service.Admin("task.create", Json.Obj("title", "Cross-process task", "objective", "Inspect through MCP", "acceptance", "Graph and details survive serialization")))["id"]);
            }
            string file = Path.Combine(directory, "client.profile.json"); File.WriteAllText(file, Json.Write(profile), new UTF8Encoding(false));
            string clientArgs = "--profile " + Quote(file) + " --session process-test ";
            var request = Json.Obj("id", "persistent-operation", "cmd", "post", "args", Json.Obj("body", "Durable after forced hub exit. 中文", "task_id", task));
            long message;
            using (var hub = Child("--serve --data " + Quote(directory)))
            {
                try
                {
                    Check(Json.Read(hub.StandardOutput.ReadLine()).ContainsKey("ready"), "Separate hub process starts");
                    var joined = Result(Json.Read(RunChild(clientArgs + "join", null)));
                    Check((string)((Dictionary<string, object>)joined["briefing"])["role"] == "worker", "A separate-process join returns the role briefing");
                    var briefed = Result(Json.Read(RunChild(clientArgs + "briefing", null)));
                    Check(((string)briefed["text"]).Contains("# Agent Room worker") && ((string)briefed["text"]).Contains("# Agent Room Message Protocol"), "The CLI briefing carries the playbook and the protocol");
                    // A harness may still hold the request file when it starts the connector.
                    string requestFile = Path.Combine(directory, "held-request.json");
                    File.WriteAllText(requestFile, Json.Write(Json.Obj("cmd", "status")));
                    using (var held = Child(clientArgs + "call " + Quote(requestFile)))
                    {
                        var heldOut = held.StandardOutput.ReadToEndAsync();
                        using (var holder = new FileStream(requestFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            held.StandardInput.Close();
                            System.Threading.Thread.Sleep(600);
                        }
                        Check(held.WaitForExit(15000) && held.ExitCode == 0 && (bool)Json.Read(heldOut.Result)["ok"], "A request file released within two seconds is read on retry");
                    }
                    using (var holder = new FileStream(requestFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    using (var blocked = Child(clientArgs + "call " + Quote(requestFile)))
                    {
                        var blockedErr = blocked.StandardError.ReadToEndAsync(); blocked.StandardInput.Close();
                        Check(blocked.WaitForExit(15000) && blocked.ExitCode == 1, "A request file that stays busy fails locally");
                        var error = (Dictionary<string, object>)Json.Read(blockedErr.Result.Trim())["error"];
                        Check((string)error["code"] == "request_unreadable" && ((string)error["message"]).Contains("call -"), "A busy request file is reported as such, with the stdin alternative");
                    }
                    var missing = Child(clientArgs + "call " + Quote(Path.Combine(directory, "absent.json"))); var missingErr = missing.StandardError.ReadToEndAsync(); missing.StandardInput.Close(); missing.WaitForExit(15000);
                    Check((string)((Dictionary<string, object>)Json.Read(missingErr.Result.Trim())["error"])["code"] == "request_missing", "A missing request file is named as missing");
                    message = Convert.ToInt64(Result(Json.Read(RunChild(clientArgs + "call -", Json.Write(request))))["message_id"]);
                    hub.Kill(); hub.WaitForExit();
                }
                finally { if (!hub.HasExited) { hub.Kill(); hub.WaitForExit(); } }
            }
            using (var hub = Child("--serve --data " + Quote(directory)))
            {
                try
                {
                    Check(Json.Read(hub.StandardOutput.ReadLine()).ContainsKey("ready"), "Hub restarts after forced exit");
                    Check(Convert.ToInt64(Result(Json.Read(RunChild(clientArgs + "call -", Json.Write(request))))["message_id"]) == message, "Restart preserves committed idempotency record");
                    Check(Rows(Json.Read(RunChild(clientArgs + "feed", null))).Count == 1, "Retry after restart does not duplicate the message");
                    string input = Json.Write(Json.Obj("jsonrpc", "2.0", "id", 1, "method", "initialize", "params", Json.Obj("protocolVersion", "2025-11-25"))) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "method", "notifications/initialized")) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "id", 2, "method", "tools/list")) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "id", 3, "method", "tools/call", "params", Json.Obj("name", "room_call", "arguments", Json.Obj("cmd", "feed")))) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "id", 4, "method", "tools/call", "params", Json.Obj("name", "room_call", "arguments", Json.Obj("cmd", "graph")))) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "id", 5, "method", "tools/call", "params", Json.Obj("name", "room_call", "arguments", Json.Obj("cmd", "message", "args", Json.Obj("message_id", message))))) + "\n";
                    var responses = RunChild(clientArgs + "--mcp", input).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => Json.Read(line.Trim())).ToArray();
                    Check(responses.Length == 5 && responses.All(r => r.ContainsKey("result")), "MCP framing, initialization, tools/list, and tools/call work in a separate process");
                    var toolList = Json.Map(responses[1], "result");
                    var schema = Json.Map((System.Collections.Generic.Dictionary<string, object>)((object[])toolList["tools"])[0], "inputSchema");
                    Check(Json.Map(schema, "properties").ContainsKey("cmd") && !Json.Map(schema, "properties").ContainsKey("op") && ((object[])schema["required"]).Cast<string>().SequenceEqual(new[] { "cmd" }), "MCP advertises cmd as its required request selector");
                    var toolResult = (Dictionary<string, object>)responses[2]["result"];
                    Check(!(bool)toolResult["isError"], "MCP reads the durable room through the local pipe");
                    string senderArgs = "--profile " + Quote(Path.Combine(directory, "sender.profile.json")) + " --session sender-test ";
                    Result(Json.Read(RunChild(senderArgs + "join", null)));
                    var quiet = Result(Json.Read(RunChild(clientArgs + "call -", Json.Write(Json.Obj("cmd", "wait", "args", Json.Obj("timeout_seconds", 1))))));
                    Check((bool)quiet["timed_out"] && ((object[])quiet["messages"]).Length == 0, "CLI wait times out cleanly when no mail arrives");
                    using (var waiting = Child(clientArgs + "call -"))
                    {
                        var waited = waiting.StandardOutput.ReadToEndAsync();
                        waiting.StandardInput.Write(Json.Write(Json.Obj("cmd", "wait", "args", Json.Obj("timeout_seconds", 60, "settle_seconds", 0)))); waiting.StandardInput.Close();
                        System.Threading.Thread.Sleep(1500);
                        Result(Json.Read(RunChild(senderArgs + "call -", Json.Write(Json.Obj("id", "wake-1", "cmd", "post", "args", Json.Obj("body", "@client please look", "task_id", task))))));
                        Check(waiting.WaitForExit(20000), "CLI wait returns soon after mail arrives");
                        var woke = Result(Json.Read(waited.Result));
                        Check(!(bool)woke["timed_out"] && ((object[])woke["messages"]).Length == 1 && woke.ContainsKey("room"), "A wait returns the new message with room state");
                    }
                    string concurrent = Json.Write(Json.Obj("jsonrpc", "2.0", "id", 1, "method", "initialize", "params", Json.Obj("protocolVersion", "2025-11-25"))) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "id", 10, "method", "tools/call", "params", Json.Obj("name", "room_call", "arguments", Json.Obj("cmd", "wait", "args", Json.Obj("timeout_seconds", 60))))) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "id", 11, "method", "ping")) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "method", "notifications/cancelled", "params", Json.Obj("requestId", 10))) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "id", 12, "method", "tools/call", "params", Json.Obj("name", "room_call", "arguments", Json.Obj("cmd", "status")))) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "id", 20, "method", "tools/call", "params", Json.Obj("name", "room_call", "arguments", Json.Obj("cmd", "wait", "args", Json.Obj("timeout_seconds", 60, "scope", "room"))))) + "\n" +
                        Json.Write(Json.Obj("jsonrpc", "2.0", "id", 21, "method", "tools/call", "params", Json.Obj("name", "room_call", "arguments", Json.Obj("cmd", "wait", "args", Json.Obj("timeout_seconds", 1, "settle_seconds", 0, "scope", "room"))))) + "\n";
                    var replies = HoldChild(clientArgs + "--mcp", concurrent, 7000).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => Json.Read(line.Trim()))
                        .ToDictionary(r => Convert.ToInt64(r["id"]));
                    Func<long, Dictionary<string, object>> tool = key => Result(Json.Read((string)((Dictionary<string, object>)((object[])((Dictionary<string, object>)replies[key]["result"])["content"])[0])["text"]));
                    Check(replies.ContainsKey(11) && replies.ContainsKey(12) && !replies.ContainsKey(10), "MCP answers other calls during a wait and stays silent for a cancelled one");
                    Check((bool)tool(20)["cancelled"] && (bool)tool(21)["timed_out"], "A newer MCP wait replaces the pending one, which reports cancelled");
                    string oversized = new string('x', 70000) + "\n" + Json.Write(Json.Obj("jsonrpc", "2.0", "id", 9, "method", "ping")) + "\n";
                    var survived = RunChild(clientArgs + "--mcp", oversized).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => Json.Read(line.Trim())).ToArray();
                    Check(survived.Length == 2 && survived[0]["id"] == null && survived[0].ContainsKey("error") && Convert.ToInt64(survived[1]["id"]) == 9 && survived[1].ContainsKey("result"),
                        "An oversized MCP line returns an error and the connector keeps serving");
                    foreach (var response in responses.Skip(3))
                    {
                        var result = (Dictionary<string, object>)response["result"];
                        var content = (Dictionary<string, object>)((object[])result["content"])[0];
                        var decoded = Result(Json.Read((string)content["text"]));
                        Check(!(bool)result["isError"] && (decoded.ContainsKey("nodes") || decoded.ContainsKey("tasks")), "MCP serializes graph and expanded message records");
                    }
                }
                finally { hub.StandardInput.Close(); if (!hub.WaitForExit(10000)) hub.Kill(); }
            }
            // Restart handoff: a hub started with --after waits for the running hub to exit, then serves the same room.
            using (var previous = Child("--serve --data " + Quote(directory)))
            {
                try
                {
                    Check(Json.Read(previous.StandardOutput.ReadLine()).ContainsKey("ready"), "The hub to be restarted is serving");
                    using (var next = Child("--serve --data " + Quote(directory) + " --after " + previous.Id))
                    {
                        try
                        {
                            var ready = next.StandardOutput.ReadLineAsync();
                            Check(!ready.Wait(1500), "A restarted hub waits while the previous hub runs");
                            previous.StandardInput.Close(); Check(previous.WaitForExit(10000), "The previous hub exits cleanly");
                            Check(ready.Wait(15000) && Json.Read(ready.Result).ContainsKey("ready"), "The restarted hub takes over once the previous hub exits");
                            Check(Rows(Json.Read(RunChild(clientArgs + "feed", null))).Count >= 1, "Agents keep their session and history across a restart");
                        }
                        finally { next.StandardInput.Close(); if (!next.WaitForExit(10000)) next.Kill(); }
                    }
                }
                finally { if (!previous.HasExited) { previous.Kill(); previous.WaitForExit(); } }
            }
        }
        // A burst larger than one response must arrive over several waits, each within budget, with nothing skipped.
        private static void WaitLimitTests()
        {
            string directory = Folder("wait-limits");
            Func<string, string> arguments = agent => "--profile " + Quote(Path.Combine(directory, agent + ".profile.json")) + " --session " + agent + "-test ";
            using (var service = new RoomService(directory))
                foreach (var agent in new[] { new[] { "lead", "coordinator" }, new[] { "mod", "moderator" }, new[] { "bee", "worker" } })
                    File.WriteAllText(Path.Combine(directory, agent[0] + ".profile.json"), Json.Write(Result(service.Admin("agent.enroll", Json.Obj("agent", agent[0], "permission", agent[1])))), new UTF8Encoding(false));
            using (var hub = Child("--serve --data " + Quote(directory)))
            {
                try
                {
                    Check(Json.Read(hub.StandardOutput.ReadLine()).ContainsKey("ready"), "The wait-limit hub is serving");
                    foreach (string agent in new[] { "lead", "mod", "bee" }) Result(Json.Read(RunChild(arguments(agent) + "join", null)));
                    Func<string, Dictionary<string, object>, Dictionary<string, object>> wait = (agent, args) =>
                    {
                        string text = RunChild(arguments(agent) + "call -", Json.Write(Json.Obj("cmd", "wait", "args", args)));
                        Check(Encoding.UTF8.GetByteCount(text) <= RoomService.ResponseBudgetBytes + 512, "A wait over the pipe stays within the response budget");
                        return Result(Json.Read(text));
                    };
                    // Start listening first, so the burst arrives during the settle window.
                    using (var listener = Child(arguments("mod") + "call -"))
                    {
                        var heard = listener.StandardOutput.ReadToEndAsync();
                        listener.StandardInput.Write(Json.Write(Json.Obj("cmd", "wait", "args", Json.Obj("scope", "room", "after", 0, "timeout_seconds", 60, "settle_seconds", 3)))); listener.StandardInput.Close();
                        var sent = new List<long>();
                        string body = new string('x', RoomService.MaxBodyChars);
                        for (int i = 0; i < 14; i++)
                            sent.Add(Convert.ToInt64(Result(Json.Read(RunChild(arguments("lead") + "call -", Json.Write(Json.Obj("id", "burst-" + i, "cmd", "post", "args", Json.Obj("body", body, "to", new[] { "bee" }))))))["message_id"]));
                        Check(listener.WaitForExit(60000), "The first wait returns during the burst");
                        Check(Encoding.UTF8.GetByteCount(heard.Result) <= RoomService.ResponseBudgetBytes + 512, "The first wait stays within the response budget");
                        var room = Result(Json.Read(heard.Result));
                        var received = ((object[])room["messages"]).Select(r => Convert.ToInt64(((Dictionary<string, object>)r)["id"])).ToList();
                        for (int round = 0; round < 10 && received.Count < sent.Count; round++)
                        {
                            room = wait("mod", Json.Obj("scope", "room", "after", room["cursor"], "timeout_seconds", 5, "settle_seconds", 0));
                            received.AddRange(((object[])room["messages"]).Select(r => Convert.ToInt64(((Dictionary<string, object>)r)["id"])));
                        }
                        Check(received.SequenceEqual(sent), "Room waits return a large burst completely, once each, in order");
                        var unread = new List<long>(); bool sawMore = false;
                        for (int round = 0; round < 10 && unread.Count < sent.Count; round++)
                        {
                            var inbox = wait("bee", Json.Obj("timeout_seconds", 5, "settle_seconds", 0));
                            sawMore = sawMore || (bool)inbox["more"];
                            foreach (Dictionary<string, object> row in (object[])inbox["messages"])
                            {
                                unread.Add(Convert.ToInt64(row["id"]));
                                Check((bool)row["body_truncated"], "A long inbox message arrives as a preview");
                                Result(Json.Read(RunChild(arguments("bee") + "call -", Json.Write(Json.Obj("id", "ack-" + row["id"], "cmd", "ack", "args", Json.Obj("message_id", row["id"]))))));
                            }
                        }
                        Check(sawMore && unread.SequenceEqual(sent), "An inbox wait reports more and delivers the rest after acknowledgment");
                    }
                }
                finally { hub.StandardInput.Close(); if (!hub.WaitForExit(10000)) hub.Kill(); }
            }
        }
        private static void Git(string directory, string args)
        {
            using (var process = Process.Start(new ProcessStartInfo("git.exe", args) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("Fixture Git timeout"); }
                if (process.ExitCode != 0) throw new Exception("Fixture Git failed: " + stderr.Result);
                stdout.GetAwaiter().GetResult();
            }
        }
        private static void WorktreeTests()
        {
            string root = Folder("git"); Directory.CreateDirectory(root); string worktree = root + "-worker";
            Git(root, "init -b main"); File.WriteAllText(Path.Combine(root, "fixture.txt"), "Synthetic test repository");
            Git(root, "add fixture.txt"); Git(root, "-c user.name=Test -c user.email=test@example.invalid commit -m fixture");
            Git(root, "worktree add -b worker " + Quote(worktree));
            using (var f = new Fixture("worktrees"))
            {
                try { f.Room.Admin("repository", Json.Obj("path", worktree)); Check(false, "Linked worktree accepted as the main checkout"); }
                catch (RoomError e) { Check(e.Code == "main_checkout", "Repository must be the main checkout, not a linked worktree"); }
                Result(f.Room.Admin("repository", Json.Obj("path", root)));
                long first = Convert.ToInt64(f.Ticket("Fixture ticket", "alice")["id"]), second = Convert.ToInt64(f.Ticket("Fixture ticket", "bob")["id"]);
                Error(f.Call("alice", "task.claim", Json.Obj("task_id", first, "version", 1, "worktree", root)), "main_checkout");
                var claim = Json.Obj("task_id", first, "version", 1, "worktree", worktree);
                Result(f.Call("alice", "task.claim", claim, "worktree-claim"));
                string searchPath = Environment.GetEnvironmentVariable("PATH");
                try
                {
                    Environment.SetEnvironmentVariable("PATH", "");
                    Check(Convert.ToInt64(Result(f.Call("alice", "task.claim", claim, "worktree-claim"))["id"]) == first,
                        "Committed worktree claim can be retried without invoking Git again");
                    Error(f.Call("bob", "task.claim", Json.Obj("task_id", second, "version", 1, "worktree", worktree)), "git_missing");
                }
                finally { Environment.SetEnvironmentVariable("PATH", searchPath); }
                // Git prints paths as UTF-8 whatever the console code page is, so a repository under a non-ASCII path must still be read.
                string unicode = Folder("git-中文"); Directory.CreateDirectory(unicode);
                Git(unicode, "init -b main"); File.WriteAllText(Path.Combine(unicode, "fixture.txt"), "Synthetic test repository");
                Git(unicode, "add fixture.txt"); Git(unicode, "-c user.name=Test -c user.email=test@example.invalid commit -m fixture");
                Encoding console = Console.OutputEncoding;
                try
                {
                    try { Console.OutputEncoding = Encoding.GetEncoding(437); } catch (IOException) { /* No console to switch; the read below still runs. */ }
                    Check(GitWorkspace.Read(unicode).IsMain, "A repository under a non-ASCII path is read under a non-UTF-8 console code page");
                }
                finally { try { Console.OutputEncoding = console; } catch (IOException) { } }
                Error(f.Call("bob", "task.claim", Json.Obj("task_id", second, "version", 1, "worktree", worktree.ToUpperInvariant())), "conflict");
                Result(f.Call("alice", "task.update", Json.Obj("task_id", first, "version", 2, "state", "paused", "checkpoint", "Quota handoff needs an OP to confirm the old writer stopped")));
                Error(f.Call("bob", "task.claim", Json.Obj("task_id", second, "version", 1, "worktree", worktree)), "conflict");
                Result(f.Call("coordinator", "task.update", Json.Obj("task_id", first, "version", 3, "state", "cancelled", "checkpoint", "Handoff requested")));
                Error(f.Call("bob", "task.claim", Json.Obj("task_id", second, "version", 1, "worktree", worktree)), "conflict");
                Error(f.Call("coordinator", "worktree.release", Json.Obj("task_id", first, "reason", "Self release")), "forbidden");
                Result(f.Room.Admin("worktree.release", Json.Obj("task_id", first, "reason", "Previous fixture writer is stopped")));
                Result(f.Call("bob", "task.claim", Json.Obj("task_id", second, "version", 1, "worktree", worktree)));
                Check(File.ReadAllText(Path.Combine(root, "fixture.txt")) == "Synthetic test repository", "Room inspection leaves checkout content unchanged");
            }
        }
        private static void Preview(bool interactive = false)
        {
            using (var f = new Fixture("preview"))
            using (var server = new RoomServer(f.Room))
            {
                server.Start(); long task = Convert.ToInt64(f.Ticket("Implement settings validation", "alice")["id"]);
                Result(f.Call("coordinator", "post", Json.Obj("body", "@alice Please implement the validation and report the checks you run.", "kind", "request", "task_id", task)));
                Result(f.Call("alice", "task.claim", Json.Obj("task_id", task, "version", 1)));
                Result(f.Call("alice", "post", Json.Obj("body", "@coordinator I have the ticket. I will checkpoint before any handoff.", "task_id", task)));
                Result(f.Call("alice", "post", Json.Obj("kind", "result", "reply_to", 1, "status", "partial", "body", "Path validation added\nChecks: unit tests pass. Remaining: Unicode file names.")));
                Result(f.Call("moderator", "moderate", Json.Obj("action", "warn", "target", "bob", "reason", "Keep requests attached to an existing ticket.")));
                long research = Convert.ToInt64(f.Ticket("Research local delivery guarantees", "bob")["id"]);
                long design = Convert.ToInt64(f.Ticket("Design the connector contract", "alice")["id"]);
                long implementation = Convert.ToInt64(Result(f.Call("coordinator", "task.create", Json.Obj("title", "Implement durable inbox delivery", "objective", "Deliver once per receipt", "acceptance", "Restart and retry checks pass", "assigned", "alice", "priority", 2, "dependencies", new[] { research.ToString(), design.ToString() }, "references", new[] { task.ToString() })))["id"]);
                Result(f.Call("moderator", "escalate", Json.Obj("severity", "warning", "summary", "Ticket 1 needs a decision on validation scope", "details", "alice asked twice; the coordinator has not answered.", "task_id", task)));
                Result(f.Call("coordinator", "task.create", Json.Obj("title", "Review concurrent clients", "objective", "Review the implementation", "acceptance", "Claims remain exclusive", "priority", 1, "dependencies", new[] { implementation.ToString() })));
                f.Now += 300;
                long current = Convert.ToInt64(f.Ticket("Verify reconnect behavior", "bob")["id"]);
                Result(f.Call("bob", "task.claim", Json.Obj("task_id", current, "version", 1)));
                Result(f.Call("bob", "post", Json.Obj("body", "Restart checks pass. Verifying reconnect after an interrupted reply.", "task_id", current)));
                long question = Convert.ToInt64(Result(f.Call("alice", "post", Json.Obj("body", "@coordinator Should Unicode filenames be included in the acceptance checks?", "kind", "question", "task_id", task)))["message_id"]);
                f.Now += 60;
                Result(f.Call("coordinator", "post", Json.Obj("body", "Yes. Include Unicode, spaces, and apostrophes in the filename checks.\nKeep the change scoped to input validation.", "kind", "answer", "reply_to", question)));
                File.WriteAllText(Path.Combine(f.Directory, "alice.profile.json"), Json.Write(f.Profiles["alice"]), new UTF8Encoding(false));
                using (var viewer = new Viewer(f.Room, server))
                {
                    if (interactive) viewer.Text += " · Synthetic preview";
                    viewer.Show(); Application.DoEvents(); viewer.LoadPreview(); Application.DoEvents();
                    using (var image = new Bitmap(viewer.Width, viewer.Height)) { viewer.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(output, "room-preview.png"), ImageFormat.Png); }
                    Check(viewer.Controls.Count != 0 && viewer.Controls.Cast<Control>().All(c => c.Visible && c.Bounds.Width > 0), "Native viewer lays out visible controls with populated room data");
                    foreach (string view in new[] { "Attention", "Inbox", "Tasks", "Graph", "Artifacts", "Agents", "Moderation", "Workspace" })
                    {
                        viewer.LoadPreview(view, "alice", Json.Obj()); Application.DoEvents();
                        using (var image = new Bitmap(viewer.Width, viewer.Height)) { viewer.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(output, "room-" + view.ToLowerInvariant() + ".png"), ImageFormat.Png); }
                    }
                    viewer.LoadPreviewThread(task); Application.DoEvents();
                    using (var image = new Bitmap(viewer.Width, viewer.Height)) { viewer.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(output, "room-thread.png"), ImageFormat.Png); }
                    if (interactive) { viewer.FormClosing += delegate(object sender, FormClosingEventArgs e) { e.Cancel = false; }; Application.Run(viewer); }
                }
            }
        }
    }
}
