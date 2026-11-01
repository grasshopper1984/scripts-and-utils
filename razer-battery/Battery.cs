using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RazerBattery
{
    internal static class BatteryProtocol
    {
        internal const byte LevelCommand = 0x80;
        internal const byte ChargeCommand = 0x84;

        internal static byte[] Request(byte command, int length)
        {
            if (command != LevelCommand && command != ChargeCommand)
                throw new ArgumentOutOfRangeException("command");
            if (length < 91) throw new ArgumentOutOfRangeException("length");
            var report = new byte[length]; // Byte 0 is the Windows HID report ID.
            report[2] = 0x1F;
            report[6] = 2;
            report[7] = 7;
            report[8] = command;
            report[89] = Checksum(report);
            return report;
        }

        internal static byte Checksum(byte[] report)
        {
            byte checksum = 0;
            for (int i = 3; i < 89; i++) checksum ^= report[i];
            return checksum;
        }

        internal static bool TryRead(byte[] report, byte command, out byte value, out string error)
        {
            value = 0;
            error = "Invalid battery response";
            if (report == null || report.Length < 91 || report[0] != 0 || report[2] != 0x1F ||
                report[3] != 0 || report[4] != 0 || report[5] != 0 || report[6] != 2 ||
                report[7] != 7 || report[8] != command || report[89] != Checksum(report)) return false;
            if (report[1] != 2)
            {
                error = report[1] == 4 ? "Mouse asleep or unavailable" :
                    report[1] == 1 ? "Mouse busy" : "Mouse did not answer";
                return false;
            }
            if (command == ChargeCommand && report[10] > 1) return false;
            value = report[10];
            error = null;
            return true;
        }

        internal static int Percent(byte raw)
        {
            return (raw * 100 + 127) / 255;
        }
    }

    internal sealed class BatteryReading
    {
        internal int? Percent;
        internal bool? Charging;
        internal string Connection;
        internal string Error;

        internal static BatteryReading Unavailable(string error)
        {
            return new BatteryReading { Error = error };
        }
    }

    internal sealed class BatteryReader
    {
        internal BatteryReading Read()
        {
            try
            {
                var devices = NativeHid.FindMice();
                string error = "Connect Cobra HyperSpeed";
                foreach (HidDevice device in devices)
                {
                    using (SafeFileHandle handle = NativeHid.Open(device.Path))
                    {
                        if (handle.IsInvalid) { error = "Mouse unavailable"; continue; }
                        byte level;
                        if (!Query(handle, device.ReportLength, BatteryProtocol.LevelCommand, out level, out error))
                            continue;
                        byte charging;
                        string chargeError;
                        bool hasCharge = Query(handle, device.ReportLength, BatteryProtocol.ChargeCommand,
                            out charging, out chargeError);
                        return new BatteryReading { Percent = BatteryProtocol.Percent(level),
                            Charging = hasCharge ? (bool?)(charging == 1) : null,
                            Connection = device.ProductId == 0x00DA ? "USB" : "Wireless" };
                    }
                }
                return BatteryReading.Unavailable(error);
            }
            catch (Win32Exception) { return BatteryReading.Unavailable("Windows HID unavailable"); }
        }

        private static bool Query(SafeFileHandle handle, int length, byte command, out byte value, out string error)
        {
            value = 0;
            error = "Mouse did not answer";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var request = BatteryProtocol.Request(command, length);
                if (!NativeHid.HidD_SetFeature(handle, request, request.Length))
                {
                    error = "HID request failed (" + Marshal.GetLastWin32Error() + ")";
                    return false;
                }
                // Cobra wired and receiver need at least 31 ms before GET_REPORT.
                Thread.Sleep(35);
                var response = new byte[length];
                if (!NativeHid.HidD_GetFeature(handle, response, response.Length))
                {
                    error = "HID read failed (" + Marshal.GetLastWin32Error() + ")";
                    return false;
                }
                if (BatteryProtocol.TryRead(response, command, out value, out error)) return true;
                if (attempt < 2) Thread.Sleep(10);
            }
            return false;
        }
    }

    internal sealed class BatteryState
    {
        internal int? Percent;
        internal bool? Charging;
        internal bool IsLive;
        internal DateTime? LastRead;
        internal string Detail = "Checking mouse...";

        internal void Apply(BatteryReading reading, DateTime now)
        {
            IsLive = reading.Percent.HasValue;
            Charging = IsLive ? reading.Charging : null;
            if (IsLive)
            {
                Percent = reading.Percent;
                LastRead = now;
                Detail = Charging == true ? "Charging" :
                    Charging == null ? "Charge status unknown" : reading.Connection;
            }
            else Detail = Percent.HasValue ? "Last known" : "Not connected";
        }

        internal string Tooltip(BatteryReading reading)
        {
            string value = Percent.HasValue ? Percent.Value + "%" : "Battery unavailable";
            return "Cobra HyperSpeed | " + value + "\n" +
                (IsLive ? Detail : (reading.Error ?? "Mouse unavailable") +
                    (LastRead.HasValue ? "\nLast read " + LastRead.Value.ToLocalTime().ToString("g") : ""));
        }
    }
}
