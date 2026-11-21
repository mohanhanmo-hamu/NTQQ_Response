using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace QQToastTray
{
    public class LocalHttpServer
    {
        private readonly AppConfig _config;
        private readonly BridgeTelemetry _telemetry;
        private readonly RotatingLogger _logger;
        private readonly Action<IncomingMessage> _onIncomingMessage;
        private readonly Func<List<ReplyCommand>> _dequeuePendingReplies;

        private TcpListener _tcpListener;
        private Thread _serverThread;
        private volatile bool _running = false;
        private int _authFailureLogged = 0;

        private const int MAX_BODY_BYTES = 512 * 1024;

        public LocalHttpServer(
            AppConfig config,
            BridgeTelemetry telemetry,
            RotatingLogger logger,
            Action<IncomingMessage> onIncomingMessage,
            Func<List<ReplyCommand>> dequeuePendingReplies)
        {
            _config = config;
            _telemetry = telemetry;
            _logger = logger;
            _onIncomingMessage = onIncomingMessage;
            _dequeuePendingReplies = dequeuePendingReplies;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _serverThread = new Thread(ServerLoop);
            _serverThread.IsBackground = true;
            _serverThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_tcpListener != null) _tcpListener.Stop();
            }
            catch { }
        }

        private void ServerLoop()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Loopback, _config.LocalPort);
                _tcpListener.Start();
                _logger.Write("LocalHttpServer listening on 127.0.0.1:" + _config.LocalPort);

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

                    TcpClient captured = client;
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(captured));
                }
            }
            catch (Exception ex)
            {
                _logger.Write("LocalHttpServer error: " + ex.Message);
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 3000;
                    stream.WriteTimeout = 3000;

                    var ms = new MemoryStream();
                    byte[] chunk = new byte[8192];
                    int headerEnd = -1;
                    int contentLength = 0;
                    string authHeader = "";

                    while (ms.Length < MAX_BODY_BYTES + 16384)
                    {
                        int n = stream.Read(chunk, 0, chunk.Length);
                        if (n <= 0) break;
                        ms.Write(chunk, 0, n);

                        if (headerEnd < 0)
                        {
                            byte[] current = ms.GetBuffer();
                            int len = (int)ms.Length;
                            string headerProbe = Encoding.ASCII.GetString(current, 0, Math.Min(len, 16384));
                            int idx = headerProbe.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                headerEnd = idx + 4;
                                string headersPart = headerProbe.Substring(0, idx);
                                foreach (string line in headersPart.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    {
                                        int.TryParse(line.Substring(15).Trim(), out contentLength);
                                        if (contentLength < 0 || contentLength > MAX_BODY_BYTES) contentLength = 0;
                                    }
                                    else if (line.StartsWith("X-Auth-Token:", StringComparison.OrdinalIgnoreCase))
                                    {
                                        authHeader = line.Substring(13).Trim();
                                    }
                                }
                            }
                        }

                        if (headerEnd >= 0 && ms.Length >= headerEnd + contentLength)
                        {
                            break;
                        }
                    }

                    if (headerEnd < 0) return;

                    byte[] allBytes = ms.ToArray();
                    string reqHeader = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
                    string firstLine = reqHeader.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                    string[] parts = firstLine.Split(' ');
                    string method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "GET";
                    string rawTarget = parts.Length > 1 ? parts[1] : "/";

                    // Validate AuthToken when configured (prevents arbitrary local process spoofing)
                    string expectedToken = _config.AuthToken ?? "";
                    if (!string.IsNullOrEmpty(expectedToken) &&
                        !string.Equals(authHeader, expectedToken, StringComparison.Ordinal))
                    {
                        if (Interlocked.Exchange(ref _authFailureLogged, 1) == 0)
                        {
                            _logger.Write("Rejected unauthenticated local IPC request (restart QQ if upgrading from v1.1.x).");
                        }
                        WriteHttpResponse(stream, "401 Unauthorized", "{\"ok\":false,\"error\":\"unauthorized\"}");
                        return;
                    }

                    string pathOnly = rawTarget;
                    string queryString = "";
                    int qIdx = rawTarget.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        pathOnly = rawTarget.Substring(0, qIdx);
                        queryString = rawTarget.Substring(qIdx + 1);
                    }
                    string pathLower = pathOnly.ToLowerInvariant();

                    _telemetry.LastHeartbeat = DateTime.Now;
                    // Per-request serializer (JavaScriptSerializer is not guaranteed thread-safe across threads)
                    var serializer = new JavaScriptSerializer();
                    string responseJson = "{\"ok\":true}";

                    if (pathLower.StartsWith("/notify") && method == "POST")
                    {
                        int bodyLen = Math.Min(contentLength, allBytes.Length - headerEnd);
                        if (bodyLen > 0)
                        {
                            string body = Encoding.UTF8.GetString(allBytes, headerEnd, bodyLen);
                            var msg = serializer.Deserialize<IncomingMessage>(body);
                            // Fixed v1.1.1 bug: allow isImageUpdate payloads whose content is intentionally empty!
                            if (msg != null &&
                                (!string.IsNullOrEmpty(msg.content) || (msg.isImageUpdate && !string.IsNullOrEmpty(msg.imagePath))))
                            {
                                if (_config.VerboseLog)
                                {
                                    _logger.Write("Received /notify: " + body);
                                }
                                else
                                {
                                    _logger.Write("Received /notify: chatType=" + msg.chatType + ", isImageUpdate=" + msg.isImageUpdate + ", img=" + (!string.IsNullOrEmpty(msg.imagePath)));
                                }
                                if (_onIncomingMessage != null)
                                {
                                    _onIncomingMessage(msg);
                                }
                            }
                        }
                    }
                    else if (pathLower.StartsWith("/poll"))
                    {
                        ParseTelemetryQuery(queryString);
                        List<ReplyCommand> cmds = _dequeuePendingReplies != null ? _dequeuePendingReplies() : null;
                        if (cmds != null && cmds.Count > 0)
                        {
                            responseJson = serializer.Serialize(new { ok = true, replies = cmds });
                            _logger.Write("Dispatched " + cmds.Count + " command(s) to bridge.");
                        }
                        else
                        {
                            responseJson = "{\"ok\":true,\"replies\":[]}";
                        }
                    }

                    WriteHttpResponse(stream, "200 OK", responseJson);
                }
            }
            catch (Exception ex)
            {
                _logger.Write("LocalHttpServer handler error: " + ex.Message);
            }
        }

        private void ParseTelemetryQuery(string queryString)
        {
            if (string.IsNullOrEmpty(queryString)) return;
            try
            {
                string[] pairs = queryString.Split('&');
                for (int i = 0; i < pairs.Length; i++)
                {
                    int eq = pairs[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string k = pairs[i].Substring(0, eq);
                    string v = Uri.UnescapeDataString(pairs[i].Substring(eq + 1));
                    if (k == "qqVersion") _telemetry.RunningQQVersion = v;
                    else if (k == "loader") _telemetry.LoaderStatus = v;
                    else if (k == "bridgeVer") _telemetry.BridgeVersion = v;
                    else if (k == "dndCount")
                    {
                        int n;
                        if (int.TryParse(v, out n)) _telemetry.DndRuleCount = n;
                    }
                }
            }
            catch { }
        }

        private static void WriteHttpResponse(NetworkStream stream, string status, string json)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
            string respHeader =
                "HTTP/1.1 " + status + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(respHeader);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }
    }
}
