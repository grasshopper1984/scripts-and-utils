using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AgentRoom
{
    internal sealed partial class RoomService
    {
        private void MigrateProjectData()
        {
            string[] statements = {
                "ALTER TABLE tasks ADD COLUMN priority INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE tasks ADD COLUMN created_at INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE tasks ADD COLUMN updated_at INTEGER NOT NULL DEFAULT 0",
                "CREATE TABLE task_references(task_id INTEGER NOT NULL REFERENCES tasks(id),refers_to INTEGER NOT NULL REFERENCES tasks(id),PRIMARY KEY(task_id,refers_to))",
                "CREATE TABLE message_tasks(message_id INTEGER NOT NULL REFERENCES messages(id),task_id INTEGER NOT NULL REFERENCES tasks(id),PRIMARY KEY(message_id,task_id))",
                "CREATE INDEX messages_by_task ON message_tasks(task_id,message_id)",
                "CREATE TABLE message_references(message_id INTEGER NOT NULL REFERENCES messages(id),refers_to INTEGER NOT NULL REFERENCES messages(id),PRIMARY KEY(message_id,refers_to))",
                "CREATE TABLE artifacts(id INTEGER PRIMARY KEY AUTOINCREMENT,task_id INTEGER NOT NULL REFERENCES tasks(id),author TEXT NOT NULL REFERENCES agents(id),title TEXT NOT NULL,description TEXT NOT NULL,path TEXT NOT NULL,sha256 TEXT NOT NULL,size INTEGER NOT NULL,created_at INTEGER NOT NULL)",
                "CREATE INDEX artifacts_by_task ON artifacts(task_id,id)",
                "CREATE TABLE message_artifacts(message_id INTEGER NOT NULL REFERENCES messages(id),artifact_id INTEGER NOT NULL REFERENCES artifacts(id),PRIMARY KEY(message_id,artifact_id))",
                "CREATE INDEX task_filter ON tasks(state,priority,updated_at)",
                "CREATE INDEX dependencies_reverse ON dependencies(depends_on,task_id)",
                "CREATE INDEX references_reverse ON task_references(refers_to,task_id)",
                "INSERT INTO message_tasks(message_id,task_id) SELECT id,task_id FROM messages WHERE task_id IS NOT NULL"
            };
            foreach (string sql in statements) db.Execute(sql);
            // Recover historical dates from the existing audit, rather than dating old work to the upgrade.
            foreach (var entry in db.Query("SELECT at,action,details FROM audit WHERE action IN ('task.create','task.claim','task.update','task.depend','worktree.release') ORDER BY id"))
            {
                var details = Json.Read((string)entry["details"]);
                long task = Json.Number(details, "task");
                if ((string)entry["action"] == "task.create") db.Execute("UPDATE tasks SET created_at=? WHERE id=?", entry["at"], task);
                db.Execute("UPDATE tasks SET updated_at=MAX(updated_at,?) WHERE id=?", entry["at"], task);
            }
        }
        private static long Priority(Dictionary<string, object> args, string key)
        {
            long value = Json.Number(args, key);
            if (value < 0 || value > 2) throw new RoomError("invalid_argument", "Importance must be 0 (normal), 1 (high), or 2 (critical).");
            return value;
        }
        private static long[] Ids(Dictionary<string, object> args, string key, int maximum)
        {
            var values = new List<long>();
            foreach (string text in Json.Strings(args, key, maximum))
            {
                long value;
                if (!Int64.TryParse(text, out value) || value <= 0) throw new RoomError("invalid_argument", key + " must contain positive record IDs as strings.");
                values.Add(value);
            }
            return values.Distinct().ToArray();
        }
        private void CheckTaskCycle(long task, long predecessor)
        {
            if (task == predecessor || db.One("WITH RECURSIVE links(task_id,predecessor) AS (SELECT task_id,depends_on FROM dependencies UNION SELECT task_id,refers_to FROM task_references), ancestors(id) AS (SELECT predecessor FROM links WHERE task_id=? UNION SELECT l.predecessor FROM links l JOIN ancestors a ON l.task_id=a.id) SELECT id FROM ancestors WHERE id=? LIMIT 1", predecessor, task) != null)
                throw new RoomError("dependency_cycle", "That link would create a cycle in the task graph.");
        }
        private void AddTaskReference(long task, long reference)
        {
            Task(reference); CheckTaskCycle(task, reference);
            db.Execute("INSERT INTO task_references(task_id,refers_to) VALUES(?,?)", task, reference);
        }
        private void AddTaskLinks(Dictionary<string, object> row)
        {
            row["dependencies"] = db.Query("SELECT depends_on FROM dependencies WHERE task_id=? ORDER BY depends_on", row["id"]).Select(r => r["depends_on"]).ToArray();
            row["references"] = db.Query("SELECT refers_to FROM task_references WHERE task_id=? ORDER BY refers_to", row["id"]).Select(r => r["refers_to"]).ToArray();
        }
        private string TaskFilter(Dictionary<string, object> args, List<object> values)
        {
            string state = Json.Text(args, "state", "all", 20), agent = Json.Text(args, "agent", "", 40);
            long since = Json.Number(args, "since"), until = Json.Number(args, "until", Int64.MaxValue);
            if (since < 0 || until <= since) throw new RoomError("invalid_argument", "Choose a valid update-date interval.");
            string filter = "t.priority>=? AND t.updated_at>=? AND t.updated_at<?";
            values.Add(Priority(args, "min_priority")); values.Add(since); values.Add(until);
            if (state == "unfinished") filter += " AND t.state NOT IN ('done','cancelled')";
            else if (state != "all")
            {
                if (!new[] { "open", "active", "blocked", "paused", "review", "done", "cancelled" }.Contains(state)) throw new RoomError("invalid_argument", "Unknown task state filter.");
                filter += " AND t.state=?"; values.Add(state);
            }
            if (agent != "") { filter += " AND (t.owner=? OR t.assigned=?)"; values.Add(agent); values.Add(agent); }
            return filter;
        }
        private bool CanSeeMessage(Dictionary<string, object> actor, Dictionary<string, object> message)
        { return (string)message["visibility"] == "published" || (string)message["author"] == (string)actor["id"] || Is(actor, "op", "moderator"); }
        private Dictionary<string, object> VisibleMessage(Dictionary<string, object> actor, long id)
        {
            var message = db.One("SELECT * FROM messages WHERE id=?", id);
            if (message == null || !CanSeeMessage(actor, message)) throw new RoomError("not_found", "Message is unavailable.");
            return message;
        }
        private object ReadProject(Dictionary<string, object> actor, string operation, Dictionary<string, object> args)
        {
            long limit = Math.Max(1, Math.Min(20, Json.Number(args, "limit", 20))), before = Json.Number(args, "before", Int64.MaxValue);
            if (operation == "message")
            {
                var message = VisibleMessage(actor, Json.Number(args, "message_id"));
                message["receipts"] = db.Query("SELECT recipient,acknowledged FROM receipts WHERE message_id=? ORDER BY recipient", message["id"]);
                message["tasks"] = db.Query("SELECT t.id,t.title,t.state FROM tasks t JOIN message_tasks m ON m.task_id=t.id WHERE m.message_id=? ORDER BY t.id", message["id"]);
                message["artifacts"] = db.Query("SELECT a.* FROM artifacts a JOIN message_artifacts r ON r.artifact_id=a.id WHERE r.message_id=? ORDER BY a.id", message["id"]);
                var references = db.Query("SELECT m.id,m.author,m.visibility,m.body FROM messages m JOIN message_references r ON r.refers_to=m.id WHERE r.message_id=? ORDER BY m.id", message["id"]);
                message["references"] = references.Select(r => CanSeeMessage(actor, r) ? Json.Obj("id", r["id"], "author", r["author"], "visibility", r["visibility"], "preview", ((string)r["body"]).Substring(0, Math.Min(200, ((string)r["body"]).Length))) : Json.Obj("id", r["id"], "visibility", "unavailable")).ToList();
                return message;
            }
            if (operation == "agent.inbox")
            {
                Require(actor, "op");
                string agent = Json.Required(args, "agent", 40), mode = Json.Text(args, "receipts", "unread", 10);
                if (mode != "unread" && mode != "all") throw new RoomError("invalid_argument", "Inbox receipts must be unread or all.");
                return db.Query("SELECT m.*,r.acknowledged FROM messages m JOIN receipts r ON r.message_id=m.id WHERE r.recipient=? AND m.id<? AND (?='all' OR r.acknowledged IS NULL) ORDER BY m.id DESC LIMIT ?", agent, before, mode, limit);
            }
            if (operation == "task")
            {
                var task = Task(Json.Number(args, "task_id")); AddTaskLinks(task);
                task["root_remaining"] = Task(Convert.ToInt64(task["root_id"]))["remaining"];
                task["messages"] = db.Query("SELECT m.id,m.author,m.kind,m.body,m.visibility FROM messages m JOIN message_tasks r ON r.message_id=m.id WHERE r.task_id=? AND (m.visibility='published' OR m.author=? OR ?=1) ORDER BY m.id DESC LIMIT 20", task["id"], actor["id"], Is(actor, "op", "moderator") ? 1 : 0);
                task["artifacts"] = db.Query("SELECT * FROM artifacts WHERE task_id=? ORDER BY id DESC LIMIT 20", task["id"]);
                return task;
            }
            if (operation == "artifacts") return db.Query("SELECT * FROM artifacts WHERE id<? AND (?=0 OR task_id=?) ORDER BY id DESC LIMIT ?", before, Json.Number(args, "task_id"), Json.Number(args, "task_id"), limit);
            return Graph(args);
        }
        private object Graph(Dictionary<string, object> args)
        {
            var values = new List<object>();
            string filter = TaskFilter(args, values);
            long focus = Json.Number(args, "focus"), limit = Math.Max(1, Math.Min(60, Json.Number(args, "limit", 40)));
            if (focus != 0)
            {
                Task(focus);
                filter = "(t.id=? OR ((" + filter + ") AND t.id IN (SELECT depends_on FROM dependencies WHERE task_id=? UNION SELECT task_id FROM dependencies WHERE depends_on=? UNION SELECT refers_to FROM task_references WHERE task_id=? UNION SELECT task_id FROM task_references WHERE refers_to=?)))";
                values.Insert(0, focus); for (int i = 0; i < 4; i++) values.Add(focus);
            }
            long total = Convert.ToInt64(db.One("SELECT COUNT(*) AS n FROM tasks t WHERE " + filter, values.ToArray())["n"]);
            values.Add(focus); values.Add(limit);
            // Conversations stay inside their node: the graph carries only message and open-ask counts.
            var nodes = db.Query("SELECT t.id,t.title,t.state,t.owner,t.assigned,t.priority,t.created_at,t.updated_at," +
                "MAX(t.updated_at,COALESCE((SELECT MAX(m.at) FROM messages m WHERE m.task_id=t.id AND m.visibility='published'),0)) AS activity_at," +
                "(SELECT COUNT(*) FROM messages m WHERE m.task_id=t.id AND m.visibility='published') AS messages," +
                "(SELECT COUNT(*) FROM messages m WHERE m.task_id=t.id AND " + OpenAsk + ") AS open_asks FROM tasks t WHERE " + filter + " ORDER BY (t.id=?) DESC,activity_at DESC,t.priority DESC,t.id DESC LIMIT ?", values.ToArray());
            var edges = new List<Dictionary<string, object>>();
            if (nodes.Count != 0)
            {
                string placeholders = String.Join(",", nodes.Select(r => "?").ToArray());
                object[] ids = nodes.Select(r => r["id"]).ToArray();
                string links = "(SELECT depends_on AS source,task_id AS target,'dependency' AS kind FROM dependencies UNION ALL SELECT refers_to AS source,task_id AS target,'reference' AS kind FROM task_references)";
                edges = db.Query("SELECT * FROM " + links + " WHERE source IN (" + placeholders + ") AND target IN (" + placeholders + ") ORDER BY source,target,kind", ids.Concat(ids).ToArray());
                foreach (var node in nodes)
                {
                    var all = db.One("SELECT COUNT(*) AS n FROM " + links + " WHERE source=? OR target=?", node["id"], node["id"]);
                    node["hidden_links"] = Convert.ToInt64(all["n"]) - edges.Count(e => Convert.ToInt64(e["source"]) == Convert.ToInt64(node["id"]) || Convert.ToInt64(e["target"]) == Convert.ToInt64(node["id"]));
                }
            }
            return Json.Obj("nodes", nodes, "edges", edges, "total", total, "limit", limit, "focus", focus, "truncated", total > nodes.Count);
        }
        private void RequireArtifactWriter(Dictionary<string, object> actor, Dictionary<string, object> args, long now)
        {
            Require(actor, "op", "coordinator", "worker"); RequireNotMuted((string)actor["id"], now);
            var task = Task(Json.Number(args, "task_id"));
            if (!Is(actor, "op", "coordinator") && ((string)task["owner"] != (string)actor["id"] || (string)task["state"] == "done" || (string)task["state"] == "cancelled"))
                throw new RoomError("forbidden", "Deliver files only for your own ongoing ticket.");
        }
        private object MutateProject(Dictionary<string, object> actor, string operation, Dictionary<string, object> args, ArtifactFile artifact, string directory, long now)
        {
            string id = (string)actor["id"];
            if (operation == "artifact.directory")
            {
                Require(actor, "op"); SetSetting("artifact_directory", directory);
                Audit(id, operation, Json.Obj("path", directory), now); return Json.Obj("path", directory);
            }
            if (operation == "artifact.register")
            {
                RequireArtifactWriter(actor, args, now);
                if (!String.Equals(Setting("artifact_directory"), directory, StringComparison.OrdinalIgnoreCase)) throw new RoomError("conflict", "Artifact directory changed. Retry with a new request ID.");
                long task = Json.Number(args, "task_id");
                db.Execute("INSERT INTO artifacts(task_id,author,title,description,path,sha256,size,created_at) VALUES(?,?,?,?,?,?,?,?)", task, id,
                    Json.Required(args, "title", 160), Json.Text(args, "description"), artifact.Path, artifact.Sha256, artifact.Size, now);
                long artifactId = db.LastId;
                Audit(id, operation, Json.Obj("artifact", artifactId, "task", task), now);
                return db.One("SELECT * FROM artifacts WHERE id=?", artifactId);
            }
            Require(actor, "op", "coordinator", "worker"); RequireNotMuted(id, now);
            var ticket = TaskVersion(Json.Number(args, "task_id"), args);
            if (!Is(actor, "op", "coordinator") && (string)ticket["owner"] != id) throw new RoomError("forbidden", "Only the owner or coordinator can change ticket metadata.");
            if (operation == "task.reference") AddTaskReference(Convert.ToInt64(ticket["id"]), Json.Number(args, "refers_to"));
            else db.Execute("UPDATE tasks SET priority=? WHERE id=?", Priority(args, "priority"), ticket["id"]);
            db.Execute("UPDATE tasks SET version=version+1,updated_at=? WHERE id=?", now, ticket["id"]);
            Audit(id, operation, Json.Obj("task", ticket["id"], "priority", operation == "task.priority" ? (object)Priority(args, "priority") : null, "refers_to", operation == "task.reference" ? (object)Json.Number(args, "refers_to") : null), now);
            var result = Task(Convert.ToInt64(ticket["id"])); AddTaskLinks(result); return result;
        }
    }

    internal sealed class ArtifactFile
    {
        internal string Path, Sha256;
        internal long Size;
        internal static ArtifactFile Read(string root, string path)
        {
            if (String.IsNullOrEmpty(root)) throw new RoomError("artifact_directory", "Choose a delivery directory in Workspace first.");
            if (!System.IO.Path.IsPathRooted(path)) throw new RoomError("artifact_path", "Provide an absolute local file path.");
            try { return Hash(root, System.IO.Path.GetFullPath(path)); }
            catch (Exception e)
            {
                if (e is RoomError) throw;
                if (e is FileNotFoundException || e is DirectoryNotFoundException) throw new RoomError("artifact_missing", "No file exists at that path. Deliver it first, then register it.");
                if (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
                    throw new RoomError("artifact_path", "That file path cannot be read. Check the name and that no other program holds it exclusively.");
                throw;
            }
        }
        private static ArtifactFile Hash(string root, string full)
        {
            string folder = System.IO.Path.GetDirectoryName(full);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
            string parent = LocalFiles.CanonicalDirectory(folder);
            string boundary = root.TrimEnd('\\') + "\\";
            if (!String.Equals(parent, root, StringComparison.OrdinalIgnoreCase) && !parent.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new RoomError("artifact_path", "Deliver the file inside the configured artifact directory before registering it.");
            full = System.IO.Path.Combine(parent, System.IO.Path.GetFileName(full));
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException();
            if ((File.GetAttributes(full) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new RoomError("artifact_path", "Register a regular local file, not a directory or symbolic link.");
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 256L * 1024 * 1024) throw new RoomError("artifact_size", "Register files up to 256 MiB; use a manifest for larger deliveries.");
                using (var hash = SHA256.Create()) return new ArtifactFile { Path = full, Size = stream.Length, Sha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() };
            }
        }
    }
}
