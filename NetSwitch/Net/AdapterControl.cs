using System;
using System.Management;
using System.Threading;

namespace NetSwitch.Net
{
    /// <summary>Enable / disable a network adapter through MSFT_NetAdapter (same path as Disable-NetAdapter). Requires elevation.</summary>
    internal static class AdapterControl
    {
        public static void Disable(Guid guid) => Invoke(guid, "Disable");
        public static void Enable(Guid guid) => Invoke(guid, "Enable");

        private static void Invoke(Guid guid, string method)
        {
            string verb = method == "Disable" ? "끄기" : "켜기";
            try
            {
                using (var mo = NetAdapterInfo.OpenManagementObject(guid))
                {
                    if (mo == null) throw new InvalidOperationException("네트워크 어댑터를 찾을 수 없습니다: " + guid.ToString("B"));
                    var result = mo.InvokeMethod(method, null, null);
                    uint rc = 0;
                    try { if (result != null && result["ReturnValue"] != null) rc = Convert.ToUInt32(result["ReturnValue"]); } catch { }
                    Log.Info("MSFT_NetAdapter." + method + "() -> " + rc + " on " + guid.ToString("B"));
                    if (rc != 0) throw new InvalidOperationException("네트워크 어댑터 " + verb + " 실패 (코드 " + rc + ")");
                }
            }
            catch (ManagementException ex)
            {
                Log.Error("MSFT_NetAdapter." + method + " WMI 오류 " + ex.ErrorCode, ex);
                string hint = ex.ErrorCode == ManagementStatus.AccessDenied ? " 관리자 권한이 없습니다." : "";
                throw new InvalidOperationException("네트워크 어댑터 " + verb + " 실패 (" + ex.ErrorCode + ")." + hint, ex);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Log.Error("MSFT_NetAdapter." + method + " COM 오류", ex);
                throw new InvalidOperationException("네트워크 어댑터 " + verb + " 실패 (0x" + ((uint)ex.ErrorCode).ToString("X8") + ")", ex);
            }
        }

        /// <summary>Polls until <paramref name="predicate"/> is true for the adapter, or the timeout elapses.</summary>
        public static NetAdapterInfo WaitFor(Guid guid, Func<NetAdapterInfo, bool> predicate, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            NetAdapterInfo last = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { last = NetAdapterInfo.QueryByGuid(guid); }
                catch (Exception ex) { Log.Warn("어댑터 상태 조회 실패: " + ex.Message); }
                if (last != null && predicate(last)) return last;
                if (DateTime.UtcNow >= deadline) return last;
                Thread.Sleep(250);
            }
        }
    }
}
