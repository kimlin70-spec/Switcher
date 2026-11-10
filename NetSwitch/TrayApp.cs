using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
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
        private readonly ToolStripMenuItem _miExit;
        private readonly Form _sync;                 // hidden, never shown; gives us a UI-thread handle for BeginInvoke
        private readonly System.Windows.Forms.Timer _watchdogTimer;
        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly System.Windows.Forms.Timer _retryTimer;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private int _watchdogBusy;                   // 0/1, interlocked
        private bool _busy;                          // UI-thread flag: a switch or restore is in flight
        private bool _animFrame;
        private string _progressText = "";
        private DateTime _lastWatchdogBalloon = DateTime.MinValue;
        private bool _ready;
        private bool _startupRunning;
        private NetObservation _lastObs;
        private string _pendingNote;
        private Task _worker;                        // current switch/restore task, awaited briefly on exit

        public TrayApp(Config cfg, string startupNote)
        {
            _cfg = cfg;
            _switcher = new NetworkSwitcher(cfg);
            _switcher.Progress += text => Post(() => { _progressText = text; UpdateTooltip(); });

            _sync = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-10000, -10000), Size = new System.Drawing.Size(1, 1) };
            var _ = _sync.Handle; // force handle creation on this (UI) thread; the form is never shown

            _miStatus = new ToolStripMenuItem("상태 확인 중…") { Enabled = false };
            _miSwitch = new ToolStripMenuItem("전환") { Enabled = false };
            _miSwitch.Click += (s, e) => Toggle();
            _miAutostart = new ToolStripMenuItem("Windows 시작 시 자동 실행") { CheckOnClick = false, Enabled = false };
            _miAutostart.Click += (s, e) => ToggleAutostart();
            _miRestore = new ToolStripMenuItem("원래대로 복구 (와이파이·유선 모두 켬)") { Enabled = false };
            _miRestore.Click += (s, e) => RestoreDefaults();
            var miLog = new ToolStripMenuItem("설정·로그 폴더 열기");
            miLog.Click += (s, e) => OpenDataFolder();
            var miAbout = new ToolStripMenuItem("정보");
            miAbout.Click += (s, e) => ShowAbout();
            _miExit = new ToolStripMenuItem("종료");
            _miExit.Click += (s, e) => ExitApp();

            _menu = new ContextMenuStrip();
            _menu.Items.Add(_miStatus);
            _menu.Items.Add(_miSwitch);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miAutostart);
            _menu.Items.Add(_miRestore);
            _menu.Items.Add(miLog);
            _menu.Items.Add(miAbout);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miExit);
            _menu.Opening += (s, e) => RefreshAutostartCheck();

            // We run elevated while Explorer runs at medium integrity. Without this, UIPI would block the
            // TaskbarCreated broadcast and the icon would never come back after an Explorer restart.
            try
            {
                uint wmTaskbarCreated = Native.NativeMethods.RegisterWindowMessage("TaskbarCreated");
                if (wmTaskbarCreated != 0) Native.NativeMethods.ChangeWindowMessageFilter(wmTaskbarCreated, Native.NativeMethods.MSGFLT_ADD);
            }
            catch (Exception ex) { Log.Warn("TaskbarCreated 메시지 필터 설정 실패: " + ex.Message); }

            _tray = new NotifyIcon
            {
                Icon = _icons.Get(TrayIconKind.SwitchingA),
                Text = "NetSwitch: 시작 중…",
                ContextMenuStrip = _menu,
                Visible = false
            };
            // Double-click only (a single click must never switch).
            _tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
            _pendingNote = startupNote;   // shown once the icon is visible (balloons on an invisible icon are dropped)
            ShowIconWhenTaskbarReady();

            _watchdogTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _watchdogTimer.Tick += (s, e) => RunWatchdog();

            _animTimer = new System.Windows.Forms.Timer { Interval = 450 };
            _animTimer.Tick += (s, e) =>
            {
                _animFrame = !_animFrame;
                _tray.Icon = _icons.Get(_animFrame ? TrayIconKind.SwitchingB : TrayIconKind.SwitchingA);
            };
            _animTimer.Start();

            // Retry discovery while it has not succeeded (e.g. WMI not ready right after logon).
            _retryTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            _retryTimer.Tick += (s, e) => { if (!_ready) StartStartup(); };

            // React quickly to cable/dock/Wi-Fi changes instead of waiting for the next 3 s tick.
            try
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += (s, e) => OnNetworkChanged(s, EventArgs.Empty);
            }
            catch (Exception ex) { Log.Warn("네트워크 변경 알림 등록 실패: " + ex.Message); }

            StartStartup();
        }

        // ------------------------------------------------------------------ startup

        /// <summary>
        /// At logon the task may start us before Explorer has created the taskbar; Shell_NotifyIcon(NIM_ADD)
        /// then fails silently. Wait (bounded) for the taskbar window before making the icon visible.
        /// </summary>
        private void ShowIconWhenTaskbarReady()
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            var t = new System.Windows.Forms.Timer { Interval = 500 };
            t.Tick += (s, e) =>
            {
                bool taskbar = false;
                try { taskbar = Native.NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero; } catch { }
                if (taskbar || DateTime.UtcNow >= deadline)
                {
                    t.Stop(); t.Dispose();
                    if (!taskbar) Log.Warn("작업 표시줄을 찾지 못했지만 트레이 아이콘을 표시합니다");
                    try { _tray.Visible = true; } catch (Exception ex) { Log.Error("트레이 아이콘 표시 실패", ex); }
                    if (!string.IsNullOrEmpty(_pendingNote)) { Balloon("NetSwitch", _pendingNote, ToolTipIcon.Info); _pendingNote = null; }
                }
            };
            t.Start();
        }

        private void StartStartup()
        {
            if (_ready || _startupRunning) return;
            _startupRunning = true;
            _animTimer.Start();
            Task.Run(() => Startup());
        }

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
                    _startupRunning = false;
                    _animTimer.Stop();
                    _tray.Icon = _icons.Get(TrayIconKind.Error);
                    _tray.Text = Trunc("NetSwitch: 오류 (15초 후 다시 시도)");
                    _miStatus.Text = "오류: " + ex.Message;
                    _miRestore.Enabled = _switcher.Discovered;
                    Balloon("NetSwitch 시작 실패", ex.Message + "\n15초마다 다시 시도합니다.", ToolTipIcon.Error);
                    _retryTimer.Start();
                });
                return;
            }

            Post(() =>
            {
                _startupRunning = false;
                _ready = true;
                _retryTimer.Stop();
                _animTimer.Stop();
                _miSwitch.Enabled = true;
                _miRestore.Enabled = true;
                _miAutostart.Enabled = true;
                if (!string.IsNullOrEmpty(note)) Balloon("NetSwitch", note, ToolTipIcon.Info);
                _watchdogTimer.Start();
                RunWatchdog();
            });
        }

        // ------------------------------------------------------------------ switching

        private void Toggle()
        {
            if (!_ready)
            {
                Balloon("NetSwitch", _startupRunning ? "아직 준비 중입니다. 잠시 후 다시 시도하세요." : "어댑터를 아직 찾지 못했습니다. 다시 시도합니다.", ToolTipIcon.Warning);
                StartStartup();
                return;
            }
            if (_busy) { Balloon("NetSwitch", "이미 전환 중입니다.", ToolTipIcon.Info); return; }
            if (!_switcher.TryBeginSwitch()) { Balloon("NetSwitch", "이미 전환 중입니다.", ToolTipIcon.Info); return; }

            var target = _switcher.NextTarget(_lastObs);
            Log.Info("사용자 요청: " + NetworkSwitcher.Describe(target) + "으로 전환");
            SetBusy(true);
            _progressText = "전환 시작…";
            _animTimer.Start();
            UpdateTooltip();
            Balloon("전환 중", NetworkSwitcher.Describe(target) + "으로 전환합니다…", ToolTipIcon.Info);

            _worker = Task.Run(() =>
            {
                string warn = null, error = null;
                try { _switcher.SwitchTo(target, _cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (SwitchWarning w) { warn = w.Message; }
                catch (SwitchException ex) { error = ex.Message; }
                catch (Exception ex) { Log.Error("전환 중 예기치 않은 오류", ex); error = "예기치 않은 오류: " + ex.Message; }

                NetObservation o = null;
                try { o = _switcher.Observe(); } catch (Exception ex) { Log.Warn("전환 후 상태 조회 실패: " + ex.Message); }

                Post(() =>
                {
                    _animTimer.Stop();
                    _progressText = "";
                    SetBusy(false);
                    if (o != null) { _lastObs = o; RefreshStatus(o); }
                    if (error != null)
                        Balloon("전환 실패", error, ToolTipIcon.Error);
                    else if (warn != null)
                        Balloon(NetworkSwitcher.Describe(_switcher.CurrentMode) + " (확인 필요)", warn, ToolTipIcon.Warning);
                    else
                        Balloon("전환 완료", NetworkSwitcher.Describe(_switcher.CurrentMode) + "을 사용할 수 있습니다.", ToolTipIcon.Info);
                });
            });
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _miSwitch.Enabled = !busy;
            _miRestore.Enabled = !busy;
            _miExit.Enabled = !busy;
        }

        // ------------------------------------------------------------------ watchdog / status

        private void OnNetworkChanged(object sender, EventArgs e)
        {
            // Raised on a thread-pool thread, possibly many times per change; the busy flag coalesces them.
            Post(() => { if (_ready && !_busy) RunWatchdog(); });
        }

        private void RunWatchdog()
        {
            if (!_ready || _busy || _switcher.IsSwitching) return;
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
                        if (!_busy) RefreshStatus(o);
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
                if (!o.EthernetPresent) { kind = TrayIconKind.LanWarn; status = "업무망(유선): 랜 어댑터가 보이지 않음"; }
                else if (!o.EthernetLink) { kind = TrayIconKind.LanWarn; status = "업무망(유선): 랜선이 연결되지 않음"; }
                else if (!o.EthernetUsable) { kind = TrayIconKind.LanWarn; status = "업무망(유선): IP 주소 대기 중"; }
                else { kind = TrayIconKind.Lan; status = "업무망(유선 랜) 사용 중 · " + o.EthernetIp.Address; }
                _miSwitch.Text = "인터넷(와이파이)으로 전환";
            }
            else if (mode == NetMode.Wifi)
            {
                if (!o.WifiConnected) { kind = TrayIconKind.WifiWarn; status = o.WifiConnecting ? "인터넷(와이파이): 연결 중" : "인터넷(와이파이): 연결되지 않음"; }
                else if (!o.WifiUsable) { kind = TrayIconKind.WifiWarn; status = "인터넷(와이파이): IP 주소 대기 중"; }
                else { kind = TrayIconKind.Wifi; status = "인터넷(와이파이) 사용 중 · " + o.Wifi.DisplayName; }
                _miSwitch.Text = "업무망(유선 랜)으로 전환";
            }
            else
            {
                kind = TrayIconKind.Neutral;
                status = "복구됨: 와이파이·유선 모두 Windows가 관리";
                _miSwitch.Text = o.WifiConnected ? "업무망(유선 랜)만 사용" : "인터넷(와이파이)만 사용";
            }
            _tray.Icon = _icons.Get(kind);
            _miStatus.Text = status;
            _tray.Text = Trunc("NetSwitch · " + status + "\n더블클릭: 전환");
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
            _miAutostart.Enabled = false;
            Task.Run(() =>
            {
                bool on;
                try { on = Autostart.IsRegistered(); } catch { on = false; }
                Post(() => { _miAutostart.Checked = on; _miAutostart.Enabled = _ready; });
            });
        }

        private void ToggleAutostart()
        {
            bool turnOn = !_miAutostart.Checked;
            _miAutostart.Enabled = false;
            _cfg.Autostart = turnOn;
            _cfg.Save();
            Task.Run(() =>
            {
                string err = null;
                try { if (turnOn) Autostart.Register(); else Autostart.Unregister(); }
                catch (Exception ex) { err = ex.Message; Log.Error("자동 실행 설정 변경 실패", ex); }
                Post(() =>
                {
                    if (err != null) Balloon("자동 실행 설정 실패", err, ToolTipIcon.Error);
                    else Balloon("NetSwitch", turnOn ? "Windows 시작 시 자동으로 실행됩니다." : "자동 실행을 해제했습니다. 직접 실행할 때는 UAC 창이 뜹니다.", ToolTipIcon.Info);
                    RefreshAutostartCheck();
                });
            });
        }

        private void RestoreDefaults()
        {
            if (_busy) return;
            if (!_switcher.Discovered) { Balloon("NetSwitch", "아직 어댑터를 찾지 못해 복구할 수 없습니다.", ToolTipIcon.Warning); return; }
            var r = Msg("와이파이와 유선 랜을 모두 켜고 Windows 기본 상태로 되돌립니다.\n두 망이 동시에 연결될 수 있습니다.\n\n계속할까요?",
                "원래대로 복구", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;
            if (_busy) return;   // a switch may have started while the dialog was open
            SetBusy(true);
            _animTimer.Start();
            _worker = Task.Run(() =>
            {
                string err = null;
                try { _switcher.RestoreDefaults(_cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { err = ex.Message; Log.Error("복구 실패", ex); }
                NetObservation o = null;
                try { o = _switcher.Observe(); } catch { }
                Post(() =>
                {
                    _animTimer.Stop();
                    SetBusy(false);
                    if (o != null) { _lastObs = o; RefreshStatus(o); }
                    if (err != null) Balloon("복구 실패", err, ToolTipIcon.Error);
                    else Balloon("복구 완료", "와이파이와 유선 랜이 모두 켜졌습니다. 더블클릭하면 다시 한쪽만 사용합니다.", ToolTipIcon.Info);
                });
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
            string eth = o == null ? "-" : !o.EthernetPresent ? "어댑터 없음" : (o.EthernetEnabled ? "장치 켜짐" : "장치 꺼짐") + ", " + (o.EthernetLink ? "링크 있음" : "링크 없음") + ", " + (o.EthernetEnabled && !o.EthernetIpBound ? "IP 뗌(사용 안 함)" : o.EthernetIp.ToKorean());
            string wifi = o == null ? "-" : o.Wifi.ToKorean() + ", " + o.WifiIp.ToKorean();
            var text =
                "NetSwitch " + ver.Major + "." + ver.Minor + "." + ver.Build + "\n" +
                "인터넷(와이파이) ↔ 업무망(유선 랜) 전환기\n\n" +
                "유선 랜 어댑터: " + (_switcher.EthernetName ?? "-") + "\n" +
                "와이파이 어댑터: " + (_switcher.WifiName ?? "-") + "\n" +
                "와이파이 끊는 방식: " + (_cfg.WifiMethod == WifiMethod.Adapter ? "장치 끄기" : "연결 끊기 + 자동구성 끄기") + "\n" +
                "유선 랜 끄는 방식: " + (_cfg.EthernetMethod == EthernetMethod.Adapter ? "장치 끄기" : "장치는 켜 둔 채 IP만 떼기") + "\n" +
                "현재 모드: " + NetworkSwitcher.Describe(_switcher.CurrentMode) + "\n" +
                "유선 상태: " + eth + "\n" +
                "와이파이 상태: " + wifi + "\n" +
                "\n설정·로그 폴더: " + AppPaths.DataDir + "\n" +
                "관리자 권한: " + (Elevation.IsElevated() ? "예" : "아니오");
            Msg(text, "NetSwitch 정보", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
        }

        private void ExitApp()
        {
            if (_busy)
            {
                Balloon("NetSwitch", "전환이 끝난 뒤 종료하세요.", ToolTipIcon.Warning);
                return;
            }
            if (_ready && _switcher.CurrentMode == NetMode.Lan)
            {
                var r = Msg("업무망 모드인 채로 종료하면 와이파이가 꺼진 상태로 남아 Windows의 와이파이 목록이 비어 보입니다.\n" +
                            "와이파이를 다시 쓰려면 NetSwitch를 다시 실행하거나 '원래대로 복구'를 쓰세요.\n\n그래도 종료할까요?",
                            "NetSwitch 종료", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
                if (_busy) { Balloon("NetSwitch", "전환이 끝난 뒤 종료하세요.", ToolTipIcon.Warning); return; }   // a switch may have started while the dialog was open
            }
            Log.Info("사용자 종료");
            _watchdogTimer.Stop();
            _animTimer.Stop();
            _retryTimer.Stop();
            // Let an in-flight watchdog intervention finish so we never exit half-switched; do not cancel it.
            var spin = DateTime.UtcNow.AddSeconds(30);
            while ((Volatile.Read(ref _watchdogBusy) != 0 || _switcher.IsSwitching) && DateTime.UtcNow < spin) Thread.Sleep(100);
            try { _worker?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _cts.Cancel();
            _tray.Visible = false;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { NetworkChange.NetworkAddressChanged -= OnNetworkChanged; } catch { }
                try { _tray.Visible = false; _tray.Dispose(); } catch { }
                try { _menu.Dispose(); } catch { }
                try { _icons.Dispose(); } catch { }
                try { _sync.Dispose(); } catch { }
                try { _watchdogTimer.Dispose(); _animTimer.Dispose(); _retryTimer.Dispose(); } catch { }
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
            try
            {
                if (!_tray.Visible)
                {
                    // Balloons on an invisible icon are discarded; keep the text until the taskbar is there.
                    _pendingNote = string.IsNullOrEmpty(_pendingNote) ? text : _pendingNote + "\n" + text;
                    return;
                }
                _tray.ShowBalloonTip(5000, title, text, icon);
            }
            catch { }
        }

        /// <summary>Message box for a tray app: no owner window, shown centred and on top.</summary>
        private static DialogResult Msg(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
        {
            return MessageBox.Show(text, caption, buttons, icon, def, MessageBoxOptions.DefaultDesktopOnly);
        }

        private static string Trunc(string s)
        {
            // NotifyIcon.Text is limited to 63 characters on .NET Framework.
            if (s == null) return "";
            return s.Length <= 63 ? s : s.Substring(0, 60) + "…";
        }
    }
}
