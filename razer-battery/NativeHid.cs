using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RazerBattery
{
    internal sealed class HidDevice
    {
        internal string Path;
        internal ushort ProductId;
        internal ushort ReportLength;
    }

    // Windows' in-box HID API: no driver, SDK, package, or read/write mouse handle.
    internal static class NativeHid
    {
        internal static List<HidDevice> FindMice()
        {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr devices = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
            if (devices == new IntPtr(-1)) throw new Win32Exception();
            var found = new List<HidDevice>();
            try
            {
                for (uint index = 0; ; index++)
                {
                    var info = new InterfaceData();
                    info.Size = Marshal.SizeOf(typeof(InterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref hidGuid, index, ref info))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 259) break; // ERROR_NO_MORE_ITEMS
                        throw new Win32Exception(error);
                    }
                    uint length;
                    SetupDiGetDeviceInterfaceDetail(devices, ref info, IntPtr.Zero, 0, out length, IntPtr.Zero);
                    if (length < 8) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)length);
                    try
                    {
                        // cbSize includes alignment; DevicePath itself always starts at byte 4.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(devices, ref info, detail, length, out length, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path == null || path.IndexOf("vid_1532", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        using (SafeFileHandle handle = Open(path))
                        {
                            if (handle.IsInvalid) continue;
                            var attributes = new Attributes();
                            attributes.Size = Marshal.SizeOf(typeof(Attributes));
                            if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != 0x1532 ||
                                !IsSupported(attributes.ProductId)) continue;
                            IntPtr preparsed;
                            if (!HidD_GetPreparsedData(handle, out preparsed)) continue;
                            try
                            {
                                Caps caps;
                                if (HidP_GetCaps(preparsed, out caps) != 0x00110000) continue;
                                // The Cobra's protocol is on the mouse collection on USB interface 0.
                                if (caps.UsagePage != 1 || caps.Usage != 2 || caps.FeatureLength < 91 ||
                                    path.IndexOf("mi_00", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                found.Add(new HidDevice { Path = path, ProductId = attributes.ProductId,
                                    ReportLength = caps.FeatureLength });
                            }
                            finally { HidD_FreePreparsedData(preparsed); }
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devices); }
            // Prefer the cable when both it and the receiver are connected.
            found.Sort(delegate(HidDevice a, HidDevice b) { return a.ProductId.CompareTo(b.ProductId); });
            return found;
        }

        internal static bool IsSupported(ushort productId)
        {
            return productId == 0x00DA || productId == 0x00DB;
        }

        internal static SafeFileHandle Open(string path)
        {
            // Access 0 permits feature reports without taking ownership of mouse input.
            return CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InterfaceData
        {
            internal int Size;
            internal Guid ClassGuid;
            internal uint Flags;
            internal UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Attributes
        {
            internal int Size;
            internal ushort VendorId, ProductId, Version;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Caps
        {
            internal ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
            internal ushort LinkNodes, InputButtons, InputValues, InputIndices;
            internal ushort OutputButtons, OutputValues, OutputIndices;
            internal ushort FeatureButtons, FeatureValues, FeatureIndices;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int length);
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, int length);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid guid,
            uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref InterfaceData data,
            IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    }
}
