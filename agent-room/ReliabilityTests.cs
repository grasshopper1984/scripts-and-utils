using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentRoom
{
    internal static partial class Tests
    {
        private static void ReliabilityTests()
        {
            foreach (string hold in new[] { "pause", "mute", "budget" })
                foreach (string kind in new[] { "answer", "result" }) HeldReplyTest(hold, kind);
            HeldReplyTest("mute", "note");
            JoinReplayTests();
        }

        private static void HeldReplyTest(string hold, string kind)
        {
            using (var f = new Fixture("held-reply-" + hold + "-" + kind))
            {
                long task = Convert.ToInt64(f.Ticket("Reply delivery", "alice")["id"]);
                long original = Convert.ToInt64(Result(f.Call("coordinator", "post", Json.Obj("kind", kind == "result" ? "request" : "question",
                    "task_id", task, "to", new[] { "alice" }, "body", "Please reply to this original")))["message_id"]);
                if (hold == "budget")
                    for (int i = 0; i < 7; i++) Result(f.Call("coordinator", "post", Json.Obj("kind", "question", "task_id", task, "to", new[] { "bob" }, "body", "Budget fixture " + i)));
                else Result(f.Room.Admin("moderate", hold == "pause" ? Json.Obj("action", "pause", "reason", "Pause fixture") :
                    Json.Obj("action", "mute", "target", "alice", "reason", "Mute fixture")));
                long otherAsks = hold == "budget" ? 7 : 0;
                var reply = Json.Obj("kind", kind, "reply_to", original, "body", "The requested reply");
                if (kind == "result") reply["status"] = "done";
                var held = Result(f.Call("alice", "post", reply, "held-reply"));
                long heldId = Convert.ToInt64(held["message_id"]);
                Check((string)held["visibility"] == "held", hold + " holds the " + kind);
                Check(Rows(f.Call("alice", "inbox")).Any(r => Convert.ToInt64(r["id"]) == original), "An undelivered " + kind + " leaves the original unread");
                Check(Rows(f.Call("coordinator", "inbox")).All(r => Convert.ToInt64(r["id"]) != heldId), "The held reply creates no requester receipt");
                AssertAskState(f, task, original, 1, 1 + otherAsks);
                var visible = DataRows(Result(f.Call("coordinator", "thread", Json.Obj("task_id", task)))["messages"]);
                Check(visible.All(r => Convert.ToInt64(r["id"]) != heldId), "The requester cannot see the undelivered reply");

                if (hold == "budget") Result(f.Room.Admin("budget.reset", Json.Obj("task_id", task, "reason", "Allow a new reply")));
                else Result(f.Room.Admin("moderate", hold == "pause" ? Json.Obj("action", "resume", "reason", "Resume fixture") :
                    Json.Obj("action", "unmute", "target", "alice", "reason", "Unmute fixture")));
                var retry = Result(f.Call("alice", "post", reply, "held-reply"));
                Check(Convert.ToInt64(retry["message_id"]) == heldId && (string)retry["visibility"] == "held", "Retrying the held operation does not silently release or duplicate it");
                Check(Rows(f.Call("alice", "inbox")).Any(r => Convert.ToInt64(r["id"]) == original), "Retrying a held reply still leaves the original unread");

                var published = Result(f.Call("alice", "post", reply, "published-reply"));
                long publishedId = Convert.ToInt64(published["message_id"]);
                Check((string)published["visibility"] == "published" && publishedId != heldId, "A fresh reply is published after the hold is resolved");
                Check(Rows(f.Call("alice", "inbox")).All(r => Convert.ToInt64(r["id"]) != original), "The published reply acknowledges the original");
                Check(Rows(f.Call("coordinator", "inbox")).Count(r => Convert.ToInt64(r["id"]) == publishedId) == 1, "The published reply reaches the requester once");
                long stillOpen = kind == "note" ? 1 : 0;
                AssertAskState(f, task, original, stillOpen, stillOpen + otherAsks);
                Check(Convert.ToInt64(Result(f.Call("alice", "post", reply, "published-reply"))["message_id"]) == publishedId, "A successful reply retry returns the original delivery");
            }
        }

        private static void AssertAskState(Fixture f, long task, long message, long open, long total)
        {
            foreach (string agent in new[] { "coordinator", "moderator" })
            {
                var thread = Result(f.Call(agent, "thread", Json.Obj("task_id", task)));
                Check(Convert.ToInt64(DataRows(thread["messages"]).Single(r => Convert.ToInt64(r["id"]) == message)["open_ask"]) == open,
                    agent + " sees the original ask state based on published replies");
            }
            Check(Convert.ToInt64(Rows(f.Call("coordinator", "threads")).Single(r => Convert.ToInt64(r["task_id"]) == task)["open_asks"]) == total,
                "Thread-list counts agree with published replies");
            var graph = Result(f.Room.Admin("graph"));
            Check(Convert.ToInt64(DataRows(graph["nodes"]).Single(r => Convert.ToInt64(r["id"]) == task)["open_asks"]) == total,
                "Graph counts agree with published replies");
        }

        private static Dictionary<string, object> ReplayRequest(Dictionary<string, object> profile, string session, string cmd, Dictionary<string, object> args, string id = "")
        {
            return Json.Obj("version", RoomService.ProtocolVersion, "token", profile["token"], "session", session, "cmd", cmd, "args", args, "id", id);
        }

        private static void JoinReplayTests()
        {
            string directory = Folder("join-replay");
            Dictionary<string, object> profile, originalJoin, post;
            long message;
            using (var room = new RoomService(directory))
            {
                profile = Result(room.Admin("agent.enroll", Json.Obj("agent", "worker", "permission", "worker", "instructions", "Replay fixture")));
                originalJoin = ReplayRequest(profile, "first-session", "join", Json.Obj(), "join-001");
                var joined = room.Handle(originalJoin); Result(joined);
                Check(Json.Write(room.Handle(originalJoin)) == Json.Write(joined), "A retry in the current session replays its successful join");
                Check(Rows(room.Admin("audit")).Count(r => (string)r["action"] == "join") == 1, "The join retry does not create another join");
                post = ReplayRequest(profile, "first-session", "post", Json.Obj("body", "Keep committed deliveries across reset"), "post-once");
                message = Convert.ToInt64(Result(room.Handle(post))["message_id"]);
                Result(room.Admin("agent.reset", Json.Obj("agent", "worker")));
                Error(room.Handle(originalJoin), "session_required");
                Error(room.Handle(ReplayRequest(profile, "first-session", "status", Json.Obj())), "session_required");
                Check((string)Rows(room.Admin("members")).Single(r => (string)r["id"] == "worker")["status"] == "not joined", "A stale retry never reattaches a reset session");
            }
            using (var room = new RoomService(directory))
            {
                Error(room.Handle(originalJoin), "session_required");
                var freshJoin = ReplayRequest(profile, "first-session", "join", Json.Obj(), "join-after-reset");
                Result(room.Handle(freshJoin)); Result(room.Handle(freshJoin));
                Result(room.Handle(ReplayRequest(profile, "first-session", "status", Json.Obj())));
                Check(Convert.ToInt64(Result(room.Handle(post))["message_id"]) == message && Rows(room.Admin("feed")).Count == 1,
                    "Reset preserves other mutation receipts, so an uncertain post retry does not duplicate delivery");

                Result(room.Admin("agent.reset", Json.Obj("agent", "worker")));
                Result(room.Handle(ReplayRequest(profile, "replacement-session", "join", Json.Obj(), "replacement-join")));
                Error(room.Handle(originalJoin), "session_conflict");
                Error(room.Handle(freshJoin), "session_conflict");
                Result(room.Handle(ReplayRequest(profile, "replacement-session", "status", Json.Obj())));
                Error(room.Handle(ReplayRequest(profile, "first-session", "status", Json.Obj())), "session_required");

                var renewed = Result(room.Admin("agent.rotate", Json.Obj("agent", "worker")));
                Error(room.Handle(originalJoin), "unauthorized");
                Error(room.Handle(ReplayRequest(renewed, "first-session", "join", Json.Obj(), "join-001")), "session_required");
                Result(room.Handle(ReplayRequest(renewed, "first-session", "join", Json.Obj(), "renewed-join")));
                Result(room.Handle(ReplayRequest(renewed, "first-session", "status", Json.Obj())));
            }
        }
    }
}
