using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace QQToastTray
{
    static class Program
    {
        private static Mutex _mutex;

        private static bool HasArg(string[] args, string keyword)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.IsNullOrEmpty(args[i]) &&
                    args[i].IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TrySignalRunningInstance()
        {
            try
            {
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(AppMeta.EXIT_EVENT_NAME))
                {
                    ev.Set();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        [STAThread]
        static int Main()
        {
            try { NativeMethods.SetProcessDPIAware(); } catch { }

            string[] args = Environment.GetCommandLineArgs();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exitFlagPath = Path.Combine(baseDir, "tray-exit.flag");

            // Elevated helper modes used when QQNT resides in Program Files
            if (HasArg(args, "--deploy-bridge"))
            {
                var logger = new RotatingLogger(Path.Combine(baseDir, "tray-debug.log"));
                var deployer = new BridgeDeployer(baseDir, logger);
                DeployResult res = deployer.DeployAllVersions(false);
                return res.AccessDeniedCount == 0 ? 0 : 1;
            }

            if (HasArg(args, "--remove-bridge"))
            {
                var logger = new RotatingLogger(Path.Combine(baseDir, "tray-debug.log"));
                var deployer = new BridgeDeployer(baseDir, logger);
                return deployer.RemoveAllBridges(false) ? 0 : 1;
            }

            // Graceful external shutdown request: QQToastTray.exe --exit
            if (HasArg(args, "--exit"))
            {
                if (!TrySignalRunningInstance())
                {
                    try { if (File.Exists(exitFlagPath)) File.Delete(exitFlagPath); } catch { }
                }
                return 0;
            }

            // Ignore COM resurrection from old Action Center notifications after explicit user exit
            if (HasArg(args, "-ToastActivated") && File.Exists(exitFlagPath))
            {
                return 0;
            }

            bool createdNew;
            _mutex = new Mutex(true, AppMeta.SINGLETON_MUTEX_NAME, out createdNew);
            if (!createdNew)
            {
                return 0;
            }

            try { if (File.Exists(exitFlagPath)) File.Delete(exitFlagPath); } catch { }

            var fatalLog = new RotatingLogger(Path.Combine(baseDir, "tray-debug.log"));
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                fatalLog.Write("[FATAL UnhandledException] " + e.ExceptionObject);
            };
            Application.ThreadException += (s, e) =>
            {
                fatalLog.Write("[FATAL ThreadException] " + e.Exception);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
            return 0;
        }
    }
}
