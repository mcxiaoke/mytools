using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ScreenLock.Models;

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
            sb.AppendLine("  \"PinHash\": \"" + Escape(Current.PinHash ?? "") + "\"");
            sb.AppendLine("}");
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
