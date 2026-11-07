using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentRoom
{
    // Each ticket keeps its own conversation thread; messages without a ticket form the room thread.
    // Threads keep task chatter out of the project graph, which shows only counts per node.
    internal sealed partial class RoomService
    {
        private const int ThreadPage = 50;
        // An ask stays open until a published result or answer reaches its recipients, or its ticket closes.
        internal const string OpenAsk = "m.visibility='published' AND m.kind IN ('request','question') AND NOT EXISTS (SELECT 1 FROM messages r WHERE r.reply_to=m.id AND r.kind IN ('result','answer') AND r.visibility='published') AND NOT EXISTS (SELECT 1 FROM tasks c WHERE c.id=m.task_id AND c.state IN ('done','cancelled'))";
        private const string Visible = "(m.visibility='published' OR m.author=? OR ?=1)";

        private object ReadThreads(Dictionary<string, object> actor, Dictionary<string, object> args)
        {
            long limit = Math.Max(1, Math.Min(ThreadPage, Json.Number(args, "limit", ThreadPage))), before = Json.Number(args, "before", Int64.MaxValue);
            bool supervision = Is(actor, "op", "moderator");
            var rows = db.Query("SELECT g.*,t.title,t.state,t.owner,(SELECT author FROM messages WHERE id=g.last_id) AS last_author FROM (" +
                "SELECT COALESCE(m.task_id,0) AS task_id,COUNT(*) AS messages,MAX(m.id) AS last_id,MAX(m.at) AS last_at," +
                "SUM(CASE WHEN " + OpenAsk + " THEN 1 ELSE 0 END) AS open_asks FROM messages m WHERE " + Visible + " GROUP BY COALESCE(m.task_id,0)) g " +
                "LEFT JOIN tasks t ON t.id=g.task_id WHERE g.last_id<? ORDER BY g.last_id DESC LIMIT ?", actor["id"], supervision ? 1 : 0, before, limit);
            foreach (var row in rows) if (Convert.ToInt64(row["task_id"]) == 0) row["title"] = "Room";
            return rows;
        }

        private object ReadThread(Dictionary<string, object> actor, Dictionary<string, object> args)
        {
            long taskId = Json.Number(args, "task_id"), limit = Math.Max(1, Math.Min(ThreadPage, Json.Number(args, "limit", ThreadPage)));
            long before = Json.Number(args, "before", Int64.MaxValue), after = Json.Number(args, "after");
            var task = taskId == 0 ? null : Task(taskId);
            bool supervision = Is(actor, "op", "moderator");
            // Newest page first, then returned oldest first so the thread reads top to bottom.
            var rows = db.Query("SELECT m.*,CASE WHEN " + OpenAsk + " THEN 1 ELSE 0 END AS open_ask FROM messages m WHERE (m.task_id=? OR (?=0 AND m.task_id IS NULL)) AND m.id<? AND m.id>? AND " + Visible +
                " ORDER BY m.id DESC LIMIT ?", taskId, taskId, before, after, actor["id"], supervision ? 1 : 0, limit);
            rows.Reverse();
            bool older = rows.Count != 0 && db.One("SELECT m.id FROM messages m WHERE (m.task_id=? OR (?=0 AND m.task_id IS NULL)) AND m.id<? AND " + Visible + " LIMIT 1",
                taskId, taskId, rows[0]["id"], actor["id"], supervision ? 1 : 0) != null;
            return Json.Obj("task", task == null ? null : Json.Obj("id", task["id"], "title", task["title"], "state", task["state"], "owner", task["owner"], "assigned", task["assigned"]),
                "messages", rows, "older", older);
        }

        private object AssignTask(Dictionary<string, object> actor, Dictionary<string, object> a, long now)
        {
            Require(actor, "op", "coordinator");
            string id = (string)actor["id"];
            RequireNotMuted(id, now);
            var task = TaskVersion(Json.Number(a, "task_id"), a);
            // Reassigning claimed work needs the operator-assisted handoff, because the previous writer may still be editing.
            if ((string)task["state"] != "open" || task["owner"] != null) throw new RoomError("conflict", "Only unclaimed open tickets can be reassigned.");
            string assignee = Json.Text(a, "assignee", "", 40).Trim().TrimStart('@').ToLowerInvariant();
            if (assignee.Length != 0) EligibleAgent(assignee);
            db.Execute("UPDATE tasks SET assigned=?,version=version+1,updated_at=? WHERE id=?", assignee.Length == 0 ? null : assignee, now, task["id"]);
            Audit(id, "task.assign", Json.Obj("task", task["id"], "assignee", assignee), now);
            return Task(Convert.ToInt64(task["id"]));
        }
    }
}
