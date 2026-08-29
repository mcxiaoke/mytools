using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ScreenLock.Models;
using SimpleJSON;

namespace ScreenLock.Services.Tasks
{
    public class TaskLoadResult
    {
        public List<TaskDefinition> Tasks { get; set; } = new List<TaskDefinition>();
        public List<string> Errors { get; set; } = new List<string>();
        public bool FileCreated { get; set; }
        public string RawJson { get; set; }
    }

    public static class TaskConfigService
    {
        public static string FilePath
        {
            get { return ConfigService.TaskFilePath; }
        }

        public static string SampleFilePath
        {
            get { return Path.Combine(ConfigService.DirPath, "tasks.sample.json"); }
        }

        public static TaskLoadResult LoadOrCreate()
        {
            var result = new TaskLoadResult();
            try
            {
                string dir = ConfigService.DirPath;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string logsDir = ConfigService.LogsDirPath;
                if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
                string scriptsDir = ConfigService.ScriptsDirPath;
                if (!Directory.Exists(scriptsDir)) Directory.CreateDirectory(scriptsDir);
            }
            catch { }

            if (!File.Exists(FilePath))
            {
                try
                {
                    string sample = BuildSampleJson();
                    File.WriteAllText(FilePath, sample, Encoding.UTF8);
                    // also write sample file for reference
                    try { File.WriteAllText(SampleFilePath, sample, Encoding.UTF8); } catch { }
                    result.FileCreated = true;
                }
                catch (Exception ex)
                {
                    result.Errors.Add("failed to create tasks.json: " + ex.Message);
                    return result;
                }
            }

            return Load();
        }

        public static TaskLoadResult Load()
        {
            var result = new TaskLoadResult();
            try
            {
                if (!File.Exists(FilePath))
                {
                    result.Errors.Add("tasks.json not found: " + FilePath);
                    return result;
                }
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                result.RawJson = json;
                if (string.IsNullOrWhiteSpace(json))
                {
                    return result;
                }
                var tasks = ParseTasksJson(json, result.Errors);
                // dedup by name
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in tasks)
                {
                    string err = t.Validate();
                    if (err != null)
                    {
                        result.Errors.Add("task [" + (t.Name ?? "?") + "] invalid: " + err);
                        continue;
                    }
                    if (seen.Contains(t.Name))
                    {
                        result.Errors.Add("duplicate task name: " + t.Name + " (skipped)");
                        continue;
                    }
                    seen.Add(t.Name);
                    result.Tasks.Add(t);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add("load exception: " + ex.Message);
            }
            return result;
        }

        public static bool Reload(out TaskLoadResult result)
        {
            result = Load();
            return result.Errors.Count == 0;
        }

        public static void Save(List<TaskDefinition> tasks)
        {
            if (tasks == null) tasks = new List<TaskDefinition>();
            var dir = ConfigService.DirPath;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                sb.AppendLine("  {");
                sb.AppendLine("    \"name\": \"" + Escape(t.Name) + "\",");
                sb.AppendLine("    \"enabled\": " + (t.Enabled ? "true" : "false") + ",");
                sb.Append("    \"trigger\": ");
                sb.Append(SerializeTrigger(t.Trigger));
                sb.AppendLine(",");
                sb.Append("    \"action\": ");
                sb.Append(SerializeAction(t.Action));
                sb.AppendLine(",");
                sb.Append("    \"options\": ");
                sb.Append(SerializeOptions(t.Options));
                if (t.When != null && t.When.HasAny())
                {
                    sb.AppendLine(",");
                    sb.Append("    \"when\": ");
                    sb.Append(SerializeWhen(t.When));
                }
                sb.AppendLine();
                sb.Append("  }");
                if (i < tasks.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("]");
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }

        private static string SerializeTrigger(TaskTrigger tr)
        {
            if (tr == null) return "{}";
            var sb = new StringBuilder();
            sb.Append("{ \"type\": \"" + Escape(tr.RawType != "" ? tr.RawType : tr.Type.ToString().ToLowerInvariant()) + "\"");
            switch (tr.Type)
            {
                case TaskTriggerType.Startup: sb.Append(", \"delaySec\": " + tr.DelaySec); break;
                case TaskTriggerType.Interval:
                    if (!string.IsNullOrWhiteSpace(tr.Every)) sb.Append(", \"every\": \"" + Escape(tr.Every) + "\"");
                    else sb.Append(", \"everySec\": " + tr.EverySec);
                    break;
                case TaskTriggerType.Daily: sb.Append(", \"at\": \"" + Escape(tr.At) + "\""); break;
                case TaskTriggerType.Cron: sb.Append(", \"expr\": \"" + Escape(tr.Expr) + "\""); break;
                case TaskTriggerType.Idle: sb.Append(", \"afterMinutes\": " + tr.AfterMinutes); break;
                case TaskTriggerType.Hotkey: sb.Append(", \"hotkey\": \"" + Escape(tr.Hotkey) + "\""); break;
                case TaskTriggerType.Watch:
                    sb.Append(", \"path\": \"" + Escape(tr.WatchPath) + "\"");
                    if (!string.IsNullOrWhiteSpace(tr.WatchFilter) && tr.WatchFilter != "*.*") sb.Append(", \"filter\": \"" + Escape(tr.WatchFilter) + "\"");
                    if (!string.IsNullOrWhiteSpace(tr.WatchEvent) && tr.WatchEvent != "created") sb.Append(", \"event\": \"" + Escape(tr.WatchEvent) + "\"");
                    break;
            }
            sb.Append(" }");
            return sb.ToString();
        }

        private static string SerializeAction(TaskAction ac)
        {
            if (ac == null) return "{}";
            var sb = new StringBuilder();
            sb.Append("{ \"file\": \"" + Escape(ac.File ?? "") + "\"");
            if (!string.IsNullOrWhiteSpace(ac.Args)) sb.Append(", \"args\": \"" + Escape(ac.Args) + "\"");
            if (!string.IsNullOrWhiteSpace(ac.WorkDir)) sb.Append(", \"workDir\": \"" + Escape(ac.WorkDir) + "\"");
            sb.Append(" }");
            return sb.ToString();
        }

        private static string SerializeOptions(TaskOptions op)
        {
            if (op == null) return "{}";
            var sb = new StringBuilder();
            sb.Append("{ \"hidden\": " + (op.Hidden ? "true" : "false"));
            if (op.TimeoutSec != 0) sb.Append(", \"timeoutSec\": " + op.TimeoutSec);
            if (op.AllowConcurrent) sb.Append(", \"allowConcurrent\": true");
            if (op.Retry != 0) sb.Append(", \"retry\": " + op.Retry);
            if (!op.NotifyOnFailure) sb.Append(", \"notifyOnFailure\": false");
            if (!string.IsNullOrWhiteSpace(op.WorkDir)) sb.Append(", \"workDir\": \"" + Escape(op.WorkDir) + "\"");
            sb.Append(" }");
            return sb.ToString();
        }

        private static string SerializeWhen(TaskCondition w)
        {
            if (w == null) return "{}";
            var sb = new StringBuilder();
            sb.Append("{");
            bool first = true;
            Action<string, string> add = (k, v) => { if (!first) sb.Append(", "); sb.Append("\"" + k + "\": " + v); first = false; };
            if (w.OnlyIdle) add("onlyIdle", "true");
            if (w.AcPower) add("acPower", "true");
            if (w.NetworkAvailable) add("networkAvailable", "true");
            if (!string.IsNullOrWhiteSpace(w.FileExists)) add("fileExists", "\"" + Escape(w.FileExists) + "\"");
            if (!string.IsNullOrWhiteSpace(w.FileNotExists)) add("fileNotExists", "\"" + Escape(w.FileNotExists) + "\"");
            sb.Append(" }");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static List<TaskDefinition> ParseTasksJson(string json, List<string> errors)
        {
            var list = new List<TaskDefinition>();
            // try SimpleJSON first (single file, supports // comments, arrays/objects, no custom code)
            try
            {
                var node = JSONNode.Parse(json);
                if (node != null)
                {
                    if (node.IsArray)
                    {
                        foreach (JSONNode item in node.AsArray.Children)
                        {
                            if (item.IsObject) list.Add(ParseTaskNode(item.AsObject, errors));
                            else errors.Add("tasks array item not an object");
                        }
                        return list;
                    }
                    else if (node.IsObject)
                    {
                        var obj = node.AsObject;
                        if (obj.HasKey("tasks") && obj["tasks"].IsArray)
                        {
                            foreach (JSONNode item in obj["tasks"].AsArray.Children)
                            {
                                if (item.IsObject) list.Add(ParseTaskNode(item.AsObject, errors));
                                else errors.Add("tasks array item not an object");
                            }
                            return list;
                        }
                        else if (obj.HasKey("name"))
                        {
                            list.Add(ParseTaskNode(obj, errors));
                            return list;
                        }
                        else
                        {
                            // empty object or unknown, return empty
                            return list;
                        }
                    }
                }
            }
            catch
            {
                // fall through to legacy parser
            }
            // fallback to legacy hand-written parser (handles /* */ comments etc.)
            try
            {
                var legacyJson = StripComments(json);
                legacyJson = legacyJson.Trim();
                if (legacyJson.Length == 0) return list;
                int idx = 0;
                SkipWs(legacyJson, ref idx);
                if (idx >= legacyJson.Length) return list;
                if (legacyJson[idx] == '[')
                {
                    var arr = ParseArray(legacyJson, ref idx);
                    foreach (var obj in arr)
                    {
                        var dict2 = obj as Dictionary<string, object>;
                        if (dict2 != null) list.Add(ParseTaskObject(dict2, errors));
                        else errors.Add("tasks array item not an object");
                    }
                }
                else if (legacyJson[idx] == '{')
                {
                    var dict = ParseObject(legacyJson, ref idx);
                    object tasksObj;
                    if (dict.TryGetValue("tasks", out tasksObj) && tasksObj is List<object>)
                    {
                        var arr = (List<object>)tasksObj;
                        foreach (var item in arr)
                        {
                            if (item is Dictionary<string, object>)
                                list.Add(ParseTaskObject((Dictionary<string, object>)item, errors));
                            else
                                errors.Add("tasks array item not an object");
                        }
                    }
                    else if (dict.ContainsKey("name"))
                    {
                        list.Add(ParseTaskObject(dict, errors));
                    }
                }
                else
                {
                    errors.Add("tasks.json must be array or object");
                }
            }
            catch (Exception ex)
            {
                errors.Add("parse error: " + ex.Message);
            }
            return list;
        }

        private static TaskDefinition ParseTaskNode(JSONObject obj, List<string> errors)
        {
            var task = new TaskDefinition();
            try
            {
                if (obj.HasKey("name")) task.Name = obj["name"].Value;
                if (obj.HasKey("enabled")) task.Enabled = obj["enabled"].AsBool;

                // trigger
                if (obj.HasKey("trigger"))
                {
                    var trigNode = obj["trigger"];
                    if (trigNode.IsObject)
                    {
                        var td = trigNode.AsObject;
                        var trig = new TaskTrigger();
                        if (td.HasKey("type")) { trig.RawType = td["type"].Value; trig.Type = ParseTriggerType(trig.RawType); }
                        if (td.HasKey("delaySec")) trig.DelaySec = td["delaySec"].AsInt;
                        if (td.HasKey("delay")) trig.DelaySec = td["delay"].AsInt;
                        if (td.HasKey("everySec")) trig.EverySec = td["everySec"].AsInt;
                        if (td.HasKey("every")) trig.Every = td["every"].Value;
                        if (td.HasKey("intervalSec")) trig.EverySec = td["intervalSec"].AsInt;
                        if (td.HasKey("at")) trig.At = td["at"].Value;
                        if (td.HasKey("time")) trig.At = td["time"].Value;
                        if (td.HasKey("expr")) trig.Expr = td["expr"].Value;
                        if (td.HasKey("cron")) trig.Expr = td["cron"].Value;
                        if (td.HasKey("afterMinutes")) trig.AfterMinutes = td["afterMinutes"].AsInt;
                        if (td.HasKey("after") && trig.AfterMinutes == 0) trig.AfterMinutes = td["after"].AsInt;
                        if (td.HasKey("hotkey")) trig.Hotkey = td["hotkey"].Value;
                        if (td.HasKey("key") && string.IsNullOrWhiteSpace(trig.Hotkey)) trig.Hotkey = td["key"].Value;
                        if (td.HasKey("path")) trig.WatchPath = td["path"].Value;
                        if (td.HasKey("watchPath") && string.IsNullOrWhiteSpace(trig.WatchPath)) trig.WatchPath = td["watchPath"].Value;
                        if (td.HasKey("dir") && string.IsNullOrWhiteSpace(trig.WatchPath)) trig.WatchPath = td["dir"].Value;
                        if (td.HasKey("filter")) trig.WatchFilter = td["filter"].Value;
                        if (td.HasKey("pattern") && string.IsNullOrWhiteSpace(trig.WatchFilter)) trig.WatchFilter = td["pattern"].Value;
                        if (td.HasKey("event")) trig.WatchEvent = td["event"].Value;
                        if (td.HasKey("watchEvent") && string.IsNullOrWhiteSpace(trig.WatchEvent)) trig.WatchEvent = td["watchEvent"].Value;
                        task.Trigger = trig;
                    }
                    else if (trigNode.IsString)
                    {
                        var trig = new TaskTrigger();
                        trig.RawType = trigNode.Value;
                        trig.Type = ParseTriggerType(trig.RawType);
                        task.Trigger = trig;
                    }
                }

                // action
                if (obj.HasKey("action") && obj["action"].IsObject)
                {
                    var ad = obj["action"].AsObject;
                    var act = new TaskAction();
                    if (ad.HasKey("file")) act.File = ad["file"].Value;
                    if (ad.HasKey("path") && string.IsNullOrWhiteSpace(act.File)) act.File = ad["path"].Value;
                    if (ad.HasKey("command") && string.IsNullOrWhiteSpace(act.File)) act.File = ad["command"].Value;
                    if (ad.HasKey("args")) act.Args = ad["args"].Value;
                    if (ad.HasKey("arguments") && string.IsNullOrWhiteSpace(act.Args)) act.Args = ad["arguments"].Value;
                    if (ad.HasKey("workDir")) act.WorkDir = ad["workDir"].Value;
                    if (ad.HasKey("workingDirectory") && string.IsNullOrWhiteSpace(act.WorkDir)) act.WorkDir = ad["workingDirectory"].Value;
                    if (ad.HasKey("cwd") && string.IsNullOrWhiteSpace(act.WorkDir)) act.WorkDir = ad["cwd"].Value;
                    task.Action = act;
                }
                else if (obj.HasKey("file"))
                {
                    var act = new TaskAction();
                    act.File = obj["file"].Value;
                    if (obj.HasKey("args")) act.Args = obj["args"].Value;
                    if (obj.HasKey("workDir")) act.WorkDir = obj["workDir"].Value;
                    task.Action = act;
                }

                // options
                if (obj.HasKey("options") && obj["options"].IsObject)
                {
                    var od = obj["options"].AsObject;
                    var opt = new TaskOptions();
                    if (od.HasKey("hidden")) opt.Hidden = od["hidden"].AsBool;
                    if (od.HasKey("timeoutSec")) opt.TimeoutSec = od["timeoutSec"].AsInt;
                    if (od.HasKey("timeout") && opt.TimeoutSec == 0) opt.TimeoutSec = od["timeout"].AsInt;
                    if (od.HasKey("allowConcurrent")) opt.AllowConcurrent = od["allowConcurrent"].AsBool;
                    if (od.HasKey("concurrent") && !opt.AllowConcurrent) opt.AllowConcurrent = od["concurrent"].AsBool;
                    if (od.HasKey("retry")) opt.Retry = od["retry"].AsInt;
                    if (od.HasKey("workDir")) opt.WorkDir = od["workDir"].Value;
                    if (od.HasKey("notifyOnFailure")) opt.NotifyOnFailure = od["notifyOnFailure"].AsBool;
                    if (od.HasKey("notify")) opt.NotifyOnFailure = od["notify"].AsBool;
                    task.Options = opt;
                }
                if (obj.HasKey("hidden")) task.Options.Hidden = obj["hidden"].AsBool;
                if (obj.HasKey("timeoutSec")) task.Options.TimeoutSec = obj["timeoutSec"].AsInt;

                // when
                JSONNode wv = null;
                if (obj.HasKey("when")) wv = obj["when"];
                else if (obj.HasKey("condition")) wv = obj["condition"];
                if (wv != null && wv.IsObject)
                {
                    var wd = wv.AsObject;
                    var cond = new TaskCondition();
                    if (wd.HasKey("onlyIdle")) cond.OnlyIdle = wd["onlyIdle"].AsBool;
                    if (wd.HasKey("acPower")) cond.AcPower = wd["acPower"].AsBool;
                    if (wd.HasKey("fileExists")) cond.FileExists = wd["fileExists"].Value;
                    if (wd.HasKey("fileNotExists")) cond.FileNotExists = wd["fileNotExists"].Value;
                    if (wd.HasKey("network")) cond.NetworkAvailable = wd["network"].AsBool;
                    if (wd.HasKey("networkAvailable")) cond.NetworkAvailable = wd["networkAvailable"].AsBool;
                    task.When = cond;
                }
            }
            catch (Exception ex)
            {
                errors.Add("parse task [" + task.Name + "] error: " + ex.Message);
            }
            if (task.Trigger == null) task.Trigger = new TaskTrigger();
            if (task.Action == null) task.Action = new TaskAction();
            if (task.Options == null) task.Options = new TaskOptions();
            if (task.When == null) task.When = new TaskCondition();
            return task;
        }

        private static TaskDefinition ParseTaskObject(Dictionary<string, object> dict, List<string> errors)
        {
            var task = new TaskDefinition();
            try
            {
                object v;
                if (dict.TryGetValue("name", out v)) task.Name = ToStr(v);
                if (dict.TryGetValue("enabled", out v)) task.Enabled = ToBool(v, true);
                // trigger
                if (dict.TryGetValue("trigger", out v) && v is Dictionary<string, object>)
                {
                    var td = (Dictionary<string, object>)v;
                    var trig = new TaskTrigger();
                    object tv;
                    if (td.TryGetValue("type", out tv)) { trig.RawType = ToStr(tv); trig.Type = ParseTriggerType(trig.RawType); }
                    if (td.TryGetValue("delaySec", out tv)) trig.DelaySec = ToInt(tv, trig.DelaySec);
                    if (td.TryGetValue("delay", out tv)) trig.DelaySec = ToInt(tv, trig.DelaySec);
                    if (td.TryGetValue("everySec", out tv)) trig.EverySec = ToInt(tv, 0);
                    if (td.TryGetValue("every", out tv)) trig.Every = ToStr(tv);
                    if (td.TryGetValue("intervalSec", out tv)) trig.EverySec = ToInt(tv, trig.EverySec);
                    if (td.TryGetValue("at", out tv)) trig.At = ToStr(tv);
                    if (td.TryGetValue("time", out tv)) trig.At = ToStr(tv);
                    if (td.TryGetValue("expr", out tv)) trig.Expr = ToStr(tv);
                    if (td.TryGetValue("cron", out tv)) trig.Expr = ToStr(tv);
                    if (td.TryGetValue("afterMinutes", out tv)) trig.AfterMinutes = ToInt(tv, 0);
                    if (td.TryGetValue("after", out tv) && trig.AfterMinutes == 0) trig.AfterMinutes = ToInt(tv, 0);
                    if (td.TryGetValue("hotkey", out tv)) trig.Hotkey = ToStr(tv);
                    if (td.TryGetValue("key", out tv) && string.IsNullOrWhiteSpace(trig.Hotkey)) trig.Hotkey = ToStr(tv);
                    if (td.TryGetValue("path", out tv)) trig.WatchPath = ToStr(tv);
                    if (td.TryGetValue("watchPath", out tv) && string.IsNullOrWhiteSpace(trig.WatchPath)) trig.WatchPath = ToStr(tv);
                    if (td.TryGetValue("dir", out tv) && string.IsNullOrWhiteSpace(trig.WatchPath)) trig.WatchPath = ToStr(tv);
                    if (td.TryGetValue("filter", out tv)) trig.WatchFilter = ToStr(tv);
                    if (td.TryGetValue("pattern", out tv) && string.IsNullOrWhiteSpace(trig.WatchFilter)) trig.WatchFilter = ToStr(tv);
                    if (td.TryGetValue("event", out tv)) trig.WatchEvent = ToStr(tv);
                    if (td.TryGetValue("watchEvent", out tv) && string.IsNullOrWhiteSpace(trig.WatchEvent)) trig.WatchEvent = ToStr(tv);
                    task.Trigger = trig;
                }
                else if (dict.TryGetValue("trigger", out v) && v is string)
                {
                    var trig = new TaskTrigger();
                    trig.RawType = ToStr(v);
                    trig.Type = ParseTriggerType(trig.RawType);
                    task.Trigger = trig;
                }
                // action
                if (dict.TryGetValue("action", out v) && v is Dictionary<string, object>)
                {
                    var ad = (Dictionary<string, object>)v;
                    var act = new TaskAction();
                    object av;
                    if (ad.TryGetValue("file", out av)) act.File = ToStr(av);
                    if (ad.TryGetValue("path", out av) && string.IsNullOrWhiteSpace(act.File)) act.File = ToStr(av);
                    if (ad.TryGetValue("command", out av) && string.IsNullOrWhiteSpace(act.File)) act.File = ToStr(av);
                    if (ad.TryGetValue("args", out av)) act.Args = ToStr(av);
                    if (ad.TryGetValue("arguments", out av) && string.IsNullOrWhiteSpace(act.Args)) act.Args = ToStr(av);
                    if (ad.TryGetValue("workDir", out av)) act.WorkDir = ToStr(av);
                    if (ad.TryGetValue("workingDirectory", out av) && string.IsNullOrWhiteSpace(act.WorkDir)) act.WorkDir = ToStr(av);
                    if (ad.TryGetValue("cwd", out av) && string.IsNullOrWhiteSpace(act.WorkDir)) act.WorkDir = ToStr(av);
                    task.Action = act;
                }
                else if (dict.TryGetValue("file", out v))
                {
                    // flat style: file/args at top level
                    var act = new TaskAction();
                    act.File = ToStr(v);
                    object av;
                    if (dict.TryGetValue("args", out av)) act.Args = ToStr(av);
                    if (dict.TryGetValue("workDir", out av)) act.WorkDir = ToStr(av);
                    task.Action = act;
                }
                // options
                if (dict.TryGetValue("options", out v) && v is Dictionary<string, object>)
                {
                    var od = (Dictionary<string, object>)v;
                    var opt = new TaskOptions();
                    object ov;
                    if (od.TryGetValue("hidden", out ov)) opt.Hidden = ToBool(ov, true);
                    if (od.TryGetValue("timeoutSec", out ov)) opt.TimeoutSec = ToInt(ov, 0);
                    if (od.TryGetValue("timeout", out ov) && opt.TimeoutSec == 0) opt.TimeoutSec = ToInt(ov, 0);
                    if (od.TryGetValue("allowConcurrent", out ov)) opt.AllowConcurrent = ToBool(ov, false);
                    if (od.TryGetValue("concurrent", out ov) && !opt.AllowConcurrent) opt.AllowConcurrent = ToBool(ov, false);
                    if (od.TryGetValue("retry", out ov)) opt.Retry = ToInt(ov, 0);
                    if (od.TryGetValue("workDir", out ov)) opt.WorkDir = ToStr(ov);
                    if (od.TryGetValue("notifyOnFailure", out ov)) opt.NotifyOnFailure = ToBool(ov, true);
                    if (od.TryGetValue("notify", out ov) && ov != null) opt.NotifyOnFailure = ToBool(ov, opt.NotifyOnFailure);
                    task.Options = opt;
                }
                // also allow hidden/timeout at top level
                object hv;
                if (dict.TryGetValue("hidden", out hv) && task.Options.Hidden == true)
                {
                    // if explicitly set at top, override
                    // only if present
                    task.Options.Hidden = ToBool(hv, task.Options.Hidden);
                }
                if (dict.TryGetValue("timeoutSec", out hv)) task.Options.TimeoutSec = ToInt(hv, task.Options.TimeoutSec);
                // when / condition
                object wv;
                if (dict.TryGetValue("when", out wv) && wv is Dictionary<string, object>)
                {
                    var wd = (Dictionary<string, object>)wv;
                    var cond = new TaskCondition();
                    object cv;
                    if (wd.TryGetValue("onlyIdle", out cv)) cond.OnlyIdle = ToBool(cv, false);
                    if (wd.TryGetValue("acPower", out cv)) cond.AcPower = ToBool(cv, false);
                    if (wd.TryGetValue("fileExists", out cv)) cond.FileExists = ToStr(cv);
                    if (wd.TryGetValue("fileNotExists", out cv)) cond.FileNotExists = ToStr(cv);
                    if (wd.TryGetValue("network", out cv)) cond.NetworkAvailable = ToBool(cv, false);
                    if (wd.TryGetValue("networkAvailable", out cv)) cond.NetworkAvailable = ToBool(cv, cond.NetworkAvailable);
                    task.When = cond;
                }
                else if (dict.TryGetValue("condition", out wv) && wv is Dictionary<string, object>)
                {
                    var wd = (Dictionary<string, object>)wv;
                    var cond = new TaskCondition();
                    object cv;
                    if (wd.TryGetValue("onlyIdle", out cv)) cond.OnlyIdle = ToBool(cv, false);
                    if (wd.TryGetValue("acPower", out cv)) cond.AcPower = ToBool(cv, false);
                    if (wd.TryGetValue("fileExists", out cv)) cond.FileExists = ToStr(cv);
                    task.When = cond;
                }
            }
            catch (Exception ex)
            {
                errors.Add("parse task [" + task.Name + "] error: " + ex.Message);
            }
            if (task.Trigger == null) task.Trigger = new TaskTrigger();
            if (task.Action == null) task.Action = new TaskAction();
            if (task.Options == null) task.Options = new TaskOptions();
            return task;
        }

        private static TaskTriggerType ParseTriggerType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return TaskTriggerType.Startup;
            raw = raw.Trim().ToLowerInvariant();
            switch (raw)
            {
                case "startup": return TaskTriggerType.Startup;
                case "start": return TaskTriggerType.Startup;
                case "boot": return TaskTriggerType.Startup;
                case "interval": return TaskTriggerType.Interval;
                case "every": return TaskTriggerType.Interval;
                case "periodic": return TaskTriggerType.Interval;
                case "daily": return TaskTriggerType.Daily;
                case "day": return TaskTriggerType.Daily;
                case "cron": return TaskTriggerType.Cron;
                case "schedule": return TaskTriggerType.Cron;
                case "sessionlock": return TaskTriggerType.SessionLock;
                case "lock": return TaskTriggerType.SessionLock;
                case "session_lock": return TaskTriggerType.SessionLock;
                case "sessionunlock": return TaskTriggerType.SessionUnlock;
                case "unlock": return TaskTriggerType.SessionUnlock;
                case "session_unlock": return TaskTriggerType.SessionUnlock;
                case "idle": return TaskTriggerType.Idle;
                case "manual": return TaskTriggerType.Manual;
                case "none": return TaskTriggerType.Manual;
                case "click": return TaskTriggerType.Manual;
                case "hotkey": return TaskTriggerType.Hotkey;
                case "key": return TaskTriggerType.Hotkey;
                case "shortcut": return TaskTriggerType.Hotkey;
                case "watch": return TaskTriggerType.Watch;
                case "filewatch": return TaskTriggerType.Watch;
                case "watcher": return TaskTriggerType.Watch;
                case "file": return TaskTriggerType.Watch;
                default: return TaskTriggerType.Startup;
            }
        }

        private static string ToStr(object v)
        {
            if (v == null) return "";
            if (v is string) return (string)v;
            return v.ToString();
        }
        private static bool ToBool(object v, bool def)
        {
            if (v == null) return def;
            if (v is bool) return (bool)v;
            string s = v.ToString().Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes") return true;
            if (s == "false" || s == "0" || s == "no") return false;
            return def;
        }
        private static int ToInt(object v, int def)
        {
            if (v == null) return def;
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is double) return (int)(double)v;
            string s = v.ToString().Trim();
            int n;
            if (int.TryParse(s, out n)) return n;
            double d;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return (int)d;
            // try duration like 5m
            int dur = TaskDefinition.ParseDuration(s);
            if (dur > 0) return dur;
            return def;
        }

        // Minimal JSON parser: supports object, array, string, number, bool, null
        private static string StripComments(string json)
        {
            // strip // and /* */ comments, naive but enough for config
            var sb = new StringBuilder(json.Length);
            bool inStr = false;
            bool esc = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
                {
                    // line comment
                    i += 2;
                    while (i < json.Length && json[i] != '\n') i++;
                    if (i < json.Length) sb.Append('\n');
                    continue;
                }
                if (c == '/' && i + 1 < json.Length && json[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/')) i++;
                    i++; // skip /
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '{') return dict;
            i++; // {
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                // key
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                SkipWs(s, ref i);
                if (key == null) break;
                object val = ParseValue(s, ref i);
                dict[key] = val;
            }
            return dict;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '[') return list;
            i++; // [
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                object val = ParseValue(s, ref i);
                list.Add(val);
            }
            return list;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '"') return ParseString(s, ref i);
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == 't' && i + 3 < s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 4 < s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 3 < s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }
            // number or bare word
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
            string token = s.Substring(start, i - start).Trim();
            // try int
            int n;
            if (int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n)) return n;
            long ln;
            if (long.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ln)) return ln;
            double d;
            if (double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
            return token;
        }

        private static string ParseString(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\')
                {
                    i++;
                    if (i >= s.Length) break;
                    char e = s[i];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    i++;
                    continue;
                }
                if (c == '"') { i++; return sb.ToString(); }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static string BuildSampleJson()
        {
            return @"// ScreenLock AutoRun tasks - place alongside config.json
// docs: docs/AUTORUN-DESIGN.md / docs/USAGE.md
// Trigger types: startup | interval | daily | cron | sessionLock | sessionUnlock | idle | manual | hotkey | watch
// Scripts without path are resolved from: <DirPath>/scripts/  (portable: exe/scripts/, roaming: %AppData%/ScreenLock/scripts/)
// Supported: .ps1/.bat/.cmd/.vbs (hidden), .js -> node, .py/.pyw -> python (auto from PATH, hidden)
// Manual tasks appear in tray -> Tasks -> Manual Run (click to execute)
// Hotkey: Ctrl+Alt+Shift+Win + A-Z/0-9/F1-24 ; Watch: file watcher with debounce 500ms
[
  // startup: run 10s after login - bare name loads from scripts/
  // {
  //   ""name"": ""startup-notify"",
  //   ""enabled"": true,
  //   ""trigger"": { ""type"": ""startup"", ""delaySec"": 10 },
  //   ""action"": { ""file"": ""hello.js"", ""args"": ""--verbose"" },
  //   ""options"": { ""hidden"": true, ""timeoutSec"": 60 }
  // },

  // manual: click in tray Tasks -> Manual Run (Quick Launcher, replaces AHK tray)
  // {
  //   ""name"": ""quick-notepad"",
  //   ""trigger"": { ""type"": ""manual"" },
  //   ""action"": { ""file"": ""notepad.exe"" }
  // },

  // hotkey: global hotkey
  // {
  //   ""name"": ""hotkey-sync"",
  //   ""trigger"": { ""type"": ""hotkey"", ""hotkey"": ""Ctrl+Alt+S"" },
  //   ""action"": { ""file"": ""sync.py"" }
  // },

  // watch: file watcher
  // {
  //   ""name"": ""watch-downloads"",
  //   ""trigger"": { ""type"": ""watch"", ""path"": ""%USERPROFILE%/Downloads"", ""filter"": ""*.zip"", ""event"": ""created"" },
  //   ""action"": { ""file"": ""unzip.js"", ""args"": ""{{task}} {{yyyyMMdd}}"" }
  // },

  // interval: every 60s (or ""1h30m"")
  // {
  //   ""name"": ""heartbeat"",
  //   ""trigger"": { ""type"": ""interval"", ""everySec"": 3600 },
  //   ""action"": { ""file"": ""sync.bat"" }
  // },

  // daily: at 03:00 every day
  // {
  //   ""name"": ""daily-clean"",
  //   ""trigger"": { ""type"": ""daily"", ""at"": ""03:00"" },
  //   ""action"": { ""file"": ""clean.ps1"" },
  //   ""options"": { ""timeoutSec"": 600 }
  // },

  // with condition and template
  // {
  //   ""name"": ""backup-logs"",
  //   ""trigger"": { ""type"": ""interval"", ""every"": ""1h"" },
  //   ""action"": { ""file"": ""backup.js"", ""args"": ""--out backup-{{yyyyMMdd}}.zip"" },
  //   ""when"": { ""onlyIdle"": true, ""acPower"": true }
  // }
]
";
        }
    }
}
