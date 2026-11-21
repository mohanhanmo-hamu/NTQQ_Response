using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QQToastTray
{
    public class ConfigStore
    {
        private readonly string _configPath;
        private readonly object _lock = new object();

        public ConfigStore(string configPath)
        {
            _configPath = configPath;
        }

        public AppConfig Load()
        {
            lock (_lock)
            {
                AppConfig cfg = null;
                bool needsSave = false;
                try
                {
                    if (File.Exists(_configPath))
                    {
                        string json = File.ReadAllText(_configPath, Encoding.UTF8);
                        if (json.Length > 0 && json[0] == '\uFEFF')
                        {
                            json = json.Substring(1);
                            needsSave = true;
                        }
                        var serializer = new JavaScriptSerializer();
                        cfg = serializer.Deserialize<AppConfig>(json);
                    }
                    else
                    {
                        needsSave = true;
                    }
                }
                catch
                {
                    needsSave = true;
                }

                if (cfg == null) cfg = new AppConfig();

                if (cfg.MaxHistoryLines < 1 || cfg.MaxHistoryLines > 4)
                {
                    cfg.MaxHistoryLines = 3;
                    needsSave = true;
                }
                if (cfg.MaxCharsPerLine <= 0)
                {
                    cfg.MaxCharsPerLine = 50;
                    needsSave = true;
                }
                if (cfg.LocalPort < 1024 || cfg.LocalPort > 65535)
                {
                    cfg.LocalPort = 39876;
                    needsSave = true;
                }
                if (string.IsNullOrEmpty(cfg.AuthToken))
                {
                    cfg.AuthToken = GenerateToken();
                    needsSave = true;
                }

                if (needsSave)
                {
                    SaveInternal(cfg);
                }
                return cfg;
            }
        }

        public void Save(AppConfig cfg)
        {
            lock (_lock)
            {
                SaveInternal(cfg);
            }
        }

        private void SaveInternal(AppConfig cfg)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(cfg);
                // Atomic UTF-8 (no BOM) write so qq-bridge.js never reads a half-written config.json
                string tmpPath = _configPath + ".tmp";
                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
                if (File.Exists(_configPath))
                {
                    try
                    {
                        File.Replace(tmpPath, _configPath, null);
                        return;
                    }
                    catch { }
                }
                File.Copy(tmpPath, _configPath, true);
                try { File.Delete(tmpPath); } catch { }
            }
            catch { }
        }

        private static string GenerateToken()
        {
            byte[] bytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }
            var sb = new StringBuilder(32);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        public static bool IsAutoStartEnabled()
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

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
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

        public static void ApplyNativeQQNotificationSuppression(bool suppress)
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
    }
}
