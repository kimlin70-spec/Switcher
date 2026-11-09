using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace NetSwitch
{
    /// <summary>
    /// Registers a Task Scheduler task that starts NetSwitch at logon with the
    /// highest available privileges. That is what makes the app both auto-start
    /// and run elevated without a UAC prompt. A non-elevated instance can also
    /// start the task on demand ("schtasks /run"), which is how a manual
    /// double-click on the exe ends up elevated without UAC once the task exists.
    /// </summary>
    internal static class Autostart
    {
        public const string TaskName = "NetSwitch";

        public static bool IsRegistered()
        {
            var r = RunSchtasks("/Query /TN \"" + TaskName + "\" /XML");
            return r.ExitCode == 0;
        }

        /// <summary>The exe path the registered task points at, or null.</summary>
        public static string RegisteredCommand()
        {
            var r = RunSchtasks("/Query /TN \"" + TaskName + "\" /XML");
            if (r.ExitCode != 0) return null;
            try
            {
                var xml = r.StdOut;
                int i = xml.IndexOf("<?xml", StringComparison.Ordinal);
                if (i > 0) xml = xml.Substring(i);
                var doc = XDocument.Parse(xml);
                foreach (var el in doc.Descendants())
                    if (el.Name.LocalName == "Command") return el.Value.Trim().Trim('"');
            }
            catch (Exception ex) { Log.Warn("작업 XML 해석 실패: " + ex.Message); }
            return null;
        }

        public static bool IsRegisteredForThisExe()
        {
            var cmd = RegisteredCommand();
            return cmd != null && string.Equals(Path.GetFullPath(cmd), Path.GetFullPath(AppPaths.ExePath), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Creates or replaces the task. Requires elevation.</summary>
        public static void Register()
        {
            string sid;
            using (var id = WindowsIdentity.GetCurrent()) sid = id.User?.Value ?? id.Name;

            var exe = AppPaths.ExePath;
            var tmp = Path.Combine(Path.GetTempPath(), "NetSwitch-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                // Task Scheduler exports tasks as UTF-16 with BOM; import that first. Some builds are reported
                // to accept only UTF-8, so fall back to a UTF-8 file whose declaration matches its bytes.
                File.WriteAllText(tmp, BuildTaskXml(exe, sid, "UTF-16"), new UnicodeEncoding(false, true));
                var r = RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
                if (r.ExitCode != 0)
                {
                    Log.Warn("UTF-16 작업 XML 등록 실패 (코드 " + r.ExitCode + "): " + (r.StdErr + " " + r.StdOut).Trim() + " -> UTF-8로 재시도");
                    File.WriteAllText(tmp, BuildTaskXml(exe, sid, "UTF-8"), new UTF8Encoding(true));
                    r = RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
                }
                if (r.ExitCode != 0)
                    throw new InvalidOperationException("작업 스케줄러 등록 실패 (코드 " + r.ExitCode + "): " + (r.StdErr + " " + r.StdOut).Trim());
                Log.Info("작업 스케줄러에 등록: " + exe);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        public static void Unregister()
        {
            var r = RunSchtasks("/Delete /TN \"" + TaskName + "\" /F");
            if (r.ExitCode != 0 && IsRegistered())
                throw new InvalidOperationException("작업 스케줄러 삭제 실패 (코드 " + r.ExitCode + "): " + (r.StdErr + " " + r.StdOut).Trim());
            Log.Info("작업 스케줄러 등록 해제");
        }

        /// <summary>Starts the registered task (elevated instance). Works from a non-elevated process of the same user.</summary>
        public static bool RunTask()
        {
            var r = RunSchtasks("/Run /TN \"" + TaskName + "\"");
            if (r.ExitCode != 0) Log.Warn("schtasks /Run 실패: " + (r.StdErr + " " + r.StdOut).Trim());
            return r.ExitCode == 0;
        }

        private static string BuildTaskXml(string exePath, string userId, string encodingName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"" + encodingName + "\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo>");
            sb.AppendLine("    <Author>NetSwitch</Author>");
            sb.AppendLine("    <Description>인터넷(와이파이)/업무망(유선 랜) 전환기를 로그온 시 관리자 권한으로 시작합니다.</Description>");
            sb.AppendLine("  </RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine("    <LogonTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <UserId>" + Esc(userId) + "</UserId>");
            sb.AppendLine("      <Delay>PT5S</Delay>");
            sb.AppendLine("    </LogonTrigger>");
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <UserId>" + Esc(userId) + "</UserId>");
            sb.AppendLine("      <LogonType>InteractiveToken</LogonType>");
            sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>false</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings>");
            sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
            sb.AppendLine("    </IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            sb.AppendLine("    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>");
            sb.AppendLine("    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>");
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>5</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>\"" + Esc(exePath) + "\"</Command>");
            sb.AppendLine("      <Arguments>--from-task</Arguments>");
            sb.AppendLine("      <WorkingDirectory>" + Esc(Path.GetDirectoryName(exePath) ?? "") + "</WorkingDirectory>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        private static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "");

        private sealed class ProcResult { public int ExitCode; public string StdOut = ""; public string StdErr = ""; }

        private static ProcResult RunSchtasks(string args)
        {
            var res = new ProcResult { ExitCode = -1 };
            try
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var psi = new ProcessStartInfo(Path.Combine(system, "schtasks.exe"), args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                    // Redirected schtasks output arrives in the console code page (CP949 on Korean Windows),
                    // which is what the default decoder uses. Do not force UTF-16 here.
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return res;
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return res; }
                    res.StdOut = outTask.Result;
                    res.StdErr = errTask.Result;
                    res.ExitCode = p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Log.Error("schtasks 실행 실패 (" + args + ")", ex);
            }
            return res;
        }
    }
}
