using System;
using System.IO;

namespace ScreenLock.Services.Tasks
{
    internal static class ScriptResolver
    {
        public static string ScriptsDir
        {
            get { return ConfigService.ScriptsDirPath; }
        }

        public static void EnsureScriptsDir()
        {
            try
            {
                var dir = ScriptsDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
        }

        public static string ResolveScriptPath(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return file;
            try { file = Environment.ExpandEnvironmentVariables(file); } catch { }
            file = file.Trim().Trim('"');

            // absolute path and exists -> use directly
            if (Path.IsPathRooted(file) && File.Exists(file))
                return Path.GetFullPath(file);

            // if absolute but not found, still return as is (let caller handle)
            if (Path.IsPathRooted(file))
                return file;

            // relative or bare name -> try scripts directory first
            string scripts = ScriptsDir;
            string candidate = Path.Combine(scripts, file);
            // handle case where file already contains "scripts\" prefix duplication
            // Path.Combine will handle subfolders like "sub/a.js"

            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            // also try with just filename if file contained subdir but not found
            // e.g., file="my.ps1" already checked; file="a/b.js" checked
            // fallback: try bare filename in scripts dir
            try
            {
                string justName = Path.GetFileName(file);
                if (!string.Equals(justName, file, StringComparison.OrdinalIgnoreCase))
                {
                    string cand2 = Path.Combine(scripts, justName);
                    if (File.Exists(cand2)) return Path.GetFullPath(cand2);
                }
            }
            catch { }

            // if candidate dir doesn't exist yet but file is script-like, return candidate path for later error logging
            // we prefer scripts path over cwd if file looks like script
            string ext = "";
            try { ext = Path.GetExtension(file).ToLowerInvariant(); } catch { }
            if (IsScriptExtension(ext))
                return Path.GetFullPath(candidate);

            // fallback to original (relative to workdir/exe)
            // try relative to DirPath
            try
            {
                string inDir = Path.Combine(ConfigService.DirPath, file);
                if (File.Exists(inDir)) return Path.GetFullPath(inDir);
            }
            catch { }

            return file;
        }

        public static bool IsScriptExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            return ext == ".ps1" || ext == ".psm1" || ext == ".bat" || ext == ".cmd" || ext == ".vbs" || ext == ".js" || ext == ".py" || ext == ".pyw";
        }

        public static string FindOnPath(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return null;
            // if already rooted and exists
            if (Path.IsPathRooted(exeName) && File.Exists(exeName)) return exeName;
            // if exeName contains extension, try directly
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                var parts = pathEnv.Split(Path.PathSeparator);
                string pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
                var exts = pathext.Split(';');
                foreach (var dir in parts)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string d = dir.Trim().Trim('"');
                    try
                    {
                        string cand = Path.Combine(d, exeName);
                        if (File.Exists(cand)) return cand;
                        // try with pathext if no extension
                        if (string.IsNullOrEmpty(Path.GetExtension(exeName)))
                        {
                            foreach (var e in exts)
                            {
                                string cand2 = cand + e;
                                if (File.Exists(cand2)) return cand2;
                                string lower = cand + e.ToLowerInvariant();
                                if (File.Exists(lower)) return lower;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        public static string FindPython()
        {
            string[] candidates = new[] { "python.exe", "python3.exe", "py.exe", "python" };
            foreach (var c in candidates)
            {
                var found = FindOnPath(c);
                if (found != null) return found;
            }
            // fallback to plain "python" let OS resolve
            return "python";
        }

        public static string FindNode()
        {
            string[] candidates = new[] { "node.exe", "node" };
            foreach (var c in candidates)
            {
                var found = FindOnPath(c);
                if (found != null) return found;
            }
            return "node";
        }
    }
}
