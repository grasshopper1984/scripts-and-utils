using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace AgentRoom
{
    internal static partial class Tests
    {
        private static List<Dictionary<string, object>> DataRows(object value) { return (List<Dictionary<string, object>>)value; }
        private static void ProjectTests()
        {
            using (var f = new Fixture("project"))
            {
                long a = Convert.ToInt64(f.Ticket("Research", "alice")["id"]), b = Convert.ToInt64(f.Ticket("Design")["id"]), context = Convert.ToInt64(f.Ticket("Background notes")["id"]);
                long combined = Convert.ToInt64(Result(f.Call("coordinator", "task.create", Json.Obj("title", "Implementation", "objective", "Combine two prerequisites", "acceptance", "Both ready", "priority", 2, "dependencies", new[] { a.ToString(), b.ToString() }, "references", new[] { context.ToString() })))["id"]);
                var combinedTask = Result(f.Call("alice", "task", Json.Obj("task_id", combined)));
                Check(((object[])combinedTask["dependencies"]).Length == 2 && ((object[])combinedTask["references"]).Length == 1, "Task stores multiple typed predecessor links");
                Error(f.Call("alice", "task.claim", Json.Obj("task_id", combined, "version", 1)), "dependencies_pending");
                Error(f.Call("coordinator", "task.reference", Json.Obj("task_id", a, "version", 1, "refers_to", combined)), "dependency_cycle");
                Error(f.Call("coordinator", "task.depend", Json.Obj("task_id", context, "version", 1, "depends_on", combined)), "dependency_cycle");
                long referenceOnly = Convert.ToInt64(Result(f.Call("coordinator", "task.create", Json.Obj("title", "Independent work", "objective", "Use background context", "acceptance", "No execution gate", "assigned", "alice", "references", new[] { context.ToString() })))["id"]);
                Result(f.Call("alice", "task.claim", Json.Obj("task_id", referenceOnly, "version", 1)));
                Check((string)Result(f.Call("alice", "task", Json.Obj("task_id", referenceOnly)))["state"] == "active", "Unfinished context references do not block work");
                Error(f.Call("bob", "task.priority", Json.Obj("task_id", referenceOnly, "version", 2, "priority", 1)), "forbidden");
                Error(f.Call("moderator", "task.reference", Json.Obj("task_id", b, "version", 1, "refers_to", a)), "forbidden");
                f.Now += 10; Result(f.Call("alice", "task.priority", Json.Obj("task_id", referenceOnly, "version", 2, "priority", 1)));
                Error(f.Call("alice", "task.priority", Json.Obj("task_id", referenceOnly, "version", 2, "priority", 2)), "stale_version");
                var filtered = Result(f.Call("observer", "graph", Json.Obj("min_priority", 2)));
                Check(DataRows(filtered["nodes"]).Count == 1 && Convert.ToInt64(DataRows(filtered["nodes"])[0]["hidden_links"]) == 3, "Filtered graph retains a count of all hidden predecessor links");
                var focused = Result(f.Call("observer", "graph", Json.Obj("focus", combined, "limit", 2, "min_priority", 0)));
                Check(Convert.ToInt64(focused["total"]) == 4 && DataRows(focused["nodes"]).Count == 2 && (bool)focused["truncated"], "Focus contains direct neighbors and reports truncation");
                Check(Convert.ToInt64(DataRows(focused["nodes"])[0]["id"]) == combined, "Focused anchor is retained under a node cap");
                var anchor = Result(f.Call("observer", "graph", Json.Obj("focus", a, "min_priority", 2, "state", "done")));
                Check(DataRows(anchor["nodes"]).Count == 1 && Convert.ToInt64(DataRows(anchor["nodes"])[0]["id"]) == a, "Explicit anchor survives filters that exclude its neighbors");
                Check(Rows(f.Call("alice", "tasks", Json.Obj("agent", "alice", "state", "active", "since", f.Now))).Single()["id"].Equals(referenceOnly), "Owner, state, and update-date filters intersect");
                Check(Rows(f.Call("alice", "tasks", Json.Obj("until", f.Now))).All(t => Convert.ToInt64(t["id"]) != referenceOnly), "End-date boundary is exclusive");
                Error(f.Call("alice", "graph", Json.Obj("since", 100, "until", 99)), "invalid_argument");
                var graph = Result(f.Call("alice", "graph"));
                var layout = GraphLayout.Create(DataRows(graph["nodes"]), DataRows(graph["edges"]), 1.5f);
                Check(DataRows(graph["edges"]).All(e => layout.Bounds[Convert.ToInt64(e["target"])].Bottom < layout.Bounds[Convert.ToInt64(e["source"])].Top), "Vertical history places every typed successor above its predecessors");
                var boxes = layout.Bounds.Values.ToArray();
                Check(boxes.SelectMany((box, i) => boxes.Skip(i + 1).Select(other => !box.IntersectsWith(other))).All(x => x), "Graph cards never overlap at scaled DPI");

                long first = Convert.ToInt64(Result(f.Call("alice", "post", Json.Obj("body", "@bob @op Source context", "task_id", a)))["message_id"]);
                long second = Convert.ToInt64(Result(f.Call("bob", "post", Json.Obj("body", "@alice Combined context", "task_id", combined, "task_refs", new[] { a.ToString(), b.ToString(), combined.ToString() }, "message_refs", new[] { first.ToString() })))["message_id"]);
                var detail = Result(f.Call("observer", "message", Json.Obj("message_id", second)));
                Check(DataRows(detail["tasks"]).Count == 3 && DataRows(detail["references"]).Count == 1, "Message relates to multiple tasks and an earlier message without duplicate links");
                Check(Rows(f.Call("observer", "feed", Json.Obj("task_id", a))).Count == 2, "Task-filtered feed includes context links as well as the primary ticket");
                Check(DataRows(Result(f.Call("observer", "task", Json.Obj("task_id", b)))["messages"]).Count == 1, "Task detail finds messages that reference it");
                Error(f.Call("bob", "agent.inbox", Json.Obj("agent", "alice")), "forbidden");
                Check(Rows(f.Room.Admin("agent.inbox", Json.Obj("agent", "bob"))).Count == 1, "OP can inspect a selected agent's inbox");
                Check(Rows(f.Call("bob", "inbox")).Count == 1, "OP inspection leaves the agent receipt unread");
                Result(f.Call("bob", "ack", Json.Obj("message_id", first)));
                Check(Rows(f.Room.Admin("agent.inbox", Json.Obj("agent", "bob"))).Count == 0, "Unread filter observes recipient acknowledgments");
                Check(Rows(f.Room.Admin("agent.inbox", Json.Obj("agent", "bob", "receipts", "all"))).Single()["acknowledged"] != null, "Inbox history retains acknowledged messages");
                Check(Rows(f.Room.Admin("agent.inbox", Json.Obj("agent", "alice", "before", second))).Count == 0, "Inbox pagination honors the before cursor");
                Result(f.Call("moderator", "moderate", Json.Obj("action", "mute", "target", "alice", "reason", "Visibility fixture")));
                long held = Convert.ToInt64(Result(f.Call("alice", "post", Json.Obj("body", "Private held fixture", "task_id", a)))["message_id"]);
                Error(f.Call("bob", "post", Json.Obj("body", "Reference denied", "message_refs", new[] { held.ToString() })), "not_found");
                long published = Convert.ToInt64(Result(f.Room.Admin("post", Json.Obj("body", "Held item is under review", "message_refs", new[] { held.ToString() })))["message_id"]);
                var masked = DataRows(Result(f.Call("bob", "message", Json.Obj("message_id", published)))["references"]).Single();
                Check(!masked.ContainsKey("preview") && (string)masked["visibility"] == "unavailable", "Reference expansion does not reveal a held message to another worker");
                Error(f.Call("bob", "message", Json.Obj("message_id", held)), "not_found");
                Check(DataRows(Result(f.Call("bob", "task", Json.Obj("task_id", a)))["messages"]).All(m => (string)m["visibility"] == "published"), "Task detail preserves held-message visibility rules");
                Result(f.Room.Admin("moderate", Json.Obj("action", "unmute", "target", "alice", "reason", "Continue fixture")));

                string delivery = Folder("delivery-files"); Directory.CreateDirectory(delivery);
                string delivered = Path.Combine(delivery, "result.txt"); File.WriteAllText(delivered, "Synthetic deliverable", new System.Text.UTF8Encoding(false));
                Result(f.Room.Admin("artifact.directory", Json.Obj("path", delivery)));
                var deliveryArgs = Json.Obj("task_id", referenceOnly, "title", "Research notes", "path", delivered, "description", "Synthetic file fixture");
                Error(f.Call("bob", "artifact.register", deliveryArgs), "forbidden");
                var file = Result(f.Call("alice", "artifact.register", deliveryArgs, "deliver-once"));
                Check((string)file["sha256"] == Json.Hash("Synthetic deliverable") && Convert.ToInt64(file["size"]) == 21, "Artifact registration records the exact delivered bytes");
                Check(Convert.ToInt64(Result(f.Call("alice", "artifact.register", deliveryArgs, "deliver-once"))["id"]) == Convert.ToInt64(file["id"]), "Artifact registration retries do not duplicate records");
                long withArtifact = Convert.ToInt64(Result(f.Call("alice", "post", Json.Obj("body", "@bob Delivered", "task_id", referenceOnly, "artifact_refs", new[] { file["id"].ToString() })))["message_id"]);
                Check(DataRows(Result(f.Call("bob", "message", Json.Obj("message_id", withArtifact)))["artifacts"]).Count == 1, "Message detail resolves structured artifact references");
                Check(DataRows(Result(f.Call("alice", "task", Json.Obj("task_id", referenceOnly)))["artifacts"]).Count == 1, "Task detail includes delivery records");
                Error(f.Call("alice", "artifact.register", Json.Obj("task_id", referenceOnly, "title", "Outside", "path", Path.Combine(f.Directory, "room.db"))), "artifact_path");
                Error(f.Call("alice", "artifact.register", Json.Obj("task_id", referenceOnly, "title", "Traversal", "path", Path.Combine(delivery, "..", "outside.txt"))), "artifact_path");
                Error(f.Call("alice", "artifact.register", Json.Obj("task_id", referenceOnly, "title", "Relative", "path", "result.txt")), "artifact_path");
                string sibling = delivery + "-sibling"; Directory.CreateDirectory(sibling); File.WriteAllText(Path.Combine(sibling, "outside.txt"), "outside");
                Error(f.Call("alice", "artifact.register", Json.Obj("task_id", referenceOnly, "title", "Prefix boundary", "path", Path.Combine(sibling, "outside.txt"))), "artifact_path");
                File.WriteAllText(delivered, "Changed after registration");
                Check((string)Rows(f.Call("alice", "artifacts")).Single()["sha256"] == Json.Hash("Synthetic deliverable"), "Artifact metadata remains an honest registration snapshot after later edits");
                Error(f.Call("bob", "artifact.directory", Json.Obj("path", delivery)), "forbidden");

                for (int i = 0; i < 65; i++) f.Ticket("Additional task " + i);
                var bounded = Result(f.Call("observer", "graph", Json.Obj("limit", 999)));
                Check(DataRows(bounded["nodes"]).Count == 60 && (bool)bounded["truncated"] && Convert.ToInt64(bounded["total"]) == 70, "Graph never exceeds its 60-node response cap");
                Check(Json.Write(bounded).Length < 65536, "Bounded graph stays compact with this project history");
            }
        }
        private static void MigrationTests()
        {
            string directory = Folder("legacy"); LocalFiles.ProtectDirectory(directory); string token = Json.Secret();
            using (var db = new Database(Path.Combine(directory, "room.db")))
            {
                db.Execute("CREATE TABLE agents(id TEXT PRIMARY KEY,permission TEXT NOT NULL,instructions TEXT NOT NULL,token_hash TEXT UNIQUE,session TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'not joined',last_seen INTEGER NOT NULL DEFAULT 0,revoked INTEGER NOT NULL DEFAULT 0)");
                db.Execute("CREATE TABLE tasks(id INTEGER PRIMARY KEY AUTOINCREMENT,title TEXT NOT NULL,objective TEXT NOT NULL,acceptance TEXT NOT NULL,parent_id INTEGER REFERENCES tasks(id),root_id INTEGER REFERENCES tasks(id),assigned TEXT REFERENCES agents(id),owner TEXT REFERENCES agents(id),state TEXT NOT NULL DEFAULT 'open',version INTEGER NOT NULL DEFAULT 1,worktree TEXT COLLATE NOCASE NOT NULL DEFAULT '',reserved INTEGER NOT NULL DEFAULT 0,branch TEXT NOT NULL DEFAULT '',base_commit TEXT NOT NULL DEFAULT '',checkpoint TEXT NOT NULL DEFAULT '',remaining INTEGER NOT NULL DEFAULT 8)");
                db.Execute("CREATE TABLE messages(id INTEGER PRIMARY KEY AUTOINCREMENT,at INTEGER NOT NULL,author TEXT NOT NULL REFERENCES agents(id),kind TEXT NOT NULL,body TEXT NOT NULL,task_id INTEGER REFERENCES tasks(id),visibility TEXT NOT NULL,held_reason TEXT NOT NULL DEFAULT '',recipients TEXT NOT NULL)");
                db.Execute("CREATE TABLE receipts(message_id INTEGER NOT NULL REFERENCES messages(id),recipient TEXT NOT NULL REFERENCES agents(id),acknowledged INTEGER,PRIMARY KEY(message_id,recipient))");
                db.Execute("CREATE TABLE audit(id INTEGER PRIMARY KEY AUTOINCREMENT,at INTEGER NOT NULL,actor TEXT NOT NULL,action TEXT NOT NULL,details TEXT NOT NULL)");
                db.Execute("INSERT INTO agents(id,permission,instructions) VALUES('human','human','Operator')");
                db.Execute("INSERT INTO agents(id,permission,instructions,token_hash,session) VALUES('legacy','worker','Existing worker',?,'existing-session')", Json.Hash(token));
                db.Execute("INSERT INTO tasks(id,title,objective,acceptance,root_id,owner,state,version,worktree,reserved,checkpoint,remaining) VALUES(1,'Existing task','Keep data','Preserved',1,'legacy','paused',4,?,1,'Continue after quota reset',5)", Path.Combine(directory, "reserved-worktree"));
                db.Execute("INSERT INTO messages(id,at,author,kind,body,task_id,visibility,recipients) VALUES(1,1200,'legacy','note','Existing message',1,'published','[\"human\"]')");
                db.Execute("INSERT INTO receipts(message_id,recipient) VALUES(1,'human')");
                db.Execute("INSERT INTO audit(at,actor,action,details) VALUES(1100,'human','task.create','{\"task\":1}')");
                db.Execute("INSERT INTO audit(at,actor,action,details) VALUES(1300,'legacy','task.update','{\"task\":1,\"state\":\"paused\"}')");
                db.Execute("PRAGMA user_version=1");
            }
            using (var room = new RoomService(directory))
            {
                var task = Result(room.Admin("task", Json.Obj("task_id", 1)));
                Check((string)task["owner"] == "legacy" && Convert.ToInt64(task["reserved"]) == 1 && Convert.ToInt64(task["version"]) == 4 && Convert.ToInt64(task["remaining"]) == 5, "Migration preserves ownership, reservation, revision, and delivery budget");
                Check(Convert.ToInt64(task["created_at"]) == 1100 && Convert.ToInt64(task["updated_at"]) == 1300, "Migration recovers original task dates from the audit");
                Check(DataRows(task["messages"]).Count == 1 && Rows(room.Admin("inbox")).Count == 1, "Migration retains message-task links and unread receipts");
                Result(room.Handle(Json.Obj("version", RoomService.ProtocolVersion, "token", token, "session", "existing-session", "cmd", "status", "args", Json.Obj())));
                Check((string)Rows(room.Admin("members")).Single(a => (string)a["id"] == "legacy")["connector"] == "generic", "Existing participants migrate to the generic connector");
                Check((string)Rows(room.Admin("members")).Single(a => (string)a["id"] == "op")["permission"] == "op", "Legacy operator identity migrates to op");
                using (var db = new Database(Path.Combine(directory, "room.db"))) Check(Convert.ToInt64(db.One("PRAGMA user_version")["user_version"]) == RoomService.SchemaVersion, "Schema version advances after successful migration");
            }
            using (var reopened = new RoomService(directory)) Check(Rows(reopened.Admin("tasks")).Count == 1, "Reopening a migrated room does not rerun destructive schema changes");
        }
        private static void ThreadTests()
        {
            string[] handoff = Viewer.ConnectionHandoff(@"C:\rooms\a b\alice.profile.json", "alice", "generic", "alice-stable-session").Split(new[] { "\r\n" }, StringSplitOptions.None);
            var config = Json.Read(handoff[handoff.Length - 1]);
            var connector = ((object[])config["args"]).Cast<string>().ToArray();
            Check(handoff[0] == "Join the Agent Room with your configured profile, then follow the briefing it returns.", "The copied config starts with the join instruction");
            Check(((string)config["command"]).EndsWith("room.exe") && connector.Contains(@"C:\rooms\a b\alice.profile.json") && connector.Last() == "--mcp", "The copied config ends with a valid connector configuration");
            using (var f = new Fixture("threads"))
            {
                // Workers take only assigned work; the coordinator may take unassigned work itself.
                long open = Convert.ToInt64(f.Ticket("Unassigned")["id"]);
                Error(f.Call("alice", "task.claim", Json.Obj("task_id", open, "version", 1)), "not_assigned");
                long mine = Convert.ToInt64(f.Ticket("Coordinator work", "coordinator")["id"]);
                Error(f.Call("alice", "task.claim", Json.Obj("task_id", mine, "version", 1)), "forbidden");
                Result(f.Call("coordinator", "task.claim", Json.Obj("task_id", mine, "version", 1)));
                Error(f.Call("alice", "task.assign", Json.Obj("task_id", open, "version", 1, "assignee", "alice")), "forbidden");
                Error(f.Call("coordinator", "task.assign", Json.Obj("task_id", open, "version", 1, "assignee", "moderator")), "invalid_assignee");
                Error(f.Call("coordinator", "task.assign", Json.Obj("task_id", mine, "version", 2, "assignee", "alice")), "conflict");
                var assigned = Result(f.Call("coordinator", "task.assign", Json.Obj("task_id", open, "version", 1, "assignee", "@Alice")));
                Check((string)assigned["assigned"] == "alice" && Convert.ToInt64(assigned["version"]) == 2, "The coordinator assigns an unclaimed ticket");
                Result(f.Call("coordinator", "task.assign", Json.Obj("task_id", open, "version", 2, "assignee", "")));
                Error(f.Call("alice", "task.claim", Json.Obj("task_id", open, "version", 3)), "not_assigned");
                Result(f.Call("coordinator", "task.assign", Json.Obj("task_id", open, "version", 3, "assignee", "alice")));
                Result(f.Call("alice", "task.claim", Json.Obj("task_id", open, "version", 4)));

                // Each ticket keeps its own thread; messages without a ticket form the room thread.
                long request = Convert.ToInt64(Result(f.Call("coordinator", "post", Json.Obj("kind", "request", "task_id", open, "to", new[] { "alice" }, "body", "Build it")))["message_id"]);
                long question = Convert.ToInt64(Result(f.Call("alice", "post", Json.Obj("kind", "question", "task_id", open, "to", new[] { "coordinator" }, "body", "Which format?")))["message_id"]);
                Result(f.Call("coordinator", "post", Json.Obj("kind", "answer", "reply_to", question, "body", "JSON")));
                Result(f.Call("coordinator", "post", Json.Obj("body", "Started on my own ticket", "task_id", mine)));
                Result(f.Call("moderator", "post", Json.Obj("body", "Room-wide reminder: keep conversations on tickets")));
                f.Room.Admin("moderate", Json.Obj("action", "mute", "target", "bob", "seconds", 60, "reason", "Thread visibility check"));
                Result(f.Call("bob", "post", Json.Obj("body", "Held note on the ticket", "task_id", open)));
                var threads = Rows(f.Call("alice", "threads"));
                var room = threads.First(r => Convert.ToInt64(r["task_id"]) == 0);
                var work = threads.First(r => Convert.ToInt64(r["task_id"]) == open);
                Check(threads.Count == 3 && (string)room["title"] == "Room" && (string)work["title"] == "Unassigned", "One thread per ticket plus the room thread");
                Check(Convert.ToInt64(work["messages"]) == 3 && Convert.ToInt64(work["open_asks"]) == 1 && (string)work["last_author"] == "coordinator", "Thread rows count visible messages and asks still awaiting a reply");
                Check(Convert.ToInt64(Rows(f.Call("moderator", "threads")).First(r => Convert.ToInt64(r["task_id"]) == open)["messages"]) == 4, "Supervisors also count held messages");
                var thread = Result(f.Call("alice", "thread", Json.Obj("task_id", open)));
                var messages = DataRows(thread["messages"]);
                Check(messages.Select(m => Convert.ToInt64(m["id"])).SequenceEqual(new[] { request, question, question + 1 }) && !(bool)thread["older"], "A thread reads oldest first without other tickets' messages");
                Check(Convert.ToInt64(messages[0]["open_ask"]) == 1 && Convert.ToInt64(messages[1]["open_ask"]) == 0 && (string)((Dictionary<string, object>)thread["task"])["title"] == "Unassigned", "Open asks are marked until answered");
                var roomThread = DataRows(Result(f.Call("alice", "thread", Json.Obj("task_id", 0)))["messages"]);
                Check(roomThread.Count == 2 && roomThread.Any(m => ((string)m["body"]).StartsWith("Moderation: mute")), "The room thread holds messages without a ticket, including moderation notices");
                var page = Result(f.Call("alice", "thread", Json.Obj("task_id", open, "limit", 2)));
                Check(DataRows(page["messages"]).Count == 2 && (bool)page["older"] && Convert.ToInt64(DataRows(page["messages"])[0]["id"]) == question, "A short page returns the newest messages and reports older ones");
                var nodes = DataRows(Result(f.Call("alice", "graph"))["nodes"]);
                var node = nodes.First(n => Convert.ToInt64(n["id"]) == open);
                Check(Convert.ToInt64(node["messages"]) == 3 && Convert.ToInt64(node["open_asks"]) == 1, "Graph nodes carry only their thread's counts");
            }
        }
        private static void BriefingTests()
        {
            using (var f = new Fixture("briefing"))
            {
                // Joining hands the agent its playbook and the protocol, so nothing has to be installed first.
                var joined = Result(f.Call("moderator", "join"));
                var briefing = (Dictionary<string, object>)joined["briefing"];
                string text = (string)briefing["text"], version = (string)briefing["version"];
                Check((string)briefing["role"] == "moderator" && (string)briefing["source"] == "built-in" && version.Length == 12, "Join returns the built-in moderator briefing with a version");
                Check(text.StartsWith("# Agent Room moderator") && text.Contains("# Agent Room Message Protocol"), "A briefing is the role playbook followed by the protocol");
                Check((string)Result(f.Call("moderator", "status"))["briefing_version"] == version, "Status reports the current briefing version");
                var worker = Result(f.Call("alice", "briefing"));
                Check((string)worker["role"] == "worker" && ((string)worker["text"]).StartsWith("# Agent Room worker") && (string)worker["instructions"] == "Test role", "Each agent is briefed for its own role with its instructions");
                Check(((string)Result(f.Call("coordinator", "briefing"))["text"]).StartsWith("# Agent Room coordinator"), "The coordinator gets the coordinator playbook");
                Check(((string)Result(f.Call("observer", "briefing"))["text"]).StartsWith("# Agent Room observer"), "Observers get the observer playbook");
                Check(((string)Result(f.Call("alice", "briefing", Json.Obj("role", "moderator")))["role"]) == "worker", "Agents cannot read another role's briefing");
                Check((string)Result(f.Room.Admin("briefing", Json.Obj("role", "coordinator")))["role"] == "coordinator", "The OP can preview any role's briefing");
                try { f.Room.Admin("briefing", Json.Obj("role", "op")); Check(false, "The OP role was briefed"); }
                catch (RoomError e) { Check(e.Code == "invalid_argument", "Only agent roles have briefings"); }

                // A file in the room's roles folder replaces the built-in copy, and the version changes with it.
                Directory.CreateDirectory(f.Room.RolesDirectory);
                File.WriteAllText(Path.Combine(f.Room.RolesDirectory, "worker.md"), "# Custom worker\r\nOnly test fixtures.\r\n");
                var custom = Result(f.Call("alice", "briefing"));
                Check((string)custom["source"] == "room" && ((string)custom["text"]).StartsWith("# Custom worker\nOnly test fixtures.") && (string)custom["version"] != (string)worker["version"], "A room override replaces the playbook and changes the version");
                Check((string)Result(f.Call("alice", "status"))["briefing_version"] == (string)custom["version"], "Status follows the override");
                Check((string)Result(f.Call("moderator", "briefing"))["version"] == version, "Overriding one role leaves the others unchanged");
                File.WriteAllText(Path.Combine(f.Room.RolesDirectory, "protocol.md"), "# House protocol\n");
                var house = Result(f.Call("moderator", "briefing"));
                Check((string)house["source"] == "room" && ((string)house["text"]).EndsWith("# House protocol\n") && (string)house["version"] != version, "The protocol can be overridden for every role");
                File.WriteAllText(Path.Combine(f.Room.RolesDirectory, "worker.md"), new string('x', 70000));
                Error(f.Call("alice", "briefing"), "briefing_too_large");
                foreach (string name in RoomService.BriefingDocuments) File.Delete(Path.Combine(f.Room.RolesDirectory, name));
                Check((string)Result(f.Call("moderator", "briefing"))["version"] == version, "Deleting overrides restores the built-in briefing");
            }
        }
        private static void CoordinationTests()
        {
            using (var f = new Fixture("coordination"))
            {
                long ticket = Convert.ToInt64(f.Ticket("Protocol", "alice")["id"]);
                Error(f.Call("alice", "post", Json.Obj("body", "Need help", "to", new[] { "op" })), "use_escalate");
                var mention = Result(f.Call("alice", "post", Json.Obj("body", "Tell @op about this")));
                Check(((string[])mention["recipients"]).Length == 0 && ((string[])mention["ignored_mentions"]).Contains("op"), "A body @op is never delivered");

                // A request is answered by one result that goes back to the requester and settles the receipt.
                long request = Convert.ToInt64(Result(f.Call("coordinator", "post", Json.Obj("kind", "request", "task_id", ticket, "to", new[] { "alice" }, "body", "Implement it")))["message_id"]);
                Check(Convert.ToInt64(Rows(f.Call("alice", "inbox")).First(r => Convert.ToInt64(r["id"]) == request)["needs_reply"]) == 1, "Requests are flagged as needing a reply");
                Error(f.Call("alice", "post", Json.Obj("kind", "result", "reply_to", request, "body", "Done")), "invalid_argument");
                Error(f.Call("bob", "post", Json.Obj("kind", "result", "reply_to", request, "status", "done", "body", "Not mine")), "forbidden");
                Error(f.Call("alice", "post", Json.Obj("kind", "answer", "reply_to", request, "body", "Wrong kind")), "invalid_argument");
                Error(f.Call("alice", "post", Json.Obj("kind", "note", "status", "done", "body", "Status on a note")), "invalid_argument");
                var result = Result(f.Call("alice", "post", Json.Obj("kind", "result", "reply_to", request, "status", "done", "body", "Implemented\nChecks pass")));
                Check(String.Join(",", (string[])result["recipients"]) == "coordinator", "A reply goes back to the requester");
                Check(Rows(f.Call("alice", "inbox")).All(r => Convert.ToInt64(r["id"]) != request), "Replying acknowledges the original");
                var stored = Result(f.Call("coordinator", "message", Json.Obj("message_id", result["message_id"])));
                Check(Convert.ToInt64(stored["reply_to"]) == request && (string)stored["status"] == "done" && Convert.ToInt64(stored["task_id"]) == ticket, "Replies record the thread, status, and inherited ticket");

                long question = Convert.ToInt64(Result(f.Call("coordinator", "post", Json.Obj("kind", "question", "task_id", ticket, "to", new[] { "alice" }, "body", "Which file?")))["message_id"]);
                Error(f.Call("alice", "post", Json.Obj("kind", "answer", "body", "No thread")), "invalid_argument");
                Error(f.Call("alice", "post", Json.Obj("kind", "result", "reply_to", question, "status", "done", "body", "Wrong kind")), "invalid_argument");
                long answer = Convert.ToInt64(Result(f.Call("alice", "post", Json.Obj("kind", "answer", "reply_to", question, "body", "settings.json")))["message_id"]);
                Check(Rows(f.Call("coordinator", "inbox")).Any(r => Convert.ToInt64(r["id"]) == answer), "An answer reaches the asker");

                // Escalations reach the OP; only the OP handles them, and they are rate limited.
                Error(f.Call("alice", "escalate", Json.Obj("summary", "Workers cannot escalate")), "forbidden");
                Error(f.Call("moderator", "escalate", Json.Obj("severity", "critical", "summary", "Bad severity")), "invalid_argument");
                var escalation = Result(f.Call("moderator", "escalate", Json.Obj("severity", "warning", "summary", "Loop on ticket", "task_id", ticket, "evidence", new[] { request.ToString() })));
                Check((string)escalation["state"] == "open" && Rows(f.Call("coordinator", "escalations")).Count == 1, "The coordinator sees open escalations");
                Error(f.Call("observer", "escalations"), "forbidden");
                Error(f.Call("moderator", "escalation.update", Json.Obj("escalation_id", escalation["id"], "state", "resolved")), "forbidden");
                Result(f.Room.Admin("escalation.update", Json.Obj("escalation_id", escalation["id"], "state", "acknowledged")));
                Check(Rows(f.Room.Admin("escalations")).Count == 0 && Rows(f.Room.Admin("escalations", Json.Obj("state", "unresolved"))).Count == 1, "Acknowledged escalations leave the open list but stay unresolved");
                Result(f.Room.Admin("escalation.update", Json.Obj("escalation_id", escalation["id"], "state", "resolved")));
                try { f.Room.Admin("escalation.update", Json.Obj("escalation_id", escalation["id"], "state", "resolved")); Check(false, "A resolved escalation was resolved again"); }
                catch (RoomError e) { Check(e.Code == "conflict", "A resolved escalation cannot change again"); }
                for (int i = 0; i < 2; i++) Result(f.Call("moderator", "escalate", Json.Obj("severity", "urgent", "summary", "Urgent " + i)));
                Error(f.Call("moderator", "escalate", Json.Obj("severity", "urgent", "summary", "Third urgent")), "rate_limited");
                for (int i = 0; i < 3; i++) Result(f.Call("moderator", "escalate", Json.Obj("summary", "Warning " + i)));
                Error(f.Call("moderator", "escalate", Json.Obj("summary", "Seventh this hour")), "rate_limited");
                f.Now += 3601;
                Result(f.Call("moderator", "escalate", Json.Obj("severity", "info", "summary", "A new hour")));

                // The moderator may top up a conversation twice a day before the OP must decide.
                for (int i = 0; i < 2; i++) Result(f.Call("moderator", "budget.reset", Json.Obj("task_id", ticket, "reason", "Productive exchange " + i)));
                Error(f.Call("moderator", "budget.reset", Json.Obj("task_id", ticket, "reason", "Third")), "budget_reset_limit");
                f.Now += 86401;
                Result(f.Call("moderator", "budget.reset", Json.Obj("task_id", ticket, "reason", "Next day")));

                // A cursor reads the feed forward for listeners.
                long latest = Convert.ToInt64(Result(f.Call("moderator", "status"))["latest_message"]);
                Result(f.Call("alice", "post", Json.Obj("body", "After the cursor")));
                var forward = Rows(f.Call("moderator", "feed", Json.Obj("after", latest)));
                Check(forward.Count == 1 && (string)forward[0]["body"] == "After the cursor", "A feed cursor returns only newer messages, oldest first");

                // Resetting chats clears conversation state only.
                Error(f.Call("moderator", "room.reset", Json.Obj("reason", "Not allowed")), "forbidden");
                Check(Convert.ToInt64(Result(f.Room.Admin("room.reset", Json.Obj("reason", "Fresh start")))["cleared_messages"]) > 0, "Reset reports cleared messages");
                Check(Rows(f.Room.Admin("feed")).Count == 0 && Rows(f.Call("coordinator", "inbox")).Count == 0, "Reset clears messages and receipts");
                Check(Convert.ToInt64(Result(f.Call("alice", "task", Json.Obj("task_id", ticket)))["root_remaining"]) == 8 && Rows(f.Room.Admin("tasks")).Count == 1, "Reset keeps tickets and refills budgets");
                Check(Convert.ToInt64(Result(f.Call("moderator", "status"))["latest_message"]) == latest + 1, "Message IDs keep counting after a reset");
                Check(Convert.ToInt64(Result(f.Call("alice", "post", Json.Obj("body", "New conversation")))["message_id"]) == latest + 2, "New messages never reuse cleared IDs");
                Check(Rows(f.Room.Admin("audit")).Any(r => (string)r["action"] == "room.reset"), "Reset is audited");
            }
        }
        private static void ReviewFixTests()
        {
            using (var f = new Fixture("review"))
            {
                // Body text with code-like @words posts as a note; only active participants become recipients.
                var plain = Result(f.Call("alice", "post", Json.Obj("body", "JSDoc @param and @returns; install @types/node; mail ops@example.test; ask @unknown")));
                Check(((string[])plain["recipients"]).Length == 0 && String.Join(",", (string[])plain["ignored_mentions"]) == "param,returns,unknown", "Non-participant @words stay text and are reported back");
                var addressed = Result(f.Call("alice", "post", Json.Obj("body", "Thanks @Bob, see `@param` in the docs")));
                Check(String.Join(",", (string[])addressed["recipients"]) == "bob" && Rows(f.Call("bob", "inbox")).Count == 1, "Body mentions match participant handles case-insensitively");
                Error(f.Call("alice", "post", Json.Obj("body", "@unknown please check", "kind", "request", "task_id", Convert.ToInt64(f.Ticket()["id"]))), "invalid_argument");

                // The moderator cannot weaken or lift a restriction or pause that the OP imposed.
                long ticket = Convert.ToInt64(f.Ticket()["id"]);
                Result(f.Room.Admin("moderate", Json.Obj("action", "mute", "target", "alice", "seconds", 900, "reason", "OP decision")));
                Error(f.Call("moderator", "moderate", Json.Obj("action", "throttle", "target", "alice", "seconds", 1, "reason", "Override")), "op_override");
                Error(f.Call("moderator", "moderate", Json.Obj("action", "unmute", "target", "alice", "reason", "Override")), "op_override");
                Result(f.Call("moderator", "moderate", Json.Obj("action", "warn", "target", "alice", "reason", "Warnings remain available")));
                f.Now += 2;
                Error(f.Call("alice", "task.claim", Json.Obj("task_id", ticket, "version", 1)), "muted");
                Check((string)Rows(f.Call("alice", "members")).First(r => (string)r["id"] == "alice")["imposed_by"] == "op", "Members report who imposed a restriction");
                Result(f.Room.Admin("moderate", Json.Obj("action", "unmute", "target", "alice", "reason", "OP lifts it")));
                Result(f.Call("moderator", "moderate", Json.Obj("action", "mute", "target", "bob", "seconds", 60, "reason", "Moderator decision")));
                Result(f.Call("moderator", "moderate", Json.Obj("action", "throttle", "target", "bob", "seconds", 30, "reason", "Moderator relaxes its own decision")));
                Result(f.Room.Admin("moderate", Json.Obj("action", "pause", "reason", "OP pause")));
                Error(f.Call("moderator", "moderate", Json.Obj("action", "resume", "reason", "Override")), "op_override");
                Result(f.Call("moderator", "moderate", Json.Obj("action", "pause", "reason", "Agrees with the pause")));
                Check((string)Result(f.Room.Admin("status"))["paused_by"] == "op", "A moderator pause during an OP pause leaves the OP in control");
                Result(f.Room.Admin("moderate", Json.Obj("action", "resume", "reason", "OP resumes")));
                Result(f.Call("moderator", "moderate", Json.Obj("action", "pause", "reason", "Moderator pause")));
                Result(f.Call("moderator", "moderate", Json.Obj("action", "resume", "reason", "Moderator resumes its own pause")));

                // A cancelled prerequisite can be removed before the claim instead of blocking the ticket forever.
                long first = Convert.ToInt64(f.Ticket("Dropped prerequisite")["id"]);
                long next = Convert.ToInt64(Result(f.Call("coordinator", "task.create", Json.Obj("title", "Successor", "objective", "o", "acceptance", "a", "assigned", "alice", "dependencies", new[] { first.ToString() })))["id"]);
                Result(f.Call("coordinator", "task.update", Json.Obj("task_id", first, "version", 1, "state", "cancelled", "checkpoint", "No longer needed")));
                Error(f.Call("alice", "task.claim", Json.Obj("task_id", next, "version", 1)), "dependencies_pending");
                Error(f.Call("alice", "task.undepend", Json.Obj("task_id", next, "version", 1, "depends_on", first, "reason", "Worker attempt")), "forbidden");
                Error(f.Call("coordinator", "task.undepend", Json.Obj("task_id", next, "version", 1, "depends_on", ticket, "reason", "Not linked")), "not_found");
                Result(f.Call("coordinator", "task.undepend", Json.Obj("task_id", next, "version", 1, "depends_on", first, "reason", "Prerequisite cancelled")));
                Result(f.Call("alice", "task.claim", Json.Obj("task_id", next, "version", 2)));
                Error(f.Call("coordinator", "task.depend", Json.Obj("task_id", next, "version", 3, "depends_on", first)), "conflict");

                // Registering a missing delivery reports the cause instead of an internal error.
                string delivery = Path.Combine(f.Directory, "deliveries"); Directory.CreateDirectory(delivery);
                Result(f.Room.Admin("artifact.directory", Json.Obj("path", delivery)));
                Error(f.Call("alice", "artifact.register", Json.Obj("task_id", next, "title", "Typo", "path", Path.Combine(delivery, "missing.txt"))), "artifact_missing");
            }
        }
    }
}
