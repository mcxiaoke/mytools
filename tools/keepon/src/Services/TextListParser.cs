using System;
using System.Collections.Generic;
using System.Linq;
using KeepOn.Models;

namespace KeepOn.Services
{
    /// <summary>
    /// 用户输入文本的解析工具。
    ///
    /// 界面与配置清洗都按同一套规则切分输入（中英文逗号、分号、空格均可作分隔符），
    /// 这里统一收口，避免分隔符数组在多个方法里各写一份。
    /// </summary>
    public static class TextListParser
    {
        /// <summary>列表输入的分隔符：英文/中文逗号、英文/中文分号、空格、制表符</summary>
        private static readonly char[] Separators = { ',', '，', ';', '；', ' ', '\t' };

        /// <summary>按统一分隔符切分为去空的片段集合</summary>
        public static IEnumerable<string> Split(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Enumerable.Empty<string>();
            }

            return raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                      .Select(part => part.Trim())
                      .Where(part => part.Length > 0);
        }

        /// <summary>
        /// 解析分钟数列表：仅保留落在合法定时时长区间内的值，去重后升序返回。
        /// </summary>
        public static List<int> ParseMinutes(string raw)
        {
            return Split(raw)
                .Select(part => int.TryParse(part, out int value) ? (int?)value : null)
                .Where(value => value.HasValue && ConfigDefaults.IsValidDurationMinutes(value.Value))
                .Select(value => value.Value)
                .Distinct()
                .OrderBy(value => value)
                .ToList();
        }

        /// <summary>
        /// 解析时刻列表：归一化为 HH:mm，丢弃非法项，去重后按输入顺序返回。
        /// </summary>
        public static List<string> ParseTimes(string raw)
        {
            return Split(raw)
                .Select(AppConfig.NormalizeTimeText)
                .Where(normalized => normalized != null)
                .Distinct()
                .ToList();
        }

        /// <summary>解析进程名列表：归一化为小写且带 .exe，去重后返回</summary>
        public static List<string> ParseProcessNames(string raw)
        {
            return ProcessHelper.NormalizeList(Split(raw));
        }
    }
}
