using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace RazerBattery
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool first;
            using (var instance = new Mutex(true, @"Local\ScriptsAndUtils.RazerBatteryWidget", out first))
            {
                if (!first)
                {
                    BatteryWidget.PostMessage(new IntPtr(0xFFFF), BatteryWidget.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "RazerBatteryWidget", "settings.txt");
                    var reader = new BatteryReader();
                    using (var widget = new BatteryWidget(reader.Read, settings, new HardwareReader())) Application.Run(widget);
                }
                finally { instance.ReleaseMutex(); }
            }
        }
    }
}
