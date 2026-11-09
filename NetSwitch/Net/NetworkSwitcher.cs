using System;
using System.Collections.Generic;
using System.Threading;

namespace NetSwitch.Net
{
    internal sealed class SwitchException : Exception
    {
        public SwitchException(string message) : base(message) { }
        public SwitchException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Observed state of both adapters, used for the tray icon / tooltip and the watchdog.</summary>
    internal sealed class NetObservation
    {
        public NetAdapterInfo Ethernet;   // may be null if the adapter vanished (dock unplugged)
        public NetAdapterInfo WifiAdapter;
        public WifiState Wifi;
        public IpStatus EthernetIp;
        public IpStatus WifiIp;

        public bool EthernetEnabled => Ethernet != null && Ethernet.IsEnabled;
        public bool EthernetLink => Ethernet != null && Ethernet.HasLink;
        public bool EthernetUsable => EthernetEnabled && EthernetLink && EthernetIp != null && EthernetIp.HasUsableIp;
        public bool WifiConnected => Wifi != null && Wifi.IsConnected;
        public bool WifiUsable => WifiConnected && WifiIp != null && WifiIp.HasUsableIp;
        public bool Overlap => (EthernetEnabled && EthernetLink) && WifiConnected;
    }

    /// <summary>
    /// The one feature of the app: move the laptop between
    ///   Wi-Fi (internet)  and  wired LAN (work network)
    /// without ever having both connected at the same time.
    /// </summary>
    internal sealed class NetworkSwitcher
    {
        private readonly Config _cfg;
        private readonly object _gate = new object();

        public Guid EthernetGuid { get; private set; }
        public Guid WifiGuid { get; private set; }
        public string EthernetName { get; private set; } = "";
        public string WifiName { get; private set; } = "";
        public WifiController Wifi { get; private set; }

        public NetMode CurrentMode { get; private set; } = NetMode.Unknown;
        public bool IsSwitching { get; private set; }

        public event Action<string> Progress;   // Korean status text for the tray

        public NetworkSwitcher(Config cfg) { _cfg = cfg; }

        // =====================================================================
        // Adapter discovery
        // =====================================================================

        public void DiscoverAdapters()
        {
            var all = NetAdapterInfo.QueryAll();
            Log.Info("어댑터 " + all.Count + "개 발견");
            foreach (var a in all) Log.Info("  " + a);

            var eth = PickAdapter(all, _cfg.EthernetAdapter, _cfg.EthernetGuid, a => a.IsPhysicalEthernetCandidate, "유선 랜");
            var wifi = PickAdapter(all, _cfg.WifiAdapter, _cfg.WifiGuid, a => a.IsPhysicalWifiCandidate, "와이파이");

            if (wifi == null)
            {
                // Fall back to what the WLAN service itself exposes.
                try
                {
                    foreach (var w in WifiController.EnumerateInterfaces())
                    {
                        wifi = all.Find(a => a.InterfaceGuid == w.Guid) ?? new NetAdapterInfo { InterfaceGuid = w.Guid, Name = "Wi-Fi", Description = w.Description, InterfaceType = 71, AdminStatus = 1, HardwareInterface = true };
                        break;
                    }
                }
                catch (Exception ex) { Log.Warn("WLAN 인터페이스 열거 실패: " + ex.Message); }
            }

            if (eth == null) throw new SwitchException("유선 랜 어댑터를 찾을 수 없습니다. 노트북에 유선 랜 포트(또는 USB 랜 어댑터)가 있는지 확인하세요.");
            if (wifi == null) throw new SwitchException("와이파이 어댑터를 찾을 수 없습니다.");

            EthernetGuid = eth.InterfaceGuid; EthernetName = eth.Name;
            WifiGuid = wifi.InterfaceGuid; WifiName = wifi.Name;
            Wifi = new WifiController(WifiGuid);

            if (!string.Equals(_cfg.EthernetGuid, eth.GuidString, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_cfg.WifiGuid, wifi.GuidString, StringComparison.OrdinalIgnoreCase))
            {
                _cfg.EthernetGuid = eth.GuidString;
                _cfg.WifiGuid = wifi.GuidString;
                _cfg.Save();
            }
            Log.Info("선택: 유선=" + eth + " / 와이파이=" + wifi);
        }

        private static NetAdapterInfo PickAdapter(List<NetAdapterInfo> all, string pin, string rememberedGuid, Func<NetAdapterInfo, bool> candidate, string label)
        {
            // 1. explicit pin (name or GUID) from config
            if (!string.IsNullOrEmpty(pin))
            {
                var pinned = all.Find(a => string.Equals(a.Name, pin, StringComparison.OrdinalIgnoreCase) || string.Equals(a.GuidString, NormalizeGuid(pin), StringComparison.OrdinalIgnoreCase));
                if (pinned != null) { Log.Info(label + ": 설정에서 지정한 어댑터 사용 " + pinned.Name); return pinned; }
                Log.Warn(label + ": 설정에 지정된 어댑터 '" + pin + "'를 찾지 못해 자동 선택합니다");
            }
            // 2. the adapter we used last time (survives disable, which hides type info on some systems)
            if (!string.IsNullOrEmpty(rememberedGuid))
            {
                var g = NormalizeGuid(rememberedGuid);
                var prev = all.Find(a => string.Equals(a.GuidString, g, StringComparison.OrdinalIgnoreCase));
                if (prev != null) return prev;
            }
            // 3. classify: prefer connected, then enabled, then with connector present
            var cands = all.FindAll(a => candidate(a));
            if (cands.Count == 0) return null;
            cands.Sort((x, y) =>
            {
                int sx = Score(x), sy = Score(y);
                if (sx != sy) return sy.CompareTo(sx);
                return x.InterfaceIndex.CompareTo(y.InterfaceIndex);
            });
            if (cands.Count > 1) Log.Warn(label + ": 후보가 " + cands.Count + "개입니다. '" + cands[0].Name + "'을 선택했습니다. 다른 것을 쓰려면 config.txt에서 지정하세요.");
            return cands[0];
        }

        private static int Score(NetAdapterInfo a)
        {
            int s = 0;
            if (a.HasLink) s += 8;
            if (a.IsEnabled) s += 4;
            if (a.ConnectorPresent) s += 2;
            if (a.PnpDeviceId != null && a.PnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) s += 1;
            return s;
        }

        private static string NormalizeGuid(string s)
        {
            return Guid.TryParse(s, out var g) ? g.ToString("B").ToUpperInvariant() : s;
        }

        // =====================================================================
        // Observation
        // =====================================================================

        public NetObservation Observe()
        {
            var o = new NetObservation();
            try { o.Ethernet = NetAdapterInfo.QueryByGuid(EthernetGuid); } catch (Exception ex) { Log.Warn("유선 상태 조회 실패: " + ex.Message); }
            try { o.WifiAdapter = NetAdapterInfo.QueryByGuid(WifiGuid); } catch (Exception ex) { Log.Warn("와이파이 어댑터 조회 실패: " + ex.Message); }
            try { o.Wifi = Wifi.Query(); } catch (Exception ex) { Log.Warn("와이파이 상태 조회 실패: " + ex.Message); o.Wifi = new WifiState(); }
            o.EthernetIp = IpProbe.Query(EthernetGuid);
            o.WifiIp = IpProbe.Query(WifiGuid);
            return o;
        }

        /// <summary>Decides which mode the machine is "in" right now, used only when nothing was saved.</summary>
        public static NetMode InferMode(NetObservation o)
        {
            bool lan = o.EthernetEnabled && o.EthernetLink;
            bool wifi = o.WifiConnected;
            if (lan && !wifi) return NetMode.Lan;
            if (wifi && !lan) return NetMode.Wifi;
            if (wifi && lan) return NetMode.Wifi;   // both: keep the Wi-Fi the user is on, turn wired off
            if (o.EthernetEnabled && o.EthernetLink) return NetMode.Lan;
            return NetMode.Wifi;
        }

        // =====================================================================
        // Startup enforcement
        // =====================================================================

        /// <summary>
        /// Called once at start. Restores the saved mode (or infers one) and makes the
        /// physical state agree with it, so the two networks never overlap even after
        /// a reboot or a crash.
        /// </summary>
        public NetMode EnforceStartupState(CancellationToken ct, out string note)
        {
            note = null;
            var o = Observe();
            var mode = _cfg.LastMode;
            if (mode == NetMode.Unknown)
            {
                mode = InferMode(o);
                Log.Info("저장된 모드 없음. 현재 상태로 추정: " + mode);
                if (o.Overlap) note = "와이파이와 유선 랜이 동시에 연결되어 있어 유선 랜을 껐습니다. 업무망은 트레이 아이콘을 더블클릭해 전환하세요.";
            }
            CurrentMode = mode;

            try
            {
                if (mode == NetMode.Lan)
                {
                    if (o.WifiConnected || o.Wifi.IsConnecting)
                    {
                        Log.Info("시작: 업무망 모드인데 와이파이가 연결됨 -> 끊음");
                        CutWifi(ct);
                        note = note ?? "업무망 모드를 유지하기 위해 와이파이를 끊었습니다.";
                    }
                    else if (_cfg.WifiMethod == WifiMethod.Autoconfig && Wifi.QueryAutoconfigEnabled() == true && o.Wifi.InterfacePresent)
                    {
                        // Wi-Fi is not connected but Windows could reconnect any moment.
                        Wifi.SetAutoconfig(false);
                    }
                    if (o.Ethernet != null && !o.EthernetEnabled)
                    {
                        Log.Info("시작: 업무망 모드인데 유선 랜이 꺼져 있음 -> 켬");
                        AdapterControl.Enable(EthernetGuid);
                    }
                }
                else
                {
                    if (o.EthernetEnabled)
                    {
                        Log.Info("시작: 인터넷 모드인데 유선 랜이 켜져 있음 -> 끔");
                        AdapterControl.Disable(EthernetGuid);
                        AdapterControl.WaitFor(EthernetGuid, a => a.IsDisabled, TimeSpan.FromSeconds(10), ct);
                    }
                    if (!o.WifiConnected)
                    {
                        Log.Info("시작: 인터넷 모드인데 와이파이가 안 붙어 있음 -> 연결 시도");
                        BringUpWifi(ct, waitForIp: false);
                    }
                    else if (_cfg.WifiMethod == WifiMethod.Autoconfig && Wifi.QueryAutoconfigEnabled() == false)
                    {
                        Wifi.SetAutoconfig(true);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error("시작 상태 정리 중 오류", ex);
                note = "시작 상태 정리 중 오류: " + ex.Message;
            }

            if (_cfg.LastMode != mode) { _cfg.LastMode = mode; _cfg.Save(); }
            return mode;
        }

        // =====================================================================
        // Switching
        // =====================================================================

        public NetMode OppositeOf(NetMode m) => m == NetMode.Lan ? NetMode.Wifi : NetMode.Lan;

        /// <summary>Switch to <paramref name="target"/>. Blocking; call from a worker thread. Throws SwitchException on failure.</summary>
        public void SwitchTo(NetMode target, CancellationToken ct)
        {
            lock (_gate)
            {
                if (IsSwitching) throw new SwitchException("이미 전환 중입니다.");
                IsSwitching = true;
            }
            try
            {
                Log.Info("===== 전환 시작: " + Describe(target) + " =====");
                var started = DateTime.UtcNow;
                try
                {
                    if (target == NetMode.Lan) SwitchToLan(ct); else SwitchToWifi(ct);
                }
                catch (SwitchWarning w)
                {
                    // The mode did change; only the final confirmation (IP / Wi-Fi association) is missing.
                    Commit(target);
                    Log.Warn("===== 전환 완료(경고): " + w.Message + " =====");
                    throw;
                }
                Commit(target);
                Log.Info("===== 전환 완료: " + Describe(target) + " (" + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0") + "초) =====");
            }
            finally
            {
                lock (_gate) IsSwitching = false;
            }
        }

        private void Commit(NetMode target)
        {
            CurrentMode = target;
            _cfg.LastMode = target;
            _cfg.Save();
            if (_cfg.FlushDns) IpProbe.FlushDns();
        }

        private void SwitchToLan(CancellationToken ct)
        {
            // 1) Wi-Fi off first. Never touch the wire while Wi-Fi is still up.
            Report("와이파이 끊는 중…");
            var before = Wifi.Query();
            if (before.IsConnected && !string.IsNullOrEmpty(before.ProfileName))
            {
                _cfg.LastWifiProfile = before.ProfileName;
                _cfg.Save();
            }
            CutWifi(ct);

            var check = Wifi.Query();
            if (check.IsConnected)
                throw new SwitchException("와이파이가 끊어지지 않아 전환을 중단했습니다. (유선 랜은 건드리지 않았습니다)");

            // 2) Wire on.
            Report("유선 랜 켜는 중…");
            var eth = NetAdapterInfo.QueryByGuid(EthernetGuid);
            if (eth == null) { RestoreWifiAfterFailure(ct); throw new SwitchException("유선 랜 어댑터가 보이지 않습니다. 독/어댑터가 연결되어 있는지 확인하세요. 와이파이로 되돌렸습니다."); }
            if (!eth.IsEnabled)
            {
                AdapterControl.Enable(EthernetGuid);
                eth = AdapterControl.WaitFor(EthernetGuid, a => a.IsEnabled, TimeSpan.FromSeconds(10), ct);
                if (eth == null || !eth.IsEnabled) { RestoreWifiAfterFailure(ct); throw new SwitchException("유선 랜 어댑터를 켜지 못했습니다. 와이파이로 되돌렸습니다."); }
            }

            // 3) Cable actually plugged in?
            Report("랜선 연결 확인 중…");
            eth = AdapterControl.WaitFor(EthernetGuid, a => a.HasLink, TimeSpan.FromSeconds(_cfg.LinkWaitSeconds), ct);
            if (eth == null || !eth.HasLink)
            {
                Log.Warn("링크 없음 -> 롤백");
                Report("랜선이 없어 와이파이로 되돌리는 중…");
                try { AdapterControl.Disable(EthernetGuid); AdapterControl.WaitFor(EthernetGuid, a => a.IsDisabled, TimeSpan.FromSeconds(10), ct); }
                catch (Exception ex) { Log.Warn("롤백 중 유선 끄기 실패: " + ex.Message); }
                RestoreWifiAfterFailure(ct);
                throw new SwitchException("랜선 연결이 감지되지 않습니다. 랜선을 확인하세요. 와이파이로 되돌렸습니다.");
            }

            // 4) Wait for a real address (DHCP / 802.1X).
            Report("업무망 IP 주소 받는 중…");
            var ip = IpProbe.WaitForUsableIp(EthernetGuid, TimeSpan.FromSeconds(_cfg.IpWaitSeconds), ct);
            Log.Info("유선 IP 상태: " + ip);
            if (!ip.HasUsableIp)
            {
                // Stay in LAN mode (the user asked for it) but tell them.
                throw new SwitchWarning("업무망으로 전환했지만 아직 IP 주소를 받지 못했습니다. 잠시 후 자동으로 연결될 수 있습니다.");
            }
        }

        private void SwitchToWifi(CancellationToken ct)
        {
            // 1) Wire off first.
            Report("유선 랜 끄는 중…");
            var eth = NetAdapterInfo.QueryByGuid(EthernetGuid);
            if (eth != null && !eth.IsDisabled)
            {
                AdapterControl.Disable(EthernetGuid);
                eth = AdapterControl.WaitFor(EthernetGuid, a => a.IsDisabled, TimeSpan.FromSeconds(10), ct);
                if (eth != null && !eth.IsDisabled)
                    throw new SwitchException("유선 랜을 끄지 못해 전환을 중단했습니다. (와이파이는 건드리지 않았습니다)");
            }

            // 2) Wi-Fi on + connect.
            Report("와이파이 연결 중…");
            BringUpWifi(ct, waitForIp: true);
        }

        // ---------------------------------------------------------------------

        private void CutWifi(CancellationToken ct)
        {
            if (_cfg.WifiMethod == WifiMethod.Adapter)
            {
                var wa = NetAdapterInfo.QueryByGuid(WifiGuid);
                if (wa != null && !wa.IsDisabled)
                {
                    AdapterControl.Disable(WifiGuid);
                    AdapterControl.WaitFor(WifiGuid, a => a.IsDisabled, TimeSpan.FromSeconds(10), ct);
                }
                return;
            }

            // autoconfig method: first stop Windows from auto-connecting (this alone does NOT drop
            // the current association), then disconnect explicitly, then verify.
            bool autoconfOff = false;
            try { Wifi.SetAutoconfig(false); autoconfOff = true; }
            catch (Exception ex) { Log.Warn("autoconfig 끄기 실패: " + ex.Message); }
            try { Wifi.Disconnect(); } catch (Exception ex) { Log.Warn("WlanDisconnect 실패: " + ex.Message); }
            var st = Wifi.WaitFor(s => !s.IsConnected && !s.IsConnecting, TimeSpan.FromSeconds(8), ct);
            Log.Info("와이파이 끊은 뒤 상태: " + st);
            if (st.IsConnected || !autoconfOff)
            {
                // Either the disconnect did not take, or Windows is still allowed to auto-reconnect
                // (e.g. group policy denies the autoconf setting). Fall back to taking the adapter down:
                // slower to bring back, but it cannot overlap with the wire.
                Log.Warn(st.IsConnected ? "와이파이가 계속 연결되어 있어 어댑터를 끕니다" : "자동 재연결을 막지 못해 와이파이 어댑터를 끕니다");
                AdapterControl.Disable(WifiGuid);
                AdapterControl.WaitFor(WifiGuid, a => a.IsDisabled, TimeSpan.FromSeconds(10), ct);
            }
        }

        /// <summary>Make Wi-Fi available again and connect to the usual network.</summary>
        private void BringUpWifi(CancellationToken ct, bool waitForIp)
        {
            // Adapter may be disabled (adapter method, or last-resort fallback).
            var wa = NetAdapterInfo.QueryByGuid(WifiGuid);
            if (wa != null && wa.IsDisabled)
            {
                Report("와이파이 장치 켜는 중…");
                AdapterControl.Enable(WifiGuid);
                AdapterControl.WaitFor(WifiGuid, a => a.IsEnabled, TimeSpan.FromSeconds(10), ct);
                Wifi.WaitFor(s => s.InterfacePresent && s.State != Native.WlanApi.WLAN_INTERFACE_STATE.NotReady, TimeSpan.FromSeconds(10), ct);
            }

            try
            {
                if (Wifi.QueryAutoconfigEnabled() != true) Wifi.SetAutoconfig(true);
            }
            catch (Exception ex) { Log.Warn("autoconfig 켜기 실패: " + ex.Message); }

            // Give the WLAN service a moment to take the interface back.
            Wifi.WaitFor(s => s.InterfacePresent && s.State != Native.WlanApi.WLAN_INTERFACE_STATE.NotReady, TimeSpan.FromSeconds(5), ct);

            var profile = ChooseProfile();
            WifiState st;
            if (!string.IsNullOrEmpty(profile))
            {
                Report("'" + profile + "' 에 연결 중…");
                uint rc = Wifi.Connect(profile);
                if (rc != 0)
                {
                    // interface may still be waking up; retry once after a short pause
                    Thread.Sleep(1000);
                    rc = Wifi.Connect(profile);
                }
                st = Wifi.WaitFor(s => s.IsConnected, TimeSpan.FromSeconds(_cfg.WifiConnectWaitSeconds), ct);
                if (!st.IsConnected)
                {
                    Log.Warn("프로필 '" + profile + "' 연결 실패 (상태 " + st + "). 윈도우 자동 연결에 맡깁니다.");
                    st = Wifi.WaitFor(s => s.IsConnected, TimeSpan.FromSeconds(8), ct);
                }
            }
            else
            {
                Log.Warn("연결할 와이파이 프로필을 모릅니다. 윈도우 자동 연결에 맡깁니다.");
                st = Wifi.WaitFor(s => s.IsConnected, TimeSpan.FromSeconds(_cfg.WifiConnectWaitSeconds), ct);
            }

            if (!st.IsConnected)
                throw new SwitchWarning("유선 랜은 껐지만 와이파이에 자동으로 연결되지 않았습니다. 작업 표시줄의 와이파이 아이콘에서 직접 연결하세요.");

            Log.Info("와이파이 연결됨: " + st);
            if (waitForIp)
            {
                Report("인터넷 IP 주소 받는 중…");
                var ip = IpProbe.WaitForUsableIp(WifiGuid, TimeSpan.FromSeconds(_cfg.IpWaitSeconds), ct);
                Log.Info("와이파이 IP 상태: " + ip);
                if (!ip.HasUsableIp)
                    throw new SwitchWarning("와이파이에 연결됐지만 아직 IP 주소를 받지 못했습니다.");
            }
        }

        private void RestoreWifiAfterFailure(CancellationToken ct)
        {
            try { BringUpWifi(ct, waitForIp: false); }
            catch (SwitchWarning w) { Log.Warn("롤백 경고: " + w.Message); }
            catch (Exception ex) { Log.Error("와이파이 롤백 실패", ex); }
        }

        private string ChooseProfile()
        {
            if (!string.IsNullOrEmpty(_cfg.WifiProfile)) return _cfg.WifiProfile;
            if (!string.IsNullOrEmpty(_cfg.LastWifiProfile)) return _cfg.LastWifiProfile;
            try
            {
                var names = Wifi.GetProfileNames();   // returned in Windows' preference order
                if (names.Count > 0) return names[0];
            }
            catch (Exception ex) { Log.Warn("프로필 목록 조회 실패: " + ex.Message); }
            return null;
        }

        // =====================================================================
        // Watchdog: keep the physical state consistent with the chosen mode.
        // Returns a Korean message if it had to intervene, else null.
        // =====================================================================

        public string Watchdog(NetObservation o, CancellationToken ct)
        {
            if (IsSwitching || CurrentMode == NetMode.Unknown) return null;
            if (!Monitor.TryEnter(_gate)) return null;
            try
            {
                if (IsSwitching) return null;
                if (CurrentMode == NetMode.Lan && (o.WifiConnected || (o.Wifi != null && o.Wifi.IsConnecting)))
                {
                    Log.Warn("감시: 업무망 모드인데 와이파이가 다시 연결됨(" + o.Wifi + ") -> 끊음");
                    CutWifi(ct);
                    return "업무망 모드인데 와이파이가 다시 연결되어 끊었습니다.";
                }
                if (CurrentMode == NetMode.Wifi && o.EthernetEnabled)
                {
                    Log.Warn("감시: 인터넷 모드인데 유선 랜이 켜져 있음 -> 끔");
                    AdapterControl.Disable(EthernetGuid);
                    return "인터넷 모드인데 유선 랜이 켜져 있어 다시 껐습니다.";
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("감시 중 오류", ex);
                return null;
            }
            finally { Monitor.Exit(_gate); }
        }

        // =====================================================================
        // Restore everything to Windows defaults (both adapters on, autoconfig on).
        // =====================================================================

        public void RestoreDefaults(CancellationToken ct)
        {
            lock (_gate)
            {
                Log.Info("원래대로 복구 요청");
                try { var wa = NetAdapterInfo.QueryByGuid(WifiGuid); if (wa != null && wa.IsDisabled) AdapterControl.Enable(WifiGuid); }
                catch (Exception ex) { Log.Warn("와이파이 어댑터 켜기 실패: " + ex.Message); }
                try { Wifi.WaitFor(s => s.InterfacePresent, TimeSpan.FromSeconds(8), ct); Wifi.SetAutoconfig(true); }
                catch (Exception ex) { Log.Warn("autoconfig 켜기 실패: " + ex.Message); }
                try { var e = NetAdapterInfo.QueryByGuid(EthernetGuid); if (e != null && e.IsDisabled) AdapterControl.Enable(EthernetGuid); }
                catch (Exception ex) { Log.Warn("유선 어댑터 켜기 실패: " + ex.Message); }
                CurrentMode = NetMode.Unknown;
                _cfg.LastMode = NetMode.Unknown;
                _cfg.Save();
            }
        }

        private void Report(string text)
        {
            Log.Info(text);
            Progress?.Invoke(text);
        }

        public static string Describe(NetMode m) => m == NetMode.Lan ? "업무망(유선 랜)" : m == NetMode.Wifi ? "인터넷(와이파이)" : "알 수 없음";
    }

    /// <summary>The switch completed in the sense that the mode changed, but with a caveat the user should see.</summary>
    internal sealed class SwitchWarning : Exception
    {
        public SwitchWarning(string message) : base(message) { }
    }
}
