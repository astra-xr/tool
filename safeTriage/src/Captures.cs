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

        public static Task<Report> RunAsync(int seconds, IProgress<string> progress, CancellationToken token)
        {
            return Task.Run(async delegate
            {
                var clock = Stopwatch.StartNew();
                var processTotals = new Dictionary<Tuple<int, long>, Totals>();
                var disk = new List<double>();
                int intervals = 0;
                double measured = 0;
                using (var meter = new DiskMeter())
                {
                    meter.Read();
                    var before = ReadProcesses(token);
                    for (int step = 1; !token.IsCancellationRequested && measured < seconds; step++)
                    {
                        await CaptureTiming.PauseUntil(clock, step, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested)
                            break;

                        var current = ReadProcesses(token);
                        if (token.IsCancellationRequested)
                            break;
                        Accumulate(before, current, processTotals);
                        before = current;
                        double? physical = meter.Read();
                        if (physical.HasValue)
                            disk.Add(physical.Value);
                        intervals++;
                        measured = clock.Elapsed.TotalSeconds;
                        if (progress != null)
                            progress.Report(CaptureTiming.Progress(clock, seconds));
                    }
                }
                return Describe(measured, intervals, processTotals.Values.ToArray(), disk, token.IsCancellationRequested);
            });
        }

        private static void Accumulate(Dictionary<int, Sample> before, Dictionary<int, Sample> current, Dictionary<Tuple<int, long>, Totals> totals)
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
                entry.ProcessorSeconds += Math.Max(sample.ProcessorTicks - previous.ProcessorTicks, 0) / (double)TimeSpan.TicksPerSecond;
                if (sample.ReadBytes.HasValue && previous.ReadBytes.HasValue &&
                    sample.ReadBytes.Value >= previous.ReadBytes.Value && sample.WriteBytes.Value >= previous.WriteBytes.Value)
                {
                    entry.IoObserved = true;
                    entry.ReadBytes += sample.ReadBytes.Value - previous.ReadBytes.Value;
                    entry.WriteBytes += sample.WriteBytes.Value - previous.WriteBytes.Value;
                }
            }
        }

        private static Report Describe(double duration, int samples, Totals[] processes, List<double> disk, bool stopped)
        {
            string summary = string.Format("{0}，共记录 {1:0.0} 秒、{2} 轮，覆盖 {3} 个可读取的进程实例。",
                stopped ? "已停止记录" : "记录完成", duration, samples, processes.Length);
            var findings = new List<Finding>();
            if (processes.Length > 0 && duration > 0)
            {
                AddRanking(findings, "进程 CPU 平均占用", processes.Where(p => p.ProcessorSeconds > 0).OrderByDescending(p => p.ProcessorSeconds),
                    process => Math.Min(100, 100 * process.ProcessorSeconds / (Environment.ProcessorCount * duration)).ToString("0.0") + "%");
                AddRanking(findings, "进程内存峰值", processes.Where(p => p.PeakMemory > 0).OrderByDescending(p => p.PeakMemory),
                    process => Size(process.PeakMemory) + " MB");
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
                findings.Add(new Finding("进程样本", CheckState.Unavailable, "未取得可用进程样本。可在卡顿出现时重新记录。"));
            }
            if (disk.Count > 0)
            {
                string detail = string.Format("物理磁盘读写合计平均 {0} MB/s，采样峰值 {1} MB/s。",
                    Size(disk.Average()), Size(disk.Max()));
                findings.Add(new Finding("物理磁盘读写速率", CheckState.Info, detail,
                    "需要确认具体文件或进程时，复现卡顿并查看资源监视器的磁盘活动。", "resmon", detail));
            }
            else
            {
                findings.Add(new Finding("物理磁盘读写速率", CheckState.Unavailable, "当前没有可用的 Windows 物理磁盘计数器样本。"));
            }
            return new Report(summary, findings.ToArray(), summary);
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
        private sealed class Round
        {
            public DateTimeOffset Time;
            public Finding Gateway;
            public Finding Dns;
            public Finding Https;
        }

        public static Task<Report> RunAsync(Uri target, int seconds, IProgress<string> progress, CancellationToken token)
        {
            return RecordAsync(seconds, () => Sample(target), progress, token);
        }

        private static async Task<Report> RecordAsync(int seconds, Func<Task<Round>> sample, IProgress<string> progress, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            var rounds = new List<Round>();
            const int interval = 5;
            for (int tick = 0; tick < seconds && !token.IsCancellationRequested; tick += interval)
            {
                rounds.Add(await sample().ConfigureAwait(false));
                if (progress != null)
                    progress.Report(CaptureTiming.Progress(clock, seconds));
                await CaptureTiming.PauseUntil(clock, tick + interval, token).ConfigureAwait(false);
            }
            return Describe(rounds, clock.Elapsed.TotalSeconds, token.IsCancellationRequested);
        }

        private static async Task<Round> Sample(Uri target)
        {
            DateTimeOffset start = DateTimeOffset.Now;
            Task<Finding> gateway = Diagnostics.Probe(ReadGateway, 2300, error => NotAvailable("网关", error));
            Task<Finding> dns = Diagnostics.Probe(() => NetworkDiagnostics.CheckDns(target), 4500, error => NotAvailable("本机 DNS", error));
            Task<NetworkDiagnostics.HttpsResult> https = NetworkDiagnostics.TestHttps(target, true);
            await Task.WhenAll(gateway, dns, https).ConfigureAwait(false);
            return new Round { Time = start, Gateway = gateway.Result, Dns = dns.Result, Https = https.Result.ToFinding("HTTPS") };
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

        private static Report Describe(List<Round> rounds, double elapsed, bool stopped)
        {
            string summary = string.Format("{0}，约 {1:0} 秒、{2} 轮。每轮并行检查网关、DNS 和 HTTPS。",
                stopped ? "已停止记录" : "记录完成", elapsed, rounds.Count);
            if (rounds.Count == 0)
                return new Report(summary, new[] { NotAvailable("断网记录", "结束前未取得完整采样。") }, summary);
            Round first = rounds.FirstOrDefault(round => IsError(round.Dns) || IsError(round.Https));
            if (first != null)
            {
                string at = IsError(first.Dns) ? (IsError(first.Https) ? "DNS 和 HTTPS" : "DNS") : "HTTPS";
                summary += string.Format(" 首次记录到异常为 {0:HH:mm:ss}，涉及 {1}。", first.Time, at);
            }
            var entries = new List<Finding>
            {
                Summarize("网关 ICMP", rounds.Select(round => round.Gateway),
                    "网关可能禁用 ICMP。若只有网关未回应，结合 DNS 和 HTTPS 结果判断。", "networkSettings"),
                Summarize("本机 DNS", rounds.Select(round => round.Dns),
                    "如果网关连通而 DNS 反复失败，可核对当前 DNS 设置或换一个域名对照。", "networkSettings"),
                Summarize("HTTPS（系统代理设置）", rounds.Select(round => round.Https),
                    "如果 DNS 和网关正常而 HTTPS 波动，继续用网络排查对比两条代理路径。", "network")
            };
            string history = string.Join("\n", rounds.Select(round => string.Format("{0:HH:mm:ss}  网关 {1} · DNS {2} · HTTPS {3}",
                round.Time, Stage(round.Gateway), Stage(round.Dns), Stage(round.Https))));
            entries.Add(new Finding("逐轮记录", CheckState.Info, history, shareDetail: history));
            return new Report(summary, entries.ToArray(), summary);
        }

        private static string Stage(Finding finding)
        {
            if (finding.State == CheckState.Clear)
                return "通过";
            if (finding.State == CheckState.Unavailable)
                return "未完成";
            if (finding.State == CheckState.Info)
                return finding.ShareDetail != null && finding.ShareDetail.StartsWith("HTTP ", StringComparison.Ordinal) ? finding.ShareDetail : "供参考";
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
