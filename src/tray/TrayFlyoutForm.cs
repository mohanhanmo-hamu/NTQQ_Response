using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace QQToastTray
{
    public class TrayFlyoutForm : Form
    {
        private readonly TrayApplicationContext _app;
        private readonly FluentPalette _palette;

        private Label _lblConnBadge;
        private Label _lblMetaInfo;
        private Label _lblVersionLine;

        private Panel _alertBanner;
        private Label _lblAlertText;
        private FluentButton _btnSyncNow;

        private FluentToggleRow _rowEnabled;
        private FluentToggleRow _rowGroup;
        private FluentToggleRow _rowMuteFg;
        private FluentToggleRow _rowSuppressNative;
        private FluentToggleRow _rowFollowQQ;
        private FluentToggleRow _rowAutoStart;
        private FluentToggleRow _rowAutoSync;
        private FluentToggleRow _rowVerboseLog;

        private FluentSegmentedControl _segLines;
        private FluentSegmentedControl _segChars;

        private FluentCard _switchesCard;
        private FluentCard _stackCard;
        private Panel _footerPanel;

        private readonly int[] _charOptions = new int[] { 30, 50, 80 };
        private bool _suppressDeactivate = false;

        public TrayFlyoutForm(TrayApplicationContext app)
        {
            _app = app;
            _palette = FluentPalette.Current();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = _palette.WindowBg;
            ForeColor = _palette.TextPrimary;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
            KeyPreview = true;
            DoubleBuffered = true;
            Width = 376;

            BuildUi();
            RefreshState();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.ApplyWin11Style(Handle, _palette.IsDark, FluentPalette.ToColorRef(_palette.CardBorder));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!_suppressDeactivate)
            {
                Hide();
            }
        }

        private void BuildUi()
        {
            int pad = 14;
            int cardW = Width - pad * 2;

            // --- Header ---
            var headerPanel = new Panel
            {
                Location = new Point(pad, 12),
                Size = new Size(cardW, 30),
                BackColor = _palette.WindowBg
            };

            var lblTitle = new Label
            {
                Text = "QQ 快捷回复助手",
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                ForeColor = _palette.TextPrimary,
                AutoSize = true,
                Location = new Point(0, 4)
            };
            headerPanel.Controls.Add(lblTitle);

            var lblVer = new Label
            {
                Text = "v" + AppMeta.Version,
                Font = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Regular),
                ForeColor = _palette.Accent,
                AutoSize = true,
                Location = new Point(126, 7)
            };
            headerPanel.Controls.Add(lblVer);

            var lblExit = new Label
            {
                Text = "退出程序",
                Font = new Font("Microsoft YaHei UI", 8.3f, FontStyle.Regular),
                ForeColor = _palette.TextSecondary,
                AutoSize = true,
                Cursor = Cursors.Hand,
                Location = new Point(cardW - 56, 6)
            };
            lblExit.MouseEnter += (s, e) => lblExit.ForeColor = Color.FromArgb(220, 68, 68);
            lblExit.MouseLeave += (s, e) => lblExit.ForeColor = _palette.TextSecondary;
            lblExit.Click += (s, e) =>
            {
                Hide();
                _app.ExitApplication();
            };
            headerPanel.Controls.Add(lblExit);
            Controls.Add(headerPanel);

            // --- Health & Version Status Card ---
            var statusCard = new FluentCard(_palette)
            {
                Location = new Point(pad, 48),
                Size = new Size(cardW, 82)
            };

            _lblConnBadge = new Label
            {
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                BackColor = _palette.CardBg,
                Location = new Point(12, 10)
            };
            statusCard.Controls.Add(_lblConnBadge);

            _lblMetaInfo = new Label
            {
                AutoSize = false,
                Size = new Size(cardW - 24, 20),
                Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular),
                ForeColor = _palette.TextSecondary,
                BackColor = _palette.CardBg,
                Location = new Point(12, 34)
            };
            statusCard.Controls.Add(_lblMetaInfo);

            _lblVersionLine = new Label
            {
                AutoSize = false,
                Size = new Size(cardW - 24, 20),
                Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular),
                ForeColor = _palette.TextSecondary,
                BackColor = _palette.CardBg,
                Location = new Point(12, 55)
            };
            statusCard.Controls.Add(_lblVersionLine);
            Controls.Add(statusCard);

            // --- Version Hot-Update Alert Banner (shown conditionally) ---
            _alertBanner = new Panel
            {
                Location = new Point(pad, 138),
                Size = new Size(cardW, 42),
                BackColor = _palette.WarnBg,
                Visible = false
            };
            _alertBanner.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, _alertBanner.Width - 1, _alertBanner.Height - 1);
                using (var path = FluentPalette.CreateRoundedRect(r, 7))
                using (var bg = new SolidBrush(_palette.WarnBg))
                using (var pen = new Pen(_palette.WarnBorder, 1f))
                {
                    e.Graphics.FillPath(bg, path);
                    e.Graphics.DrawPath(pen, path);
                }
            };

            _lblAlertText = new Label
            {
                AutoSize = false,
                Size = new Size(cardW - 96, 32),
                Location = new Point(10, 5),
                Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular),
                ForeColor = _palette.WarnFg,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _alertBanner.Controls.Add(_lblAlertText);

            _btnSyncNow = new FluentButton(_palette, "立即同步", true)
            {
                Size = new Size(74, 26),
                Location = new Point(cardW - 82, 8)
            };
            _btnSyncNow.Click += (s, e) =>
            {
                _suppressDeactivate = true;
                try
                {
                    _app.SyncBridgeVersionsInteractive();
                    RefreshState();
                }
                finally
                {
                    _suppressDeactivate = false;
                }
            };
            _alertBanner.Controls.Add(_btnSyncNow);
            Controls.Add(_alertBanner);

            // --- Switches Card ---
            _switchesCard = new FluentCard(_palette)
            {
                Size = new Size(cardW, 276)
            };

            _rowEnabled = AddToggle(_switchesCard, 6, "启用系统级快捷回复通知", "Windows 11 原生横幅直接打字回复", v => _app.SetEnabled(v));
            _rowGroup = AddToggle(_switchesCard, 50, "接收未免打扰的群聊通知", null, v => _app.SetGroupNotify(v));
            _rowMuteFg = AddToggle(_switchesCard, 84, "QQ 窗口在前台时静默", null, v => _app.SetMuteWhenForeground(v));
            _rowSuppressNative = AddToggle(_switchesCard, 118, "屏蔽 QQ 原生重复系统通知", null, v => _app.SetSuppressNative(v));
            _rowFollowQQ = AddToggle(_switchesCard, 152, "跟随 QQ 启动自动运行", null, v => _app.SetFollowQQStartup(v));
            _rowAutoStart = AddToggle(_switchesCard, 186, "开机自动启动", null, v => ConfigStore.SetAutoStart(v));
            _rowAutoSync = AddToggle(_switchesCard, 220, "自动同步 NTQQ 热更新版本", null, v => _app.SetAutoSyncVersions(v));
            _rowVerboseLog = AddToggle(_switchesCard, 254, "调试日志记录消息正文 (排错用)", null, v => _app.SetVerboseLog(v));
            _switchesCard.Height = 294;
            Controls.Add(_switchesCard);

            // --- Stacking & Limits Card ---
            _stackCard = new FluentCard(_palette)
            {
                Size = new Size(cardW, 92)
            };

            var lblLines = new Label
            {
                Text = "同会话堆叠条数",
                Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular),
                ForeColor = _palette.TextSecondary,
                BackColor = _palette.CardBg,
                AutoSize = true,
                Location = new Point(12, 13)
            };
            _stackCard.Controls.Add(lblLines);

            _segLines = new FluentSegmentedControl(_palette, new[] { "1 条", "2 条", "3 条", "4 条" }, 2)
            {
                Location = new Point(128, 9),
                Size = new Size(cardW - 140, 28)
            };
            _segLines.SelectedIndexChanged += (s, e) => _app.SetMaxHistoryLines(_segLines.SelectedIndex + 1);
            _stackCard.Controls.Add(_segLines);

            var lblChars = new Label
            {
                Text = "单条字数上限",
                Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular),
                ForeColor = _palette.TextSecondary,
                BackColor = _palette.CardBg,
                AutoSize = true,
                Location = new Point(12, 53)
            };
            _stackCard.Controls.Add(lblChars);

            _segChars = new FluentSegmentedControl(_palette, new[] { "30 字", "50 字", "80 字" }, 1)
            {
                Location = new Point(128, 49),
                Size = new Size(cardW - 140, 28)
            };
            _segChars.SelectedIndexChanged += (s, e) => _app.SetMaxCharsPerLine(_charOptions[_segChars.SelectedIndex]);
            _stackCard.Controls.Add(_segChars);
            Controls.Add(_stackCard);

            // --- Footer Quick Actions ---
            _footerPanel = new Panel
            {
                Size = new Size(cardW, 34),
                BackColor = _palette.WindowBg
            };

            int gap = 8;
            int btnW = (cardW - gap * 2) / 3;

            var btnPreview = new FluentButton(_palette, "预览通知", true)
            {
                Location = new Point(0, 0),
                Size = new Size(btnW, 32)
            };
            btnPreview.Click += (s, e) => _app.SendPreviewToast();

            var btnRepair = new FluentButton(_palette, "修复并重启QQ", false)
            {
                Location = new Point(btnW + gap, 0),
                Size = new Size(btnW + 8, 32)
            };
            btnRepair.Click += (s, e) =>
            {
                Hide();
                _app.RepairAndRestartQQ();
            };

            var btnFolder = new FluentButton(_palette, "日志目录", false)
            {
                Location = new Point((btnW + gap) * 2 + 8, 0),
                Size = new Size(cardW - ((btnW + gap) * 2 + 8), 32)
            };
            btnFolder.Click += (s, e) =>
            {
                Hide();
                try { Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); } catch { }
            };

            _footerPanel.Controls.Add(btnPreview);
            _footerPanel.Controls.Add(btnRepair);
            _footerPanel.Controls.Add(btnFolder);
            Controls.Add(_footerPanel);

            RelayoutVertical();
        }

        private FluentToggleRow AddToggle(FluentCard parent, int top, string title, string subtitle, Action<bool> onChange)
        {
            var row = new FluentToggleRow(_palette, title, subtitle)
            {
                Location = new Point(2, top),
                Width = parent.Width - 4
            };
            row.CheckedChanged += (s, e) => onChange(row.Checked);
            parent.Controls.Add(row);
            return row;
        }

        private void RelayoutVertical()
        {
            int pad = 14;
            int y = 138;
            if (_alertBanner.Visible)
            {
                _alertBanner.Location = new Point(pad, y);
                y += _alertBanner.Height + 10;
            }
            _switchesCard.Location = new Point(pad, y);
            y += _switchesCard.Height + 10;

            _stackCard.Location = new Point(pad, y);
            y += _stackCard.Height + 12;

            _footerPanel.Location = new Point(pad, y);
            y += _footerPanel.Height + 14;

            Height = y;
        }

        public void RefreshState()
        {
            AppConfig cfg = _app.Config;
            BridgeTelemetry tel = _app.Telemetry;
            QQInstallationReport report = _app.LatestReport;

            if (tel.IsConnected)
            {
                _lblConnBadge.Text = "●  已连接 NTQQ 内核";
                _lblConnBadge.ForeColor = _palette.SuccessFg;
                string loaderDesc = string.IsNullOrEmpty(tel.LoaderStatus) ? "桥接正常"
                    : (tel.LoaderStatus.StartsWith("patched:dynamic") ? "动态内存适配已生效" : tel.LoaderStatus);
                _lblMetaInfo.Text = "运行中: " + (string.IsNullOrEmpty(tel.RunningQQVersion) ? "NTQQ" : tel.RunningQQVersion)
                                    + "  ·  " + loaderDesc
                                    + "  ·  免打扰规则 " + tel.DndRuleCount + " 条";
            }
            else
            {
                _lblConnBadge.Text = "●  等待 QQ 桥接连接";
                _lblConnBadge.ForeColor = _palette.WarnFg;
                _lblMetaInfo.Text = report.Found
                    ? ("已检测到 NTQQ (" + report.Versions.Count + " 个版本)，启动或重启 QQ 后自动连接")
                    : "未检测到 NTQQ 安装目录，请检查 QQ 安装路径";
            }

            // Build concise version status summary
            if (report.Found && report.Versions.Count > 0)
            {
                var sb = new StringBuilder("版本: ");
                for (int i = 0; i < report.Versions.Count; i++)
                {
                    if (i > 0) sb.Append("  |  ");
                    var v = report.Versions[i];
                    sb.Append(v.Name);
                    if (v.IsReadyUpdate) sb.Append("(热更新)");
                    sb.Append(v.IsFullyDeployed ? " ✓" : " !");
                }
                _lblVersionLine.Text = sb.ToString();
            }
            else
            {
                _lblVersionLine.Text = "安装目录: " + report.QQRoot;
            }

            // Show hot-update / out-of-date alert banner if any version needs sync
            int pending = report.PendingSyncCount;
            if (report.Found && pending > 0)
            {
                if (!string.IsNullOrEmpty(report.ReadyVersion))
                {
                    _lblAlertText.Text = "检测到 NTQQ 热更新 " + report.ReadyVersion + "，建议立即预同步桥接。";
                }
                else
                {
                    _lblAlertText.Text = "有 " + pending + " 个 NTQQ 版本桥接未同步或需更新 loader。";
                }
                _alertBanner.Visible = true;
            }
            else
            {
                _alertBanner.Visible = false;
            }

            _rowEnabled.SetCheckedSilent(cfg.Enabled);
            _rowGroup.SetCheckedSilent(cfg.EnableGroupNotify);
            _rowMuteFg.SetCheckedSilent(cfg.MuteWhenQQForeground);
            _rowSuppressNative.SetCheckedSilent(cfg.SuppressNativeQQBanner);
            _rowFollowQQ.SetCheckedSilent(cfg.FollowQQStartup);
            _rowAutoStart.SetCheckedSilent(ConfigStore.IsAutoStartEnabled());
            _rowAutoSync.SetCheckedSilent(cfg.AutoSyncVersions);
            _rowVerboseLog.SetCheckedSilent(cfg.VerboseLog);

            _segLines.SetSelectedSilent(Math.Max(0, Math.Min(3, cfg.MaxHistoryLines - 1)));
            int charIdx = 1;
            if (cfg.MaxCharsPerLine <= 30) charIdx = 0;
            else if (cfg.MaxCharsPerLine >= 80) charIdx = 2;
            _segChars.SetSelectedSilent(charIdx);

            RelayoutVertical();
            PositionNearTaskbar();
        }

        public void ShowAtTaskbar()
        {
            RefreshState();
            PositionNearTaskbar();
            Show();
            Activate();
        }

        private void PositionNearTaskbar()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x = wa.Right - Width - 12;
            int y = wa.Bottom - Height - 12;
            if (x < wa.Left + 8) x = wa.Left + 8;
            if (y < wa.Top + 8) y = wa.Top + 8;
            Location = new Point(x, y);
        }
    }
}
