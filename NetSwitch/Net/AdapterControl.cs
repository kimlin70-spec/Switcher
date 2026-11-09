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
            using (var mo = NetAdapterInfo.OpenManagementObject(guid))
            {
                if (mo == null) throw new InvalidOperationException("어댑터를 찾을 수 없습니다: " + guid.ToString("B"));
                var result = mo.InvokeMethod(method, null, null);
                uint rc = 0;
                try { if (result != null && result["ReturnValue"] != null) rc = Convert.ToUInt32(result["ReturnValue"]); } catch { }
                Log.Info("MSFT_NetAdapter." + method + "() -> " + rc + " on " + guid.ToString("B"));
                if (rc != 0) throw new InvalidOperationException("어댑터 " + method + " 실패 (코드 " + rc + ")");
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
