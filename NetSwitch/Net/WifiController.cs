using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NetSwitch.Native;
using static NetSwitch.Native.WlanApi;

namespace NetSwitch.Net
{
    internal sealed class WifiState
    {
        public WLAN_INTERFACE_STATE State = WLAN_INTERFACE_STATE.NotReady;
        public string ProfileName = "";
        public string Ssid = "";
        public bool InterfacePresent;       // the WLAN service knows the interface (false when adapter disabled)
        public bool IsConnected => State == WLAN_INTERFACE_STATE.Connected;
        public bool IsConnecting => State == WLAN_INTERFACE_STATE.Associating || State == WLAN_INTERFACE_STATE.Authenticating || State == WLAN_INTERFACE_STATE.Discovering;
        public override string ToString() => (InterfacePresent ? State.ToString() : "absent") + (IsConnected ? " profile='" + ProfileName + "' ssid='" + Ssid + "'" : "");
    }

    /// <summary>
    /// Controls the single physical Wi-Fi interface through the Native Wifi API.
    /// Thread-safe: every public method opens/closes its own WLAN handle.
    /// </summary>
    internal sealed class WifiController
    {
        private readonly Guid _guid;
        public Guid InterfaceGuid => _guid;

        public WifiController(Guid interfaceGuid) { _guid = interfaceGuid; }

        // ---------- enumeration ----------

        public sealed class WlanInterface
        {
            public Guid Guid; public string Description; public WLAN_INTERFACE_STATE State;
        }

        public static List<WlanInterface> EnumerateInterfaces()
        {
            var list = new List<WlanInterface>();
            IntPtr h = IntPtr.Zero, pList = IntPtr.Zero;
            try
            {
                Check(WlanOpenHandle(2, IntPtr.Zero, out _, out h), "WlanOpenHandle");
                Check(WlanEnumInterfaces(h, IntPtr.Zero, out pList), "WlanEnumInterfaces");
                int count = Marshal.ReadInt32(pList);
                int itemSize = Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO));
                for (int i = 0; i < count; i++)
                {
                    var p = new IntPtr(pList.ToInt64() + 8 + (long)i * itemSize);
                    var info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(p, typeof(WLAN_INTERFACE_INFO));
                    list.Add(new WlanInterface { Guid = info.InterfaceGuid, Description = info.strInterfaceDescription, State = info.isState });
                }
            }
            finally
            {
                if (pList != IntPtr.Zero) WlanFreeMemory(pList);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
            return list;
        }

        // ---------- state ----------

        public WifiState Query()
        {
            var st = new WifiState();
            IntPtr h = IntPtr.Zero;
            try
            {
                Check(WlanOpenHandle(2, IntPtr.Zero, out _, out h), "WlanOpenHandle");
                var guid = _guid;

                // Is the interface known to the WLAN service at all?
                IntPtr pList = IntPtr.Zero;
                try
                {
                    Check(WlanEnumInterfaces(h, IntPtr.Zero, out pList), "WlanEnumInterfaces");
                    int count = Marshal.ReadInt32(pList);
                    int itemSize = Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO));
                    for (int i = 0; i < count; i++)
                    {
                        var p = new IntPtr(pList.ToInt64() + 8 + (long)i * itemSize);
                        var info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(p, typeof(WLAN_INTERFACE_INFO));
                        if (info.InterfaceGuid == guid) { st.InterfacePresent = true; st.State = info.isState; break; }
                    }
                }
                finally { if (pList != IntPtr.Zero) WlanFreeMemory(pList); }

                if (!st.InterfacePresent) return st;

                if (st.State == WLAN_INTERFACE_STATE.Connected)
                {
                    IntPtr pData = IntPtr.Zero;
                    try
                    {
                        uint rc = WlanQueryInterface(h, ref guid, WLAN_INTF_OPCODE.CurrentConnection, IntPtr.Zero, out uint size, out pData, IntPtr.Zero);
                        if (rc == ERROR_SUCCESS && pData != IntPtr.Zero && size >= Marshal.SizeOf(typeof(WLAN_CONNECTION_ATTRIBUTES)))
                        {
                            var attr = (WLAN_CONNECTION_ATTRIBUTES)Marshal.PtrToStructure(pData, typeof(WLAN_CONNECTION_ATTRIBUTES));
                            st.ProfileName = attr.strProfileName ?? "";
                            st.Ssid = attr.wlanAssociationAttributes.dot11Ssid.ToString();
                        }
                        else if (rc != ERROR_INVALID_STATE)
                        {
                            Log.Warn("WlanQueryInterface(current_connection) -> " + rc);
                        }
                    }
                    finally { if (pData != IntPtr.Zero) WlanFreeMemory(pData); }
                }
            }
            finally { if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero); }
            return st;
        }

        public bool? QueryAutoconfigEnabled()
        {
            IntPtr h = IntPtr.Zero, pData = IntPtr.Zero;
            try
            {
                Check(WlanOpenHandle(2, IntPtr.Zero, out _, out h), "WlanOpenHandle");
                var guid = _guid;
                uint rc = WlanQueryInterface(h, ref guid, WLAN_INTF_OPCODE.AutoconfEnabled, IntPtr.Zero, out uint size, out pData, IntPtr.Zero);
                if (rc != ERROR_SUCCESS || pData == IntPtr.Zero || size < 4) return null;
                return Marshal.ReadInt32(pData) != 0;
            }
            catch { return null; }
            finally
            {
                if (pData != IntPtr.Zero) WlanFreeMemory(pData);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
        }

        public List<string> GetProfileNames()
        {
            var names = new List<string>();
            IntPtr h = IntPtr.Zero, pList = IntPtr.Zero;
            try
            {
                Check(WlanOpenHandle(2, IntPtr.Zero, out _, out h), "WlanOpenHandle");
                var guid = _guid;
                uint rc = WlanGetProfileList(h, ref guid, IntPtr.Zero, out pList);
                if (rc != ERROR_SUCCESS) { Log.Warn("WlanGetProfileList -> " + rc); return names; }
                int count = Marshal.ReadInt32(pList);
                int itemSize = Marshal.SizeOf(typeof(WLAN_PROFILE_INFO));
                for (int i = 0; i < count; i++)
                {
                    var p = new IntPtr(pList.ToInt64() + 8 + (long)i * itemSize);
                    var info = (WLAN_PROFILE_INFO)Marshal.PtrToStructure(p, typeof(WLAN_PROFILE_INFO));
                    if (!string.IsNullOrEmpty(info.strProfileName)) names.Add(info.strProfileName);
                }
            }
            finally
            {
                if (pList != IntPtr.Zero) WlanFreeMemory(pList);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
            return names;
        }

        // ---------- actions ----------

        /// <summary>Turns WLAN auto-configuration on/off for this interface. Off = Windows stops managing (and auto-connecting) the interface. Needs elevation.</summary>
        public void SetAutoconfig(bool enabled)
        {
            IntPtr h = IntPtr.Zero;
            IntPtr pData = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(pData, enabled ? 1 : 0);
                Check(WlanOpenHandle(2, IntPtr.Zero, out _, out h), "WlanOpenHandle");
                var guid = _guid;
                uint rc = WlanSetInterface(h, ref guid, WLAN_INTF_OPCODE.AutoconfEnabled, 4, pData, IntPtr.Zero);
                Log.Info("WlanSetInterface(autoconf=" + (enabled ? "on" : "off") + ") -> " + rc);
                Check(rc, "WlanSetInterface(autoconf)");
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
        }

        public void Disconnect()
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                Check(WlanOpenHandle(2, IntPtr.Zero, out _, out h), "WlanOpenHandle");
                var guid = _guid;
                uint rc = WlanDisconnect(h, ref guid, IntPtr.Zero);
                Log.Info("WlanDisconnect -> " + rc);
                // ERROR_INVALID_STATE: already disconnected / not managed. Not fatal.
                if (rc != ERROR_SUCCESS && rc != ERROR_INVALID_STATE) Check(rc, "WlanDisconnect");
            }
            finally { if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero); }
        }

        /// <summary>Starts an asynchronous connect to a saved profile. Returns the Win32 error code (0 = request accepted).</summary>
        public uint Connect(string profileName)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                Check(WlanOpenHandle(2, IntPtr.Zero, out _, out h), "WlanOpenHandle");
                var guid = _guid;
                var prm = new WLAN_CONNECTION_PARAMETERS
                {
                    wlanConnectionMode = WLAN_CONNECTION_MODE.Profile,
                    strProfile = profileName,
                    pDot11Ssid = IntPtr.Zero,
                    pDesiredBssidList = IntPtr.Zero,
                    dot11BssType = DOT11_BSS_TYPE.Any,
                    dwFlags = 0
                };
                uint rc = WlanConnect(h, ref guid, ref prm, IntPtr.Zero);
                Log.Info("WlanConnect('" + profileName + "') -> " + rc);
                return rc;
            }
            finally { if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero); }
        }

        public WifiState WaitFor(Func<WifiState, bool> predicate, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            WifiState last = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { last = Query(); }
                catch (Exception ex) { Log.Warn("와이파이 상태 조회 실패: " + ex.Message); last = new WifiState(); }
                if (predicate(last)) return last;
                if (DateTime.UtcNow >= deadline) return last;
                Thread.Sleep(250);
            }
        }

        private static void Check(uint rc, string what)
        {
            if (rc != ERROR_SUCCESS)
                throw new System.ComponentModel.Win32Exception((int)rc, what + " 실패 (코드 " + rc + ")");
        }
    }
}
