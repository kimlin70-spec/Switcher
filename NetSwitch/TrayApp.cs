using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NetSwitch.Net;

namespace NetSwitch
{
    /// <summary>Tray-only UI. Owns the NotifyIcon, the menu, the watchdog timer and the switch worker.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Config _cfg;
        private readonly NetworkSwitcher _switcher;
        private readonly TrayIcons _icons = new TrayIcons();
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miStatus;
        private readonly ToolStripMenuItem _miSwitch;
        private readonly ToolStripMenuItem _miAutostart;
        private readonly ToolStripMenuItem _miRestore;
        private readonly Form _sync;                 // hidden; gives us a UI-thread handle for BeginInvoke
        private readonly System.Windows.Forms.Timer _watchdogTimer;
        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private int _watchdogBusy;                   // 0/1, interlocked
        private bool _animFrame;
        private string _progressText = "";
        private DateTime _lastWatchdogBalloon = DateTime.MinValue;
        private bool _ready;
        private NetObservation _lastObs;

        public TrayApp(Config cfg, string startupNote)
        {
            _cfg = cfg;
            _switcher = new NetworkSwitcher(cfg);
            _switcher.Progress += text => Post(() => { _progressText = text; UpdateTooltip(); });

            _sync = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized, FormBorderStyle = FormBorderStyle.FixedToolWindow, Opacity = 0 };
            var _ = _sync.Handle; // force handle creation on this (UI) thread

            _miStatus = new ToolStripMenuItem("상태 확인 중…") { Enabled = false };
            _miSwitch = new ToolStripMenuItem("전환") { Enabled = false };
            _miSwitch.Click += (s, e) => Toggle();
            _miAutostart = new ToolStripMenuItem("Windows 시작 시 자동 실행") { CheckOnClick = false };
            _miAutostart.Click += (s, e) => ToggleAutostart();
            _miRestore = new ToolStripMenuItem("원래대로 복구 (와이파이·유선 모두 켬)");
            _miRestore.Click += (s, e) => RestoreDefaults();
            var miLog = new ToolStripMenuItem("로그 폴더 열기");
            miLog.Click += (s, e) => OpenDataFolder();
            var miAbout = new ToolStripMenuItem("정보");
            miAbout.Click += (s, e) => ShowAbout();
            var miExit = new ToolStripMenuItem("종료");
            miExit.Click += (s, e) => ExitApp();

            _menu = new ContextMenuStrip();
            _menu.Items.Add(_miStatus);
            _menu.Items.Add(_miSwitch);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miAutostart);
            _menu.Items.Add(_miRestore);
            _menu.Items.Add(miLog);
            _menu.Items.Add(miAbout);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miExit);
            _menu.Opening += (s, e) => RefreshAutostartCheck();

            _tray = new NotifyIcon
            {
                Icon = _icons.Get(TrayIconKind.SwitchingA),
                Text = "NetSwitch: 시작 중…",
                ContextMenuStrip = _menu,
                Visible = true
            };
            // Double-click only (a single click must never switch).
            _tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) Toggle(); };

            _watchdogTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _watchdogTimer.Tick += (s, e) => RunWatchdog();

            _animTimer = new System.Windows.Forms.Timer { Interval = 450 };
            _animTimer.Tick += (s, e) =>
            {
                _animFrame = !_animFrame;
                _tray.Icon = _icons.Get(_animFrame ? TrayIconKind.SwitchingB : TrayIconKind.SwitchingA);
            };
            _animTimer.Start();

            if (!string.IsNullOrEmpty(startupNote)) Balloon("NetSwitch", startupNote, ToolTipIcon.Info);

            // Discover adapters and make the physical state agree with the saved mode, off the UI thread.
            Task.Run(() => Startup());
        }

        // ------------------------------------------------------------------ startup

        private void Startup()
        {
            string note = null;
            try
            {
                _switcher.DiscoverAdapters();
                _switcher.EnforceStartupState(_cts.Token, out note);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error("시작 실패", ex);
                Post(() =>
                {
                    _animTimer.Stop();
                    _tray.Icon = _icons.Get(TrayIconKind.Error);
                    _tray.Text = Trunc("NetSwitch: 오류");
                    _miStatus.Text = "오류: " + ex.Message;
                    Balloon("NetSwitch 시작 실패", ex.Message, ToolTipIcon.Error);
                });
                return;
            }

            Post(() =>
            {
                _ready = true;
                _animTimer.Stop();
                _miSwitch.Enabled = true;
                if (!string.IsNullOrEmpty(note)) Balloon("NetSwitch", note, ToolTipIcon.Info);
                _watchdogTimer.Start();
            });
            RunWatchdog();
        }

        // ------------------------------------------------------------------ switching

        private void Toggle()
        {
            if (!_ready || _switcher.IsSwitching) return;
            var target = _switcher.OppositeOf(_switcher.CurrentMode);
            Log.Info("사용자 요청: " + NetworkSwitcher.Describe(target) + " 로 전환");
            _miSwitch.Enabled = false;
            _progressText = "전환 시작…";
            _animTimer.Start();
            UpdateTooltip();
            Balloon("전환 중", NetworkSwitcher.Describe(target) + " 으로 전환합니다…", ToolTipIcon.Info);

            Task.Run(() =>
            {
                string warn = null, error = null;
                try { _switcher.SwitchTo(target, _cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (SwitchWarning w) { warn = w.Message; }
                catch (SwitchException ex) { error = ex.Message; }
                catch (Exception ex) { Log.Error("전환 중 예기치 않은 오류", ex); error = "예기치 않은 오류: " + ex.Message; }

                Post(() =>
                {
                    _animTimer.Stop();
                    _progressText = "";
                    _miSwitch.Enabled = true;
                    if (error != null)
                        Balloon("전환 실패", error, ToolTipIcon.Error);
                    else if (warn != null)
                        Balloon(NetworkSwitcher.Describe(_switcher.CurrentMode) + " (확인 필요)", warn, ToolTipIcon.Warning);
                    else
                        Balloon("전환 완료", NetworkSwitcher.Describe(_switcher.CurrentMode) + " 을(를) 사용할 수 있습니다.", ToolTipIcon.Info);
                });
                RunWatchdog();
            });
        }

        // ------------------------------------------------------------------ watchdog / status

        private void RunWatchdog()
        {
            if (!_ready || _switcher.IsSwitching) return;
            if (Interlocked.CompareExchange(ref _watchdogBusy, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try
                {
                    var o = _switcher.Observe();
                    string msg = _cfg.Watchdog ? _switcher.Watchdog(o, _cts.Token) : null;
                    if (msg != null) o = _switcher.Observe();
                    Post(() =>
                    {
                        _lastObs = o;
                        RefreshStatus(o);
                        if (msg != null && (DateTime.UtcNow - _lastWatchdogBalloon).TotalMinutes >= 2)
                        {
                            _lastWatchdogBalloon = DateTime.UtcNow;
                            Balloon("NetSwitch", msg, ToolTipIcon.Warning);
                        }
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Error("상태 갱신 실패", ex); }
                finally { Interlocked.Exchange(ref _watchdogBusy, 0); }
            });
        }

        private void RefreshStatus(NetObservation o)
        {
            if (_switcher.IsSwitching) return;
            var mode = _switcher.CurrentMode;
            TrayIconKind kind;
            string status;
            if (mode == NetMode.Lan)
            {
                if (o.Ethernet == null) { kind = TrayIconKind.LanWarn; status = "업무망(유선): 어댑터가 보이지 않음"; }
                else if (!o.EthernetLink) { kind = TrayIconKind.LanWarn; status = "업무망(유선): 랜선이 연결되지 않음"; }
                else if (!o.EthernetUsable) { kind = TrayIconKind.LanWarn; status = "업무망(유선): IP 주소 대기 중"; }
                else { kind = TrayIconKind.Lan; status = "업무망(유선 랜) 사용 중 · " + o.EthernetIp.Address; }
                _miSwitch.Text = "인터넷(와이파이)으로 전환";
            }
            else if (mode == NetMode.Wifi)
            {
                if (!o.WifiConnected) { kind = TrayIconKind.WifiWarn; status = "인터넷(와이파이): 연결되지 않음"; }
                else if (!o.WifiUsable) { kind = TrayIconKind.WifiWarn; status = "인터넷(와이파이): IP 주소 대기 중"; }
                else { kind = TrayIconKind.Wifi; status = "인터넷(와이파이) 사용 중 · " + (string.IsNullOrEmpty(o.Wifi.Ssid) ? o.Wifi.ProfileName : o.Wifi.Ssid); }
                _miSwitch.Text = "업무망(유선 랜)으로 전환";
            }
            else
            {
                kind = TrayIconKind.Error; status = "모드 알 수 없음";
                _miSwitch.Text = "업무망(유선 랜)으로 전환";
            }
            _tray.Icon = _icons.Get(kind);
            _miStatus.Text = status;
            _tray.Text = Trunc("NetSwitch · " + status + "\n더블클릭: 반대쪽으로 전환");
        }

        private void UpdateTooltip()
        {
            if (!string.IsNullOrEmpty(_progressText))
            {
                _tray.Text = Trunc("NetSwitch · " + _progressText);
                _miStatus.Text = _progressText;
            }
        }

        // ------------------------------------------------------------------ menu actions

        private void RefreshAutostartCheck()
        {
            Task.Run(() =>
            {
                bool on;
                try { on = Autostart.IsRegistered(); } catch { on = false; }
                Post(() => _miAutostart.Checked = on);
            });
        }

        private void ToggleAutostart()
        {
            bool turnOn = !_miAutostart.Checked;
            Task.Run(() =>
            {
                string err = null;
                try { if (turnOn) Autostart.Register(); else Autostart.Unregister(); }
                catch (Exception ex) { err = ex.Message; Log.Error("자동 실행 설정 변경 실패", ex); }
                Post(() =>
                {
                    if (err != null) Balloon("자동 실행 설정 실패", err, ToolTipIcon.Error);
                    else Balloon("NetSwitch", turnOn ? "Windows 시작 시 자동으로 실행됩니다." : "자동 실행을 해제했습니다. (관리자 권한 없이 실행하면 UAC 창이 뜹니다)", ToolTipIcon.Info);
                    RefreshAutostartCheck();
                });
            });
        }

        private void RestoreDefaults()
        {
            if (_switcher.IsSwitching) return;
            var r = MessageBox.Show(_sync,
                "와이파이와 유선 랜을 모두 켜고 윈도우 기본 상태로 되돌립니다.\n두 망이 동시에 연결될 수 있습니다.\n\n계속할까요?",
                "원래대로 복구", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;
            _miSwitch.Enabled = false;
            Task.Run(() =>
            {
                string err = null;
                try { _switcher.RestoreDefaults(_cts.Token); }
                catch (Exception ex) { err = ex.Message; }
                Post(() =>
                {
                    _miSwitch.Enabled = true;
                    if (err != null) Balloon("복구 실패", err, ToolTipIcon.Error);
                    else Balloon("복구 완료", "와이파이와 유선 랜이 모두 켜졌습니다. 더블클릭하면 다시 한쪽만 사용합니다.", ToolTipIcon.Info);
                });
                RunWatchdog();
            });
        }

        private void OpenDataFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(AppPaths.DataDir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.DataDir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Balloon("폴더 열기 실패", ex.Message, ToolTipIcon.Error); }
        }

        private void ShowAbout()
        {
            var ver = typeof(TrayApp).Assembly.GetName().Version;
            var o = _lastObs;
            var text =
                "NetSwitch " + ver + "\n" +
                "인터넷(와이파이) ↔ 업무망(유선 랜) 전환기\n\n" +
                "유선 랜: " + (_switcher.EthernetName ?? "-") + "\n" +
                "와이파이: " + (_switcher.WifiName ?? "-") + "\n" +
                "와이파이 끊는 방식: " + (_cfg.WifiMethod == WifiMethod.Adapter ? "장치 끄기" : "자동구성 끄기 + 연결 끊기") + "\n" +
                "현재 모드: " + NetworkSwitcher.Describe(_switcher.CurrentMode) + "\n" +
                (o != null ? "유선: " + (o.Ethernet == null ? "없음" : (o.EthernetEnabled ? "켜짐" : "꺼짐") + (o.EthernetLink ? ", 링크 있음" : ", 링크 없음") + ", " + o.EthernetIp) + "\n" : "") +
                (o != null ? "와이파이: " + o.Wifi + ", " + o.WifiIp + "\n" : "") +
                "\n설정·로그 폴더: " + AppPaths.DataDir + "\n" +
                "관리자 권한: " + (Elevation.IsElevated() ? "예" : "아니오");
            MessageBox.Show(_sync, text, "NetSwitch 정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExitApp()
        {
            Log.Info("사용자 종료");
            _cts.Cancel();
            _watchdogTimer.Stop();
            _animTimer.Stop();
            _tray.Visible = false;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _tray.Visible = false; _tray.Dispose(); } catch { }
                try { _menu.Dispose(); } catch { }
                try { _icons.Dispose(); } catch { }
                try { _sync.Dispose(); } catch { }
                try { _watchdogTimer.Dispose(); _animTimer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ helpers

        private void Post(Action a)
        {
            try
            {
                if (_sync.IsDisposed) return;
                if (_sync.InvokeRequired) _sync.BeginInvoke(a); else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void Balloon(string title, string text, ToolTipIcon icon)
        {
            try { _tray.ShowBalloonTip(5000, title, text, icon); } catch { }
        }

        private static string Trunc(string s)
        {
            // NotifyIcon.Text is limited to 63 characters on .NET Framework.
            if (s == null) return "";
            return s.Length <= 63 ? s : s.Substring(0, 60) + "…";
        }
    }
}
