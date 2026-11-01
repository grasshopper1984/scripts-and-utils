using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RazerBattery
{
    internal static class Tests
    {
        private static int checks;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string output = args[0];
                Directory.CreateDirectory(output);
                ProtocolTests();
                StateTests();
                HardwareTests();
                PollerTests();
                SettingsTests(output);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                RenderTests(output);
                Console.WriteLine("PASS: {0} assertions; UI previews in test-output.", checks);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }

        private static void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new Exception(message);
        }

        private static byte[] LevelReply()
        {
            // Independent 64% response fixture, including the Windows report-ID prefix.
            var bytes = new byte[91];
            byte[] header = { 0, 2, 0x1F, 0, 0, 0, 2, 7, 0x80, 0, 0xA3 };
            Array.Copy(header, bytes, header.Length);
            bytes[89] = 0x26;
            return bytes;
        }

        private static bool Accepts(byte[] response, byte command)
        {
            byte value;
            string error;
            return BatteryProtocol.TryRead(response, command, out value, out error);
        }

        private static void ProtocolTests()
        {
            byte[] request = BatteryProtocol.Request(0x80, 91);
            Check(request[89] == 0x85 && request[2] == 0x1F && request[6] == 2 && request[7] == 7,
                "Battery request matches protocol fixture");
            Check(BatteryProtocol.Request(0x84, 91)[89] == 0x81, "Charging request checksum");
            byte value;
            string error;
            Check(BatteryProtocol.TryRead(LevelReply(), 0x80, out value, out error) && value == 163,
                "Decode independently constructed battery response");
            Check(BatteryProtocol.Percent(value) == 64, "64% fixture");
            Check(!Accepts(null, 0x80) && !Accepts(new byte[90], 0x80), "Reject missing/truncated packets");
            Check(!Accepts(request, 0x80), "Do not mistake an echoed request for a response");
            Check(!Accepts(LevelReply(), 0x84), "Reject a battery response to a charging request");
            foreach (int offset in new[] { 0, 2, 3, 4, 5, 6, 7, 8, 10, 40, 89 })
            {
                var corrupt = LevelReply();
                corrupt[offset] ^= 1;
                Check(!Accepts(corrupt, 0x80), "Reject corrupt or mismatched byte " + offset);
            }
            foreach (byte status in new byte[] { 0, 1, 3, 4, 5 })
            {
                var response = LevelReply();
                response[1] = status;
                Check(!Accepts(response, 0x80), "Reject unsuccessful status " + status);
            }
            var charge = LevelReply();
            charge[8] = 0x84;
            charge[10] = 1;
            charge[89] = 0x80;
            Check(Accepts(charge, 0x84), "Charging fixture");
            charge[10] = 2;
            charge[89] = 0x83;
            Check(!Accepts(charge, 0x84), "Reject non-boolean charging value");
            var empty = LevelReply();
            empty[10] = 0;
            empty[89] = 0x85;
            Check(Accepts(empty, 0x80), "A valid empty battery must remain 0%, not stale");
            int previous = -1;
            for (int i = 0; i <= 255; i++)
            {
                int percent = BatteryProtocol.Percent((byte)i);
                Check(percent >= previous && percent >= 0 && percent <= 100, "Bounded monotonic percentage");
                previous = percent;
            }
            Check(BatteryProtocol.Percent(0) == 0 && BatteryProtocol.Percent(255) == 100, "Percentage endpoints");
            bool threw = false;
            try { BatteryProtocol.Request(0x04, 91); } catch (ArgumentOutOfRangeException) { threw = true; }
            Check(threw, "Never build a device-setting command");
            Check(NativeHid.IsSupported(0xDA) && NativeHid.IsSupported(0xDB) && !NativeHid.IsSupported(0x257),
                "Only allow Cobra HyperSpeed; exclude Razer keyboards");
        }

        private static void StateTests()
        {
            var state = new BatteryState();
            DateTime time = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            state.Apply(BatteryReading.Unavailable("Mouse asleep"), time);
            Check(!state.Percent.HasValue && !state.IsLive, "No fabricated initial battery value");
            state.Apply(new BatteryReading { Percent = 64, Charging = true, Connection = "USB" }, time);
            Check(state.Percent == 64 && state.IsLive && state.Detail == "Charging", "Live charging state");
            state.Apply(BatteryReading.Unavailable("Mouse asleep"), time.AddMinutes(1));
            Check(state.Percent == 64 && !state.IsLive && state.Charging == null && state.LastRead == time &&
                state.Detail == "Last known", "Disconnect retains the last value and clears charging");
            state.Apply(new BatteryReading { Percent = 0, Charging = false, Connection = "Wireless" }, time.AddMinutes(2));
            Check(state.Percent == 0 && state.IsLive && state.Detail == "Wireless", "Reconnect accepts actual zero");
            state.Apply(new BatteryReading { Percent = 75, Charging = null, Connection = "USB" }, time.AddMinutes(3));
            Check(state.IsLive && state.Detail == "Charge status unknown", "Partial read keeps battery with unknown charge status");
        }

        private static void SettingsTests(string output)
        {
            string path = Path.Combine(output, "settings-test.txt");
            File.WriteAllText(path, "corrupt settings");
            Check(!WidgetSettings.Load(path).Position.HasValue, "Invalid settings fall back safely");
            var settings = new WidgetSettings { Position = new Point(-500, 200), TopMost = false };
            settings.Save(path);
            var loaded = WidgetSettings.Load(path);
            Check(loaded.Position == settings.Position && !loaded.TopMost, "Restore negative monitor coordinates and pin preference");
            var area = new Rectangle(-1920, 0, 1920, 1040);
            Rectangle clamped = BatteryWidget.ClampToScreen(new Rectangle(10000, -10000, 264, 112), area);
            Check(area.Contains(clamped), "Recover a widget after its saved monitor disappears");
        }

        private static void HardwareTests()
        {
            Check(Marshal.SizeOf(typeof(HardwareReader.PdhValue)) == 16 &&
                Marshal.OffsetOf(typeof(HardwareReader.PdhValue), "Number").ToInt32() == 8, "PDH native union ABI");
            var cpu = new CpuUsage();
            Check(!cpu.Sample(100, 150, 50).HasValue, "CPU usage needs an interval, not time since boot");
            Check(cpu.Sample(150, 250, 150) == 75, "CPU kernel time includes idle; fixture is 75% busy");
            Check(cpu.Sample(250, 350, 150) == 0, "Idle CPU is valid zero");
            Check(cpu.Sample(250, 400, 200) == 100, "Fully busy CPU");
            Check(!cpu.Sample(250, 400, 200).HasValue, "No elapsed CPU time is unknown");
            Check(!cpu.Sample(0, 0, 0).HasValue, "Counter regression is unknown, not overflow");
            cpu.Reset();
            Check(!cpu.Sample(100, 200, 300).HasValue, "Resume/hide gap resets the CPU baseline");
            Check(!cpu.Sample(1000, 201, 301).HasValue, "Reject inconsistent idle and total deltas");
            Check(Math.Abs(HardwareReading.Celsius(3352).Value - 62.05) < 0.00001, "Tenths Kelvin converts to Celsius");
            Check(!HardwareReading.Celsius(null).HasValue && !HardwareReading.Celsius(0).HasValue &&
                !HardwareReading.Celsius(65535).HasValue, "Missing or invalid firmware temperatures stay unavailable");
            Check(HardwareReading.CpuFrequency(1512, 300) == 4536, "CPU frequency retains turbo above nominal");
            Check(!HardwareReading.CpuFrequency(1512, null).HasValue && !HardwareReading.CpuFrequency(0, 100).HasValue,
                "Do not show nominal CPU frequency when the performance estimate is unavailable");
            Check(!HardwareReading.InRange(Double.NaN, 0, 100).HasValue &&
                !HardwareReading.InRange(Double.PositiveInfinity, 0, 100).HasValue &&
                !HardwareReading.InRange(101, 0, 100).HasValue, "Reject nonfinite and out-of-range readings");
            Check(HardwareReading.InRange(0, 0, 100) == 0, "Zero GPU load is valid");
            Check(HardwareReading.Temperature(null) == "--" && HardwareReading.Percent(0) == "0%" &&
                HardwareReading.Clock(4536) == "4.54 GHz", "Display distinguishes missing values and zero, with clock units");
        }

        private static void PollerTests()
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                int reads = 0, closes = 0;
                var poller = new HardwarePoller(delegate
                {
                    Interlocked.Increment(ref reads);
                    entered.Set();
                    if (!release.WaitOne(5000)) throw new TimeoutException("Test read release");
                    return new HardwareReading();
                }, delegate { Interlocked.Increment(ref closes); });
                try
                {
                    var task = poller.Begin();
                    Check(task != null && entered.WaitOne(5000), "Sensor read starts on a worker");
                    Check(poller.Begin() == null && reads == 1, "Slow reads cannot queue or overlap");
                    poller.Dispose();
                    Check(!poller.Cleanup.IsCompleted && poller.Begin() == null,
                        "Closing returns while a read is blocked and prevents new reads");
                    release.Set();
                    Check(poller.Cleanup.Wait(5000) && closes == 1, "Cleanup follows the last native read");
                    poller.Dispose();
                    Check(closes == 1, "Repeated disposal does not shut the driver down twice");
                }
                finally { release.Set(); poller.Dispose(); poller.Cleanup.Wait(5000); }
            }
            int attempts = 0;
            var retry = new HardwarePoller(delegate
            {
                if (++attempts == 1) throw new InvalidOperationException("Simulated sensor failure");
                return new HardwareReading { CpuLoad = 25 };
            }, delegate { });
            var failed = retry.Begin();
            bool observed = false;
            try { failed.GetAwaiter().GetResult(); } catch (InvalidOperationException) { observed = true; }
            retry.Complete(failed);
            var recovered = retry.Begin();
            Check(observed && recovered.GetAwaiter().GetResult().CpuLoad == 25, "A failed read permits recovery on the next tick");
            retry.Complete(recovered);
            retry.Dispose();
            Check(retry.Cleanup.Wait(5000), "Idle cleanup completes on the worker");
        }

        private static void RenderTests(string output)
        {
            using (var widget = new BatteryWidget(null, null))
            {
                IntPtr handle = widget.Handle;
                foreach (uint dpi in new uint[] { 96, 144, 192 })
                {
                    widget.SetDpi(dpi);
                    Check(widget.ClientSize == new Size((int)(264 * dpi / 96), (int)(112 * dpi / 96)), "DPI-sized widget");
                    widget.ApplyHardware(new HardwareReading { CpuLoad = 35, CpuMHz = 4320, GpuLoad = 96,
                        GpuMHz = 1470, GpuTemperature = 74, SystemTemperature = 63.05 }, Stopwatch.GetTimestamp());
                    widget.ApplyReading(new BatteryReading { Percent = 64, Charging = true, Connection = "USB" });
                    SavePreview(widget, Path.Combine(output, "charging-" + dpi + ".png"));
                }
                widget.SetDpi(144);
                widget.ApplyReading(new BatteryReading { Percent = 9, Charging = false, Connection = "Wireless" });
                SavePreview(widget, Path.Combine(output, "low.png"));
                widget.ApplyReading(BatteryReading.Unavailable("Mouse asleep or unavailable"));
                Check(widget.AccessibleDescription.Contains("Last read"), "Expose stale state to accessibility tools");
                SavePreview(widget, Path.Combine(output, "stale.png"));
                widget.ApplyReading(new BatteryReading { Percent = 100, Charging = false, Connection = "USB" });
                SavePreview(widget, Path.Combine(output, "full.png"));
                widget.ApplyHardware(new HardwareReading { CpuLoad = 100, CpuMHz = 4999, GpuLoad = 100,
                    GpuMHz = 10500, GpuTemperature = 100, SystemTemperature = 150 }, Stopwatch.GetTimestamp());
                Check(widget.HardwareIsFresh && widget.AccessibleDescription.Contains("sensor mapping is unverified"),
                    "Accessibility identifies system temperature without claiming it is a CPU sensor");
                SavePreview(widget, Path.Combine(output, "hardware-wide-values.png"));
                widget.ApplyHardware(new HardwareReading { CpuLoad = 25, CpuMHz = 3300 }, Stopwatch.GetTimestamp());
                SavePreview(widget, Path.Combine(output, "hardware-partial.png"));
                widget.ApplyHardware(new HardwareReading { CpuLoad = 25 }, Stopwatch.GetTimestamp() - 11 * Stopwatch.Frequency);
                Check(!widget.HardwareIsFresh && widget.AccessibleDescription.Contains("stale"), "Old sensor values are not presented as current");
                SavePreview(widget, Path.Combine(output, "hardware-stale.png"));
            }
            using (var widget = new BatteryWidget(null, null))
            {
                IntPtr handle = widget.Handle;
                widget.SetDpi(144);
                widget.ApplyReading(BatteryReading.Unavailable("Connect Cobra HyperSpeed"));
                SavePreview(widget, Path.Combine(output, "disconnected.png"));
            }
        }

        private static void SavePreview(BatteryWidget widget, string path)
        {
            using (var bitmap = new Bitmap(widget.Width, widget.Height))
            {
                widget.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(path, ImageFormat.Png);
            }
        }
    }
}
