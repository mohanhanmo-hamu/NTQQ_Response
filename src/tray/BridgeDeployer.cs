using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QQToastTray
{
    public class BridgeDeployer
    {
        private readonly string _baseDir;
        private readonly string _bridgeJsPath;
        private readonly string _srcDllPath;
        private readonly RotatingLogger _logger;

        public BridgeDeployer(string baseDir, RotatingLogger logger)
        {
            _baseDir = baseDir;
            _bridgeJsPath = Path.Combine(baseDir, "qq-bridge.js");
            _srcDllPath = Path.Combine(baseDir, "version.dll");
            _logger = logger;
        }

        public static string DetectQQInstallRoot()
        {
            string[] subKeys = new string[]
            {
                @"SOFTWARE\WOW6432Node\Tencent\QQNT",
                @"SOFTWARE\Tencent\QQNT"
            };
            foreach (string subKey in subKeys)
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(subKey, false))
                    {
                        if (key == null) continue;
                        string install = key.GetValue("Install") as string;
                        if (!string.IsNullOrEmpty(install) && Directory.Exists(install))
                        {
                            return install;
                        }
                    }
                }
                catch { }
            }

            string defaultPath = @"C:\Program Files\Tencent\QQNT";
            if (Directory.Exists(defaultPath)) return defaultPath;
            return defaultPath;
        }

        public string BuildExpectedLoaderScript()
        {
            string normalizedBridge = _bridgeJsPath.Replace("\\", "/");
            return "// NTQQ_Response Loader\ntry { require(\"" + normalizedBridge + "\"); } catch (e) {}\n";
        }

        private static string ComputeFileSha256(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder(64);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return "";
            }
        }

        public QQInstallationReport InspectInstallation()
        {
            var report = new QQInstallationReport();
            report.QQRoot = DetectQQInstallRoot();
            string versionsDir = Path.Combine(report.QQRoot, "versions");
            if (!Directory.Exists(versionsDir))
            {
                report.Found = false;
                return report;
            }
            report.Found = true;

            // Parse NTQQ versions/config.json to know curVersion and readyVersion (hot update)
            try
            {
                string qqCfgPath = Path.Combine(versionsDir, "config.json");
                if (File.Exists(qqCfgPath))
                {
                    string raw = File.ReadAllText(qqCfgPath, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    var dict = serializer.Deserialize<Dictionary<string, object>>(raw);
                    if (dict != null)
                    {
                        object curObj;
                        if (dict.TryGetValue("curVersion", out curObj) && curObj != null)
                            report.CurVersion = curObj.ToString();
                        object readyObj;
                        if (dict.TryGetValue("readyVersion", out readyObj) && readyObj != null)
                            report.ReadyVersion = readyObj.ToString();
                    }
                }
            }
            catch { }

            string srcDllHash = ComputeFileSha256(_srcDllPath);
            string expectedScript = BuildExpectedLoaderScript();

            try
            {
                string[] dirs = Directory.GetDirectories(versionsDir);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < dirs.Length; i++)
                {
                    string vDir = dirs[i];
                    string resDir = Path.Combine(vDir, "resources");
                    string qqntDll = Path.Combine(vDir, "QQNT.dll");
                    // Skip delta patch folders like "9.9.36-53644-9.9.37-54395" that lack QQNT.dll or resources/
                    if (!Directory.Exists(resDir) || !File.Exists(qqntDll)) continue;

                    string name = Path.GetFileName(vDir);
                    string targetDll = Path.Combine(vDir, "version.dll");
                    string targetQqt = Path.Combine(resDir, "qqt.js");

                    bool hasDll = File.Exists(targetDll);
                    bool dllMatch = hasDll && !string.IsNullOrEmpty(srcDllHash) &&
                                    string.Equals(ComputeFileSha256(targetDll), srcDllHash, StringComparison.OrdinalIgnoreCase);

                    bool hasScript = File.Exists(targetQqt);
                    bool scriptMatch = false;
                    if (hasScript)
                    {
                        try
                        {
                            string existing = File.ReadAllText(targetQqt, Encoding.UTF8);
                            scriptMatch = string.Equals(existing.Trim(), expectedScript.Trim(), StringComparison.Ordinal);
                        }
                        catch { }
                    }

                    report.Versions.Add(new QQVersionInfo
                    {
                        Name = name,
                        DirPath = vDir,
                        IsCurrent = string.Equals(name, report.CurVersion, StringComparison.OrdinalIgnoreCase),
                        IsReadyUpdate = !string.IsNullOrEmpty(report.ReadyVersion) &&
                                        string.Equals(name, report.ReadyVersion, StringComparison.OrdinalIgnoreCase),
                        HasLoaderDll = hasDll,
                        LoaderDllUpToDate = dllMatch,
                        HasLoaderScript = hasScript,
                        LoaderScriptUpToDate = scriptMatch
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Write("InspectInstallation error: " + ex.Message);
            }

            return report;
        }

        public DeployResult DeployAllVersions(bool allowUacFallback)
        {
            var result = new DeployResult();
            try
            {
                // Clean up obsolete NODE_OPTIONS environment variable from early versions
                try
                {
                    string current = Environment.GetEnvironmentVariable("NODE_OPTIONS", EnvironmentVariableTarget.User) ?? "";
                    if (current.Contains("qq-bridge.js"))
                    {
                        Environment.SetEnvironmentVariable("NODE_OPTIONS", null, EnvironmentVariableTarget.User);
                    }
                }
                catch { }

                if (!File.Exists(_srcDllPath) || !File.Exists(_bridgeJsPath))
                {
                    result.ErrorMessage = "缺少 version.dll 或 qq-bridge.js";
                    return result;
                }

                QQInstallationReport report = InspectInstallation();
                if (!report.Found || report.Versions.Count == 0)
                {
                    result.ErrorMessage = "未检测到 NTQQ 安装目录";
                    return result;
                }

                string loaderCode = BuildExpectedLoaderScript();
                var utf8NoBom = new UTF8Encoding(false);

                foreach (var v in report.Versions)
                {
                    if (v.IsFullyDeployed)
                    {
                        result.SkippedUpToDateCount++;
                        continue;
                    }

                    try
                    {
                        string resDir = Path.Combine(v.DirPath, "resources");
                        string qqtPath = Path.Combine(resDir, "qqt.js");
                        string legacyPath = Path.Combine(resDir, "qqtoast.js");
                        string targetDll = Path.Combine(v.DirPath, "version.dll");

                        File.WriteAllText(qqtPath, loaderCode, utf8NoBom);
                        // Also keep legacy qqtoast.js updated while old version.dll may still be loaded in memory
                        File.WriteAllText(legacyPath, loaderCode, utf8NoBom);

                        if (!v.LoaderDllUpToDate)
                        {
                            File.Copy(_srcDllPath, targetDll, true);
                        }

                        result.UpdatedCount++;
                        _logger.Write("Deployed bridge to NTQQ version: " + v.Name);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        result.AccessDeniedCount++;
                    }
                    catch (IOException)
                    {
                        // version.dll is currently loaded by running QQ.exe
                        result.LockedByProcessCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.Write("Deploy error for " + v.Name + ": " + ex.Message);
                    }
                }

                if (result.AccessDeniedCount > 0 && allowUacFallback)
                {
                    if (RunElevatedSelf("--deploy-bridge"))
                    {
                        return InspectAfterElevation();
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                _logger.Write("DeployAllVersions error: " + ex.Message);
            }
            return result;
        }

        private DeployResult InspectAfterElevation()
        {
            var r = new DeployResult();
            var report = InspectInstallation();
            foreach (var v in report.Versions)
            {
                if (v.IsFullyDeployed) r.UpdatedCount++;
                else r.LockedByProcessCount++;
            }
            return r;
        }

        public bool RemoveAllBridges(bool allowUacFallback)
        {
            bool anyAccessDenied = false;
            try
            {
                KillQQProcesses();
                Thread.Sleep(500);

                QQInstallationReport report = InspectInstallation();
                string versionsDir = Path.Combine(report.QQRoot, "versions");
                if (Directory.Exists(versionsDir))
                {
                    foreach (var vDir in Directory.GetDirectories(versionsDir))
                    {
                        try
                        {
                            string qqt = Path.Combine(vDir, "resources", "qqt.js");
                            if (File.Exists(qqt)) File.Delete(qqt);
                            string legacy = Path.Combine(vDir, "resources", "qqtoast.js");
                            if (File.Exists(legacy)) File.Delete(legacy);
                            string dll = Path.Combine(vDir, "version.dll");
                            if (File.Exists(dll)) File.Delete(dll);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            anyAccessDenied = true;
                        }
                        catch (Exception ex)
                        {
                            _logger.Write("RemoveBridge error in " + vDir + ": " + ex.Message);
                        }
                    }
                }

                if (anyAccessDenied && allowUacFallback)
                {
                    if (RunElevatedSelf("--remove-bridge"))
                    {
                        ConfigStore.ApplyNativeQQNotificationSuppression(false);
                        return true;
                    }
                    return false;
                }

                ConfigStore.ApplyNativeQQNotificationSuppression(false);
                return !anyAccessDenied;
            }
            catch (Exception ex)
            {
                _logger.Write("RemoveAllBridges error: " + ex.Message);
                return false;
            }
        }

        public bool RepairAndRestartQQ()
        {
            try
            {
                string qqRoot = DetectQQInstallRoot();
                string qqExePath = Path.Combine(qqRoot, "QQ.exe");

                KillQQProcesses();
                Thread.Sleep(700);

                DeployResult res = DeployAllVersions(true);
                if (res.AccessDeniedCount > 0)
                {
                    MessageBox.Show("写入 QQ 安装目录需要管理员权限，请在 UAC 提示中允许或以管理员身份运行。", AppMeta.APP_DISPLAY_NAME, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                if (File.Exists(qqExePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = qqExePath,
                        WorkingDirectory = qqRoot,
                        UseShellExecute = true
                    });
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.Write("RepairAndRestartQQ error: " + ex.Message);
                MessageBox.Show("重启 QQ 失败: " + ex.Message, AppMeta.APP_DISPLAY_NAME, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        public static void KillQQProcesses()
        {
            foreach (var proc in Process.GetProcessesByName("QQ"))
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
            }
        }

        private bool RunElevatedSelf(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = arguments,
                    WorkingDirectory = _baseDir,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.WaitForExit(15000);
                        return p.ExitCode == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Write("UAC elevation cancelled or failed: " + ex.Message);
            }
            return false;
        }
    }
}
