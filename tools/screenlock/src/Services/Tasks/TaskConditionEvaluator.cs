using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks
{
    internal static class TaskConditionEvaluator
    {
        public static bool ShouldRun(TaskDefinition task, out string skipReason)
        {
            skipReason = null;
            if (task == null || task.When == null || !task.When.HasAny()) return true;
            var c = task.When;

            if (c.OnlyIdle)
            {
                try
                {
                    uint idleMs = IdleDetector.GetIdleMilliseconds();
                    // consider idle if > 60s without input
                    if (idleMs < 60 * 1000)
                    {
                        skipReason = "when.onlyIdle: idle " + (idleMs / 1000) + "s < 60s";
                        return false;
                    }
                }
                catch { }
            }

            if (c.AcPower)
            {
                try
                {
                    var status = SystemInformation.PowerStatus;
                    if (status.PowerLineStatus != PowerLineStatus.Online)
                    {
                        skipReason = "when.acPower: not on AC";
                        return false;
                    }
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(c.FileExists))
            {
                string p = Expand(c.FileExists);
                bool exists = File.Exists(p) || Directory.Exists(p);
                if (!exists)
                {
                    skipReason = "when.fileExists: not found " + p;
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(c.FileNotExists))
            {
                string p = Expand(c.FileNotExists);
                bool exists = File.Exists(p) || Directory.Exists(p);
                if (exists)
                {
                    skipReason = "when.fileNotExists: exists " + p;
                    return false;
                }
            }

            if (c.NetworkAvailable)
            {
                try
                {
                    if (!NetworkInterface.GetIsNetworkAvailable())
                    {
                        skipReason = "when.networkAvailable: no network";
                        return false;
                    }
                }
                catch { }
            }

            return true;
        }

        private static string Expand(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try { path = Environment.ExpandEnvironmentVariables(path); } catch { }
            // also resolve scripts path if bare name
            try
            {
                if (!Path.IsPathRooted(path) && !path.Contains(":"))
                {
                    // try scripts dir
                    string cand = Path.Combine(ConfigService.ScriptsDirPath, path);
                    if (File.Exists(cand) || Directory.Exists(cand)) return cand;
                }
            }
            catch { }
            return path;
        }
    }
}
