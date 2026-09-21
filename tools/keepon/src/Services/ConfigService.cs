using System;
using System.IO;
using System.Text;
using KeepOn.Models;
using Newtonsoft.Json;

namespace KeepOn.Services
{
    /// <summary>
    /// 配置读写：JSON 持久化到 %AppData%\KeepOn\config.json。
    /// 采用"先写临时文件再替换"的方式，避免断电或崩溃导致配置损坏。
    /// </summary>
    public class ConfigService
    {
        private readonly object _syncRoot = new object();
        private readonly JsonSerializerSettings _jsonSettings;

        public ConfigService()
        {
            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Include,
                ObjectCreationHandling = ObjectCreationHandling.Replace
            };
        }

        public string ConfigFilePath => Paths.ConfigFile;

        /// <summary>
        /// 加载配置。文件不存在或解析失败时返回默认配置，并保证返回对象已通过 Sanitize。
        /// </summary>
        public AppConfig Load()
        {
            lock (_syncRoot)
            {
                AppConfig config = null;

                try
                {
                    string path = ConfigFilePath;
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path, Encoding.UTF8);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            config = JsonConvert.DeserializeObject<AppConfig>(json, _jsonSettings);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Config", "读取配置失败，将使用默认配置", ex);
                    BackupCorruptedFile();
                    config = null;
                }

                if (config == null)
                {
                    config = AppConfig.CreateDefault();
                }

                config.Sanitize();
                return config;
            }
        }

        /// <summary>
        /// 保存配置。返回是否写入成功；失败时已记录日志，不抛出异常。
        /// </summary>
        public bool Save(AppConfig config)
        {
            if (config == null) return false;

            lock (_syncRoot)
            {
                try
                {
                    config.Sanitize();

                    string path = ConfigFilePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string json = JsonConvert.SerializeObject(config, _jsonSettings);

                    // 原子写入：临时文件 -> 替换目标
                    string tempPath = path + ".tmp";
                    File.WriteAllText(tempPath, json, new UTF8Encoding(false));

                    if (File.Exists(path))
                    {
                        File.Replace(tempPath, path, null);
                    }
                    else
                    {
                        File.Move(tempPath, path);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error("Config", "保存配置失败", ex);
                    return false;
                }
            }
        }

        /// <summary>把无法解析的配置文件改名留存，便于排查，同时避免下次启动重复报错</summary>
        private void BackupCorruptedFile()
        {
            try
            {
                string path = ConfigFilePath;
                if (!File.Exists(path)) return;

                string backup = path + ".corrupted-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(path, backup);
                Log.Warn("Config", "已将损坏的配置文件备份为 " + Path.GetFileName(backup));
            }
            catch
            {
                // 备份失败不影响主流程
            }
        }
    }
}
