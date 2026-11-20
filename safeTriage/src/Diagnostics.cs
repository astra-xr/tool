using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SafeTriage
{
    internal enum CheckState { Clear, Info, Warning, Error, Unavailable }

    internal sealed class Finding
    {
        public Finding(string name, CheckState state, string detail, string advice = null,
            string action = null, string shareDetail = null, string shareName = null)
        {
            Name = name;
            State = state;
            Detail = detail;
            Advice = advice;
            Action = action;
            ShareDetail = shareDetail;
            ShareName = shareName ?? name;
        }

        public string Name { get; private set; }
        public CheckState State { get; private set; }
        public string Detail { get; private set; }
        public string Advice { get; private set; }
        public string Action { get; private set; }
        public string ShareDetail { get; private set; }
        public string ShareName { get; private set; }

        public static string Status(CheckState state)
        {
            switch (state)
            {
                case CheckState.Clear: return "已检查";
                case CheckState.Info: return "供参考";
                case CheckState.Warning: return "建议检查";
                case CheckState.Error: return "发现异常";
                default: return "未完成";
            }
        }
    }

    internal sealed class Report
    {
        public Report(string summary, Finding[] findings, string shareSummary = null)
        {
            Summary = summary;
            Findings = findings;
            CreatedAt = DateTimeOffset.Now;
            ShareSummary = shareSummary;
        }

        public string Summary { get; private set; }
        public Finding[] Findings { get; private set; }
        public DateTimeOffset CreatedAt { get; private set; }
        public string ShareSummary { get; private set; }
    }

    internal static class Diagnostics
    {
        internal static async Task<T> Probe<T>(Func<T> read, int milliseconds, Func<string, T> unavailable,
            CancellationToken token = default(CancellationToken))
        {
            if (token.IsCancellationRequested)
                return unavailable("检测已停止。");
            Task<T> work = Task.Run(delegate
            {
                try { return read(); }
                catch (UnauthorizedAccessException) { return unavailable("当前账户没有读取权限。"); }
                catch (EventLogException) { return unavailable("Windows 事件日志无法读取。"); }
                catch (Exception) { return unavailable("本次读取未完成，请稍后重试。"); }
            });
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                if (await Task.WhenAny(work, Task.Delay(milliseconds, timer.Token)).ConfigureAwait(false) != work)
                    return unavailable(token.IsCancellationRequested ? "检测已停止。" : "检测超时，可以稍后复查。");
                timer.Cancel();
                return await work.ConfigureAwait(false);
            }
        }

        private static Task<Finding> Local(string name, Func<Finding> read, int timeout = 4000)
        {
            return Probe(read, timeout, message => new Finding(name, CheckState.Unavailable, message));
        }

        public static async Task<Report> QuickAsync()
        {
            Finding[] findings = await Task.WhenAll(
                Local("系统盘空间", CheckStorage),
                Local("CPU 与内存", CheckResources),
                Local("网络连接", NetworkDiagnostics.CheckLink),
                Local("系统代理", NetworkDiagnostics.CheckProxy),
                Local("蓝屏与异常重启", CheckRestarts),
                Local("Windows 更新", CheckUpdates));

            Finding[] ordered = findings.OrderByDescending(finding => Priority(finding.State)).ToArray();
            int problems = findings.Count(finding => finding.State == CheckState.Warning || finding.State == CheckState.Error);
            int unavailable = findings.Count(finding => finding.State == CheckState.Unavailable);
            string summary;
            if (problems > 0)
                summary = string.Format("发现 {0} 项值得检查。可以先看「{1}」。", problems, ordered[0].Name);
            else if (unavailable > 0)
                summary = string.Format("已完成的项目未发现明显异常。还有 {0} 项未能取得结果。", unavailable);
            else
                summary = "本轮基础排查未发现明显异常。如果问题还在，可以按症状继续排查。";
            return new Report(summary, ordered, summary);
        }

        private static int Priority(CheckState state)
        {
            switch (state)
            {
                case CheckState.Error: return 4;
                case CheckState.Warning: return 3;
                case CheckState.Unavailable: return 2;
                case CheckState.Info: return 1;
                default: return 0;
            }
        }

        private static Finding CheckStorage()
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
            long free = drive.AvailableFreeSpace;
            long size = drive.TotalSize;
            const double gib = 1024.0 * 1024 * 1024;
            CheckState state = StorageDiagnostics.FreeSpaceState(free, size);
            if (state == CheckState.Unavailable)
                return new Finding("系统盘空间", state, "Windows 返回的容量数据不完整。");
            string shared = string.Format("可用 {0:0.0} GB，共 {1:0.0} GB。", free / gib, size / gib);
            string detail = drive.Name + " " + shared;
            if (state == CheckState.Error)
                return new Finding("系统盘空间", CheckState.Error, detail,
                    "先在 Windows 存储设置查看大项，移走确认可以移动的大文件。", "storage", shared);
            if (state == CheckState.Warning)
                return new Finding("系统盘空间", CheckState.Warning, detail,
                    "可用空间偏少。打开存储设置，查看临时文件和占用较大的应用。", "storage", shared);
            return new Finding("系统盘空间", CheckState.Clear, detail, shareDetail: shared);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Size;
            public uint Load;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

        private static Finding CheckResources()
        {
            ulong firstIdle, firstKernel, firstUser, idle, kernel, user;
            if (!GetSystemTimes(out firstIdle, out firstKernel, out firstUser))
                throw new InvalidOperationException();
            Thread.Sleep(1500);
            if (!GetSystemTimes(out idle, out kernel, out user))
                throw new InvalidOperationException();
            ulong elapsed = kernel - firstKernel + user - firstUser;
            if (elapsed == 0)
                throw new InvalidOperationException();

            double usage = Math.Max(0, Math.Min(100, 100 * (1 - (idle - firstIdle) / (double)elapsed)));
            var memory = new MemoryStatus { Size = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (!GlobalMemoryStatusEx(ref memory))
                throw new InvalidOperationException();

            string detail = string.Format("CPU {0:0}%（约 1.5 秒平均），物理内存使用 {1}%。", usage, memory.Load);
            if (usage >= 90 || memory.Load >= 90)
                return new Finding("CPU 与内存", CheckState.Warning, detail,
                    "如果卡顿仍在，打开任务管理器，按 CPU 或内存排序并观察一段时间。", "taskmgr", detail);
            return new Finding("CPU 与内存", CheckState.Clear, detail, shareDetail: detail);
        }

        private static string When(DateTime? date)
        {
            return date.HasValue ? date.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "时间未记录";
        }

        private static Finding CheckRestarts()
        {
            const string filter = "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or " +
                "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)";
            WindowsEvent[] events = WindowsEvents.ReadRecent("System", filter, 60);
            if (events.Length == 0)
                return new Finding("蓝屏与异常重启", CheckState.Clear, "系统日志近 7 天没有匹配的异常关机或蓝屏事件。");

            WindowsEvent recent = events[0];
            bool nearLatest = recent.Time.HasValue;
            WindowsEvent crash = events.FirstOrDefault(item => item.Id == 1001 &&
                (item == recent || (nearLatest && item.Time.HasValue && Math.Abs((item.Time.Value - recent.Time.Value).TotalMinutes) <= 2)));
            if (crash != null)
            {
                Match code = Regex.Match(crash.Read("param1"), @"\b0x[\da-fA-F]{1,8}\b");
                string detail = string.Format("{0} 发现蓝屏重启事件（{1} / {2}）{3}。", When(crash.Time),
                    crash.Provider, crash.Id, code.Success ? "，停止码 " + code.Value : "");
                return new Finding("蓝屏与异常重启", CheckState.Warning, detail,
                    "如果当时确实发生了故障，打开可靠性历史，查看同一时段的设备、更新和应用记录。", "reliability", detail);
            }

            string evidence = string.Format("{0} 有异常关机记录（{1} / {2}），原因待确认。", When(recent.Time), recent.Provider, recent.Id);
            return new Finding("蓝屏与异常重启", CheckState.Warning, evidence,
                "对照当时是否断电、卡死或强制关机。若反复发生，查看可靠性历史中的同一时段。", "reliability", evidence);
        }

        private static string UpdateKey(WindowsEvent item)
        {
            string id = item.Read("updateGuid").Trim();
            string title = item.Read("updateTitle").Trim();
            string revision = item.Read("updateRevisionNumber").Trim();
            Guid guid;
            if (Guid.TryParse(id, out guid) && guid != Guid.Empty)
                return "id:" + guid.ToString("D") + ":" + revision;
            return title.Length > 0 ? "title:" + title : string.Empty;
        }

        private static Finding CheckUpdates()
        {
            const string filter = "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20)";
            WindowsEvent[] events = WindowsEvents.ReadRecent("System", filter, 150);
            var successful = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            WindowsEvent failure = null;
            foreach (WindowsEvent item in events)
            {
                string key = UpdateKey(item);
                if (item.Id == 19)
                {
                    if (key.Length > 0)
                        successful.Add(key);
                }
                else if (key.Length == 0 || !successful.Contains(key))
                {
                    failure = item;
                    break;
                }
            }
            if (failure == null)
                return new Finding("Windows 更新", CheckState.Clear, "近 7 天最近的更新事件中，未发现需要继续跟进的安装失败。");

            string title = failure.Read("updateTitle").Trim();
            if (title.Length > 100)
                title = title.Substring(0, 100) + "…";
            string codeText = failure.Read("errorCode").Trim();
            uint decimalCode;
            if (uint.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out decimalCode))
                codeText = "0x" + decimalCode.ToString("X8", CultureInfo.InvariantCulture);
            Match code = Regex.Match(codeText, @"^0x[\da-fA-F]{1,8}$", RegexOptions.IgnoreCase);
            string detail = string.Format("{0} 安装失败（WindowsUpdateClient / 20）{1}{2}。", When(failure.Time),
                title.Length > 0 ? "，" + title : "", code.Success ? "，错误码 " + code.Value : "");
            return new Finding("Windows 更新", CheckState.Warning, detail,
                "打开更新历史记录核对当前状态。如果仍显示失败，保留更新名称与错误码，再尝试该更新。", "updates",
                string.Format("{0} 安装失败（WindowsUpdateClient / 20）{1}。", When(failure.Time), code.Success ? "，错误码 " + code.Value : ""));
        }
    }
}
