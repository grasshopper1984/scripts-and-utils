using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace AgentRoom
{
    internal sealed class GraphLayout
    {
        internal readonly Dictionary<long, Rectangle> Bounds = new Dictionary<long, Rectangle>();
        internal Size Size;
        internal static long Activity(Dictionary<string, object> node) { return Json.Number(node, "activity_at", Json.Number(node, "updated_at")); }
        internal static GraphLayout Create(List<Dictionary<string, object>> nodes, List<Dictionary<string, object>> edges, float scale, int availableWidth = 0)
        {
            var layout = new GraphLayout();
            var byId = nodes.ToDictionary(n => Convert.ToInt64(n["id"]));
            var degree = nodes.ToDictionary(n => Convert.ToInt64(n["id"]), n => 0);
            // Reverse topological order, like a newest-first history: successors above their
            // prerequisites, with activity breaking ties only where the links permit it.
            foreach (var edge in edges) degree[Convert.ToInt64(edge["source"])]++;
            var ready = degree.Where(p => p.Value == 0).Select(p => p.Key).ToList();
            var order = new List<long>();
            var columns = new Dictionary<long, int>();
            int columnCount = Math.Max(1, (int)((availableWidth / scale - 40 + 48) / 280)), row = 0;
            while (ready.Count != 0)
            {
                // Pack independent branches across the available width. A predecessor becomes
                // eligible only on the next row, so every link still points upward between rows.
                var batch = ready.OrderByDescending(n => Activity(byId[n])).ThenByDescending(n => Json.Text(byId[n], "state") == "active")
                    .ThenByDescending(n => Json.Number(byId[n], "priority")).ThenByDescending(n => n).Take(columnCount).ToList();
                var free = Enumerable.Range(0, columnCount).ToList();
                foreach (long id in batch)
                {
                    var successors = edges.Where(e => Convert.ToInt64(e["source"]) == id).Select(e => columns[Convert.ToInt64(e["target"])]).ToList();
                    double preferred = successors.Count == 0 ? free[0] : successors.Average();
                    int column = free.OrderBy(c => Math.Abs(c - preferred)).ThenBy(c => c).First();
                    free.Remove(column); columns[id] = column; ready.Remove(id); order.Add(id);
                    var bounds = new Rectangle((int)((20 + column * 280) * scale), (int)((20 + row * 140) * scale), (int)(232 * scale), (int)(112 * scale));
                    layout.Bounds.Add(id, bounds);
                    layout.Size = new Size(Math.Max(layout.Size.Width, bounds.Right + (int)(20 * scale)), bounds.Bottom + (int)(20 * scale));
                }
                foreach (long id in batch)
                    foreach (var edge in edges.Where(e => Convert.ToInt64(e["target"]) == id))
                    {
                        long source = Convert.ToInt64(edge["source"]);
                        if (--degree[source] == 0) ready.Add(source);
                    }
                row++;
            }
            if (order.Count != nodes.Count) throw new InvalidOperationException("Task graph contains a cycle.");
            return layout;
        }
    }

    internal sealed class GraphView : ScrollableControl
    {
        private List<Dictionary<string, object>> nodes = new List<Dictionary<string, object>>();
        private List<Dictionary<string, object>> edges = new List<Dictionary<string, object>>();
        private GraphLayout layout = new GraphLayout();
        internal const float DefaultZoom = .8f;
        private float zoom = DefaultZoom;
        private float dpiScale = 1;
        private bool arranging;
        internal long SelectedTask { get; private set; }
        internal event Action<long> SelectedTaskChanged;
        internal event Action<long> FocusRequested;
        internal GraphView()
        {
            Dock = DockStyle.Fill; AutoScroll = true; BackColor = Color.FromArgb(248, 250, 253); TabStop = true;
            AccessibleName = "Task history graph, newest activity first within dependency order. Up and Down select tasks; Enter focuses their neighbors.";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }
        internal void SetGraph(Dictionary<string, object> graph)
        {
            nodes = (List<Dictionary<string, object>>)graph["nodes"];
            edges = (List<Dictionary<string, object>>)graph["edges"];
            if (!nodes.Any(n => Convert.ToInt64(n["id"]) == SelectedTask)) SelectedTask = 0;
            Arrange();
        }
        internal void SetZoom(float value) { zoom = value; Arrange(); }
        private void Arrange()
        {
            if (arranging || nodes == null) return;
            arranging = true;
            try
            {
                if (IsHandleCreated) using (Graphics graphics = CreateGraphics()) dpiScale = graphics.DpiX / 96f;
                layout = GraphLayout.Create(nodes, edges, dpiScale * zoom, ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                AutoScrollMinSize = layout.Size; Invalidate();
            }
            finally { arranging = false; }
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (nodes != null) Arrange(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Arrange(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (nodes.Count == 0) { TextRenderer.DrawText(e.Graphics, "No tasks match these filters.", Font, new Point(20, 20), Color.DimGray); return; }
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            float scale = dpiScale * zoom;
            using (var arrow = new AdjustableArrowCap(4, 5))
            using (var dependency = new Pen(Color.FromArgb(77, 112, 157), Math.Max(1, 1.5f * scale)))
            using (var reference = new Pen(Color.FromArgb(146, 129, 159), Math.Max(1, 1.3f * scale)))
            {
                dependency.CustomEndCap = arrow; reference.CustomEndCap = arrow; reference.DashStyle = DashStyle.Dash;
                foreach (var edge in edges)
                {
                    Rectangle from = layout.Bounds[Convert.ToInt64(edge["source"])], to = layout.Bounds[Convert.ToInt64(edge["target"])];
                    float offset = (string)edge["kind"] == "reference" ? 5 * scale : 0;
                    var start = new PointF(from.Left + from.Width / 2f + offset, from.Top);
                    var end = new PointF(to.Left + to.Width / 2f + offset, to.Bottom);
                    using (var path = new GraphicsPath())
                    {
                        if (start.Y - end.Y <= 30 * scale)
                        {
                            float middle = (start.Y + end.Y) / 2;
                            path.AddBezier(start, new PointF(start.X, middle), new PointF(end.X, middle), end);
                        }
                        else
                        {
                            // Long links travel through the spaces between cards and rows.
                            // This keeps ordinary diagram arrows clear of intermediate cards.
                            float gutter = from.Right + 24 * scale, lower = start.Y - 14 * scale, upper = end.Y + 14 * scale;
                            RoundedPath(path, new[] { start, new PointF(start.X, lower), new PointF(gutter, lower),
                                new PointF(gutter, upper), new PointF(end.X, upper), end }, 7 * scale);
                        }
                        e.Graphics.DrawPath((string)edge["kind"] == "dependency" ? dependency : reference, path);
                    }
                }
            }
            using (var normal = new Font(Font.FontFamily, Font.Size * zoom))
            using (var title = new Font(Font.FontFamily, Font.Size * zoom, FontStyle.Bold))
            foreach (var node in nodes)
            {
                long id = Convert.ToInt64(node["id"]); Rectangle box = layout.Bounds[id];
                using (var fill = new SolidBrush(SelectedTask == id ? Color.FromArgb(239, 246, 255) : Color.White)) e.Graphics.FillRectangle(fill, box);
                using (var border = new Pen(SelectedTask == id ? Color.RoyalBlue : Color.FromArgb(196, 206, 219), SelectedTask == id ? 2.5f : 1)) e.Graphics.DrawRectangle(border, box);
                using (var accent = new SolidBrush(StateColor((string)node["state"]))) e.Graphics.FillRectangle(accent, box.X, box.Y, Math.Max(3, 4 * scale), box.Height);
                int left = box.X + (int)(12 * scale), width = box.Width - (int)(20 * scale);
                string priority = Convert.ToInt64(node["priority"]) == 2 ? "  Critical" : Convert.ToInt64(node["priority"]) == 1 ? "  High" : "";
                int dateWidth = (int)(142 * scale);
                Draw(e.Graphics, "#" + id + priority, normal, Color.DimGray, new Rectangle(left, box.Y + (int)(7 * scale), Math.Max(1, width - dateWidth), (int)(20 * scale)));
                long activity = GraphLayout.Activity(node);
                if (activity != 0) Draw(e.Graphics, DateTimeOffset.FromUnixTimeSeconds(activity).LocalDateTime.ToString("MMM d · HH:mm"), normal, Color.DimGray,
                    new Rectangle(box.Right - dateWidth - (int)(8 * scale), box.Y + (int)(7 * scale), dateWidth, (int)(20 * scale)), StringAlignment.Far);
                Draw(e.Graphics, (string)node["title"], title, Color.FromArgb(29, 43, 62), new Rectangle(left, box.Y + (int)(29 * scale), width, (int)(38 * scale)));
                Draw(e.Graphics, node["state"] + "  " + (node["owner"] ?? node["assigned"] ?? "unassigned"), normal, StateColor((string)node["state"]), new Rectangle(left, box.Y + (int)(72 * scale), width, (int)(19 * scale)));
                // The node's thread stays inside it: only its size and unanswered asks show on the card.
                long hidden = Convert.ToInt64(node["hidden_links"]), messages = node.ContainsKey("messages") ? Convert.ToInt64(node["messages"]) : 0, open = node.ContainsKey("open_asks") ? Convert.ToInt64(node["open_asks"]) : 0;
                var notes = new List<string>();
                if (messages != 0) notes.Add(messages + (messages == 1 ? " message" : " messages"));
                if (open != 0) notes.Add(open + " awaiting reply");
                if (hidden != 0) notes.Add(hidden + " link(s) outside view");
                if (notes.Count != 0) Draw(e.Graphics, String.Join("  ·  ", notes), normal, open != 0 ? Color.FromArgb(176, 74, 20) : Color.FromArgb(96, 108, 124), new Rectangle(left, box.Y + (int)(91 * scale), width, (int)(19 * scale)));
            }
        }
        private static void RoundedPath(GraphicsPath path, PointF[] points, float radius)
        {
            PointF previous = points[0];
            for (int i = 1; i < points.Length - 1; i++)
            {
                PointF corner = points[i], before = points[i - 1], after = points[i + 1];
                float incoming = Math.Abs(corner.X - before.X) + Math.Abs(corner.Y - before.Y);
                float outgoing = Math.Abs(corner.X - after.X) + Math.Abs(corner.Y - after.Y);
                if (incoming == 0 || outgoing == 0) continue;
                float bend = Math.Min(radius, Math.Min(incoming, outgoing) / 2);
                var entry = new PointF(corner.X + (before.X - corner.X) * bend / incoming, corner.Y + (before.Y - corner.Y) * bend / incoming);
                var exit = new PointF(corner.X + (after.X - corner.X) * bend / outgoing, corner.Y + (after.Y - corner.Y) * bend / outgoing);
                path.AddLine(previous, entry); path.AddBezier(entry, corner, corner, exit); previous = exit;
            }
            path.AddLine(previous, points[points.Length - 1]);
        }
        private static void Draw(Graphics graphics, string text, Font font, Color color, Rectangle bounds, StringAlignment alignment = StringAlignment.Near)
        {
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, Alignment = alignment }) graphics.DrawString(text, font, brush, bounds, format);
        }
        private static Color StateColor(string state)
        {
            switch (state) { case "active": return Color.FromArgb(35, 102, 183); case "blocked": return Color.Firebrick; case "paused": return Color.DarkGoldenrod;
                case "review": return Color.FromArgb(128, 72, 157); case "done": return Color.SeaGreen; default: return Color.DimGray; }
        }
        private void SelectTask(long id)
        {
            SelectedTask = id; Invalidate();
            if (SelectedTaskChanged != null) SelectedTaskChanged(id);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            var point = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
            foreach (var pair in layout.Bounds) if (pair.Value.Contains(point)) { SelectTask(pair.Key); break; }
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var point = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
            foreach (var pair in layout.Bounds) if (pair.Value.Contains(point) && FocusRequested != null) { FocusRequested(pair.Key); break; }
        }
        protected override bool IsInputKey(Keys keyData) { return new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down }.Contains(keyData & Keys.KeyCode) || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter && SelectedTask != 0 && FocusRequested != null) { FocusRequested(SelectedTask); e.Handled = true; }
            if (new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down }.Contains(e.KeyCode) && nodes.Count != 0)
            {
                var ids = nodes.Select(n => Convert.ToInt64(n["id"])).OrderBy(id => layout.Bounds[id].Y).ThenBy(id => layout.Bounds[id].X).ToList();
                int current = ids.IndexOf(SelectedTask); int next = current < 0 ? 0 : Math.Max(0, Math.Min(ids.Count - 1, current + (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down ? 1 : -1)));
                SelectTask(ids[next]); Rectangle box = layout.Bounds[ids[next]]; AutoScrollPosition = new Point(Math.Max(0, box.X - 20), Math.Max(0, box.Y - 20)); e.Handled = true;
            }
        }
    }
}
