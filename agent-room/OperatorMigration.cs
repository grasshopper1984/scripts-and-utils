using System;
using System.Linq;

namespace AgentRoom
{
    internal sealed partial class RoomService
    {
        private void MigrateOperatorIdentity()
        {
            var previous = db.One("SELECT * FROM agents WHERE id='human'");
            if (previous == null) return; // A new room has no operator until the normal seed runs.
            if ((string)previous["permission"] != "human")
                throw new RoomError("operator_identity", "The old room operator has an unexpected permission. Resolve the identity before upgrading.");
            // Never promote a pre-existing agent (or reuse its credential) to room operator.
            if (db.One("SELECT id FROM agents WHERE id='op'") != null)
                throw new RoomError("operator_handle_conflict", "This room already has an agent named op. Resolve that handle before upgrading; no room records were changed.");
            db.Execute("INSERT INTO agents(id,permission,instructions,status,last_seen,revoked,connector) " +
                "SELECT 'op','op',instructions,status,last_seen,revoked,connector FROM agents WHERE id='human'");
            // Insert the new parent first, then move every reference before deleting the old one.
            // Foreign-key checks remain enabled throughout the surrounding schema transaction.
            foreach (string column in new[] { "assigned", "owner" })
                db.Execute("UPDATE tasks SET " + column + "='op' WHERE " + column + "='human'");
            foreach (string table in new[] { "messages", "artifacts", "escalations" })
                db.Execute("UPDATE " + table + " SET author='op' WHERE author='human'");
            db.Execute("UPDATE receipts SET recipient='op' WHERE recipient='human'");
            db.Execute("UPDATE restrictions SET target='op' WHERE target='human'");
            db.Execute("UPDATE restrictions SET imposed_by='op' WHERE imposed_by='human'");
            db.Execute("UPDATE audit SET actor='op' WHERE actor='human'");
            db.Execute("UPDATE requests SET actor='op' WHERE actor='human'");
            db.Execute("UPDATE settings SET value='op' WHERE key='paused_by' AND value='human'");
            foreach (var message in db.Query("SELECT id,recipients FROM messages WHERE recipients LIKE ?", "%\"human\"%"))
            {
                var recipients = (object[])Json.Serializer().DeserializeObject((string)message["recipients"]);
                db.Execute("UPDATE messages SET recipients=? WHERE id=?", Json.Write(recipients.Select(r => (string)r == "human" ? "op" : (string)r).ToArray()), message["id"]);
            }
            // Message bodies and historical JSON payloads are authored/history text, not identities.
            db.Execute("DELETE FROM agents WHERE id='human'");
        }
    }
}
