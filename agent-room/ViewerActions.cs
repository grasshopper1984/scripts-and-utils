using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace AgentRoom
{
    internal sealed partial class Viewer
    {
        private void ConfigureRepository()
        {
            using (var folder = new FolderBrowserDialog { Description = "Choose the main checkout for this room", ShowNewFolderButton = false })
                if (folder.ShowDialog(this) == DialogResult.OK) Run("repository", Json.Obj("path", folder.SelectedPath));
        }
        private void ConfigureArtifacts()
        {
            using (var folder = new FolderBrowserDialog { Description = "Choose the local artifact delivery directory", ShowNewFolderButton = true })
                if (folder.ShowDialog(this) == DialogResult.OK) Run("artifact.directory", Json.Obj("path", folder.SelectedPath));
        }
        private void OpenFolder(string path)
        {
            if (String.IsNullOrEmpty(path) || !Directory.Exists(path)) { MessageBox.Show(this, "Choose an existing local directory in Workspace first.", "Agent Room"); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        private void Enroll()
        {
            using (var dialog = new FieldsDialog("Enroll agent"))
            {
                dialog.Add("agent", "Mention handle", ""); AddAgentFields(dialog, "worker", "generic", "");
                if (dialog.ShowDialog(this) == DialogResult.OK) Run("agent.enroll", AgentValues(dialog), SaveProfile);
            }
        }
        private void EditRole()
        {
            var row = Selected(agents); string agent = SelectedAgent(); if (agent == null) return;
            using (var dialog = new FieldsDialog("Edit agent"))
            {
                AddAgentFields(dialog, (string)row["permission"], Json.Text(row, "connector", "generic"), (string)row["instructions"]);
                if (dialog.ShowDialog(this) == DialogResult.OK) { var values = AgentValues(dialog); values["agent"] = agent; Run("agent.update", values); }
            }
        }
        internal static string ConnectorLabel(string connector) { return connector == "codex" ? "Codex (local)" : connector == "agy" ? "Antigravity (local)" : "Generic (MCP)"; }
        internal static void AddAgentFields(FieldsDialog dialog, string permission, string connector, string instructions)
        {
            dialog.AddChoice("permission", "Role", permission, "worker", "coordinator", "moderator", "observer");
            dialog.AddChoice("connector", "Connector", ConnectorLabel(connector), ConnectorLabel("generic"), ConnectorLabel("codex"), ConnectorLabel("agy"));
            dialog.Add("instructions", "Role instructions", instructions, true);
        }
        private static Dictionary<string, object> AgentValues(FieldsDialog dialog)
        {
            var values = dialog.Values();
            string connector = (string)values["connector"];
            values["connector"] = connector == ConnectorLabel("codex") ? "codex" : connector == ConnectorLabel("agy") ? "agy" : "generic";
            return values;
        }
        private void RenewProfile()
        {
            string agent = SelectedAgent();
            if (agent != null && Confirm("Issue a new profile for @" + agent + "? The current profile stops working and its session must join again.")) Run("agent.rotate", Json.Obj("agent", agent), SaveProfile);
        }
        private void ResetSession()
        {
            string agent = SelectedAgent();
            if (agent != null && Confirm("Let a new session join as @" + agent + "? This does not stop the old process; confirm it has stopped first. For MCP, copy the updated config before reconnecting.")) Run("agent.reset", Json.Obj("agent", agent));
        }
        private void Revoke()
        {
            string agent = SelectedAgent();
            if (agent != null && Confirm("Deregister @" + agent + "? Its profile stops working permanently and its handle cannot be reused. Reserved worktrees stay reserved until you release them.")) Run("agent.revoke", Json.Obj("agent", agent));
        }
        private void ResetChats()
        {
            using (var dialog = new FieldsDialog("Reset chats"))
            {
                dialog.Add("reason", "Deletes every message, held post, and inbox receipt. Tickets, agents, artifacts, and the audit stay. Conversation budgets refill. Reason:", "", true);
                if (dialog.ShowDialog(this) == DialogResult.OK && Confirm("Delete all room messages now? This cannot be undone."))
                    Run("room.reset", dialog.Values(), delegate(Dictionary<string, object> result) { status.Text = "Cleared " + result["cleared_messages"] + " message(s)."; });
            }
        }
        private void CustomizePlaybooks()
        {
            // Copies only missing files, so earlier edits are never overwritten.
            string folder = service.RolesDirectory; int copied = 0;
            try
            {
                Directory.CreateDirectory(folder);
                foreach (string name in RoomService.BriefingDocuments)
                {
                    string path = Path.Combine(folder, name);
                    if (File.Exists(path)) continue;
                    File.WriteAllText(path, RoomService.BuiltInDocument(name), new UTF8Encoding(false)); copied++;
                }
            }
            catch (Exception e) { MessageBox.Show(this, e is RoomError ? e.Message : "The playbooks could not be copied to the room folder.", "Agent Room"); return; }
            status.Text = (copied == 0 ? "Playbooks are already in the room folder." : "Copied " + copied + " built-in playbook(s) for editing.") +
                " Agents get edits at their next join or briefing; delete a file to use the built-in version again.";
            Reload(); OpenFolder(folder);
        }
        private string PlaybookSummary()
        {
            var custom = RoomService.BriefingDocuments.Where(name => File.Exists(Path.Combine(service.RolesDirectory, name))).ToArray();
            return custom.Length == 0 ? "Built-in. Agents receive their role playbook and the message protocol when they join."
                : "This room overrides: " + String.Join(", ", custom) + "\r\n" + service.RolesDirectory + "\r\nOverridden files no longer follow built-in updates; delete one to return to the built-in version.";
        }
        private void SetIdleWait()
        {
            using (var dialog = new FieldsDialog("Idle wait length"))
            {
                dialog.Add("seconds", "Seconds a wait blocks when no mail arrives (60 to 86400). Every time it ends, the waiting agent spends a model call, so use the longest your harnesses keep a call alive: Claude Code needs the progress-aware connector, Codex its background terminal limit, and 1500 is safe everywhere.", waitSeconds.ToString());
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                long seconds; if (!Int64.TryParse((string)dialog.Values()["seconds"], out seconds)) { MessageBox.Show(this, "Enter a number of seconds.", "Agent Room"); return; }
                Run("room.wait", Json.Obj("seconds", seconds));
            }
        }
        private void RestartHub()
        {
            // The executable path may hold a newer build: the build moves a running copy aside instead of overwriting it.
            string executable = Application.ExecutablePath;
            if (!File.Exists(executable)) { MessageBox.Show(this, "No Agent Room executable is at " + executable + ". Finish the build, then restart.", "Agent Room"); return; }
            if (!Confirm("Restart the hub now? Room data is kept. Agents waiting for mail reconnect on their own; other calls fail for a few seconds and can be retried with the same request ID.")) return;
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            int at = args.IndexOf("--after");
            if (at >= 0) args.RemoveRange(at, Math.Min(2, args.Count - at));
            args.Add("--after"); args.Add(System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable, String.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""))) { UseShellExecute = false }); }
            catch (Exception) { MessageBox.Show(this, "The new hub could not be started, so this one keeps running.", "Agent Room"); return; }
            quitting = true; Close();
        }
        private void HandleEscalation(string state)
        {
            var row = Selected(attention); if (row == null) { status.Text = "Select an escalation first."; return; }
            Run("escalation.update", Json.Obj("escalation_id", row["id"], "state", state));
        }
        private bool Confirm(string question) { return MessageBox.Show(this, question, "Agent Room", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK; }
        private void SaveProfile(Dictionary<string, object> profile)
        {
            string directory = Path.Combine(service.DirectoryPath, "profiles"); LocalFiles.ProtectDirectory(directory);
            string file = Path.Combine(directory, (string)profile["agent"] + ".profile.json"); File.WriteAllText(file, Json.Write(profile), new UTF8Encoding(false));
            Clipboard.SetText(ConnectionHandoff(file, (string)profile["agent"], Json.Text(profile, "connector", "generic"), service.McpSession((string)profile["agent"])));
            MessageBox.Show(this, "Profile saved locally. Join instructions for " + ConnectorLabel(Json.Text(profile, "connector", "generic")) + " were copied to the clipboard.\r\n\r\nPaste them into the selected session. Treat the profile file as a credential.\r\n\r\n" + file, "Agent enrolled", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private void CopyConnection() { CopyConnection((file, agent) => ConnectionHandoff(file, agent, Json.Text(Selected(agents), "connector", "generic"), service.McpSession(agent)), "Join instructions copied. Paste them into the intended session only."); }
        private void CopyConfigOnly() { CopyConnection((file, agent) => ConnectionConfig(file, service.McpSession(agent)), "MCP config copied, ready for a harness configuration file."); }
        private void CopyConnection(Func<string, string, string> format, string done)
        {
            string agent = SelectedAgent(); if (agent == null) return; string file = Path.Combine(service.DirectoryPath, "profiles", agent + ".profile.json");
            if (!File.Exists(file)) { MessageBox.Show(this, "Renew the profile to issue a local connector file.", "Agent Room"); return; }
            Clipboard.SetText(format(file, agent)); status.Text = done;
        }
        internal static string ConnectionConfig(string file, string session)
        {
            if (String.IsNullOrEmpty(session)) throw new RoomError("configuration", "This agent has no saved MCP session. Restart the updated hub before copying its config.");
            return Json.Write(Json.Obj("command", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"), "args", new[] { "--profile", file, "--session", session, "--mcp" }));
        }
        // Everything one session needs, pasted as a single message.
        internal static string ConnectionHandoff(string file, string agent, string connector, string session)
        {
            if (connector == "codex") return CodexAdapter.Handoff(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"), file, agent);
            if (connector == "agy") return AgyAdapter.Handoff(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "room.exe"), file, agent);
            return "Join the Agent Room with your configured profile, then follow the briefing it returns.\r\n\r\nIf the room's MCP server is not connected yet, add it as \"agent-room\" with this configuration:\r\n" + ConnectionConfig(file, session);
        }
        private void ResetBudget()
        {
            var row = Selected(tickets); if (row == null) return;
            using (var dialog = new FieldsDialog("Reset conversation budget"))
            {
                dialog.Add("reason", "Reason for eight more deliveries", "", true);
                if (dialog.ShowDialog(this) == DialogResult.OK) { var args = dialog.Values(); args["task_id"] = row["id"]; Run("budget.reset", args); }
            }
        }
        private void ReleaseWorktree()
        {
            var row = Selected(tickets); if (row == null) return;
            using (var dialog = new FieldsDialog("Release reserved worktree"))
            {
                dialog.Add("reason", "Confirm the previous writer has stopped and explain the release", "", true);
                if (dialog.ShowDialog(this) == DialogResult.OK) { var args = dialog.Values(); args["task_id"] = row["id"]; Run("worktree.release", args); }
            }
        }
        private void Moderate()
        {
            using (var dialog = new FieldsDialog("Moderation"))
            {
                var selected = CurrentView == "Agents" ? Selected(agents) : null;
                dialog.AddChoice("action", "Action", "warn", "warn", "throttle", "mute", "unmute", "pause", "resume"); dialog.Add("target", "Target handle (blank for room pause)", selected == null || (string)selected["id"] == "op" ? "" : (string)selected["id"]);
                dialog.Add("reason", "Reason", "", true); dialog.Add("seconds", "Mute/throttle duration (1 to 900 seconds)", "300"); dialog.Add("evidence", "Evidence message IDs, comma separated", "");
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    var args = dialog.Values(); long seconds; if (!Int64.TryParse((string)args["seconds"], out seconds)) { MessageBox.Show(this, "Enter a duration in seconds."); return; }
                    args["seconds"] = seconds; args["evidence"] = Split((string)args["evidence"]); Run("moderate", args);
                }
            }
        }
        private bool OptionalId(string text, out long id)
        {
            id = 0; if (text.Trim().Length == 0) return true;
            if (Int64.TryParse(text.Trim().TrimStart('#'), out id) && id > 0) return true;
            MessageBox.Show(this, "Enter a positive record ID, or leave the optional field blank.", "Agent Room"); return false;
        }
        private static string AgentText(Dictionary<string, object> row)
        {
            if (row == null) return "Select an agent to inspect its role. Double-click it to open its inbox.";
            return "@" + row["id"] + "  ·  " + row["permission"] + (Convert.ToInt64(row["revoked"]) != 0 ? "  ·  revoked" : "") +
                "\r\nStatus: " + row["status"] + "    Last contact: " + (Convert.ToInt64(row["last_seen"]) == 0 ? "never" : Time(row["last_seen"])) +
                "\r\nConnector: " + ConnectorLabel(Json.Text(row, "connector", "generic")) +
                (row["restriction"] == null ? "" : "\r\nRestricted: " + row["restriction"] + " until " + Time(row["until_at"]) + ((string)row["imposed_by"] == "op" ? " (set by the OP)" : "") + ": " + row["reason"]) +
                "\r\n\r\nRole instructions\r\n" + (Convert.ToString(row["instructions"]) == "" ? "None" : row["instructions"]);
        }
        private static DetailDocument MessageDocument(Dictionary<string, object> row)
        {
            var document = new DetailDocument();
            AppendMessage(document, row, 0, new Dictionary<long, Dictionary<string, object>>());
            document.Add("Receipts and references", DetailStyle.Heading);
            document.Add("Receipts: " + String.Join("; ", Rows(row["receipts"]).Select(r => r["recipient"] + (r["acknowledged"] == null ? " · unread" : " · acknowledged " + Time(r["acknowledged"])))), DetailStyle.Meta);
            document.Add("Tasks: " + String.Join("; ", Rows(row["tasks"]).Select(r => "#" + r["id"] + " " + r["title"])), DetailStyle.Meta);
            foreach (var reference in Rows(row["references"])) document.Add("Ref message #" + reference["id"] + ": " + (reference.ContainsKey("preview") ? reference["preview"] : "unavailable"), DetailStyle.Meta);
            foreach (var artifact in Rows(row["artifacts"])) document.Add("Artifact #" + artifact["id"] + " " + artifact["title"] + "\n" + artifact["path"], DetailStyle.Meta);
            return document;
        }
        private static string TaskText(Dictionary<string, object> row)
        {
            var text = new StringBuilder("#" + row["id"] + "  " + row["title"] + "  [" + row["state"] + "]\r\nImportance: " + Importance(row["priority"]) + "  Owner: " + (row["owner"] ?? row["assigned"] ?? "unassigned") + "\r\nCreated: " + Time(row["created_at"]) + "  Updated: " + Time(row["updated_at"]) + "\r\n\r\nObjective: " + row["objective"] + "\r\nAcceptance: " + row["acceptance"] + "\r\nDependencies: " + Json.Write(row["dependencies"]) + "\r\nContext references: " + Json.Write(row["references"]) + "\r\nWorktree: " + row["worktree"] + "\r\nBranch: " + row["branch"] + "\r\nCheckpoint: " + row["checkpoint"] + "\r\nRoot deliveries remaining: " + row["root_remaining"]);
            foreach (var artifact in Rows(row["artifacts"])) text.Append("\r\nArtifact #" + artifact["id"] + " " + artifact["title"] + "\r\n" + artifact["path"]);
            return text.ToString();
        }
        internal static DetailDocument ThreadDocument(Dictionary<string, object> thread) { return ThreadDocument(thread, null); }
        private static DetailDocument ThreadDocument(Dictionary<string, object> thread, string taskDetails)
        {
            var rows = Rows(thread["messages"]).OrderBy(r => Convert.ToInt64(r["id"])).ToList();
            var task = thread["task"] as Dictionary<string, object>;
            var document = new DetailDocument();
            if (taskDetails != null) { document.Add("Ticket details", DetailStyle.Heading); document.Add(taskDetails); }
            document.Add(task == null ? "Room thread" : "Thread for #" + task["id"] + "  " + task["title"], DetailStyle.Heading);
            document.Add(rows.Count + (rows.Count == 1 ? " message" : " messages") + ((bool)thread["older"] ? " · Older messages are not shown" : ""), DetailStyle.Meta);
            if (rows.Count == 0) { document.Add("No messages yet.", DetailStyle.Meta); return document; }
            // Replies sit directly under the message they answer (oldest first among siblings), indented up to four levels.
            var loaded = rows.ToDictionary(r => Convert.ToInt64(r["id"]));
            var children = rows.Where(r => r["reply_to"] != null && loaded.ContainsKey(Convert.ToInt64(r["reply_to"]))).ToLookup(r => Convert.ToInt64(r["reply_to"]));
            Action<Dictionary<string, object>, int> visit = null;
            visit = (row, level) => { AppendMessage(document, row, level, loaded); foreach (var child in children[Convert.ToInt64(row["id"])]) visit(child, level + 1); };
            foreach (var root in rows.Where(r => r["reply_to"] == null || !loaded.ContainsKey(Convert.ToInt64(r["reply_to"])))) visit(root, 0);
            return document;
        }
        private static void AppendMessage(DetailDocument document, Dictionary<string, object> row, int level, Dictionary<long, Dictionary<string, object>> loaded)
        {
            bool reply = row["reply_to"] != null;
            if (reply) level = Math.Max(1, level);
            string to = Recipients(row), kind = Convert.ToString(row["kind"]).ToUpperInvariant(), status = Json.Text(row, "status");
            document.Add("#" + row["id"] + "  @" + row["author"] + (to == "" ? "" : "  →  " + to), DetailStyle.Author, level);
            document.Add(kind + (status == "" ? "" : " · " + status.ToUpperInvariant()) + "     " + Time(row["at"]), DetailStyle.Meta, level);
            if (reply)
            {
                Dictionary<string, object> parent;
                string reference = "↳ Reply to #" + row["reply_to"];
                if (loaded.TryGetValue(Convert.ToInt64(row["reply_to"]), out parent))
                {
                    string excerpt = Convert.ToString(parent["body"]).Replace("\r", " ").Replace("\n", " ");
                    if (excerpt.Length > 100) excerpt = excerpt.Substring(0, 100) + "…";
                    reference += " · @" + parent["author"] + ": " + excerpt;
                }
                else reference += " · Original message is not on this page";
                document.Add(reference, DetailStyle.Reply, level);
            }
            if (Json.Number(row, "open_ask") == 1) document.Add("Awaiting reply", DetailStyle.Warning, level);
            if (Json.Text(row, "visibility") == "held") document.Add("Held: " + row["held_reason"], DetailStyle.Warning, level);
            document.Add(Convert.ToString(row["body"]), DetailStyle.Body, level);
        }
        private static string Recipients(Dictionary<string, object> row)
        {
            var list = Json.Serializer().DeserializeObject(Convert.ToString(row["recipients"])) as object[];
            return list == null ? "" : String.Join(", ", list.Select(x => Convert.ToString(x)));
        }
        private static string AttentionText(Dictionary<string, object> row)
        {
            if (row == null) return "Select an escalation for its details and evidence. Acknowledge tells the room you saw it; Resolve closes it.";
            return "#" + row["id"] + "  " + Convert.ToString(row["severity"]).ToUpperInvariant() + " from @" + row["author"] + "  ·  " + Time(row["at"]) +
                "\r\nState: " + row["state"] + (Convert.ToInt64(row["handled_at"]) == 0 ? "" : " at " + Time(row["handled_at"])) +
                (row["task_id"] == null ? "" : "\r\nTicket #" + row["task_id"]) + "\r\n\r\n" + row["summary"] +
                (Convert.ToString(row["details"]) == "" ? "" : "\r\n\r\n" + row["details"]) +
                "\r\n\r\nEvidence messages: " + (Convert.ToString(row["evidence"]) == "[]" ? "none" : row["evidence"]);
        }
        private static string AuditText(Dictionary<string, object> row)
        {
            return row == null ? "Select an entry to see its full details." : "#" + row["id"] + "  " + Time(row["at"]) + "\r\n" + row["actor"] + "  ·  " + row["action"] + "\r\n\r\n" + row["details"];
        }
        private static string ArtifactText(Dictionary<string, object> row)
        {
            return row == null ? "Select a delivery to inspect its recorded file metadata." : "#" + row["id"] + "  " + row["title"] + "\r\nTicket #" + row["task_id"] + "  " + row["author"] + "  " + Time(row["created_at"]) + "\r\n" + row["description"] + "\r\n\r\n" + row["path"] + "\r\nSize: " + row["size"] + " bytes\r\nSHA256: " + row["sha256"] + "\r\n\r\nMetadata describes the file at registration time. Later edits are not automatically verified.";
        }
    }

    internal sealed class FieldsDialog : Form
    {
        private readonly TableLayoutPanel fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(14) };
        private readonly Dictionary<string, Control> inputs = new Dictionary<string, Control>();
        internal FieldsDialog(string title, int height = 520)
        {
            SuspendLayout(); Text = title; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); ClientSize = new Size(560, height);
            MinimumSize = new Size(480, 420); StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var save = new Button { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(save); buttons.Controls.Add(cancel); AcceptButton = save; CancelButton = cancel; Controls.Add(fields); Controls.Add(buttons); ResumeLayout(true);
        }
        internal void Add(string key, string title, string value, bool multiline = false)
        { AddControl(key, title, new TextBox { Text = value, Multiline = multiline, Height = multiline ? 88 : 28, Dock = DockStyle.Top, MaxLength = 8192, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None }); }
        internal void AddChoice(string key, string title, string value, params string[] choices)
        {
            var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top }; input.Items.AddRange(choices); input.SelectedItem = value; if (input.SelectedIndex < 0) input.SelectedIndex = 0; AddControl(key, title, input);
        }
        private void AddControl(string key, string title, Control control)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize)); fields.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 7, 0, 3) }); fields.RowStyles.Add(new RowStyle(SizeType.AutoSize)); fields.Controls.Add(control); inputs.Add(key, control);
        }
        internal Dictionary<string, object> Values() { var result = Json.Obj(); foreach (var pair in inputs) result[pair.Key] = pair.Value.Text.Trim(); return result; }
    }
}
