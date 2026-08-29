using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks
{
    public static class TaskRunner
    {
        public static async Task<int> RunAsync(TaskDefinition task, string reason)
        {
            if (task == null) return -1;
            string workDir = task.EffectiveWorkDir();
            string file = task.Action != null ? task.Action.File : "";
            string args = task.Action != null ? task.Action.Args : "";
            bool hidden = task.Options != null ? task.Options.Hidden : true;
            int timeoutSec = task.Options != null ? task.Options.TimeoutSec : 0;

            // expand env vars + template vars {{date}} etc in file/args/workDir
            try
            {
                if (!string.IsNullOrEmpty(file)) file = Environment.ExpandEnvironmentVariables(file);
                if (!string.IsNullOrEmpty(args)) args = Environment.ExpandEnvironmentVariables(args);
                if (!string.IsNullOrEmpty(workDir)) workDir = Environment.ExpandEnvironmentVariables(workDir);
                // template expansion
                file = TemplateExpander.Expand(file, task);
                args = TemplateExpander.Expand(args, task);
                workDir = TemplateExpander.Expand(workDir, task);
            }
            catch { }

            // resolve script path via scripts/ subdir (portable: exe/scripts, non-portable: %AppData%/ScreenLock/scripts)
            try { ScriptResolver.EnsureScriptsDir(); } catch { }
            string resolvedFile = file;
            try
            {
                // only resolve if file looks like script or is bare name without dir
                string extCheck = "";
                try { extCheck = Path.GetExtension(file).ToLowerInvariant(); } catch { }
                bool isScript = ScriptResolver.IsScriptExtension(extCheck);
                bool hasDir = false;
                try { hasDir = !string.IsNullOrEmpty(Path.GetDirectoryName(file)); } catch { }
                bool isRooted = false;
                try { isRooted = Path.IsPathRooted(file); } catch { }
                // if bare name (no dir) or script extension without absolute path, try scripts dir
                if (!isRooted && (isScript || !hasDir))
                {
                    string r = ScriptResolver.ResolveScriptPath(file);
                    // use resolved if it points inside scripts or file exists
                    if (!string.Equals(r, file, StringComparison.OrdinalIgnoreCase) || File.Exists(r))
                        resolvedFile = r;
                }
                else if (isScript && !isRooted)
                {
                    string r = ScriptResolver.ResolveScriptPath(file);
                    if (File.Exists(r)) resolvedFile = r;
                }
                // if file is absolute but not found, keep as is for error logging
            }
            catch { }
            file = resolvedFile;

            // resolve workDir fallback
            if (string.IsNullOrWhiteSpace(workDir) && !string.IsNullOrWhiteSpace(file))
            {
                try { workDir = Path.GetDirectoryName(Path.GetFullPath(file)); } catch { workDir = ""; }
            }
            if (!string.IsNullOrWhiteSpace(workDir) && !Directory.Exists(workDir))
            {
                try { Directory.CreateDirectory(workDir); } catch { }
            }
            if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
                workDir = ConfigService.DirPath;

            // script wrapping - all scripts default hidden (no black window)
            string ext = "";
            try { ext = Path.GetExtension(file).ToLowerInvariant(); } catch { }
            string realFile = file;
            string realArgs = args;
            if (ext == ".ps1")
            {
                realFile = "powershell.exe";
                realArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\" " + args;
            }
            else if (ext == ".psm1")
            {
                realFile = "powershell.exe";
                realArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\" " + args;
            }
            else if (ext == ".bat" || ext == ".cmd")
            {
                realFile = "cmd.exe";
                realArgs = "/c \"" + file + "\" " + args;
            }
            else if (ext == ".vbs")
            {
                realFile = "wscript.exe";
                realArgs = "\"" + file + "\" " + args;
            }
            else if (ext == ".js")
            {
                string node = ScriptResolver.FindNode();
                realFile = node;
                realArgs = "\"" + file + "\" " + args;
            }
            else if (ext == ".py" || ext == ".pyw")
            {
                string py = ScriptResolver.FindPython();
                realFile = py;
                // for py.exe launcher, -u for unbuffered
                if (py.ToLowerInvariant().EndsWith("py.exe"))
                    realArgs = "\"" + file + "\" " + args;
                else
                    realArgs = "-u \"" + file + "\" " + args;
            }

            var psi = new ProcessStartInfo
            {
                FileName = realFile,
                Arguments = realArgs,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var sw = Stopwatch.StartNew();
            TaskLogger.Info(task.Name, "triggered(" + reason + ") -> starting: " + psi.FileName + " " + psi.Arguments + " [workDir=" + workDir + "]");

            Process proc = null;
            int exitCode = -1;
            try
            {
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();

                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    stdout.AppendLine(e.Data);
                    TaskLogger.WriteOutput(task.Name, "OUT", e.Data);
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    stderr.AppendLine(e.Data);
                    TaskLogger.WriteOutput(task.Name, "ERR", e.Data);
                };

                if (!proc.Start())
                {
                    TaskLogger.Error(task.Name, "failed to start process");
                    return -1;
                }

                int pid = -1;
                try { pid = proc.Id; } catch { }
                TaskLogger.Info(task.Name, "started pid=" + pid);

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                bool exited;
                if (timeoutSec > 0)
                {
                    var cts = new CancellationTokenSource();
                    var waitTask = Task.Run(() => proc.WaitForExit(), cts.Token);
                    var delay = Task.Delay(timeoutSec * 1000, cts.Token);
                    var completed = await Task.WhenAny(waitTask, delay).ConfigureAwait(false);
                    exited = completed == waitTask;
                    if (!exited)
                    {
                        TaskLogger.Warn(task.Name, "timeout after " + timeoutSec + "s, killing pid=" + pid);
                        try { KillTree(proc); } catch { }
                        // wait a bit for kill
                        try { proc.WaitForExit(5000); } catch { }
                        TaskLogger.Error(task.Name, "killed on timeout, duration=" + sw.Elapsed);
                        return -1;
                    }
                    else
                    {
                        cts.Cancel();
                    }
                }
                else
                {
                    await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);
                    exited = true;
                }

                // ensure async output drained
                await Task.Delay(100).ConfigureAwait(false);
                try { exitCode = proc.HasExited ? proc.ExitCode : -1; } catch { }

                sw.Stop();
                string outText = stdout.ToString();
                string errText = stderr.ToString();
                // already logged line by line, just summary
                TaskLogger.Info(task.Name, string.Format("finished pid={0} exitCode={1} duration={2:0.0}s", pid, exitCode, sw.Elapsed.TotalSeconds));
                if (exitCode != 0)
                {
                    TaskLogger.Warn(task.Name, "non-zero exitCode=" + exitCode);
                }
                return exitCode;
            }
            catch (Exception ex)
            {
                sw.Stop();
                TaskLogger.Error(task.Name, "exception: " + ex + " duration=" + sw.Elapsed);
                return -1;
            }
            finally
            {
                try { if (proc != null) proc.Dispose(); } catch { }
            }
        }

        private static void KillTree(Process proc)
        {
            try
            {
                // try kill children via WMI, fallback to Kill
                int pid = proc.Id;
                try
                {
                    // kill child processes first
                    var searcherCmd = "taskkill /PID " + pid + " /T /F";
                    var psi = new ProcessStartInfo("cmd.exe", "/c " + searcherCmd)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(5000); }
                }
                catch { }
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
            catch { }
        }
    }
}
