using System;
using System.Diagnostics;
using System.Management;

namespace NetSwitch.Net
{
    /// <summary>
    /// Turns the IP stack (IPv4 + IPv6) of one adapter on or off without disabling the device.
    /// Same thing as unticking "인터넷 프로토콜 버전 4/6" in the adapter properties, or
    /// Disable-NetAdapterBinding -ComponentID ms_tcpip,ms_tcpip6.
    ///
    /// Why: PnP-disabling a USB Ethernet adapter leaves the device without a driver; Windows then
    /// suspends the port and some USB NICs/docks drop off the bus entirely until re-plugged.
    /// With only the bindings removed the device stays powered and running (link up), but the
    /// interface has no IP address, no route and no DNS, so it is not a network connection.
    /// Requires elevation.
    /// </summary>
    internal static class BindingControl
    {
        private static readonly string[] IpComponents = { "ms_tcpip", "ms_tcpip6" };

        /// <summary>Binds or unbinds IPv4+IPv6 on the adapter. Tries WMI first, PowerShell second. Throws if both fail.</summary>
        public static void SetIpBound(Guid adapterGuid, string adapterName, bool bound)
        {
            Exception wmiError = null;
            try
            {
                if (SetViaWmi(adapterGuid, adapterName, bound)) return;
                wmiError = new InvalidOperationException("바인딩 항목을 찾지 못했습니다");
            }
            catch (Exception ex) { wmiError = ex; }
            Log.Warn("WMI로 IP 바인딩 " + (bound ? "켜기" : "끄기") + " 실패, PowerShell로 재시도: " + wmiError.Message);

            int rc = SetViaPowerShell(adapterGuid, bound);
            if (rc != 0)
                throw new InvalidOperationException("유선 랜 IP 바인딩 " + (bound ? "켜기" : "끄기") + " 실패 (WMI: " + wmiError.Message + ", PowerShell 코드 " + rc + ")");
        }

        private static bool SetViaWmi(Guid guid, string name, bool bound)
        {
            var scope = new ManagementScope(@"\\.\root\StandardCimv2");
            string g = guid.ToString("B");
            int touched = 0;
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_NetAdapterBindingSettingData")))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        string comp = Str(mo, "ComponentID");
                        if (Array.FindIndex(IpComponents, c => string.Equals(c, comp, StringComparison.OrdinalIgnoreCase)) < 0) continue;

                        // InstanceID looks like "{GUID}::ms_tcpip"; fall back to the connection name.
                        string instanceId = Str(mo, "InstanceID");
                        bool mine = instanceId.IndexOf(g, StringComparison.OrdinalIgnoreCase) >= 0
                                 || (!string.IsNullOrEmpty(name) && string.Equals(Str(mo, "Name"), name, StringComparison.OrdinalIgnoreCase));
                        if (!mine) continue;

                        bool enabled = false;
                        try { enabled = Convert.ToBoolean(mo["Enabled"]); } catch { }
                        if (enabled == bound) { touched++; continue; }

                        var result = mo.InvokeMethod(bound ? "Enable" : "Disable", null, null);
                        uint rc = 0;
                        try { if (result != null && result["ReturnValue"] != null) rc = Convert.ToUInt32(result["ReturnValue"]); } catch { }
                        Log.Info("MSFT_NetAdapterBindingSettingData." + (bound ? "Enable" : "Disable") + "(" + comp + ") -> " + rc);
                        if (rc != 0) throw new InvalidOperationException(comp + " 코드 " + rc);
                        touched++;
                    }
                }
            }
            return touched > 0;
        }

        private static int SetViaPowerShell(Guid guid, bool bound)
        {
            string verb = bound ? "Enable-NetAdapterBinding" : "Disable-NetAdapterBinding";
            string script =
                "$ErrorActionPreference='Stop';" +
                "$a = Get-NetAdapter -IncludeHidden | Where-Object { $_.InterfaceGuid -eq '" + guid.ToString("B") + "' };" +
                "if (-not $a) { exit 3 };" +
                verb + " -Name $a.Name -ComponentID ms_tcpip,ms_tcpip6 -Confirm:$false";
            try
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var psi = new ProcessStartInfo(System.IO.Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return -2; }
                    Log.Info("PowerShell " + verb + " -> " + p.ExitCode);
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Log.Error("PowerShell 실행 실패", ex);
                return -1;
            }
        }

        /// <summary>Asks Windows to re-scan for hardware (same as 장치 관리자 → 하드웨어 변경 사항 검색).</summary>
        public static void RescanDevices()
        {
            try
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var psi = new ProcessStartInfo(System.IO.Path.Combine(system, "pnputil.exe"), "/scan-devices")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return;
                    if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                    else Log.Info("pnputil /scan-devices -> " + p.ExitCode);
                }
            }
            catch (Exception ex) { Log.Warn("장치 다시 검색 실패: " + ex.Message); }
        }

        private static string Str(ManagementBaseObject mo, string prop)
        {
            try { var v = mo[prop]; return v == null ? "" : Convert.ToString(v); } catch { return ""; }
        }
    }
}
