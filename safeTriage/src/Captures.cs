using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SafeTriage
{
    internal sealed class CaptureMarks
    {
        private readonly List<double> offsets = new List<double>();
        private bool completed;
        public readonly Stopwatch Clock = Stopwatch.StartNew();

        public bool TryAdd(out double offset)
        {
            lock (offsets)
            {
                offset = Clock.Elapsed.TotalSeconds;
                if (completed || (offsets.Count > 0 && offset - offsets[offsets.Count - 1] < 0.4))
                    return false;
                offsets.Add(offset);
                return true;
            }
        }

        public double[] Complete()
        {
            lock (offsets)
            {
                completed = true;
                return offsets.ToArray();
            }
        }

        public static string At(double offset)
        {
            return "第 " + offset.ToString("0.0") + " 秒";
        }
    }

    internal static class CaptureTiming
    {
        public static async Task PauseUntil(Stopwatch clock, double next, CancellationToken token)
        {
            int delay = (int)Math.Ceiling(1000 * (next - clock.Elapsed.TotalSeconds));
            if (delay <= 0 || token.IsCancellationRequested)
                return;
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        public static string Progress(Stopwatch clock, int duration)
        {
            return string.Format("已记录 {0:0} / {1} 秒", Math.Min(duration, clock.Elapsed.TotalSeconds), duration);
        }
    }

    internal sealed class PerformancePoint
    {
        public readonly double Offset;
        public readonly double? Cpu, Memory, Disk;

        public PerformancePoint(double offset, double? cpu, double? memory, double? disk)
        {
            Offset = offset;
            Cpu = cpu;
            Memory = memory;
            Disk = disk;
        }
    }

    internal sealed class PerformanceSeries
    {
        public readonly PerformancePoint[] Points;
        public readonly double[] Marks;

        public PerformanceSeries(PerformancePoint[] points, double[] marks)
        {
            Points = points;
            Marks = marks;
        }
    }

    internal static class PerformanceCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        private sealed class Sample
        {
            public int Id;
            public string Name;
            public long Created;
            public long ProcessorTicks;
            public long Memory;
            public ulong? ReadBytes;
            public ulong? WriteBytes;
        }

        private sealed class Totals
        {
            public string Name;
            public double ProcessorSeconds;
            public long PeakMemory;
            public bool IoObserved;
            public double ReadBytes;
            public double WriteBytes;
        }

        private sealed class Interval
        {
            public double From, To;
            public string CpuName;
            public double CpuSeconds;
            public double? DiskRate;
        }

        private static Dictionary<int, Sample> ReadProcesses(CancellationToken token)
        {
            var samples = new Dictionary<int, Sample>();
            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (Process process in processes)
                {
                    if (token.IsCancellationRequested)
                        break;
                    if (process.Id == 0)
                        continue;
                    const uint queryLimited = 0x1000;
                    IntPtr handle = OpenProcess(queryLimited, false, process.Id);
                    if (handle == IntPtr.Zero)
                        continue;
                    try
                    {
                        long created, exit, kernel, user;
                        if (!GetProcessTimes(handle, out created, out exit, out kernel, out user))
                            continue;
                        IoCounters io;
                        bool hasIo = GetProcessIoCounters(handle, out io);
                        samples[process.Id] = new Sample
                        {
                            Id = process.Id, Name = WindowsEvents.ShortValue(process.ProcessName),
                            Created = created, ProcessorTicks = kernel + user, Memory = process.WorkingSet64,
                            ReadBytes = hasIo ? (ulong?)io.ReadBytes : null,
                            WriteBytes = hasIo ? (ulong?)io.WriteBytes : null
                        };
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    finally { CloseHandle(handle); }
                }
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
            return samples;
        }

        public static Task<Report> RunAsync(int seconds, IProgress<string> progress, CancellationToken token, CaptureMarks marks)
        {
            return Task.Run(async delegate
            {
                var clock = marks.Clock;
                var intervals = new List<Interval>();
                var points = new List<PerformancePoint>();
                Dictionary<Tuple<int, long>, Totals> processTotals;
                double measured = 0;
                using (var meter = new DiskMeter())
                {
                    var before = ReadProcesses(token);
                    processTotals = InitialTotals(before.Values);
                    var cpuBefore = Diagnostics.ReadCpuTimes();
                    meter.Read();
                    double beforeAt = clock.Elapsed.TotalSeconds;
                    points.Add(new PerformancePoint(beforeAt, null, Diagnostics.ReadMemoryLoad(), null));
                    while (!token.IsCancellationRequested && beforeAt < seconds)
                    {
                        await CaptureTiming.PauseUntil(clock, Math.Min(seconds, beforeAt + 1), token).ConfigureAwait(false);
                        if (token.IsCancellationRequested)
                            break;

                        var current = ReadProcesses(token);
                        if (token.IsCancellationRequested)
                            break;
                        var cpuNow = Diagnostics.ReadCpuTimes();
                        var interval = new Interval { From = beforeAt, To = clock.Elapsed.TotalSeconds, DiskRate = meter.Read() };
                        points.Add(new PerformancePoint(interval.To, Diagnostics.CpuUsage(cpuBefore, cpuNow),
                            Diagnostics.ReadMemoryLoad(), interval.DiskRate / (1024 * 1024)));
                        cpuBefore = cpuNow;
                        Accumulate(before, current, processTotals, interval);
                        before = current;
                        beforeAt = interval.To;
                        intervals.Add(interval);
                        measured = interval.To;
                        if (progress != null)
                            progress.Report(CaptureTiming.Progress(clock, seconds));
                    }
                }
                var series = new PerformanceSeries(points.ToArray(), marks.Complete());
                return Describe(measured, intervals, processTotals.Values.ToArray(), token.IsCancellationRequested, series);
            });
        }

        private static Dictionary<Tuple<int, long>, Totals> InitialTotals(IEnumerable<Sample> samples)
        {
            return samples.ToDictionary(sample => Tuple.Create(sample.Id, sample.Created),
                sample => new Totals { Name = sample.Name, PeakMemory = sample.Memory });
        }

        private static void Accumulate(Dictionary<int, Sample> before, Dictionary<int, Sample> current,
            Dictionary<Tuple<int, long>, Totals> totals, Interval interval)
        {
            foreach (Sample sample in current.Values)
            {
                var key = Tuple.Create(sample.Id, sample.Created);
                Totals entry;
                if (!totals.TryGetValue(key, out entry))
                {
                    entry = new Totals { Name = sample.Name };
                    totals.Add(key, entry);
                }

                entry.PeakMemory = Math.Max(entry.PeakMemory, sample.Memory);
                Sample previous;
                if (!before.TryGetValue(sample.Id, out previous) || previous.Created != sample.Created)
                    continue;
                double cpu = Math.Max(sample.ProcessorTicks - previous.ProcessorTicks, 0) / (double)TimeSpan.TicksPerSecond;
                entry.ProcessorSeconds += cpu;
                if (cpu > interval.CpuSeconds)
                {
                    interval.CpuSeconds = cpu;
                    interval.CpuName = sample.Name;
                }
                if (sample.ReadBytes.HasValue && previous.ReadBytes.HasValue &&
                    sample.ReadBytes.Value >= previous.ReadBytes.Value && sample.WriteBytes.Value >= previous.WriteBytes.Value)
                {
                    entry.IoObserved = true;
                    entry.ReadBytes += sample.ReadBytes.Value - previous.ReadBytes.Value;
                    entry.WriteBytes += sample.WriteBytes.Value - previous.WriteBytes.Value;
                }
            }
        }

        private static Report Describe(double duration, List<Interval> intervals, Totals[] processes,
            bool stopped, PerformanceSeries performance)
        {
            string summary = string.Format("{0}，共记录 {1:0.0} 秒、{2} 轮，覆盖 {3} 个可读取的进程实例。",
                stopped ? "已停止记录" : "记录完成", duration, intervals.Count, processes.Length);
            var findings = new List<Finding>();
            if (performance.Marks.Length > 0)
                findings.Add(new Finding("故障标记附近的负载", CheckState.Info, DescribeMarks(intervals, performance.Marks, true),
                    "读数来自覆盖标记的采样区间，可结合卡顿时的操作进一步核对。",
                    shareDetail: DescribeMarks(intervals, performance.Marks, false)));
            double sampleDuration = intervals.Sum(interval => interval.To - interval.From);
            if (processes.Length > 0)
                AddRanking(findings, "进程内存峰值", processes.Where(p => p.PeakMemory > 0).OrderByDescending(p => p.PeakMemory),
                    process => Size(process.PeakMemory) + " MB");
            if (processes.Length > 0 && sampleDuration > 0)
            {
                AddRanking(findings, "进程 CPU 平均占用", processes.Where(p => p.ProcessorSeconds > 0).OrderByDescending(p => p.ProcessorSeconds),
                    process => Math.Min(100, 100 * process.ProcessorSeconds / (Environment.ProcessorCount * sampleDuration)).ToString("0.0") + "%");
                if (processes.Any(process => process.IoObserved))
                    AddRanking(findings, "进程读写 I/O", processes.Where(p => p.ReadBytes + p.WriteBytes > 0).OrderByDescending(p => p.ReadBytes + p.WriteBytes),
                        process => "读 " + Size(process.ReadBytes) + " MB · 写 " + Size(process.WriteBytes) + " MB",
                        "进程计数可能包含文件、网络和设备 I/O。打开资源监视器的「磁盘」页可核对具体磁盘读写。", "resmon");
                else
                    findings.Add(new Finding("进程读写 I/O", CheckState.Unavailable, "未读到可用的进程读写计数。",
                        "可在资源监视器里查看实时磁盘活动。", "resmon"));
            }
            else
            {
                findings.Add(new Finding("进程 CPU 与 I/O", CheckState.Unavailable, "未取得完整的采样区间。可在卡顿出现时重新记录。"));
            }
            Interval[] disk = intervals.Where(interval => interval.DiskRate.HasValue && interval.To > interval.From).ToArray();
            if (disk.Length > 0)
            {
                double recordedSeconds = disk.Sum(interval => interval.To - interval.From);
                double average = disk.Sum(interval => interval.DiskRate.Value * (interval.To - interval.From)) / recordedSeconds;
                string detail = string.Format("物理磁盘读写合计平均 {0} MB/s，采样峰值 {1} MB/s。",
                    Size(average), Size(disk.Max(interval => interval.DiskRate.Value)));
                findings.Add(new Finding("物理磁盘读写速率", CheckState.Info, detail,
                    "需要确认具体文件或进程时，复现卡顿并查看资源监视器的磁盘活动。", "resmon", detail));
            }
            else
            {
                findings.Add(new Finding("物理磁盘读写速率", CheckState.Unavailable, "当前没有可用的 Windows 物理磁盘计数器样本。"));
            }
            bool hasReadings = performance.Points.Any(point => point.Cpu.HasValue || point.Memory.HasValue || point.Disk.HasValue);
            return new Report(summary, findings.ToArray(), summary, canCompare: intervals.Count > 0,
                performance: hasReadings ? performance : null);
        }

        private static string DescribeMarks(List<Interval> intervals, double[] marks, bool includeNames)
        {
            return string.Join("\n", marks.Select(offset =>
            {
                Interval item = intervals.FirstOrDefault(sample => sample.From <= offset && sample.To >= offset);
                string marker = CaptureMarks.At(offset);
                if (item == null)
                    return marker + " · 当时没有完整的采样区间。";
                var parts = new List<string> { marker };
                if (item.CpuName != null && item.To > item.From)
                {
                    double percent = Math.Min(100, 100 * item.CpuSeconds / (Environment.ProcessorCount * (item.To - item.From)));
                    parts.Add((includeNames ? item.CpuName : "CPU 占用最高进程") + " CPU " + percent.ToString("0.0") + "%");
                }
                if (item.DiskRate.HasValue)
                    parts.Add("物理磁盘 " + Size(item.DiskRate.Value) + " MB/s");
                if (parts.Count == 1)
                    parts.Add("这一轮没有可用的 CPU 或磁盘读数。");
                return string.Join(" · ", parts);
            }));
        }

        private static void AddRanking(List<Finding> findings, string title, IEnumerable<Totals> sorted,
            Func<Totals, string> metric, string advice = null, string action = null)
        {
            Totals[] top = sorted.Take(5).ToArray();
            string rows = top.Length == 0 ? "这一轮未观察到可排名的活动。" :
                string.Join("\n", top.Select(process => process.Name + " · " + metric(process)));
            string exported = top.Length == 0 ? rows :
                string.Join("\n", top.Select((process, i) => "第 " + (i + 1) + " 位 · " + metric(process)));
            findings.Add(new Finding(title, CheckState.Info, rows, advice, action, exported));
        }

        private static string Size(double bytes) { return (bytes / (1024 * 1024)).ToString("0.00"); }

        private sealed class DiskMeter : IDisposable
        {
            private IntPtr query;
            private IntPtr counter;

            [StructLayout(LayoutKind.Explicit)]
            private struct Formatted
            {
                [FieldOffset(0)] public uint Status;
                [FieldOffset(8)] public double Value;
            }

            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            private static extern uint PdhOpenQuery(string source, UIntPtr data, out IntPtr query);

            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr data, out IntPtr counter);

            [DllImport("pdh.dll")]
            private static extern uint PdhCollectQueryData(IntPtr query);

            [DllImport("pdh.dll")]
            private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out Formatted value);

            [DllImport("pdh.dll")]
            private static extern uint PdhCloseQuery(IntPtr query);

            public DiskMeter()
            {
                if (PdhOpenQuery(null, UIntPtr.Zero, out query) == 0)
                {
                    if (PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Bytes/sec", UIntPtr.Zero, out counter) != 0)
                        counter = IntPtr.Zero;
                }
            }

            public double? Read()
            {
                if (counter == IntPtr.Zero || PdhCollectQueryData(query) != 0)
                    return null;
                Formatted result;
                const uint pdhFmtDouble = 0x200;
                if (PdhGetFormattedCounterValue(counter, pdhFmtDouble, IntPtr.Zero, out result) != 0 ||
                    result.Status > 1 || result.Value < 0 || double.IsNaN(result.Value) || double.IsInfinity(result.Value))
                    return null;
                return result.Value;
            }

            public void Dispose()
            {
                if (query != IntPtr.Zero)
                    PdhCloseQuery(query);
            }
        }
    }

    internal static class NetworkCapture
    {
        internal sealed class Round
        {
            public DateTimeOffset Time;
            public double Offset;
            public Finding Gateway;
            public Finding Dns;
            public Finding SystemHttps;
            public Finding DirectHttps;
        }

        public static Task<Report> RunAsync(Uri target, int seconds, IProgress<string> progress, CancellationToken token, CaptureMarks marks)
        {
            return RecordAsync(seconds, stop => Sample(target, stop), progress, token, marks);
        }

        internal static async Task<Report> RecordAsync(int seconds, Func<CancellationToken, Task<Round>> sample,
            IProgress<string> progress, CancellationToken token, CaptureMarks marks)
        {
            var clock = marks.Clock;
            var rounds = new List<Round>();
            const int interval = 5;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, seconds - clock.Elapsed.TotalSeconds)));
                double next = 0;
                while (next < seconds && !limit.IsCancellationRequested)
                {
                    await CaptureTiming.PauseUntil(clock, next, limit.Token).ConfigureAwait(false);
                    if (limit.IsCancellationRequested || clock.Elapsed.TotalSeconds >= seconds)
                        break;

                    double offset = clock.Elapsed.TotalSeconds;
                    DateTimeOffset time = DateTimeOffset.Now;
                    Round round = await sample(limit.Token).ConfigureAwait(false);
                    if (limit.IsCancellationRequested)
                        break;
                    round.Offset = offset;
                    round.Time = time;
                    rounds.Add(round);
                    if (progress != null)
                        progress.Report(CaptureTiming.Progress(clock, seconds));
                    next = Math.Max(next + interval, Math.Ceiling(clock.Elapsed.TotalSeconds / interval) * interval);
                }
                await CaptureTiming.PauseUntil(clock, seconds, limit.Token).ConfigureAwait(false);
            }
            return Describe(rounds, Math.Min(seconds, clock.Elapsed.TotalSeconds), token.IsCancellationRequested, marks.Complete());
        }

        private static async Task<Round> Sample(Uri target, CancellationToken token)
        {
            Task<Finding> gateway = Diagnostics.Probe(ReadGateway, 2300, error => NotAvailable("网关", error), token);
            Task<Finding> dns = Diagnostics.Probe(() => NetworkDiagnostics.CheckDns(target), 4500, error => NotAvailable("本机 DNS", error), token);
            Task<NetworkDiagnostics.HttpsResult> system = NetworkDiagnostics.TestHttps(target, true, recording: true, stop: token);
            Task<NetworkDiagnostics.HttpsResult> direct = NetworkDiagnostics.TestHttps(target, false, recording: true, stop: token);
            await Task.WhenAll(gateway, dns, system, direct).ConfigureAwait(false);
            return new Round
            {
                Gateway = gateway.Result, Dns = dns.Result,
                SystemHttps = system.Result.ToFinding("系统代理设置"),
                DirectHttps = direct.Result.ToFinding("应用不使用代理")
            };
        }

        private static Finding NotAvailable(string title, string error)
        {
            return new Finding(title, CheckState.Unavailable, error, shareDetail: error);
        }

        private static Finding ReadGateway()
        {
            IPAddress[] gateways = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().GatewayAddresses)
                .Select(gateway => gateway.Address)
                .Where(address => !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                .Distinct().Take(3).ToArray();
            if (gateways.Length == 0)
                return NotAvailable("网关", "这轮未读到可探测的网关地址。");

            bool attempted = false;
            foreach (IPAddress address in gateways)
            {
                try
                {
                    using (var ping = new Ping())
                    {
                        PingReply reply = ping.Send(address, 550);
                        attempted = true;
                        if (reply.Status == IPStatus.Success)
                        {
                            string detail = "收到网关 ICMP 响应，往返 " + reply.RoundtripTime + " ms。";
                            return new Finding("网关", CheckState.Clear, detail, shareDetail: detail);
                        }
                    }
                }
                catch (PingException) { }
                catch (System.Net.Sockets.SocketException) { }
            }
            if (!attempted)
                return NotAvailable("网关", "Windows 未完成网关 ICMP 探测。");
            return new Finding("网关", CheckState.Warning, "网关没有回应 ICMP。设备可能禁用了 Ping。", shareDetail: "网关无 ICMP 响应。");
        }

        private static Report Describe(List<Round> rounds, double elapsed, bool stopped, double[] marks)
        {
            string summary = string.Format("{0}，约 {1:0} 秒、{2} 轮。每轮并行检查网关、DNS 和两条 HTTPS 路径。",
                stopped ? "已停止记录" : "记录完成", elapsed, rounds.Count);
            var entries = new List<Finding>();
            if (marks.Length > 0)
            {
                string detail = string.Join("\n", marks.Select(offset =>
                {
                    Round prior = rounds.LastOrDefault(round => round.Offset <= offset);
                    Round later = rounds.FirstOrDefault(round => round.Offset > offset);
                    return CaptureMarks.At(offset) + " · 标记故障\n  前一轮 " + NearRound(prior) + "\n  后一轮 " + NearRound(later);
                }));
                entries.Add(new Finding("故障标记前后的连通性", CheckState.Info, detail,
                    "每轮探测需要时间。报告以探测开始时间与标记对照。", shareDetail: detail));
            }
            if (rounds.Count == 0)
            {
                entries.Add(NotAvailable("断网记录", "结束前未取得完整采样。"));
                return new Report(summary, entries.ToArray(), summary, canCompare: false);
            }
            Round first = rounds.FirstOrDefault(round => IsError(round.Dns) || IsError(round.SystemHttps) || IsError(round.DirectHttps));
            if (first != null)
            {
                var problems = new List<string>();
                if (IsError(first.Dns)) problems.Add("DNS");
                if (IsError(first.SystemHttps)) problems.Add("系统代理设置路径");
                if (IsError(first.DirectHttps)) problems.Add("应用不使用代理路径");
                string at = string.Join("、", problems);
                summary += string.Format(" 首次记录到异常为 {0:HH:mm:ss}，涉及 {1}。", first.Time, at);
            }
            entries.AddRange(new[]
            {
                Summarize("网关 ICMP", rounds.Select(round => round.Gateway),
                    "网关可能禁用 ICMP。若只有网关未回应，结合 DNS 和 HTTPS 结果判断。", "networkSettings"),
                Summarize("本机 DNS", rounds.Select(round => round.Dns),
                    "如果网关连通而 DNS 反复失败，可核对当前 DNS 设置或换一个域名对照。", "networkSettings"),
                Summarize("HTTPS（系统代理设置）", rounds.Select(round => round.SystemHttps),
                    "如果仅这条路径异常，核对 Windows 代理和自动配置脚本。", "proxySettings"),
                Summarize("HTTPS（应用不使用代理）", rounds.Select(round => round.DirectHttps),
                    "这条路径仍可能经过 VPN。可和系统代理设置路径逐轮对比。", "network")
            });
            string history = string.Join("\n", rounds.Select(round => string.Format("{0:HH:mm:ss} · {1}\n{2}",
                round.Time, CaptureMarks.At(round.Offset), RoundEvidence(round))));
            entries.Add(new Finding("逐轮记录", CheckState.Info, history, shareDetail: history));
            return new Report(summary, entries.ToArray(), summary);
        }

        private static string NearRound(Round round)
        {
            return round == null ? "没有完整采样" : CaptureMarks.At(round.Offset) + "\n  " + RoundEvidence(round);
        }

        private static string RoundEvidence(Round round)
        {
            return string.Format("网关 {0} · DNS {1} · 系统代理设置 {2} · 应用不使用代理 {3}",
                Stage(round.Gateway), Stage(round.Dns), Stage(round.SystemHttps), Stage(round.DirectHttps));
        }

        private static string Stage(Finding finding)
        {
            if (finding.ShareDetail != null && (finding.ShareDetail.StartsWith("HTTP ", StringComparison.Ordinal) ||
                finding.ShareDetail.StartsWith("返回 HTTP ", StringComparison.Ordinal) ||
                finding.ShareDetail.StartsWith("代理 CONNECT ", StringComparison.Ordinal)))
                return finding.ShareDetail;
            if (finding.State == CheckState.Clear)
                return "通过";
            if (finding.State == CheckState.Unavailable)
                return "未完成";
            if (finding.State == CheckState.Info)
                return "供参考";
            return "异常";
        }

        private static bool IsError(Finding finding)
        {
            return finding.State == CheckState.Warning || finding.State == CheckState.Error;
        }

        private static Finding Summarize(string name, IEnumerable<Finding> source, string advice, string action)
        {
            Finding[] stages = source.ToArray();
            int good = stages.Count(stage => stage.State == CheckState.Clear);
            int errors = stages.Count(IsError);
            int info = stages.Count(stage => stage.State == CheckState.Info);
            int unknown = stages.Length - good - errors - info;
            string counts = string.Format("通过 {0} 轮 · 异常 {1} 轮 · 供参考 {2} 轮 · 未完成 {3} 轮。", good, errors, info, unknown);
            string example = stages.Where(stage => stage.State != CheckState.Clear)
                .Select(stage => stage.ShareDetail).FirstOrDefault(detail => !string.IsNullOrWhiteSpace(detail));
            if (example != null)
                counts += "\n示例 " + example;
            CheckState state = errors > 0 ? CheckState.Warning : unknown == stages.Length ? CheckState.Unavailable :
                info > 0 || unknown > 0 ? CheckState.Info : CheckState.Clear;
            return new Finding(name, state, counts, advice, action, counts);
        }
    }
}
