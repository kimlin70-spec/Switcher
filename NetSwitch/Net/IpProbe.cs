using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace NetSwitch.Net
{
    internal sealed class IpStatus
    {
        public bool InterfaceUp;
        public IPAddress Address;      // first usable (non-APIPA) IPv4, or null
        public IPAddress Gateway;      // first IPv4 gateway, or null
        public bool HasUsableIp => Address != null;
        public override string ToString() => (InterfaceUp ? "up" : "down") + " ip=" + (Address?.ToString() ?? "-") + " gw=" + (Gateway?.ToString() ?? "-");
    }

    /// <summary>Reads IPv4 configuration of an interface via System.Net.NetworkInformation.</summary>
    internal static class IpProbe
    {
        public static IpStatus Query(Guid interfaceGuid)
        {
            var st = new IpStatus();
            var target = interfaceGuid.ToString("B");
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception ex) { Log.Warn("NetworkInterface 조회 실패: " + ex.Message); return st; }

            foreach (var ni in all)
            {
                if (!string.Equals(ni.Id, target, StringComparison.OrdinalIgnoreCase)) continue;
                st.InterfaceUp = ni.OperationalStatus == OperationalStatus.Up;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { break; }
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IsApipa(ua.Address) || ua.Address.Equals(IPAddress.Any)) continue;
                    st.Address = ua.Address;
                    break;
                }
                foreach (var gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (gw.Address.Equals(IPAddress.Any)) continue;
                    st.Gateway = gw.Address;
                    break;
                }
                break;
            }
            return st;
        }

        public static bool IsApipa(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return b.Length == 4 && b[0] == 169 && b[1] == 254;
        }

        public static IpStatus WaitForUsableIp(Guid interfaceGuid, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            IpStatus last;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                last = Query(interfaceGuid);
                if (last.HasUsableIp) return last;
                if (DateTime.UtcNow >= deadline) return last;
                Thread.Sleep(300);
            }
        }

        public static void FlushDns()
        {
            try
            {
                if (!Native.NativeMethods.DnsFlushResolverCache())
                    Log.Warn("DNS 캐시 비우기 실패");
            }
            catch (Exception ex)
            {
                Log.Warn("DNS 캐시 비우기 예외: " + ex.Message);
            }
        }
    }
}
