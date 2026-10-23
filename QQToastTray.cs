using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace QQToastTray
{
    public class AppConfig
    {
        public bool Enabled { get; set; }
        public bool EnableGroupNotify { get; set; }
        public bool MuteWhenQQForeground { get; set; }
        public bool SuppressNativeQQBanner { get; set; }
        public bool FollowQQStartup { get; set; }
        public int MaxHistoryLines { get; set; }
        public int MaxCharsPerLine { get; set; }
        public int LocalPort { get; set; }

        public AppConfig()
        {
            Enabled = true;
            EnableGroupNotify = true;
            MuteWhenQQForeground = true;
            SuppressNativeQQBanner = true;
            FollowQQStartup = true;
            MaxHistoryLines = 3;
            MaxCharsPerLine = 50;
            LocalPort = 39876;
        }
    }

    public class IncomingMessage
    {
        public string msgId { get; set; }
        public int chatType { get; set; } // 1: C2C, 2: Group
        public string peerUid { get; set; }
        public string peerName { get; set; }
        public string senderUid { get; set; }
        public string senderName { get; set; }
        public string content { get; set; }
        public string avatarPath { get; set; }
        public string imagePath { get; set; }
        public bool isImageUpdate { get; set; }
    }

    public class ReplyCommand
    {
        public string id { get; set; }
        public string action { get; set; } // "reply" or "openChat"
        public int chatType { get; set; }
        public string peerUid { get; set; }
        public string content { get; set; }

        public ReplyCommand()
        {
            action = "reply";
        }
    }

    public class ConversationState
    {
        public int ChatType;
        public string PeerUid;
        public string PeerName;
        public string AvatarPath;
        public string ImagePath;
        public List<string> Lines = new List<string>();
        public DateTime LastUpdated = DateTime.Now;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFICATION_USER_INPUT_DATA
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Key;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Value;
    }

    [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), ComVisible(true), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationActivationCallback
    {
        void Activate(
            [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [In, MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] NOTIFICATION_USER_INPUT_DATA[] data,
            [In, MarshalAs(UnmanagedType.U4)] uint dataCount);
    }

    [ComVisible(true), Guid("E8B84582-4D5A-49A6-9375-52F570296A42"), ClassInterface(ClassInterfaceType.None)]
    public class QQToastNotificationActivator : INotificationActivationCallback
    {
        public void Activate(string appUserModelId, string invokedArgs, NOTIFICATION_USER_INPUT_DATA[] data, uint dataCount)
        {
            try
            {
                string replyText = "";
                if (data != null)
                {
                    for (int i = 0; i < dataCount && i < data.Length; i++)
                    {
                        if (data[i].Key == "replyText")
                        {
                            replyText = data[i].Value ?? "";
                            break;
                        }
                    }
                }
                if (TrayApplicationContext.Instance != null)
                {
                    TrayApplicationContext.Instance.HandleActivatedReply(invokedArgs ?? "", replyText, "COM_Activator");
                }
            }
            catch { }
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        public static TrayApplicationContext Instance { get; private set; }

        private NotifyIcon _trayIcon;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _statusItem;
        private ToolStripMenuItem _enableItem;
        private ToolStripMenuItem _groupItem;
        private ToolStripMenuItem _muteFgItem;
        private ToolStripMenuItem _suppressNativeItem;
        private ToolStripMenuItem _stackingMenu;
        private ToolStripMenuItem _followQQItem;
        private ToolStripMenuItem _autoStartItem;

        private AppConfig _config;
        private string _configPath;
        private string _bridgeJsPath;
        private string _logPath;
        private TcpListener _tcpListener;
        private Thread _serverThread;
        private volatile bool _running = true;
        private SynchronizationContext _uiContext;

        private readonly object _queueLock = new object();
        private List<ReplyCommand> _pendingReplies = new List<ReplyCommand>();
        private readonly HashSet<string> _processedReplyTokens = new HashSet<string>();
        private DateTime _lastBridgeHeartbeat = DateTime.MinValue;
        private System.Windows.Forms.Timer _statusTimer;

        // Live Toast Banner state & multi-message history management
        private readonly object _toastLock = new object();
        private readonly Dictionary<string, ConversationState> _conversations = new Dictionary<string, ConversationState>();
        private readonly List<string> _queuedSessionKeys = new List<string>();
        private string _activeBannerSessionKey = null;
        private string _activeBannerImagePath = null;
        private DateTime _activeBannerShownAt = DateTime.MinValue;
        private string _latestOtherSessionAlert = "";
        private uint _sequenceNumber = 1;
        private readonly List<ToastNotification> _activeToasts = new List<ToastNotification>();

        public const string APP_AUMID = "Tencent.QQNT.QuickReply";
        public const string ACTIVATOR_CLSID = "{E8B84582-4D5A-49A6-9375-52F570296A42}";
        private const string LIVE_TAG = "QQLiveBanner";
        private const string LIVE_GROUP = "QQQuickReply";
        private string _activeAumid = APP_AUMID;
        private uint _comCookie = 0;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("ole32.dll")]
        private static extern int CoRegisterClassObject(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
            [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
            uint dwClsContext,
            uint flags,
            out uint lpdwRegister);

        [DllImport("ole32.dll")]
        private static extern int CoRevokeClassObject(uint dwRegister);

        [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IClassFactory
        {
            [PreserveSig]
            int CreateInstance([In, MarshalAs(UnmanagedType.IUnknown)] object pUnkOuter, [In] ref Guid riid, [Out, MarshalAs(UnmanagedType.Interface)] out object ppvObject);
            [PreserveSig]
            int LockServer([In, MarshalAs(UnmanagedType.Bool)] bool fLock);
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr ptrVal;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            void Commit();
        }

        private class ActivatorClassFactory : IClassFactory
        {
            public int CreateInstance(object pUnkOuter, ref Guid riid, out object ppvObject)
            {
                if (pUnkOuter != null)
                {
                    ppvObject = null;
                    return unchecked((int)0x80040110);
                }
                ppvObject = new QQToastNotificationActivator();
                return 0;
            }

            public int LockServer(bool fLock)
            {
                return 0;
            }
        }

        public TrayApplicationContext()
        {
            Instance = this;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(baseDir, "config.json");
            _bridgeJsPath = Path.Combine(baseDir, "qq-bridge.js");
            _logPath = Path.Combine(baseDir, "tray-debug.log");

            Log("=== QQToastTray v1.1.0 starting ===");
            LoadConfig();
            RegisterComActivatorAndAumid();
            InitTray();
            _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            StartLocalServer();

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 2500;
            _statusTimer.Tick += (s, e) => UpdateBridgeStatus();
            _statusTimer.Start();
        }

        public void Log(string msg)
        {
            try
            {
                File.AppendAllText(_logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    _config = serializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                    if (_config.MaxHistoryLines < 1 || _config.MaxHistoryLines > 4)
                    {
                        _config.MaxHistoryLines = 3;
                    }
                    if (_config.MaxCharsPerLine <= 0)
                    {
                        _config.MaxCharsPerLine = 50;
                    }
                }
                else
                {
                    _config = new AppConfig();
                    SaveConfig();
                }
            }
            catch
            {
                _config = new AppConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                var json = serializer.Serialize(_config);
                File.WriteAllText(_configPath, json, Encoding.UTF8);
            }
            catch { }
        }

        private void RegisterComActivatorAndAumid()
        {
            try
            {
                string exePath = Application.ExecutablePath;

                try
                {
                    string startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    string shortcutPath = Path.Combine(startMenuDir, "QQ 快捷回复助手.lnk");

                    Type shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
                    IShellLinkW shellLink = (IShellLinkW)Activator.CreateInstance(shellLinkType);
                    shellLink.SetPath(exePath);
                    shellLink.SetArguments("");
                    shellLink.SetWorkingDirectory(AppDomain.CurrentDomain.BaseDirectory);
                    shellLink.SetDescription("QQ Windows 11 系统级快捷回复助手");

                    string qqExe = Path.Combine(GetQQInstallRoot(), "QQ.exe");
                    if (File.Exists(qqExe))
                    {
                        shellLink.SetIconLocation(qqExe, 0);
                    }

                    IPropertyStore propStore = (IPropertyStore)shellLink;

                    PROPERTYKEY pkeyAumid = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                    PROPVARIANT pvAumid = new PROPVARIANT { vt = 31, ptrVal = Marshal.StringToCoTaskMemUni(APP_AUMID) };
                    propStore.SetValue(ref pkeyAumid, ref pvAumid);
                    Marshal.FreeCoTaskMem(pvAumid.ptrVal);

                    PROPERTYKEY pkeyClsid = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 26 };
                    Guid clsidGuid = new Guid(ACTIVATOR_CLSID);
                    IntPtr pGuid = Marshal.AllocCoTaskMem(16);
                    Marshal.Copy(clsidGuid.ToByteArray(), 0, pGuid, 16);
                    PROPVARIANT pvClsid = new PROPVARIANT { vt = 72, ptrVal = pGuid };
                    propStore.SetValue(ref pkeyClsid, ref pvClsid);
                    Marshal.FreeCoTaskMem(pGuid);

                    propStore.Commit();
                    ((IPersistFile)shellLink).Save(shortcutPath, true);
                }
                catch (Exception exLnk)
                {
                    Log("Create shortcut notice: " + exLnk.Message);
                }

                using (var clsidKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + ACTIVATOR_CLSID + @"\LocalServer32"))
                {
                    if (clsidKey != null)
                    {
                        clsidKey.SetValue(null, "\"" + exePath + "\" -ToastActivated", RegistryValueKind.String);
                    }
                }

                using (var aumidKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + APP_AUMID))
                {
                    if (aumidKey != null)
                    {
                        aumidKey.SetValue("DisplayName", "QQ (快捷回复)", RegistryValueKind.String);
                        aumidKey.SetValue("CustomActivator", ACTIVATOR_CLSID, RegistryValueKind.String);
                    }
                }

                Guid clsid = new Guid(ACTIVATOR_CLSID);
                int hr = CoRegisterClassObject(clsid, new ActivatorClassFactory(), 4, 1, out _comCookie);
                Log("Registered COM ToastActivator: hr=" + hr + ", cookie=" + _comCookie);
                _activeAumid = APP_AUMID;
            }
            catch (Exception ex)
            {
                Log("RegisterComActivatorAndAumid error: " + ex.Message);
            }
        }

        private Icon CreateModernTrayIcon(bool active)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Color bgColor = active ? Color.FromArgb(255, 0, 153, 255) : Color.FromArgb(255, 120, 120, 120);
                using (Brush bgBrush = new SolidBrush(bgColor))
                {
                    g.FillEllipse(bgBrush, 2, 2, 28, 28);
                }

                using (Brush whiteBrush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(whiteBrush, 8, 9, 16, 12);
                    Point[] tail = new Point[] {
                        new Point(11, 19),
                        new Point(8, 24),
                        new Point(16, 20)
                    };
                    g.FillPolygon(whiteBrush, tail);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private void InitTray()
        {
            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

            _statusItem = new ToolStripMenuItem("状态: 正在检测 QQ 连接...")
            {
                Enabled = false
            };

            _enableItem = new ToolStripMenuItem("启用系统级快捷回复通知")
            {
                Checked = _config.Enabled,
                CheckOnClick = true
            };
            _enableItem.CheckedChanged += (s, e) =>
            {
                _config.Enabled = _enableItem.Checked;
                SaveConfig();
                _trayIcon.Icon = CreateModernTrayIcon(_config.Enabled);
                ApplyNativeQQNotificationSuppression(_config.Enabled && _config.SuppressNativeQQBanner);
            };

            _groupItem = new ToolStripMenuItem("接收未免打扰的群聊通知")
            {
                Checked = _config.EnableGroupNotify,
                CheckOnClick = true
            };
            _groupItem.CheckedChanged += (s, e) =>
            {
                _config.EnableGroupNotify = _groupItem.Checked;
                SaveConfig();
            };

            _muteFgItem = new ToolStripMenuItem("QQ 窗口在前台时静默")
            {
                Checked = _config.MuteWhenQQForeground,
                CheckOnClick = true
            };
            _muteFgItem.CheckedChanged += (s, e) =>
            {
                _config.MuteWhenQQForeground = _muteFgItem.Checked;
                SaveConfig();
            };

            _suppressNativeItem = new ToolStripMenuItem("自动屏蔽 QQ 原生重复系统通知")
            {
                Checked = _config.SuppressNativeQQBanner,
                CheckOnClick = true
            };
            _suppressNativeItem.CheckedChanged += (s, e) =>
            {
                _config.SuppressNativeQQBanner = _suppressNativeItem.Checked;
                SaveConfig();
                ApplyNativeQQNotificationSuppression(_config.Enabled && _config.SuppressNativeQQBanner);
            };

            _stackingMenu = new ToolStripMenuItem("消息堆叠与字数上限");

            var line1Item = new ToolStripMenuItem("最多堆叠 1 条 (仅最新)");
            var line2Item = new ToolStripMenuItem("最多堆叠 2 条");
            var line3Item = new ToolStripMenuItem("最多堆叠 3 条 (默认)");
            var line4Item = new ToolStripMenuItem("最多堆叠 4 条");
            var lineItems = new ToolStripMenuItem[] { line1Item, line2Item, line3Item, line4Item };

            for (int i = 0; i < lineItems.Length; i++)
            {
                int linesVal = i + 1;
                ToolStripMenuItem item = lineItems[i];
                item.Checked = (_config.MaxHistoryLines == linesVal);
                item.Click += (s, e) =>
                {
                    _config.MaxHistoryLines = linesVal;
                    for (int j = 0; j < lineItems.Length; j++)
                    {
                        lineItems[j].Checked = (j + 1 == linesVal);
                    }
                    SaveConfig();
                };
                _stackingMenu.DropDownItems.Add(item);
            }

            _stackingMenu.DropDownItems.Add(new ToolStripSeparator());

            var char30Item = new ToolStripMenuItem("单条上限 30 字 (紧凑)");
            var char50Item = new ToolStripMenuItem("单条上限 50 字 (默认)");
            var char80Item = new ToolStripMenuItem("单条上限 80 字 (宽松)");
            var charItems = new ToolStripMenuItem[] { char30Item, char50Item, char80Item };
            var charLimits = new int[] { 30, 50, 80 };

            for (int i = 0; i < charItems.Length; i++)
            {
                int limitVal = charLimits[i];
                ToolStripMenuItem item = charItems[i];
                item.Checked = (_config.MaxCharsPerLine == limitVal);
                item.Click += (s, e) =>
                {
                    _config.MaxCharsPerLine = limitVal;
                    for (int j = 0; j < charItems.Length; j++)
                    {
                        charItems[j].Checked = (charLimits[j] == limitVal);
                    }
                    SaveConfig();
                };
                _stackingMenu.DropDownItems.Add(item);
            }

            _followQQItem = new ToolStripMenuItem("跟随 QQ 启动自动运行")
            {
                Checked = _config.FollowQQStartup,
                CheckOnClick = true
            };
            _followQQItem.CheckedChanged += (s, e) =>
            {
                _config.FollowQQStartup = _followQQItem.Checked;
                SaveConfig();
            };

            var testItem = new ToolStripMenuItem("发送多条测试通知 (体验打字中实时追加)");
            testItem.Click += (s, e) =>
            {
                ShowQuickReplyToast(new IncomingMessage
                {
                    msgId = "test_1_" + DateTime.Now.Ticks,
                    chatType = 1,
                    peerUid = "test_peer",
                    peerName = "测试好友",
                    senderUid = "test_sender",
                    senderName = "测试好友",
                    content = "第 1 条：你可以现在开始在下方输入框打字（先别按回车）..."
                });

                // Simulate a 2nd message arriving 2.5 seconds later while user is typing!
                var t1 = new System.Windows.Forms.Timer { Interval = 2500 };
                t1.Tick += (s2, e2) =>
                {
                    t1.Stop();
                    t1.Dispose();
                    ShowQuickReplyToast(new IncomingMessage
                    {
                        msgId = "test_2_" + DateTime.Now.Ticks,
                        chatType = 1,
                        peerUid = "test_peer",
                        peerName = "测试好友",
                        senderUid = "test_sender",
                        senderName = "测试好友",
                        content = "第 2 条：看！新消息直接在上面追加了，没有打断你的输入框吧！"
                    });
                };
                t1.Start();
            };

            var injectAndRestartQQItem = new ToolStripMenuItem("修复桥接并重启 QQ");
            injectAndRestartQQItem.Click += (s, e) => SetupBridgeAndRestartQQ(true);

            var removeBridgeItem = new ToolStripMenuItem("卸载 QQ 桥接组件");
            removeBridgeItem.Click += (s, e) => RemoveBridgeEnv();

            _autoStartItem = new ToolStripMenuItem("开机自动启动")
            {
                Checked = IsAutoStartEnabled(),
                CheckOnClick = true
            };
            _autoStartItem.CheckedChanged += (s, e) => SetAutoStart(_autoStartItem.Checked);

            var exitItem = new ToolStripMenuItem("退出程序");
            exitItem.Click += (s, e) => ExitApplication();

            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_enableItem);
            _menu.Items.Add(_groupItem);
            _menu.Items.Add(_muteFgItem);
            _menu.Items.Add(_suppressNativeItem);
            _menu.Items.Add(_stackingMenu);
            _menu.Items.Add(_followQQItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(testItem);
            _menu.Items.Add(injectAndRestartQQItem);
            _menu.Items.Add(removeBridgeItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(exitItem);

            _trayIcon = new NotifyIcon
            {
                Icon = CreateModernTrayIcon(_config.Enabled),
                Text = "QQ Win11 快捷回复助手 v1.1.0",
                ContextMenuStrip = _menu,
                Visible = true
            };

            _trayIcon.DoubleClick += (s, e) =>
            {
                _enableItem.Checked = !_enableItem.Checked;
            };

            ApplyNativeQQNotificationSuppression(_config.Enabled && _config.SuppressNativeQQBanner);
            EnsureBridgeEnvInstalled();
        }

        private void ApplyNativeQQNotificationSuppression(bool suppress)
        {
            string[] qqAppIds = new string[] { "QQ", "Tencent.QQNT" };
            foreach (string appId in qqAppIds)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + appId))
                    {
                        if (key != null)
                        {
                            key.SetValue("Enabled", suppress ? 0 : 1, RegistryValueKind.DWord);
                            key.SetValue("ShowInActionCenter", suppress ? 0 : 1, RegistryValueKind.DWord);
                        }
                    }
                }
                catch { }
            }
        }

        private void UpdateBridgeStatus()
        {
            bool connected = (DateTime.Now - _lastBridgeHeartbeat).TotalSeconds < 10;
            if (connected)
            {
                _statusItem.Text = "🟢 状态: 已连接 NTQQ 内核 (免打扰过滤已生效)";
                _trayIcon.Text = "QQ Win11 快捷回复助手 v1.1.0 (已连接)";
            }
            else
            {
                _statusItem.Text = "🟡 状态: 等待 QQ 桥接连接";
                _trayIcon.Text = "QQ Win11 快捷回复助手 v1.1.0 (等待QQ连接)";
            }
        }

        private string GetQQInstallRoot()
        {
            string qqRoot = @"C:\Program Files\Tencent\QQNT";
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Tencent\QQNT"))
                {
                    if (key != null)
                    {
                        var install = key.GetValue("Install") as string;
                        if (!string.IsNullOrEmpty(install) && Directory.Exists(install))
                        {
                            qqRoot = install;
                        }
                    }
                }
            }
            catch { }
            return qqRoot;
        }

        private void EnsureBridgeEnvInstalled()
        {
            try
            {
                string current = Environment.GetEnvironmentVariable("NODE_OPTIONS", EnvironmentVariableTarget.User) ?? "";
                if (current.Contains("qq-bridge.js"))
                {
                    Environment.SetEnvironmentVariable("NODE_OPTIONS", null, EnvironmentVariableTarget.User);
                }

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string srcDll = Path.Combine(baseDir, "version.dll");
                if (!File.Exists(srcDll) || !File.Exists(_bridgeJsPath)) return;

                string qqRoot = GetQQInstallRoot();
                string versionsDir = Path.Combine(qqRoot, "versions");
                if (!Directory.Exists(versionsDir)) return;

                string normalizedBridge = _bridgeJsPath.Replace("\\", "/");
                string loaderCode = "require(\"" + normalizedBridge + "\");\r\n";

                foreach (var vDir in Directory.GetDirectories(versionsDir))
                {
                    try
                    {
                        string resDir = Path.Combine(vDir, "resources");
                        if (Directory.Exists(resDir))
                        {
                            string targetJs = Path.Combine(resDir, "qqtoast.js");
                            File.WriteAllText(targetJs, loaderCode, Encoding.UTF8);

                            string targetDll = Path.Combine(vDir, "version.dll");
                            if (!File.Exists(targetDll))
                            {
                                File.Copy(srcDll, targetDll, true);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("EnsureBridgeEnvInstalled error: " + ex.Message);
            }
        }

        public void SetupBridgeAndRestartQQ(bool askOrNotify)
        {
            try
            {
                string qqRoot = GetQQInstallRoot();
                string qqExePath = Path.Combine(qqRoot, "QQ.exe");

                foreach (var proc in Process.GetProcessesByName("QQ"))
                {
                    try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                }

                Thread.Sleep(600);
                EnsureBridgeEnvInstalled();

                if (File.Exists(qqExePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = qqExePath,
                        WorkingDirectory = qqRoot,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("重启 QQ 失败: " + ex.Message, "QQ 快捷回复助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RemoveBridgeEnv()
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("QQ"))
                {
                    try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                }
                Thread.Sleep(500);

                string qqRoot = GetQQInstallRoot();
                string versionsDir = Path.Combine(qqRoot, "versions");
                if (Directory.Exists(versionsDir))
                {
                    foreach (var vDir in Directory.GetDirectories(versionsDir))
                    {
                        try
                        {
                            string targetJs = Path.Combine(vDir, "resources", "qqtoast.js");
                            if (File.Exists(targetJs)) File.Delete(targetJs);

                            string targetDll = Path.Combine(vDir, "version.dll");
                            if (File.Exists(targetDll)) File.Delete(targetDll);
                        }
                        catch { }
                    }
                }
                ApplyNativeQQNotificationSuppression(false);
                MessageBox.Show("已成功卸载 QQ 桥接组件，QQ 已恢复纯净原版状态。", "QQ 快捷回复助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("卸载时部分文件正在被占用，请先完全退出 QQ 后再点卸载: " + ex.Message, "QQ 快捷回复助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && key.GetValue("QQToastTray") != null;
                }
            }
            catch { return false; }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enable)
                    {
                        key.SetValue("QQToastTray", "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue("QQToastTray", false);
                    }
                }
            }
            catch { }
        }

        private bool IsQQForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                if (pid == 0) return false;
                using (var proc = Process.GetProcessById((int)pid))
                {
                    return string.Equals(proc.ProcessName, "QQ", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
        }

        private string PrepareAvatarUri(string rawAvatarPath)
        {
            if (string.IsNullOrEmpty(rawAvatarPath) || !File.Exists(rawAvatarPath)) return "";
            try
            {
                string avatarFile = rawAvatarPath;
                if (string.IsNullOrEmpty(Path.GetExtension(avatarFile)))
                {
                    string cacheDir = Path.Combine(Path.GetTempPath(), "QQToastAvatars");
                    Directory.CreateDirectory(cacheDir);
                    string cachedJpg = Path.Combine(cacheDir, Path.GetFileName(avatarFile) + ".jpg");
                    if (!File.Exists(cachedJpg))
                    {
                        File.Copy(avatarFile, cachedJpg, true);
                    }
                    avatarFile = cachedJpg;
                }
                return new Uri(avatarFile).AbsoluteUri;
            }
            catch
            {
                return "";
            }
        }

        private string PrepareToastImageUri(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath) || !File.Exists(rawPath)) return "";
            try
            {
                string cacheDir = Path.Combine(Path.GetTempPath(), "QQToastAvatars");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string ext = Path.GetExtension(rawPath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";

                int hash = rawPath.GetHashCode();
                string cachedImg = Path.Combine(cacheDir, "img_" + Math.Abs(hash) + ext);

                FileInfo fi = new FileInfo(rawPath);
                if (!File.Exists(cachedImg) || new FileInfo(cachedImg).Length != fi.Length)
                {
                    File.Copy(rawPath, cachedImg, true);
                }
                return new Uri(cachedImg).AbsoluteUri;
            }
            catch (Exception ex)
            {
                Log("PrepareToastImageUri error: " + ex.Message);
                return "";
            }
        }

        private string BuildHistoryBodyText(ConversationState conv)
        {
            if (conv == null || conv.Lines == null || conv.Lines.Count == 0) return "";

            int maxLines = _config.MaxHistoryLines;
            if (maxLines < 1) maxLines = 1;
            if (maxLines > 4) maxLines = 4;

            int maxChars = _config.MaxCharsPerLine;
            if (maxChars <= 0) maxChars = 50;

            int olderLimit = Math.Min(26, maxChars);
            int totalCharLimit = 110;

            // When inline image thumbnail is present, available text area is restricted, cap at 2 lines and 65 chars
            if (!string.IsNullOrEmpty(conv.ImagePath))
            {
                maxLines = Math.Min(maxLines, 2);
                totalCharLimit = 65;
            }

            int lastIdx = conv.Lines.Count - 1;
            string latestRaw = conv.Lines[lastIdx] ?? "";
            string latestFormatted = latestRaw;
            if (latestFormatted.Length > maxChars)
            {
                latestFormatted = latestFormatted.Substring(0, Math.Max(0, maxChars - 3)) + "...";
            }

            List<string> selected = new List<string>();
            selected.Add(latestFormatted);
            int totalChars = latestFormatted.Length;

            for (int i = lastIdx - 1; i >= 0 && selected.Count < maxLines; i--)
            {
                string olderRaw = conv.Lines[i] ?? "";
                string olderFormatted = olderRaw;
                if (olderFormatted.Length > olderLimit)
                {
                    olderFormatted = olderFormatted.Substring(0, Math.Max(0, olderLimit - 3)) + "...";
                }

                if (totalChars + olderFormatted.Length > totalCharLimit)
                {
                    break;
                }

                selected.Insert(0, olderFormatted);
                totalChars += olderFormatted.Length;
            }

            return string.Join("\n", selected.ToArray());
        }

        public void ShowQuickReplyToast(IncomingMessage msg)
        {
            if (!_config.Enabled) return;
            if (msg.chatType == 2 && !_config.EnableGroupNotify) return;
            if (_config.MuteWhenQQForeground && msg.peerUid != "test_peer" && IsQQForeground()) return;

            if (_uiContext != null && SynchronizationContext.Current != _uiContext)
            {
                _uiContext.Post(_ => ProcessIncomingToastOnUiThread(msg), null);
            }
            else
            {
                ProcessIncomingToastOnUiThread(msg);
            }
        }

        private void ProcessIncomingToastOnUiThread(IncomingMessage msg)
        {
            try
            {
                string sessionKey = msg.chatType + ":" + (msg.peerUid ?? "");

                if (msg.isImageUpdate)
                {
                    lock (_toastLock)
                    {
                        ConversationState c;
                        if (_conversations.TryGetValue(sessionKey, out c))
                        {
                            if (!string.IsNullOrEmpty(msg.imagePath) && File.Exists(msg.imagePath))
                            {
                                c.ImagePath = msg.imagePath;
                                if (_activeBannerSessionKey == sessionKey)
                                {
                                    ShowBannerForConversation(sessionKey, c);
                                    Log("Refreshed active banner with async image for " + c.PeerName);
                                }
                            }
                        }
                    }
                    return;
                }

                string title = msg.chatType == 2
                    ? (string.IsNullOrEmpty(msg.peerName) ? "群聊消息" : msg.peerName)
                    : (string.IsNullOrEmpty(msg.senderName) ? "QQ 好友" : msg.senderName);

                string cleanContent = (msg.content ?? "").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");

                string lineText = msg.chatType == 2 && !string.IsNullOrEmpty(msg.senderName)
                    ? (msg.senderName + ": " + cleanContent)
                    : ("▸ " + cleanContent);

                lock (_toastLock)
                {
                    // 1. Update conversation history buffer
                    ConversationState conv;
                    if (!_conversations.TryGetValue(sessionKey, out conv))
                    {
                        conv = new ConversationState
                        {
                            ChatType = msg.chatType,
                            PeerUid = msg.peerUid,
                            PeerName = title,
                            AvatarPath = msg.avatarPath,
                            ImagePath = (!string.IsNullOrEmpty(msg.imagePath) && File.Exists(msg.imagePath)) ? msg.imagePath : null
                        };
                        _conversations[sessionKey] = conv;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(title) && title != "QQ好友" && title != "QQ 好友") conv.PeerName = title;
                        if (!string.IsNullOrEmpty(msg.avatarPath)) conv.AvatarPath = msg.avatarPath;

                        if (!string.IsNullOrEmpty(msg.imagePath) && File.Exists(msg.imagePath))
                        {
                            conv.ImagePath = msg.imagePath;
                        }
                        else if (_activeBannerSessionKey != sessionKey)
                        {
                            conv.ImagePath = null;
                        }

                        // Clear old lines if last message was more than 5 minutes ago
                        if ((DateTime.Now - conv.LastUpdated).TotalMinutes > 5)
                        {
                            conv.Lines.Clear();
                            conv.ImagePath = (!string.IsNullOrEmpty(msg.imagePath) && File.Exists(msg.imagePath)) ? msg.imagePath : null;
                        }
                    }

                    conv.LastUpdated = DateTime.Now;
                    conv.Lines.Add(lineText);
                    while (conv.Lines.Count > 10)
                    {
                        conv.Lines.RemoveAt(0);
                    }

                    // 2. Check if there is currently an active banner on screen
                    if (_activeBannerSessionKey != null)
                    {
                        if (_activeBannerSessionKey == sessionKey)
                        {
                            // If image state changed on active banner, ToastNotifier.Update cannot dynamically insert <image> into XML!
                            // So re-show the banner to smoothly render the image thumbnail!
                            if (conv.ImagePath != _activeBannerImagePath)
                            {
                                ShowBannerForConversation(sessionKey, conv);
                                return;
                            }

                            // Same conversation! Update in-place without recreating Toast so user's typing input is NEVER interrupted!
                            if (TryUpdateLiveBannerInPlace(conv))
                            {
                                Log("Updated active Toast banner in-place for " + conv.PeerName + " (lines=" + conv.Lines.Count + ")");
                                return;
                            }
                        }
                        else
                        {
                            // Different conversation arrived while user is viewing/typing on _activeBannerSessionKey!
                            // Show a live ticker line at the top of the current banner AND queue this session to pop right after!
                            ConversationState activeConv;
                            if (_conversations.TryGetValue(_activeBannerSessionKey, out activeConv))
                            {
                                string preview = cleanContent;
                                if (preview.Length > 22) preview = preview.Substring(0, 22) + "...";
                                _latestOtherSessionAlert = "📩 [" + conv.PeerName + "] " + preview;
                                if (!_queuedSessionKeys.Contains(sessionKey))
                                {
                                    _queuedSessionKeys.Add(sessionKey);
                                }
                                if (TryUpdateLiveBannerInPlace(activeConv))
                                {
                                    Log("Displayed live ticker on active banner for new msg from " + conv.PeerName);
                                    return;
                                }
                            }
                        }
                    }

                    // 3. No active banner on screen (or Update returned NotFound) -> Show fresh Toast banner for this conversation
                    ShowBannerForConversation(sessionKey, conv);
                }
            }
            catch (Exception ex)
            {
                Log("ProcessIncomingToastOnUiThread error: " + ex.Message);
            }
        }

        private NotificationData BuildNotificationData(ConversationState conv)
        {
            var data = new NotificationData();
            data.SequenceNumber = ++_sequenceNumber;

            string headerTitle = conv.PeerName;
            if (!string.IsNullOrEmpty(_latestOtherSessionAlert))
            {
                headerTitle = conv.PeerName + "  |  " + _latestOtherSessionAlert;
            }

            string bodyHistory = BuildHistoryBodyText(conv);

            data.Values["title"] = headerTitle;
            data.Values["bodyHistory"] = bodyHistory;
            return data;
        }

        private bool TryUpdateLiveBannerInPlace(ConversationState conv)
        {
            try
            {
                var data = BuildNotificationData(conv);
                var result = ToastNotificationManager.CreateToastNotifier(_activeAumid).Update(data, LIVE_TAG, LIVE_GROUP);
                return result == NotificationUpdateResult.Succeeded;
            }
            catch (Exception ex)
            {
                Log("TryUpdateLiveBannerInPlace notice: " + ex.Message);
                return false;
            }
        }

        private void ShowBannerForConversation(string sessionKey, ConversationState conv)
        {
            try
            {
                _activeBannerSessionKey = sessionKey;
                _activeBannerImagePath = conv.ImagePath;
                _activeBannerShownAt = DateTime.Now;
                _latestOtherSessionAlert = "";
                _queuedSessionKeys.Remove(sessionKey);

                string fileUri = PrepareAvatarUri(conv.AvatarPath);
                string imageTag = "";
                if (!string.IsNullOrEmpty(fileUri))
                {
                    imageTag = "<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"" + EscapeXml(fileUri) + "\"/>";
                }

                string inlineImageTag = "";
                if (!string.IsNullOrEmpty(conv.ImagePath))
                {
                    string imgUri = PrepareToastImageUri(conv.ImagePath);
                    if (!string.IsNullOrEmpty(imgUri))
                    {
                        inlineImageTag = "<image placement=\"inline\" src=\"" + EscapeXml(imgUri) + "\"/>";
                    }
                }

                string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                string launchArgs = "action=toastClick&token=" + token + "&chatType=" + conv.ChatType + "&peerUid=" + Uri.EscapeDataString(conv.PeerUid ?? "");
                string actionArgs = "action=reply&token=" + token + "&chatType=" + conv.ChatType + "&peerUid=" + Uri.EscapeDataString(conv.PeerUid ?? "");

                // Use Windows 10/11 Native Data Binding placeholders {title} and {bodyHistory}
                // so subsequent ToastNotifier.Update() calls dynamically append messages above the input box without resetting focus!
                string xmlString =
                    "<toast launch=\"" + EscapeXml(launchArgs) + "\">" +
                        "<visual>" +
                            "<binding template=\"ToastGeneric\">" +
                                "<text>{title}</text>" +
                                "<text>{bodyHistory}</text>" +
                                imageTag +
                                inlineImageTag +
                            "</binding>" +
                        "</visual>" +
                        "<actions>" +
                            "<input id=\"replyText\" type=\"text\" placeHolderContent=\"直接回复 " + EscapeXml(conv.PeerName) + "...\" />" +
                            "<action content=\"发送\" arguments=\"" + EscapeXml(actionArgs) + "\" hint-inputId=\"replyText\" />" +
                        "</actions>" +
                    "</toast>";

                XmlDocument toastXml = new XmlDocument();
                toastXml.LoadXml(xmlString);

                ToastNotification toast = new ToastNotification(toastXml);
                toast.Tag = LIVE_TAG;
                toast.Group = LIVE_GROUP;
                toast.Data = BuildNotificationData(conv);

                toast.Activated += OnToastActivated;
                toast.Dismissed += (s, e) => OnBannerClosed(s, sessionKey);
                toast.Failed += (s, e) => OnBannerClosed(s, sessionKey);

                lock (_activeToasts)
                {
                    _activeToasts.Clear();
                    _activeToasts.Add(toast);
                }

                ToastNotificationManager.CreateToastNotifier(_activeAumid).Show(toast);
                Log("Toast banner shown for " + conv.PeerName + " (lines=" + conv.Lines.Count + ", img=" + (!string.IsNullOrEmpty(conv.ImagePath) ? "yes" : "no") + ")");
            }
            catch (Exception ex)
            {
                Log("ShowBannerForConversation error: " + ex.Message);
            }
        }

        private void OnBannerClosed(ToastNotification sender, string closedSessionKey)
        {
            if (_uiContext != null && SynchronizationContext.Current != _uiContext)
            {
                _uiContext.Post(_ => OnBannerClosedOnUiThread(sender, closedSessionKey), null);
            }
            else
            {
                OnBannerClosedOnUiThread(sender, closedSessionKey);
            }
        }

        private void OnBannerClosedOnUiThread(ToastNotification sender, string closedSessionKey)
        {
            lock (_toastLock)
            {
                if (_activeBannerSessionKey == closedSessionKey)
                {
                    _activeBannerSessionKey = null;
                    _activeBannerImagePath = null;
                    _latestOtherSessionAlert = "";
                }
                ConversationState conv;
                if (_conversations.TryGetValue(closedSessionKey, out conv))
                {
                    conv.ImagePath = null;
                }
                PopNextQueuedSessionIfAny();
            }
        }

        private void PopNextQueuedSessionIfAny()
        {
            if (_queuedSessionKeys.Count > 0)
            {
                string nextKey = _queuedSessionKeys[0];
                _queuedSessionKeys.RemoveAt(0);
                ConversationState nextConv;
                if (_conversations.TryGetValue(nextKey, out nextConv) && nextConv.Lines.Count > 0)
                {
                    var delayTimer = new System.Windows.Forms.Timer { Interval = 350 };
                    delayTimer.Tick += (s, e) =>
                    {
                        delayTimer.Stop();
                        delayTimer.Dispose();
                        lock (_toastLock)
                        {
                            if (_activeBannerSessionKey == null)
                            {
                                ShowBannerForConversation(nextKey, nextConv);
                            }
                        }
                    };
                    delayTimer.Start();
                }
            }
        }

        private void OnToastActivated(ToastNotification sender, object args)
        {
            try
            {
                string argument = "";
                string replyText = "";

                var argProp = args.GetType().GetProperty("Arguments");
                if (argProp != null)
                {
                    argument = argProp.GetValue(args, null) as string ?? "";
                }

                var userInputProp = args.GetType().GetProperty("UserInput");
                if (userInputProp != null)
                {
                    var userInputObj = userInputProp.GetValue(args, null);
                    if (userInputObj != null)
                    {
                        var dict = userInputObj as IDictionary<string, object>;
                        if (dict != null && dict.ContainsKey("replyText") && dict["replyText"] != null)
                        {
                            replyText = dict["replyText"].ToString();
                        }
                        else
                        {
                            var lookupMethod = userInputObj.GetType().GetMethod("Lookup");
                            if (lookupMethod != null)
                            {
                                var val = lookupMethod.Invoke(userInputObj, new object[] { "replyText" });
                                if (val != null) replyText = val.ToString();
                            }
                        }
                    }
                }

                HandleActivatedReply(argument, replyText, "WinRT_Event");
            }
            catch (Exception ex)
            {
                Log("OnToastActivated error: " + ex.Message);
            }
        }

        public void HandleActivatedReply(string argument, string replyText, string source)
        {
            try
            {
                string action = "";
                int chatType = 1;
                string peerUid = "";
                string token = "";
                foreach (var part in (argument ?? "").Split('&'))
                {
                    var kv = part.Split('=');
                    if (kv.Length == 2)
                    {
                        if (kv[0] == "action") action = kv[1];
                        if (kv[0] == "chatType") int.TryParse(kv[1], out chatType);
                        if (kv[0] == "peerUid") peerUid = Uri.UnescapeDataString(kv[1]);
                        if (kv[0] == "token") token = kv[1];
                    }
                }

                string sessionKey = chatType + ":" + peerUid;

                // Move token deduplication BEFORE isToastClick check so toastClick is also deduped!
                lock (_queueLock)
                {
                    if (!string.IsNullOrEmpty(token))
                    {
                        if (_processedReplyTokens.Contains(token))
                        {
                            return;
                        }
                        _processedReplyTokens.Add(token);
                    }
                }

                bool isToastClick = (action == "toastClick") || (string.IsNullOrWhiteSpace(replyText) && (action == "reply" || string.IsNullOrEmpty(action)));

                if (isToastClick)
                {
                    Log("Toast clicked without replyText / toastClick (" + source + ") for peerUid=" + peerUid + ", chatType=" + chatType + ". Activating QQ main window.");

                    // 1. Win32: Find QQ main window handle, restore and bring to foreground
                    try
                    {
                        foreach (var proc in Process.GetProcessesByName("QQ"))
                        {
                            IntPtr hWnd = proc.MainWindowHandle;
                            if (hWnd != IntPtr.Zero)
                            {
                                ShowWindow(hWnd, 9 /* SW_RESTORE */);
                                SetForegroundWindow(hWnd);
                                break;
                            }
                        }
                    }
                    catch (Exception exHwnd)
                    {
                        Log("SetForegroundWindow notice: " + exHwnd.Message);
                    }

                    // 2. Dispatch openChat command to bridge for NTQQ in-app chat jump
                    if (!string.IsNullOrEmpty(peerUid))
                    {
                        lock (_queueLock)
                        {
                            _pendingReplies.Add(new ReplyCommand
                            {
                                id = "open_" + DateTime.Now.Ticks,
                                action = "openChat",
                                chatType = chatType,
                                peerUid = peerUid,
                                content = ""
                            });
                        }
                    }

                    // 3. Clear active banner
                    if (_uiContext != null)
                    {
                        _uiContext.Post(_ =>
                        {
                            lock (_toastLock)
                            {
                                _activeBannerSessionKey = null;
                                _activeBannerImagePath = null;
                                _latestOtherSessionAlert = "";
                                PopNextQueuedSessionIfAny();
                            }
                        }, null);
                    }
                    return;
                }

                Log("Captured quick reply via " + source + "! peerUid=" + peerUid + ", chatType=" + chatType + ", text=" + replyText);

                // Clear history lines of the replied conversation and pop next queued session if any!
                if (_uiContext != null)
                {
                    _uiContext.Post(_ =>
                    {
                        lock (_toastLock)
                        {
                            ConversationState conv;
                            if (_conversations.TryGetValue(sessionKey, out conv))
                            {
                                conv.Lines.Clear();
                                conv.ImagePath = null;
                            }
                            _activeBannerSessionKey = null;
                            _activeBannerImagePath = null;
                            _latestOtherSessionAlert = "";
                            PopNextQueuedSessionIfAny();
                        }
                    }, null);
                }

                if (peerUid == "test_peer")
                {
                    ShowSimpleConfirmToast("测试回复已捕获！", "你刚才输入的回复内容是：「" + replyText + "」（全程未弹出主窗口）");
                    return;
                }

                if (!string.IsNullOrEmpty(peerUid))
                {
                    lock (_queueLock)
                    {
                        _pendingReplies.Add(new ReplyCommand
                        {
                            id = Guid.NewGuid().ToString("N"),
                            action = "reply",
                            chatType = chatType,
                            peerUid = peerUid,
                            content = replyText
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log("HandleActivatedReply error: " + ex.Message);
            }
        }

        private void ShowSimpleConfirmToast(string title, string content)
        {
            try
            {
                string xmlString =
                    "<toast>" +
                        "<visual>" +
                            "<binding template=\"ToastGeneric\">" +
                                "<text>" + EscapeXml(title) + "</text>" +
                                "<text>" + EscapeXml(content) + "</text>" +
                            "</binding>" +
                        "</visual>" +
                    "</toast>";
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlString);
                ToastNotification toast = new ToastNotification(doc);
                toast.ExpirationTime = DateTimeOffset.Now.AddSeconds(5);
                ToastNotificationManager.CreateToastNotifier(_activeAumid).Show(toast);
            }
            catch { }
        }

        private void StartLocalServer()
        {
            _serverThread = new Thread(() =>
            {
                try
                {
                    _tcpListener = new TcpListener(IPAddress.Loopback, _config.LocalPort);
                    _tcpListener.Start();
                    Log("TcpListener started on 127.0.0.1:" + _config.LocalPort);

                    var serializer = new JavaScriptSerializer();

                    while (_running)
                    {
                        TcpClient client = null;
                        try
                        {
                            client = _tcpListener.AcceptTcpClient();
                        }
                        catch
                        {
                            if (!_running) break;
                            continue;
                        }

                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                using (client)
                                using (var stream = client.GetStream())
                                {
                                    stream.ReadTimeout = 3000;
                                    stream.WriteTimeout = 3000;

                                    byte[] buffer = new byte[65536];
                                    int totalRead = 0;
                                    int headerEnd = -1;
                                    int contentLength = 0;

                                    while (totalRead < buffer.Length)
                                    {
                                        int n = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                                        if (n <= 0) break;
                                        totalRead += n;

                                        if (headerEnd < 0)
                                        {
                                            string currentStr = Encoding.ASCII.GetString(buffer, 0, totalRead);
                                            int idx = currentStr.IndexOf("\r\n\r\n");
                                            if (idx >= 0)
                                            {
                                                headerEnd = idx + 4;
                                                string headers = currentStr.Substring(0, idx);
                                                foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                                                {
                                                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        int.TryParse(line.Substring(15).Trim(), out contentLength);
                                                    }
                                                }
                                            }
                                        }

                                        if (headerEnd >= 0 && totalRead >= headerEnd + contentLength)
                                        {
                                            break;
                                        }
                                    }

                                    if (headerEnd < 0) return;

                                    string reqHeader = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                                    string firstLine = reqHeader.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                                    string[] parts = firstLine.Split(' ');
                                    string method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "GET";
                                    string rawPath = parts.Length > 1 ? parts[1].ToLowerInvariant() : "/";

                                    _lastBridgeHeartbeat = DateTime.Now;
                                    string responseJson = "{\"ok\":true}";

                                    if (rawPath.StartsWith("/notify") && method == "POST")
                                    {
                                        string body = Encoding.UTF8.GetString(buffer, headerEnd, Math.Min(contentLength, totalRead - headerEnd));
                                        Log("Received /notify: " + body);
                                        var msg = serializer.Deserialize<IncomingMessage>(body);
                                        if (msg != null && !string.IsNullOrEmpty(msg.content))
                                        {
                                            ShowQuickReplyToast(msg);
                                        }
                                    }
                                    else if (rawPath.StartsWith("/poll"))
                                    {
                                        List<ReplyCommand> cmds = null;
                                        lock (_queueLock)
                                        {
                                            if (_pendingReplies.Count > 0)
                                            {
                                                cmds = new List<ReplyCommand>(_pendingReplies);
                                                _pendingReplies.Clear();
                                            }
                                        }
                                        if (cmds != null && cmds.Count > 0)
                                        {
                                            responseJson = serializer.Serialize(new { ok = true, replies = cmds });
                                            Log("Dispatched replies to bridge: " + responseJson);
                                        }
                                        else
                                        {
                                            responseJson = "{\"ok\":true,\"replies\":[]}";
                                        }
                                    }

                                    byte[] bodyBytes = Encoding.UTF8.GetBytes(responseJson);
                                    string respHeader =
                                        "HTTP/1.1 200 OK\r\n" +
                                        "Content-Type: application/json; charset=utf-8\r\n" +
                                        "Content-Length: " + bodyBytes.Length + "\r\n" +
                                        "Connection: close\r\n\r\n";
                                    byte[] headerBytes = Encoding.ASCII.GetBytes(respHeader);
                                    stream.Write(headerBytes, 0, headerBytes.Length);
                                    stream.Write(bodyBytes, 0, bodyBytes.Length);
                                    stream.Flush();
                                }
                            }
                            catch (Exception ex)
                            {
                                Log("Request handler error: " + ex.Message);
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log("TCP server error: " + ex.Message);
                }
            });
            _serverThread.IsBackground = true;
            _serverThread.Start();
        }

        private void ExitApplication()
        {
            _running = false;
            try { if (_comCookie != 0) CoRevokeClassObject(_comCookie); } catch { }
            try { if (_tcpListener != null) _tcpListener.Stop(); } catch { }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            Application.Exit();
        }
    }

    static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            _mutex = new Mutex(true, "Local\\QQToastTray_Singleton_Mutex", out createdNew);
            if (!createdNew)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray-debug.log");
                    File.AppendAllText(p, "[FATAL UnhandledException] " + e.ExceptionObject + Environment.NewLine);
                }
                catch { }
            };
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray-debug.log");
                    File.AppendAllText(p, "[FATAL ThreadException] " + e.Exception + Environment.NewLine);
                }
                catch { }
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }
    }
}
