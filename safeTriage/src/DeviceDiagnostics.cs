using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace SafeTriage
{
    internal static class DeviceDiagnostics
    {
        public static async Task<Report> AudioAsync()
        {
            Finding[] findings = await Task.WhenAll(
                Diagnostics.Probe(() => AudioService("Windows 音频服务", "Audiosrv"), 4000, error => Missing("Windows 音频服务", error)),
                Diagnostics.Probe(() => AudioService("音频端点生成服务", "AudioEndpointBuilder"), 4000, error => Missing("音频端点生成服务", error)),
                Diagnostics.Probe(ReadOutputDevices, 5000, error => Missing("声音输出设备", error)));
            return new Report(AudioSummary(findings), findings);
        }

        private static string AudioSummary(Finding[] findings)
        {
            if (findings.Any(f => f.State == CheckState.Warning || f.State == CheckState.Error))
                return "有音频服务或输出设备需要检查。展开相关项目查看依据和处理方法。";
            if (findings.Any(f => f.State == CheckState.Unavailable))
                return "部分音频信息没有取到。可以在声音设置里核对输出设备并播放测试音。";
            return "Windows 音频服务正在运行。请核对当前默认输出是不是你想用的扬声器或耳机。";
        }

        private static Finding AudioService(string title, string name)
        {
            try
            {
                using (var service = new ServiceController(name))
                {
                    ServiceControllerStatus status = service.Status;
                    bool running = status == ServiceControllerStatus.Running;
                    string detail = running ? "服务正在运行。" : "服务当前状态为 " + status + "。";
                    return new Finding(title, running ? CheckState.Clear : CheckState.Warning, detail,
                        running ? null : "在服务管理器里核对该服务状态。", running ? null : "services", detail);
                }
            }
            catch (InvalidOperationException error)
            {
                var windows = error.InnerException as System.ComponentModel.Win32Exception;
                string evidence = windows == null ? "Windows 未返回该服务的状态。" :
                    "Windows 未返回该服务的状态（错误 " + windows.NativeErrorCode + "）。";
                return new Finding(title, CheckState.Unavailable, evidence, "可在服务管理器核对该服务状态。", "services", evidence);
            }
        }

        private static Finding Missing(string title, string error)
        {
            return new Finding(title, CheckState.Unavailable, error, shareDetail: error);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid Format;
            public uint Id;
        }

        // The app is built for x64. Only the string representation of PROPVARIANT is read here.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropertyValue
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropertyValue value);

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out IAudioCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IAudioDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IAudioDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioDevice
        {
            [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(uint mode, out IAudioProperties properties);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioProperties
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
        }

        private static string AudioName(IAudioDevice device)
        {
            IAudioProperties properties;
            if (device.OpenPropertyStore(0, out properties) < 0 || properties == null)
                return "名称未取得";
            try
            {
                var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                PropertyValue value;
                int result = properties.GetValue(ref key, out value);
                try
                {
                    if (result >= 0 && value.Type == 31 && value.Pointer != IntPtr.Zero)
                    {
                        string name = WindowsEvents.ShortValue(Marshal.PtrToStringUni(value.Pointer));
                        if (name.Length > 0)
                            return name;
                    }
                    return "名称未取得";
                }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(properties); }
        }

        private static Finding ReadOutputDevices()
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            var enumerator = (IAudioEnumerator)Activator.CreateInstance(type);
            IAudioCollection collection = null;
            IAudioDevice current = null;
            const int render = 0, multimedia = 1, notFound = unchecked((int)0x80070490);
            const uint active = 1;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(render, active, out collection));
                uint count;
                Marshal.ThrowExceptionForHR(collection.GetCount(out count));

                var names = new List<string>();
                for (uint i = 0; i < Math.Min(count, 12u); i++)
                {
                    IAudioDevice device;
                    if (collection.Item(i, out device) >= 0 && device != null)
                    {
                        try { names.Add(AudioName(device)); }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }

                int result = enumerator.GetDefaultAudioEndpoint(render, multimedia, out current);
                if (result < 0 && result != notFound)
                    return Missing("声音输出设备", string.Format("Windows 读取默认输出失败（0x{0:X8}）。", (uint)result));

                string safeDetail = count + " 个可用输出。默认输出" + (current == null ? "未设置。" : "已设置。");
                string detail = "默认输出 " + (current == null ? "未设置" : AudioName(current)) +
                    "。\n" + count + " 个可用输出" + (names.Count > 0 ? "：" + string.Join("、", names) : "。");
                return new Finding("声音输出设备", current == null ? CheckState.Warning : CheckState.Clear, detail,
                    current == null ? "确认耳机已插好，或蓝牙已连接音频。打开声音设置查看可用输出。" :
                    "在声音设置里选择实际使用的设备，再播放测试音。也可检查音量混合器。",
                    "sound", safeDetail);
            }
            finally
            {
                if (current != null) Marshal.ReleaseComObject(current);
                if (collection != null) Marshal.ReleaseComObject(collection);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        private const uint Present = 0x100;
        private const uint BufferSmall = 0x1A;
        private const uint HasProblem = 0x400;
        private const string UsbSource = "USB";

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID_List(string filter, IntPtr output, uint length, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNode(out uint device, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint device, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_DevNode_Registry_Property(uint device, uint property, out uint type,
            [Out] byte[] data, ref uint length, uint flags);

        public static Task<Report> UsbAsync()
        {
            return Diagnostics.Probe(ReadUsb, 6000, UsbUnavailable);
        }

        internal static Report UsbUnavailable(string error)
        {
            return new Report(error, new[] { Missing("USB 识别情况", error) }, error, incompleteSources: new[] { UsbSource });
        }

        private sealed class UsbDevice
        {
            public string Name;
            public uint? Problem;
        }

        private static Report ReadUsb()
        {
            var devices = new List<UsbDevice>();
            foreach (string id in ReadDeviceIds())
            {
                if (!id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) &&
                    !id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase))
                    continue;

                uint device, status, problem;
                if (CM_Locate_DevNode(out device, id, 0) != 0)
                    continue; // Unplugged during the scan.

                string name = ReadDeviceName(device, 0xD);
                if (name.Length == 0)
                    name = ReadDeviceName(device, 1);
                devices.Add(new UsbDevice
                {
                    Name = name.Length > 0 ? name : "名称未取得的 USB 设备",
                    Problem = CM_Get_DevNode_Status(out status, out problem, device, 0) == 0 ? (uint?)((status & HasProblem) != 0 ? problem : 0) : null
                });
            }

            int issues = devices.Count(device => device.Problem.GetValueOrDefault() != 0);
            string counts = string.Format("已枚举 {0} 个 USB 设备节点，{1} 个带设备问题代码。包含集线器和设备接口。", devices.Count, issues);
            var findings = new List<Finding>
            {
                new Finding("USB 识别情况", issues > 0 ? CheckState.Warning : CheckState.Info, counts,
                    "插拔目标设备后复查，比较设备名称和数量。也可以换一个 USB 口再试。", "devices", counts)
            };
            foreach (var group in devices.GroupBy(device => device.Name).OrderByDescending(group => group.Any(d => d.Problem.GetValueOrDefault() > 0)).ThenBy(group => group.Key))
            {
                uint[] codes = group.Where(device => device.Problem.GetValueOrDefault() != 0).Select(device => device.Problem.Value).Distinct().ToArray();
                int unknown = group.Count(item => !item.Problem.HasValue);
                CheckState state = codes.Length > 0 ? CheckState.Warning : unknown > 0 ? CheckState.Unavailable : CheckState.Clear;
                string detail = group.Count() + " 个已枚举节点。";
                if (codes.Length > 0)
                    detail += "问题代码 " + string.Join("、", codes) + "。";
                if (unknown > 0)
                    detail += unknown + " 个节点的状态无法读取。";
                string advice = codes.Length > 0 ? string.Join(" ", codes.Select(UsbAdvice)) : null;
                findings.Add(new Finding(group.Key, state, detail, advice, codes.Length > 0 ? "devices" : null, detail, "USB 设备条目",
                    source: UsbSource));
            }
            return new Report(counts, findings.ToArray(), counts);
        }

        private static string UsbAdvice(uint code)
        {
            switch (code)
            {
                case 10: return "设备无法启动。可换 USB 口重插，再核对驱动。";
                case 22: return "设备已禁用。可在设备管理器确认是否要启用。";
                case 28: return "设备驱动未安装。可查 Windows 更新或厂商提供的驱动。";
                case 43: return "设备报告了问题。先检查连接或线材，再在设备管理器展开详情。";
                default: return "可在设备管理器按问题代码核对设备状态。";
            }
        }

        private static string ReadDeviceName(uint device, uint property)
        {
            byte[] data = new byte[1024];
            uint length = (uint)data.Length, type;
            if (CM_Get_DevNode_Registry_Property(device, property, out type, data, ref length, 0) != 0 || type != 1)
                return "";
            return WindowsEvents.ShortValue(Encoding.Unicode.GetString(data, 0, (int)length).TrimEnd('\0'));
        }

        private static string[] ReadDeviceIds()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint size;
                if (CM_Get_Device_ID_List_Size(out size, null, Present) != 0 || size > 2 * 1024 * 1024)
                    break;
                IntPtr buffer = Marshal.AllocHGlobal(checked((int)size * sizeof(char)));
                try
                {
                    uint status = CM_Get_Device_ID_List(null, buffer, size, Present);
                    if (status == 0)
                        return ParseDeviceIds(Marshal.PtrToStringUni(buffer, (int)size));
                    if (status != BufferSmall)
                        break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("USB device enumeration failed.");
        }

        internal static string[] ParseDeviceIds(string buffer)
        {
            if (string.IsNullOrEmpty(buffer) || buffer[0] == '\0')
                return new string[0];
            int end = buffer.IndexOf("\0\0", StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidOperationException("The Windows device list is missing its terminator.");
            return buffer.Substring(0, end).Split('\0');
        }
    }

    internal static class StartupDiagnostics
    {
        private const string Log = "Microsoft-Windows-Diagnostics-Performance/Operational";

        public static Task<Report> RunAsync()
        {
            return Diagnostics.Probe(ReadBootHistory, 5500, error => new Report(error, new[]
            {
                new Finding("启动性能日志", CheckState.Unavailable, error,
                    "也可以先在任务管理器查看启动应用。", "startup", error)
            }));
        }

        private static Report ReadBootHistory()
        {
            var boots = WindowsEvents.ReadRecent(Log, "EventID=100", 12)
                .Where(item => item.Milliseconds("BootTime") > 0).Take(5).ToArray();
            if (boots.Length == 0)
                return new Report("最近七天没有可用的 Windows 启动计时。", new[]
                {
                    new Finding("启动性能日志", CheckState.Info, "等待下一次完整启动后复查。",
                        "可先在任务管理器查看启动应用。", "startup")
                });

            WindowsEvent latest = boots[0];
            long total = latest.Milliseconds("BootTime"), main = latest.Milliseconds("MainPathBootTime");
            long post = latest.Milliseconds("BootPostBootTime");
            long[] previous = boots.Skip(1).Select(item => item.Milliseconds("BootTime")).OrderBy(value => value).ToArray();
            double baseline = previous.Length == 0 ? 0 :
                previous[(previous.Length - 1) / 2] / 2.0 + previous[previous.Length / 2] / 2.0;
            bool slower = baseline > 0 && total >= baseline * 1.25 && total - baseline >= 10000;
            string summary = "最近一次 Windows 启动计时 " + Seconds(total) + "。";
            if (baseline > 0)
                summary += "此前 " + previous.Length + " 次的中位数为 " + Seconds(baseline) + "。";

            string advice = post > main ?
                "桌面出现后的初始化耗时较长。先检查任务管理器的启动应用和后台任务。" :
                "可先核对最近的 Windows 更新、驱动变化和存储状态。";
            var findings = new List<Finding>
            {
                new Finding("最近一次启动", slower ? CheckState.Warning : CheckState.Info, DescribeBoot(latest), advice, "startup", DescribeBoot(latest))
            };
            if (previous.Length != 0)
            {
                string history = string.Join("\n", boots.Skip(1).Select(DescribeBoot));
                findings.Add(new Finding("启动历史", CheckState.Info, history, shareDetail: history));
            }

            if (latest.Time.HasValue)
            {
                var logged = new DateTimeOffset(latest.Time.Value);
                var window = logged.AddMilliseconds(-Math.Min(total, 600000)).AddMinutes(-1);
                try
                {
                    var events = WindowsEvents.ReadBetween(Log, "EventID=101 or EventID=102 or EventID=103", window, logged.AddMinutes(2), 15);
                    if (events.Length > 0)
                    {
                        var entries = events.Select(item => FormatDelay(item, true));
                        var safeEntries = events.Select(item => FormatDelay(item, false));
                        findings.Add(new Finding("附近的启动延迟记录", CheckState.Info, string.Join("\n", entries),
                            "这是与最近启动记录时间接近的线索。可到任务管理器核对仍在自启的应用。", "startup", string.Join("\n", safeEntries)));
                    }
                }
                catch (UnauthorizedAccessException) { AddUnavailableDelay(findings); }
                catch (System.Diagnostics.Eventing.Reader.EventLogException) { AddUnavailableDelay(findings); }
            }
            return new Report(summary, findings.ToArray(), summary);
        }

        private static void AddUnavailableDelay(List<Finding> findings)
        {
            findings.Add(new Finding("附近的启动延迟记录", CheckState.Unavailable, "无法读取详细延迟事件，已保留可用的启动计时。"));
        }

        private static string DescribeBoot(WindowsEvent item)
        {
            DateTimeOffset boot;
            string time = DateTimeOffset.TryParse(item.Read("BootStartTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out boot)
                ? boot.ToLocalTime().ToString("MM-dd HH:mm") : item.Time.HasValue ? item.Time.Value.ToLocalTime().ToString("MM-dd HH:mm") : "时间未知";
            string description = time + " · 总计 " + Seconds(item.Milliseconds("BootTime"));
            long main = item.Milliseconds("MainPathBootTime"), post = item.Milliseconds("BootPostBootTime");
            if (main > 0) description += " · 桌面出现前 " + Seconds(main);
            if (post > 0) description += " · 桌面出现后 " + Seconds(post);
            return description;
        }

        private static string FormatDelay(WindowsEvent item, bool names)
        {
            string label = item.Id == 101 ? "启动应用" : item.Id == 102 ? "驱动" : "服务";
            string name = names ? WindowsEvents.ShortValue(item.Read("Name")) : "";
            long total = item.Milliseconds("TotalTime"), added = item.Milliseconds("DegradationTime");
            if (name.Length > 0) label += " " + name;
            string detail = label + "（事件 " + item.Id + "）";
            if (total > 0) detail += " · 耗时 " + Seconds(total);
            if (added > 0) detail += " · 增加 " + Seconds(added);
            return detail;
        }

        private static string Seconds(double ms) { return (ms / 1000).ToString("0.0") + " 秒"; }
    }
}
