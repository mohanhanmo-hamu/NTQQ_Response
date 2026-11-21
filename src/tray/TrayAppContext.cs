using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace QQToastTray
{
    public class TrayApplicationContext : ApplicationContext
    {
        private readonly string _baseDir;
        private readonly string _exitFlagPath;
        private readonly RotatingLogger _logger;
        private readonly ConfigStore _configStore;
        private readonly BridgeDeployer _deployer;
        private readonly LocalHttpServer _httpServer;
        private readonly ToastManager _toastManager;

        private readonly object _queueLock = new object();
        private readonly List<ReplyCommand> _pendingReplies = new List<ReplyCommand>();

        private SynchronizationContext _uiContext;
        private NotifyIcon _trayIcon;
        private Icon _currentIcon;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _menuToggleItem;
        private TrayFlyoutForm _flyout;
        private bool _flyoutDarkState;

        private System.Windows.Forms.Timer _statusTimer;
        private System.Windows.Forms.Timer _versionWatchTimer;
        private EventWaitHandle _exitEvent;
        private int _exitRequested = 0;

        public AppConfig Config { get; private set; }
        public BridgeTelemetry Telemetry { get; private set; }
        public QQInstallationReport LatestReport { get; private set; }

        public TrayApplicationContext()
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _exitFlagPath = Path.Combine(_baseDir, "tray-exit.flag");
            _logger = new RotatingLogger(Path.Combine(_baseDir, "tray-debug.log"));
            _configStore = new ConfigStore(Path.Combine(_baseDir, "config.json"));
            _deployer = new BridgeDeployer(_baseDir, _logger);
            Telemetry = new BridgeTelemetry();

            _logger.Write("=== QQToastTray v" + AppMeta.Version + " starting ===");
            Config = _configStore.Load();

            _toastManager = new ToastManager(Config, Telemetry, _logger, EnqueueReply);
            _toastManager.RegisterComActivatorAndAumid();

            _httpServer = new LocalHttpServer(Config, Telemetry, _logger, OnIncomingMessage, DequeuePendingReplies);

            LatestReport = _deployer.InspectInstallation();
            if (Config.AutoSyncVersions && LatestReport.Found && LatestReport.PendingSyncCount > 0)
            {
                _deployer.DeployAllVersions(false);
                LatestReport = _deployer.InspectInstallation();
            }

            InitTray();
            _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _toastManager.SetUiContext(_uiContext);

            _httpServer.Start();
            RegisterExitSignalWatcher();

            _statusTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            _statusTimer.Tick += (s, e) => UpdateStatusUi();
            _statusTimer.Start();

            // Periodically watch for NTQQ background hot-updates (e.g. readyVersion)
            _versionWatchTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            _versionWatchTimer.Tick += (s, e) => CheckVersionsBackground();
            _versionWatchTimer.Start();
        }

        private void CheckVersionsBackground()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var report = _deployer.InspectInstallation();
                    if (Config.AutoSyncVersions && report.Found && report.PendingSyncCount > 0)
                    {
                        _deployer.DeployAllVersions(false);
                        report = _deployer.InspectInstallation();
                    }
                    LatestReport = report;
                    if (_uiContext != null)
                    {
                        _uiContext.Post(__ =>
                        {
                            if (_flyout != null && !_flyout.IsDisposed && _flyout.Visible)
                            {
                                _flyout.RefreshState();
                            }
                        }, null);
                    }
                }
                catch { }
            });
        }

        private void OnIncomingMessage(IncomingMessage msg)
        {
            _toastManager.ShowQuickReplyToast(msg);
        }

        private void EnqueueReply(ReplyCommand cmd)
        {
            lock (_queueLock)
            {
                _pendingReplies.Add(cmd);
            }
        }

        private List<ReplyCommand> DequeuePendingReplies()
        {
            lock (_queueLock)
            {
                if (_pendingReplies.Count == 0) return null;
                var list = new List<ReplyCommand>(_pendingReplies);
                _pendingReplies.Clear();
                return list;
            }
        }

        // Leak-free GDI+ tray icon creation: clones from HICON and destroys native handle immediately.
        private static Icon CreateTrayIconSafe(bool active, bool connected)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    Color bgColor = active
                        ? (connected ? Color.FromArgb(255, 0, 120, 212) : Color.FromArgb(255, 38, 150, 232))
                        : Color.FromArgb(255, 122, 122, 122);

                    using (Brush bgBrush = new SolidBrush(bgColor))
                    {
                        g.FillEllipse(bgBrush, 2, 2, 28, 28);
                    }

                    using (Brush whiteBrush = new SolidBrush(Color.White))
                    {
                        g.FillEllipse(whiteBrush, 8, 8, 16, 12);
                        Point[] tail = new Point[]
                        {
                            new Point(11, 18),
                            new Point(8, 24),
                            new Point(16, 19)
                        };
                        g.FillPolygon(whiteBrush, tail);
                    }

                    // Small status indicator dot at bottom-right when connected
                    if (active && connected)
                    {
                        using (Brush ring = new SolidBrush(Color.White))
                        using (Brush dot = new SolidBrush(Color.FromArgb(255, 46, 204, 113)))
                        {
                            g.FillEllipse(ring, 20, 20, 10, 10);
                            g.FillEllipse(dot, 22, 22, 6, 6);
                        }
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    NativeMethods.DestroyIcon(hIcon);
                }
            }
        }

        private void RefreshTrayIconGraphic()
        {
            Icon next = CreateTrayIconSafe(Config.Enabled, Telemetry.IsConnected);
            Icon prev = _currentIcon;
            _currentIcon = next;
            if (_trayIcon != null) _trayIcon.Icon = next;
            if (prev != null) prev.Dispose();
        }

        private void InitTray()
        {
            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);

            var openPanelItem = new ToolStripMenuItem("打开控制面板")
            {
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };
            openPanelItem.Click += (s, e) => ToggleFlyout(true);

            _menuToggleItem = new ToolStripMenuItem("启用快捷回复通知")
            {
                Checked = Config.Enabled,
                CheckOnClick = true
            };
            _menuToggleItem.CheckedChanged += (s, e) => SetEnabled(_menuToggleItem.Checked);

            var repairItem = new ToolStripMenuItem("修复桥接并重启 QQ");
            repairItem.Click += (s, e) => RepairAndRestartQQ();

            var removeItem = new ToolStripMenuItem("卸载 QQ 桥接组件");
            removeItem.Click += (s, e) => RemoveBridgeInteractive();

            var exitItem = new ToolStripMenuItem("退出程序");
            exitItem.Click += (s, e) => ExitApplication();

            _menu.Items.Add(openPanelItem);
            _menu.Items.Add(_menuToggleItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(repairItem);
            _menu.Items.Add(removeItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _currentIcon = CreateTrayIconSafe(Config.Enabled, false);
            _trayIcon = new NotifyIcon
            {
                Icon = _currentIcon,
                Text = "QQ 快捷回复助手 v" + AppMeta.Version,
                ContextMenuStrip = _menu,
                Visible = true
            };

            _trayIcon.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ToggleFlyout(false);
                }
            };

            ConfigStore.ApplyNativeQQNotificationSuppression(Config.Enabled && Config.SuppressNativeQQBanner);
        }

        private void ToggleFlyout(bool forceShow)
        {
            bool darkNow = FluentPalette.IsSystemDarkMode();
            if (_flyout != null && !_flyout.IsDisposed && _flyoutDarkState != darkNow)
            {
                _flyout.Close();
                _flyout.Dispose();
                _flyout = null;
            }

            if (_flyout == null || _flyout.IsDisposed)
            {
                _flyoutDarkState = darkNow;
                _flyout = new TrayFlyoutForm(this);
            }

            if (!forceShow && _flyout.Visible)
            {
                _flyout.Hide();
            }
            else
            {
                LatestReport = _deployer.InspectInstallation();
                _flyout.ShowAtTaskbar();
            }
        }

        private bool _lastConnectedVisual = false;

        private void UpdateStatusUi()
        {
            bool connected = Telemetry.IsConnected;
            if (connected != _lastConnectedVisual)
            {
                _lastConnectedVisual = connected;
                RefreshTrayIconGraphic();
            }

            string tip = connected
                ? ("QQ 快捷回复助手 v" + AppMeta.Version + " (已连接 " + Telemetry.RunningQQVersion + ")")
                : ("QQ 快捷回复助手 v" + AppMeta.Version + " (等待 QQ 连接)");
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (_trayIcon != null) _trayIcon.Text = tip;

            if (_flyout != null && !_flyout.IsDisposed && _flyout.Visible)
            {
                _flyout.RefreshState();
            }
        }

        // --- Settings mutators invoked by Flyout & Context Menu ---
        public void SetEnabled(bool enabled)
        {
            if (Config.Enabled == enabled) return;
            Config.Enabled = enabled;
            _configStore.Save(Config);
            if (_menuToggleItem != null && _menuToggleItem.Checked != enabled) _menuToggleItem.Checked = enabled;
            RefreshTrayIconGraphic();
            ConfigStore.ApplyNativeQQNotificationSuppression(Config.Enabled && Config.SuppressNativeQQBanner);
        }

        public void SetGroupNotify(bool val) { Config.EnableGroupNotify = val; _configStore.Save(Config); }
        public void SetMuteWhenForeground(bool val) { Config.MuteWhenQQForeground = val; _configStore.Save(Config); }

        public void SetSuppressNative(bool val)
        {
            Config.SuppressNativeQQBanner = val;
            _configStore.Save(Config);
            ConfigStore.ApplyNativeQQNotificationSuppression(Config.Enabled && Config.SuppressNativeQQBanner);
        }

        public void SetFollowQQStartup(bool val) { Config.FollowQQStartup = val; _configStore.Save(Config); }
        public void SetAutoSyncVersions(bool val) { Config.AutoSyncVersions = val; _configStore.Save(Config); }
        public void SetVerboseLog(bool val) { Config.VerboseLog = val; _configStore.Save(Config); }
        public void SetMaxHistoryLines(int lines) { Config.MaxHistoryLines = lines; _configStore.Save(Config); }
        public void SetMaxCharsPerLine(int chars) { Config.MaxCharsPerLine = chars; _configStore.Save(Config); }

        public void SendPreviewToast()
        {
            _toastManager.SendPreviewToast();
        }

        public void SyncBridgeVersionsInteractive()
        {
            DeployResult res = _deployer.DeployAllVersions(true);
            LatestReport = _deployer.InspectInstallation();
            if (res.LockedByProcessCount > 0)
            {
                var ans = MessageBox.Show(
                    "检测到有 " + res.LockedByProcessCount + " 个正在运行中的 QQ 版本文件被占用（无法热替换 version.dll）。\n\n是否立即重启 QQ 完成桥接更新？",
                    AppMeta.APP_DISPLAY_NAME,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (ans == DialogResult.Yes)
                {
                    RepairAndRestartQQ();
                }
            }
            else if (res.UpdatedCount > 0)
            {
                MessageBox.Show("已成功同步 " + res.UpdatedCount + " 个 NTQQ 版本的桥接组件。", AppMeta.APP_DISPLAY_NAME, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        public void RepairAndRestartQQ()
        {
            _deployer.RepairAndRestartQQ();
            LatestReport = _deployer.InspectInstallation();
        }

        public void RemoveBridgeInteractive()
        {
            var ans = MessageBox.Show(
                "此操作将关闭 QQ 并删除注入到 NTQQ 版本目录的桥接文件，恢复 QQ 原生通知。\n\n是否继续？",
                AppMeta.APP_DISPLAY_NAME,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (ans != DialogResult.Yes) return;

            bool ok = _deployer.RemoveAllBridges(true);
            LatestReport = _deployer.InspectInstallation();
            if (ok)
            {
                MessageBox.Show("已成功卸载 QQ 桥接组件，QQ 已恢复纯净原版状态。", AppMeta.APP_DISPLAY_NAME, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("部分文件删除失败，请以管理员身份运行或关闭 QQ 后重试。", AppMeta.APP_DISPLAY_NAME, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RegisterExitSignalWatcher()
        {
            try
            {
                bool createdNew;
                _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppMeta.EXIT_EVENT_NAME, out createdNew);
                ThreadPool.RegisterWaitForSingleObject(_exitEvent, (state, timedOut) =>
                {
                    if (timedOut) return;
                    _logger.Write("Received graceful exit signal.");
                    if (_uiContext != null) _uiContext.Post(_ => ExitApplication(), null);
                    else ExitApplication();
                }, null, Timeout.Infinite, false);
            }
            catch (Exception ex)
            {
                _logger.Write("RegisterExitSignalWatcher notice: " + ex.Message);
            }
        }

        private void WriteExitFlag()
        {
            try
            {
                long ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                File.WriteAllText(_exitFlagPath, ms.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public void ExitApplication()
        {
            if (Interlocked.Exchange(ref _exitRequested, 1) != 0) return;

            WriteExitFlag();
            _logger.Write("Exit requested by user. Shutting down cleanly.");

            try { if (_statusTimer != null) { _statusTimer.Stop(); _statusTimer.Dispose(); } } catch { }
            try { if (_versionWatchTimer != null) { _versionWatchTimer.Stop(); _versionWatchTimer.Dispose(); } } catch { }
            try { if (_flyout != null && !_flyout.IsDisposed) { _flyout.Close(); _flyout.Dispose(); } } catch { }

            _httpServer.Stop();
            _toastManager.Shutdown();
            ConfigStore.ApplyNativeQQNotificationSuppression(false);

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            if (_currentIcon != null)
            {
                _currentIcon.Dispose();
                _currentIcon = null;
            }

            Application.Exit();
            Environment.Exit(0);
        }
    }
}
