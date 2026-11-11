using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AgentRoom
{
    internal static partial class Tests
    {
        private static Dictionary<string, object> AgyHookInput(string conversationId, int invocation = 0, int steps = 0)
        {
            return Json.Obj("conversationId", conversationId, "workspacePaths", new[] { output }, "invocationNum", invocation, "initialNumSteps", steps);
        }
        private static string AgyInjectedMessage(Dictionary<string, object> hookOutput)
        {
            var steps = (object[])hookOutput["injectSteps"];
            var step = (Dictionary<string, object>)steps[0];
            Check(!step.ContainsKey("systemMessage"), "Antigravity refreshes use temporary context instead of appending persistent briefings");
            return Json.Required(step, "ephemeralMessage", 200000);
        }
        private static void AgyTests()
        {
            Check(AgyAdapter.Session("", "conv-a") == "conv-a", "Antigravity session falls back to AGY_CONVERSATION_ID");
            Check(AgyAdapter.Session("conv-a", null) == "conv-a", "Antigravity session accepts explicit --session ConversationId");
            ExpectRoomError(() => AgyAdapter.Session("conv-b", "conv-a"), "agy_session_mismatch");
            ExpectRoomError(() => AgyAdapter.Session("", null), "agy_session_required");
            ExpectRoomError(() => AgyAdapter.Session("../other", null), "agy_session_required");
            string unusualInstall = Folder("agy&(tools)$literal");
            Directory.CreateDirectory(unusualInstall);
            string unusualExecutable = Path.Combine(unusualInstall, "room.exe");
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"), unusualExecutable);
            string quoted = AgyAdapter.QuoteCommandExecutable(unusualExecutable);
            Check(quoted == "& " + CodexAdapter.QuotePowerShell(unusualExecutable), "Executable paths are always PowerShell literals, including shell metacharacters");
            Check(RunAgyShell(quoted + " --help", output).Contains("Antigravity integration"), "Copied executable command runs from a path containing ampersands, parentheses, and dollar signs");

            string agyHandoff = Viewer.ConnectionHandoff(@"C:\synthetic O'Brien\mod.profile.json", "mod", "agy", "mod-stable-session");
            Check(agyHandoff.Contains("O''Brien") && agyHandoff.Contains("agy setup") && agyHandoff.Contains("agy join --profile") &&
                agyHandoff.Contains("--session <ConversationId>") && !agyHandoff.Contains("\"--mcp\""),
                "Antigravity handoff quotes profile path and instructs passing ConversationId");

            using (var dialog = new FieldsDialog("Edit agent"))
            {
                Viewer.AddAgentFields(dialog, "moderator", "agy", "Synthetic moderator instructions");
                Check((string)dialog.Values()["connector"] == "Antigravity (local)" && (string)dialog.Values()["permission"] == "moderator",
                    "Editing restores Antigravity (local) connector choice");
            }

            string agyHome = Folder("agy-home O'Brien 中文"), directory = Folder("agy-room"), profileFile = Path.Combine(directory, "agy.profile.json");
            var adapter = new AgyAdapter(agyHome);
            Dictionary<string, object> profile, generic;
            using (var room = new RoomService(directory))
            using (var server = new RoomServer(room))
            {
                server.Start();
                profile = Result(room.Admin("agent.enroll", Json.Obj("agent", "agyworker", "permission", "worker", "connector", "agy", "instructions", "Agy custom role")));
                Check((string)profile["connector"] == "agy", "Enrollment returns agy connector preference");
                generic = Result(room.Admin("agent.enroll", Json.Obj("agent", "generic", "permission", "worker")));
                File.WriteAllText(profileFile, Json.Write(profile));
                Result(Wire.Call(generic, "generic-session", "join", Json.Obj(), "generic-join"));

                Check(adapter.Hook(AgyHookInput("unbound")) == null && !Directory.Exists(agyHome), "Unbound Antigravity conversations do not create files or join rooms");
                var joined = Result(adapter.Join("conv-1", profileFile));
                Check((string)joined["session"] == "agy:conv-1" && (string)joined["agent"] == "agyworker", "Antigravity adapter binds conversationId");
                string steady = AgyInjectedMessage(adapter.Hook(AgyHookInput("conv-1", 1, 40)));
                Check(Encoding.UTF8.GetByteCount(steady) < 1024 && steady.Contains("@agyworker") && steady.Contains(" briefing") && !steady.Contains("# Agent Room"),
                    "After a join the hook sends a short pointer before each model call, not the whole briefing");
                var childJoined = Result(Json.Read(RunAgyChild(agyHome, "conv-1", "join --profile " + Quote(profileFile), null)));
                Result(Json.Read(RunAgyShell(Json.Required(childJoined, "adapter_command") + " status", agyHome)));
                string binding = Directory.GetFiles(Path.Combine(agyHome, "agent-room", "bindings")).Single();
                Check(!File.ReadAllText(binding).Contains((string)profile["token"]), "Antigravity bindings never store the raw token");

                ExpectRoomError(() => adapter.Join("conv-2", profileFile), "session_conflict");
                ExpectRoomError(() => adapter.Call("unbound", Json.Obj("cmd", "status")), "agy_not_bound");

                var request = Json.Obj("cmd", "post", "id", "agy-note", "args", Json.Obj("body", "Hello from Antigravity @generic", "kind", "note"));
                long message = Convert.ToInt64(Result(adapter.Call("conv-1", request))["message_id"]);
                Check(Convert.ToInt64(Result(adapter.Call("conv-1", request))["message_id"]) == message, "Antigravity adapter preserves mutation IDs across retries");
                Check(Rows(Wire.Call(generic, "generic-session", "inbox", Json.Obj(), "")).Count == 1, "Generic participant receives Antigravity message");

                room.Admin("agent.update", Json.Obj("agent", "agyworker", "permission", "worker", "instructions", "Updated agy instructions"));
                Directory.CreateDirectory(room.RolesDirectory);
                File.WriteAllText(Path.Combine(room.RolesDirectory, "worker.md"), "Updated agy worker playbook");
                string context = AgyInjectedMessage(adapter.Hook(AgyHookInput("conv-1", 7, 120)));
                Check(context.Contains("Updated agy instructions") && context.Contains("Updated agy worker playbook"),
                    "Antigravity PreInvocation refreshes existing conversations with current role instructions and playbook");

                Result(Json.Read(RunAgyChild(agyHome, "conv-1", "status", null)));
                string plugin = adapter.ExportPackage(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"));
                Check(File.Exists(Path.Combine(plugin, "plugin.json")) && File.Exists(Path.Combine(plugin, "hooks.json")) &&
                    File.Exists(Path.Combine(plugin, "skills", "agent-room", "SKILL.md")) &&
                    File.Exists(Path.Combine(plugin, "skills", "agent-room", "references", "room-cli.md")),
                    "Antigravity plugin package places hooks.json at plugin root alongside plugin.json and skill files");
                var hooks = Json.Map(Program.ReadJsonFile(Path.Combine(plugin, "hooks.json"), "hooks"), "agent-room");
                Check(hooks.ContainsKey("PreInvocation") && !hooks.ContainsKey("SessionStart"), "The registered hook fires for existing conversations, resumes, and post-compaction model calls");
                File.WriteAllText(Path.Combine(plugin, "hooks", "session-start.ps1"), "obsolete owned hook");
                adapter.ExportPackage(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"));
                Check(!File.Exists(Path.Combine(plugin, "hooks", "session-start.ps1")), "Setup removes the obsolete package-owned SessionStart script");

                string hookOutput = RunAgyPackagedHook(plugin, agyHome, Json.Write(AgyHookInput("conv-1", 8, 124)));
                Check(AgyInjectedMessage(Json.Read(hookOutput)).Contains("Updated agy worker playbook"),
                    "Packaged Antigravity hook preserves its custom home even when the environment points elsewhere");
                room.Admin("agent.update", Json.Obj("agent", "agyworker", "permission", "worker", "instructions", "After compaction instructions"));
                Check(AgyInjectedMessage(Json.Read(RunAgyPackagedHook(plugin, agyHome, Json.Write(AgyHookInput("conv-1", 9, 3))))).Contains("After compaction instructions"),
                    "A later invocation with a compacted trajectory receives fresh instructions");
                Check(Json.Read(RunAgyPackagedHook(plugin, agyHome, Json.Write(AgyHookInput("unbound-conv", 4, 80)))).Count == 0,
                    "Unbound existing conversations receive valid empty hook JSON without joining");

                Result(adapter.Call("conv-1", Json.Obj("cmd", "briefing")));
                string settled = AgyInjectedMessage(adapter.Hook(AgyHookInput("conv-1", 10, 8)));
                Check(Encoding.UTF8.GetByteCount(settled) < 1024 && !settled.Contains("After compaction instructions"), "Once the model has re-read the briefing, the hook returns to the pointer");
                Check(AgyInjectedMessage(adapter.Hook(AgyHookInput("conv-1", 11, 2))).Contains("After compaction instructions"), "A shorter trajectory brings the whole briefing back until it is re-read");

                string full = new string('\u754c', 12000) + "\nFINAL PROTOCOL RULE";
                string reference = adapter.HookContext("conv-1", Json.Obj("self", "agyworker", "instructions", "Current instructions"), Json.Obj("text", full));
                string snapshot = Directory.GetFiles(Path.Combine(agyHome, "agent-room", "briefings")).Single();
                Check(Encoding.UTF8.GetByteCount(reference) <= AgyAdapter.HookContextBytes && reference.Contains(snapshot) &&
                    File.ReadAllText(snapshot, Encoding.UTF8).EndsWith(full), "Oversized Antigravity context preserves the complete protocol in a bounded file handoff");

                room.Admin("agent.reset", Json.Obj("agent", "agyworker"));
                ExpectRoomError(() => adapter.Hook(AgyHookInput("conv-1")), "session_required");
                Check(AgyInjectedMessage(Json.Read(RunAgyPackagedHook(plugin, agyHome, Json.Write(AgyHookInput("conv-1", 10, 6))))).Contains("session_required"),
                    "The packaged refresh reports lost membership without reclaiming a reset profile");
                Result(adapter.Join("conv-1", profileFile));
            }
        }
        private static string RunAgyChild(string agyHome, string session, string arguments, string input)
        {
            var start = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"), "agy --agy-home " + Quote(agyHome) + " --session " + Quote(session) + " " + arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables.Remove("AGY_CONVERSATION_ID");
            return CaptureCodexProcess(start, input);
        }
        private static string RunAgyPackagedHook(string plugin, string agyHome, string input)
        {
            var hooks = Json.Map(Program.ReadJsonFile(Path.Combine(plugin, "hooks.json"), "hooks"), "agent-room");
            string command = Json.Required((Dictionary<string, object>)((object[])hooks["PreInvocation"])[0], "command");
            return RunAgyShell(command, agyHome, input, plugin);
        }
        private static string RunAgyShell(string command, string agyHome, string input = null, string directory = null)
        {
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)))
            {
                WorkingDirectory = directory ?? output, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            // A custom --agy-home must be carried by the exported package and returned command,
            // not accidentally supplied by the test process's environment.
            start.EnvironmentVariables["AGY_CONFIG_DIR"] = Path.Combine(agyHome, "unrelated-default");
            start.EnvironmentVariables.Remove("AGY_CONVERSATION_ID");
            return CaptureCodexProcess(start, input);
        }
    }
}
