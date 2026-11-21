using System;
using System.IO;
using System.Text;

namespace QQToastTray
{
    public class RotatingLogger
    {
        private readonly string _logPath;
        private readonly string _backupPath;
        private readonly long _maxBytes;
        private readonly object _lock = new object();
        private int _writeCount = 0;

        public RotatingLogger(string logPath, long maxBytes = 512 * 1024)
        {
            _logPath = logPath;
            _backupPath = logPath + ".1";
            _maxBytes = maxBytes;
        }

        public string LogPath { get { return _logPath; } }

        public void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    _writeCount++;
                    if (_writeCount == 1 || (_writeCount % 40) == 0)
                    {
                        RotateIfNeeded();
                    }
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine;
                    File.AppendAllText(_logPath, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        private void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(_logPath)) return;
                FileInfo fi = new FileInfo(_logPath);
                if (fi.Length >= _maxBytes)
                {
                    try
                    {
                        if (File.Exists(_backupPath)) File.Delete(_backupPath);
                    }
                    catch { }
                    File.Move(_logPath, _backupPath);
                }
            }
            catch { }
        }
    }
}
