using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AgentRoom
{
    // Message protocol, escalations to the OP, moderator budget resets, and chat resets.
    // PROTOCOL.md is the plain-language record of the rules enforced here.
    internal sealed partial class RoomService
    {
        internal static readonly string[] MessageKinds = { "note", "request", "question", "result", "answer" };
        internal static readonly string[] ResultStatuses = { "done", "partial", "blocked", "declined" };
        private static readonly string[] Severities = { "info", "warning", "urgent" };
        private const int ModeratorResetsPerDay = 2, EscalationsPerHour = 6, UrgentPerHour = 2;
        // Loop control. A subtask adds deliveries to its root; addressed notes have a per-recipient allowance.
        internal const int SubtaskDeliveries = 4, WorkerNotes = 6, SupervisorNotes = 20, NoteWindowSeconds = 1800, TicketsPerHour = 100;

        // When a hold clears, the hub tells each author which posts to send again. Otherwise they would learn it only
        // from a moderator's note or at their next wait timeout, and an expensive session would sit idle meanwhile.
        private void AnnounceCleared(string actor, long now, object taskId, string what, string filter, params object[] values)
        {
            var held = db.Query("SELECT m.id,m.author FROM messages m JOIN agents a ON a.id=m.author WHERE m.visibility='held' AND m.hold_cleared=0 AND a.revoked=0 AND " + filter + " ORDER BY m.id", values);
            if (held.Count == 0) return;
            foreach (var row in held) db.Execute("UPDATE messages SET hold_cleared=? WHERE id=?", now, row["id"]);
            var authors = held.GroupBy(r => (string)r["author"]).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            string body = "Hold cleared: " + what + ". Post these again with a new request ID; the old IDs stay held. " +
                String.Join("; ", authors.Select(g => "@" + g.Key + ": " + String.Join(", ", g.Select(r => "#" + r["id"]))));
            if (body.Length > MaxBodyChars) body = body.Substring(0, MaxBodyChars - 3) + "...";
            db.Execute("INSERT INTO messages(at,author,kind,body,task_id,visibility,recipients) VALUES(?,?,'note',?,?,'published',?)", now, actor, body, taskId, Json.Write(authors.Select(g => g.Key).ToArray()));
            long notice = db.LastId;
            if (taskId != null) db.Execute("INSERT INTO message_tasks(message_id,task_id) VALUES(?,?)", notice, taskId);
            foreach (var group in authors) if (group.Key != actor) db.Execute("INSERT INTO receipts(message_id,recipient) VALUES(?,?)", notice, group.Key);
        }

        private void MigrateCoordination()
        {
            db.Execute("ALTER TABLE messages ADD COLUMN reply_to INTEGER REFERENCES messages(id)");
            db.Execute("ALTER TABLE messages ADD COLUMN status TEXT NOT NULL DEFAULT ''");
            db.Execute("CREATE TABLE escalations(id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, author TEXT NOT NULL REFERENCES agents(id), severity TEXT NOT NULL, summary TEXT NOT NULL, details TEXT NOT NULL, task_id INTEGER REFERENCES tasks(id), evidence TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'open', handled_at INTEGER NOT NULL DEFAULT 0)");
            db.Execute("CREATE INDEX escalations_by_state ON escalations(state,id)");
        }

        // Resolves the reply fields of a post. Returns the message being answered, or null.
        private Dictionary<string, object> ReplyTarget(Dictionary<string, object> actor, string kind, Dictionary<string, object> a)
        {
            string status = Json.Text(a, "status", "", 20);
            if (kind == "result" && !ResultStatuses.Contains(status))
                throw new RoomError("invalid_argument", "A result needs status: done, partial, blocked, or declined.");
            if (kind != "result" && status.Length != 0) throw new RoomError("invalid_argument", "Only results carry a status.");
            long replyTo = Json.Number(a, "reply_to");
            if (kind == "answer" && replyTo == 0) throw new RoomError("invalid_argument", "An answer needs reply_to naming the question.");
            if (replyTo == 0) return null;
            var original = VisibleMessage(actor, replyTo);
            if ((string)original["visibility"] != "published") throw new RoomError("invalid_argument", "Reply only to published messages.");
            if (kind == "answer" && (string)original["kind"] != "question") throw new RoomError("invalid_argument", "An answer must reply to a question.");
            if (kind == "result" && (string)original["kind"] != "request") throw new RoomError("invalid_argument", "A result can reply only to a request.");
            if ((kind == "answer" || kind == "result") &&
                db.One("SELECT message_id FROM receipts WHERE message_id=? AND recipient=?", replyTo, actor["id"]) == null)
                throw new RoomError("forbidden", "Only a recipient of that message can answer it.");
            return original;
        }

        private object Escalate(Dictionary<string, object> actor, Dictionary<string, object> a, long now)
        {
            Require(actor, "moderator", "coordinator");
            string id = (string)actor["id"], severity = Json.Text(a, "severity", "warning", 10);
            if (!Severities.Contains(severity)) throw new RoomError("invalid_argument", "Severity must be info, warning, or urgent.");
            // Escalations stay available while muted, so an agent can still report abuse; rate limits keep them rare.
            long recent = Count("SELECT COUNT(*) AS n FROM escalations WHERE author=? AND at>?", id, now - 3600);
            long urgent = Count("SELECT COUNT(*) AS n FROM escalations WHERE author=? AND at>? AND severity='urgent'", id, now - 3600);
            if (recent >= EscalationsPerHour || severity == "urgent" && urgent >= UrgentPerHour)
                throw new RoomError("rate_limited", "Escalation limit reached for this hour. Add details to an open escalation instead.");
            long taskId = Json.Number(a, "task_id");
            if (taskId != 0) Task(taskId);
            string[] evidence = Json.Strings(a, "evidence", 10);
            foreach (string item in evidence)
            {
                long message;
                if (!Int64.TryParse(item, out message) || db.One("SELECT id FROM messages WHERE id=?", message) == null)
                    throw new RoomError("invalid_argument", "Evidence must reference existing message IDs.");
            }
            db.Execute("INSERT INTO escalations(at,author,severity,summary,details,task_id,evidence) VALUES(?,?,?,?,?,?,?)", now, id, severity,
                Json.Required(a, "summary", 160), Json.Text(a, "details", "", 4000), taskId == 0 ? null : (object)taskId, Json.Write(evidence));
            long escalation = db.LastId;
            Audit(id, "escalate", Json.Obj("escalation", escalation, "severity", severity), now);
            return db.One("SELECT * FROM escalations WHERE id=?", escalation);
        }

        private object HandleEscalation(Dictionary<string, object> actor, Dictionary<string, object> a, long now)
        {
            Require(actor, "op");
            long id = Json.Number(a, "escalation_id");
            string state = Json.Required(a, "state", 20);
            if (state != "acknowledged" && state != "resolved") throw new RoomError("invalid_argument", "State must be acknowledged or resolved.");
            var row = db.One("SELECT * FROM escalations WHERE id=?", id);
            if (row == null) throw new RoomError("not_found", "Escalation not found.");
            if ((string)row["state"] == "resolved") throw new RoomError("conflict", "That escalation is already resolved.");
            db.Execute("UPDATE escalations SET state=?,handled_at=? WHERE id=?", state, now, id);
            Audit((string)actor["id"], "escalation." + state, Json.Obj("escalation", id, "note", Json.Text(a, "note", "", 1000)), now);
            return db.One("SELECT * FROM escalations WHERE id=?", id);
        }

        private object ReadEscalations(Dictionary<string, object> actor, Dictionary<string, object> args)
        {
            Require(actor, "op", "moderator", "coordinator");
            string state = Json.Text(args, "state", "open", 20);
            if (state != "open" && state != "unresolved" && state != "all") throw new RoomError("invalid_argument", "State must be open, unresolved, or all.");
            long limit = Math.Max(1, Math.Min(20, Json.Number(args, "limit", 20))), before = Json.Number(args, "before", Int64.MaxValue);
            return db.Query("SELECT * FROM escalations WHERE id<? AND (?='all' OR state='open' OR (?='unresolved' AND state='acknowledged')) ORDER BY id DESC LIMIT ?",
                before, state, state, limit);
        }

        private object ResetBudget(Dictionary<string, object> actor, Dictionary<string, object> a, long now)
        {
            Require(actor, "op", "moderator");
            string id = (string)actor["id"];
            var task = Task(Json.Number(a, "task_id"));
            string reason = Json.Required(a, "reason", 1000);
            if (!Is(actor, "op"))
            {
                // The moderator may be part of the exchange that ran out, so its top-ups are capped before the OP must decide.
                RequireNotMuted(id, now);
                long used = Count("SELECT COUNT(*) AS n FROM audit WHERE action='budget.reset' AND actor=? AND at>? AND details LIKE ?",
                    id, now - 86400, "{\"root\":" + task["root_id"] + ",%");
                if (used >= ModeratorResetsPerDay)
                    throw new RoomError("budget_reset_limit", "The moderator already reset this conversation twice today. Escalate to the OP instead.");
            }
            db.Execute("UPDATE tasks SET remaining=8 WHERE id=?", task["root_id"]);
            Audit(id, "budget.reset", Json.Obj("root", task["root_id"], "reason", reason), now);
            AnnounceCleared(id, now, task["root_id"], "the delivery budget of ticket #" + task["root_id"] + " was reset",
                "m.held_reason='Root delivery budget exhausted' AND m.task_id IN (SELECT id FROM tasks WHERE root_id=?)", task["root_id"]);
            return Json.Obj("root", task["root_id"], "remaining", 8);
        }

        private object ResetChats(Dictionary<string, object> actor, Dictionary<string, object> a, long now)
        {
            Require(actor, "op");
            string reason = Json.Required(a, "reason", 1000);
            long messages = Count("SELECT COUNT(*) AS n FROM messages");
            // Links and receipts go first; message IDs keep counting up so old references never point at new messages.
            foreach (string table in new[] { "receipts", "message_tasks", "message_references", "message_artifacts" }) db.Execute("DELETE FROM " + table);
            db.Execute("DELETE FROM messages");
            db.Execute("UPDATE tasks SET remaining=8 WHERE id=root_id");
            Audit((string)actor["id"], "room.reset", Json.Obj("messages", messages, "reason", reason), now);
            return Json.Obj("cleared_messages", messages);
        }

        private long Count(string sql, params object[] values) { return Convert.ToInt64(db.One(sql, values)["n"]); }

        // A briefing is the role playbook followed by the message protocol. Files in the room's roles folder
        // replace the built-in copies, so a room can be tuned without rebuilding.
        internal static readonly string[] BriefedRoles = { "moderator", "coordinator", "worker", "observer" };
        internal static readonly string[] BriefingDocuments = { "moderator.md", "coordinator.md", "worker.md", "observer.md", "protocol.md" };
        internal string RolesDirectory { get { return Path.Combine(DirectoryPath, "roles"); } }
        private Dictionary<string, object> Briefing(string role)
        {
            if (!BriefedRoles.Contains(role)) throw new RoomError("invalid_argument", "Briefings exist for moderator, coordinator, worker, and observer.");
            bool custom = false;
            string playbook = Document(role + ".md", ref custom), protocol = Document("protocol.md", ref custom);
            string text = playbook.Replace("\r\n", "\n").TrimEnd() + "\n\n---\n\n" + protocol.Replace("\r\n", "\n").TrimEnd() + "\n";
            return Json.Obj("role", role, "version", Json.Hash(text).Substring(0, 12), "source", custom ? "room" : "built-in", "text", text);
        }
        private string Document(string name, ref bool custom)
        {
            string path = Path.Combine(RolesDirectory, name);
            if (!File.Exists(path)) return BuiltInDocument(name);
            if (new FileInfo(path).Length > 65536) throw new RoomError("briefing_too_large", "A custom playbook in the room's roles folder exceeds 64 KiB.");
            custom = true;
            return File.ReadAllText(path, Encoding.UTF8);
        }
        internal static string BuiltInDocument(string name)
        {
            using (var stream = typeof(RoomService).Assembly.GetManifestResourceStream("AgentRoom.Roles." + name))
            {
                if (stream == null) throw new RoomError("briefing_missing", "This build does not include the built-in " + name + ".");
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        private object ReadBriefing(Dictionary<string, object> actor, Dictionary<string, object> args)
        {
            // Agents always get their own role; the OP may preview any role.
            if (!Is(actor, "op")) { var own = Briefing((string)actor["permission"]); own["instructions"] = actor["instructions"]; return own; }
            return Briefing(Json.Required(args, "role", 20));
        }
        private object BriefingVersion(Dictionary<string, object> actor)
        {
            return BriefedRoles.Contains((string)actor["permission"]) ? Briefing((string)actor["permission"])["version"] : null;
        }
    }
}
