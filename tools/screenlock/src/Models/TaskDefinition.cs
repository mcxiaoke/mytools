using System;
using System.Collections.Generic;

namespace ScreenLock.Models
{
    public enum TaskTriggerType
    {
        Startup,
        Interval,
        Daily,
        Cron,
        SessionLock,
        SessionUnlock,
        Idle,
        Manual,
        Hotkey,
        Watch
    }

    public class TaskTrigger
    {
        public TaskTriggerType Type { get; set; } = TaskTriggerType.Startup;
        public int DelaySec { get; set; } = 5;
        public int EverySec { get; set; } = 0;
        public string Every { get; set; } = "";
        public string At { get; set; } = "";
        public string Expr { get; set; } = "";
        public int AfterMinutes { get; set; } = 0;
        public string Hotkey { get; set; } = "";
        public string WatchPath { get; set; } = "";
        public string WatchFilter { get; set; } = "*.*";
        public string WatchEvent { get; set; } = "created";

        public string RawType { get; set; } = "";
    }

    public class TaskCondition
    {
        public bool OnlyIdle { get; set; } = false;
        public bool AcPower { get; set; } = false;
        public string FileExists { get; set; } = "";
        public string FileNotExists { get; set; } = "";
        public bool NetworkAvailable { get; set; } = false;

        public bool HasAny()
        {
            return OnlyIdle || AcPower || !string.IsNullOrWhiteSpace(FileExists) || !string.IsNullOrWhiteSpace(FileNotExists) || NetworkAvailable;
        }
    }

    public class TaskAction
    {
        public string File { get; set; } = "";
        public string Args { get; set; } = "";
        public string WorkDir { get; set; } = "";
    }

    public class TaskOptions
    {
        public bool Hidden { get; set; } = true;
        public int TimeoutSec { get; set; } = 0;
        public bool AllowConcurrent { get; set; } = false;
        public int Retry { get; set; } = 0;
        public string WorkDir { get; set; } = "";
        public bool NotifyOnFailure { get; set; } = true;
    }

    public class TaskDefinition
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public TaskTrigger Trigger { get; set; } = new TaskTrigger();
        public TaskAction Action { get; set; } = new TaskAction();
        public TaskOptions Options { get; set; } = new TaskOptions();
        public TaskCondition When { get; set; } = new TaskCondition();

        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) return "name required";
            if (Name.Length > 64) return "name too long";
            foreach (char c in Name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok) return "name invalid char: " + c;
            }
            if (Trigger == null) return "trigger required";
            if (Action == null || string.IsNullOrWhiteSpace(Action.File)) return "action.file required";
            if (Trigger.Type == TaskTriggerType.Interval)
            {
                int sec = Trigger.EverySec;
                if (sec <= 0 && !string.IsNullOrWhiteSpace(Trigger.Every))
                    sec = ParseDuration(Trigger.Every);
                if (sec <= 0) return "interval everySec/every required and >0";
            }
            if (Trigger.Type == TaskTriggerType.Daily)
            {
                if (string.IsNullOrWhiteSpace(Trigger.At)) return "daily at required (HH:mm)";
                TimeSpan t;
                if (!TryParseTime(Trigger.At, out t)) return "daily at invalid: " + Trigger.At;
            }
            if (Trigger.Type == TaskTriggerType.Cron)
            {
                if (string.IsNullOrWhiteSpace(Trigger.Expr)) return "cron expr required";
                string err;
                if (!CronHelper.Validate(Trigger.Expr, out err)) return "cron invalid: " + err;
            }
            if (Trigger.Type == TaskTriggerType.Idle)
            {
                if (Trigger.AfterMinutes <= 0) return "idle afterMinutes required and >0";
            }
            if (Trigger.Type == TaskTriggerType.Hotkey)
            {
                if (string.IsNullOrWhiteSpace(Trigger.Hotkey)) return "hotkey required (e.g. Ctrl+Alt+Q)";
                string err;
                if (!HotkeyHelper.Validate(Trigger.Hotkey, out err)) return "hotkey invalid: " + err;
            }
            if (Trigger.Type == TaskTriggerType.Watch)
            {
                if (string.IsNullOrWhiteSpace(Trigger.WatchPath)) return "watch path required";
            }
            if (Options != null && Options.TimeoutSec < 0) return "timeoutSec invalid";
            if (Options != null && Options.Retry < 0) return "retry invalid";
            return null;
        }

        public static int ParseDuration(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            text = text.Trim().ToLowerInvariant();
            int total = 0;
            int num = 0;
            bool hasNum = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '0' && c <= '9')
                {
                    num = num * 10 + (c - '0');
                    hasNum = true;
                }
                else if (c == 'h')
                {
                    if (!hasNum) return 0;
                    total += num * 3600;
                    num = 0; hasNum = false;
                }
                else if (c == 'm')
                {
                    if (!hasNum) return 0;
                    // check for "ms" ? not needed
                    total += num * 60;
                    num = 0; hasNum = false;
                }
                else if (c == 's')
                {
                    if (!hasNum) return 0;
                    total += num;
                    num = 0; hasNum = false;
                }
                else if (c == ' ' || c == '\t')
                {
                    continue;
                }
                else
                {
                    return 0;
                }
            }
            if (hasNum) total += num; // bare number as seconds
            return total;
        }

        public static bool TryParseTime(string text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            string[] parts = text.Split(':');
            if (parts.Length < 2 || parts.Length > 3) return false;
            int h, m, s = 0;
            if (!int.TryParse(parts[0], out h)) return false;
            if (!int.TryParse(parts[1], out m)) return false;
            if (parts.Length == 3 && !int.TryParse(parts[2], out s)) return false;
            if (h < 0 || h > 23) return false;
            if (m < 0 || m > 59) return false;
            if (s < 0 || s > 59) return false;
            time = new TimeSpan(h, m, s);
            return true;
        }

        public int GetIntervalSeconds()
        {
            if (Trigger == null) return 0;
            if (Trigger.EverySec > 0) return Trigger.EverySec;
            if (!string.IsNullOrWhiteSpace(Trigger.Every)) return ParseDuration(Trigger.Every);
            return 0;
        }

        public string EffectiveWorkDir()
        {
            if (Options != null && !string.IsNullOrWhiteSpace(Options.WorkDir)) return Options.WorkDir;
            if (Action != null && !string.IsNullOrWhiteSpace(Action.WorkDir)) return Action.WorkDir;
            if (Action != null && !string.IsNullOrWhiteSpace(Action.File))
            {
                try { return System.IO.Path.GetDirectoryName(Action.File); } catch { }
            }
            return "";
        }
    }

    internal static class CronHelper
    {
        public static bool Validate(string expr, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(expr)) { error = "empty"; return false; }
            var parts = expr.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) { error = "need 5 fields (m h dom mon dow)"; return false; }
            // minute 0-59, hour 0-23, dom 1-31, mon 1-12, dow 0-7 (0/7 Sunday)
            int[][] ranges = new int[][] { new int[]{0,59}, new int[]{0,23}, new int[]{1,31}, new int[]{1,12}, new int[]{0,7} };
            for (int i = 0; i < 5; i++)
            {
                string e;
                if (!ValidateField(parts[i], ranges[i][0], ranges[i][1], out e)) { error = "field " + (i+1) + ": " + e; return false; }
            }
            return true;
        }

        private static bool ValidateField(string field, int min, int max, out string error)
        {
            error = null;
            if (field == "*" || field == "?") return true;
            var tokens = field.Split(',');
            foreach (var token in tokens)
            {
                string t = token.Trim();
                if (string.IsNullOrEmpty(t)) { error = "empty token"; return false; }
                string rangePart = t;
                string stepPart = null;
                int slash = t.IndexOf('/');
                if (slash >= 0)
                {
                    rangePart = t.Substring(0, slash);
                    stepPart = t.Substring(slash + 1);
                    int step;
                    if (!int.TryParse(stepPart, out step) || step <= 0) { error = "bad step " + stepPart; return false; }
                }
                if (rangePart == "*" || rangePart == "") continue;
                if (rangePart.Contains("-"))
                {
                    var rp = rangePart.Split('-');
                    if (rp.Length != 2) { error = "bad range " + rangePart; return false; }
                    int a,b;
                    if (!int.TryParse(rp[0], out a) || !int.TryParse(rp[1], out b)) { error = "bad range number"; return false; }
                    if (a < min || b > max || a > b) { error = "range out of bounds"; return false; }
                }
                else
                {
                    int v;
                    if (!int.TryParse(rangePart, out v)) { error = "bad value " + rangePart; return false; }
                    if (v < min || v > max) { error = "value out of range"; return false; }
                }
            }
            return true;
        }

        public static bool IsMatch(DateTime dt, string expr)
        {
            var parts = expr.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) return false;
            int[] vals = new int[] { dt.Minute, dt.Hour, dt.Day, dt.Month, (int)dt.DayOfWeek };
            // cron dow: 0 and 7 both Sunday
            if (vals[4] == 0) { /* keep 0 */ }
            int[][] ranges = new int[][] { new int[]{0,59}, new int[]{0,23}, new int[]{1,31}, new int[]{1,12}, new int[]{0,7} };
            for (int i = 0; i < 5; i++)
            {
                if (!FieldMatches(parts[i], vals[i], ranges[i][0], ranges[i][1])) return false;
            }
            return true;
        }

        private static bool FieldMatches(string field, int value, int min, int max)
        {
            if (field == "*" || field == "?") return true;
            var tokens = field.Split(',');
            foreach (var token in tokens)
            {
                string t = token.Trim();
                string rangePart = t;
                int step = 1;
                int slash = t.IndexOf('/');
                if (slash >= 0)
                {
                    rangePart = t.Substring(0, slash);
                    int.TryParse(t.Substring(slash + 1), out step);
                    if (step <= 0) step = 1;
                }
                if (rangePart == "*" || rangePart == "")
                {
                    if ((value - min) % step == 0) return true;
                    continue;
                }
                if (rangePart.Contains("-"))
                {
                    var rp = rangePart.Split('-');
                    int a = int.Parse(rp[0]);
                    int b = int.Parse(rp[1]);
                    if (value >= a && value <= b && ((value - a) % step == 0)) return true;
                }
                else
                {
                    int v = int.Parse(rangePart);
                    // handle dow 7 == 0
                    if (max == 7 && v == 7) v = 0;
                    int cmp = value;
                    if (max == 7 && cmp == 7) cmp = 0;
                    if (cmp == v) return true;
                }
            }
            return false;
        }
    }

    internal static class HotkeyHelper
    {
        public static bool Validate(string hotkey, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(hotkey)) { error = "empty"; return false; }
            int mods;
            int vk;
            if (!TryParse(hotkey, out mods, out vk, out error)) return false;
            if (vk == 0) { error = "no key"; return false; }
            return true;
        }

        public static bool TryParse(string hotkey, out int mods, out int vk, out string error)
        {
            mods = 0; vk = 0; error = null;
            if (string.IsNullOrWhiteSpace(hotkey)) { error = "empty"; return false; }
            // format: Ctrl+Alt+Shift+Win+Key  (case insensitive, + or - separator)
            string s = hotkey.Trim();
            s = s.Replace("-", "+");
            var parts = s.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { error = "empty"; return false; }
            string keyPart = parts[parts.Length - 1].Trim();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string m = parts[i].Trim().ToLowerInvariant();
                if (m == "ctrl" || m == "control") mods |= 0x0002;
                else if (m == "alt") mods |= 0x0001;
                else if (m == "shift") mods |= 0x0004;
                else if (m == "win" || m == "windows" || m == "meta") mods |= 0x0008;
                else { error = "unknown modifier " + parts[i]; return false; }
            }
            // key: single char A-Z, 0-9, F1-24, or names like Space, Enter, Esc
            string k = keyPart.ToUpperInvariant();
            if (k.Length == 1)
            {
                char c = k[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    vk = (int)c;
                    return true;
                }
            }
            // try function keys
            if (k.StartsWith("F"))
            {
                int fn;
                if (int.TryParse(k.Substring(1), out fn) && fn >= 1 && fn <= 24)
                {
                    vk = 0x70 + (fn - 1); // VK_F1=0x70
                    return true;
                }
            }
            // named keys
            switch (k)
            {
                case "SPACE": vk = 0x20; return true;
                case "ENTER": case "RETURN": vk = 0x0D; return true;
                case "ESC": case "ESCAPE": vk = 0x1B; return true;
                case "TAB": vk = 0x09; return true;
                case "BACKSPACE": case "BACK": vk = 0x08; return true;
                case "INS": case "INSERT": vk = 0x2D; return true;
                case "DEL": case "DELETE": vk = 0x2E; return true;
                case "HOME": vk = 0x24; return true;
                case "END": vk = 0x23; return true;
                case "PGUP": case "PAGEUP": vk = 0x21; return true;
                case "PGDN": case "PAGEDOWN": vk = 0x22; return true;
                case "LEFT": vk = 0x25; return true;
                case "UP": vk = 0x26; return true;
                case "RIGHT": vk = 0x27; return true;
                case "DOWN": vk = 0x28; return true;
                default: error = "unknown key " + keyPart; return false;
            }
        }
    }
}
