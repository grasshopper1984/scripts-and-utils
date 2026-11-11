using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace AgentRoom
{
    // Blocks inside the connector until room mail arrives, so a waiting agent spends no model tokens.
    // The hub sees only ordinary short reads; nothing here runs a model or holds a hub connection open.
    internal static class Waiter
    {
        // Every timeout costs the waiting agent a model call, so a wait should last as long as its harness allows.
        // A command line wait can outlast anything. An MCP wait may exceed ShortMaximum only while the client is
        // being sent progress, because Claude Code aborts a stdio tool call that stays silent for 30 minutes.
        internal const int ShortMaximum = 1680, LongMaximum = (int)RoomService.LongestWaitSeconds;
        private const int PollMilliseconds = 2000, MaximumSettle = 60, BeatSeconds = 60;
        private static readonly string[] Transient = { "hub_unavailable", "connection_lost", "timeout" }, Arguments = { "scope", "after", "timeout_seconds", "settle_seconds", "ack" };
        private sealed class Hub
        {
            // Whether the last attempt to reach the hub failed, so a wait can tell a quiet room from a missing hub.
            internal bool Unreachable;
            internal readonly List<string> AckFailed = new List<string>();
            internal Action Beat;
            private readonly Stopwatch sinceBeat = Stopwatch.StartNew();
            internal void Tick()
            {
                if (Beat == null || sinceBeat.Elapsed.TotalSeconds < BeatSeconds) return;
                sinceBeat.Restart();
                try { Beat(); } catch (Exception) { /* A progress beat is a courtesy to the client. */ }
            }
        }

        internal static Dictionary<string, object> Run(Dictionary<string, object> profile, string session, Dictionary<string, object> args, CancellationToken cancel,
            int maximum = ShortMaximum, Action beat = null)
        {
            string unknown = args.Keys.FirstOrDefault(key => !Arguments.Contains(key));
            if (unknown != null) throw new RoomError("unknown_argument", "wait has no argument named " + unknown + ". It takes: " + String.Join(", ", Arguments) + ".");
            string scope = Json.Text(args, "scope", "inbox", 10);
            if (scope != "inbox" && scope != "room") throw new RoomError("invalid_argument", "scope must be inbox or room.");
            bool inbox = scope == "inbox";
            var acknowledge = new List<long>();
            foreach (string item in Json.Strings(args, "ack", 20))
            {
                long message;
                if (!Int64.TryParse(item, out message) || message <= 0) throw new RoomError("invalid_argument", "ack must contain message IDs as strings.");
                acknowledge.Add(message);
            }
            long timeout = Timeout(args, null, maximum);
            long settle = Math.Max(0, Math.Min(MaximumSettle, Json.Number(args, "settle_seconds", 10)));
            var clock = Stopwatch.StartNew();
            var hub = new Hub { Beat = beat };
            long after = 0; string self = "";
            // Both scopes start from the hub: the room cursor, or the newest inbox mail an earlier wait already reported.
            var status = Poll(profile, session, "status", Json.Obj(), clock, timeout, cancel, hub);
            if (status == null) return Finish(profile, session, hub, new object[0], after, !cancel.IsCancellationRequested, cancel.IsCancellationRequested);
            if (!(bool)status["ok"]) return status;
            var room = (Dictionary<string, object>)status["result"];
            self = (string)room["self"];
            timeout = Timeout(args, room, maximum);
            // Acknowledging the previous batch here saves the caller one call per note.
            foreach (long message in acknowledge)
            {
                try { if (!(bool)Wire.Call(profile, session, "ack", Json.Obj("message_id", message), Guid.NewGuid().ToString("N"))["ok"]) hub.AckFailed.Add(message.ToString()); }
                catch (RoomError) { hub.AckFailed.Add(message.ToString()); }
            }
            // An inbox wait wakes only for mail no earlier wait reported. Unread mail it already reported, such as a
            // request whose reply was held, must not return at once on every call; it comes back as a reminder on timeout.
            if (inbox) after = Json.Number(room, "inbox_seen");
            else after = args.ContainsKey("after") ? Json.Number(args, "after") : Convert.ToInt64(room["latest_message"]);
            // Room scope skips the listener's own posts but still moves the cursor past them.
            Func<Dictionary<string, object>> read = () =>
            {
                var response = Poll(profile, session, inbox ? "inbox" : "feed", Json.Obj("after", after, "limit", 20), clock, timeout, cancel, hub);
                if (response == null || !(bool)response["ok"] || inbox) return response;
                var all = (object[])response["result"];
                if (all.Length != 0) after = Math.Max(after, all.Max(r => Convert.ToInt64(((Dictionary<string, object>)r)["id"])));
                response["result"] = all.Where(r => (string)((Dictionary<string, object>)r)["author"] != self).ToArray();
                return response;
            };
            object[] rows = new object[0];
            bool more = false;
            // Nothing new arrived in time. Mail an earlier wait reported and that is still unread rides along as a reminder.
            Func<Dictionary<string, object>> expired = () =>
                Finish(profile, session, hub, inbox && !hub.Unreachable ? Pending(profile, session) : new object[0], after, true, false);
            while (true)
            {
                var response = read();
                // A read gives up when the wait is cancelled or its deadline passes, which usually happens between polls.
                if (response == null) return cancel.IsCancellationRequested ? Finish(profile, session, hub, new object[0], after, false, true) : expired();
                if (!(bool)response["ok"]) return response;
                rows = rows.Concat((object[])response["result"]).ToArray();
                more = More(response);
                if (rows.Length != 0)
                {
                    // Let a burst finish so the agent takes one turn for it instead of several.
                    var quiet = Stopwatch.StartNew(); var burst = Stopwatch.StartNew();
                    while (!more && rows.Length < 20 && quiet.Elapsed.TotalSeconds < settle && burst.Elapsed.TotalSeconds < MaximumSettle && clock.Elapsed.TotalSeconds < timeout)
                    {
                        if (cancel.WaitHandle.WaitOne(PollMilliseconds)) break;
                        var next = read();
                        if (next == null || !(bool)next["ok"]) break;
                        // The inbox re-reads every new unread receipt; the room feed returns only messages after the cursor.
                        var combined = inbox ? (object[])next["result"] : rows.Concat((object[])next["result"]).ToArray();
                        // A settling burst must also fit one response: leave the rest for the next wait by rewinding the cursor.
                        if (!inbox && Encoding.UTF8.GetByteCount(Json.Write(combined)) > RoomService.ResponseBudgetBytes)
                        {
                            after = rows.Max(r => Convert.ToInt64(((Dictionary<string, object>)r)["id"]));
                            more = true;
                            break;
                        }
                        more = More(next);
                        if (Signature(combined) != Signature(rows)) { rows = combined; quiet.Restart(); }
                    }
                    if (inbox)
                    {
                        // A withdrawn or replaced inbox wait reports nothing and records nothing, so the next wait still gets this mail.
                        if (cancel.IsCancellationRequested) return Finish(profile, session, hub, new object[0], after, false, true);
                        MarkSeen(profile, session, rows);
                    }
                    return Finish(profile, session, hub, rows, after, false, false, more);
                }
                if (clock.Elapsed.TotalSeconds >= timeout) return expired();
                hub.Tick();
                if (cancel.WaitHandle.WaitOne(PollMilliseconds)) return Finish(profile, session, hub, new object[0], after, false, true);
            }
        }
        // A caller that names no timeout gets the room's configured idle wait, as far as its connection allows.
        internal static long Timeout(Dictionary<string, object> args, Dictionary<string, object> room, int maximum)
        {
            bool named = args.ContainsKey("timeout_seconds") && args["timeout_seconds"] != null;
            long seconds = named ? Json.Number(args, "timeout_seconds") : room == null ? RoomService.DefaultWaitSeconds : Json.Number(room, "wait_seconds", RoomService.DefaultWaitSeconds);
            return Math.Max(1, Math.Min(maximum, seconds));
        }
        // Retries transient transport failures until the deadline; returns null on timeout or cancellation.
        private static Dictionary<string, object> Poll(Dictionary<string, object> profile, string session, string cmd, Dictionary<string, object> args,
            Stopwatch clock, long timeout, CancellationToken cancel, Hub hub)
        {
            while (!cancel.IsCancellationRequested && clock.Elapsed.TotalSeconds < timeout)
            {
                try { var response = Wire.Call(profile, session, cmd, args, ""); hub.Unreachable = false; return response; }
                catch (RoomError e) { if (!Transient.Contains(e.Code)) throw; hub.Unreachable = true; }
                hub.Tick();
                double remaining = timeout - clock.Elapsed.TotalSeconds;
                if (remaining <= 0 || cancel.WaitHandle.WaitOne((int)Math.Min(5000, remaining * 1000 + 1))) break;
            }
            return null;
        }
        // Records the newest mail this wait reports, so the next wait wakes only for newer mail. Best effort:
        // if the record is lost, the next wait reports the same mail again instead of missing any.
        private static void MarkSeen(Dictionary<string, object> profile, string session, object[] rows)
        {
            if (rows.Length == 0) return;
            try { Wire.Call(profile, session, "inbox.seen", Json.Obj("message_id", rows.Max(r => Convert.ToInt64(((Dictionary<string, object>)r)["id"]))), Guid.NewGuid().ToString("N")); }
            catch (RoomError) { }
        }
        // The oldest unread mail, whether or not an earlier wait reported it.
        private static object[] Pending(Dictionary<string, object> profile, string session)
        {
            try
            {
                var response = Wire.Call(profile, session, "inbox", Json.Obj("limit", 20), "");
                return (bool)response["ok"] ? (object[])response["result"] : new object[0];
            }
            catch (RoomError) { return new object[0]; }
        }
        private static bool More(Dictionary<string, object> response) { object value; return response.TryGetValue("more", out value) && value is bool && (bool)value; }
        private static string Signature(object[] rows) { return String.Join(",", rows.Select(r => Convert.ToString(((Dictionary<string, object>)r)["id"]))); }
        private static Dictionary<string, object> Finish(Dictionary<string, object> profile, string session, Hub hub, object[] rows, long after, bool timedOut, bool cancelled, bool more = false)
        {
            long cursor = rows.Length == 0 ? after : Math.Max(after, rows.Max(r => Convert.ToInt64(((Dictionary<string, object>)r)["id"])));
            var result = Json.Obj("messages", rows, "cursor", cursor, "more", more, "timed_out", timedOut, "cancelled", cancelled);
            if (hub.AckFailed.Count != 0) result["ack_failed"] = hub.AckFailed.ToArray();
            if (!cancelled)
            {
                // Pause and restriction state ride along so the agent can see why work may be held.
                try
                {
                    var status = Wire.Call(profile, session, "status", Json.Obj(), "");
                    hub.Unreachable = false;
                    if ((bool)status["ok"])
                    {
                        var room = (Dictionary<string, object>)status["result"];
                        if (room.ContainsKey("unread")) result["unread"] = room["unread"];
                        result["room"] = Json.Obj("paused", room["paused"], "restriction", room["restriction"], "briefing_version", room["briefing_version"]);
                    }
                }
                catch (RoomError e) { if (Transient.Contains(e.Code)) hub.Unreachable = true; }
                // Without this, a hub that is not running looks exactly like a quiet room.
                if (hub.Unreachable) result["hub_unreachable"] = true;
            }
            return Json.Ok(result);
        }
    }
}
