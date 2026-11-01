using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RazerBattery
{
    internal sealed class WidgetSettings
    {
        internal Point? Position;
        internal bool TopMost = true;

        internal static WidgetSettings Load(string path)
        {
            var settings = new WidgetSettings();
            if (path == null) return settings;
            try
            {
                string[] lines = File.ReadAllLines(path);
                int x, y;
                if (lines.Length == 3 && Int32.TryParse(lines[0], out x) && Int32.TryParse(lines[1], out y) &&
                    Math.Abs((long)x) < 100000 && Math.Abs((long)y) < 100000)
                {
                    settings.Position = new Point(x, y);
                    settings.TopMost = lines[2] != "false";
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return settings;
        }

        internal void Save(string path)
        {
            if (path == null || !Position.HasValue) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[] { Position.Value.X.ToString(), Position.Value.Y.ToString(),
                    TopMost ? "true" : "false" });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class BatteryWidget : Form
    {
        private const int LogicalWidth = 264, LogicalHeight = 112;
        private readonly Func<BatteryReading> reader;
        private readonly string settingsPath;
        private readonly WidgetSettings settings;
        private readonly BatteryState state = new BatteryState();
        private readonly Timer timer = new Timer { Interval = 60000 };
        private readonly Timer hardwareTimer = new Timer { Interval = 2000 };
        private readonly HardwareReader hardwareReader;
        private readonly HardwarePoller hardwarePoller;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem refresh = new ToolStripMenuItem("Refresh now");
        private readonly ToolStripMenuItem visibility = new ToolStripMenuItem("Hide widget");
        private readonly Font numberFont = new Font("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font labelFont = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font metricFont = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
        private Icon trayImage;
        private bool polling, closing, shown, hardwareWasFresh;
        private HardwareReading hardware = new HardwareReading();
        private long hardwareSampledAt;
        private int hardwareGeneration;
        private string batteryDescription = "Cobra HyperSpeed | Checking mouse...";
        private float scale = 1;
        internal static readonly uint ShowMessage = RegisterWindowMessage("ScriptsAndUtils.RazerBattery.Show");

        internal BatteryWidget(Func<BatteryReading> reader, string settingsPath, HardwareReader hardwareReader = null)
        {
            this.reader = reader;
            this.settingsPath = settingsPath;
            this.hardwareReader = hardwareReader;
            if (hardwareReader != null) hardwarePoller = new HardwarePoller(hardwareReader.Read, hardwareReader.Dispose);
            settings = WidgetSettings.Load(settingsPath);
            Text = "Razer Battery";
            AccessibleName = "Razer mouse battery and system monitor";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 27, 29);
            TopMost = settings.TopMost;
            ClientSize = new Size(LogicalWidth, LogicalHeight);
            ContextMenuStrip = menu;
            refresh.Click += delegate { RefreshAll(); };
            var pin = new ToolStripMenuItem("Always on top") { Checked = TopMost, CheckOnClick = true };
            pin.CheckedChanged += delegate { TopMost = pin.Checked; SavePosition(); };
            visibility.Click += delegate { if (Visible) Hide(); else Reveal(); };
            menu.Opening += delegate { visibility.Text = Visible ? "Hide widget" : "Show widget"; };
            menu.Items.Add(refresh);
            menu.Items.Add(pin);
            menu.Items.Add(visibility);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Reveal(); };
            timer.Tick += delegate { RefreshReading(); };
            hardwareTimer.Tick += delegate
            {
                if (hardwareWasFresh && !HardwareIsFresh)
                {
                    hardwareWasFresh = false;
                    UpdateDescription();
                    Invalidate();
                }
                RefreshHardware();
            };
            UpdateTray();
        }

        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80; return p; } // Tool window; no Alt+Tab entry.
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetDpi(GetDpiForWindow(Handle));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = settings.Position ?? new Point(area.Right - Width - 20, area.Bottom - Height - 20);
            KeepOnScreen();
            tray.Visible = true;
            shown = true;
            if (reader != null)
            {
                timer.Start();
                RefreshReading();
            }
            if (hardwarePoller != null)
            {
                hardwareTimer.Start();
                RefreshHardware();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!shown || closing || hardwarePoller == null) return;
            hardwareGeneration++;
            hardwareSampledAt = 0;
            hardwareWasFresh = false;
            UpdateDescription();
            if (Visible) { hardwareTimer.Start(); RefreshHardware(); }
            else hardwareTimer.Stop();
        }

        private void RefreshAll() { RefreshReading(); RefreshHardware(); }

        private async void RefreshHardware()
        {
            if (closing || !Visible || hardwarePoller == null) return;
            Task<HardwareReading> pending = hardwarePoller.Begin();
            if (pending == null) return;
            long started = Stopwatch.GetTimestamp();
            int generation = hardwareGeneration;
            HardwareReading reading;
            try { reading = await pending; }
            catch (Exception) { reading = new HardwareReading(); }
            finally { hardwarePoller.Complete(pending); }
            if (closing || IsDisposed || !Visible || generation != hardwareGeneration) return;
            ApplyHardware(reading, started);
        }

        internal bool HardwareIsFresh
        {
            get { return hardwareSampledAt != 0 &&
                (Stopwatch.GetTimestamp() - hardwareSampledAt) / (double)Stopwatch.Frequency <= 10; }
        }

        internal void ApplyHardware(HardwareReading reading, long sampledAt)
        {
            hardware = reading;
            hardwareSampledAt = sampledAt;
            hardwareWasFresh = HardwareIsFresh;
            UpdateDescription();
            Invalidate();
        }

        private void UpdateDescription()
        {
            AccessibleDescription = batteryDescription + "\n\n" +
                (HardwareIsFresh ? hardware.Description : "Hardware readings pending or stale (over 10 seconds).");
        }

        private async void RefreshReading()
        {
            if (polling || closing || reader == null) return;
            polling = true;
            BatteryReading reading;
            try { reading = await Task.Run(reader); }
            catch (Exception) { reading = BatteryReading.Unavailable("Battery read failed"); }
            if (closing || IsDisposed) return;
            polling = false;
            ApplyReading(reading);
        }

        internal void ApplyReading(BatteryReading reading)
        {
            state.Apply(reading, DateTime.UtcNow);
            batteryDescription = state.Tooltip(reading);
            UpdateDescription();
            UpdateTray();
            Invalidate();
        }

        private Color Accent
        {
            get { return !state.IsLive ? Color.FromArgb(140, 148, 150) :
                state.Percent <= 20 && state.Charging != true ? Color.FromArgb(255, 182, 87) :
                Color.FromArgb(146, 219, 120); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(scale, scale);
            using (var border = new Pen(Color.FromArgb(58, 64, 65)))
            using (var outline = Rounded(new RectangleF(0.5f, 0.5f, LogicalWidth - 1, LogicalHeight - 1), 12))
                g.DrawPath(border, outline);
            using (var pen = new Pen(Accent, 1.6f))
            using (var mouse = Rounded(new RectangleF(15, 10, 18, 26), 8))
            {
                g.DrawPath(pen, mouse);
                g.DrawLine(pen, 24, 11, 24, 20);
                g.DrawLine(pen, 16, 21, 32, 21);
            }
            using (var color = new SolidBrush(Accent))
            {
                g.DrawString(state.Percent.HasValue ? state.Percent + "%" : "--", numberFont, color, 39, 4);
                if (state.Charging == true)
                    g.FillPolygon(color, new[] { new PointF(129, 13), new PointF(122, 24),
                        new PointF(127, 24), new PointF(124, 33), new PointF(133, 21), new PointF(128, 21) });
            }
            DrawHardware(g);
        }

        private void DrawHardware(Graphics g)
        {
            bool fresh = HardwareIsFresh;
            using (var line = new Pen(Color.FromArgb(58, 64, 65)))
                g.DrawLine(line, 14, 44, LogicalWidth - 14, 44);
            using (var muted = new SolidBrush(Color.FromArgb(150, 161, 164)))
            using (var value = new SolidBrush(Color.FromArgb(219, 224, 225)))
            using (var cpu = new SolidBrush(Color.FromArgb(112, 184, 255)))
            using (var gpu = new SolidBrush(Color.FromArgb(235, 139, 225)))
            using (var right = new StringFormat { Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString("SYS", labelFont, muted, 153, 15);
                g.DrawString(HardwareReading.Temperature(fresh ? hardware.SystemTemperature : null), metricFont, value,
                    new RectangleF(194, 9, 58, 28), right);
                g.DrawString("CPU", labelFont, cpu, 14, 53);
                g.DrawString("GPU", labelFont, gpu, 14, 85);
                g.DrawString(HardwareReading.Percent(fresh ? hardware.CpuLoad : null), metricFont, cpu,
                    new RectangleF(47, 47, 50, 28), right);
                g.DrawString(HardwareReading.Clock(fresh ? hardware.CpuMHz : null), metricFont, cpu,
                    new RectangleF(101, 47, 91, 28), right);
                g.DrawString(HardwareReading.Percent(fresh ? hardware.GpuLoad : null), metricFont, gpu,
                    new RectangleF(47, 79, 50, 28), right);
                g.DrawString(HardwareReading.Clock(fresh ? hardware.GpuMHz : null), metricFont, gpu,
                    new RectangleF(101, 79, 91, 28), right);
                g.DrawString(HardwareReading.Temperature(fresh ? hardware.GpuTemperature : null), metricFont, gpu,
                    new RectangleF(194, 79, 58, 28), right);
            }
        }

        private void UpdateTray()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            using (var font = new Font("Segoe UI", state.Percent == 100 ? 18 : 23, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Accent))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(state.Percent.HasValue ? state.Percent.ToString() : "?", font, brush,
                    new RectangleF(0, 0, 32, 32), format);
                IntPtr handle = bitmap.GetHicon();
                Icon next;
                try { using (Icon borrowed = Icon.FromHandle(handle)) next = (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
                tray.Icon = next;
                if (trayImage != null) trayImage.Dispose();
                trayImage = next;
            }
            tray.Text = "Mouse battery: " + (state.Percent.HasValue ? state.Percent + "%" : "--") +
                (state.Charging == true ? " (charging)" : state.Percent.HasValue && !state.IsLive ? " (last known)" : "");
        }

        internal void SetDpi(uint dpi)
        {
            scale = dpi == 0 ? 1 : dpi / 96f;
            ClientSize = new Size((int)(LogicalWidth * scale), (int)(LogicalHeight * scale));
            Region old = Region;
            using (var shape = Rounded(new RectangleF(0, 0, Width, Height), 12 * scale))
                Region = new Region(shape);
            if (old != null) old.Dispose();
            Invalidate();
        }

        internal static Rectangle ClampToScreen(Rectangle bounds, Rectangle area)
        {
            bounds.X = Math.Max(area.Left, Math.Min(bounds.X, area.Right - bounds.Width));
            bounds.Y = Math.Max(area.Top, Math.Min(bounds.Y, area.Bottom - bounds.Height));
            return bounds;
        }

        private void KeepOnScreen() { Bounds = ClampToScreen(Bounds, Screen.FromRectangle(Bounds).WorkingArea); }
        private void Reveal() { Show(); KeepOnScreen(); }

        private void SavePosition()
        {
            settings.Position = Location;
            settings.TopMost = TopMost;
            settings.Save(settingsPath);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); // Native window drag.
        }

        protected override void WndProc(ref Message m)
        {
            if ((uint)m.Msg == ShowMessage) { Reveal(); return; }
            if (m.Msg == 0x02E0) // WM_DPICHANGED
            {
                var rect = (NativeRect)Marshal.PtrToStructure(m.LParam, typeof(NativeRect));
                SetDpi((uint)(m.WParam.ToInt64() & 0xFFFF));
                Location = new Point(rect.Left, rect.Top);
                KeepOnScreen();
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x0232) { KeepOnScreen(); SavePosition(); } // WM_EXITSIZEMOVE
            if (m.Msg == 0x007E || m.Msg == 0x001A) KeepOnScreen(); // Display/work-area changes.
            if (m.Msg == 0x0218 && (m.WParam.ToInt32() == 7 || m.WParam.ToInt32() == 18))
            {
                hardwareGeneration++;
                hardwareSampledAt = 0;
                if (hardwareReader != null) hardwareReader.RequestReset();
                UpdateDescription();
                Invalidate();
                RefreshAll();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Hide(); return true; }
            if (keyData == (Keys.Control | Keys.R)) { RefreshAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            timer.Stop();
            hardwareTimer.Stop();
            SavePosition();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing = true;
                timer.Dispose();
                hardwareTimer.Dispose();
                if (hardwarePoller != null) hardwarePoller.Dispose();
                tray.Visible = false;
                tray.Dispose();
                if (trayImage != null) trayImage.Dispose();
                menu.Dispose();
                numberFont.Dispose();
                labelFont.Dispose();
                metricFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    }
}
