using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AgentRoom
{
    internal sealed partial class RoomService : IDisposable
    {
        internal const int ProtocolVersion = 2;
        internal const int SchemaVersion = 7;
        private readonly object gate = new object();
        private readonly Database db;
        private readonly Func<long> clock;
        private long revision;
        internal long Revision { get { return Interlocked.Read(ref revision); } }
        internal string DirectoryPath { get; private set; }
        internal string PipeName { get; private set; }
        private static readonly string[] ReadOperations = { "members", "feed", "inbox", "tasks", "audit", "status", "message", "task", "graph", "artifacts", "agent.inbox", "escalations", "briefing", "threads", "thread" };

        internal RoomService(string directory, Func<long> clock = null)
        {
            DirectoryPath = Path.GetFullPath(directory);
            if (String.Equals(DirectoryPath.TrimEnd('\\'), Path.GetPathRoot(DirectoryPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(DirectoryPath) && Directory.EnumerateFileSystemEntries(DirectoryPath).Any() && !File.Exists(Path.Combine(DirectoryPath, "room.db")))
                throw new RoomError("data_directory", "Choose an empty dedicated data directory or an existing Agent Room directory.");
            LocalFiles.ProtectDirectory(DirectoryPath);
            DirectoryPath = LocalFiles.CanonicalDirectory(DirectoryPath);
            PipeName = LocalFiles.PipeName(DirectoryPath);
            this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            db = new Database(Path.Combine(DirectoryPath, "room.db"));
            long schema = Convert.ToInt64(db.One("PRAGMA user_version")["user_version"]);
            if (schema > SchemaVersion) { db.Dispose(); throw new RoomError("schema_version", "This database needs a newer Agent Room build."); }
            string[] statements = {
                "CREATE TABLE IF NOT EXISTS agents(id TEXT PRIMARY KEY, permission TEXT NOT NULL, instructions TEXT NOT NULL, token_hash TEXT UNIQUE, session TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'not joined', last_seen INTEGER NOT NULL DEFAULT 0, revoked INTEGER NOT NULL DEFAULT 0)",
                "CREATE UNIQUE INDEX IF NOT EXISTS one_special_role ON agents(permission) WHERE revoked=0 AND permission IN ('moderator','coordinator')",
                "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS tasks(id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, objective TEXT NOT NULL, acceptance TEXT NOT NULL, parent_id INTEGER REFERENCES tasks(id), root_id INTEGER REFERENCES tasks(id), assigned TEXT REFERENCES agents(id), owner TEXT REFERENCES agents(id), state TEXT NOT NULL DEFAULT 'open', version INTEGER NOT NULL DEFAULT 1, worktree TEXT COLLATE NOCASE NOT NULL DEFAULT '', reserved INTEGER NOT NULL DEFAULT 0, branch TEXT NOT NULL DEFAULT '', base_commit TEXT NOT NULL DEFAULT '', checkpoint TEXT NOT NULL DEFAULT '', remaining INTEGER NOT NULL DEFAULT 8)",
                "CREATE UNIQUE INDEX IF NOT EXISTS worktree_reservation ON tasks(worktree) WHERE reserved=1",
                "CREATE TABLE IF NOT EXISTS dependencies(task_id INTEGER NOT NULL REFERENCES tasks(id), depends_on INTEGER NOT NULL REFERENCES tasks(id), PRIMARY KEY(task_id,depends_on))",
                "CREATE TABLE IF NOT EXISTS messages(id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, author TEXT NOT NULL REFERENCES agents(id), kind TEXT NOT NULL, body TEXT NOT NULL, task_id INTEGER REFERENCES tasks(id), visibility TEXT NOT NULL, held_reason TEXT NOT NULL DEFAULT '', recipients TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS receipts(message_id INTEGER NOT NULL REFERENCES messages(id), recipient TEXT NOT NULL REFERENCES agents(id), acknowledged INTEGER, PRIMARY KEY(message_id,recipient))",
                "CREATE INDEX IF NOT EXISTS inbox_lookup ON receipts(recipient,acknowledged,message_id)",
                "CREATE INDEX IF NOT EXISTS recent_sender ON messages(author,at)",
                "CREATE TABLE IF NOT EXISTS restrictions(target TEXT PRIMARY KEY REFERENCES agents(id), action TEXT NOT NULL, until_at INTEGER NOT NULL, reason TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL, details TEXT NOT NULL)",
                "CREATE TABLE IF NOT EXISTS requests(actor TEXT NOT NULL REFERENCES agents(id), id TEXT NOT NULL, digest TEXT NOT NULL, response TEXT NOT NULL, PRIMARY KEY(actor,id))",
                "INSERT OR IGNORE INTO settings(key,value) VALUES('paused','false')"
            };
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                foreach (string sql in statements) db.Execute(sql);
                if (schema < 2) MigrateProjectData();
                // Version 3 records who imposed a restriction, so a moderator cannot weaken an OP decision.
                if (schema < 3) db.Execute("ALTER TABLE restrictions ADD COLUMN imposed_by TEXT NOT NULL DEFAULT ''");
                // Version 4 adds threaded replies, result status, and escalations to the OP.
                if (schema < 4) MigrateCoordination();
                // Version 5 adds the connector preference used by the OP's copy/join handoff.
                if (schema < 5) db.Execute("ALTER TABLE agents ADD COLUMN connector TEXT NOT NULL DEFAULT 'generic'");
                if (schema < 6) MigrateOperatorIdentity();
                if (schema < 7) MigrateConnectionSessions();
                db.Execute("INSERT OR IGNORE INTO agents(id,permission,instructions,status) VALUES('op','op','Room operator','available')");
                // Thread views read messages by ticket and look up replies; both indexes are safe to recreate.
                db.Execute("CREATE INDEX IF NOT EXISTS messages_thread ON messages(task_id,id)");
                db.Execute("CREATE INDEX IF NOT EXISTS messages_reply ON messages(reply_to)");
                db.Execute("PRAGMA user_version=" + SchemaVersion); db.Execute("COMMIT");
            }
            catch { db.Execute("ROLLBACK"); db.Dispose(); throw; }
        }

        internal Dictionary<string, object> Handle(Dictionary<string, object> request)
        {
            try
            {
                if (Json.Number(request, "version") != ProtocolVersion) throw new RoomError("protocol_version", "Expected protocol version 2 with a cmd field. Update the connector and restart it.");
                return Invoke(false, Json.Required(request, "token", 128), Json.Text(request, "session", "", 100),
                    Json.Required(request, "cmd", 40), Json.Map(request, "args"), Json.Text(request, "id", "", 100));
            }
            catch (RoomError e) { return Json.Error(e.Code, e.Message); }
            catch (Exception) { return Json.Error("internal_error", "The request could not be completed."); }
        }
        internal Dictionary<string, object> Admin(string operation, Dictionary<string, object> args = null, string requestId = null)
        {
            return Invoke(true, "", "", operation, args ?? Json.Obj(), requestId ?? Guid.NewGuid().ToString("N"));
        }
        private Dictionary<string, object> Authenticate(bool isOperator, string token, string session, string operation)
        {
            var actor = isOperator ? db.One("SELECT * FROM agents WHERE id='op'") :
                db.One("SELECT * FROM agents WHERE token_hash=? AND revoked=0", Json.Hash(token));
            if (actor == null) throw new RoomError("unauthorized", "Profile is invalid or revoked.");
            if (!isOperator && operation != "join" && (session.Length == 0 || (string)actor["session"] != session))
                throw new RoomError("session_required", "Join with this session ID before using room operations.");
            return actor;
        }
        private Dictionary<string, object> Invoke(bool isOperator, string token, string session, string operation,
            Dictionary<string, object> args, string requestId)
        {
            GitWorkspace workspace = null;
            ArtifactFile artifact = null;
            string artifactDirectory = null;
            bool readOnly = ReadOperations.Contains(operation);
            if (!readOnly && requestId.Length == 0) throw new RoomError("request_id_required", "Mutations require a stable request ID for retries.");
            string digest = readOnly ? null : Json.Hash(operation + "\n" + Json.Canonical(args) + (operation == "join" ? "\n" + session : ""));
            lock (gate)
            {
                var preflightActor = Authenticate(isOperator, token, session, operation);
                var cached = readOnly ? null : Cached(preflightActor, session, operation, requestId, digest);
                if (cached != null) return cached;
                if (operation == "repository") Require(preflightActor, "op");
                if (operation == "task.claim") Require(preflightActor, "op", "coordinator", "worker");
                if (operation == "artifact.directory") Require(preflightActor, "op");
                if (operation == "artifact.register") { RequireArtifactWriter(preflightActor, args, clock()); artifactDirectory = Setting("artifact_directory"); }
            }
            // Read-only Git commands run outside database transactions and the room lock.
            if (operation == "repository" || (operation == "task.claim" && Json.Text(args, "worktree").Length != 0))
                workspace = GitWorkspace.Read(Json.Required(args, operation == "repository" ? "path" : "worktree", 1024));
            if (operation == "artifact.directory") artifactDirectory = LocalFiles.CanonicalDirectory(Json.Required(args, "path", 1024));
            if (operation == "artifact.register") artifact = ArtifactFile.Read(artifactDirectory, Json.Required(args, "path", 1024));
            lock (gate)
            {
                var actor = Authenticate(isOperator, token, session, operation);
                string actorId = (string)actor["id"];
                long now = clock();
                if (readOnly)
                {
                    if (!isOperator && now - Convert.ToInt64(actor["last_seen"]) >= 30)
                    {
                        db.Execute("UPDATE agents SET last_seen=? WHERE id=?", now, actorId);
                        Interlocked.Increment(ref revision);
                    }
                    return Json.Ok(Read(actor, operation, args, now));
                }
                // Another caller may have committed this request while Git inspection ran.
                var cached = Cached(actor, session, operation, requestId, digest);
                if (cached != null) return cached;
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    object result = Mutate(actor, session, operation, args, workspace, artifact, artifactDirectory, now);
                    var response = Json.Ok(result);
                    // A credential is returned once to the OP, never written into the database ledger.
                    object persisted = operation == "agent.enroll" || operation == "agent.rotate"
                        ? Json.Error("profile_already_issued", "Profile was already issued. Renew it in the GUI if the file was lost.") : response;
                    db.Execute("INSERT INTO requests(actor,id,digest,response) VALUES(?,?,?,?)", actorId, requestId, digest, Json.Write(persisted));
                    db.Execute("UPDATE agents SET last_seen=? WHERE id=?", now, actorId);
                    db.Execute("COMMIT");
                    Interlocked.Increment(ref revision);
                    return response;
                }
                catch { db.Execute("ROLLBACK"); throw; }
            }
        }
        private Dictionary<string, object> Cached(Dictionary<string, object> actor, string session, string operation, string requestId, string digest)
        {
            var cached = db.One("SELECT digest,response FROM requests WHERE actor=? AND id=?", actor["id"], requestId);
            if (cached == null) return null;
            if ((string)cached["digest"] != digest) throw new RoomError("request_id_reused", "That request ID was already used with different input.");
            // A join retry may replay success only while that membership still exists. Never
            // reattach a reset session or hide a replacement owner behind a cached response.
            if (operation == "join" && (string)actor["session"] != session)
            {
                if ((string)actor["session"] == "")
                    throw new RoomError("session_required", "That join is no longer active. Join explicitly with a new request ID in the intended session.");
                throw new RoomError("session_conflict", "Another session now owns this profile. Ask the OP to stop it and reset the session before joining.");
            }
            return Json.Read((string)cached["response"]);
        }
        private object Read(Dictionary<string, object> actor, string cmd, Dictionary<string, object> args, long now)
        {
            string id = (string)actor["id"];
            long limit = Math.Max(1, Math.Min(20, Json.Number(args, "limit", 20)));
            long before = Json.Number(args, "before", Int64.MaxValue);
            bool supervision = Is(actor, "op", "moderator");
            if (new[] { "message", "task", "graph", "artifacts", "agent.inbox" }.Contains(cmd)) return ReadProject(actor, cmd, args);
            if (cmd == "members") return db.Query("SELECT a.id,a.permission,a.connector,a.instructions,a.status,a.last_seen,a.revoked,r.action AS restriction,r.until_at,r.reason,r.imposed_by FROM agents a LEFT JOIN restrictions r ON r.target=a.id AND r.until_at>? ORDER BY a.id", now);
            if (cmd == "escalations") return ReadEscalations(actor, args);
            if (cmd == "briefing") return ReadBriefing(actor, args);
            if (cmd == "threads") return ReadThreads(actor, args);
            if (cmd == "thread") return ReadThread(actor, args);
            // A cursor reads forward, oldest first, so a listener can resume where it stopped.
            if (cmd == "feed" && args.ContainsKey("after")) return db.Query("SELECT * FROM messages WHERE id>? AND (visibility='published' OR author=? OR ?=1) ORDER BY id LIMIT ?", Json.Number(args, "after"), id, supervision ? 1 : 0, limit);
            if (cmd == "feed") return db.Query("SELECT * FROM messages WHERE id<? AND (visibility='published' OR author=? OR ?=1) AND (?=0 OR id IN (SELECT message_id FROM message_tasks WHERE task_id=?)) ORDER BY id DESC LIMIT ?", before, id, supervision ? 1 : 0, Json.Number(args, "task_id"), Json.Number(args, "task_id"), limit);
            if (cmd == "inbox") return db.Query("SELECT m.*,CASE WHEN m.kind IN ('request','question') THEN 1 ELSE 0 END AS needs_reply FROM messages m JOIN receipts r ON r.message_id=m.id WHERE r.recipient=? AND r.acknowledged IS NULL ORDER BY m.id LIMIT ?", id, limit);
            if (cmd == "tasks")
            {
                var values = new List<object>();
                string filter = TaskFilter(args, values);
                values.Add(before); values.Add(limit);
                var rows = db.Query("SELECT t.*,root.remaining AS root_remaining FROM tasks t LEFT JOIN tasks root ON root.id=t.root_id WHERE " + filter + " AND t.id<? ORDER BY t.id DESC LIMIT ?", values.ToArray());
                foreach (var row in rows) AddTaskLinks(row);
                return rows;
            }
            if (cmd == "audit")
            {
                Require(actor, "op", "moderator", "coordinator");
                return db.Query("SELECT * FROM audit WHERE id<? ORDER BY id DESC LIMIT ?", before, limit);
            }
            return Json.Obj("paused", Paused, "paused_by", Setting("paused_by"), "repository", Setting("repository"), "artifact_directory", Setting("artifact_directory"), "data_directory", DirectoryPath, "self", id,
                "permission", actor["permission"], "instructions", actor["instructions"],
                "restriction", Restriction(id, now), "briefing_version", BriefingVersion(actor), "delivery", "durable inbox; use the connector's wait operation to block until mail arrives",
                "protocol", ProtocolVersion, "latest_message", Count("SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='messages'),0) AS n"));
        }
        private object Mutate(Dictionary<string, object> actor, string session, string cmd,
            Dictionary<string, object> a, GitWorkspace workspace, ArtifactFile artifact, string artifactDirectory, long now)
        {
            string id = (string)actor["id"];
            if (new[] { "task.reference", "task.priority", "artifact.directory", "artifact.register" }.Contains(cmd))
                return MutateProject(actor, cmd, a, artifact, artifactDirectory, now);
            if (cmd == "join")
            {
                if (session.Length == 0) throw new RoomError("invalid_argument", "A session ID is required.");
                if ((string)actor["session"] != "" && (string)actor["session"] != session)
                    throw new RoomError("session_conflict", "Another session owns this profile. Ask the OP to reset it.");
                db.Execute("UPDATE agents SET session=?,status='available' WHERE id=?", session, id);
                Audit(id, cmd, Json.Obj("agent", id), now);
                // The role briefing arrives with the join, so no skill has to be installed before the session starts.
                return Json.Obj("agent", id, "permission", actor["permission"], "instructions", actor["instructions"], "briefing", Briefing((string)actor["permission"]));
            }
            if (cmd == "heartbeat")
            {
                string status = Json.Required(a, "status", 32);
                if (!new[] { "available", "working", "paused", "quota exhausted", "disconnected" }.Contains(status))
                    throw new RoomError("invalid_argument", "Unknown status.");
                db.Execute("UPDATE agents SET status=? WHERE id=?", status, id);
                return Json.Obj("status", status);
            }
            if (cmd == "agent.enroll" || cmd == "agent.update" || cmd == "agent.revoke" || cmd == "agent.reset" || cmd == "agent.rotate")
            {
                Require(actor, "op");
                string target = Json.Required(a, "agent", 40).ToLowerInvariant();
                if (!Regex.IsMatch(target, "^[a-z][a-z0-9_-]{0,31}$") || target == "op") throw new RoomError("invalid_argument", "Choose a handle using letters, numbers, hyphens, or underscores.");
                var existing = db.One("SELECT * FROM agents WHERE id=?", target);
                if (cmd != "agent.enroll" && existing == null) throw new RoomError("not_found", "Agent not found.");
                if (cmd == "agent.rotate")
                {
                    if (Convert.ToInt64(existing["revoked"]) != 0) throw new RoomError("revoked", "Enroll a new identity for a revoked agent.");
                    string secret = Json.Secret();
                    db.Execute("UPDATE agents SET token_hash=?,session='',mcp_session=?,status='not joined' WHERE id=?", Json.Hash(secret), NewMcpSession(target), target);
                    Audit(id, cmd, Json.Obj("agent", target), now);
                    return Json.Obj("agent", target, "pipe", PipeName, "token", secret, "connector", existing["connector"]);
                }
                if (cmd == "agent.enroll" || cmd == "agent.update")
                {
                    string permission = Json.Required(a, "permission", 20);
                    string connector = Json.Text(a, "connector", existing == null ? "generic" : (string)existing["connector"], 20);
                    if (connector != "generic" && connector != "codex" && connector != "agy") throw new RoomError("invalid_argument", "Unknown connector type.");
                    if (!new[] { "worker", "moderator", "coordinator", "observer" }.Contains(permission)) throw new RoomError("invalid_argument", "Unknown permission.");
                    if (permission == "moderator" && db.One("SELECT id FROM tasks WHERE owner=? AND state NOT IN ('done','cancelled') LIMIT 1", target) != null)
                        throw new RoomError("active_work", "A moderator must be free of active implementation tickets.");
                    if (existing != null && permission == "observer" && db.One("SELECT id FROM tasks WHERE owner=? AND state NOT IN ('done','cancelled') LIMIT 1", target) != null)
                        throw new RoomError("active_work", "Resolve owned tickets before making this agent an observer.");
                    // One coordinator keeps a single voice and integrator; one moderator keeps one set of restrictions.
                    var holder = permission == "moderator" || permission == "coordinator"
                        ? db.One("SELECT id FROM agents WHERE permission=? AND revoked=0 AND id<>?", permission, target) : null;
                    if (holder != null)
                        throw new RoomError("role_taken", "This room already has an active " + permission + ": @" + holder["id"] +
                            ". To replace its session, use Reset session on @" + holder["id"] + "; otherwise deregister it or change its role first.");
                    string instructions = Json.Text(a, "instructions");
                    if (cmd == "agent.enroll")
                    {
                        string secret = Json.Secret();
                        db.Execute("INSERT INTO agents(id,permission,instructions,token_hash,connector,mcp_session) VALUES(?,?,?,?,?,?)", target, permission, instructions, Json.Hash(secret), connector, NewMcpSession(target));
                        Audit(id, cmd, Json.Obj("agent", target, "permission", permission, "connector", connector), now);
                        // Enrollment responses contain a credential and are not saved in the idempotency ledger.
                        return Json.Obj("agent", target, "pipe", PipeName, "token", secret, "connector", connector);
                    }
                    db.Execute("UPDATE agents SET permission=?,instructions=?,connector=? WHERE id=?", permission, instructions, connector, target);
                }
                else if (cmd == "agent.revoke") db.Execute("UPDATE agents SET revoked=1,status='revoked',session='' WHERE id=?", target);
                else db.Execute("UPDATE agents SET session='',mcp_session=?,status='not joined' WHERE id=?", NewMcpSession(target), target);
                Audit(id, cmd, cmd == "agent.update" ? Json.Obj("agent", target, "permission", Json.Required(a, "permission", 20), "instructions", Json.Text(a, "instructions"), "connector", Json.Text(a, "connector", (string)existing["connector"])) : Json.Obj("agent", target), now);
                return Json.Obj("agent", target);
            }
            if (cmd == "repository")
            {
                Require(actor, "op");
                if (db.One("SELECT id FROM tasks WHERE state NOT IN ('done','cancelled') OR reserved=1 LIMIT 1") != null)
                    throw new RoomError("active_work", "Finish existing tickets before changing the repository.");
                if (!workspace.IsMain) throw new RoomError("main_checkout", "Choose the main checkout, not a linked worktree.");
                SetSetting("repository", workspace.Path);
                SetSetting("common_directory", workspace.CommonDirectory);
                Audit(id, cmd, Json.Obj("path", workspace.Path), now);
                return Json.Obj("path", workspace.Path);
            }
            if (cmd == "post") return Post(actor, a, now);
            if (cmd == "ack")
            {
                long message = Json.Number(a, "message_id");
                if (db.One("SELECT message_id FROM receipts WHERE message_id=? AND recipient=?", message, id) == null)
                    throw new RoomError("not_found", "No receipt for this agent and message.");
                db.Execute("UPDATE receipts SET acknowledged=COALESCE(acknowledged,?) WHERE message_id=? AND recipient=?", now, message, id);
                return Json.Obj("acknowledged", message);
            }
            if (cmd == "moderate") return Moderate(actor, a, now);
            if (cmd == "task.create")
            {
                Require(actor, "op", "coordinator");
                RequireNotMuted(id, now);
                long parent = Json.Number(a, "parent_id");
                var parentTask = parent == 0 ? null : Task(parent);
                string assigned = Json.Text(a, "assigned", "", 40);
                if (assigned.Length != 0) EligibleAgent(assigned);
                db.Execute("INSERT INTO tasks(title,objective,acceptance,parent_id,root_id,assigned,priority,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?)",
                    Json.Required(a, "title", 160), Json.Required(a, "objective"), Json.Required(a, "acceptance"),
                    parent == 0 ? null : (object)parent, parentTask == null ? null : parentTask["root_id"], assigned.Length == 0 ? null : assigned, Priority(a, "priority"), now, now);
                long taskId = db.LastId;
                if (parent == 0) db.Execute("UPDATE tasks SET root_id=id WHERE id=?", taskId);
                foreach (string dependency in Json.Strings(a, "dependencies", 20))
                {
                    long dependencyId;
                    if (!Int64.TryParse(dependency, out dependencyId)) throw new RoomError("invalid_argument", "Dependency IDs must be numbers.");
                    AddDependency(taskId, dependencyId);
                }
                foreach (long reference in Ids(a, "references", 20)) AddTaskReference(taskId, reference);
                Audit(id, cmd, Json.Obj("task", taskId), now);
                return Task(taskId);
            }
            if (cmd == "task.claim")
            {
                Require(actor, "op", "coordinator", "worker");
                RequireNotMuted(id, now);
                if (Paused && !Is(actor, "op")) throw new RoomError("paused", "New claims are paused.");
                long taskId = Json.Number(a, "task_id");
                var task = TaskVersion(taskId, a);
                if ((string)task["state"] != "open" || task["owner"] != null) throw new RoomError("conflict", "Ticket is already owned or not open.");
                if (task["assigned"] != null && (string)task["assigned"] != id) throw new RoomError("forbidden", "Ticket is assigned to another agent.");
                if (db.One("SELECT d.depends_on FROM dependencies d JOIN tasks t ON t.id=d.depends_on WHERE d.task_id=? AND t.state<>'done' LIMIT 1", taskId) != null)
                    throw new RoomError("dependencies_pending", "Ticket dependencies are unfinished.");
                // Workers only take what the coordinator hands them; the coordinator may pick up unassigned work itself.
                if (Is(actor, "worker") && task["assigned"] == null)
                    throw new RoomError("not_assigned", "Workers claim only tickets assigned to them. Ask the coordinator for work.");
                if (workspace != null)
                {
                    if (Setting("common_directory") == "" || !String.Equals(workspace.CommonDirectory, Setting("common_directory"), StringComparison.OrdinalIgnoreCase))
                        throw new RoomError("wrong_repository", "Worktree does not belong to the room repository.");
                    if (workspace.IsMain || String.Equals(workspace.Path, Setting("repository"), StringComparison.OrdinalIgnoreCase))
                        throw new RoomError("main_checkout", "Editing tickets must use a separate worktree.");
                    if (workspace.Branch.Length == 0) throw new RoomError("detached_worktree", "Use a named branch for an editing ticket.");
                }
                db.Execute("UPDATE tasks SET owner=?,state='active',version=version+1,worktree=?,reserved=?,branch=?,base_commit=?,updated_at=? WHERE id=?",
                    id, workspace == null ? "" : workspace.Path, workspace == null ? 0 : 1, workspace == null ? "" : workspace.Branch,
                    workspace == null ? "" : workspace.Commit, now, taskId);
                Audit(id, cmd, Json.Obj("task", taskId, "owner", id), now);
                return Task(taskId);
            }
            if (cmd == "task.update")
            {
                Require(actor, "op", "coordinator", "worker");
                long taskId = Json.Number(a, "task_id");
                var task = TaskVersion(taskId, a);
                if (!Is(actor, "op", "coordinator") && (string)task["owner"] != id) throw new RoomError("forbidden", "Only the owner can update this ticket.");
                string state = Json.Required(a, "state", 20);
                if (!new[] { "active", "blocked", "paused", "review", "done", "cancelled" }.Contains(state)) throw new RoomError("invalid_argument", "Unknown ticket state.");
                if (state == "done" || state == "cancelled") Require(actor, "op", "coordinator");
                if ((string)task["state"] == "done" || (string)task["state"] == "cancelled") throw new RoomError("conflict", "Closed tickets cannot be changed.");
                if (task["owner"] == null && state != "cancelled") throw new RoomError("conflict", "Claim the ticket first.");
                var restriction = Restriction(id, now);
                if (!Is(actor, "op") && restriction != null && (string)restriction["action"] == "mute" &&
                    ((string)task["owner"] != id || state == "done" || state == "cancelled"))
                    throw new RoomError("muted", "A muted agent can only checkpoint its own ongoing work.");
                if (!Is(actor, "op") && (Paused || restriction != null && (string)restriction["action"] == "mute") && state == "active")
                    throw new RoomError("paused", "Record a checkpoint or pause; new work is restricted.");
                string checkpoint = Json.Required(a, "checkpoint");
                db.Execute("UPDATE tasks SET state=?,checkpoint=?,version=version+1,updated_at=? WHERE id=?", state, checkpoint, now, taskId);
                Audit(id, cmd, Json.Obj("task", taskId, "state", state, "checkpoint", checkpoint), now);
                return Task(taskId);
            }
            if (cmd == "task.depend")
            {
                Require(actor, "op", "coordinator");
                RequireNotMuted(id, now);
                long taskId = Json.Number(a, "task_id");
                var task = TaskVersion(taskId, a);
                if ((string)task["state"] != "open") throw new RoomError("conflict", "Dependencies can be added only before a claim.");
                AddDependency(taskId, Json.Number(a, "depends_on"));
                db.Execute("UPDATE tasks SET version=version+1,updated_at=? WHERE id=?", now, taskId);
                Audit(id, cmd, Json.Obj("task", taskId, "depends_on", Json.Number(a, "depends_on")), now);
                return Task(taskId);
            }
            if (cmd == "task.assign") return AssignTask(actor, a, now);
            if (cmd == "task.undepend")
            {
                Require(actor, "op", "coordinator");
                RequireNotMuted(id, now);
                long taskId = Json.Number(a, "task_id"), dependency = Json.Number(a, "depends_on");
                var task = TaskVersion(taskId, a);
                if ((string)task["state"] != "open") throw new RoomError("conflict", "Dependencies can be removed only before a claim.");
                if (db.One("SELECT task_id FROM dependencies WHERE task_id=? AND depends_on=?", taskId, dependency) == null)
                    throw new RoomError("not_found", "That ticket is not a dependency.");
                db.Execute("DELETE FROM dependencies WHERE task_id=? AND depends_on=?", taskId, dependency);
                db.Execute("UPDATE tasks SET version=version+1,updated_at=? WHERE id=?", now, taskId);
                Audit(id, cmd, Json.Obj("task", taskId, "depends_on", dependency, "reason", Json.Required(a, "reason", 1000)), now);
                return Task(taskId);
            }
            if (cmd == "budget.reset") return ResetBudget(actor, a, now);
            if (cmd == "escalate") return Escalate(actor, a, now);
            if (cmd == "escalation.update") return HandleEscalation(actor, a, now);
            if (cmd == "room.reset") return ResetChats(actor, a, now);
            if (cmd == "worktree.release")
            {
                Require(actor, "op");
                long taskId = Json.Number(a, "task_id");
                var task = Task(taskId);
                if ((string)task["state"] != "done" && (string)task["state"] != "cancelled")
                    throw new RoomError("active_work", "Close the ticket and confirm its writer stopped before releasing the worktree.");
                string reason = Json.Required(a, "reason", 1000);
                db.Execute("UPDATE tasks SET reserved=0,version=version+1,updated_at=? WHERE id=?", now, taskId);
                Audit(id, cmd, Json.Obj("task", taskId, "worktree", task["worktree"], "reason", reason), now);
                return Task(taskId);
            }
            throw new RoomError("unknown_operation", "Unknown room operation.");
        }

        private object Post(Dictionary<string, object> actor, Dictionary<string, object> a, long now)
        {
            Require(actor, "op", "moderator", "coordinator", "worker");
            string id = (string)actor["id"];
            string body = Json.Required(a, "body");
            string kind = Json.Text(a, "kind", "note", 20);
            if (!MessageKinds.Contains(kind)) throw new RoomError("invalid_argument", "Unknown message kind. Use note, request, question, result, or answer.");
            var original = ReplyTarget(actor, kind, a);
            bool agent = !Is(actor, "op");
            var recipients = new HashSet<string>(Json.Strings(a, "to", 4).Select(x => x.Trim().TrimStart('@').ToLowerInvariant()), StringComparer.Ordinal);
            foreach (string recipient in recipients)
            {
                if (agent && recipient == "op") throw new RoomError("use_escalate", "Agents reach the OP with the escalate operation, not messages.");
                if (db.One("SELECT id FROM agents WHERE id=? AND revoked=0", recipient) == null) throw new RoomError("unknown_recipient", "Unknown or revoked recipient: " + recipient);
            }
            // Body mentions address only active participants, so code such as @param or @types/node stays plain text.
            var ignored = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(body, "(?<![A-Za-z0-9_.@/])@([A-Za-z][A-Za-z0-9_-]{0,31})(?![A-Za-z0-9_/])"))
            {
                string handle = match.Groups[1].Value.ToLowerInvariant();
                if ((agent && handle == "op") || db.One("SELECT id FROM agents WHERE id=? AND revoked=0", handle) == null) ignored.Add(handle); else recipients.Add(handle);
            }
            // A reply with no explicit addressee goes back to the original author.
            if (original != null && recipients.Count == 0 && (string)original["author"] != id && !(agent && (string)original["author"] == "op"))
                recipients.Add((string)original["author"]);
            recipients.Remove(id);
            if (recipients.Count > 4) throw new RoomError("invalid_argument", "At most four recipients per message.");
            long taskId = Json.Number(a, "task_id");
            if (taskId == 0 && original != null && original["task_id"] != null) taskId = Convert.ToInt64(original["task_id"]);
            var task = taskId == 0 ? null : Task(taskId);
            long[] taskReferences = Ids(a, "task_refs", 20), messageReferences = Ids(a, "message_refs", 20), artifactReferences = Ids(a, "artifact_refs", 20);
            foreach (long reference in taskReferences) Task(reference);
            foreach (long reference in messageReferences) VisibleMessage(actor, reference);
            foreach (long reference in artifactReferences)
                if (db.One("SELECT id FROM artifacts WHERE id=?", reference) == null) throw new RoomError("not_found", "Artifact not found.");
            bool actionable = kind != "note";
            if (actionable && (task == null || recipients.Count == 0)) throw new RoomError("invalid_argument", "Actionable messages need a ticket and recipient.");
            long recent = Convert.ToInt64(db.One("SELECT COUNT(*) AS n FROM messages WHERE author=? AND at>?", id, now - 60)["n"]);
            if (!Is(actor, "op") && recent >= 20) throw new RoomError("rate_limited", "At most 20 posts per minute. Wait before retrying.");
            string held = "";
            var restriction = Restriction(id, now);
            if (restriction != null && (string)restriction["action"] == "mute") held = "Agent temporarily muted: " + (string)restriction["reason"];
            if (restriction != null && (string)restriction["action"] == "throttle" && db.One("SELECT id FROM messages WHERE author=? AND at>? LIMIT 1", id, now - 30) != null)
                throw new RoomError("rate_limited", "Throttled: at most one post per 30 seconds.");
            if (actionable && Paused && !Is(actor, "op")) held = "Actionable delivery paused";
            if (actionable && held.Length == 0)
            {
                var root = Task(Convert.ToInt64(task["root_id"]));
                if (Convert.ToInt64(root["remaining"]) < recipients.Count && !Is(actor, "op")) held = "Root delivery budget exhausted";
                else if (!Is(actor, "op")) db.Execute("UPDATE tasks SET remaining=remaining-? WHERE id=?", recipients.Count, root["id"]);
            }
            db.Execute("INSERT INTO messages(at,author,kind,body,task_id,visibility,held_reason,recipients,reply_to,status) VALUES(?,?,?,?,?,?,?,?,?,?)",
                now, id, kind, body, taskId == 0 ? null : (object)taskId, held.Length == 0 ? "published" : "held", held, Json.Write(recipients.OrderBy(x => x).ToArray()),
                original == null ? null : original["id"], Json.Text(a, "status", "", 20));
            long messageId = db.LastId;
            // Only a published reply settles the original receipt. A held reply reaches nobody,
            // so the original remains unread until a successful reply or an explicit acknowledgment.
            if (original != null && held.Length == 0) db.Execute("UPDATE receipts SET acknowledged=COALESCE(acknowledged,?) WHERE message_id=? AND recipient=?", now, original["id"], id);
            foreach (long reference in taskReferences.Concat(taskId == 0 ? new long[0] : new[] { taskId }).Distinct())
                db.Execute("INSERT INTO message_tasks(message_id,task_id) VALUES(?,?)", messageId, reference);
            foreach (long reference in messageReferences)
                db.Execute("INSERT INTO message_references(message_id,refers_to) VALUES(?,?)", messageId, reference);
            foreach (long reference in artifactReferences)
                db.Execute("INSERT INTO message_artifacts(message_id,artifact_id) VALUES(?,?)", messageId, reference);
            if (held.Length == 0) foreach (string recipient in recipients)
                db.Execute("INSERT INTO receipts(message_id,recipient) VALUES(?,?)", messageId, recipient);
            if (held.Length != 0) Audit(id, "message.held", Json.Obj("message", messageId, "reason", held), now);
            return Json.Obj("message_id", messageId, "visibility", held.Length == 0 ? "published" : "held", "reason", held,
                "recipients", recipients.OrderBy(x => x).ToArray(), "ignored_mentions", ignored.ToArray());
        }
        private object Moderate(Dictionary<string, object> actor, Dictionary<string, object> a, long now)
        {
            Require(actor, "op", "moderator");
            string id = (string)actor["id"];
            if (!Is(actor, "op"))
            {
                RequireNotMuted(id, now);
                if (Convert.ToInt64(db.One("SELECT COUNT(*) AS n FROM messages WHERE author=? AND at>?", id, now - 60)["n"]) >= 20)
                    throw new RoomError("rate_limited", "Moderator message limit reached. Wait before retrying.");
                var ownRestriction = Restriction(id, now);
                if (ownRestriction != null && (string)ownRestriction["action"] == "throttle" && db.One("SELECT id FROM messages WHERE author=? AND at>? LIMIT 1", id, now - 30) != null)
                    throw new RoomError("rate_limited", "Moderator is temporarily throttled.");
            }
            string action = Json.Required(a, "action", 20);
            string reason = Json.Required(a, "reason", 1000);
            string target = Json.Text(a, "target", "", 40);
            string[] evidence = Json.Strings(a, "evidence", 10);
            foreach (string item in evidence)
            {
                long message;
                if (!Int64.TryParse(item, out message) || db.One("SELECT id FROM messages WHERE id=?", message) == null)
                    throw new RoomError("invalid_argument", "Evidence must reference existing message IDs.");
            }
            long until = 0;
            bool isOperator = Is(actor, "op");
            if (action == "pause" || action == "resume")
            {
                bool operatorPause = Paused && Setting("paused_by") == "op";
                if (!isOperator && action == "resume" && operatorPause) throw new RoomError("op_override", "The OP paused deliveries; only the OP can resume them.");
                // A moderator pause during an OP pause keeps the OP as the owner of that pause.
                if (isOperator || !operatorPause) { SetSetting("paused", action == "pause" ? "true" : "false"); SetSetting("paused_by", action == "pause" ? id : ""); }
            }
            else
            {
                if (target == "op" || target == id) throw new RoomError("forbidden", "The OP and acting moderator cannot be restricted.");
                if (db.One("SELECT id FROM agents WHERE id=?", target) == null) throw new RoomError("not_found", "Agent not found.");
                var existing = db.One("SELECT imposed_by FROM restrictions WHERE target=? AND until_at>?", target, now);
                if (!isOperator && action != "warn" && existing != null && (string)existing["imposed_by"] == "op")
                    throw new RoomError("op_override", "The OP restricted this agent; only the OP can change that restriction.");
                if (action == "mute" || action == "throttle")
                {
                    long seconds = Json.Number(a, "seconds", 300);
                    if (seconds < 1 || seconds > 900) throw new RoomError("invalid_argument", "Restriction duration must be 1 to 900 seconds.");
                    until = now + seconds;
                    db.Execute("INSERT OR REPLACE INTO restrictions(target,action,until_at,reason,imposed_by) VALUES(?,?,?,?,?)", target, action, until, reason, id);
                }
                else if (action == "unmute") db.Execute("DELETE FROM restrictions WHERE target=?", target);
                else if (action != "warn") throw new RoomError("invalid_argument", "Unknown moderation action.");
            }
            var details = Json.Obj("target", target, "action", action, "reason", reason, "until", until, "evidence", evidence);
            Audit(id, "moderation", details, now);
            // Moderation notices are notes, never automatic work requests.
            db.Execute("INSERT INTO messages(at,author,kind,body,visibility,recipients) VALUES(?,?,? ,?,'published',?)", now, id, "note",
                "Moderation: " + action + (target == "" ? "" : " @" + target) + ". " + reason,
                Json.Write(target == "" ? new string[0] : new[] { target }));
            long messageId = db.LastId;
            if (target != "") db.Execute("INSERT INTO receipts(message_id,recipient) VALUES(?,?)", messageId, target);
            return details;
        }
        private void AddDependency(long task, long dependency)
        {
            Task(dependency);
            CheckTaskCycle(task, dependency);
            db.Execute("INSERT INTO dependencies(task_id,depends_on) VALUES(?,?)", task, dependency);
        }
        private Dictionary<string, object> Task(long id)
        {
            var row = db.One("SELECT * FROM tasks WHERE id=?", id);
            if (row == null) throw new RoomError("not_found", "Ticket not found.");
            return row;
        }
        private Dictionary<string, object> TaskVersion(long id, Dictionary<string, object> args)
        {
            var row = Task(id);
            if (Json.Number(args, "version") != Convert.ToInt64(row["version"])) throw new RoomError("stale_version", "Ticket changed. Read it again before updating.");
            return row;
        }
        private void EligibleAgent(string id)
        {
            var agent = db.One("SELECT * FROM agents WHERE id=? AND revoked=0", id);
            if (agent == null || !Is(agent, "worker", "coordinator")) throw new RoomError("invalid_assignee", "Assign work to an active worker or coordinator.");
        }
        private void RequireNotMuted(string id, long now)
        {
            var restriction = Restriction(id, now);
            if (restriction != null && (string)restriction["action"] == "mute") throw new RoomError("muted", "New work is blocked while this agent is muted.");
        }
        private Dictionary<string, object> Restriction(string id, long now) { return db.One("SELECT action,until_at,reason,imposed_by FROM restrictions WHERE target=? AND until_at>?", id, now); }
        private string Setting(string key) { var row = db.One("SELECT value FROM settings WHERE key=?", key); return row == null ? "" : (string)row["value"]; }
        private bool Paused { get { return Setting("paused") == "true"; } }
        private void SetSetting(string key, string value) { db.Execute("INSERT OR REPLACE INTO settings(key,value) VALUES(?,?)", key, value); }
        private void Audit(string actor, string action, object details, long now) { db.Execute("INSERT INTO audit(at,actor,action,details) VALUES(?,?,?,?)", now, actor, action, Json.Write(details)); }
        private static bool Is(Dictionary<string, object> actor, params string[] permissions) { return permissions.Contains((string)actor["permission"]); }
        private static void Require(Dictionary<string, object> actor, params string[] permissions)
        {
            if (!Is(actor, permissions)) throw new RoomError("forbidden", "This operation is not allowed for your permission level.");
        }
        public void Dispose() { lock (gate) db.Dispose(); }
    }
}
