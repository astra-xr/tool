using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SafeTriage
{
    internal sealed class FindingChange
    {
        public Finding Before;
        public Finding After;
        public bool Unconfirmed;

        public string Name { get { return (After ?? Before).Name; } }
        public string ShareName { get { return (After ?? Before).ShareName; } }
        public bool StateChanged { get { return !Unconfirmed && (Before == null || After == null || Before.State != After.State); } }
    }

    internal static class ReportOutput
    {
        public static FindingChange[] Compare(Report previous, Report current)
        {
            var past = previous.Findings.GroupBy(item => item.ComparisonKey)
                .ToDictionary(group => group.Key, group => new Queue<Finding>(group));
            var changes = new List<FindingChange>();
            foreach (Finding item in current.Findings)
            {
                Queue<Finding> matches;
                Finding before = past.TryGetValue(item.ComparisonKey, out matches) && matches.Count > 0 ? matches.Dequeue() : null;
                if (before == null || before.State != item.State || before.Detail != item.Detail)
                    changes.Add(new FindingChange
                    {
                        Before = before, After = item,
                        Unconfirmed = before == null && previous.IncompleteSources.Contains(item.Source)
                    });
            }
            foreach (Queue<Finding> remaining in past.Values)
            {
                foreach (Finding item in remaining)
                    changes.Add(new FindingChange { Before = item, Unconfirmed = current.IncompleteSources.Contains(item.Source) });
            }
            return changes.OrderByDescending(change => change.StateChanged).ToArray();
        }

        public static string Text(string category, Report current, Report previous, FindingChange[] changes)
        {
            var output = new StringBuilder();
            output.AppendLine("安诊 · 脱敏诊断报告");
            output.AppendLine(category);
            output.AppendFormat("生成时间 {0:yyyy-MM-dd HH:mm:ss zzz}", current.CreatedAt).AppendLine();
            if (!string.IsNullOrEmpty(current.ShareSummary))
                output.AppendLine(current.ShareSummary);
            output.AppendLine();
            output.AppendLine("本次检测");
            if (current.Findings.Length == 0)
                output.AppendLine("当前没有可导出的检测条目。");
            foreach (Finding item in current.Findings)
            {
                output.Append("［").Append(Finding.Status(item.State)).Append("］").AppendLine(item.ShareName);
                if (!string.IsNullOrWhiteSpace(item.ShareDetail))
                    output.AppendLine(item.ShareDetail);
                output.AppendLine();
            }
            if (current.Performance != null)
            {
                output.AppendLine("卡顿采样 · 秒 | CPU % | 内存 % | 磁盘 MB/s");
                foreach (PerformancePoint point in current.Performance.Points)
                {
                    output.AppendFormat("{0:0.0} | {1} | {2} | {3}", point.Offset, FormatReading(point.Cpu),
                        FormatReading(point.Memory), FormatReading(point.Disk)).AppendLine();
                }
                if (current.Performance.Marks.Length > 0)
                    output.AppendLine("故障标记 · " + string.Join("、", current.Performance.Marks.Select(CaptureMarks.At)));
                output.AppendLine();
            }

            if (previous != null)
            {
                output.AppendFormat("与 {0:yyyy-MM-dd HH:mm:ss zzz} 的结果对比", previous.CreatedAt).AppendLine();
                if (changes.Length == 0)
                    output.AppendLine("两次结果未发现变化。");
                foreach (FindingChange change in changes)
                {
                    output.AppendLine(change.ShareName);
                    output.Append("上次 ").AppendLine(Describe(change.Before, change.Unconfirmed));
                    output.Append("本次 ").AppendLine(Describe(change.After, change.Unconfirmed));
                }
            }
            return output.ToString();
        }

        public static void Save(string filename, string report)
        {
            string target = Path.GetFullPath(filename);
            string temporary = Path.Combine(Path.GetDirectoryName(target), ".safetriage-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 4096, true))
                        writer.Write(report);
                    stream.Flush(true);
                }
                if (File.Exists(target))
                    File.Replace(temporary, target, null);
                else
                    File.Move(temporary, target);
                created = false;
            }
            finally
            {
                if (created)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static string FormatReading(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.0") : "未取得";
        }

        private static string Describe(Finding item, bool unconfirmed)
        {
            if (item == null)
                return unconfirmed ? "未能核对（读取不完整）" : "未出现";
            return Finding.Status(item.State) + (string.IsNullOrEmpty(item.ShareDetail) ? "" : " · " + item.ShareDetail);
        }
    }
}
