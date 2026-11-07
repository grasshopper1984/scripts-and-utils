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
        private const int PollMilliseconds = 2000, MaximumTimeout = 1680, MaximumSettle = 60;
        private static readonly string[] Transient = { "hub_unavailable", "connection_lost", "timeout" };

        internal static Dictionary<string, object> Run(Dictionary<string, object> profile, string session, Dictionary<string, object> args, CancellationToken cancel)
        {
            string scope = Json.Text(args, "scope", "inbox", 10);
            if (scope != "inbox" && scope != "room") throw new RoomError("invalid_argument", "scope must be inbox or room.");
            // The default stays under Claude Code's 30-minute MCP idle timeout.
            long timeout = Math.Max(1, Math.Min(MaximumTimeout, Json.Number(args, "timeout_seconds", 1500)));
            long settle = Math.Max(0, Math.Min(MaximumSettle, Json.Number(args, "settle_seconds", 10)));
            var clock = Stopwatch.StartNew();
            long after = 0; string self = "";
            if (scope == "room")
            {
                var status = Poll(profile, session, "status", Json.Obj(), clock, timeout, cancel);
                if (status == null) return Finish(profile, session, new object[0], after, !cancel.IsCancellationRequested, cancel.IsCancellationRequested);
                if (!(bool)status["ok"]) return status;
                var room = (Dictionary<string, object>)status["result"];
                self = (string)room["self"];
                after = args.ContainsKey("after") ? Json.Number(args, "after") : Convert.ToInt64(room["latest_message"]);
            }
            // Room scope skips the listener's own posts but still moves the cursor past them.
            Func<Dictionary<string, object>> read = () =>
            {
                var response = Poll(profile, session, scope == "inbox" ? "inbox" : "feed",
                    scope == "inbox" ? Json.Obj("limit", 20) : Json.Obj("after", after, "limit", 20), clock, timeout, cancel);
                if (response == null || !(bool)response["ok"] || scope == "inbox") return response;
                var all = (object[])response["result"];
                if (all.Length != 0) after = Math.Max(after, all.Max(r => Convert.ToInt64(((Dictionary<string, object>)r)["id"])));
                response["result"] = all.Where(r => (string)((Dictionary<string, object>)r)["author"] != self).ToArray();
                return response;
            };
            object[] rows = new object[0];
            bool more = false;
            while (true)
            {
                var response = read();
                if (response == null) return Finish(profile, session, new object[0], after, !cancel.IsCancellationRequested, cancel.IsCancellationRequested);
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
                        // The inbox re-reads every unread receipt; the room feed returns only messages after the cursor.
                        var combined = scope == "inbox" ? (object[])next["result"] : rows.Concat((object[])next["result"]).ToArray();
                        // A settling burst must also fit one response: leave the rest for the next wait by rewinding the cursor.
                        if (scope == "room" && Encoding.UTF8.GetByteCount(Json.Write(combined)) > RoomService.ResponseBudgetBytes)
                        {
                            after = rows.Max(r => Convert.ToInt64(((Dictionary<string, object>)r)["id"]));
                            more = true;
                            break;
                        }
                        more = More(next);
                        if (Signature(combined) != Signature(rows)) { rows = combined; quiet.Restart(); }
                    }
                    return Finish(profile, session, rows, after, false, false, more);
                }
                if (clock.Elapsed.TotalSeconds >= timeout) return Finish(profile, session, rows, after, true, false);
                if (cancel.WaitHandle.WaitOne(PollMilliseconds)) return Finish(profile, session, new object[0], after, false, true);
            }
        }
        // Retries transient transport failures until the deadline; returns null on timeout or cancellation.
        private static Dictionary<string, object> Poll(Dictionary<string, object> profile, string session, string cmd, Dictionary<string, object> args,
            Stopwatch clock, long timeout, CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested && clock.Elapsed.TotalSeconds < timeout)
            {
                try { return Wire.Call(profile, session, cmd, args, ""); }
                catch (RoomError e) { if (!Transient.Contains(e.Code)) throw; }
                if (cancel.WaitHandle.WaitOne(5000)) break;
            }
            return null;
        }
        private static bool More(Dictionary<string, object> response) { object value; return response.TryGetValue("more", out value) && value is bool && (bool)value; }
        private static string Signature(object[] rows) { return String.Join(",", rows.Select(r => Convert.ToString(((Dictionary<string, object>)r)["id"]))); }
        private static Dictionary<string, object> Finish(Dictionary<string, object> profile, string session, object[] rows, long after, bool timedOut, bool cancelled, bool more = false)
        {
            long cursor = rows.Length == 0 ? after : Math.Max(after, rows.Max(r => Convert.ToInt64(((Dictionary<string, object>)r)["id"])));
            var result = Json.Obj("messages", rows, "cursor", cursor, "more", more, "timed_out", timedOut, "cancelled", cancelled);
            if (!cancelled)
            {
                // Pause and restriction state ride along so the agent can see why work may be held.
                try
                {
                    var status = Wire.Call(profile, session, "status", Json.Obj(), "");
                    if ((bool)status["ok"])
                    {
                        var room = (Dictionary<string, object>)status["result"];
                        result["room"] = Json.Obj("paused", room["paused"], "restriction", room["restriction"], "briefing_version", room["briefing_version"]);
                    }
                }
                catch (RoomError) { }
            }
            return Json.Ok(result);
        }
    }
}
