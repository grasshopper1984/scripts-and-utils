using System;

namespace AgentRoom
{
    internal sealed partial class RoomService
    {
        private static string NewMcpSession(string agent) { return agent + "-" + Guid.NewGuid().ToString("N").Substring(0, 8); }

        private void MigrateConnectionSessions()
        {
            db.Execute("ALTER TABLE agents ADD COLUMN mcp_session TEXT NOT NULL DEFAULT ''");
            foreach (var agent in db.Query("SELECT id,session FROM agents WHERE id<>'op'"))
            {
                string active = (string)agent["session"], id = (string)agent["id"];
                // Keep an already joined MCP client's identity. Native Codex/Antigravity ownership belongs
                // to its actual chat, so its optional MCP config gets a separate initial value.
                string copied = active.Length != 0 && !active.StartsWith("codex:", StringComparison.Ordinal) && !active.StartsWith("agy:", StringComparison.Ordinal) ? active : NewMcpSession(id);
                db.Execute("UPDATE agents SET mcp_session=? WHERE id=?", copied, id);
            }
        }

        // The local viewer reads this directly; copying never changes membership or the value.
        internal string McpSession(string agent)
        {
            lock (gate)
            {
                var row = db.One("SELECT mcp_session FROM agents WHERE id=? AND id<>'op'", agent);
                if (row == null) throw new RoomError("not_found", "Agent not found.");
                return (string)row["mcp_session"];
            }
        }
    }
}
