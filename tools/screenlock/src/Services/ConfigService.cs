using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ScreenLock.Models;
using SimpleJSON;

namespace ScreenLock.Services
{
    public class ConfigService
    {
        public static string AppDataDirPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenLock"); }
        }

        private static string PortableFilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); }
        }

        public static bool IsPortableMode { get; private set; }

        public static string DirPath
        {
            get { return IsPortableMode ? AppDomain.CurrentDomain.BaseDirectory : AppDataDirPath; }
        }

        public static string FilePath
        {
            get { return IsPortableMode ? PortableFilePath : Path.Combine(AppDataDirPath, "config.json"); }
        }

        public static string TaskFilePath
        {
            get { return Path.Combine(DirPath, "tasks.json"); }
        }

        public static string LogsDirPath
        {
            get { return Path.Combine(DirPath, "logs"); }
        }

        public static string ScriptsDirPath
        {
            get { return Path.Combine(DirPath, "scripts"); }
        }

        public static string GlobalTasksEnabledPath
        {
            get { return Path.Combine(DirPath, "tasks.enabled"); }
        }

        public AppSettings Current { get; private set; }

        public void LoadOrCreate()
        {
            if (File.Exists(PortableFilePath))
            {
                IsPortableMode = true;
                Current = ReadFile();
                return;
            }
            if (!Directory.Exists(AppDataDirPath)) Directory.CreateDirectory(AppDataDirPath);
            if (!File.Exists(FilePath))
            {
                Current = new AppSettings();
                Save();
                return;
            }
            Current = ReadFile();
        }

        public bool Reload()
        {
            try
            {
                if (!IsPortableMode && File.Exists(PortableFilePath))
                {
                    IsPortableMode = true;
                }
                if (!File.Exists(FilePath)) return false;
                var settings = ReadFile();
                Current = settings;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private AppSettings ReadFile()
        {
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var s = new AppSettings();
            try
            {
                var node = JSONNode.Parse(json);
                if (node != null && node.IsObject)
                {
                    var obj = node.AsObject;
                    if (obj.HasKey("IdleMinutes")) s.IdleMinutes = obj["IdleMinutes"].AsInt;
                    if (obj.HasKey("AutoStart")) s.AutoStart = obj["AutoStart"].AsBool;
                    if (obj.HasKey("ShowClock")) s.ShowClock = obj["ShowClock"].AsBool;
                    if (obj.HasKey("OverlayOpacity")) s.OverlayOpacity = obj["OverlayOpacity"].AsDouble;
                    if (obj.HasKey("PinSalt")) s.PinSalt = obj["PinSalt"].Value;
                    if (obj.HasKey("PinHash")) s.PinHash = obj["PinHash"].Value;
                    if (obj.HasKey("TasksEnabled")) s.TasksEnabled = obj["TasksEnabled"].AsBool;
                    else s.TasksEnabled = true;

                    // ExcludeProcesses: support array ["a.exe","b.exe"] or comma-string "a.exe, b.exe"
                    if (obj.HasKey("ExcludeProcesses"))
                    {
                        var exclNode = obj["ExcludeProcesses"];
                        var list = new List<string>();
                        if (exclNode.IsArray)
                        {
                            foreach (JSONNode item in exclNode.AsArray.Children)
                            {
                                var v = item.Value != null ? item.Value.Trim() : "";
                                if (!string.IsNullOrEmpty(v)) list.Add(v);
                            }
                            s.ExcludeProcesses = list;
                        }
                        else if (exclNode.IsString)
                        {
                            var str = exclNode.Value;
                            var parts = str.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                            var list2 = new List<string>();
                            foreach (var p in parts)
                            {
                                var t = p.Trim();
                                if (!string.IsNullOrEmpty(t)) list2.Add(t);
                            }
                            s.ExcludeProcesses = list2;
                        }
                        else
                        {
                            s.ExcludeProcesses = new List<string>();
                        }
                    }
                    else
                    {
                        s.ExcludeProcesses = new List<string>();
                    }
                }
                else
                {
                    // fallback to old parser for malformed? use SimpleJson
                    return ReadFileLegacy(json);
                }
            }
            catch
            {
                // fallback to legacy parser on exception
                return ReadFileLegacy(json);
            }
            return AppSettings.Merge(s);
        }

        private AppSettings ReadFileLegacy(string json)
        {
            var map = SimpleJson.Parse(json);
            var s = new AppSettings();
            int n;
            double d;
            if (map.ContainsKey("IdleMinutes") && int.TryParse(map["IdleMinutes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                s.IdleMinutes = n;
            if (map.ContainsKey("AutoStart"))
                s.AutoStart = map["AutoStart"] == "true";
            if (map.ContainsKey("ShowClock"))
                s.ShowClock = map["ShowClock"] == "true";
            if (map.ContainsKey("OverlayOpacity") && double.TryParse(map["OverlayOpacity"], NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                s.OverlayOpacity = d;
            if (map.ContainsKey("PinSalt")) s.PinSalt = map["PinSalt"];
            if (map.ContainsKey("PinHash")) s.PinHash = map["PinHash"];
            if (map.ContainsKey("TasksEnabled"))
                s.TasksEnabled = map["TasksEnabled"] == "true";
            else
                s.TasksEnabled = true;
            var excl = ExtractStringArray(json, "ExcludeProcesses");
            if (excl != null) s.ExcludeProcesses = excl;
            else s.ExcludeProcesses = new List<string>();
            return AppSettings.Merge(s);
        }

        public void Save()
        {
            if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"IdleMinutes\": " + Current.IdleMinutes + ",");
            sb.AppendLine("  \"AutoStart\": " + (Current.AutoStart ? "true" : "false") + ",");
            sb.AppendLine("  \"ShowClock\": " + (Current.ShowClock ? "true" : "false") + ",");
            sb.AppendLine("  \"OverlayOpacity\": " + Current.OverlayOpacity.ToString(CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"PinSalt\": \"" + Escape(Current.PinSalt ?? "") + "\",");
            sb.AppendLine("  \"PinHash\": \"" + Escape(Current.PinHash ?? "") + "\",");
            sb.AppendLine("  \"TasksEnabled\": " + (Current.TasksEnabled ? "true" : "false") + ",");
            sb.Append("  \"ExcludeProcesses\": ");
            sb.Append(SerializeStringArray(Current.ExcludeProcesses));
            sb.AppendLine();
            sb.AppendLine("}");
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string SerializeStringArray(List<string> list)
        {
            if (list == null || list.Count == 0) return "[]";
            var sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("\"").Append(Escape(list[i] ?? "")).Append("\"");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static List<string> ExtractStringArray(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            try
            {
                // find "key" (case-insensitive)
                int idx = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                idx = json.IndexOf(':', idx);
                if (idx < 0) return null;
                idx++;
                // skip ws
                while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++;
                if (idx >= json.Length) return null;
                if (json[idx] == '[')
                {
                    // parse array of strings
                    idx++;
                    var result = new List<string>();
                    while (idx < json.Length)
                    {
                        while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++;
                        if (idx >= json.Length) break;
                        if (json[idx] == ']') { idx++; break; }
                        if (json[idx] == ',') { idx++; continue; }
                        if (json[idx] == '"')
                        {
                            // read string
                            int start = idx;
                            int p = idx;
                            string s = SimpleJsonReadString(json, ref p);
                            if (s != null)
                            {
                                // trim and ignore empty
                                s = s.Trim();
                                if (!string.IsNullOrEmpty(s)) result.Add(s);
                                idx = p;
                            }
                            else
                            {
                                idx++;
                            }
                        }
                        else
                        {
                            // unexpected token, skip to next
                            idx++;
                        }
                    }
                    return result;
                }
                else if (json[idx] == '"')
                {
                    int p = idx;
                    string s = SimpleJsonReadString(json, ref p);
                    if (s == null) return new List<string>();
                    // support comma-separated inside single string
                    var parts = s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    var list = new List<string>();
                    foreach (var part in parts)
                    {
                        var t = part.Trim();
                        if (!string.IsNullOrEmpty(t)) list.Add(t);
                    }
                    return list;
                }
                else
                {
                    // bare value (unlikely)
                    return new List<string>();
                }
            }
            catch { return null; }
        }

        private static string SimpleJsonReadString(string s, ref int i)
        {
            // reuse SimpleJson.ReadString logic but static
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
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
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
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
    }

    public static class SimpleJson
    {
        public static Dictionary<string, string> Parse(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return result;

            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{') return result;
            i++;

            while (i < json.Length)
            {
                SkipWs(json, ref i);
                if (i >= json.Length) break;
                char c = json[i];
                if (c == '}') break;
                if (c == ',') { i++; continue; }

                string key = ReadString(json, ref i);
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                SkipWs(json, ref i);
                if (key == null || i >= json.Length) break;

                char vc = json[i];
                string value;
                if (vc == '"')
                {
                    value = ReadString(json, ref i);
                }
                else
                {
                    int start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != '}' && !char.IsWhiteSpace(json[i]))
                        i++;
                    value = json.Substring(start, i - start).Trim();
                }
                if (key != null && value != null)
                    result[key] = value;
            }
            return result;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static string ReadString(string s, ref int i)
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
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
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
                if (c == '"')
                {
                    i++;
                    return sb.ToString();
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
