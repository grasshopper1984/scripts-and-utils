using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRoom
{
    // The hub remains harness-neutral. This adapter owns only local Codex setup and chat bindings.
    internal sealed class CodexAdapter
    {
        internal const int HookContextBytes = 32 * 1024;
        private readonly string home;
        internal CodexAdapter(string codexHome)
        {
            home = Path.GetFullPath(codexHome);
            if (home.StartsWith(@"\\", StringComparison.Ordinal)) throw new RoomError("local_only", "Codex setup must use a local directory.");
        }
        internal static string DefaultHome
        {
            get { return Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"); }
        }
        private string StateDirectory { get { return Path.Combine(home, "agent-room"); } }
        private string BindingFile(string session) { return Path.Combine(StateDirectory, "bindings", Json.Hash(session) + ".json"); }
        internal static string Session(string supplied, string environment)
        {
            if (supplied != "" && !String.IsNullOrEmpty(environment) && supplied != environment)
                throw new RoomError("codex_session_mismatch", "The supplied session ID differs from this Codex chat. Do not reuse another chat's ID.");
            string value = supplied != "" ? supplied : environment;
            if (String.IsNullOrEmpty(value) || !Regex.IsMatch(value, @"\A[a-zA-Z0-9_-]{1,80}\z"))
                throw new RoomError("codex_session_required", "Run inside the intended Codex chat with CODEX_THREAD_ID available, or supply --session with its verified hook session_id. Do not invent an ID.");
            return value;
        }
        internal static string QuotePowerShell(string text) { return "'" + text.Replace("'", "''") + "'"; }
        internal static string Handoff(string executable, string profile, string agent)
        {
            string command = "& " + QuotePowerShell(executable);
            return "Join the Agent Room as @" + agent + " in this local Codex chat, then follow its returned briefing.\r\n\r\n" +
                "Run these PowerShell commands in order, checking each result before continuing:\r\n" +
                command + " codex setup\r\n" + command + " codex join --profile " + QuotePowerShell(profile) + "\r\n\r\n" +
                "Setup installs or updates the local AgentRoom skill and session hook. Review untrusted hooks in Codex Settings > Hooks (CLI: /hooks); do not bypass hook trust. " +
                "If the newly installed plugin is not loaded, reopen/resume this chat and repeat the join. " +
                "The adapter uses this chat's CODEX_THREAD_ID, not a copied session name. If that variable is unavailable, use only this chat's verified hook session_id with --session, or stop and report the missing identity. " +
                "On session_conflict, stop and ask the OP to stop the old participant and reset its session in AgentRoom. " +
                "Use the returned adapter command for subsequent room operations; do not use an older MCP connector for this profile. " +
                "Keep profile contents private. Report join success only after the adapter verifies it.";
        }
        internal static int Run(List<string> args)
        {
            string codexHome = Program.Option(args, "--codex-home", DefaultHome);
            string supplied = Program.Option(args, "--session", "");
            string profile = Program.Option(args, "--profile", "");
            var adapter = new CodexAdapter(codexHome);
            if (args.Count == 1 && args[0] == "setup")
            {
                Console.WriteLine(Json.Write(Json.Ok(adapter.Setup(ApplicationPath, "codex.exe")))); return 0;
            }
            if (args.Count == 1 && args[0] == "hook")
            {
                // Hook failures are visible but must never break an unrelated Codex session.
                try
                {
                    var output = adapter.Hook(Json.Read(Program.ReadInput()));
                    if (output != null) Console.WriteLine(Json.Write(output));
                }
                catch (Exception e)
                {
                    var known = e as RoomError;
                    string detail = known == null ? "local adapter error" : known.Code + ": " + known.Message;
                    if (detail.Length > 2048) detail = detail.Substring(0, 2048) + "...";
                    string message = "AgentRoom context was not restored: " + detail +
                        ". Do not perform room work until the current role instructions and complete briefing can be read.";
                    Console.WriteLine(Json.Write(Json.Obj("systemMessage", message, "hookSpecificOutput",
                        Json.Obj("hookEventName", "SessionStart", "additionalContext", message))));
                }
                return 0;
            }
            string session = Session(supplied, Environment.GetEnvironmentVariable("CODEX_THREAD_ID"));
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
                if (profile != "") throw new RoomError("usage", "Room calls use the saved chat binding. Use codex join to bind a profile.");
                response = adapter.Call(session, args[1] == "-" ? Json.Read(Program.ReadInput()) : Program.ReadJsonFile(args[1], "request"));
            }
            else if (args.Count == 1 && new[] { "status", "briefing", "members", "inbox", "feed", "tasks", "graph", "artifacts", "audit", "escalations", "threads", "wait" }.Contains(args[0]))
            {
                if (profile != "") throw new RoomError("usage", "Room calls use the saved chat binding. Use codex join to bind a profile.");
                response = adapter.Call(session, Json.Obj("cmd", args[0], "args", Json.Obj()));
            }
            else throw new RoomError("usage", "Use codex setup, join --profile FILE, forget, status, briefing, inbox, wait, or call FILE|-. No MCP configuration is needed.");
            Console.WriteLine(Json.Write(response));
            return (bool)response["ok"] ? 0 : 2;
        }
        internal static string ApplicationPath { get { return Assembly.GetExecutingAssembly().Location; } }
        private Dictionary<string, object> Binding(string session)
        {
            string file = BindingFile(session);
            if (!File.Exists(file)) throw new RoomError("codex_not_bound", "This chat has not joined AgentRoom. Use the selected agent's Copy join instructions.");
            var binding = Program.ReadJsonFile(file, "binding");
            if (Json.Required(binding, "session", 80) != session) throw new RoomError("codex_binding_invalid", "The saved binding belongs to another chat.");
            return binding;
        }
        private static Dictionary<string, object> BoundProfile(Dictionary<string, object> binding)
        {
            var profile = Program.ReadJsonFile(Json.Required(binding, "profile", 2048), "profile");
            if (Json.Required(profile, "agent", 40) != Json.Required(binding, "agent", 40) ||
                Json.Required(profile, "pipe", 100) != Json.Required(binding, "pipe", 100) ||
                Json.Hash(Json.Required(profile, "token", 128)) != Json.Required(binding, "credential_hash", 64))
                throw new RoomError("codex_profile_changed", "This profile was renewed or replaced. Join explicitly with the intended profile again.");
            return profile;
        }
        private static Dictionary<string, object> Send(Dictionary<string, object> profile, string session, string operation, Dictionary<string, object> args, string id)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return Wire.Call(profile, "codex:" + session, operation, args, id); }
                catch (RoomError e)
                {
                    if ((e.Code != "hub_unavailable" && e.Code != "connection_lost") || attempt == 2) throw;
                    Thread.Sleep(150 * (attempt + 1)); // The same mutation ID and payload survive an uncertain response.
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
            if (String.IsNullOrWhiteSpace(profilePath)) throw new RoomError("configuration", "codex join requires --profile FILE from AgentRoom.");
            profilePath = Path.GetFullPath(profilePath);
            var profile = Program.ReadJsonFile(profilePath, "profile");
            string agent = Json.Required(profile, "agent", 40), pipe = Json.Required(profile, "pipe", 100);
            LocalFiles.ProtectDirectory(Path.Combine(StateDirectory, "bindings"));
            // Serialize binding changes for this chat; other chats remain independent.
            using (var mutex = new Mutex(false, "Local\\AgentRoom.Codex." + Json.Hash(home.ToUpperInvariant() + "\n" + session)))
            {
                try { if (!mutex.WaitOne(10000)) throw new RoomError("codex_busy", "Another join for this chat is still running."); }
                catch (AbandonedMutexException) { /* A crashed join released ownership; verify the hub before saving a binding. */ }
                try
                {
                    if (File.Exists(BindingFile(session)))
                    {
                        var previous = Binding(session);
                        if (Json.Required(previous, "agent") != agent || Json.Required(previous, "pipe") != pipe)
                            throw new RoomError("codex_binding_conflict", "This chat is already bound to a different participant. Use a separate chat, or explicitly run codex forget first.");
                    }
                    var status = Send(profile, session, "status", Json.Obj(), "");
                    if (!(bool)status["ok"] && Json.Text(Json.Map(status, "error"), "code") == "session_required")
                    {
                        Success(Send(profile, session, "join", Json.Obj(), Guid.NewGuid().ToString("N")));
                        status = Send(profile, session, "status", Json.Obj(), "");
                    }
                    var current = Success(status);
                    if (Json.Required(current, "self") != agent) throw new RoomError("codex_binding_invalid", "The hub returned an unexpected identity.");
                    var briefing = Success(Send(profile, session, "briefing", Json.Obj(), ""));
                    WriteFile(BindingFile(session), Json.Write(Json.Obj("session", session, "profile", profilePath, "agent", agent, "pipe", pipe,
                        "credential_hash", Json.Hash(Json.Required(profile, "token", 128)))));
                    return Json.Ok(Json.Obj("agent", agent, "session", "codex:" + session, "permission", current["permission"], "instructions", current["instructions"],
                        "briefing", briefing, "adapter_command", "& " + QuotePowerShell(ApplicationPath) + " codex", "context", Context(session, current, briefing)));
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        internal Dictionary<string, object> Call(string session, Dictionary<string, object> request)
        {
            Session(session, null);
            var profile = BoundProfile(Binding(session));
            string cmd = Json.Required(request, "cmd", 40), id = Json.Text(request, "id", "", 100);
            if (cmd == "join") throw new RoomError("usage", "Use codex join --profile FILE for explicit joining.");
            var response = cmd == "wait" ? Waiter.Run(profile, "codex:" + session, Json.Map(request, "args"), CancellationToken.None)
                : Send(profile, session, cmd, Json.Map(request, "args"), id);
            response["request_id"] = id;
            return response;
        }
        internal Dictionary<string, object> Hook(Dictionary<string, object> input)
        {
            if (Json.Text(input, "hook_event_name") != "SessionStart") return null;
            string session = Session(Json.Required(input, "session_id", 80), null);
            if (!File.Exists(BindingFile(session))) return null; // Includes new/forked chats. Never enroll implicitly.
            var profile = BoundProfile(Binding(session));
            var status = Success(Send(profile, session, "status", Json.Obj(), ""));
            // Do not re-join on session_required: an admin reset must not let an old hook reclaim the profile.
            var briefing = Success(Send(profile, session, "briefing", Json.Obj(), ""));
            return Json.Obj("hookSpecificOutput", Json.Obj("hookEventName", "SessionStart", "additionalContext", HookContext(session, status, briefing)));
        }
        internal string HookContext(string session, Dictionary<string, object> status, Dictionary<string, object> briefing)
        {
            string context = Context(session, status, briefing);
            int bytes = Encoding.UTF8.GetByteCount(context);
            if (bytes <= HookContextBytes) return context;
            // Codex's limit is an approximate token threshold. Disable its preview only because
            // we bound the complete output ourselves, including the header and role instructions.
            string directory = Path.Combine(StateDirectory, "briefings"), hash = Json.Hash(context);
            LocalFiles.ProtectDirectory(directory);
            string file = Path.Combine(directory, Json.Hash(session).Substring(0, 16) + "-" + hash + ".md");
            WriteFile(file, context);
            string reference = "AgentRoom membership verified for this chat, but its full context exceeds the inline limit. " +
                "Before any room work, read the ENTIRE UTF-8 file below, in bounded chunks if necessary, through the end of the room protocol. " +
                "It includes the adapter command, current role instructions, playbook, and protocol; none is included inline. " +
                "If any part cannot be read, stop room work and report the failure. Do not treat a preview as the complete briefing.\n" +
                "Context file: " + file + "\nUTF-8 bytes: " + bytes + "\nSHA-256: " + hash;
            if (Encoding.UTF8.GetByteCount(reference) > HookContextBytes)
                throw new RoomError("codex_context_too_large", "The briefing file reference exceeds the hook context limit.");
            return reference;
        }
        private static string Context(string session, Dictionary<string, object> status, Dictionary<string, object> briefing)
        {
            return "AgentRoom membership verified: @" + Json.Required(status, "self") + ". Codex session_id: " + session + ".\n" +
                "Use & " + QuotePowerShell(ApplicationPath) + " codex <operation> for this chat's room operations. " +
                "If CODEX_THREAD_ID is unavailable, pass --session " + QuotePowerShell(session) + ". " +
                "Use call - with a JSON request (id, cmd, args) for mutations; preserve id and payload on retries. " +
                "Read the installed agent-room skill for the CLI workflow.\nRole instructions: " + Json.Text(status, "instructions") + "\n\n" + Json.Required(briefing, "text", 196608);
        }
        internal Dictionary<string, object> Setup(string executable, string codexExecutable)
        {
            string root = ExportPackage(executable);
            // Let Codex own its config/cache format. No direct TOML edits or hook-trust changes.
            RunCodex(codexExecutable, "plugin marketplace add " + QuoteArgument(root) + " --json");
            RunCodex(codexExecutable, "plugin add agent-room@agent-room-local --json");
            return Json.Obj("installed", true, "plugin", "agent-room@agent-room-local", "marketplace", root,
                "next", "Review AgentRoom hooks in Codex Settings > Hooks (CLI: /hooks). If the plugin is not loaded in this chat, reopen/resume it. Then run codex join with the selected profile.");
        }
        internal string ExportPackage(string executable)
        {
            string root = Path.Combine(StateDirectory, "marketplace"), plugin = Path.Combine(root, "plugins", "agent-room");
            executable = Path.GetFullPath(executable);
            string previousCopy = Path.Combine(plugin, "room.exe");
            if (executable.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(executable))
                throw new RoomError("codex_adapter_missing", "Run setup from the installed local AgentRoom executable.");
            if (String.Equals(executable, previousCopy, StringComparison.OrdinalIgnoreCase))
                throw new RoomError("codex_adapter_copy", "Run setup from the original AgentRoom installation, not the old plugin copy.");
            LocalFiles.ProtectDirectory(StateDirectory);
            string manifest = Json.Write(Json.Obj("name", "agent-room", "version", "0.3.0", "description", "Join a local Windows AgentRoom and restore this chat's role briefing.",
                "skills", "./skills/", "hooks", "./hooks/hooks.json", "interface", Json.Obj("displayName", "AgentRoom", "shortDescription", "Join and work in a local agent room", "developerName", "AgentRoom", "category", "Productivity")));
            WriteFile(Path.Combine(plugin, ".codex-plugin", "plugin.json"), manifest);
            string hookCommand = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command '& (Join-Path $env:PLUGIN_ROOT \"hooks/session-start.ps1\")'";
            WriteFile(Path.Combine(plugin, "hooks", "hooks.json"), Json.Write(Json.Obj("hooks", Json.Obj("SessionStart", new[] {
                Json.Obj("matcher", "^(startup|resume|clear|compact|fork)$", "hooks", new[] { Json.Obj("type", "command", "command", hookCommand,
                    "timeout", 25, "additionalContextLimit", 0) }) }))));
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentRoom.CodexHook"))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) WriteFile(Path.Combine(plugin, "hooks", "session-start.ps1"), reader.ReadToEnd());
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentRoom.CodexSkill"))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) WriteFile(Path.Combine(plugin, "skills", "agent-room", "SKILL.md"), reader.ReadToEnd());
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentRoom.Readme"))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) WriteFile(Path.Combine(plugin, "skills", "agent-room", "references", "room-cli.md"), reader.ReadToEnd());
            WriteFile(Path.Combine(plugin, "adapter.json"), Json.Write(Json.Obj("executable", executable)));
            // Upgrade only the old package-owned copy; never remove the configured installation.
            if (File.Exists(previousCopy)) File.Delete(previousCopy);
            WriteFile(Path.Combine(root, ".agents", "plugins", "marketplace.json"), Json.Write(Json.Obj("name", "agent-room-local",
                "interface", Json.Obj("displayName", "Local AgentRoom"), "plugins", new[] { Json.Obj("name", "agent-room", "source", Json.Obj("source", "local", "path", "./plugins/agent-room"),
                    "policy", Json.Obj("installation", "AVAILABLE", "authentication", "ON_INSTALL"), "category", "Productivity") })));
            return root;
        }
        private void RunCodex(string executable, string arguments)
        {
            var start = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.EnvironmentVariables["CODEX_HOME"] = home;
            try
            {
                using (var process = Process.Start(start))
                {
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(30000)) { process.Kill(); throw new RoomError("codex_setup_timeout", "Codex plugin setup timed out. Retry setup after checking Codex."); }
                    if (process.ExitCode != 0) throw new RoomError("codex_setup_failed", "Codex plugin setup failed: " + stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
                    stdout.GetAwaiter().GetResult(); stderr.GetAwaiter().GetResult();
                }
            }
            catch (System.ComponentModel.Win32Exception) { throw new RoomError("codex_missing", "Codex CLI was not found. Install it or add codex.exe to PATH, then repeat setup."); }
        }
        internal static string QuoteArgument(string value)
        {
            // Windows CommandLineToArgvW quoting, including backslashes before quotes and the closing quote.
            var quoted = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); quoted.Append(c); slashes = 0;
            }
            return quoted.Append('\\', slashes * 2).Append('"').ToString();
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
