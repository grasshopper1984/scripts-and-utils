using System;
using System.Diagnostics;
using System.Threading;

namespace RazerBattery
{
    internal static class HardwareProbe
    {
        private static int Main()
        {
            using (var reader = new HardwareReader())
            {
                HardwareReading reading = null;
                for (int i = 0; i < 3; i++)
                {
                    var time = Stopwatch.StartNew();
                    reading = reader.Read();
                    time.Stop();
                    Console.WriteLine("CPU {0}, {1}; {2}: {3}, {4}, {5}; System temp {6}; read {7:F1} ms",
                        HardwareReading.Percent(reading.CpuLoad), HardwareReading.Clock(reading.CpuMHz),
                        reading.GpuName, HardwareReading.Percent(reading.GpuLoad), HardwareReading.Clock(reading.GpuMHz),
                        HardwareReading.Temperature(reading.GpuTemperature), HardwareReading.Temperature(reading.SystemTemperature),
                        time.Elapsed.TotalMilliseconds);
                    if (i < 2) Thread.Sleep(2000);
                }
                Console.WriteLine("System temperature is firmware zone TZ00, not a verified CPU package sensor.");
                return reading.CpuLoad.HasValue && reading.CpuMHz.HasValue && reading.GpuLoad.HasValue &&
                    reading.GpuMHz.HasValue && reading.GpuTemperature.HasValue && reading.SystemTemperature.HasValue ? 0 : 1;
            }
        }
    }
}
