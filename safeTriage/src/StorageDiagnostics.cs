using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace SafeTriage
{
    internal static class StorageDiagnostics
    {
        private const double Gib = 1024.0 * 1024 * 1024;
        private const string EventSources =
            "(Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153 or EventID=157)) or " +
            "((Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs']) and (EventID=55 or EventID=98)) or " +
            "((Provider[@Name='storahci'] or Provider[@Name='stornvme'] or Provider[@Name='iaStorAVC'] or " +
            "Provider[@Name='Microsoft-Windows-StorPort']) and EventID=129)";

        public static async Task<Report> RunAsync()
        {
            Task<Finding[]> volumes = Read("本地卷", ReadVolumes);
            Task<Finding[]> hardware = Read("物理磁盘健康", ReadPhysicalDisks);
            Task<Finding[]> logs = Read("存储事件", ReadEvents);
            Finding[] findings = (await Task.WhenAll(volumes, hardware, logs).ConfigureAwait(false))
                .SelectMany(part => part).ToArray();
            int issues = findings.Count(item => item.State == CheckState.Error || item.State == CheckState.Warning);
            int unknown = findings.Count(item => item.State == CheckState.Unavailable);
            string summary = issues > 0 ? "发现 " + issues + " 项存储线索需要检查。展开对应条目查看读数和事件来源。" :
                unknown > 0 ? "已取得的存储读数中未发现异常。" : "可读取的本地卷、物理磁盘健康和近期存储事件未发现异常。";
            if (unknown > 0)
                summary += " 另有 " + unknown + " 项未完成读取。";
            return new Report(summary, findings, summary);
        }

        private static Task<Finding[]> Read(string name, Func<Finding[]> read)
        {
            return Diagnostics.Probe(read, 5500, message => new[] { Missing(name, message) });
        }

        private static Finding Missing(string name, string message, string shareName = null)
        {
            return new Finding(name, CheckState.Unavailable, message, shareDetail: message, shareName: shareName);
        }

        internal static CheckState FreeSpaceState(long available, long total)
        {
            if (total <= 0 || available < 0 || available > total)
                return CheckState.Unavailable;
            if (available < 2 * Gib)
                return CheckState.Error;
            if (available < 10 * Gib || (available < 30 * Gib && available / (double)total < 0.05))
                return CheckState.Warning;
            return CheckState.Clear;
        }

        private static Finding[] ReadVolumes()
        {
            string system = Path.GetPathRoot(Environment.SystemDirectory);
            DriveInfo[] drives = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed)
                .OrderByDescending(drive => string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase))
                .ThenBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (drives.Length == 0)
                return new[] { Missing("本地卷", "未枚举到可检查的固定盘分区。") };

            var rows = new List<Finding>();
            for (int i = 0; i < drives.Length; i++)
            {
                var drive = drives[i];
                bool os = string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase);
                string title = (os ? "系统盘 " : "本地卷 ") + drive.Name.TrimEnd('\\');
                string exported = os ? "系统盘" : "本地卷 " + (i + 1);
                try
                {
                    long available = drive.AvailableFreeSpace, total = drive.TotalSize;
                    CheckState state = FreeSpaceState(available, total);
                    if (state == CheckState.Unavailable)
                    {
                        rows.Add(Missing(title, "Windows 返回的容量数据不完整。", exported));
                        continue;
                    }

                    string detail = string.Format("{0} · 可用 {1:0.0} / {2:0.0} GB（{3:P1}）。",
                        drive.DriveFormat, available / Gib, total / Gib, available / (double)total);
                    string advice = state == CheckState.Clear ? null :
                        os ? "系统盘空间偏少。可在 Windows 存储设置查看清理建议。" : "剩余空间偏少。先核对这个盘上的大文件和应用，再决定是否迁移。";
                    rows.Add(new Finding(title, state, detail, advice, advice == null ? null : "storage", detail, exported));
                }
                catch (IOException) { rows.Add(Missing(title, "这个卷当前未能读取，可能尚未就绪。", exported)); }
                catch (UnauthorizedAccessException) { rows.Add(Missing(title, "当前账户不能读取这个卷。", exported)); }
            }
            return rows.ToArray();
        }

        private static Finding[] ReadPhysicalDisks()
        {
            var rows = new List<Finding>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                var query = new ObjectQuery("SELECT FriendlyName, DeviceId, HealthStatus, Size FROM MSFT_PhysicalDisk");
                var options = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(3) };
                using (var search = new ManagementObjectSearcher(scope, query, options))
                using (ManagementObjectCollection result = search.Get())
                {
                    var devices = new List<Tuple<string, string, ushort, ulong>>();
                    foreach (ManagementBaseObject device in result)
                    {
                        using (device)
                        {
                            ushort health;
                            ulong size;
                            if (!ushort.TryParse(Convert.ToString(device["HealthStatus"]), out health)) health = 5;
                            ulong.TryParse(Convert.ToString(device["Size"]), out size);
                            devices.Add(Tuple.Create(Convert.ToString(device["DeviceId"]), Convert.ToString(device["FriendlyName"]), health, size));
                        }
                    }

                    foreach (var disk in devices.OrderBy(item => item.Item1, StringComparer.Ordinal))
                        rows.Add(DescribeDisk(rows.Count + 1, disk.Item2, disk.Item3, disk.Item4));
                }
            }
            catch (ManagementException)
            {
                return new[] { Missing("物理磁盘健康", "Windows 存储提供程序未返回物理磁盘健康信息。") };
            }
            return rows.Count == 0 ? new[] { Missing("物理磁盘健康", "存储提供程序没有列出可读取的物理磁盘。") } : rows.ToArray();
        }

        internal static Finding DescribeDisk(int number, string deviceName, ushort health, ulong bytes)
        {
            string name = "物理磁盘 " + number;
            string label = WindowsEvents.ShortValue(deviceName);
            string description = health == 0 ? "正常" : health == 1 ? "警告" : health == 2 ? "不健康" : "未报告";
            CheckState state = health == 0 ? CheckState.Clear : health == 1 ? CheckState.Warning :
                health == 2 ? CheckState.Error : CheckState.Unavailable;
            string detail = "Windows 报告的健康状态为 " + description + "。";
            if (bytes > 0)
                detail += string.Format(" 容量 {0:0.0} GB。", bytes / Gib);
            string advice = health == 1 || health == 2 ? "先备份重要文件，再核对磁盘连接和厂商诊断结果。" : null;
            return new Finding(label.Length == 0 ? name : name + " · " + label, state, detail, advice,
                shareDetail: detail, shareName: name);
        }

        private static Finding[] ReadEvents()
        {
            const int limit = 24;
            WindowsEvent[] events = WindowsEvents.ReadRecent("System", "(" + EventSources + ") and (Level=1 or Level=2 or Level=3)", limit + 1);
            if (events.Length == 0)
                return new[] { new Finding("近七天存储事件", CheckState.Clear, "系统日志中未找到匹配的磁盘或文件系统警告。") };

            string detail = string.Join("\n", events.Take(limit).Select(item => string.Format("{0} · {1} / {2} · {3}",
                item.Time.HasValue ? item.Time.Value.ToLocalTime().ToString("MM-dd HH:mm:ss") : "时间未知",
                item.Provider, item.Id, EventLabel(item.Id))));
            if (events.Length > limit)
                detail += "\n仅显示最近 " + limit + " 条。";
            return new[] { new Finding("近七天存储事件", CheckState.Warning, detail,
                "事件可能涉及不同磁盘。若同类错误重复出现，先备份重要文件，再按时间和设备核查相关日志。",
                "systemLog", detail) };
        }

        private static string EventLabel(int id)
        {
            switch (id)
            {
                case 7: return "坏块记录";
                case 51: return "磁盘读写错误记录";
                case 55:
                case 98: return "文件系统异常";
                case 129: return "存储设备重置";
                case 153: return "磁盘读写重试";
                case 157: return "磁盘意外断开";
                default: return "存储控制器错误";
            }
        }
    }
}
