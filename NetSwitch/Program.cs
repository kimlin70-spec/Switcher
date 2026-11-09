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
            if (fromTask) Log.Info("작업 스케줄러에서 시작됨");
            Log.Info("---- NetSwitch " + typeof(Program).Assembly.GetName().Version + " 시작 (elevated=" + elevated + ", args=" + string.Join(" ", args ?? new string[0]) + ")");

            // ---- everything below needs administrator rights ----
            if (!elevated)
            {
                // Preferred path: the registered task starts us elevated without UAC.
                if (!restore && !installOnly && !uninstallOnly && Autostart.IsRegisteredForThisExe())
                {
                    if (Autostart.RunTask())
                    {
                        Log.Info("등록된 작업으로 관리자 인스턴스를 시작했습니다. 이 인스턴스는 종료합니다.");
                        return 0;
                    }
                }
                // Fallback: one UAC prompt. The elevated instance registers the task so this does not repeat.
                if (Elevation.RelaunchElevated(string.Join(" ", args ?? new string[0])))
                    return 0;
                MessageBox.Show("NetSwitch는 유선 랜을 켜고 끄기 위해 관리자 권한이 필요합니다.\nUAC 창에서 '예'를 눌러 주세요.",
                    "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            if (installOnly)
            {
                try { Autostart.Register(); return 0; }
                catch (Exception ex) { MessageBox.Show(ex.Message, "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }
            if (uninstallOnly)
            {
                try { Autostart.Unregister(); return 0; }
                catch (Exception ex) { MessageBox.Show(ex.Message, "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }

            var cfg = Config.Load();

            if (restore)
            {
                try
                {
                    var sw = new Net.NetworkSwitcher(cfg);
                    sw.DiscoverAdapters();
                    sw.RestoreDefaults(CancellationToken.None);
                    MessageBox.Show("와이파이와 유선 랜을 모두 켜고 윈도우 기본 상태로 되돌렸습니다.", "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Error("복구 실패", ex);
                    MessageBox.Show("복구 실패: " + ex.Message, "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }

            // Single elevated instance.
            using (var mutex = new Mutex(true, MutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    Log.Info("이미 실행 중인 인스턴스가 있어 종료합니다.");
                    return 0;
                }

                string note = null;
                try
                {
                    if (!Autostart.IsRegisteredForThisExe())
                    {
                        Autostart.Register();
                        note = "Windows 시작 시 자동으로 실행되도록 등록했습니다. 앞으로 UAC 창은 뜨지 않습니다.";
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("자동 실행 등록 실패", ex);
                    note = "자동 실행 등록에 실패했습니다: " + ex.Message;
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
    }
}
