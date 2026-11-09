using System;
using System.Collections.Generic;
using System.Threading;
using static NetSwitch.Native.WlanApi;

namespace NetSwitch.Net
{
    internal sealed class SwitchException : Exception
    {
        public SwitchException(string message) : base(message) { }
        public SwitchException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>The switch completed in the sense that the mode changed, but with a caveat the user should see.</summary>
    internal sealed class SwitchWarning : Exception
    {
        public SwitchWarning(string message) : base(message) { }
    }

    /// <summary>Observed state of both adapters, used for the tray icon / tooltip and the watchdog.</summary>
    internal sealed class NetObservation
    {
        public NetAdapterInfo Ethernet;   // null if the adapter is not present (dock unplugged)
        public NetAdapterInfo WifiAdapter;
        public WifiState Wifi;
        public IpStatus EthernetIp;
        public IpStatus WifiIp;

        public bool EthernetPresent => Ethernet != null;
        public bool EthernetEnabled => Ethernet != null && Ethernet.IsEnabled;
        public bool EthernetLink => Ethernet != null && Ethernet.HasLink;
        public bool EthernetUsable => EthernetEnabled && EthernetLink && EthernetIp != null && EthernetIp.HasUsableIp;
        public bool WifiConnected => Wifi != null && Wifi.IsConnected;
        public bool WifiConnecting => Wifi != null && Wifi.IsConnecting;
        public bool WifiUsable => WifiConnected && WifiIp != null && WifiIp.HasUsableIp;
        public bool Overlap => EthernetEnabled && EthernetLink && WifiConnected;
    }

    /// <summary>
    /// The one feature of the app: move the laptop between
    ///   Wi-Fi (internet)  and  wired LAN (work network)
    /// without ever having both connected at the same time.
    ///
    /// Thread model: SwitchTo / RestoreDefaults hold <see cref="_gate"/> for their whole duration.
    /// Watchdog uses TryEnter and therefore never runs concurrently with them.
    /// </summary>
    internal sealed class NetworkSwitcher
    {
        private readonly Config _cfg;
        private readonly object _gate = new object();
        private volatile bool _switching;

        public Guid EthernetGuid { get; private set; }
        public Guid WifiGuid { get; private set; }
        public string EthernetName { get; private set; } = "";
        public string WifiName { get; private set; } = "";
        public WifiController Wifi { get; private set; }
        public bool Discovered => Wifi != null;

        public NetMode CurrentMode { get; private set; } = NetMode.Unknown;
        public bool IsSwitching => _switching;

        public event Action<string> Progress;   // Korean status text for the tray

        public NetworkSwitcher(Config cfg) { _cfg = cfg; }

        // =====================================================================
        // Adapter discovery
        // =====================================================================

        /// <summary>
        /// Finds the Wi-Fi and wired adapters. Wi-Fi is mandatory. A missing wired adapter
        /// (USB dock / dongle unplugged) is NOT fatal: we remember its GUID from the config and
        /// keep looking for it in the watchdog.
        /// </summary>
        public void DiscoverAdapters()
        {
            var all = NetAdapterInfo.QueryAll();
            Log.Info("어댑터 " + all.Count + "개 발견");
            foreach (var a in all) Log.Info("  " + a);

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
            if (wifi == null) throw new SwitchException("와이파이 어댑터를 찾을 수 없습니다.");

            WifiGuid = wifi.InterfaceGuid; WifiName = wifi.Name;
            Wifi = new WifiController(WifiGuid);

            var eth = PickAdapter(all, _cfg.EthernetAdapter, _cfg.EthernetGuid, a => a.IsPhysicalEthernetCandidate, "유선 랜");
            if (eth != null)
            {
                EthernetGuid = eth.InterfaceGuid; EthernetName = eth.Name;
            }
            else if (Guid.TryParse(_cfg.EthernetGuid, out var remembered) && remembered != Guid.Empty)
            {
                EthernetGuid = remembered; EthernetName = "(연결되지 않음)";
                Log.Warn("유선 랜 어댑터가 지금은 보이지 않습니다(독/USB 랜 분리?). 기억한 GUID " + remembered.ToString("B") + " 로 계속 감시합니다.");
            }
            else
            {
                EthernetGuid = Guid.Empty; EthernetName = "(없음)";
                Log.Warn("유선 랜 어댑터를 찾지 못했습니다. 연결되면 자동으로 인식합니다.");
            }

            PersistAdapterGuids(eth, wifi);
            Log.Info("선택: 유선=" + (eth?.ToString() ?? EthernetName) + " / 와이파이=" + wifi);
        }

        private void PersistAdapterGuids(NetAdapterInfo eth, NetAdapterInfo wifi)
        {
            bool changed = false;
            if (eth != null && !string.Equals(_cfg.EthernetGuid, eth.GuidString, StringComparison.OrdinalIgnoreCase)) { _cfg.EthernetGuid = eth.GuidString; changed = true; }
            if (wifi != null && !string.Equals(_cfg.WifiGuid, wifi.GuidString, StringComparison.OrdinalIgnoreCase)) { _cfg.WifiGuid = wifi.GuidString; changed = true; }
            if (changed) _cfg.Save();
        }

        /// <summary>Called from Observe when the wired adapter is absent: adopt a (re)attached one.</summary>
        private NetAdapterInfo TryAdoptEthernet(List<NetAdapterInfo> all)
        {
            var eth = PickAdapter(all, _cfg.EthernetAdapter, _cfg.EthernetGuid, a => a.IsPhysicalEthernetCandidate, "유선 랜", quiet: true);
            if (eth == null) return null;
            if (eth.InterfaceGuid != EthernetGuid)
            {
                Log.Info("유선 랜 어댑터 인식: " + eth);
                EthernetGuid = eth.InterfaceGuid;
                PersistAdapterGuids(eth, null);
            }
            EthernetName = eth.Name;
            return eth;
        }

        private static NetAdapterInfo PickAdapter(List<NetAdapterInfo> all, string pin, string rememberedGuid, Func<NetAdapterInfo, bool> candidate, string label, bool quiet = false)
        {
            // 1. explicit pin (name or GUID) from config
            if (!string.IsNullOrEmpty(pin))
            {
                var pinned = all.Find(a => string.Equals(a.Name, pin, StringComparison.OrdinalIgnoreCase) || string.Equals(a.GuidString, NormalizeGuid(pin), StringComparison.OrdinalIgnoreCase));
                if (pinned != null) { if (!quiet) Log.Info(label + ": 설정에서 지정한 어댑터 사용 " + pinned.Name); return pinned; }
                if (!quiet) Log.Warn(label + ": 설정에 지정된 어댑터 '" + pin + "'를 찾지 못해 자동 선택합니다");
            }
            // 2. the adapter we used last time
            if (!string.IsNullOrEmpty(rememberedGuid))
            {
                var g = NormalizeGuid(rememberedGuid);
                var prev = all.Find(a => string.Equals(a.GuidString, g, StringComparison.OrdinalIgnoreCase));
                if (prev != null) return prev;
            }
            // 3. classify: prefer connected, then enabled, then built-in (PCI) over USB
            var cands = all.FindAll(a => candidate(a));
            if (cands.Count == 0) return null;
            cands.Sort((x, y) =>
            {
                int sx = Score(x), sy = Score(y);
                if (sx != sy) return sy.CompareTo(sx);
                return x.InterfaceIndex.CompareTo(y.InterfaceIndex);
            });
            if (cands.Count > 1 && !quiet) Log.Warn(label + ": 후보가 " + cands.Count + "개입니다. '" + cands[0].Name + "'을 선택했습니다. 다른 것을 쓰려면 config.txt에서 지정하세요.");
            return cands[0];
        }

        private static int Score(NetAdapterInfo a)
        {
            int s = 0;
            if (a.HasLink) s += 8;
            if (a.IsEnabled) s += 4;
            if (a.PnpDeviceId != null && a.PnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) s += 3;
            if (a.ConnectorPresent) s += 2;
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
            List<NetAdapterInfo> all = null;
            try { all = NetAdapterInfo.QueryAll(); }
            catch (Exception ex) { Log.Warn("어댑터 상태 조회 실패: " + ex.Message); }

            if (all != null)
            {
                o.Ethernet = EthernetGuid == Guid.Empty ? null : all.Find(a => a.InterfaceGuid == EthernetGuid);
                if (o.Ethernet == null) o.Ethernet = TryAdoptEthernet(all);
                o.WifiAdapter = all.Find(a => a.InterfaceGuid == WifiGuid);
            }
            try { o.Wifi = Wifi.Query(); } catch (Exception ex) { Log.Warn("와이파이 상태 조회 실패: " + ex.Message); o.Wifi = new WifiState(); }

            var nics = IpProbe.Snapshot();
            o.EthernetIp = EthernetGuid == Guid.Empty ? new IpStatus() : IpProbe.Query(nics, EthernetGuid);
            o.WifiIp = IpProbe.Query(nics, WifiGuid);
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
            return NetMode.Wifi;
        }

        // =====================================================================
        // Startup enforcement
        // =====================================================================

        /// <summary>
        /// Called once at start. Restores the saved mode (or infers one) and makes the physical
        /// state agree with it, so the two networks never overlap even after a reboot or a crash.
        /// </summary>
        public NetMode EnforceStartupState(CancellationToken ct, out string note)
        {
            note = null;
            lock (_gate)
            {
                var o = Observe();
                Log.Info("시작 상태: 유선=" + (o.Ethernet == null ? "없음" : (o.EthernetEnabled ? "켜짐" : "꺼짐") + (o.EthernetLink ? "/링크" : "/링크없음")) + " 와이파이=" + o.Wifi + " autoconf=" + (Wifi.QueryAutoconfigEnabled()?.ToString() ?? "?"));

                if (_cfg.Restored)
                {
                    // The user explicitly put Windows back in charge. Do not touch anything until they switch again.
                    CurrentMode = NetMode.Unknown;
                    Log.Info("복구 상태 유지(사용자가 '원래대로 복구'를 선택함). 강제하지 않음.");
                    return CurrentMode;
                }

                var mode = _cfg.LastMode;
                if (mode == NetMode.Unknown)
                {
                    mode = InferMode(o);
                    Log.Info("저장된 모드 없음. 현재 상태로 추정: " + mode);
                    if (o.Overlap) note = "와이파이와 유선 랜이 동시에 연결되어 있어 유선 랜을 껐습니다. 업무망은 트레이 아이콘을 더블클릭해 전환하세요.";
                }

                try
                {
                    if (mode == NetMode.Lan)
                    {
                        if (o.Ethernet == null && !o.WifiConnected)
                        {
                            // Booted without the dock in LAN mode: there is no wire to use, so go back to Wi-Fi.
                            Log.Warn("시작: 업무망 모드지만 유선 랜 어댑터가 없음 -> 인터넷(와이파이) 모드로 전환");
                            mode = NetMode.Wifi;
                            BringUpWifi(ct, waitForIp: false, connectWait: TimeSpan.FromSeconds(12));
                            note = "유선 랜 어댑터가 없어 인터넷(와이파이) 모드로 시작했습니다.";
                        }
                        else
                        {
                            if (o.WifiConnected || o.WifiConnecting)
                            {
                                Log.Info("시작: 업무망 모드인데 와이파이가 연결됨 -> 끊음 (자동구성 설정이 재부팅 후 유지되지 않았을 수 있음)");
                                CutWifi(ct);
                                note = note ?? "업무망 모드를 유지하기 위해 와이파이를 끊었습니다.";
                            }
                            else if (_cfg.WifiMethod == WifiMethod.Autoconfig && o.Wifi.InterfacePresent && Wifi.QueryAutoconfigEnabled() == true)
                            {
                                Wifi.SetAutoconfig(false);   // not connected yet, but Windows could reconnect any moment
                            }
                            if (o.Ethernet != null && !o.EthernetEnabled)
                            {
                                Log.Info("시작: 업무망 모드인데 유선 랜이 꺼져 있음 -> 켬");
                                AdapterControl.Enable(EthernetGuid);
                            }
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
                        if (o.WifiConnected)
                        {
                            if (_cfg.WifiMethod == WifiMethod.Autoconfig && Wifi.QueryAutoconfigEnabled() == false) Wifi.SetAutoconfig(true);
                        }
                        else if (o.WifiConnecting && Wifi.QueryAutoconfigEnabled() != false)
                        {
                            // Windows is already associating; do not interrupt it.
                            Log.Info("시작: 와이파이가 연결 중 -> 기다림");
                            Wifi.WaitFor(s => s.IsConnected || !s.IsConnecting, TimeSpan.FromSeconds(15), ct);
                        }
                        else
                        {
                            Log.Info("시작: 인터넷 모드인데 와이파이가 안 붙어 있음 -> 연결 시도");
                            BringUpWifi(ct, waitForIp: false, connectWait: TimeSpan.FromSeconds(12));
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (SwitchWarning w)
                {
                    Log.Warn("시작 상태 정리 경고: " + w.Message);
                    note = w.Message;
                }
                catch (Exception ex)
                {
                    Log.Error("시작 상태 정리 중 오류", ex);
                    note = "시작 상태 정리 중 오류: " + ex.Message;
                }

                CurrentMode = mode;
                if (_cfg.LastMode != mode) { _cfg.LastMode = mode; _cfg.Save(); }
                return mode;
            }
        }

        // =====================================================================
        // Switching
        // =====================================================================

        /// <summary>Which mode a toggle should go to. With an unknown mode (after 복구) decide from what is connected now.</summary>
        public NetMode NextTarget(NetObservation o)
        {
            if (CurrentMode == NetMode.Lan) return NetMode.Wifi;
            if (CurrentMode == NetMode.Wifi) return NetMode.Lan;
            if (o != null && o.WifiConnected) return NetMode.Lan;
            return NetMode.Wifi;
        }

        /// <summary>Marks the switcher busy. Returns false if a switch/restore is already running.</summary>
        public bool TryBeginSwitch()
        {
            lock (_gate)
            {
                if (_switching) return false;
                _switching = true;
                return true;
            }
        }

        /// <summary>Switch to <paramref name="target"/>. Blocking; call from a worker thread after TryBeginSwitch(). Throws SwitchException on failure, SwitchWarning on success-with-caveat.</summary>
        public void SwitchTo(NetMode target, CancellationToken ct)
        {
            if (!_switching) throw new InvalidOperationException("TryBeginSwitch() 먼저");
            Monitor.Enter(_gate);
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
                _switching = false;
                Monitor.Exit(_gate);
            }
        }

        private void Commit(NetMode target)
        {
            CurrentMode = target;
            _cfg.LastMode = target;
            _cfg.Restored = false;
            _cfg.Save();
            if (_cfg.FlushDns) IpProbe.FlushDns();
        }

        private void SwitchToLan(CancellationToken ct)
        {
            // 0) Is there a wire to switch to at all? Check BEFORE touching Wi-Fi.
            var eth = EthernetGuid == Guid.Empty ? null : NetAdapterInfo.QueryByGuid(EthernetGuid);
            if (eth == null)
            {
                var all = NetAdapterInfo.QueryAll();
                eth = TryAdoptEthernet(all);
            }
            if (eth == null)
                throw new SwitchException("유선 랜 어댑터가 보이지 않습니다. 독이나 USB 랜 어댑터가 연결되어 있는지 확인하세요. (와이파이는 그대로입니다)");

            // 1) Wi-Fi off first. Never touch the wire while Wi-Fi is still up.
            Report("와이파이 끊는 중…");
            var before = Wifi.Query();
            if (before.IsConnected && !string.IsNullOrEmpty(before.ProfileName))
            {
                _cfg.LastWifiProfile = before.ProfileName;
                _cfg.Save();
            }

            bool wireTouched = false;
            try
            {
                CutWifi(ct);
                var check = Wifi.Query();
                if (check.IsConnected)
                    throw new SwitchException("와이파이가 끊어지지 않아 전환을 중단했습니다. (유선 랜은 건드리지 않았습니다)");

                // 2) Wire on.
                Report("유선 랜 켜는 중…");
                if (!eth.IsEnabled)
                {
                    wireTouched = true;
                    AdapterControl.Enable(EthernetGuid);
                    eth = AdapterControl.WaitFor(EthernetGuid, a => a.IsEnabled, TimeSpan.FromSeconds(15), ct);
                    if (eth == null || !eth.IsEnabled)
                    {
                        RollbackToWifi(ct, wireTouched: true);
                        throw new SwitchException("유선 랜 어댑터를 켜지 못했습니다. 와이파이로 되돌렸습니다.");
                    }
                }
                wireTouched = true;

                // 3) Cable actually plugged in?
                Report("랜선 연결 확인 중…");
                eth = AdapterControl.WaitFor(EthernetGuid, a => a.HasLink, TimeSpan.FromSeconds(_cfg.LinkWaitSeconds), ct);
                if (eth == null || !eth.HasLink)
                {
                    Log.Warn("링크 없음 -> 롤백");
                    Report("랜선이 없어 와이파이로 되돌리는 중…");
                    RollbackToWifi(ct, wireTouched: true);
                    throw new SwitchException("랜선 연결이 감지되지 않습니다. 랜선을 확인하세요. 와이파이로 되돌렸습니다.");
                }
            }
            catch (SwitchException) { throw; }
            catch (SwitchWarning) { throw; }
            catch (OperationCanceledException)
            {
                // Exit/shutdown mid-switch: best effort to not leave the machine with nothing.
                Log.Warn("전환이 취소됨 -> 와이파이로 되돌림");
                try { RollbackToWifi(CancellationToken.None, wireTouched); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("업무망 전환 중 예외 -> 롤백", ex);
                try { RollbackToWifi(ct, wireTouched); } catch (Exception rb) { Log.Error("롤백 실패", rb); }
                throw new SwitchException("업무망으로 전환하지 못했습니다(" + ex.Message + "). 와이파이로 되돌렸습니다.", ex);
            }

            // 4) Wait for a real address (DHCP / 802.1X). We are in LAN mode from here on.
            Report("업무망 IP 주소 받는 중…");
            var ip = IpProbe.WaitForUsableIp(EthernetGuid, TimeSpan.FromSeconds(_cfg.IpWaitSeconds), ct);
            Log.Info("유선 IP 상태: " + ip);
            if (!ip.HasUsableIp)
                throw new SwitchWarning("업무망으로 전환했지만 아직 IP 주소를 받지 못했습니다. 잠시 후 자동으로 연결될 수 있습니다.");
        }

        private void SwitchToWifi(CancellationToken ct)
        {
            // 1) Wire off first.
            Report("유선 랜 끄는 중…");
            var eth = EthernetGuid == Guid.Empty ? null : NetAdapterInfo.QueryByGuid(EthernetGuid);
            if (eth != null && !eth.IsDisabled)
            {
                AdapterControl.Disable(EthernetGuid);
                eth = AdapterControl.WaitFor(EthernetGuid, a => a.IsDisabled, TimeSpan.FromSeconds(10), ct);
                if (eth != null && !eth.IsDisabled)
                    throw new SwitchException("유선 랜을 끄지 못해 전환을 중단했습니다. (와이파이는 건드리지 않았습니다)");
            }
            else if (eth == null)
            {
                Log.Warn("유선 랜 어댑터가 없는 상태에서 인터넷 모드로 전환. 어댑터가 다시 나타나면 감시 기능이 즉시 끕니다.");
            }

            // 2) Wi-Fi on + connect. From here the wire is off, so Wi-Fi mode is the only sane outcome.
            Report("와이파이 연결 중…");
            try
            {
                BringUpWifi(ct, waitForIp: true, connectWait: TimeSpan.FromSeconds(_cfg.WifiConnectWaitSeconds));
            }
            catch (SwitchWarning) { throw; }
            catch (OperationCanceledException)
            {
                try { if (Wifi.QueryAutoconfigEnabled() != true) Wifi.SetAutoconfig(true); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("와이파이 복귀 중 예외", ex);
                try { if (Wifi.QueryAutoconfigEnabled() != true) Wifi.SetAutoconfig(true); } catch { }
                throw new SwitchWarning("유선 랜은 껐지만 와이파이를 자동으로 연결하지 못했습니다(" + ex.Message + "). 작업 표시줄의 와이파이 아이콘에서 직접 연결하세요.");
            }
        }

        /// <summary>Undo a half-done switch to LAN: turn the wire back off (if we touched it) and bring Wi-Fi back.</summary>
        private void RollbackToWifi(CancellationToken ct, bool wireTouched)
        {
            if (wireTouched && EthernetGuid != Guid.Empty)
            {
                try
                {
                    AdapterControl.Disable(EthernetGuid);
                    AdapterControl.WaitFor(EthernetGuid, a => a.IsDisabled, TimeSpan.FromSeconds(10), ct);
                }
                catch (Exception ex) { Log.Warn("롤백 중 유선 끄기 실패: " + ex.Message); }
            }
            try { BringUpWifi(ct, waitForIp: false, connectWait: TimeSpan.FromSeconds(_cfg.WifiConnectWaitSeconds)); }
            catch (SwitchWarning w) { Log.Warn("롤백 경고: " + w.Message); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Error("와이파이 롤백 실패", ex); }
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
            var st = Wifi.WaitFor(s => !s.IsConnected && !s.IsTransitioning, TimeSpan.FromSeconds(8), ct);
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
        private void BringUpWifi(CancellationToken ct, bool waitForIp, TimeSpan connectWait)
        {
            // Adapter may be disabled (adapter method, or last-resort fallback).
            var wa = NetAdapterInfo.QueryByGuid(WifiGuid);
            if (wa != null && wa.IsDisabled)
            {
                Report("와이파이 장치 켜는 중…");
                AdapterControl.Enable(WifiGuid);
                AdapterControl.WaitFor(WifiGuid, a => a.IsEnabled, TimeSpan.FromSeconds(10), ct);
            }

            // Wait until the WLAN service knows the interface again, then re-enable auto-config (bounded retry).
            Wifi.WaitFor(s => s.InterfacePresent && s.State != WLAN_INTERFACE_STATE.NotReady, TimeSpan.FromSeconds(15), ct);
            var deadline = DateTime.UtcNow.AddSeconds(8);
            bool autoconfOn = Wifi.QueryAutoconfigEnabled() == true;
            while (!autoconfOn)
            {
                ct.ThrowIfCancellationRequested();
                try { Wifi.SetAutoconfig(true); }
                catch (Exception ex) { Log.Warn("autoconfig 켜기 실패: " + ex.Message); }
                autoconfOn = Wifi.QueryAutoconfigEnabled() == true;
                if (autoconfOn || DateTime.UtcNow >= deadline) break;
                Thread.Sleep(400);
            }
            if (!autoconfOn)
                throw new SwitchWarning("와이파이 자동구성을 다시 켜지 못했습니다. 트레이 메뉴의 '원래대로 복구'를 실행하거나 PC를 다시 시작하세요.");

            // Give Windows' own auto-connect a short head start; it usually picks the right network at once.
            Report("와이파이 연결 중…");
            var st = Wifi.WaitFor(s => s.IsConnected, TimeSpan.FromSeconds(4), ct);

            if (!st.IsConnected)
            {
                var profile = ChooseProfile();
                if (!string.IsNullOrEmpty(profile))
                {
                    Report("'" + profile + "'에 연결 중…");
                    uint rc = Wifi.Connect(profile);
                    if (rc == ERROR_INVALID_STATE || rc == ERROR_NOT_READY)
                    {
                        Thread.Sleep(1000);   // interface still waking up
                        rc = Wifi.Connect(profile);
                    }
                    if (rc == ERROR_NOT_FOUND)
                    {
                        Log.Warn("프로필 '" + profile + "'이(가) 없습니다. 기억한 프로필을 지우고 윈도우 자동 연결에 맡깁니다.");
                        if (string.Equals(profile, _cfg.LastWifiProfile, StringComparison.Ordinal)) { _cfg.LastWifiProfile = ""; _cfg.Save(); }
                        st = Wifi.WaitFor(s => s.IsConnected, TimeSpan.FromSeconds(10), ct);
                    }
                    else
                    {
                        if (rc != 0) Log.Warn("WlanConnect 코드 " + rc + " -> 윈도우 자동 연결과 함께 기다립니다");
                        st = Wifi.WaitFor(s => s.IsConnected, connectWait, ct);
                    }
                }
                else
                {
                    Log.Warn("연결할 와이파이 프로필을 모릅니다. 윈도우 자동 연결에 맡깁니다.");
                    st = Wifi.WaitFor(s => s.IsConnected, connectWait, ct);
                }
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
            if (_switching || CurrentMode == NetMode.Unknown) return null;
            if (!Monitor.TryEnter(_gate)) return null;
            try
            {
                if (_switching) return null;
                if (CurrentMode == NetMode.Lan && (o.WifiConnected || o.WifiConnecting))
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
            catch (OperationCanceledException) { return null; }
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
            if (!Discovered) throw new SwitchException("어댑터 정보가 없어 복구할 수 없습니다.");
            if (!TryBeginSwitch()) throw new SwitchException("전환 중에는 복구할 수 없습니다. 잠시 후 다시 시도하세요.");
            Monitor.Enter(_gate);
            try
            {
                Log.Info("원래대로 복구 요청");
                try { var wa = NetAdapterInfo.QueryByGuid(WifiGuid); if (wa != null && wa.IsDisabled) AdapterControl.Enable(WifiGuid); }
                catch (Exception ex) { Log.Warn("와이파이 어댑터 켜기 실패: " + ex.Message); }
                try { Wifi.WaitFor(s => s.InterfacePresent, TimeSpan.FromSeconds(8), ct); if (Wifi.QueryAutoconfigEnabled() != true) Wifi.SetAutoconfig(true); }
                catch (Exception ex) { Log.Warn("autoconfig 켜기 실패: " + ex.Message); }
                if (EthernetGuid != Guid.Empty)
                {
                    try { var e = NetAdapterInfo.QueryByGuid(EthernetGuid); if (e != null && e.IsDisabled) AdapterControl.Enable(EthernetGuid); }
                    catch (Exception ex) { Log.Warn("유선 어댑터 켜기 실패: " + ex.Message); }
                }
                CurrentMode = NetMode.Unknown;
                _cfg.LastMode = NetMode.Unknown;
                _cfg.Restored = true;
                _cfg.Save();
            }
            finally
            {
                _switching = false;
                Monitor.Exit(_gate);
            }
        }

        private void Report(string text)
        {
            Log.Info(text);
            Progress?.Invoke(text);
        }

        public static string Describe(NetMode m) => m == NetMode.Lan ? "업무망(유선 랜)" : m == NetMode.Wifi ? "인터넷(와이파이)" : "복구됨(양쪽 모두 켜짐)";
    }
}
