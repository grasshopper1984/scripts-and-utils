using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AgentRoom
{
    // Local Antigravity (AGY) connector: installs the global agent-room plugin under ~/.gemini/config/plugins/agent-room
    // and binds a conversationId to a local profile so chats need no per-agent MCP server configuration.
    internal sealed class AgyAdapter
    {
        internal const int HookContextBytes = 32 * 1024;
        private readonly string home;
        internal AgyAdapter(string agyHome)
        {
            home = Path.GetFullPath(agyHome);
            if (home.StartsWith(@"\\", StringComparison.Ordinal)) throw new RoomError("local_only", "Antigravity setup must use a local directory.");
        }
        internal static string DefaultHome
        {
            get { return Environment.GetEnvironmentVariable("AGY_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config"); }
        }
        private string StateDirectory { get { return Path.Combine(home, "agent-room"); } }
        private string BindingFile(string session) { return Path.Combine(StateDirectory, "bindings", Json.Hash(session) + ".json"); }
        internal static string Session(string supplied, string environment)
        {
            if (supplied != "" && !String.IsNullOrEmpty(environment) && supplied != environment)
                throw new RoomError("agy_session_mismatch", "The supplied session ID differs from AGY_CONVERSATION_ID. Do not reuse another conversation's ID.");
            string value = supplied != "" ? supplied : environment;
            if (String.IsNullOrEmpty(value) || !Regex.IsMatch(value, @"\A[a-zA-Z0-9_-]{1,80}\z"))
                throw new RoomError("agy_session_required", "Supply --session with this Antigravity chat's Conversation ID (or set AGY_CONVERSATION_ID). Do not invent an ID.");
            return value;
        }
        internal static string QuoteCommandExecutable(string executable)
        {
            return "& " + CodexAdapter.QuotePowerShell(executable);
        }
        private string Command(string session)
        {
            return QuoteCommandExecutable(CodexAdapter.ApplicationPath) + " agy --agy-home " +
                CodexAdapter.QuotePowerShell(home) + " --session " + CodexAdapter.QuotePowerShell(session);
        }
        internal static string Handoff(string executable, string profile, string agent)
        {
            string cmd = QuoteCommandExecutable(executable);
            return "Join the Agent Room as @" + agent + " in this Antigravity conversation, then follow its returned briefing.\r\n\r\n" +
                "Run these commands in order using your Conversation ID (from <user_information>) for --session, checking each result before continuing:\r\n" +
                cmd + " agy setup\r\n" +
                cmd + " agy join --profile " + CodexAdapter.QuotePowerShell(profile) + " --session <ConversationId>\r\n\r\n" +
                "Setup installs or updates the local AgentRoom plugin (skill and PreInvocation hook) under ~/.gemini/config/plugins/agent-room. " +
                "Pass this conversation's Conversation ID with --session; do not invent an ID or reuse another conversation's ID. " +
                "On session_conflict, stop and ask the OP to stop the old participant and reset its session in AgentRoom. " +
                "Use the returned adapter_command for subsequent room operations (no MCP server configuration is needed), and run wait as a background task (WaitMsBeforeAsync: 500) so completed waits wake this chat. " +
                "Keep profile contents private. Report join success only after the adapter verifies it.";
        }
        internal static int Run(List<string> args)
        {
            string agyHome = Program.Option(args, "--agy-home", DefaultHome);
            string supplied = Program.Option(args, "--session", "");
            string profile = Program.Option(args, "--profile", "");
            var adapter = new AgyAdapter(agyHome);
            if (args.Count == 1 && args[0] == "setup")
            {
                Console.WriteLine(Json.Write(Json.Ok(adapter.Setup(CodexAdapter.ApplicationPath)))); return 0;
            }
            if (args.Count == 1 && args[0] == "hook")
            {
                try
                {
                    var output = adapter.Hook(Json.Read(Program.ReadInput()));
                    Console.WriteLine(Json.Write(output ?? Json.Obj()));
                }
                catch (Exception e)
                {
                    var known = e as RoomError;
                    string detail = known == null ? "local adapter error" : known.Code + ": " + known.Message;
                    if (detail.Length > 2048) detail = detail.Substring(0, 2048) + "...";
                    string message = "AgentRoom context was not restored: " + detail +
                        ". Do not perform room work until the current role instructions and complete briefing can be read.";
                    Console.WriteLine(Json.Write(HookMessage(message)));
                }
                return 0;
            }
            string session = Session(supplied, Environment.GetEnvironmentVariable("AGY_CONVERSATION_ID"));
            Dictionary<string, object> response;
            if (args.Count == 1 && args[0] == "join") response = adapter.Join(session, profile);
            else if (args.Count == 1 && args[0] == "forget")
            {
                string file = adapter.BindingFile(session);
                if (File.Exists(file)) File.Delete(file);
                response = Json.Ok(Json.Obj("forgotten", true, "note", "Local binding removed. Room ownership was not reset."));
            }
            else if (args.Count == 2 && args[0] == "call")
            {
                if (profile != "") throw new RoomError("usage", "Room calls use the saved chat binding. Use agy join to bind a profile.");
                response = adapter.Call(session, args[1] == "-" ? Json.Read(Program.ReadInput()) : Program.ReadJsonFile(args[1], "request"));
            }
            else if (args.Count == 1 && new[] { "status", "briefing", "members", "inbox", "feed", "tasks", "graph", "artifacts", "audit", "escalations", "threads", "wait" }.Contains(args[0]))
            {
                if (profile != "") throw new RoomError("usage", "Room calls use the saved chat binding. Use agy join to bind a profile.");
                response = adapter.Call(session, Json.Obj("cmd", args[0], "args", Json.Obj()));
            }
            else throw new RoomError("usage", "Use agy setup, join --profile FILE --session ID, forget, status, briefing, inbox, wait, or call FILE|-. No MCP configuration is needed.");
            Console.WriteLine(Json.Write(response));
            return (bool)response["ok"] ? 0 : 2;
        }
        internal static Dictionary<string, object> HookMessage(string text)
        {
            // PreInvocation runs for existing chats too. Ephemeral context survives resume and
            // compaction without appending another full briefing to the persistent transcript.
            return Json.Obj("injectSteps", new[] { Json.Obj("ephemeralMessage", text) });
        }
        private Dictionary<string, object> Binding(string session)
        {
            string file = BindingFile(session);
            if (!File.Exists(file)) throw new RoomError("agy_not_bound", "This conversation has not joined AgentRoom. Use the selected agent's Copy join instructions.");
            var binding = Program.ReadJsonFile(file, "binding");
            if (Json.Required(binding, "session", 80) != session) throw new RoomError("agy_binding_invalid", "The saved binding belongs to another conversation.");
            return binding;
        }
        private static Dictionary<string, object> BoundProfile(Dictionary<string, object> binding)
        {
            var profile = Program.ReadJsonFile(Json.Required(binding, "profile", 2048), "profile");
            if (Json.Required(profile, "agent", 40) != Json.Required(binding, "agent", 40) ||
                Json.Required(profile, "pipe", 100) != Json.Required(binding, "pipe", 100) ||
                Json.Hash(Json.Required(profile, "token", 128)) != Json.Required(binding, "credential_hash", 64))
                throw new RoomError("agy_profile_changed", "This profile was renewed or replaced. Join explicitly with the intended profile again.");
            return profile;
        }
        private static Dictionary<string, object> Send(Dictionary<string, object> profile, string session, string operation, Dictionary<string, object> args, string id)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return Wire.Call(profile, "agy:" + session, operation, args, id); }
                catch (RoomError e)
                {
                    if ((e.Code != "hub_unavailable" && e.Code != "connection_lost") || attempt == 2) throw;
                    Thread.Sleep(150 * (attempt + 1));
                }
            }
        }
        private static Dictionary<string, object> Success(Dictionary<string, object> response)
        {
            if (!(bool)response["ok"])
            {
                var error = Json.Map(response, "error"); throw new RoomError(Json.Required(error, "code"), Json.Required(error, "message"));
            }
            return Json.Map(response, "result");
        }
        internal Dictionary<string, object> Join(string session, string profilePath)
        {
            Session(session, null);
            if (String.IsNullOrWhiteSpace(profilePath)) throw new RoomError("configuration", "agy join requires --profile FILE from AgentRoom.");
            profilePath = Path.GetFullPath(profilePath);
            var profile = Program.ReadJsonFile(profilePath, "profile");
            string agent = Json.Required(profile, "agent", 40), pipe = Json.Required(profile, "pipe", 100);
            LocalFiles.ProtectDirectory(Path.Combine(StateDirectory, "bindings"));
            using (var mutex = new Mutex(false, "Local\\AgentRoom.Agy." + Json.Hash(home.ToUpperInvariant() + "\n" + session)))
            {
                try { if (!mutex.WaitOne(10000)) throw new RoomError("agy_busy", "Another join for this conversation is still running."); }
                catch (AbandonedMutexException) { }
                try
                {
                    if (File.Exists(BindingFile(session)))
                    {
                        var previous = Binding(session);
                        if (Json.Required(previous, "agent") != agent || Json.Required(previous, "pipe") != pipe)
                            throw new RoomError("agy_binding_conflict", "This conversation is already bound to a different participant. Use a separate conversation, or explicitly run agy forget first.");
                    }
                    var status = Send(profile, session, "status", Json.Obj(), "");
                    if (!(bool)status["ok"] && Json.Text(Json.Map(status, "error"), "code") == "session_required")
                    {
                        Success(Send(profile, session, "join", Json.Obj(), Guid.NewGuid().ToString("N")));
                        status = Send(profile, session, "status", Json.Obj(), "");
                    }
                    var current = Success(status);
                    if (Json.Required(current, "self") != agent) throw new RoomError("agy_binding_invalid", "The hub returned an unexpected identity.");
                    var briefing = Success(Send(profile, session, "briefing", Json.Obj(), ""));
                    WriteFile(BindingFile(session), Json.Write(Json.Obj("session", session, "profile", profilePath, "agent", agent, "pipe", pipe,
                        "credential_hash", Json.Hash(Json.Required(profile, "token", 128)))));
                    string command = Command(session);
                    return Json.Ok(Json.Obj("agent", agent, "session", "agy:" + session, "permission", current["permission"], "instructions", current["instructions"],
                        "briefing", briefing, "adapter_command", command, "context", Context(session, current, briefing)));
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        internal Dictionary<string, object> Call(string session, Dictionary<string, object> request)
        {
            Session(session, null);
            var profile = BoundProfile(Binding(session));
            string cmd = Json.Required(request, "cmd", 40), id = Json.Text(request, "id", "", 100);
            if (cmd == "join") throw new RoomError("usage", "Use agy join --profile FILE --session ID for explicit joining.");
            var response = cmd == "wait" ? Waiter.Run(profile, "agy:" + session, Json.Map(request, "args"), CancellationToken.None)
                : Send(profile, session, cmd, Json.Map(request, "args"), id);
            response["request_id"] = id;
            return response;
        }
        internal Dictionary<string, object> Hook(Dictionary<string, object> input)
        {
            string rawSession = Json.Text(input, "conversationId", "", 80);
            if (rawSession == "") rawSession = Json.Text(input, "session_id", "", 80);
            string session = Session(rawSession, null);
            if (!File.Exists(BindingFile(session))) return null;
            var profile = BoundProfile(Binding(session));
            var status = Success(Send(profile, session, "status", Json.Obj(), ""));
            var briefing = Success(Send(profile, session, "briefing", Json.Obj(), ""));
            return HookMessage(HookContext(session, status, briefing));
        }
        internal string HookContext(string session, Dictionary<string, object> status, Dictionary<string, object> briefing)
        {
            string context = Context(session, status, briefing);
            int bytes = Encoding.UTF8.GetByteCount(context);
            if (bytes <= HookContextBytes) return context;
            string directory = Path.Combine(StateDirectory, "briefings"), hash = Json.Hash(context);
            LocalFiles.ProtectDirectory(directory);
            string file = Path.Combine(directory, Json.Hash(session).Substring(0, 16) + "-" + hash + ".md");
            WriteFile(file, context);
            string reference = "AgentRoom membership verified for this conversation, but its full context exceeds the inline limit. " +
                "Before any room work, read the ENTIRE UTF-8 file below, in bounded chunks if necessary, through the end of the room protocol. " +
                "It includes the adapter command, current role instructions, playbook, and protocol; none is included inline. " +
                "If any part cannot be read, stop room work and report the failure. Do not treat a preview as the complete briefing.\n" +
                "Context file: " + file + "\nUTF-8 bytes: " + bytes + "\nSHA-256: " + hash;
            if (Encoding.UTF8.GetByteCount(reference) > HookContextBytes)
                throw new RoomError("agy_context_too_large", "The briefing file reference exceeds the hook context limit.");
            return reference;
        }
        private string Context(string session, Dictionary<string, object> status, Dictionary<string, object> briefing)
        {
            string command = Command(session);
            return "AgentRoom membership verified: @" + Json.Required(status, "self") + ". Antigravity conversationId: " + session + ".\n" +
                "Use " + command + " <operation> for this conversation's room operations. " +
                "Write mutation or wait JSON requests (id, cmd, args) to your scratch directory and pass the file path to call; preserve id and payload on retries. " +
                "Run wait in the background (WaitMsBeforeAsync: 500) so completed waits wake this conversation.\nRole instructions: " + Json.Text(status, "instructions") + "\n\n" + Json.Required(briefing, "text", 196608);
        }
        internal Dictionary<string, object> Setup(string executable)
        {
            string plugin = ExportPackage(executable);
            return Json.Obj("installed", true, "plugin", "agent-room", "path", plugin,
                "next", "Run agy join --profile FILE --session <ConversationId> in the intended Antigravity conversation.");
        }
        internal string ExportPackage(string executable)
        {
            string plugin = Path.Combine(home, "plugins", "agent-room");
            executable = Path.GetFullPath(executable);
            string previousCopy = Path.Combine(plugin, "room.exe");
            if (executable.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(executable))
                throw new RoomError("agy_adapter_missing", "Run setup from the installed local AgentRoom executable.");
            if (String.Equals(executable, previousCopy, StringComparison.OrdinalIgnoreCase))
                throw new RoomError("agy_adapter_copy", "Run setup from the original AgentRoom installation, not a plugin copy.");
            LocalFiles.ProtectDirectory(StateDirectory);
            Directory.CreateDirectory(plugin);
            string manifest = Json.Write(Json.Obj("name", "agent-room", "description", "Join a local Windows AgentRoom and restore this conversation's role briefing."));
            WriteFile(Path.Combine(plugin, "plugin.json"), manifest);
            string hookCommand = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"./hooks/pre-invocation.ps1\"";
            WriteFile(Path.Combine(plugin, "hooks.json"), Json.Write(Json.Obj("agent-room", Json.Obj("PreInvocation", new[] {
                Json.Obj("type", "command", "command", hookCommand, "timeout", 25) }))));
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentRoom.AgyHook"))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) WriteFile(Path.Combine(plugin, "hooks", "pre-invocation.ps1"), reader.ReadToEnd());
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentRoom.AgySkill"))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) WriteFile(Path.Combine(plugin, "skills", "agent-room", "SKILL.md"), reader.ReadToEnd());
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentRoom.Readme"))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) WriteFile(Path.Combine(plugin, "skills", "agent-room", "references", "room-cli.md"), reader.ReadToEnd());
            WriteFile(Path.Combine(plugin, "adapter.json"), Json.Write(Json.Obj("executable", executable, "home", home)));
            string previousHook = Path.Combine(plugin, "hooks", "session-start.ps1");
            if (File.Exists(previousHook)) File.Delete(previousHook);
            if (File.Exists(previousCopy)) File.Delete(previousCopy);
            return plugin;
        }
        private static void WriteFile(string path, string text) { WriteBytes(path, new UTF8Encoding(false).GetBytes(text)); }
        private static void WriteBytes(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) return;
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
