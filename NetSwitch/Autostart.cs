using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace NetSwitch
{
    /// <summary>
    /// Registers a Task Scheduler task that starts NetSwitch at logon with the
    /// highest available privileges. That is what makes the app both auto-start
    /// and run elevated without a UAC prompt. A non-elevated instance can also
    /// start the task on demand, which is how a manual double-click on the exe
    /// ends up elevated without UAC once the task exists.
    ///
    /// Primary path: the Task Scheduler COM API (Schedule.Service) through late binding,
    /// so no XML file, no encoding and no schema-ordering pitfalls.
    /// Fallback path: schtasks.exe with an XML definition.
    /// </summary>
    internal static class Autostart
    {
        public const string TaskName = "NetSwitch";

        // Task Scheduler 2.0 constants
        private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
        private const int TASK_RUNLEVEL_HIGHEST = 1;
        private const int TASK_TRIGGER_LOGON = 9;
        private const int TASK_ACTION_EXEC = 0;
        private const int TASK_CREATE_OR_UPDATE = 6;
        private const int TASK_INSTANCES_IGNORE_NEW = 2;
        private const int TASK_COMPATIBILITY_V2_1 = 3;

        // ------------------------------------------------------------------ queries

        public static bool IsRegistered()
        {
            try
            {
                dynamic svc = ConnectCom();
                return TryGetTask(svc) != null;
            }
            catch (Exception ex)
            {
                Log.Warn("COM으로 작업 조회 실패, schtasks로 재시도: " + ex.Message);
                return RunSchtasks("/Query /TN \"" + TaskName + "\"").ExitCode == 0;
            }
        }

        /// <summary>The exe path the registered task points at, or null.</summary>
        public static string RegisteredCommand()
        {
            try
            {
                dynamic svc = ConnectCom();
                dynamic task = TryGetTask(svc);
                if (task == null) return null;
                foreach (dynamic action in task.Definition.Actions)
                {
                    try
                    {
                        string path = action.Path;
                        if (!string.IsNullOrEmpty(path)) return path.Trim().Trim('"');
                    }
                    catch { }
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("COM으로 작업 명령 조회 실패, schtasks로 재시도: " + ex.Message);
                return RegisteredCommandViaSchtasks();
            }
        }

        public static bool IsRegisteredForThisExe()
        {
            var cmd = RegisteredCommand();
            if (cmd == null) return false;
            try { return string.Equals(Path.GetFullPath(cmd), Path.GetFullPath(AppPaths.ExePath), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // ------------------------------------------------------------------ register / unregister / run

        /// <summary>Creates or replaces the task. Requires elevation.</summary>
        public static void Register()
        {
            string sid, accountName;
            using (var id = WindowsIdentity.GetCurrent())
            {
                sid = id.User?.Value ?? id.Name;
                accountName = id.Name;
            }
            var exe = AppPaths.ExePath;

            Exception comError;
            try
            {
                RegisterViaCom(exe, sid, accountName);
                Log.Info("작업 스케줄러에 등록(COM): " + exe);
                return;
            }
            catch (Exception ex)
            {
                comError = ex;
                Log.Warn("COM 등록 실패, schtasks XML로 재시도: " + Describe(ex));
            }

            try
            {
                RegisterViaSchtasks(exe, sid, accountName);
                Log.Info("작업 스케줄러에 등록(schtasks): " + exe);
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException("작업 스케줄러 등록 실패. COM: " + Describe(comError) + " / schtasks: " + ex2.Message);
            }
        }

        public static void Unregister()
        {
            try
            {
                dynamic svc = ConnectCom();
                dynamic root = svc.GetFolder("\\");
                if (TryGetTask(svc) != null) root.DeleteTask(TaskName, 0);
                Log.Info("작업 스케줄러 등록 해제(COM)");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("COM 삭제 실패, schtasks로 재시도: " + Describe(ex));
            }
            var r = RunSchtasks("/Delete /TN \"" + TaskName + "\" /F");
            if (r.ExitCode != 0 && IsRegistered())
                throw new InvalidOperationException("작업 스케줄러 삭제 실패 (코드 " + r.ExitCode + "): " + (r.StdErr + " " + r.StdOut).Trim());
            Log.Info("작업 스케줄러 등록 해제(schtasks)");
        }

        /// <summary>Starts the registered task (elevated instance). Works from a non-elevated process of the same user.</summary>
        public static bool RunTask()
        {
            try
            {
                dynamic svc = ConnectCom();
                dynamic task = TryGetTask(svc);
                if (task != null)
                {
                    task.Run(null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("COM으로 작업 실행 실패, schtasks로 재시도: " + Describe(ex));
            }
            var r = RunSchtasks("/Run /TN \"" + TaskName + "\"");
            if (r.ExitCode != 0) Log.Warn("schtasks /Run 실패: " + (r.StdErr + " " + r.StdOut).Trim());
            return r.ExitCode == 0;
        }

        // ------------------------------------------------------------------ COM implementation

        private static dynamic ConnectCom()
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true);
            dynamic svc = Activator.CreateInstance(type);
            svc.Connect();
            return svc;
        }

        private static dynamic TryGetTask(dynamic svc)
        {
            try
            {
                dynamic root = svc.GetFolder("\\");
                return root.GetTask(TaskName);
            }
            catch (FileNotFoundException) { return null; }      // HRESULT 0x80070002 surfaces as this
            catch (DirectoryNotFoundException) { return null; }
            catch (COMException ex) when ((uint)ex.ErrorCode == 0x80070002) { return null; }
        }

        private static void RegisterViaCom(string exe, string sid, string accountName)
        {
            dynamic svc = ConnectCom();
            dynamic root = svc.GetFolder("\\");
            dynamic td = svc.NewTask(0);

            td.RegistrationInfo.Author = "NetSwitch";
            td.RegistrationInfo.Description = "인터넷(와이파이)/업무망(유선 랜) 전환기를 로그온 시 관리자 권한으로 시작합니다.";

            dynamic principal = td.Principal;
            principal.Id = "Author";
            principal.UserId = sid;
            principal.LogonType = TASK_LOGON_INTERACTIVE_TOKEN;
            principal.RunLevel = TASK_RUNLEVEL_HIGHEST;

            dynamic s = td.Settings;
            s.Compatibility = TASK_COMPATIBILITY_V2_1;
            s.MultipleInstances = TASK_INSTANCES_IGNORE_NEW;
            s.DisallowStartIfOnBatteries = false;
            s.StopIfGoingOnBatteries = false;
            s.AllowHardTerminate = false;
            s.StartWhenAvailable = true;
            s.RunOnlyIfNetworkAvailable = false;
            s.AllowDemandStart = true;
            s.Enabled = true;
            s.Hidden = false;
            s.RunOnlyIfIdle = false;
            s.WakeToRun = false;
            s.ExecutionTimeLimit = "PT0S";       // no time limit
            s.Priority = 5;
            try { s.IdleSettings.StopOnIdleEnd = false; s.IdleSettings.RestartOnIdle = false; } catch { }
            try { s.DisallowStartOnRemoteAppSession = false; } catch { }

            dynamic trigger = td.Triggers.Create(TASK_TRIGGER_LOGON);
            trigger.Id = "LogonTrigger";
            trigger.Enabled = true;
            trigger.Delay = "PT5S";
            try { trigger.UserId = accountName; }
            catch (Exception ex) { Log.Warn("로그온 트리거 사용자 지정 실패(모든 사용자 로그온으로 등록): " + ex.Message); }

            dynamic action = td.Actions.Create(TASK_ACTION_EXEC);
            action.Id = "Exec";
            action.Path = exe;
            action.Arguments = "--from-task";
            action.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";

            root.RegisterTaskDefinition(TaskName, td, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
        }

        // ------------------------------------------------------------------ schtasks fallback

        private static string RegisteredCommandViaSchtasks()
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

        private static void RegisterViaSchtasks(string exe, string sid, string accountName)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "NetSwitch-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(tmp, BuildTaskXml(exe, sid, accountName, "UTF-16"), new UnicodeEncoding(false, true));
                var r = RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
                if (r.ExitCode != 0)
                {
                    Log.Warn("UTF-16 작업 XML 등록 실패 (코드 " + r.ExitCode + "): " + (r.StdErr + " " + r.StdOut).Trim() + " -> UTF-8로 재시도");
                    File.WriteAllText(tmp, BuildTaskXml(exe, sid, accountName, "UTF-8"), new UTF8Encoding(false));
                    r = RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
                }
                if (r.ExitCode != 0)
                    throw new InvalidOperationException("(코드 " + r.ExitCode + ") " + (r.StdErr + " " + r.StdOut).Trim());
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        private static string BuildTaskXml(string exePath, string sid, string accountName, string encodingName)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"").Append(encodingName).Append("\"?>\r\n");
            sb.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n");
            sb.Append("  <RegistrationInfo>\r\n");
            sb.Append("    <Author>NetSwitch</Author>\r\n");
            sb.Append("    <Description>NetSwitch autostart (elevated)</Description>\r\n");
            sb.Append("  </RegistrationInfo>\r\n");
            sb.Append("  <Triggers>\r\n");
            sb.Append("    <LogonTrigger>\r\n");
            sb.Append("      <Enabled>true</Enabled>\r\n");
            sb.Append("      <UserId>").Append(Esc(accountName)).Append("</UserId>\r\n");
            sb.Append("      <Delay>PT5S</Delay>\r\n");
            sb.Append("    </LogonTrigger>\r\n");
            sb.Append("  </Triggers>\r\n");
            sb.Append("  <Principals>\r\n");
            sb.Append("    <Principal id=\"Author\">\r\n");
            sb.Append("      <UserId>").Append(Esc(sid)).Append("</UserId>\r\n");
            sb.Append("      <LogonType>InteractiveToken</LogonType>\r\n");
            sb.Append("      <RunLevel>HighestAvailable</RunLevel>\r\n");
            sb.Append("    </Principal>\r\n");
            sb.Append("  </Principals>\r\n");
            sb.Append("  <Settings>\r\n");
            sb.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n");
            sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n");
            sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n");
            sb.Append("    <AllowHardTerminate>false</AllowHardTerminate>\r\n");
            sb.Append("    <StartWhenAvailable>true</StartWhenAvailable>\r\n");
            sb.Append("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n");
            sb.Append("    <IdleSettings>\r\n");
            sb.Append("      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n");
            sb.Append("      <RestartOnIdle>false</RestartOnIdle>\r\n");
            sb.Append("    </IdleSettings>\r\n");
            sb.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n");
            sb.Append("    <Enabled>true</Enabled>\r\n");
            sb.Append("    <Hidden>false</Hidden>\r\n");
            sb.Append("    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n");
            sb.Append("    <WakeToRun>false</WakeToRun>\r\n");
            sb.Append("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n");
            sb.Append("    <Priority>5</Priority>\r\n");
            sb.Append("  </Settings>\r\n");
            sb.Append("  <Actions Context=\"Author\">\r\n");
            sb.Append("    <Exec>\r\n");
            sb.Append("      <Command>").Append(Esc(exePath)).Append("</Command>\r\n");
            sb.Append("      <Arguments>--from-task</Arguments>\r\n");
            sb.Append("      <WorkingDirectory>").Append(Esc(Path.GetDirectoryName(exePath) ?? "")).Append("</WorkingDirectory>\r\n");
            sb.Append("    </Exec>\r\n");
            sb.Append("  </Actions>\r\n");
            sb.Append("</Task>\r\n");
            return sb.ToString();
        }

        private static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "");

        private static string Describe(Exception ex)
        {
            if (ex == null) return "(null)";
            var com = ex as COMException;
            return ex.Message + (com != null ? " (HRESULT 0x" + ((uint)com.ErrorCode).ToString("X8") + ")" : "");
        }

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
