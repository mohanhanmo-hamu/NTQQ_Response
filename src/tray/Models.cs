using System;
using System.Collections.Generic;
using System.Reflection;

namespace QQToastTray
{
    public static class AppMeta
    {
        public const string APP_NAME = "NTQQ_Response";
        public const string APP_DISPLAY_NAME = "QQ 快捷回复助手";
        public const string APP_AUMID = "Tencent.QQNT.QuickReply";
        public const string ACTIVATOR_CLSID = "{E8B84582-4D5A-49A6-9375-52F570296A42}";
        public const string EXIT_EVENT_NAME = @"Local\QQToastTray_ExitEvent";
        public const string SINGLETON_MUTEX_NAME = @"Local\QQToastTray_Singleton_Mutex";

        public static string Version
        {
            get
            {
                try
                {
                    object[] attrs = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                    {
                        var info = (AssemblyInformationalVersionAttribute)attrs[0];
                        if (!string.IsNullOrEmpty(info.InformationalVersion))
                            return info.InformationalVersion;
                    }
                    Version v = Assembly.GetExecutingAssembly().GetName().Version;
                    if (v != null)
                        return v.Major + "." + v.Minor + "." + v.Build;
                }
                catch { }
                return "1.2.0";
            }
        }
    }

    public class AppConfig
    {
        public bool Enabled { get; set; }
        public bool EnableGroupNotify { get; set; }
        public bool MuteWhenQQForeground { get; set; }
        public bool SuppressNativeQQBanner { get; set; }
        public bool FollowQQStartup { get; set; }
        public bool AutoSyncVersions { get; set; }
        public bool VerboseLog { get; set; }
        public int MaxHistoryLines { get; set; }
        public int MaxCharsPerLine { get; set; }
        public int LocalPort { get; set; }
        public string AuthToken { get; set; }

        public AppConfig()
        {
            Enabled = true;
            EnableGroupNotify = true;
            MuteWhenQQForeground = true;
            SuppressNativeQQBanner = true;
            FollowQQStartup = true;
            AutoSyncVersions = true;
            VerboseLog = false;
            MaxHistoryLines = 3;
            MaxCharsPerLine = 50;
            LocalPort = 39876;
            AuthToken = "";
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
        public string peerName { get; set; }
        public string content { get; set; }

        public ReplyCommand()
        {
            action = "reply";
            peerName = "";
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

    public class BridgeTelemetry
    {
        public DateTime LastHeartbeat = DateTime.MinValue;
        public string RunningQQVersion = "";
        public string LoaderStatus = "";
        public string BridgeVersion = "";
        public int DndRuleCount = 0;

        public bool IsConnected
        {
            get { return (DateTime.Now - LastHeartbeat).TotalSeconds < 10; }
        }
    }

    public class QQVersionInfo
    {
        public string Name;
        public string DirPath;
        public bool IsCurrent;
        public bool IsReadyUpdate;
        public bool HasLoaderDll;
        public bool LoaderDllUpToDate;
        public bool HasLoaderScript;
        public bool LoaderScriptUpToDate;

        public bool IsFullyDeployed
        {
            get { return HasLoaderDll && LoaderDllUpToDate && HasLoaderScript && LoaderScriptUpToDate; }
        }
    }

    public class QQInstallationReport
    {
        public string QQRoot = "";
        public bool Found = false;
        public string CurVersion = "";
        public string ReadyVersion = "";
        public List<QQVersionInfo> Versions = new List<QQVersionInfo>();

        public int PendingSyncCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Versions.Count; i++)
                {
                    if (!Versions[i].IsFullyDeployed) n++;
                }
                return n;
            }
        }
    }

    public class DeployResult
    {
        public int UpdatedCount;
        public int SkippedUpToDateCount;
        public int AccessDeniedCount;
        public int LockedByProcessCount;
        public string ErrorMessage;
    }
}
