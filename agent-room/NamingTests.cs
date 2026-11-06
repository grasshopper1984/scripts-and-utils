using System;
using System.IO;
using System.Linq;

namespace AgentRoom
{
    internal static partial class Tests
    {
        private static void NamingTests()
        {
            using (var f = new Fixture("protocol-names"))
            {
                var status = Result(f.Room.Admin("status"));
                Check((string)status["self"] == "op" && (string)status["permission"] == "op" && Json.Number(status, "protocol") == 2, "The built-in operator is op in protocol 2");
                var request = Json.Obj("version", 1, "token", f.Profiles["alice"]["token"], "session", "alice-test", "op", "status");
                Error(f.Room.Handle(request), "protocol_version");
                request["version"] = 2;
                Error(f.Room.Handle(request), "invalid_argument");
                request.Remove("op"); request["cmd"] = "status";
                Check((string)Result(f.Room.Handle(request))["self"] == "alice", "The cmd field selects an operation with the authenticated agent identity");
                request["op"] = "op"; request["permission"] = "op"; request["cmd"] = "agent.enroll";
                request["id"] = "spoof-operator"; request["args"] = Json.Obj("agent", "fake", "permission", "worker");
                Error(f.Room.Handle(request), "forbidden");
                foreach (string cmd in new[] { "agent.enroll", "agent.update", "agent.revoke", "agent.reset", "agent.rotate" })
                {
                    try { f.Room.Admin(cmd, Json.Obj("agent", "OP", "permission", "worker", "instructions", "Reserved identity")); Check(false, "Operator handle accepted by " + cmd); }
                    catch (RoomError e) { Check(e.Code == "invalid_argument", "The op handle is reserved for " + cmd); }
                }
                try { f.Room.Admin("agent.enroll", Json.Obj("agent", "fake", "permission", "op", "instructions", "No operator profiles")); Check(false, "Operator permission accepted for an agent"); }
                catch (RoomError e) { Check(e.Code == "invalid_argument", "Agents cannot be enrolled with operator permission"); }
                Error(f.Call("alice", "post", Json.Obj("body", "Reach the operator", "to", new[] { "op" })), "use_escalate");
                Error(f.Call("moderator", "moderate", Json.Obj("action", "mute", "target", "op", "reason", "Cannot restrict operator")), "forbidden");
                var posted = Result(f.Call("alice", "post", Json.Obj("body", "Literal @op mention")));
                Check(((string[])posted["recipients"]).Length == 0 && Rows(f.Room.Admin("inbox")).Count == 0, "Mentioning op does not create an operator receipt");
            }
            OperatorMigrationTests();
        }

        private static string LegacyOperatorRoom(string topic)
        {
            string directory = Folder(topic);
            using (var room = new RoomService(directory)) { }
            using (var db = new Database(Path.Combine(directory, "room.db")))
            {
                // Build a schema-5 fixture with the former built-in identity, without live room data.
                // The new room is empty: recreate just its agents table with the historical columns.
                db.Execute("DROP TABLE agents");
                db.Execute("CREATE TABLE agents(id TEXT PRIMARY KEY,permission TEXT NOT NULL,instructions TEXT NOT NULL,token_hash TEXT UNIQUE,session TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'not joined',last_seen INTEGER NOT NULL DEFAULT 0,revoked INTEGER NOT NULL DEFAULT 0,connector TEXT NOT NULL DEFAULT 'generic')");
                db.Execute("INSERT INTO agents(id,permission,instructions,status,last_seen) VALUES('human','human','Existing operator instructions','available',1234)");
                db.Execute("PRAGMA user_version=5");
            }
            return directory;
        }

        private static void OperatorMigrationTests()
        {
            string directory = LegacyOperatorRoom("operator-migration"), token = Json.Secret();
            string history = "{\"body\":\"human @human\",\"op\":\"post\"}";
            using (var db = new Database(Path.Combine(directory, "room.db")))
            {
                db.Execute("INSERT INTO agents(id,permission,instructions,token_hash,session,connector) VALUES('reviewer','moderator','Existing moderator',?,'reviewer-session','codex')", Json.Hash(token));
                db.Execute("INSERT INTO agents(id,permission,instructions) VALUES('worker','worker','Existing worker')");
                db.Execute("INSERT INTO tasks(id,title,objective,acceptance,root_id,assigned,owner,state,version,checkpoint) VALUES(1,'Existing task','Keep references','All references resolve',1,'human','human','paused',4,'Keep this checkpoint')");
                db.Execute("INSERT INTO messages(id,at,author,kind,body,task_id,visibility,recipients) VALUES(1,1200,'human','note','human @human',1,'published','[\"human\",\"worker\"]')");
                db.Execute("INSERT INTO receipts(message_id,recipient) VALUES(1,'human')");
                db.Execute("INSERT INTO artifacts(task_id,author,title,description,path,sha256,size,created_at) VALUES(1,'human','Existing artifact','Keep record',?,'fixture-hash',7,1200)", Path.Combine(directory, "artifact.txt"));
                db.Execute("INSERT INTO escalations(at,author,severity,summary,details,evidence) VALUES(1200,'human','info','Existing escalation','human @human','[]')");
                db.Execute("INSERT INTO restrictions(target,action,until_at,reason,imposed_by) VALUES('worker','mute',1800000300,'Existing operator decision','human')");
                db.Execute("INSERT OR REPLACE INTO settings(key,value) VALUES('paused','true')");
                db.Execute("INSERT INTO settings(key,value) VALUES('paused_by','human')");
                db.Execute("INSERT INTO audit(at,actor,action,details) VALUES(1200,'human','fixture',?)", history);
                db.Execute("INSERT INTO requests(actor,id,digest,response) VALUES('human','existing-request','existing-digest',?)", history);
            }
            using (var room = new RoomService(directory, () => 1800000000))
            {
                var status = Result(room.Handle(Json.Obj("version", 2, "token", token, "session", "reviewer-session", "cmd", "status")));
                Check((string)status["self"] == "reviewer" && (string)status["permission"] == "moderator" && (string)status["paused_by"] == "op", "Migration preserves authenticated sessions and operator pause ownership");
                foreach (var args in new[] { Json.Obj("action", "resume", "reason", "Cannot override pause"), Json.Obj("action", "unmute", "target", "worker", "reason", "Cannot override restriction") })
                    Error(room.Handle(Json.Obj("version", 2, "token", token, "session", "reviewer-session", "cmd", "moderate", "args", args, "id", Guid.NewGuid().ToString("N"))), "op_override");
                using (var db = new Database(Path.Combine(directory, "room.db")))
                {
                    var op = db.One("SELECT * FROM agents WHERE id='op'");
                    Check((string)op["permission"] == "op" && op["token_hash"] == null && (string)op["session"] == "" && (string)op["instructions"] == "Existing operator instructions" && Json.Number(op, "last_seen") == 1234, "The migrated operator retains its metadata without an agent credential");
                    Check(db.One("SELECT id FROM agents WHERE id='human'") == null, "The previous operator identity is removed");
                    var reviewer = db.One("SELECT * FROM agents WHERE id='reviewer'");
                    Check((string)reviewer["token_hash"] == Json.Hash(token) && (string)reviewer["connector"] == "codex", "Migration keeps other agents' credentials and connector preferences");
                    var task = db.One("SELECT * FROM tasks WHERE id=1");
                    Check((string)task["assigned"] == "op" && (string)task["owner"] == "op" && Json.Number(task, "version") == 4, "Task identities move without changing the task revision");
                    foreach (string table in new[] { "messages", "artifacts", "escalations" })
                        Check((string)db.One("SELECT author FROM " + table + " WHERE id=1")["author"] == "op", "Migration moves " + table + " authors");
                    Check((string)db.One("SELECT recipient FROM receipts WHERE message_id=1")["recipient"] == "op", "Migration retains the operator's unread receipts");
                    var message = db.One("SELECT * FROM messages WHERE id=1");
                    Check((string)message["recipients"] == "[\"op\",\"worker\"]" && (string)message["body"] == "human @human", "Structured recipients migrate while authored message text is preserved");
                    var audit = db.One("SELECT * FROM audit WHERE action='fixture'");
                    var request = db.One("SELECT * FROM requests WHERE id='existing-request'");
                    Check((string)audit["actor"] == "op" && (string)audit["details"] == history && (string)request["actor"] == "op" && (string)request["response"] == history, "Historical audit and retry records retain their contents with the renamed actor");
                    Check(db.Query("PRAGMA foreign_key_check").Count == 0 && Convert.ToInt64(db.One("PRAGMA user_version")["user_version"]) == RoomService.SchemaVersion, "Operator migration commits the current schema with every foreign key intact");
                }
            }
            using (var room = new RoomService(directory))
                Check(Rows(room.Admin("members")).Count(a => (string)a["permission"] == "op") == 1, "Reopening does not duplicate the migrated operator");

            string conflict = LegacyOperatorRoom("operator-conflict"), conflictingToken = Json.Secret();
            using (var db = new Database(Path.Combine(conflict, "room.db")))
                db.Execute("INSERT INTO agents(id,permission,instructions,token_hash,session) VALUES('op','worker','Existing agent',?,'existing-session')", Json.Hash(conflictingToken));
            try { using (var room = new RoomService(conflict)) { } Check(false, "Migration accepted an existing op agent"); }
            catch (RoomError e) { Check(e.Code == "operator_handle_conflict", "An existing op agent blocks migration rather than acquiring operator privileges"); }
            using (var db = new Database(Path.Combine(conflict, "room.db")))
            {
                var agent = db.One("SELECT * FROM agents WHERE id='op'");
                Check((string)agent["permission"] == "worker" && (string)agent["token_hash"] == Json.Hash(conflictingToken) && (string)agent["session"] == "existing-session", "Failed migration leaves the conflicting agent unchanged");
                Check(db.One("SELECT id FROM agents WHERE id='human'") != null && Convert.ToInt64(db.One("PRAGMA user_version")["user_version"]) == 5 && db.Query("PRAGMA foreign_key_check").Count == 0, "Failed migration rolls back the identity and schema changes");
            }
        }
    }
}
