using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AgentRoom
{
    // Keeps one agent read small enough for every harness's tool-output limit. Lists stop at the budget and say
    // how to continue, so nothing is dropped silently. The OP's viewer reads through Admin and stays uncapped.
    internal sealed partial class RoomService
    {
        internal const int ResponseBudgetBytes = 24 * 1024, PreviewChars = 2000, MaxBodyChars = 4000;
        private const int EnvelopeBytes = 512, InstructionPreviewChars = 500;

        private Dictionary<string, object> Bounded(string cmd, object result, Dictionary<string, object> args)
        {
            var response = Json.Ok(result);
            var rows = result as List<Dictionary<string, object>>;
            long limit = Math.Max(1, Math.Min(cmd == "threads" ? 50 : 20, Json.Number(args, "limit", cmd == "threads" ? 50 : 20)));
            if (cmd == "inbox" || cmd == "feed") { foreach (var row in rows) Preview(row, "body", PreviewChars); ContinueList(response, rows, limit, cmd == "inbox" || args.ContainsKey("after") ? "after" : "before", "id"); }
            else if (cmd == "tasks")
            {
                foreach (var row in rows) foreach (string field in new[] { "objective", "acceptance", "checkpoint" }) Preview(row, field, PreviewChars);
                ContinueList(response, rows, limit, "before", "id");
            }
            else if (cmd == "artifacts") { foreach (var row in rows) Preview(row, "description", PreviewChars); ContinueList(response, rows, limit, "before", "id"); }
            else if (cmd == "audit" || cmd == "escalations") ContinueList(response, rows, limit, "before", "id");
            else if (cmd == "threads") ContinueList(response, rows, limit, "before", "last_id");
            else if (cmd == "members") { foreach (var row in rows) Preview(row, "instructions", InstructionPreviewChars); }
            else if (cmd == "thread") BoundThread(response, (Dictionary<string, object>)result);
            else if (cmd == "task") BoundTask((Dictionary<string, object>)result);
            else if (cmd == "message") { foreach (var row in (List<Dictionary<string, object>>)((Dictionary<string, object>)result)["artifacts"]) Preview(row, "description", PreviewChars); }
            return response;
        }
        // Keeps the first rows that fit, always at least one, and reports where the next page starts.
        private static void ContinueList(Dictionary<string, object> response, List<Dictionary<string, object>> rows, long limit, string direction, string key)
        {
            int kept = FittingRows(rows);
            bool more = kept < rows.Count || rows.Count >= limit;
            if (kept < rows.Count) rows.RemoveRange(kept, rows.Count - kept);
            if (!more || rows.Count == 0) return;
            response["more"] = true;
            response["next"] = Json.Obj(direction, rows[rows.Count - 1][key]);
        }
        private static void BoundThread(Dictionary<string, object> response, Dictionary<string, object> thread)
        {
            var rows = (List<Dictionary<string, object>>)thread["messages"];
            foreach (var row in rows) Preview(row, "body", PreviewChars);
            // A thread page is oldest first but holds the newest messages, so the oldest rows give way.
            rows.Reverse();
            int kept = FittingRows(rows);
            if (kept < rows.Count) { rows.RemoveRange(kept, rows.Count - kept); thread["older"] = true; }
            rows.Reverse();
            if ((bool)thread["older"] && rows.Count != 0) { response["more"] = true; response["next"] = Json.Obj("before", rows[0]["id"]); }
        }
        private static void BoundTask(Dictionary<string, object> task)
        {
            var messages = (List<Dictionary<string, object>>)task["messages"];
            var artifacts = (List<Dictionary<string, object>>)task["artifacts"];
            foreach (var row in messages) Preview(row, "body", PreviewChars);
            foreach (var row in artifacts) Preview(row, "description", PreviewChars);
            // The ticket's own fields are always whole; its recent messages use what is left of the budget.
            int room = ResponseBudgetBytes - ByteSize(task.Where(pair => pair.Key != "messages").ToDictionary(pair => pair.Key, pair => pair.Value));
            int kept = room <= EnvelopeBytes ? 0 : FittingRows(messages, room);
            if (kept >= messages.Count) return;
            messages.RemoveRange(kept, messages.Count - kept);
            task["messages_more"] = true;
        }
        private static int FittingRows(List<Dictionary<string, object>> rows) { return FittingRows(rows, ResponseBudgetBytes); }
        private static int FittingRows(List<Dictionary<string, object>> rows, int budget)
        {
            int used = EnvelopeBytes, kept = 0;
            foreach (var row in rows)
            {
                used += ByteSize(row) + 1;
                if (used > budget && kept != 0) break;
                kept++;
            }
            return kept;
        }
        private static int ByteSize(object value) { return Encoding.UTF8.GetByteCount(Json.Write(value)); }
        // Long text is cut in lists; the single-record reads (message, task) return it whole.
        private static void Preview(Dictionary<string, object> row, string field, int chars)
        {
            object value;
            if (!row.TryGetValue(field, out value)) return;
            var text = value as string;
            if (text == null || text.Length <= chars) return;
            int length = Char.IsHighSurrogate(text[chars - 1]) ? chars - 1 : chars;
            row[field] = text.Substring(0, length);
            row[field + "_truncated"] = true;
            row[field + "_length"] = text.Length;
        }
        // A join already carries the full text in its context, so the briefing itself need not repeat it.
        internal static Dictionary<string, object> BriefingSummary(Dictionary<string, object> briefing)
        {
            return Json.Obj("role", briefing["role"], "version", briefing["version"], "source", briefing["source"]);
        }
    }
}
