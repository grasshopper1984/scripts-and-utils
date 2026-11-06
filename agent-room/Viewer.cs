using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgentRoom
{
    internal sealed partial class Viewer : Form
    {
        private static readonly Color Ink = Color.FromArgb(29, 43, 62), Muted = Color.FromArgb(96, 108, 124), Canvas = Color.FromArgb(244, 246, 249), Line = Color.FromArgb(214, 221, 230);
        private static readonly Color Sidebar = Color.FromArgb(27, 37, 53), SidebarHover = Color.FromArgb(38, 51, 72), SidebarActive = Color.FromArgb(47, 64, 90), Accent = Color.FromArgb(58, 120, 220);
        private readonly RoomService service;
        private readonly RoomServer server;
        private readonly SmoothList threads = List(0, "Thread", "Messages", "Awaiting reply", "Last from", "Last activity", "State"), inbox = List(4, "ID", "From", "Kind", "Ticket", "Message", "Receipt");
        private readonly SmoothList tickets = List(1, "ID", "Title", "Importance", "Owner", "State", "Updated"), artifacts = List(1, "ID", "Title", "Ticket", "Delivered by", "Size", "Registered");
        private readonly SmoothList agents = List(4, "Agent", "Role", "Status", "Last contact", "Restriction"), audit = List(3, "ID", "Actor", "Action", "Details");
        private readonly SmoothList attention = List(3, "ID", "From", "Severity", "Summary", "Ticket", "State", "Raised");
        private readonly DetailView threadDetails = new DetailView(), inboxDetails = new DetailView(), ticketDetails = new DetailView(), graphDetails = new DetailView();
        private readonly TextBox artifactDetails = ReadBox(), agentDetails = ReadBox(), auditDetails = ReadBox(), workspaceDetails = ReadBox(), attentionDetails = ReadBox();
        private readonly TextBox focusNumber = new TextBox { Width = 60 };
        private readonly ComboBox receiptMode = Choice(100, "Unread", "All"), inboxAgentChoice = Choice(140, "(no agents)"), attentionMode = Choice(130, "Open", "Unresolved", "All");
        private readonly ComboBox stateFilter = Choice(115, "all", "unfinished", "open", "active", "blocked", "paused", "review", "done", "cancelled");
        private readonly ComboBox agentFilter = Choice(130, "All agents"), priorityFilter = Choice(155, "Any importance", "High +", "Critical"), updatedFilter = Choice(125, "Any time", "Today", "Last 7 days", "Last 30 days");
        private readonly ComboBox graphLimit = Choice(65, "40", "60"), graphZoom = Choice(75, "100%", "80%", "60%");
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(16, 5, 0, 0), ForeColor = Muted, BackColor = Canvas };
        private readonly Label roomState = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(190, 201, 216), Padding = new Padding(4, 0, 0, 0) };
        private readonly Label graphSummary = new Label { Dock = DockStyle.Top, Height = 26, Padding = new Padding(2, 4, 0, 0), ForeColor = Muted };
        private readonly Button pause = new Button { Text = "Pause room", Dock = DockStyle.Bottom, Height = 36, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Accent };
        private readonly Button resetChats = new Button { Text = "Reset chats...", Dock = DockStyle.Bottom, Height = 32, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(205, 214, 226), BackColor = Sidebar };
        private readonly GraphView graph = new GraphView();
        private readonly FlowLayoutPanel filterBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 2, 0, 0) };
        private readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
        private readonly Panel navigation = new Panel { Dock = DockStyle.Left, Width = 196, BackColor = Sidebar };
        private readonly Dictionary<string, PageView> pages = new Dictionary<string, PageView>();
        private readonly Dictionary<ListView, string> shownRows = new Dictionary<ListView, string>();
        private readonly Timer refresh = new Timer { Interval = 1000 };
        private readonly NotifyIcon tray = new NotifyIcon { Text = "Agent Room" };
        private readonly Icon appIcon = LoadAppIcon(SystemInformation.IconSize), trayIcon = LoadAppIcon(SystemInformation.SmallIconSize);
        private readonly Dictionary<string, long> before = new Dictionary<string, long>();
        private Dictionary<string, object> filters = Json.Obj();
        private Font unreadFont;
        // Highest escalation already announced; -1 until the first check so startup shows one summary instead of a burst.
        private long alertedEscalation = -1;
        private int attentionCheck;
        private bool checkingAttention;
        private PageView current;
        private long displayedRevision = -1, graphFocus;
        private int viewEpoch, detailsEpoch;
        private DateTime lastRefresh;
        private bool loading, quitting, paused, rendering;
        private string inboxAgent = "", artifactDirectory = "", memberSignature = "", graphSignature = "";

        private sealed class PageView
        {
            internal string Name;
            internal Panel Root, Heading, FilterSlot;
            internal FlowLayoutPanel Tools;
            internal Button Nav;
        }

        internal Viewer(RoomService service, RoomServer server)
        {
            SuspendLayout(); this.service = service; this.server = server;
            Text = "Agent Room"; AccessibleName = "Local agent project room"; Icon = appIcon; tray.Icon = trayIcon;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); Font = new Font("Segoe UI", 10);
            ClientSize = new Size(1240, 820); MinimumSize = new Size(1040, 720); StartPosition = FormStartPosition.CenterScreen; BackColor = Canvas;

            // Each page owns its header, toolbar, and optional filter row. Switching pages swaps one panel,
            // so no shared control appears, disappears, or wraps and pushes the content around.
            var attentionPage = NewPage("Attention", "Attention", "Escalations from the moderator and coordinator. New ones also appear as desktop alerts.", false);
            attentionPage.Tools.Controls.Add(Caption("Show")); attentionPage.Tools.Controls.Add(attentionMode);
            AddButton(attentionPage.Tools, "Acknowledge", delegate { HandleEscalation("acknowledged"); }, true); AddButton(attentionPage.Tools, "Resolve", delegate { HandleEscalation("resolved"); });
            AddButton(attentionPage.Tools, "Show ticket", delegate { var row = Selected(attention); if (row != null && row["task_id"] != null) FocusGraph(Convert.ToInt64(row["task_id"])); });
            AddPaging(attentionPage.Tools, "Attention", attention);
            Assemble(attentionPage, Split(attention, attentionDetails, .55));
            attentionMode.SelectedIndexChanged += delegate { if (!rendering) { before["Attention"] = Int64.MaxValue; Reload(); } };

            // The OP watches the room and steers through the coordinator's own session, so room views are read-only.
            // Each ticket keeps its own thread, so conversations never clutter the project graph.
            var threadsPage = NewPage("Threads", "Threads", "One conversation per ticket, plus the room thread for messages without one. Viewing never acknowledges anything.", false);
            AddButton(threadsPage.Tools, "Show in graph", delegate { var row = Selected(threads); if (row != null && Convert.ToInt64(row["task_id"]) != 0) FocusGraph(Convert.ToInt64(row["task_id"])); });
            threadsPage.Tools.Controls.Add(new Label { Width = 14 });
            AddButton(threadsPage.Tools, "Newest", delegate { before["Threads"] = Int64.MaxValue; Reload(); });
            AddButton(threadsPage.Tools, "Older", delegate { if (threads.Items.Count != 0) { before["Threads"] = threads.Items.Cast<ListViewItem>().Min(item => Convert.ToInt64(((Dictionary<string, object>)item.Tag)["last_id"])); Reload(); } });
            Assemble(threadsPage, Split(threads, threadDetails, .28));

            var inboxPage = NewPage("Inbox", "Inbox", "What each agent still has to read or answer. Inspecting an inbox never acknowledges for the agent.", false);
            inboxPage.Tools.Controls.Add(Caption("For")); inboxPage.Tools.Controls.Add(inboxAgentChoice); inboxPage.Tools.Controls.Add(receiptMode);
            AddButton(inboxPage.Tools, "Assigned tasks", delegate { rendering = true; try { agentFilter.SelectedItem = inboxAgent; } finally { rendering = false; } ShowPage("Tasks", false); ApplyFilters(); });
            AddPaging(inboxPage.Tools, "Inbox", inbox);
            Assemble(inboxPage, Split(inbox, inboxDetails, .6));
            inboxAgentChoice.SelectedIndexChanged += delegate { if (!rendering) { inboxAgent = inboxAgentChoice.Text; before["Inbox"] = Int64.MaxValue; Reload(); } };
            receiptMode.SelectedIndexChanged += delegate { if (!rendering) { before["Inbox"] = Int64.MaxValue; Reload(); } };

            var tasksPage = NewPage("Tasks", "Tasks", "Tickets the coordinator plans and workers claim. Dependencies must finish before a claim.", true);
            AddButton(tasksPage.Tools, "Show in graph", delegate { var row = Selected(tickets); if (row != null) FocusGraph(Convert.ToInt64(row["id"])); });
            AddButton(tasksPage.Tools, "Reset budget...", ResetBudget); AddButton(tasksPage.Tools, "Release worktree...", ReleaseWorktree);
            AddPaging(tasksPage.Tools, "Tasks", tickets);
            Assemble(tasksPage, Split(tickets, ticketDetails, .6));

            var graphPage = NewPage("Graph", "Graph", "Newest activity first within link order. Solid arrows: dependencies; dashed: context. Double-click to focus.", true);
            graphPage.Tools.Controls.Add(Caption("Task #")); graphPage.Tools.Controls.Add(focusNumber);
            AddButton(graphPage.Tools, "Focus", delegate { long id; if (OptionalId(focusNumber.Text, out id)) FocusGraph(id); });
            AddButton(graphPage.Tools, "Neighbors of selected", delegate { if (graph.SelectedTask != 0) FocusGraph(graph.SelectedTask); }); AddButton(graphPage.Tools, "Whole project", delegate { FocusGraph(0); });
            graphPage.Tools.Controls.Add(Caption("Limit")); graphPage.Tools.Controls.Add(graphLimit); graphPage.Tools.Controls.Add(Caption("Zoom")); graphPage.Tools.Controls.Add(graphZoom);
            var graphArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.White }; graphArea.Controls.Add(graph); graphArea.Controls.Add(graphSummary); graphSummary.BackColor = Color.White;
            Assemble(graphPage, Split(graphArea, graphDetails, .7));
            graphLimit.SelectedIndexChanged += delegate { if (!rendering) Reload(); }; graphZoom.SelectedIndexChanged += delegate { graph.SetZoom(graphZoom.SelectedIndex == 0 ? 1 : graphZoom.SelectedIndex == 1 ? .8f : .6f); };
            graphZoom.SelectedIndex = 1;
            graph.SelectedTaskChanged += delegate(long id) { LoadTaskDetails(id, graphDetails); }; graph.FocusRequested += FocusGraph;

            var artifactsPage = NewPage("Artifacts", "Artifacts", "Registered deliveries with their size and SHA256 at registration time.", false);
            AddButton(artifactsPage.Tools, "Show folder", delegate { var row = Selected(artifacts); if (row != null) OpenFolder(Path.GetDirectoryName((string)row["path"])); });
            AddPaging(artifactsPage.Tools, "Artifacts", artifacts);
            Assemble(artifactsPage, Split(artifacts, artifactDetails, .6));

            var agentsPage = NewPage("Agents", "Agents", "Register the coordinator, moderator, and workers. Status is self-reported.", false);
            AddButton(agentsPage.Tools, "Register agent...", Enroll, true); AddButton(agentsPage.Tools, "Edit agent...", EditRole); AddButton(agentsPage.Tools, "Copy join instructions", CopyConnection);
            AddButton(agentsPage.Tools, "Moderate...", Moderate);
            AddMenu(agentsPage.Tools, "More", "Copy config only", (Action)CopyConfigOnly, "Renew profile...", (Action)RenewProfile, "Reset session...", (Action)ResetSession, "Deregister...", (Action)Revoke);
            Assemble(agentsPage, Split(agents, agentDetails, .6));
            agents.DoubleClick += delegate { OpenSelectedInbox(); };

            var moderationPage = NewPage("Moderation", "Moderation", "Audit of every room decision. Your restrictions override the moderator's.", false);
            AddButton(moderationPage.Tools, "Override...", Moderate); AddPaging(moderationPage.Tools, "Moderation", audit);
            Assemble(moderationPage, Split(audit, auditDetails, .6));

            var workspacePage = NewPage("Workspace", "Workspace", "Repository, delivery directory, and where this room keeps its data.", false);
            AddButton(workspacePage.Tools, "Repository...", ConfigureRepository); AddButton(workspacePage.Tools, "Delivery directory...", ConfigureArtifacts);
            AddButton(workspacePage.Tools, "Customize playbooks", CustomizePlaybooks); AddButton(workspacePage.Tools, "Restart hub", RestartHub);
            AddMenu(workspacePage.Tools, "More", "Open delivery folder", (Action)delegate { OpenFolder(artifactDirectory); }, "Open room data", (Action)delegate { OpenFolder(service.DirectoryPath); });
            Assemble(workspacePage, Card(workspaceDetails));

            // Parent the shared filter row before layout scaling; it moves between the Tasks and Graph pages later.
            filterBar.Parent = tasksPage.FilterSlot;
            filterBar.Controls.Add(Caption("Show")); filterBar.Controls.Add(stateFilter); filterBar.Controls.Add(agentFilter); filterBar.Controls.Add(priorityFilter);
            filterBar.Controls.Add(Caption("Updated")); filterBar.Controls.Add(updatedFilter); AddButton(filterBar, "Clear filters", ClearFilters);
            foreach (var combo in new[] { stateFilter, agentFilter, priorityFilter, updatedFilter }) combo.SelectedIndexChanged += delegate { if (!rendering) ApplyFilters(); };
            stateFilter.AccessibleName = "Task status"; agentFilter.AccessibleName = "Task agent"; priorityFilter.AccessibleName = "Minimum importance"; updatedFilter.AccessibleName = "Updated within";

            BuildNavigation();
            foreach (var page in pages.Values) before[page.Name] = Int64.MaxValue;
            Controls.Add(content); Controls.Add(status); Controls.Add(navigation);

            threads.SelectedIndexChanged += delegate { if (!rendering) LoadThread(Selected(threads)); }; inbox.SelectedIndexChanged += delegate { if (!rendering) LoadMessageDetails(inbox, inboxDetails); };
            tickets.SelectedIndexChanged += delegate { if (!rendering) { var row = Selected(tickets); if (row != null) LoadTaskDetails(Convert.ToInt64(row["id"]), ticketDetails); else SetText(ticketDetails, ""); } };
            artifacts.SelectedIndexChanged += delegate { if (!rendering) SetText(artifactDetails, ArtifactText(Selected(artifacts))); };
            agents.SelectedIndexChanged += delegate { if (!rendering) SetText(agentDetails, AgentText(Selected(agents))); };
            audit.SelectedIndexChanged += delegate { if (!rendering) SetText(auditDetails, AuditText(Selected(audit))); };
            attention.SelectedIndexChanged += delegate { if (!rendering) SetText(attentionDetails, AttentionText(Selected(attention))); };

            var trayMenu = new ContextMenuStrip(); trayMenu.Items.Add("Open Agent Room", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); }); trayMenu.Items.Add("Restart hub", null, delegate { RestartHub(); }); trayMenu.Items.Add("Exit hub", null, delegate { quitting = true; Close(); }); tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
            tray.BalloonTipClicked += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); ShowPage("Attention"); };
            // Escalation alerts must reach the OP while the window sits hidden in the tray.
            refresh.Tick += delegate { if (Visible) RefreshRoom(); else if (++attentionCheck % 3 == 0) CheckAttention(); };
            Shown += delegate { SizeColumns(); tray.Visible = true; refresh.Start(); RefreshRoom(); }; DpiChanged += delegate { SizeColumns(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
            ShowPage("Attention", false); ResumeLayout(true);
        }

        private PageView NewPage(string name, string title, string hint, bool filtered)
        {
            var page = new PageView { Name = name, Root = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(20, 12, 20, 10), BackColor = Canvas } };
            page.Heading = new Panel { Height = 58 };
            page.Heading.Controls.Add(new Label { Text = hint, Dock = DockStyle.Top, Height = 22, ForeColor = Muted, AutoEllipsis = true });
            page.Heading.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI Semibold", 15), ForeColor = Ink });
            page.Tools = new FlowLayoutPanel { Height = 42, WrapContents = false, Padding = new Padding(0, 2, 0, 0) };
            if (filtered) page.FilterSlot = new Panel { Height = 40 };
            page.Nav = new Button { Text = title, Height = 40, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(205, 214, 226), BackColor = Sidebar, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0), Cursor = Cursors.Hand };
            page.Nav.FlatAppearance.BorderSize = 0; page.Nav.FlatAppearance.MouseOverBackColor = SidebarHover; page.Nav.FlatAppearance.MouseDownBackColor = SidebarActive;
            page.Nav.Click += delegate { ShowPage(name); };
            page.Nav.Paint += delegate(object sender, PaintEventArgs e) { if (current == page) using (var brush = new SolidBrush(Accent)) e.Graphics.FillRectangle(brush, 0, 6, 4, page.Nav.Height - 12); };
            content.Controls.Add(page.Root); pages.Add(name, page);
            return page;
        }
        private static void Assemble(PageView page, Control body, Control bottom = null)
        {
            // WinForms docks the most recently added control first: add the body, then the bottom, then the rows from bottom to top.
            page.Root.Controls.Add(body);
            if (bottom != null) { bottom.Dock = DockStyle.Bottom; page.Root.Controls.Add(bottom); }
            var rows = new List<Control> { page.Heading, page.Tools };
            if (page.FilterSlot != null) rows.Add(page.FilterSlot);
            rows.Add(new Panel { Height = 6 });
            for (int i = rows.Count - 1; i >= 0; i--) { rows[i].Dock = DockStyle.Top; page.Root.Controls.Add(rows[i]); }
        }
        private void BuildNavigation()
        {
            var list = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            foreach (var page in pages.Values.Reverse()) { page.Nav.Dock = DockStyle.Top; list.Controls.Add(page.Nav); }
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 196, Padding = new Padding(14, 0, 14, 16) };
            pause.FlatAppearance.BorderColor = Accent; pause.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 104, 196);
            resetChats.FlatAppearance.BorderColor = Color.FromArgb(78, 96, 124); resetChats.FlatAppearance.MouseOverBackColor = SidebarHover;
            pause.Click += delegate { Run("moderate", Json.Obj("action", paused ? "resume" : "pause", "reason", paused ? "Resumed by the OP" : "Paused by the OP")); };
            resetChats.Click += delegate { ResetChats(); };
            // Dock order: the last added control docks first, so Reset sits at the bottom below Pause.
            footer.Controls.Add(roomState); footer.Controls.Add(pause); footer.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 8 }); footer.Controls.Add(resetChats);
            var title = new Label { Text = "Agent Room", Dock = DockStyle.Top, Height = 64, Padding = new Padding(16, 18, 0, 0), Font = new Font("Segoe UI Semibold", 15), ForeColor = Color.White };
            navigation.Controls.Add(list); navigation.Controls.Add(footer); navigation.Controls.Add(title);
        }
        private string CurrentView { get { return current == null ? "Threads" : current.Name; } }
        private void ShowPage(string name, bool reload = true)
        {
            var next = pages[name];
            if (current != next)
            {
                content.SuspendLayout();
                var previous = current; current = next;
                if (next.FilterSlot != null) filterBar.Parent = next.FilterSlot;
                next.Root.Visible = true; next.Root.BringToFront();
                if (previous != null) { previous.Root.Visible = false; previous.Nav.BackColor = Sidebar; previous.Nav.ForeColor = Color.FromArgb(205, 214, 226); previous.Nav.Invalidate(); }
                next.Nav.BackColor = SidebarActive; next.Nav.ForeColor = Color.White;
                content.ResumeLayout(true);
            }
            if (reload) Reload();
        }
        private void Reload() { viewEpoch++; detailsEpoch++; displayedRevision = -1; if (IsHandleCreated) RefreshRoom(); }
        private Dictionary<string, object> ViewArguments(string view)
        {
            var args = new Dictionary<string, object>(filters); if (before.ContainsKey(view)) args["before"] = before[view];
            if (view == "Graph") { args["focus"] = graphFocus; args["limit"] = graphLimit.SelectedIndex == 0 ? 40 : 60; }
            if (view == "Inbox") args = Json.Obj("before", before[view], "agent", inboxAgent, "receipts", receiptMode.SelectedIndex == 0 ? "unread" : "all");
            if (view == "Threads") args = Json.Obj("before", before[view]);
            if (view == "Attention") args = Json.Obj("before", before[view], "state", new[] { "open", "unresolved", "all" }[Math.Max(0, attentionMode.SelectedIndex)]);
            return args;
        }
        private Dictionary<string, object> Snapshot(string view, Dictionary<string, object> args)
        {
            var result = Json.Obj("members", service.Admin("members")["result"], "status", service.Admin("status")["result"],
                "open", service.Admin("escalations", Json.Obj("state", "open"))["result"]);
            if (view == "Inbox" && inboxAgent == "") return result;
            string cmd = view == "Attention" ? "escalations" : view == "Threads" ? "threads" : view == "Inbox" ? "agent.inbox" : view == "Tasks" ? "tasks" : view == "Graph" ? "graph" : view == "Artifacts" ? "artifacts" : view == "Moderation" ? "audit" : "";
            if (cmd != "") result["data"] = service.Admin(cmd, args)["result"]; return result;
        }
        private async void RefreshRoom()
        {
            if (loading || (displayedRevision == service.Revision && (DateTime.UtcNow - lastRefresh).TotalSeconds < 10)) return;
            loading = true; long requestedRevision = service.Revision; int epoch = viewEpoch; string view = CurrentView; var args = ViewArguments(view);
            try { var snapshot = await Task.Run(() => Snapshot(view, args)); if (IsDisposed || epoch != viewEpoch) return; ShowSnapshot(view, snapshot); displayedRevision = requestedRevision; lastRefresh = DateTime.UtcNow; }
            catch (Exception e) { if (!IsDisposed && epoch == viewEpoch) status.Text = e is RoomError ? e.Message : "Unable to refresh the room."; }
            finally { loading = false; if (!IsDisposed && epoch != viewEpoch) RefreshRoom(); }
        }
        internal void LoadPreview() { LoadPreview("Threads", "op", null); }
        // Opens one ticket's thread synchronously so previews can capture the transcript layout.
        internal void LoadPreviewThread(long taskId)
        {
            LoadPreview("Threads", "op", null);
            rendering = true;
            try { foreach (ListViewItem item in threads.Items) item.Selected = Convert.ToInt64(((Dictionary<string, object>)item.Tag)["task_id"]) == taskId; }
            finally { rendering = false; }
            threadDetails.Render(ThreadDocument((Dictionary<string, object>)service.Admin("thread", Json.Obj("task_id", taskId))["result"]), "thread:" + taskId);
        }
        internal void LoadPreview(string view, string agent, Dictionary<string, object> filter)
        {
            viewEpoch++;
            rendering = true; try { inboxAgent = agent; if (filter != null) filters = filter; ShowPage(view, false); } finally { rendering = false; }
            SizeColumns(); ShowSnapshot(view, Snapshot(view, ViewArguments(view)));
        }
        private void ShowSnapshot(string view, Dictionary<string, object> snapshot)
        {
            detailsEpoch++;
            rendering = true;
            try
            {
                var people = Rows(snapshot["members"]);
                string signature = String.Join(",", people.Select(p => (string)p["id"]));
                if (signature != memberSignature)
                {
                    memberSignature = signature;
                    Repopulate(agentFilter, "All agents", people, agentFilter.Text);
                    // The OP is not addressable, so only agents have inboxes worth inspecting.
                    Repopulate(inboxAgentChoice, null, people.Where(p => (string)p["id"] != "op").ToList(), inboxAgent);
                }
                if (inboxAgent == "" && inboxAgentChoice.Items.Count != 0) inboxAgent = (string)inboxAgentChoice.Items[0];
                if (inboxAgentChoice.Text != inboxAgent) inboxAgentChoice.SelectedItem = inboxAgent;
                var room = (Dictionary<string, object>)snapshot["status"]; paused = (bool)room["paused"]; artifactDirectory = (string)room["artifact_directory"];
                pause.Text = paused ? "Resume room" : "Pause room";
                bool moderator = people.Any(r => (string)r["permission"] == "moderator" && Convert.ToInt64(r["revoked"]) == 0 && Convert.ToInt64(r["last_seen"]) != 0);
                roomState.Text = (server.Healthy ? "● Hub running locally" : "● Hub error") + "\r\n" +
                    (paused ? "● Deliveries paused" + ((string)room["paused_by"] == "" ? "" : " by " + room["paused_by"]) : "● Inbox delivery on") + "\r\n" +
                    (moderator ? "● Moderator joined" : "○ No moderator joined") + "\r\n" +
                    (Rows(snapshot["open"]).Count == 0 ? "● Nothing needs you" : "▲ " + Rows(snapshot["open"]).Count + " escalation(s) open");
                ShowAttention(Rows(snapshot["open"]));
                SetText(workspaceDetails, "Main checkout\r\n" + Empty((string)room["repository"]) + "\r\n\r\nArtifact delivery directory\r\n" + Empty(artifactDirectory) + "\r\n\r\nRoom database and profiles\r\n" + service.DirectoryPath + "\r\n\r\nRole playbooks\r\n" + PlaybookSummary() + "\r\n\r\nCode and history stay in Git. Editing agents reserve separate worktrees.\r\nDeliver files into the artifact directory, then register them against a ticket.\r\nRegistration records the path, size, and SHA256; it does not copy or freeze a file.\r\nThe coordinator integrates work through its own harness.");
                if (view == "Agents")
                {
                    foreach (var person in people)
                    {
                        person["contact"] = Convert.ToInt64(person["last_seen"]) == 0 ? "never" : Time(person["last_seen"]);
                        person["limit"] = person["restriction"] == null ? "" : person["restriction"] + " until " + Clock(person["until_at"]) + ((string)person["imposed_by"] == "op" ? " (OP)" : "");
                    }
                    Fill(agents, people, "id", "permission", "status", "contact", "limit");
                }
                bool hasData = snapshot.ContainsKey("data");
                if (view == "Inbox" && !hasData) Fill(inbox, new List<Dictionary<string, object>>(), "id");
                if (hasData && view == "Graph")
                {
                    var data = (Dictionary<string, object>)snapshot["data"];
                    string drawn = Json.Write(data);
                    if (drawn != graphSignature) { graphSignature = drawn; graph.SetGraph(data); }
                    graphSummary.Text = "Showing " + Rows(data["nodes"]).Count + " of " + data["total"] + (graphFocus == 0 ? " matching tasks" : " tasks around #" + graphFocus) + ((bool)data["truncated"] ? " · limit reached" : "");
                }
                else if (hasData)
                {
                    var rows = Rows(snapshot["data"]);
                    foreach (var row in rows) if (row.ContainsKey("kind")) row["label"] = KindLabel(row);
                    if (view == "Attention") { foreach (var row in rows) row["raised"] = Time(row["at"]); Fill(attention, rows, "id", "author", "severity", "summary", "task_id", "state", "raised"); }
                    if (view == "Threads")
                    {
                        foreach (var row in rows)
                        {
                            long task = Convert.ToInt64(row["task_id"]);
                            // Selection follows the thread, not its newest message, so new activity keeps the row selected.
                            row["id"] = task; row["name"] = task == 0 ? "Room (no ticket)" : "#" + task + "  " + row["title"];
                            row["last"] = Time(row["last_at"]); row["phase"] = task == 0 ? "" : row["state"] + (row["owner"] == null ? "" : " · " + row["owner"]);
                        }
                        Fill(threads, rows, "name", "messages", "open_asks", "last_author", "last", "phase");
                    }
                    if (view == "Inbox") { foreach (var row in rows) row["receipt"] = row["acknowledged"] == null ? "Unread" : "Acknowledged"; Fill(inbox, rows, "id", "author", "label", "task_id", "body", "receipt"); }
                    if (view == "Tasks") { foreach (var row in rows) { row["importance"] = Importance(row["priority"]); row["date"] = Time(row["updated_at"]); } Fill(tickets, rows, "id", "title", "importance", "owner", "state", "date"); }
                    if (view == "Artifacts") { foreach (var row in rows) { row["bytes"] = Bytes(Convert.ToInt64(row["size"])); row["date"] = Time(row["created_at"]); } Fill(artifacts, rows, "id", "title", "task_id", "author", "bytes", "date"); }
                    if (view == "Moderation") Fill(audit, rows, "id", "actor", "action", "details");
                }
            }
            finally { rendering = false; }
            if (view == "Threads") LoadThread(Selected(threads)); if (view == "Inbox") LoadMessageDetails(inbox, inboxDetails);
            if (view == "Tasks") { var row = Selected(tickets); if (row == null) SetText(ticketDetails, "Select a ticket for its objective, links, checkpoint, and recent messages."); else LoadTaskDetails(Convert.ToInt64(row["id"]), ticketDetails); }
            if (view == "Graph") { if (graph.SelectedTask == 0) SetText(graphDetails, "Select a task for details. Double-click it to focus its neighbors."); else LoadTaskDetails(graph.SelectedTask, graphDetails); }
            if (view == "Artifacts") SetText(artifactDetails, ArtifactText(Selected(artifacts)));
            if (view == "Agents") SetText(agentDetails, AgentText(Selected(agents)));
            if (view == "Moderation") SetText(auditDetails, AuditText(Selected(audit)));
            if (view == "Attention") SetText(attentionDetails, AttentionText(Selected(attention)));
        }
        private async void CheckAttention()
        {
            if (checkingAttention) return; checkingAttention = true;
            try { var rows = await Task.Run(() => Rows(service.Admin("escalations", Json.Obj("state", "open"))["result"])); if (!IsDisposed) ShowAttention(rows); }
            catch (Exception) { }
            finally { checkingAttention = false; }
        }
        private void ShowAttention(List<Dictionary<string, object>> open)
        {
            string title = open.Count == 0 ? "Attention" : "Attention  (" + (open.Count >= 20 ? "20+" : open.Count.ToString()) + ")";
            if (pages["Attention"].Nav.Text != title) pages["Attention"].Nav.Text = title;
            long newest = open.Count == 0 ? 0 : open.Max(r => Convert.ToInt64(r["id"]));
            var fresh = open.Where(r => Convert.ToInt64(r["id"]) > alertedEscalation).ToList();
            bool startup = alertedEscalation < 0;
            alertedEscalation = Math.Max(alertedEscalation, newest);
            if (fresh.Count == 0 || !tray.Visible) return;
            var latest = fresh.OrderByDescending(r => Convert.ToInt64(r["id"])).First();
            string severity = (string)latest["severity"];
            if (startup) tray.ShowBalloonTip(10000, "Agent Room needs you", open.Count + " escalation(s) are open.", ToolTipIcon.Warning);
            else tray.ShowBalloonTip(10000, (fresh.Count > 1 ? fresh.Count + " escalations · " : "") + severity + " from @" + latest["author"], (string)latest["summary"],
                severity == "urgent" ? ToolTipIcon.Error : severity == "warning" ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        private static string KindLabel(Dictionary<string, object> row)
        {
            string label = (string)row["kind"];
            if (row.ContainsKey("status") && Convert.ToString(row["status"]) != "") label += " · " + row["status"];
            if (row.ContainsKey("reply_to") && row["reply_to"] != null) label += " ↩" + row["reply_to"];
            return label;
        }
        private async void LoadMessageDetails(ListView list, DetailView output)
        {
            var row = Selected(list); if (row == null) { detailsEpoch++; SetText(output, "Select a message to inspect its references and receipts. Viewing does not acknowledge it."); return; }
            await LoadDetails("message", Json.Obj("message_id", row["id"]), output, MessageDocument);
        }
        private async void LoadTaskDetails(long id, DetailView output)
        {
            int epoch = ++detailsEpoch;
            try
            {
                // A ticket's details include its whole thread, read in the same background step.
                var parts = await Task.Run(() => new[] { (Dictionary<string, object>)service.Admin("task", Json.Obj("task_id", id))["result"],
                    (Dictionary<string, object>)service.Admin("thread", Json.Obj("task_id", id))["result"] });
                if (!IsDisposed && epoch == detailsEpoch) output.Render(ThreadDocument(parts[1], TaskText(parts[0])), "task:" + id);
            }
            catch (Exception e) { if (!IsDisposed && epoch == detailsEpoch) SetText(output, e is RoomError ? e.Message : "Details unavailable."); }
        }
        private async void LoadThread(Dictionary<string, object> row)
        {
            if (row == null) { detailsEpoch++; SetText(threadDetails, "Select a thread to read it from the start. Replies are indented under the message they answer."); return; }
            await LoadDetails("thread", Json.Obj("task_id", row["task_id"]), threadDetails, ThreadDocument);
        }
        private async Task LoadDetails(string cmd, Dictionary<string, object> args, DetailView output, Func<Dictionary<string, object>, DetailDocument> format)
        {
            int epoch = ++detailsEpoch;
            try { var response = await Task.Run(() => service.Admin(cmd, args)); if (!IsDisposed && epoch == detailsEpoch) output.Render(format((Dictionary<string, object>)response["result"]), cmd + ":" + Json.Canonical(args)); }
            catch (Exception e) { if (!IsDisposed && epoch == detailsEpoch) SetText(output, e is RoomError ? e.Message : "Details unavailable."); }
        }
        private async void Run(string operation, Dictionary<string, object> args, Action<Dictionary<string, object>> after = null)
        {
            try { var response = await Task.Run(() => service.Admin(operation, args)); if (IsDisposed) return; if (after != null) after((Dictionary<string, object>)response["result"]); Reload(); }
            catch (Exception e) { if (!IsDisposed) MessageBox.Show(this, e is RoomError ? e.Message : "The operation could not be completed.", "Agent Room", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        private void ApplyFilters()
        {
            int index = updatedFilter.SelectedIndex;
            long since = index <= 0 ? 0 : new DateTimeOffset(DateTime.Today.AddDays(index == 1 ? 0 : index == 2 ? -6 : -29)).ToUnixTimeSeconds();
            filters = Json.Obj("state", stateFilter.Text, "agent", agentFilter.SelectedIndex <= 0 ? "" : agentFilter.Text, "min_priority", priorityFilter.SelectedIndex, "since", since, "until", Int64.MaxValue);
            before["Tasks"] = Int64.MaxValue; Reload();
        }
        private void ClearFilters()
        {
            rendering = true; try { stateFilter.SelectedIndex = 0; agentFilter.SelectedIndex = 0; priorityFilter.SelectedIndex = 0; updatedFilter.SelectedIndex = 0; } finally { rendering = false; }
            ApplyFilters();
        }
        private void FocusGraph(long id) { graphFocus = id; focusNumber.Text = id == 0 ? "" : id.ToString(); ShowPage("Graph"); }
        private void OpenSelectedInbox()
        {
            var row = Selected(agents); if (row == null || (string)row["id"] == "op") return;
            inboxAgent = (string)row["id"]; before["Inbox"] = Int64.MaxValue;
            rendering = true; try { inboxAgentChoice.SelectedItem = inboxAgent; } finally { rendering = false; }
            ShowPage("Inbox");
        }
        private void AddPaging(Control bar, string view, ListView list)
        {
            bar.Controls.Add(new Label { Width = 14 });
            AddButton(bar, "Newest", delegate { before[view] = Int64.MaxValue; Reload(); });
            AddButton(bar, "Older", delegate { if (list.Items.Count != 0) { before[view] = list.Items.Cast<ListViewItem>().Min(item => Convert.ToInt64(((Dictionary<string, object>)item.Tag)["id"])); Reload(); } });
        }
        private void SizeColumns()
        {
            float scale; using (Graphics graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
            SetWidths(agents, scale, 130, 110, 120, 150, 200); SetWidths(attention, scale, 50, 110, 80, 300, 60, 105, 150);
            SetWidths(threads, scale, 320, 85, 120, 120, 150, 140); SetWidths(inbox, scale, 50, 105, 140, 60, 300, 110);
            SetWidths(tickets, scale, 50, 300, 100, 110, 85, 150); SetWidths(audit, scale, 50, 110, 140, 400); SetWidths(artifacts, scale, 50, 260, 65, 115, 90, 150);
        }
        private static void SetWidths(SmoothList view, float scale, params int[] widths)
        {
            for (int i = 0; i < widths.Length; i++) view.Columns[i].Width = (int)(widths[i] * scale);
            view.MinimumStretch = (int)(160 * scale); view.Stretch();
        }
        private static void Repopulate(ComboBox combo, string first, List<Dictionary<string, object>> people, string chosen)
        {
            combo.BeginUpdate();
            try
            {
                combo.Items.Clear(); if (first != null) combo.Items.Add(first);
                foreach (var person in people) if (Convert.ToInt64(person["revoked"]) == 0) combo.Items.Add((string)person["id"]);
                combo.SelectedItem = chosen; if (combo.SelectedIndex < 0 && combo.Items.Count != 0) combo.SelectedIndex = 0;
            }
            finally { combo.EndUpdate(); }
        }
        private static void SetText(TextBoxBase box, string text)
        {
            // Reassigning identical text would reset the caret and scroll position on every refresh.
            var detail = box as DetailView;
            if (detail != null) detail.ShowText(text); else if (box.Text != text) box.Text = text;
        }
        private static string Empty(string text) { return text == "" ? "Not configured" : text; }
        private static string Importance(object value) { return new[] { "Normal", "High", "Critical" }[(int)Convert.ToInt64(value)]; }
        private static string Bytes(long bytes) { return bytes < 1024 ? bytes + " B" : bytes < 1048576 ? (bytes / 1024.0).ToString("0.#") + " KiB" : (bytes / 1048576.0).ToString("0.#") + " MiB"; }
        private string SelectedAgent() { var row = Selected(agents); if (row == null || (string)row["id"] == "op") { status.Text = "Select an enrolled agent first."; return null; } return (string)row["id"]; }
        private static string[] Split(string text) { return text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimStart('@', '#')).ToArray(); }
        private static string Time(object value) { return value == null || Convert.ToInt64(value) == 0 ? "unknown" : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value)).LocalDateTime.ToString("g"); }
        private static string Clock(object value) { return value == null ? "" : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value)).LocalDateTime.ToString("t"); }
        private static List<Dictionary<string, object>> Rows(object value) { return (List<Dictionary<string, object>>)value; }
        private static Dictionary<string, object> Selected(ListView list) { return list.SelectedItems.Count == 0 ? null : (Dictionary<string, object>)list.SelectedItems[0].Tag; }
        private static SmoothList List(int stretch, params string[] columns) { var list = new SmoothList { StretchColumn = stretch }; foreach (string title in columns) list.Columns.Add(title, 100); return list; }
        private static TextBox ReadBox() { return new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill }; }
        private static ComboBox Choice(int width, params string[] values) { var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(0, 4, 6, 0) }; combo.Items.AddRange(values); combo.SelectedIndex = 0; return combo; }
        private static Label Caption(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(8, 8, 4, 0), ForeColor = Muted }; }
        private static Button AddButton(Control parent, string text, Action action, bool primary = false)
        {
            // Every toolbar button shares one flat style so primary and secondary buttons line up at the same height.
            var button = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(0, 2, 6, 0), Padding = new Padding(6, 0, 6, 0), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = Color.FromArgb(196, 205, 218); button.FlatAppearance.MouseOverBackColor = Color.FromArgb(234, 240, 248);
            if (primary) { button.BackColor = Accent; button.ForeColor = Color.White; button.FlatAppearance.BorderColor = Accent; button.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 104, 196); }
            if (action != null) button.Click += delegate { action(); };
            parent.Controls.Add(button); return button;
        }
        private static void AddMenu(Control parent, string text, params object[] items)
        {
            var menu = new ContextMenuStrip();
            for (int i = 0; i < items.Length; i += 2) { var action = (Action)items[i + 1]; menu.Items.Add((string)items[i], null, delegate { action(); }); }
            var button = AddButton(parent, text + " ▾", null);
            button.Click += delegate { menu.Show(button, new Point(0, button.Height)); };
        }
        private static Control Split(Control first, Control second, double fraction)
        {
            var split = new SplitContainer { Size = new Size(900, 600), Orientation = Orientation.Horizontal, Dock = DockStyle.Fill, Panel1MinSize = 100, Panel2MinSize = 80, SplitterWidth = 10, SplitterDistance = 360, BackColor = Canvas };
            bool arranging = false, userMoving = false;
            Action arrange = delegate
            {
                int available = split.ClientSize.Height - split.SplitterWidth;
                if (available < split.Panel1MinSize + split.Panel2MinSize) return;
                arranging = true; try { split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min(available - split.Panel2MinSize, (int)(available * fraction))); } finally { arranging = false; }
            };
            split.SizeChanged += delegate { arrange(); }; split.VisibleChanged += delegate { if (split.Visible) arrange(); };
            split.SplitterMoving += delegate { userMoving = true; };
            split.SplitterMoved += delegate
            {
                int available = split.ClientSize.Height - split.SplitterWidth;
                if (!arranging && userMoving && available > 0) fraction = (double)split.SplitterDistance / available;
                userMoving = false;
            };
            split.Panel1.Controls.Add(Card(first)); split.Panel2.Controls.Add(Card(second));
            return split;
        }
        private static Panel Card(Control inner)
        {
            var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = inner is TextBoxBase ? new Padding(12, 10, 6, 6) : new Padding(1) };
            card.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(Line)) e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
            card.Resize += delegate { card.Invalidate(); };
            inner.Dock = DockStyle.Fill; card.Controls.Add(inner); return card;
        }
        private void Fill(ListView list, List<Dictionary<string, object>> rows, params string[] columns)
        {
            // Skip identical refreshes so the list keeps its scroll position and does not flash every second.
            string signature = Json.Write(rows.Select(row => columns.Select(key => Convert.ToString(row[key])).Concat(new[] { row.ContainsKey("visibility") ? Convert.ToString(row["visibility"]) : "" }).ToArray()).ToArray());
            string previous;
            if (shownRows.TryGetValue(list, out previous) && previous == signature)
            {
                // Rows are identical, but keep each item's backing record current for detail views.
                for (int i = 0; i < list.Items.Count && i < rows.Count; i++) list.Items[i].Tag = rows[i];
                return;
            }
            shownRows[list] = signature;
            var selected = Selected(list); string selectedId = selected == null ? "" : Convert.ToString(selected["id"]);
            int top = list.TopItem == null ? 0 : list.TopItem.Index;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var row in rows)
                {
                    var item = new ListViewItem(columns.Select(key => Convert.ToString(row[key]).Replace("\r", " ").Replace("\n", " ")).ToArray()) { Tag = row };
                    if (row.ContainsKey("visibility") && (string)row["visibility"] == "held") item.ForeColor = Color.DarkGoldenrod;
                    if (row.ContainsKey("receipt") && (string)row["receipt"] == "Unread") { if (unreadFont == null) unreadFont = new Font(list.Font, FontStyle.Bold); item.Font = unreadFont; }
                    list.Items.Add(item); item.Selected = Convert.ToString(row["id"]) == selectedId;
                }
                if (list.Items.Count != 0) list.TopItem = list.Items[Math.Min(top, list.Items.Count - 1)];
            }
            finally { list.EndUpdate(); }
        }
        private static Icon LoadAppIcon(Size size)
        {
            using (var stream = typeof(Viewer).Assembly.GetManifestResourceStream("AgentRoom.AppIcon"))
            using (var icon = new Icon(stream, size)) return (Icon)icon.Clone();
        }
        protected override void Dispose(bool disposing) { if (disposing) { refresh.Dispose(); if (unreadFont != null) unreadFont.Dispose(); tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose(); Icon = null; trayIcon.Dispose(); appIcon.Dispose(); } base.Dispose(disposing); }
    }

    internal sealed class SmoothList : ListView
    {
        internal int StretchColumn = -1, MinimumStretch = 160;
        internal SmoothList()
        {
            DoubleBuffered = true; View = View.Details; FullRowSelect = true; HideSelection = false; MultiSelect = false; BorderStyle = BorderStyle.None; Dock = DockStyle.Fill;
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Stretch(); }
        internal void Stretch()
        {
            // One text column absorbs spare width, so wide windows show more message text instead of an empty band.
            if (StretchColumn < 0 || StretchColumn >= Columns.Count || !IsHandleCreated) return;
            int others = 0; for (int i = 0; i < Columns.Count; i++) if (i != StretchColumn) others += Columns[i].Width;
            int width = Math.Max(MinimumStretch, ClientSize.Width - others - 2);
            if (Columns[StretchColumn].Width != width) Columns[StretchColumn].Width = width;
        }
    }
}
