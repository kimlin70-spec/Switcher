using System;
using System.Diagnostics;
using System.Security.Principal;

namespace NetSwitch
{
    internal static class Elevation
    {
        public static bool IsElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>Relaunches this exe with the "runas" verb (one UAC prompt). Returns false if the user declined.</summary>
        public static bool RelaunchElevated(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo(AppPaths.ExePath, arguments ?? "")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = System.IO.Path.GetDirectoryName(AppPaths.ExePath) ?? ""
                };
                Process.Start(psi);
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
            {
                Log.Warn("사용자가 UAC 창을 취소했습니다");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("관리자 권한으로 다시 시작 실패", ex);
                return false;
            }
        }
    }
}
