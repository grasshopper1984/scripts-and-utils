using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RazerBattery
{
    internal sealed class HardwareReading
    {
        internal double? CpuLoad, CpuMHz, GpuLoad, GpuMHz, GpuTemperature, SystemTemperature;
        internal string GpuName = "NVIDIA GPU";

        internal static double? InRange(double? value, double minimum, double maximum)
        {
            return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) &&
                value >= minimum && value <= maximum ? value : null;
        }

        internal static double? Celsius(double? tenthsKelvin)
        {
            return InRange(tenthsKelvin / 10.0 - 273.15, -30, 150);
        }

        internal static double? CpuFrequency(double? nominalMHz, double? performancePercent)
        {
            // Windows reports average performance relative to nominal frequency; turbo may exceed 100%.
            if (!InRange(nominalMHz, 1, 20000).HasValue || !InRange(performancePercent, 0, 2000).HasValue)
                return null;
            return InRange(nominalMHz * performancePercent / 100.0, 0, 20000);
        }

        internal static string Percent(double? value) { return Format(value, "0", "%"); }
        internal static string Clock(double? value) { return Format(value / 1000.0, "0.00", " GHz"); }
        internal static string Temperature(double? value) { return Format(value, "0", "\u00B0C"); }
        private static string Format(double? value, string format, string unit)
        {
            return value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) + unit : "--";
        }

        internal string Description
        {
            get
            {
                return "CPU: " + Percent(CpuLoad) + " | " + Clock(CpuMHz) + " (estimated active clock)\n" +
                    GpuName + ": " + Percent(GpuLoad) + " | " + Clock(GpuMHz) + " | " + Temperature(GpuTemperature) +
                    "\nSystem temperature: " + Temperature(SystemTemperature) +
                    "\nSystem temperature is firmware zone TZ00; its CPU sensor mapping is unverified." +
                    "\n-- means unavailable. Hardware updates every 2 seconds while visible.";
            }
        }
    }

    internal sealed class CpuUsage
    {
        private bool ready;
        private ulong idle, kernel, user;

        internal void Reset() { ready = false; }

        internal double? Sample(ulong nextIdle, ulong nextKernel, ulong nextUser)
        {
            double? result = null;
            if (ready && nextIdle >= idle && nextKernel >= kernel && nextUser >= user)
            {
                ulong total = nextKernel - kernel + nextUser - user;
                ulong unused = nextIdle - idle; // GetSystemTimes includes idle time in kernel time.
                if (total > 0 && unused <= total) result = 100.0 * (total - unused) / total;
            }
            idle = nextIdle;
            kernel = nextKernel;
            user = nextUser;
            ready = true;
            return result;
        }
    }

    // This object is used only by the sampling worker. RequestReset is the sole cross-thread operation.
    internal sealed class HardwareReader : IDisposable
    {
        internal const string ThermalCounter = @"\Thermal Zone Information(\_TZ.TZ00)\High Precision Temperature";
        private readonly CpuUsage cpu = new CpuUsage();
        private readonly NvidiaReader nvidia = new NvidiaReader();
        private IntPtr query, frequency, performance, temperature;
        private long lastSample, retryCountersAt;
        private int resetRequested;
        private bool disposed;

        internal void RequestReset() { Interlocked.Exchange(ref resetRequested, 1); }

        internal HardwareReading Read()
        {
            if (disposed) throw new ObjectDisposedException("HardwareReader");
            long now = Stopwatch.GetTimestamp();
            bool reset = Interlocked.Exchange(ref resetRequested, 0) != 0;
            if (reset || (lastSample != 0 && (now - lastSample) / (double)Stopwatch.Frequency > 10))
            {
                cpu.Reset();
                CloseCounters();
                retryCountersAt = 0;
                if (reset) nvidia.Reset();
            }
            lastSample = now;
            var reading = new HardwareReading();
            ulong idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user)) reading.CpuLoad = cpu.Sample(idle, kernel, user);
            else cpu.Reset();

            if (query == IntPtr.Zero && now >= retryCountersAt)
            {
                retryCountersAt = now + 30 * Stopwatch.Frequency;
                if (PdhOpenQuery(null, UIntPtr.Zero, out query) == 0)
                {
                    frequency = AddCounter(@"\Processor Information(_Total)\Processor Frequency");
                    performance = AddCounter(@"\Processor Information(_Total)\% Processor Performance");
                    temperature = AddCounter(ThermalCounter);
                }
                else query = IntPtr.Zero;
            }
            if (query != IntPtr.Zero)
            {
                if (PdhCollectQueryData(query) == 0)
                {
                    reading.CpuMHz = HardwareReading.CpuFrequency(Value(frequency), Value(performance));
                    reading.SystemTemperature = HardwareReading.Celsius(Value(temperature));
                }
                else CloseCounters();
            }
            nvidia.ReadInto(reading);
            return reading;
        }

        private IntPtr AddCounter(string path)
        {
            IntPtr counter;
            return PdhAddEnglishCounter(query, path, UIntPtr.Zero, out counter) == 0 ? counter : IntPtr.Zero;
        }

        private static double? Value(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return null;
            PdhValue value;
            // DOUBLE | NOSCALE | NOCAP100: preserve raw Kelvin units and CPU turbo above 100%.
            if (PdhGetFormattedCounterValue(counter, 0x9200, IntPtr.Zero, out value) != 0 || value.Status > 1)
                return null;
            return value.Number;
        }

        private void CloseCounters()
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            query = frequency = performance = temperature = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CloseCounters();
            nvidia.Dispose();
        }

        // PDH's union starts at byte 8 on both x86 and x64, even though CStatus is a DWORD.
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        internal struct PdhValue
        {
            [FieldOffset(0)] internal uint Status;
            [FieldOffset(8)] internal double Number;
        }

        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
        private static extern uint PdhOpenQuery(string source, UIntPtr data, out IntPtr query);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
        private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr data, out IntPtr counter);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhValue value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    }

    internal sealed class NvidiaReader : IDisposable
    {
        private bool initialized;
        private IntPtr device;
        private long retryAt;
        private string name = "NVIDIA GPU";

        internal void ReadInto(HardwareReading reading)
        {
            long now = Stopwatch.GetTimestamp();
            try
            {
                if (!initialized)
                {
                    if (now < retryAt) return;
                    retryAt = now + 30 * Stopwatch.Frequency;
                    if (nvmlInit_v2() != 0) return;
                    initialized = true;
                    if (nvmlDeviceGetHandleByIndex_v2(0, out device) != 0) { Shutdown(); return; }
                    var buffer = new StringBuilder(128);
                    if (nvmlDeviceGetName(device, buffer, (uint)buffer.Capacity) == 0) name = buffer.ToString();
                }
                reading.GpuName = name;
                uint clock, temperature;
                Utilization utilization;
                uint loadStatus = nvmlDeviceGetUtilizationRates(device, out utilization);
                uint clockStatus = nvmlDeviceGetClockInfo(device, 0, out clock); // Graphics clock, MHz.
                uint temperatureStatus = nvmlDeviceGetTemperature(device, 0, out temperature); // GPU sensor, Celsius.
                if (loadStatus == 0) reading.GpuLoad = HardwareReading.InRange(utilization.Gpu, 0, 100);
                if (clockStatus == 0) reading.GpuMHz = HardwareReading.InRange(clock, 0, 20000);
                if (temperatureStatus == 0) reading.GpuTemperature = HardwareReading.InRange(temperature, 0, 150);
                if ((loadStatus != 0 && clockStatus != 0 && temperatureStatus != 0) ||
                    loadStatus == 15 || clockStatus == 15 || temperatureStatus == 15) // GPU lost / driver reset.
                {
                    Shutdown();
                    retryAt = now + 30 * Stopwatch.Frequency;
                }
            }
            catch (DllNotFoundException) { MissingLibrary(now); }
            catch (EntryPointNotFoundException) { MissingLibrary(now); }
            catch (BadImageFormatException) { MissingLibrary(now); }
        }

        private void MissingLibrary(long now)
        {
            Shutdown();
            retryAt = now + 30 * Stopwatch.Frequency;
        }

        private void Shutdown()
        {
            if (initialized)
            {
                initialized = false;
                try { nvmlShutdown(); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                catch (BadImageFormatException) { }
            }
            device = IntPtr.Zero;
        }

        internal void Reset() { Shutdown(); retryAt = 0; }
        public void Dispose() { Shutdown(); }

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization { internal uint Gpu, Memory; }

        // Load only the driver copy in Windows System32, never a DLL beside the widget or in the working directory.
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint nvmlInit_v2();
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint nvmlShutdown();
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern uint nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint nvmlDeviceGetClockInfo(IntPtr device, uint type, out uint clock);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint nvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint temperature);
    }

    // Called from the UI thread. Native reads and cleanup always run on the pool, with at most one read in flight.
    internal sealed class HardwarePoller : IDisposable
    {
        private readonly Func<HardwareReading> read;
        private readonly Action close;
        private Task<HardwareReading> pending;
        private bool disposed;
        internal Task Cleanup { get; private set; }

        internal HardwarePoller(Func<HardwareReading> read, Action close) { this.read = read; this.close = close; }

        internal Task<HardwareReading> Begin()
        {
            if (disposed || pending != null) return null;
            pending = Task.Run(read);
            return pending;
        }

        internal void Complete(Task<HardwareReading> completed)
        {
            if (Object.ReferenceEquals(pending, completed)) pending = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Cleanup = pending == null ? Task.Run(close) :
                pending.ContinueWith(delegate(Task<HardwareReading> task)
                {
                    if (task.IsFaulted) task.Exception.Handle(delegate { return true; });
                    close();
                }, TaskScheduler.Default);
        }
    }
}
