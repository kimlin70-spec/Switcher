using System;
using System.Threading;
using System.Windows.Forms;

namespace NetSwitch
{
    internal static class Program
    {
        private const string MutexName = @"Local\NetSwitch.Elevated.Instance";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log.Error("UI 스레드 예외", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("처리되지 않은 예외", e.ExceptionObject as Exception);

            bool fromTask = false, restore = false, installOnly = false, uninstallOnly = false;
            foreach (var a in args ?? new string[0])
            {
                switch (a.ToLowerInvariant())
                {
                    case "--from-task": fromTask = true; break;
                    case "--restore": restore = true; break;
                    case "--install-task": installOnly = true; break;
                    case "--uninstall-task": uninstallOnly = true; break;
                }
            }

            bool elevated = Elevation.IsElevated();
            Log.Info("---- NetSwitch " + typeof(Program).Assembly.GetName().Version + " 시작 (elevated=" + elevated + ", args=" + string.Join(" ", args ?? new string[0]) + ")");
            if (fromTask) Log.Info("작업 스케줄러에서 시작됨");

            var cfg = Config.Load();
            if (!elevated && !fromTask) WarnIfBadLocation();   // launcher only; never block a task-started instance

            // ---- everything below needs administrator rights ----
            if (!elevated)
            {
                bool running = InstanceRunning();
                if (running && !restore && !installOnly && !uninstallOnly)
                {
                    MessageBox.Show("NetSwitch가 이미 트레이에서 실행 중입니다.\n작업 표시줄 오른쪽의 숨겨진 아이콘(^)도 확인해 보세요.",
                        "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                if (fromTask)
                {
                    // The task itself started us but we are not elevated: this account is not an administrator.
                    Log.Error("작업 스케줄러가 시작했지만 관리자 권한이 없습니다. 이 계정은 관리자가 아닙니다.");
                    MessageBox.Show("NetSwitch는 관리자 계정에서만 동작합니다.\n이 Windows 계정은 관리자 권한이 없어 유선 랜을 켜고 끌 수 없습니다.",
                        "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                // Preferred path: the registered task starts us elevated without UAC.
                if (!restore && !installOnly && !uninstallOnly && Autostart.IsRegisteredForThisExe())
                {
                    if (Autostart.RunTask())
                    {
                        Log.Info("등록된 작업으로 관리자 인스턴스를 시작했습니다. 이 인스턴스는 종료합니다.");
                        return 0;
                    }
                }
                // Fallback: one UAC prompt. The elevated instance (re)registers the task so this does not repeat.
                while (true)
                {
                    if (Elevation.RelaunchElevated(string.Join(" ", args ?? new string[0]))) return 0;
                    var r = MessageBox.Show(
                        "NetSwitch는 와이파이와 유선 랜을 전환하기 위해 관리자 권한이 필요합니다.\n관리자 권한 요청이 취소되었습니다.\n\n다시 시도할까요?",
                        "NetSwitch", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                    if (r != DialogResult.Retry) return 1;
                }
            }

            if (installOnly)
            {
                try { cfg.Autostart = true; cfg.Save(); Autostart.Register(); return 0; }
                catch (Exception ex) { MessageBox.Show(ex.Message, "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }
            if (uninstallOnly)
            {
                try { cfg.Autostart = false; cfg.Save(); Autostart.Unregister(); return 0; }
                catch (Exception ex) { MessageBox.Show(ex.Message, "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }

            if (restore)
            {
                if (InstanceRunning())
                {
                    MessageBox.Show("트레이에서 실행 중인 NetSwitch를 먼저 종료한 뒤 다시 실행하세요.\n(또는 트레이 아이콘 우클릭 → '원래대로 복구')",
                        "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
                try
                {
                    var sw = new Net.NetworkSwitcher(cfg);
                    sw.DiscoverAdapters();
                    sw.RestoreDefaults(CancellationToken.None);
                    MessageBox.Show("와이파이와 유선 랜을 모두 켜고 Windows 기본 상태로 되돌렸습니다.", "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Error("복구 실패", ex);
                    MessageBox.Show("복구 실패: " + ex.Message, "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }

            // Keep the autostart task pointing at this exe (unless the user turned autostart off). Done before the
            // single-instance check so that replacing the exe while an older copy is still running still fixes it.
            // When started by hand (not by the task) we re-register unconditionally: that also repairs a disabled task.
            string note = null;
            if (cfg.Autostart)
            {
                try
                {
                    bool wasRegistered = Autostart.IsRegisteredForThisExe();
                    if (!wasRegistered || !fromTask)
                    {
                        Autostart.Register();
                        if (!wasRegistered) note = "Windows 시작 시 자동으로 실행되도록 등록했습니다. 앞으로 UAC 창은 뜨지 않습니다.";
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("자동 실행 등록 실패", ex);
                    note = "자동 실행 등록에 실패했습니다: " + ex.Message;
                }
            }

            // Single elevated instance.
            using (var mutex = new Mutex(true, MutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    Log.Info("이미 실행 중인 인스턴스가 있어 종료합니다.");
                    MessageBox.Show("NetSwitch가 이미 트레이에서 실행 중입니다.\n새 버전으로 바꾸려면 트레이 아이콘 우클릭 → 종료 후 다시 실행하세요.",
                        "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                using (var app = new TrayApp(cfg, note))
                {
                    Application.Run(app);
                }
                GC.KeepAlive(mutex);
            }
            Log.Info("---- 종료");
            return 0;
        }

        /// <summary>Elevated tokens do not see mapped network drives, and removable drives change letters; both break the logon task.</summary>
        private static void WarnIfBadLocation()
        {
            try
            {
                var exe = AppPaths.ExePath;
                if (exe.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    MessageBox.Show("NetSwitch.exe가 네트워크 경로에 있습니다. 로컬 폴더(예: C:\\Tools\\NetSwitch)로 복사한 뒤 실행하세요.", "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var root = System.IO.Path.GetPathRoot(exe);
                if (string.IsNullOrEmpty(root)) return;
                var type = new System.IO.DriveInfo(root).DriveType;
                if (type == System.IO.DriveType.Network || type == System.IO.DriveType.Removable)
                    MessageBox.Show("NetSwitch.exe가 " + (type == System.IO.DriveType.Network ? "네트워크 드라이브" : "이동식 드라이브") + "에 있습니다.\n자동 실행이 제대로 되려면 로컬 폴더(예: C:\\Tools\\NetSwitch)로 옮긴 뒤 실행하세요.", "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        /// <summary>True if an elevated tray instance holds the mutex. From a medium-IL process the open may be denied; treat that as "running".</summary>
        private static bool InstanceRunning()
        {
            try
            {
                using (Mutex.OpenExisting(MutexName)) return true;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }
            catch (Exception ex) { Log.Warn("인스턴스 확인 실패: " + ex.Message); return false; }
        }
    }
}
