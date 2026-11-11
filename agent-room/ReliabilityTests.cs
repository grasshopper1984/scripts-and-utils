using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace AgentRoom
{
    internal static partial class Tests
    {
        private static void ReliabilityTests()
        {
            foreach (string hold in new[] { "pause", "mute" })
                foreach (string kind in new[] { "answer", "result" }) HeldReplyTest(hold, kind);
            HeldReplyTest("mute", "note");
            BudgetTests();
            LedgerStatusAndArgumentTests();
            JoinReplayTests();
            ListenerSurvivalTest();
            WaitWakeTests();
            AddressingAndInputTests();
        }

        // A client that connects and leaves without sending anything must not stop the listener.
        private static void ListenerSurvivalTest()
        {
            using (var f = new Fixture("listener"))
            using (var server = new RoomServer(f.Room))
            {
                server.Start();
                for (int i = 0; i < 300; i++)
                    using (var client = new NamedPipeClientStream(".", f.Room.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                        try { client.Connect(2000); } catch (TimeoutException) { } catch (IOException) { }
                // Checking whether the pipe exists also opens and closes a connection.
                for (int i = 0; i < 20; i++) File.Exists(@"\\.\pipe\" + f.Room.PipeName);
                Result(Wire.Call(f.Profiles["alice"], "alice-test", "status", Json.Obj(), ""));
                Check(server.Healthy, "Clients that connect and leave without a request do not stop the listener");
            }
            // Many clients at once can leave stale cancellation callbacks behind; shutting down must still be clean.
            using (var f = new Fixture("listener-shutdown"))
            {
                var server = new RoomServer(f.Room);
                server.Start();
                var clients = Enumerable.Range(0, 8).Select(n => System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 1500; i++)
                        using (var client = new NamedPipeClientStream(".", f.Room.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                            try { client.Connect(2000); } catch (TimeoutException) { } catch (IOException) { }
                })).ToArray();
                System.Threading.Tasks.Task.WaitAll(clients);
                Result(Wire.Call(f.Profiles["alice"], "alice-test", "status", Json.Obj(), ""));
                bool clean = true;
                try { server.Dispose(); } catch (AggregateException) { clean = false; }
                Check(clean, "The listener shuts down cleanly after a burst of abandoned connections");
            }
        }

        // An inbox wait wakes for mail no earlier wait reported. Unread mail it already reported returns only as a
        // reminder on timeout, so a request whose reply is held cannot wake its recipient on every call.
        private static void WaitWakeTests()
        {
            using (var f = new Fixture("wait-wake"))
            using (var server = new RoomServer(f.Room))
            {
                server.Start();
                long task = Convert.ToInt64(f.Ticket("Held reply", "alice")["id"]);
                long request = Convert.ToInt64(Result(f.Call("coordinator", "post", Json.Obj("kind", "request", "task_id", task, "to", new[] { "alice" }, "body", "Please do this")))["message_id"]);
                Result(f.Room.Admin("moderate", Json.Obj("action", "pause", "reason", "Pause fixture")));
                Func<long, Dictionary<string, object>> wait = seconds =>
                    Result(Waiter.Run(f.Profiles["alice"], "alice-test", Json.Obj("timeout_seconds", seconds, "settle_seconds", 0), CancellationToken.None));
                Func<Dictionary<string, object>, string> ids = result => String.Join(",", ((object[])result["messages"]).Select(m => Convert.ToString(((Dictionary<string, object>)m)["id"])));
                var first = wait(30);
                Check(!(bool)first["timed_out"] && ids(first) == request.ToString() && Convert.ToInt64(first["unread"]) == 1, "A wait reports new mail at once");
                Check((string)Result(f.Call("alice", "post", Json.Obj("kind", "result", "reply_to", request, "status", "done", "body", "Done")))["visibility"] == "held", "The pause holds the reply");
                var clock = Stopwatch.StartNew();
                var second = wait(3);
                Check((bool)second["timed_out"] && clock.Elapsed.TotalSeconds >= 2.5, "Unread mail a wait already reported does not wake the next wait");
                Check(ids(second) == request.ToString() && Convert.ToInt64(second["unread"]) == 1 && (bool)((Dictionary<string, object>)second["room"])["paused"],
                    "A timeout repeats still-unread mail as a reminder, with the room state");
                long note = Convert.ToInt64(Result(f.Call("coordinator", "post", Json.Obj("to", new[] { "alice" }, "body", "A newer note")))["message_id"]);
                var third = wait(30);
                Check(!(bool)third["timed_out"] && ids(third) == note.ToString() && Convert.ToInt64(third["unread"]) == 2, "Newer mail wakes the wait and arrives without the older reminder");
                Result(f.Call("alice", "join"));
                var rejoined = wait(30);
                Check(!(bool)rejoined["timed_out"] && ids(rejoined) == request + "," + note, "A session that joins again starts from every unread message");
                Check(Convert.ToInt64(Result(f.Call("alice", "inbox.seen", Json.Obj("message_id", 999999)))["inbox_seen"]) == note, "The reported-mail mark never passes the newest message");
            }
            var absent = Json.Obj("pipe", "ScriptsAndUtils.AgentRoom.000000000000000000000000", "token", "none");
            var unreachable = Result(Waiter.Run(absent, "nobody", Json.Obj("timeout_seconds", 1), CancellationToken.None));
            Check((bool)unreachable["timed_out"] && unreachable.ContainsKey("hub_unreachable") && (bool)unreachable["hub_unreachable"],
                "A wait that never reaches the hub says so instead of looking like a quiet room");
        }

        private static void AddressingAndInputTests()
        {
            using (var f = new Fixture("addressing"))
            using (var server = new RoomServer(f.Room))
            {
                server.Start();
                long task = Convert.ToInt64(f.Ticket("Addressing", "alice")["id"]), other = Convert.ToInt64(f.Ticket("Other ticket", "bob")["id"]);
                Func<string, long> ask = body => Convert.ToInt64(Result(f.Call("coordinator", "post", Json.Obj("kind", "request", "task_id", task, "to", new[] { "alice" }, "body", body)))["message_id"]);
                // A mention in a reply adds a reader. It must not take the requester off the result.
                var mentioned = Result(f.Call("alice", "post", Json.Obj("kind", "result", "reply_to", ask("First request"), "status", "done", "body", "Done. @bob may want to see the parser change.")));
                Check(String.Join(",", (string[])mentioned["recipients"]) == "bob,coordinator" &&
                    Rows(f.Call("coordinator", "inbox")).Any(r => Convert.ToInt64(r["id"]) == Convert.ToInt64(mentioned["message_id"])), "A body mention in a reply still reaches the requester");
                var redirected = Result(f.Call("alice", "post", Json.Obj("kind", "result", "reply_to", ask("Second request"), "status", "done", "to", new[] { "bob" }, "body", "Handing this to bob")));
                Check(String.Join(",", (string[])redirected["recipients"]) == "bob", "An explicit to still redirects a reply");

                // An observer cannot post, so an ask addressed to one could never be answered.
                Error(f.Call("coordinator", "post", Json.Obj("kind", "request", "task_id", task, "to", new[] { "observer" }, "body", "Please review")), "invalid_recipient");
                Check((string)Result(f.Call("coordinator", "post", Json.Obj("task_id", task, "to", new[] { "observer" }, "body", "For your information")))["visibility"] == "published", "A note may still address an observer");
                Result(f.Call("alice", "post", Json.Obj("body", "Optional fields sent as null", "reply_to", null, "task_id", null, "to", null, "task_refs", null)));

                Check((string)Result(f.Call("moderator", "moderate", Json.Obj("action", "warn", "target", "@Alice", "reason", "Handles are normalized")))["target"] == "alice", "A moderation target accepts @ and any letter case");
                Error(f.Call("moderator", "moderate", Json.Obj("action", "pause", "target", "alice", "reason", "Meant one agent")), "invalid_argument");
                Check(!(bool)Result(f.Call("moderator", "status"))["paused"], "A pause that names a target does not pause the whole room");

                Result(f.Call("coordinator", "post", Json.Obj("task_id", other, "body", "On the other ticket")));
                var filtered = Rows(f.Call("observer", "feed", Json.Obj("after", 0, "task_id", other)));
                Check(filtered.Count == 1 && Convert.ToInt64(filtered[0]["task_id"]) == other, "A forward feed keeps its ticket filter");

                string wide = new string('<', 8000);
                try { Wire.Call(f.Profiles["coordinator"], "coordinator-test", "task.create", Json.Obj("title", "Wide", "objective", wide, "acceptance", wide), "wide-1"); Check(false, "An oversized request was sent"); }
                catch (RoomError e) { Check(e.Code == "request_too_large", "A request too large for one frame fails with its cause, not as a lost connection"); }

                Result(f.Room.Admin("agent.revoke", Json.Obj("agent", "bob")));
                try { f.Room.Admin("agent.reset", Json.Obj("agent", "bob")); Check(false, "A deregistered agent was reset"); }
                catch (RoomError e) { Check(e.Code == "revoked", "A deregistered agent cannot be reset to not joined"); }
            }
        }

        private static void HeldReplyTest(string hold, string kind)
        {
            using (var f = new Fixture("held-reply-" + hold + "-" + kind))
            {
                long task = Convert.ToInt64(f.Ticket("Reply delivery", "alice")["id"]);
                long original = Convert.ToInt64(Result(f.Call("coordinator", "post", Json.Obj("kind", kind == "result" ? "request" : "question",
                    "task_id", task, "to", new[] { "alice" }, "body", "Please reply to this original")))["message_id"]);
                Result(f.Room.Admin("moderate", hold == "pause" ? Json.Obj("action", "pause", "reason", "Pause fixture") :
                    Json.Obj("action", "mute", "target", "alice", "reason", "Mute fixture")));
                long otherAsks = 0;
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

                Result(f.Room.Admin("moderate", hold == "pause" ? Json.Obj("action", "resume", "reason", "Resume fixture") :
                    Json.Obj("action", "unmute", "target", "alice", "reason", "Unmute fixture")));
                Check(Rows(f.Call("alice", "inbox")).Any(r => ((string)r["body"]).StartsWith("Hold cleared") && ((string)r["body"]).Contains("@alice: #" + heldId)),
                    "When the " + hold + " ends, the hub tells the author which held post to send again");
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

        // The budget meters new asks. Replies to them, and splitting work into subtasks, are not what exhausts it;
        // addressed notes, which the budget does not count, have an allowance of their own.
        private static void BudgetTests()
        {
            using (var f = new Fixture("budget"))
            {
                long root = Convert.ToInt64(f.Ticket("Budget", "alice")["id"]);
                Func<long> remaining = () => Convert.ToInt64(Result(f.Call("coordinator", "task", Json.Obj("task_id", root)))["root_remaining"]);
                Func<string, Dictionary<string, object>> ask = body => Result(f.Call("coordinator", "post", Json.Obj("kind", "request", "task_id", root, "to", new[] { "alice" }, "body", body)));
                long first = Convert.ToInt64(ask("Request 0")["message_id"]);
                for (int i = 1; i < 8; i++) ask("Request " + i);
                Check(remaining() == 0, "Eight requests spend the root's deliveries");
                var result = Result(f.Call("alice", "post", Json.Obj("kind", "result", "reply_to", first, "status", "done", "body", "Done")));
                Check((string)result["visibility"] == "published" && remaining() == 0 && Rows(f.Call("coordinator", "inbox")).Any(r => Convert.ToInt64(r["id"]) == Convert.ToInt64(result["message_id"])),
                    "A result to an open request is delivered on a spent budget and costs nothing");
                var held = ask("Ninth request");
                Check((string)held["visibility"] == "held" && (string)held["reason"] == "Root delivery budget exhausted", "A new ask is held once the budget is spent");
                f.Ticket("Subtask", "bob", root);
                Check(remaining() == RoomService.SubtaskDeliveries, "A subtask adds deliveries to its root");
                Result(f.Call("moderator", "budget.reset", Json.Obj("task_id", root, "reason", "Productive work")));
                Check(Rows(f.Call("coordinator", "inbox")).Any(r => ((string)r["body"]).StartsWith("Hold cleared") && ((string)r["body"]).Contains("@coordinator: #" + held["message_id"])),
                    "A budget reset tells the author which held post to send again");
                Check(Rows(f.Call("coordinator", "inbox")).Count(r => ((string)r["body"]).StartsWith("Hold cleared")) == 1 && remaining() == 8, "A hold is announced once");
                Result(f.Call("moderator", "budget.reset", Json.Obj("task_id", root, "reason", "Second reset")));
                Check(Rows(f.Call("coordinator", "inbox")).Count(r => ((string)r["body"]).StartsWith("Hold cleared")) == 1, "A later reset does not announce the same held post again");

                for (int i = 0; i < RoomService.WorkerNotes; i++) Result(f.Call("alice", "post", Json.Obj("to", new[] { "bob" }, "body", "Note " + i)));
                Error(f.Call("alice", "post", Json.Obj("to", new[] { "bob" }, "body", "One note too many")), "rate_limited");
                Result(f.Call("alice", "post", Json.Obj("to", new[] { "coordinator" }, "body", "The allowance is per recipient")));
                f.Now += RoomService.NoteWindowSeconds + 1;
                Result(f.Call("alice", "post", Json.Obj("to", new[] { "bob" }, "body", "Allowed again after the window")));
            }
        }

        private static void LedgerStatusAndArgumentTests()
        {
            using (var f = new Fixture("ledger"))
            using (var server = new RoomServer(f.Room))
            {
                server.Start();
                // A request ID replays its result for a day; after that the ID is free and the operation really runs.
                var pause = Json.Obj("action", "pause", "reason", "Loop between workers");
                Result(f.Call("moderator", "moderate", pause, "mod-1"));
                Result(f.Call("moderator", "moderate", Json.Obj("action", "resume", "reason", "Settled"), "mod-2"));
                Result(f.Call("moderator", "moderate", pause, "mod-1"));
                Check(!(bool)Result(f.Call("moderator", "status"))["paused"], "Within a day a repeated request ID replays its recorded result");
                f.Now += RoomService.ReplaySeconds + 1;
                Result(f.Call("moderator", "moderate", pause, "mod-1"));
                Check((bool)Result(f.Call("moderator", "status"))["paused"], "After a day the ID is free again, so a reused ID is not a silent no-op");
                Result(f.Call("moderator", "moderate", Json.Obj("action", "resume", "reason", "Done"), "mod-3"));

                // Status follows tickets, so agents do not spend calls reporting it.
                long ticket = Convert.ToInt64(f.Ticket("Status", "alice")["id"]);
                Func<string> status = () => (string)Rows(f.Call("coordinator", "members")).Single(r => (string)r["id"] == "alice")["status"];
                Result(f.Call("alice", "task.claim", Json.Obj("task_id", ticket, "version", 1)));
                Check(status() == "working", "Claiming a ticket marks its owner working");
                Result(f.Call("alice", "task.update", Json.Obj("task_id", ticket, "version", 2, "state", "review", "checkpoint", "Ready")));
                Check(status() == "available", "Handing a ticket to review marks its owner available");
                Result(f.Call("alice", "task.update", Json.Obj("task_id", ticket, "version", 3, "state", "active", "checkpoint", "Follow-up")));
                Check(status() == "working", "Reactivating the ticket marks its owner working again");
                Result(f.Call("alice", "heartbeat", Json.Obj("status", "quota exhausted")));
                Result(f.Call("coordinator", "task.update", Json.Obj("task_id", ticket, "version", 4, "state", "done", "checkpoint", "Closed")));
                Check(status() == "quota exhausted", "A status the agent set for another reason is left alone");

                // A misspelled argument is reported instead of ignored.
                var misnamed = f.Call("alice", "post", Json.Obj("body", "Meant for the ticket", "ticket", ticket));
                Error(misnamed, "unknown_argument");
                Check(((string)((Dictionary<string, object>)misnamed["error"])["message"]).Contains("task_id"), "The error names the arguments the operation accepts");

                // The room's idle wait is the default for waits that name no timeout, as far as the connection allows.
                Check(Convert.ToInt64(Result(f.Call("alice", "status"))["wait_seconds"]) == RoomService.DefaultWaitSeconds, "Status carries the room's idle wait");
                Error(f.Call("alice", "room.wait", Json.Obj("seconds", 7200)), "forbidden");
                Result(f.Room.Admin("room.wait", Json.Obj("seconds", 7200)));
                var room = Result(f.Call("alice", "status"));
                Check(Waiter.Timeout(Json.Obj(), room, Waiter.LongMaximum) == 7200 && Waiter.Timeout(Json.Obj(), room, Waiter.ShortMaximum) == Waiter.ShortMaximum &&
                    Waiter.Timeout(Json.Obj("timeout_seconds", 30), room, Waiter.LongMaximum) == 30, "A wait uses the room's idle wait unless it names a timeout, within its connection's limit");
                try { Waiter.Run(f.Profiles["alice"], "alice-test", Json.Obj("timeout", 5), CancellationToken.None); Check(false, "A misnamed wait argument was accepted"); }
                catch (RoomError e) { Check(e.Code == "unknown_argument", "wait rejects a misnamed argument"); }

                // The next wait can acknowledge the notes from the last one, saving a call each.
                long note = Convert.ToInt64(Result(f.Call("coordinator", "post", Json.Obj("to", new[] { "alice" }, "body", "For your information")))["message_id"]);
                Result(Waiter.Run(f.Profiles["alice"], "alice-test", Json.Obj("timeout_seconds", 30, "settle_seconds", 0), CancellationToken.None));
                var next = Result(Waiter.Run(f.Profiles["alice"], "alice-test", Json.Obj("timeout_seconds", 1, "ack", new[] { note.ToString() }), CancellationToken.None));
                Check(Convert.ToInt64(next["unread"]) == 0 && !next.ContainsKey("ack_failed") && Rows(f.Call("alice", "inbox")).Count == 0, "wait acknowledges the listed messages before it waits");

                f.Room.Log("Diagnostics check");
                Check(File.ReadAllText(Path.Combine(f.Directory, "diagnostics.log")).Contains("Diagnostics check"), "Diagnostics are written to the room's data folder");
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
