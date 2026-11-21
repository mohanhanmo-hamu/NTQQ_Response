using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace QQToastTray
{
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
                if (ToastManager.ActiveInstance != null)
                {
                    ToastManager.ActiveInstance.HandleActivatedReply(invokedArgs ?? "", replyText, "COM_Activator");
                }
            }
            catch { }
        }
    }

    public class ToastManager
    {
        public static ToastManager ActiveInstance { get; private set; }

        private const string LIVE_TAG = "QQLiveBanner";
        private const string LIVE_GROUP = "QQQuickReply";

        private readonly AppConfig _config;
        private readonly BridgeTelemetry _telemetry;
        private readonly RotatingLogger _logger;
        private readonly Action<ReplyCommand> _enqueueReply;

        private SynchronizationContext _uiContext;
        private string _activeAumid = AppMeta.APP_AUMID;
        private uint _comCookie = 0;

        private readonly object _toastLock = new object();
        private readonly Dictionary<string, ConversationState> _conversations = new Dictionary<string, ConversationState>();
        private readonly List<string> _queuedSessionKeys = new List<string>();
        private readonly List<ToastNotification> _activeToasts = new List<ToastNotification>();
        private readonly object _tokenLock = new object();
        private readonly HashSet<string> _processedTokenSet = new HashSet<string>();
        private readonly Queue<string> _processedTokenOrder = new Queue<string>();

        private string _activeBannerSessionKey = null;
        private string _activeBannerImagePath = null;
        private string _latestOtherSessionAlert = "";
        private uint _sequenceNumber = 1;
        private int _cacheCleanupCounter = 0;

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

            public int LockServer(bool fLock) { return 0; }
        }

        public ToastManager(
            AppConfig config,
            BridgeTelemetry telemetry,
            RotatingLogger logger,
            Action<ReplyCommand> enqueueReply)
        {
            _config = config;
            _telemetry = telemetry;
            _logger = logger;
            _enqueueReply = enqueueReply;
            ActiveInstance = this;
        }

        public void SetUiContext(SynchronizationContext ctx)
        {
            _uiContext = ctx;
        }

        // Each registration step is isolated and checks existing values read-only first,
        // so an AV/permission block on Start Menu or LocalServer32 never prevents CoRegisterClassObject.
        public void RegisterComActivatorAndAumid()
        {
            string exePath = Application.ExecutablePath;
            _activeAumid = AppMeta.APP_AUMID;

            // 1. Start Menu Shortcut with AUMID + ToastActivatorCLSID
            try
            {
                string startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string shortcutPath = Path.Combine(startMenuDir, AppMeta.APP_DISPLAY_NAME + ".lnk");
                CreateOrUpdateShortcut(shortcutPath, exePath);
            }
            catch (Exception exLnk)
            {
                _logger.Write("Shortcut registration notice: " + exLnk.Message);
            }

            // 2. HKCU CLSID LocalServer32 (check read-only first to avoid unnecessary writes)
            try
            {
                string expectedCmd = "\"" + exePath + "\" -ToastActivated";
                string clsidSubKey = @"Software\Classes\CLSID\" + AppMeta.ACTIVATOR_CLSID + @"\LocalServer32";
                string existingCmd = null;
                using (var roKey = Registry.CurrentUser.OpenSubKey(clsidSubKey, false))
                {
                    if (roKey != null) existingCmd = roKey.GetValue(null) as string;
                }
                if (!string.Equals(existingCmd, expectedCmd, StringComparison.OrdinalIgnoreCase))
                {
                    using (var rwKey = Registry.CurrentUser.CreateSubKey(clsidSubKey))
                    {
                        if (rwKey != null) rwKey.SetValue(null, expectedCmd, RegistryValueKind.String);
                    }
                }
            }
            catch (Exception exClsid)
            {
                _logger.Write("CLSID registration notice: " + exClsid.Message);
            }

            // 3. HKCU AppUserModelId
            try
            {
                string aumidSubKey = @"Software\Classes\AppUserModelId\" + AppMeta.APP_AUMID;
                string existingAct = null;
                using (var roKey = Registry.CurrentUser.OpenSubKey(aumidSubKey, false))
                {
                    if (roKey != null) existingAct = roKey.GetValue("CustomActivator") as string;
                }
                if (!string.Equals(existingAct, AppMeta.ACTIVATOR_CLSID, StringComparison.OrdinalIgnoreCase))
                {
                    using (var rwKey = Registry.CurrentUser.CreateSubKey(aumidSubKey))
                    {
                        if (rwKey != null)
                        {
                            rwKey.SetValue("DisplayName", "QQ (快捷回复)", RegistryValueKind.String);
                            rwKey.SetValue("CustomActivator", AppMeta.ACTIVATOR_CLSID, RegistryValueKind.String);
                        }
                    }
                }
            }
            catch (Exception exAumid)
            {
                _logger.Write("AUMID registration notice: " + exAumid.Message);
            }

            // 4. Register in-process COM class factory
            try
            {
                Guid clsid = new Guid(AppMeta.ACTIVATOR_CLSID);
                int hr = NativeMethods.CoRegisterClassObject(clsid, new ActivatorClassFactory(), 4, 1, out _comCookie);
                _logger.Write("Registered COM ToastActivator: hr=" + hr + ", cookie=" + _comCookie);
            }
            catch (Exception exCom)
            {
                _logger.Write("CoRegisterClassObject error: " + exCom.Message);
            }
        }

        private static void CreateOrUpdateShortcut(string shortcutPath, string exePath)
        {
            Type shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            IShellLinkW shellLink = (IShellLinkW)Activator.CreateInstance(shellLinkType);
            shellLink.SetPath(exePath);
            shellLink.SetArguments("");
            shellLink.SetWorkingDirectory(AppDomain.CurrentDomain.BaseDirectory);
            shellLink.SetDescription("QQ Windows 11 系统级快捷回复助手");

            string qqExe = Path.Combine(BridgeDeployer.DetectQQInstallRoot(), "QQ.exe");
            if (File.Exists(qqExe))
            {
                shellLink.SetIconLocation(qqExe, 0);
            }

            IPropertyStore propStore = (IPropertyStore)shellLink;

            PROPERTYKEY pkeyAumid = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            PROPVARIANT pvAumid = new PROPVARIANT { vt = 31, ptrVal = Marshal.StringToCoTaskMemUni(AppMeta.APP_AUMID) };
            propStore.SetValue(ref pkeyAumid, ref pvAumid);
            Marshal.FreeCoTaskMem(pvAumid.ptrVal);

            PROPERTYKEY pkeyClsid = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 26 };
            Guid clsidGuid = new Guid(AppMeta.ACTIVATOR_CLSID);
            IntPtr pGuid = Marshal.AllocCoTaskMem(16);
            Marshal.Copy(clsidGuid.ToByteArray(), 0, pGuid, 16);
            PROPVARIANT pvClsid = new PROPVARIANT { vt = 72, ptrVal = pGuid };
            propStore.SetValue(ref pkeyClsid, ref pvClsid);
            Marshal.FreeCoTaskMem(pGuid);

            propStore.Commit();
            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }

        public void Shutdown()
        {
            try { ToastNotificationManager.History.Remove(LIVE_TAG, LIVE_GROUP, _activeAumid); } catch { }
            try { ToastNotificationManager.History.Clear(_activeAumid); } catch { }
            try { if (_comCookie != 0) NativeMethods.CoRevokeClassObject(_comCookie); } catch { }
            ActiveInstance = null;
        }

        public void ShowQuickReplyToast(IncomingMessage msg)
        {
            if (!_config.Enabled) return;
            if (msg.chatType == 2 && !_config.EnableGroupNotify) return;
            if (_config.MuteWhenQQForeground && IsQQForeground()) return;

            PostToUi(() => ProcessIncomingToastOnUiThread(msg));
        }

        // Sends a harmless local preview notification (and appends a 2nd line after 1.5s)
        // so users can verify Win11 Toast behavior & stacking settings right from the control panel.
        public void SendPreviewToast()
        {
            var first = new IncomingMessage
            {
                msgId = "preview_1_" + DateTime.Now.Ticks,
                chatType = 1,
                peerUid = "__preview__",
                peerName = "预览通知 · 快捷回复助手",
                senderName = "预览通知 · 快捷回复助手",
                content = "这是原生 Win11 通知预览（当前堆叠 " + _config.MaxHistoryLines + " 条 / 单条 " + _config.MaxCharsPerLine + " 字）"
            };
            PostToUi(() => ProcessIncomingToastOnUiThread(first));

            var t = new System.Windows.Forms.Timer { Interval = 1600 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                var second = new IncomingMessage
                {
                    msgId = "preview_2_" + DateTime.Now.Ticks,
                    chatType = 1,
                    peerUid = "__preview__",
                    peerName = "预览通知 · 快捷回复助手",
                    senderName = "预览通知 · 快捷回复助手",
                    content = "第二条消息已原位追加，不会清空输入框中的文字！"
                };
                ProcessIncomingToastOnUiThread(second);
            };
            t.Start();
        }

        private void PostToUi(Action action)
        {
            if (_uiContext != null && SynchronizationContext.Current != _uiContext)
            {
                _uiContext.Post(_ => action(), null);
            }
            else
            {
                action();
            }
        }

        private static bool IsQQForeground()
        {
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                uint pid;
                NativeMethods.GetWindowThreadProcessId(fg, out pid);
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

        private string GetCacheDir()
        {
            string cacheDir = Path.Combine(Path.GetTempPath(), "QQToastAvatars");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
            return cacheDir;
        }

        private void CleanupImageCachePeriodically()
        {
            _cacheCleanupCounter++;
            if ((_cacheCleanupCounter % 30) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string cacheDir = GetCacheDir();
                    var files = new DirectoryInfo(cacheDir).GetFiles();
                    if (files.Length <= 80) return;
                    Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    for (int i = 60; i < files.Length; i++)
                    {
                        try { files[i].Delete(); } catch { }
                    }
                }
                catch { }
            });
        }

        private string PrepareAvatarUri(string rawAvatarPath)
        {
            if (string.IsNullOrEmpty(rawAvatarPath) || !File.Exists(rawAvatarPath)) return "";
            try
            {
                string avatarFile = rawAvatarPath;
                if (string.IsNullOrEmpty(Path.GetExtension(avatarFile)))
                {
                    string cacheDir = GetCacheDir();
                    string cachedJpg = Path.Combine(cacheDir, Path.GetFileName(avatarFile) + ".jpg");
                    if (!File.Exists(cachedJpg))
                    {
                        try { File.Copy(avatarFile, cachedJpg, false); } catch { }
                    }
                    if (File.Exists(cachedJpg)) avatarFile = cachedJpg;
                }
                return new Uri(avatarFile).AbsoluteUri;
            }
            catch
            {
                return "";
            }
        }

        // Fixed v1.1.1 bug: never overwrite a locked file currently read by Windows Toast host;
        // include file length + unsigned hash in filename so updates write to a clean target.
        private string PrepareToastImageUri(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath) || !File.Exists(rawPath)) return "";
            try
            {
                CleanupImageCachePeriodically();
                string cacheDir = GetCacheDir();
                string ext = Path.GetExtension(rawPath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";

                FileInfo fi = new FileInfo(rawPath);
                uint uhash = unchecked((uint)rawPath.GetHashCode());
                string cachedImg = Path.Combine(cacheDir, "img_" + uhash.ToString("x8") + "_" + fi.Length + ext);

                if (!File.Exists(cachedImg))
                {
                    try
                    {
                        File.Copy(rawPath, cachedImg, false);
                    }
                    catch
                    {
                        // If another thread raced to create it, reuse existing file if present
                        if (!File.Exists(cachedImg))
                        {
                            string altImg = Path.Combine(cacheDir, "img_" + uhash.ToString("x8") + "_" + DateTime.UtcNow.Ticks + ext);
                            File.Copy(rawPath, altImg, false);
                            cachedImg = altImg;
                        }
                    }
                }
                return new Uri(cachedImg).AbsoluteUri;
            }
            catch (Exception ex)
            {
                _logger.Write("PrepareToastImageUri error: " + ex.Message);
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

            if (!string.IsNullOrEmpty(conv.ImagePath))
            {
                maxLines = Math.Min(maxLines, 2);
                totalCharLimit = 65;
            }

            int lastIdx = conv.Lines.Count - 1;
            string latestFormatted = conv.Lines[lastIdx] ?? "";
            if (latestFormatted.Length > maxChars)
            {
                latestFormatted = latestFormatted.Substring(0, Math.Max(0, maxChars - 3)) + "...";
            }

            var selected = new List<string>();
            selected.Add(latestFormatted);
            int totalChars = latestFormatted.Length;

            for (int i = lastIdx - 1; i >= 0 && selected.Count < maxLines; i--)
            {
                string olderFormatted = conv.Lines[i] ?? "";
                if (olderFormatted.Length > olderLimit)
                {
                    olderFormatted = olderFormatted.Substring(0, Math.Max(0, olderLimit - 3)) + "...";
                }
                if (totalChars + olderFormatted.Length > totalCharLimit) break;

                selected.Insert(0, olderFormatted);
                totalChars += olderFormatted.Length;
            }

            return string.Join("\n", selected.ToArray());
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
                                    _logger.Write("Refreshed active banner with async image.");
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

                        if ((DateTime.Now - conv.LastUpdated).TotalMinutes > 5)
                        {
                            conv.Lines.Clear();
                            conv.ImagePath = (!string.IsNullOrEmpty(msg.imagePath) && File.Exists(msg.imagePath)) ? msg.imagePath : null;
                        }
                    }

                    conv.LastUpdated = DateTime.Now;
                    conv.Lines.Add(lineText);
                    while (conv.Lines.Count > 10) conv.Lines.RemoveAt(0);

                    if (_activeBannerSessionKey != null)
                    {
                        if (_activeBannerSessionKey == sessionKey)
                        {
                            if (conv.ImagePath != _activeBannerImagePath)
                            {
                                ShowBannerForConversation(sessionKey, conv);
                                return;
                            }
                            if (TryUpdateLiveBannerInPlace(conv))
                            {
                                return;
                            }
                        }
                        else
                        {
                            ConversationState activeConv;
                            if (_conversations.TryGetValue(_activeBannerSessionKey, out activeConv))
                            {
                                string preview = cleanContent.Length > 22 ? cleanContent.Substring(0, 22) + "..." : cleanContent;
                                _latestOtherSessionAlert = "📩 [" + conv.PeerName + "] " + preview;
                                if (!_queuedSessionKeys.Contains(sessionKey)) _queuedSessionKeys.Add(sessionKey);
                                if (TryUpdateLiveBannerInPlace(activeConv)) return;
                            }
                        }
                    }

                    ShowBannerForConversation(sessionKey, conv);
                }
            }
            catch (Exception ex)
            {
                _logger.Write("ProcessIncomingToastOnUiThread error: " + ex.Message);
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

            data.Values["title"] = headerTitle;
            data.Values["bodyHistory"] = BuildHistoryBodyText(conv);
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
                _logger.Write("TryUpdateLiveBannerInPlace notice: " + ex.Message);
                return false;
            }
        }

        private void ShowBannerForConversation(string sessionKey, ConversationState conv)
        {
            try
            {
                _activeBannerSessionKey = sessionKey;
                _activeBannerImagePath = conv.ImagePath;
                _latestOtherSessionAlert = "";
                _queuedSessionKeys.Remove(sessionKey);

                string fileUri = PrepareAvatarUri(conv.AvatarPath);
                string imageTag = !string.IsNullOrEmpty(fileUri)
                    ? "<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"" + EscapeXml(fileUri) + "\"/>"
                    : "";

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
                toast.Dismissed += (s, e) => OnBannerClosed(sessionKey);
                toast.Failed += (s, e) => OnBannerClosed(sessionKey);

                lock (_activeToasts)
                {
                    _activeToasts.Clear();
                    _activeToasts.Add(toast);
                }

                ToastNotificationManager.CreateToastNotifier(_activeAumid).Show(toast);
            }
            catch (Exception ex)
            {
                _logger.Write("ShowBannerForConversation error: " + ex.Message);
            }
        }

        private void OnBannerClosed(string closedSessionKey)
        {
            PostToUi(() =>
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
            });
        }

        private void PopNextQueuedSessionIfAny()
        {
            if (_queuedSessionKeys.Count == 0) return;
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

        private void OnToastActivated(ToastNotification sender, object args)
        {
            try
            {
                string argument = "";
                string replyText = "";

                var argProp = args.GetType().GetProperty("Arguments");
                if (argProp != null) argument = argProp.GetValue(args, null) as string ?? "";

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
                _logger.Write("OnToastActivated error: " + ex.Message);
            }
        }

        private bool TryRecordToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return true;
            lock (_tokenLock)
            {
                if (_processedTokenSet.Contains(token)) return false;
                _processedTokenSet.Add(token);
                _processedTokenOrder.Enqueue(token);
                while (_processedTokenOrder.Count > 500)
                {
                    string oldest = _processedTokenOrder.Dequeue();
                    _processedTokenSet.Remove(oldest);
                }
                return true;
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

                foreach (string part in (argument ?? "").Split('&'))
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

                if (!TryRecordToken(token)) return;

                string sessionKey = chatType + ":" + peerUid;

                // Preview toast interaction: do not forward to NTQQ
                if (peerUid == "__preview__")
                {
                    PostToUi(() =>
                    {
                        lock (_toastLock)
                        {
                            ConversationState conv;
                            if (_conversations.TryGetValue(sessionKey, out conv)) conv.Lines.Clear();
                            _activeBannerSessionKey = null;
                            _activeBannerImagePath = null;
                            _latestOtherSessionAlert = "";
                            PopNextQueuedSessionIfAny();
                        }
                    });
                    if (!string.IsNullOrWhiteSpace(replyText))
                    {
                        _logger.Write("Preview toast reply tested successfully (len=" + replyText.Length + ").");
                    }
                    return;
                }

                bool isToastClick = (action == "toastClick") || (string.IsNullOrWhiteSpace(replyText) && (action == "reply" || string.IsNullOrEmpty(action)));

                if (isToastClick)
                {
                    if (_telemetry.IsConnected)
                    {
                        try { NativeMethods.AllowSetForegroundWindow(-1); } catch { }
                    }
                    else
                    {
                        TryActivateQQWithoutBridge();
                    }

                    if (!string.IsNullOrEmpty(peerUid) && _enqueueReply != null)
                    {
                        string clickPeerName = "";
                        lock (_toastLock)
                        {
                            ConversationState clickConv;
                            if (_conversations.TryGetValue(sessionKey, out clickConv))
                            {
                                clickPeerName = clickConv.PeerName ?? "";
                            }
                        }
                        _enqueueReply(new ReplyCommand
                        {
                            id = "open_" + DateTime.Now.Ticks,
                            action = "openChat",
                            chatType = chatType,
                            peerUid = peerUid,
                            peerName = clickPeerName,
                            content = ""
                        });
                    }

                    PostToUi(() =>
                    {
                        lock (_toastLock)
                        {
                            _activeBannerSessionKey = null;
                            _activeBannerImagePath = null;
                            _latestOtherSessionAlert = "";
                            PopNextQueuedSessionIfAny();
                        }
                    });
                    return;
                }

                _logger.Write("Captured quick reply via " + source + " (chatType=" + chatType + ", len=" + replyText.Length + ")");

                PostToUi(() =>
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
                });

                if (!string.IsNullOrEmpty(peerUid) && _enqueueReply != null)
                {
                    _enqueueReply(new ReplyCommand
                    {
                        id = Guid.NewGuid().ToString("N"),
                        action = "reply",
                        chatType = chatType,
                        peerUid = peerUid,
                        content = replyText
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Write("HandleActivatedReply error: " + ex.Message);
            }
        }

        private void TryActivateQQWithoutBridge()
        {
            try
            {
                if (Process.GetProcessesByName("QQ").Length > 0) return;
                string qqExe = Path.Combine(BridgeDeployer.DetectQQInstallRoot(), "QQ.exe");
                if (File.Exists(qqExe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = qqExe,
                        WorkingDirectory = Path.GetDirectoryName(qqExe),
                        UseShellExecute = true
                    });
                }
            }
            catch { }
        }
    }
}
