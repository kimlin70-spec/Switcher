using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetSwitch
{
    internal enum NetMode
    {
        Unknown = 0,
        Wifi = 1,   // 인터넷(와이파이)
        Lan = 2     // 업무망(유선 랜)
    }

    internal enum WifiMethod
    {
        /// <summary>Disable WLAN auto-configuration on the interface and disconnect (fast, blocks auto-reconnect).</summary>
        Autoconfig = 0,
        /// <summary>Disable the whole Wi-Fi adapter (slowest, most forceful).</summary>
        Adapter = 1
    }

    /// <summary>
    /// Plain key=value settings file in %LOCALAPPDATA%\NetSwitch\config.txt.
    /// Written by the app; a user may edit it in a text editor while the app is closed.
    /// </summary>
    internal sealed class Config
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string ConfigPath => Path.Combine(AppPaths.DataDir, "config.txt");

        // --- user-editable ---
        /// <summary>Wi-Fi profile to reconnect to. Empty = use the profile that was connected when we last left Wi-Fi.</summary>
        public string WifiProfile { get => Get("wifi_profile"); set => Set("wifi_profile", value); }
        public WifiMethod WifiMethod
        {
            get => string.Equals(Get("wifi_method"), "adapter", StringComparison.OrdinalIgnoreCase) ? WifiMethod.Adapter : WifiMethod.Autoconfig;
            set => Set("wifi_method", value == WifiMethod.Adapter ? "adapter" : "autoconfig");
        }
        /// <summary>Optional pin of the wired adapter by connection name (e.g. "이더넷") or interface GUID.</summary>
        public string EthernetAdapter { get => Get("ethernet_adapter"); set => Set("ethernet_adapter", value); }
        /// <summary>Optional pin of the Wi-Fi adapter by connection name (e.g. "Wi-Fi") or interface GUID.</summary>
        public string WifiAdapter { get => Get("wifi_adapter"); set => Set("wifi_adapter", value); }
        public int LinkWaitSeconds { get => GetInt("link_wait_sec", 12, 3, 120); set => Set("link_wait_sec", value.ToString(CultureInfo.InvariantCulture)); }
        public int IpWaitSeconds { get => GetInt("ip_wait_sec", 25, 3, 300); set => Set("ip_wait_sec", value.ToString(CultureInfo.InvariantCulture)); }
        public int WifiConnectWaitSeconds { get => GetInt("wifi_connect_wait_sec", 20, 3, 300); set => Set("wifi_connect_wait_sec", value.ToString(CultureInfo.InvariantCulture)); }
        public bool Watchdog { get => GetBool("watchdog", true); set => Set("watchdog", value ? "true" : "false"); }
        public bool FlushDns { get => GetBool("flush_dns", true); set => Set("flush_dns", value ? "true" : "false"); }

        // --- maintained by the app ---
        public NetMode LastMode
        {
            get
            {
                var v = Get("mode");
                if (string.Equals(v, "wifi", StringComparison.OrdinalIgnoreCase)) return NetMode.Wifi;
                if (string.Equals(v, "lan", StringComparison.OrdinalIgnoreCase)) return NetMode.Lan;
                return NetMode.Unknown;
            }
            set => Set("mode", value == NetMode.Wifi ? "wifi" : value == NetMode.Lan ? "lan" : "");
        }
        public string LastWifiProfile { get => Get("last_wifi_profile"); set => Set("last_wifi_profile", value); }
        public string EthernetGuid { get => Get("ethernet_guid"); set => Set("ethernet_guid", value); }
        public string WifiGuid { get => Get("wifi_guid"); set => Set("wifi_guid", value); }

        public string Get(string key) => _values.TryGetValue(key, out var v) ? v : string.Empty;

        public void Set(string key, string value)
        {
            if (value == null) value = string.Empty;
            _values[key] = value.Trim();
        }

        private int GetInt(string key, int def, int min, int max)
        {
            if (int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return Math.Max(min, Math.Min(max, n));
            return def;
        }

        private bool GetBool(string key, bool def)
        {
            var v = Get(key);
            if (string.IsNullOrEmpty(v)) return def;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public static Config Load()
        {
            var cfg = new Config();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    foreach (var raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        cfg._values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("설정 파일을 읽지 못했습니다", ex);
            }
            return cfg;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var sb = new StringBuilder();
                sb.AppendLine("# NetSwitch 설정 파일. 프로그램을 종료한 뒤 편집하세요.");
                sb.AppendLine("# wifi_profile      : 인터넷으로 돌아올 때 연결할 와이파이 프로필 이름(비우면 마지막에 쓰던 와이파이)");
                sb.AppendLine("# wifi_method       : autoconfig(기본, 빠름) 또는 adapter(와이파이 장치를 통째로 끔)");
                sb.AppendLine("# ethernet_adapter  : 유선 랜 어댑터 지정(연결 이름 또는 GUID, 보통 비워 둠)");
                sb.AppendLine("# wifi_adapter      : 와이파이 어댑터 지정(연결 이름 또는 GUID, 보통 비워 둠)");
                sb.AppendLine("# link_wait_sec     : 유선 랜을 켠 뒤 랜선 연결(링크)을 기다리는 최대 시간(초)");
                sb.AppendLine("# ip_wait_sec       : IP 주소 할당을 기다리는 최대 시간(초)");
                sb.AppendLine("# watchdog          : true면 반대쪽 망이 몰래 다시 연결될 때 자동으로 끊음");
                sb.AppendLine("# 아래 값들은 프로그램이 자동으로 관리합니다: mode, last_wifi_profile, ethernet_guid, wifi_guid");
                sb.AppendLine();
                var keys = new List<string>(_values.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var k in keys)
                    sb.Append(k).Append('=').AppendLine(_values[k]);
                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Error("설정 파일을 저장하지 못했습니다", ex);
            }
        }
    }
}
