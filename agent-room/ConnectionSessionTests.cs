using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentRoom
{
    internal static partial class Tests
    {
        private static string CopiedConfig(RoomService room, string file, string agent)
        {
            string session = room.McpSession(agent), config = Viewer.ConnectionConfig(file, session);
            string handoff = Viewer.ConnectionHandoff(file, agent, "generic", session);
            Check(handoff.Split(new[] { "\r\n" }, StringSplitOptions.None).Last() == config, "Copy config and Copy join instructions use the same saved session");
            var args = ((object[])Json.Read(config)["args"]).Cast<string>().ToArray();
            Check(args[Array.IndexOf(args, "--session") + 1] == session, "The copied MCP arguments contain the saved session exactly");
            return config;
        }

        private static void ConnectionSessionTests()
        {
            string directory = Folder("copied-session"), file = Path.Combine(directory, "worker.profile.json");
            Dictionary<string, object> profile;
            string first, firstConfig, peerSession, renewedSession;
            using (var room = new RoomService(directory))
            {
                profile = Result(room.Admin("agent.enroll", Json.Obj("agent", "worker", "permission", "worker", "instructions", "Initial instructions")));
                Result(room.Admin("agent.enroll", Json.Obj("agent", "peer", "permission", "worker")));
                File.WriteAllText(file, Json.Write(profile));
                first = room.McpSession("worker"); peerSession = room.McpSession("peer");
                Check(first.StartsWith("worker-") && first != peerSession, "Enrollment initializes a distinct copied session for each agent");
                long revision = room.Revision;
                firstConfig = CopiedConfig(room, file, "worker");
                Check(CopiedConfig(room, file, "worker") == firstConfig && room.Revision == revision, "Repeated copies are identical and do not change room state");
                Check((string)Rows(room.Admin("members")).Single(r => (string)r["id"] == "worker")["status"] == "not joined", "Copying does not join the agent");
                Check(!firstConfig.Contains((string)profile["token"]), "The config uses the profile path without exposing its credential");
                Result(room.Handle(ReplayRequest(profile, first, "join", Json.Obj(), "initial-join")));
                Result(room.Handle(ReplayRequest(profile, first, "status", Json.Obj())));
                Check(CopiedConfig(room, file, "worker") == firstConfig, "Joining does not regenerate the copied session");
                Result(room.Admin("agent.update", Json.Obj("agent", "worker", "permission", "worker", "instructions", "Changed instructions", "connector", "codex")));
                Check(CopiedConfig(room, file, "worker") == firstConfig, "Role instructions and connector preferences do not regenerate the copied session");
                string native = Viewer.ConnectionHandoff(file, "worker", "codex", room.McpSession("worker"));
                Check(native.Contains("CODEX_THREAD_ID") && !native.Contains(first), "The native Codex handoff still uses the actual chat identity");
                Result(room.Admin("room.reset", Json.Obj("reason", "Synthetic chat reset")));
                Check(room.McpSession("worker") == first, "Reset chats leaves the copied session unchanged");
            }
            using (var room = new RoomService(directory))
            {
                Check(CopiedConfig(room, file, "worker") == firstConfig, "The copied config survives a hub restart unchanged");
                Result(room.Handle(ReplayRequest(profile, first, "status", Json.Obj())));
                Result(room.Admin("agent.reset", Json.Obj("agent", "worker"), "reset-once"));
                string reset = room.McpSession("worker"), resetConfig = CopiedConfig(room, file, "worker");
                Check(reset != first && resetConfig == firstConfig.Replace(first, reset), "Reset session changes only the copied session argument");
                Result(room.Admin("agent.reset", Json.Obj("agent", "worker"), "reset-once"));
                Check(room.McpSession("worker") == reset && room.McpSession("peer") == peerSession, "A reset retry does not rotate again or affect another agent");
                Error(room.Handle(ReplayRequest(profile, first, "status", Json.Obj())), "session_required");
                Result(room.Handle(ReplayRequest(profile, reset, "join", Json.Obj(), "join-after-reset")));
                Check(CopiedConfig(room, file, "worker") == resetConfig, "The new config remains stable after joining with its unchanged profile credential");

                var renewed = Result(room.Admin("agent.rotate", Json.Obj("agent", "worker"), "renew-once"));
                renewedSession = room.McpSession("worker");
                File.WriteAllText(file, Json.Write(renewed));
                string renewedConfig = CopiedConfig(room, file, "worker");
                Check(renewedSession != reset && renewedConfig == resetConfig.Replace(reset, renewedSession), "Renew profile generates a new copied session");
                Error(room.Admin("agent.rotate", Json.Obj("agent", "worker"), "renew-once"), "profile_already_issued");
                Check(room.McpSession("worker") == renewedSession && room.McpSession("peer") == peerSession, "A renewal retry does not rotate again or affect another agent");
                Error(room.Handle(ReplayRequest(profile, reset, "status", Json.Obj())), "unauthorized");
                Result(room.Handle(ReplayRequest(renewed, renewedSession, "join", Json.Obj(), "join-after-renewal")));
                Result(room.Handle(ReplayRequest(renewed, renewedSession, "status", Json.Obj())));
                Check(CopiedConfig(room, file, "worker") == renewedConfig, "Copies stay stable after the renewed profile joins");
                profile = renewed;
            }
            using (var room = new RoomService(directory))
            {
                Check(room.McpSession("worker") == renewedSession, "The replacement copied session survives another restart");
                Result(room.Handle(ReplayRequest(profile, renewedSession, "status", Json.Obj())));
            }
            ConnectionSessionMigrationTests();
        }

        private static void ConnectionSessionMigrationTests()
        {
            string directory = LegacyOperatorRoom("copied-session-upgrade"), token = Json.Secret();
            using (var db = new Database(Path.Combine(directory, "room.db")))
            {
                // Schema 6 has the renamed operator, but no saved MCP handoff session.
                db.Execute("UPDATE agents SET id='op',permission='op' WHERE id='human'");
                db.Execute("INSERT INTO agents(id,permission,instructions,token_hash,session,status) VALUES('worker','worker','Existing participant',?,'existing-mcp-session','available')", Json.Hash(token));
                db.Execute("INSERT INTO agents(id,permission,instructions,session,status,connector) VALUES('native','worker','Native participant','codex:actual-chat','available','codex')");
                db.Execute("INSERT INTO agents(id,permission,instructions) VALUES('unjoined','worker','Not yet joined')");
                db.Execute("PRAGMA user_version=6");
            }
            string nativeSession, unjoinedSession;
            using (var room = new RoomService(directory))
            {
                Check(room.McpSession("worker") == "existing-mcp-session", "Migration preserves the existing generic owner's session in copied configs");
                Result(room.Handle(Json.Obj("version", RoomService.ProtocolVersion, "token", token, "session", "existing-mcp-session", "cmd", "status")));
                nativeSession = room.McpSession("native"); unjoinedSession = room.McpSession("unjoined");
                Check(nativeSession.StartsWith("native-") && unjoinedSession.StartsWith("unjoined-"), "Native and unjoined agents receive initial stable MCP copy values");
                using (var db = new Database(Path.Combine(directory, "room.db")))
                {
                    Check((string)db.One("SELECT session FROM agents WHERE id='native'")["session"] == "codex:actual-chat", "Initializing an MCP copy value does not alter native Codex ownership");
                    Check(Convert.ToInt64(db.One("PRAGMA user_version")["user_version"]) == RoomService.SchemaVersion && db.Query("PRAGMA foreign_key_check").Count == 0,
                        "The copied-session migration commits the current schema with foreign keys intact");
                }
            }
            using (var room = new RoomService(directory))
                Check(room.McpSession("worker") == "existing-mcp-session" && room.McpSession("native") == nativeSession && room.McpSession("unjoined") == unjoinedSession,
                    "Reopening does not regenerate migrated copy values");
        }
    }
}
