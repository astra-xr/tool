using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SafeTriage
{
    internal sealed class WindowsEvent
    {
        public string Log;
        public long? RecordId;
        public string Provider;
        public int Id;
        public DateTime? Time;
        public Dictionary<string, string> Data;

        public string Read(string name)
        {
            string value;
            return Data.TryGetValue(name, out value) ? value.Trim() : string.Empty;
        }

        public long Milliseconds(string name)
        {
            long value;
            return long.TryParse(Read(name), NumberStyles.None, CultureInfo.InvariantCulture, out value) ? value : 0;
        }
    }

    internal static class WindowsEvents
    {
        public static WindowsEvent[] ReadRecent(string log, string source, int max)
        {
            return Read(log, source, "TimeCreated[timediff(@SystemTime) <= 604800000]", max);
        }

        public static WindowsEvent[] ReadBetween(string log, string source, DateTimeOffset from, DateTimeOffset to, int max)
        {
            string time = string.Format(CultureInfo.InvariantCulture, "TimeCreated[@SystemTime >= '{0}' and @SystemTime <= '{1}']",
                from.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), to.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
            return Read(log, source, time, max);
        }

        private static WindowsEvent[] Read(string log, string source, string time, int max)
        {
            var query = new EventLogQuery(log, PathType.LogName, "*[System[" + time + " and (" + source + ")]]")
            {
                ReverseDirection = true
            };
            var found = new List<WindowsEvent>();
            using (var reader = new EventLogReader(query))
            {
                while (found.Count < max)
                {
                    using (EventRecord record = reader.ReadEvent(TimeSpan.FromSeconds(2)))
                    {
                        if (record == null)
                            break;
                        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                        XElement data = XElement.Parse(record.ToXml()).Element(ns + "EventData");
                        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (data != null)
                        {
                            foreach (XElement value in data.Elements(ns + "Data"))
                            {
                                string name = (string)value.Attribute("Name");
                                if (!string.IsNullOrEmpty(name))
                                    values[name] = value.Value;
                            }
                        }
                        found.Add(new WindowsEvent
                        {
                            Log = log, RecordId = record.RecordId, Provider = record.ProviderName,
                            Id = record.Id, Time = record.TimeCreated, Data = values
                        });
                    }
                }
            }
            return found.ToArray();
        }

        public static string ShortValue(string text, int max = 85)
        {
            text = Regex.Replace(text ?? "", @"[\p{C}\s]+", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        public static string ErrorCode(string text, bool bareHex = false)
        {
            Match match = Regex.Match(text, @"\b0x[0-9a-fA-F]{1,8}\b");
            if (match.Success)
                return match.Value;
            if (bareHex && Regex.IsMatch(text, @"\A[0-9a-fA-F]{1,8}\z"))
                return "0x" + text;
            uint number;
            return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) ?
                "0x" + number.ToString("X8", CultureInfo.InvariantCulture) : "";
        }
    }

    internal static class TimelineDiagnostics
    {
        private const int Limit = 36;
        private const string SystemSources =
            "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or " +
            "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or " +
            "(Provider[@Name='EventLog'] and EventID=6008) or " +
            "(Provider[@Name='Display'] and EventID=4101) or " +
            "(Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=1 or EventID=17 or EventID=18 or EventID=19 or EventID=20 or EventID=46 or EventID=47)) or " +
            "(Provider[@Name='disk'] and (EventID=7 or EventID=51 or EventID=153)) or " +
            "(Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20))";
        private const string ApplicationSources =
            "(Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002)";

        private sealed class LogRead
        {
            public string Log;
            public WindowsEvent[] Events;
            public string Failure;
        }

        private static Task<LogRead> Read(string log, string filter, DateTimeOffset from, DateTimeOffset to)
        {
            return Diagnostics.Probe(() => new LogRead { Log = log, Events = WindowsEvents.ReadBetween(log, filter, from, to, Limit + 1) },
                5000, error => new LogRead { Log = log, Failure = error, Events = new WindowsEvent[0] });
        }

        public static async Task<Report> RunAsync(DateTimeOffset incident)
        {
            DateTimeOffset from = incident.AddMinutes(-10), to = incident.AddMinutes(10);
            LogRead[] logs = await Task.WhenAll(Read("System", SystemSources, from, to), Read("Application", ApplicationSources, from, to));
            WindowsEvent[] items = logs.SelectMany(log => log.Events.Take(Limit)).OrderByDescending(item => item.Time).ToArray();
            string range = string.Format("{0:MM-dd HH:mm} 至 {1:MM-dd HH:mm}", from, to);
            var findings = new List<Finding>();
            foreach (LogRead log in logs)
            {
                if (log.Failure != null)
                    findings.Add(new Finding(log.Log + " 日志", CheckState.Unavailable, log.Failure, shareDetail: log.Failure));
                else if (log.Events.Length > Limit)
                    findings.Add(new Finding(log.Log + " 日志", CheckState.Info, "只显示这一时段最新的 " + Limit + " 条匹配记录。"));
            }
            findings.AddRange(items.Select(Describe));
            string summary;
            if (items.Length > 0)
                summary = string.Format("{0} 共列出 {1} 条记录。时间接近的事件可作排查线索，原因仍需结合故障现象判断。", range, items.Length);
            else if (logs.Any(log => log.Failure != null))
                summary = range + " 可读取的日志中没有匹配记录。部分日志未能读取，无法完整判断。";
            else
                summary = range + " 没有匹配记录。可以换一个更接近故障的时间再查。";
            return new Report(summary, findings.ToArray(), range + " · 已列出 " + items.Length + " 条记录。");
        }

        internal static Finding Describe(WindowsEvent item)
        {
            string title, detail = "", advice = null, action = null, safeExtra = "";
            CheckState state = CheckState.Warning;
            switch (item.Provider)
            {
                case "Application Error":
                case "Application Hang":
                    title = item.Id == 1000 ? "应用闪退" : "应用无响应";
                    string app = WindowsEvents.ShortValue(item.Read("AppName"));
                    string module = WindowsEvents.ShortValue(item.Read("ModuleName"));
                    if (app.Length == 0)
                        app = WindowsEvents.ShortValue(item.Read("ExeFileName"));
                    detail = app.Length > 0 ? "应用 " + app : "";
                    if (module.Length > 0)
                        detail += (detail.Length > 0 ? " · " : "") + "故障模块 " + module;
                    string exception = WindowsEvents.ErrorCode(item.Read("ExceptionCode"), bareHex: true);
                    safeExtra = exception.Length > 0 ? "异常代码 " + exception : "";
                    break;
                case "Microsoft-Windows-Kernel-Power":
                case "EventLog":
                    title = "上次关机异常";
                    detail = "这是 Windows 记录该事件的时间。异常断电、死机和强制关机都可能留下这类记录。";
                    break;
                case "Microsoft-Windows-WER-SystemErrorReporting":
                    title = "蓝屏重启";
                    string stop = WindowsEvents.ErrorCode(item.Read("param1"));
                    safeExtra = stop.Length > 0 ? "停止码 " + stop : "";
                    break;
                case "Display":
                    title = "显示驱动超时后恢复";
                    advice = "若同一时段遇到黑屏，核对驱动变更与出问题的应用。";
                    break;
                case "Microsoft-Windows-WHEA-Logger":
                    title = "硬件错误记录";
                    if (item.Id == 17 || item.Id == 19 || item.Id == 47)
                        state = CheckState.Info;
                    advice = "如同类记录重复出现，可进一步核对对应硬件或驱动。";
                    break;
                case "disk":
                    title = "存储读写异常记录";
                    advice = "如果这类记录反复出现，先备份重要文件，再检查设备状态。";
                    break;
                default:
                    state = item.Id == 19 ? CheckState.Info : CheckState.Warning;
                    title = item.Id == 19 ? "系统更新安装成功" : "系统更新安装失败";
                    detail = WindowsEvents.ShortValue(item.Read("updateTitle"));
                    string code = WindowsEvents.ErrorCode(item.Read("errorCode"));
                    safeExtra = code.Length > 0 ? "错误码 " + code : "";
                    action = "updates";
                    break;
            }

            string time = item.Time.HasValue ? item.Time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "时间未知";
            string evidence = time + " · " + item.Provider + " / " + item.Id;
            string share = safeExtra.Length > 0 ? evidence + " · " + safeExtra : evidence;
            string header = (item.Time.HasValue ? item.Time.Value.ToLocalTime().ToString("MM-dd HH:mm:ss") : "时间未知") + " · " + title;
            long? timestamp = item.Time.HasValue ? (long?)item.Time.Value.ToUniversalTime().Ticks : null;
            string comparisonKey = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}",
                item.Log, item.RecordId, timestamp, item.Provider, item.Id);
            return new Finding(header, state, detail.Length > 0 ? share + " · " + detail : share, advice, action, share, title,
                comparisonKey: comparisonKey);
        }
    }
}
