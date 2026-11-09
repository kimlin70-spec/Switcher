using System;
using System.Runtime.InteropServices;

namespace NetSwitch.Native
{
    /// <summary>
    /// Minimal P/Invoke surface over wlanapi.dll (Native Wifi API).
    /// Only what the switcher needs: enumerate interfaces, read state / current
    /// connection, toggle auto-configuration, connect to a profile, disconnect.
    /// </summary>
    internal static class WlanApi
    {
        public const uint ERROR_SUCCESS = 0;
        public const uint ERROR_INVALID_STATE = 5023;
        public const uint ERROR_NOT_FOUND = 1168;

        public enum WLAN_INTERFACE_STATE
        {
            NotReady = 0,
            Connected = 1,
            AdHocNetworkFormed = 2,
            Disconnecting = 3,
            Disconnected = 4,
            Associating = 5,
            Discovering = 6,
            Authenticating = 7
        }

        public enum WLAN_INTF_OPCODE
        {
            AutoconfEnabled = 1,
            BackgroundScanEnabled = 2,
            MediaStreamingMode = 3,
            RadioState = 4,
            BssType = 5,
            InterfaceState = 6,
            CurrentConnection = 7,
            ChannelNumber = 8
        }

        public enum WLAN_CONNECTION_MODE
        {
            Profile = 0,
            TemporaryProfile = 1,
            DiscoverySecure = 2,
            DiscoveryUnsecure = 3,
            Auto = 4,
            Invalid = 5
        }

        public enum DOT11_BSS_TYPE
        {
            Infrastructure = 1,
            Independent = 2,
            Any = 3
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public WLAN_INTERFACE_STATE isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;

            public override string ToString()
            {
                if (ucSSID == null || uSSIDLength == 0) return string.Empty;
                int len = (int)Math.Min(uSSIDLength, 32);
                try { return System.Text.Encoding.UTF8.GetString(ucSSID, 0, len); }
                catch { return System.Text.Encoding.Default.GetString(ucSSID, 0, len); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public int dot11BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public int dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality;
            public uint ulRxRate;
            public uint ulTxRate;
        }

        /// <summary>
        /// Prefix of WLAN_CONNECTION_ATTRIBUTES. The real struct continues with
        /// WLAN_SECURITY_ATTRIBUTES, which we never read; the buffer returned by
        /// WlanQueryInterface is always the full size so reading a prefix is safe.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_CONNECTION_ATTRIBUTES
        {
            public WLAN_INTERFACE_STATE isState;
            public WLAN_CONNECTION_MODE wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_CONNECTION_PARAMETERS
        {
            public WLAN_CONNECTION_MODE wlanConnectionMode;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string strProfile;
            public IntPtr pDot11Ssid;
            public IntPtr pDesiredBssidList;
            public DOT11_BSS_TYPE dot11BssType;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_PROFILE_INFO
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public uint dwFlags;
        }

        [DllImport("wlanapi.dll")]
        public static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, WLAN_INTF_OPCODE opCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanSetInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, WLAN_INTF_OPCODE opCode, uint dwDataSize, IntPtr pData, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanConnect(IntPtr hClientHandle, ref Guid pInterfaceGuid, ref WLAN_CONNECTION_PARAMETERS pConnectionParameters, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanDisconnect(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanGetProfileList(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved, out IntPtr ppProfileList);

        [DllImport("wlanapi.dll")]
        public static extern void WlanFreeMemory(IntPtr pMemory);
    }
}
