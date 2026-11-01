using System;

namespace RazerBattery
{
    internal static class Probe
    {
        internal static int Run()
        {
            var devices = NativeHid.FindMice();
            foreach (HidDevice device in devices)
                Console.WriteLine("Cobra HyperSpeed PID={0:X4}, feature bytes={1}", device.ProductId, device.ReportLength);
            var result = new BatteryReader().Read();
            if (!result.Percent.HasValue)
            {
                Console.WriteLine(result.Error);
                return 1;
            }
            Console.WriteLine("Battery: {0}%; charging: {1}; connection: {2}", result.Percent,
                result.Charging.HasValue ? result.Charging.Value.ToString() : "unknown", result.Connection);
            return 0;
        }

        private static int Main() { return Run(); }
    }
}
