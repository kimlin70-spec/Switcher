using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;

namespace NetSwitch.Net
{
    /// <summary>Snapshot of one adapter from root\StandardCimv2\MSFT_NetAdapter.</summary>
    internal sealed class NetAdapterInfo
    {
        public string Name;                  // connection name, e.g. "이더넷", "Wi-Fi"
        public string Description;           // driver description, e.g. "Realtek PCIe GbE Family Controller"
        public Guid InterfaceGuid;
        public int InterfaceIndex;
        public int InterfaceType;            // IANA ifType: 6 = Ethernet, 71 = IEEE 802.11
        public int PhysicalMediaType;        // 14 = 802.3, 9 = Native 802.11
        public bool Virtual;
        public bool HardwareInterface;
        public bool ConnectorPresent;
        public bool Hidden;
        public int AdminStatus;              // 1 = Up (enabled), 2 = Down (disabled)
        public int OperationalStatus;        // 1 = Up, 2 = Down, ...
        public int MediaConnectState;        // 0 = Unknown, 1 = Connected, 2 = Disconnected
        public int State;                    // 0 Unknown, 1 Present, 2 Started, 3 Disabled
        public string PnpDeviceId;

        public bool IsEnabled => AdminStatus == 1 && State != 3;
        public bool IsDisabled => AdminStatus == 2 || State == 3;
        public bool HasLink => MediaConnectState == 1;

        public bool IsEthernet => InterfaceType == 6 || PhysicalMediaType == 14;
        public bool IsWifi => InterfaceType == 71 || PhysicalMediaType == 9;

        private static readonly Regex VirtualNameRx = new Regex(
            @"(virtual|hyper-v|vmware|virtualbox|vbox|tap-|tap |tun|wintun|wan miniport|loopback|bluetooth|wi-fi direct|wifi direct|npcap|vethernet|docker|wsl|zerotier|tailscale|hamachi|openvpn|wireguard|fortissl|pangp|cisco anyconnect|juniper|teredo|isatap|6to4|kernel debug|microsoft wi-fi|xbox|rndis|remote ndis|internet sharing|mobile device ethernet|usb tether|iphone|android)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public bool LooksVirtual
        {
            get
            {
                if (Virtual || !HardwareInterface) return true;
                if (!string.IsNullOrEmpty(PnpDeviceId) && PnpDeviceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) return true;
                if (VirtualNameRx.IsMatch(Description ?? "") || VirtualNameRx.IsMatch(Name ?? "")) return true;
                return false;
            }
        }

        public bool IsPhysicalEthernetCandidate => IsEthernet && !IsWifi && !LooksVirtual && !Hidden;
        public bool IsPhysicalWifiCandidate => IsWifi && !LooksVirtual && !Hidden;

        public string GuidString => InterfaceGuid.ToString("B").ToUpperInvariant();

        public override string ToString()
        {
            return "[" + Name + "] " + Description + " guid=" + GuidString + " type=" + InterfaceType + "/" + PhysicalMediaType
                 + " admin=" + AdminStatus + " oper=" + OperationalStatus + " media=" + MediaConnectState + " state=" + State
                 + (Virtual ? " virtual" : "") + (HardwareInterface ? " hw" : "") + (ConnectorPresent ? " connector" : "") + (Hidden ? " hidden" : "");
        }

        // ------------------------------------------------------------------

        private static readonly ManagementScope Scope = CreateScope();

        private static ManagementScope CreateScope()
        {
            var scope = new ManagementScope(@"\\.\root\StandardCimv2");
            try { scope.Connect(); } catch (Exception ex) { Log.Warn("WMI 연결 실패(나중에 재시도): " + ex.Message); }
            return scope;
        }

        public static List<NetAdapterInfo> QueryAll() => Query("SELECT * FROM MSFT_NetAdapter");

        private static List<NetAdapterInfo> Query(string wql)
        {
            var list = new List<NetAdapterInfo>();
            using (var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery(wql)))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        var info = FromManagementObject(mo);
                        if (info != null) list.Add(info);
                    }
                }
            }
            return list;
        }

        public static NetAdapterInfo QueryByGuid(Guid guid)
        {
            if (guid == Guid.Empty) return null;
            // Targeted query first (cheap); WQL string comparison is case-insensitive.
            var hits = Query("SELECT * FROM MSFT_NetAdapter WHERE InterfaceGuid = '" + guid.ToString("B") + "'");
            foreach (var a in hits)
                if (a.InterfaceGuid == guid) return a;
            foreach (var a in QueryAll())
                if (a.InterfaceGuid == guid) return a;
            return null;
        }

        /// <summary>Opens the live WMI object for the adapter (for Enable()/Disable()).</summary>
        public static ManagementObject OpenManagementObject(Guid guid)
        {
            var g = guid.ToString("B");
            using (var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT * FROM MSFT_NetAdapter WHERE InterfaceGuid = '" + g + "'")))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                    return mo; // caller disposes
            }
            // Some systems report the GUID in different casing; fall back to a scan.
            using (var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT * FROM MSFT_NetAdapter")))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    if (TryGuid(mo, "InterfaceGuid") == guid) return mo;
                    mo.Dispose();
                }
            }
            return null;
        }

        private static NetAdapterInfo FromManagementObject(ManagementObject mo)
        {
            var guid = TryGuid(mo, "InterfaceGuid");
            if (guid == Guid.Empty) return null;
            return new NetAdapterInfo
            {
                Name = TryString(mo, "Name"),
                Description = TryString(mo, "InterfaceDescription"),
                InterfaceGuid = guid,
                InterfaceIndex = TryInt(mo, "InterfaceIndex"),
                InterfaceType = TryInt(mo, "InterfaceType"),
                PhysicalMediaType = TryInt(mo, "PhysicalMediaType"),
                Virtual = TryBool(mo, "Virtual"),
                HardwareInterface = TryBool(mo, "HardwareInterface"),
                ConnectorPresent = TryBool(mo, "ConnectorPresent"),
                Hidden = TryBool(mo, "Hidden"),
                AdminStatus = TryInt(mo, "InterfaceAdminStatus"),
                OperationalStatus = TryInt(mo, "InterfaceOperationalStatus"),
                MediaConnectState = TryInt(mo, "MediaConnectState"),
                State = TryInt(mo, "State"),
                PnpDeviceId = TryString(mo, "PnPDeviceID")
            };
        }

        private static object TryGet(ManagementBaseObject mo, string prop)
        {
            try { return mo[prop]; } catch { return null; }
        }

        private static string TryString(ManagementBaseObject mo, string prop)
        {
            var v = TryGet(mo, prop);
            return v == null ? string.Empty : Convert.ToString(v);
        }

        private static int TryInt(ManagementBaseObject mo, string prop)
        {
            var v = TryGet(mo, prop);
            if (v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }

        private static bool TryBool(ManagementBaseObject mo, string prop)
        {
            var v = TryGet(mo, prop);
            if (v == null) return false;
            try { return Convert.ToBoolean(v); } catch { return false; }
        }

        private static Guid TryGuid(ManagementBaseObject mo, string prop)
        {
            var s = TryString(mo, prop);
            return Guid.TryParse(s, out var g) ? g : Guid.Empty;
        }
    }
}
