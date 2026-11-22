using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SafeTriage
{
    internal static class NetworkDiagnostics
    {
        private enum LocalConnection { Connected, Failed, Restricted }
        private static readonly Uri ReferenceSite = new Uri("https://www.microsoft.com/");

        public static bool TryTarget(string text, out Uri target)
        {
            target = ReferenceSite;
            text = (text ?? "").Trim();
            if (text.Length == 0)
                return true;

            Uri parsed;
            if (text.Length > 500 || !Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out parsed)
                || parsed.Scheme != "https" || parsed.Port <= 0 || parsed.UserInfo.Length != 0 || parsed.HostNameType == UriHostNameType.Unknown)
                return false;

            target = new UriBuilder("https", parsed.IdnHost, parsed.IsDefaultPort ? -1 : parsed.Port, "/").Uri;
            return true;
        }

        public static Finding CheckLink()
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(adapter => adapter.GetIPProperties()).ToArray();

            if (active.Length == 0)
                return new Finding("网络连接", CheckState.Error, "Windows 当前未报告已连接的网卡。",
                    "检查 Wi-Fi 是否已连接、飞行模式是否开启，或者重新插好网线。", "networkSettings");

            IPAddress[] addresses = active.SelectMany(properties => properties.UnicastAddresses)
                .Select(item => item.Address).Where(address => !IPAddress.IsLoopback(address)).ToArray();
            int usable = addresses.Count(address => !address.IsIPv6LinkLocal && !IsAutomaticV4(address));
            if (usable == 0)
            {
                string detail = addresses.Any(IsAutomaticV4) ? "只看到有限范围的地址，其中有 169.254.x.x 自动分配地址。" :
                    "已连接网卡上没有发现可用于普通上网的 IP 地址。";
                return new Finding("网络连接", CheckState.Warning, detail,
                    "试着重新连接当前 Wi-Fi 或网线。若仍然如此，检查同一网络下其他设备能否上网。", "networkSettings");
            }

            bool gateway = active.SelectMany(properties => properties.GatewayAddresses)
                .Any(item => !item.Address.Equals(IPAddress.Any) && !item.Address.Equals(IPAddress.IPv6Any));
            if (!gateway)
                return new Finding("网络连接", CheckState.Info, "已读到网卡 IP。网卡配置中未读到默认网关。",
                    "如果无法上网，继续检测 DNS 和 HTTPS 连通性。", "network");

            return new Finding("网络连接", CheckState.Clear,
                string.Format("已连接 {0} 个网卡，发现 IP 地址和默认网关。", active.Length));
        }

        private static bool IsAutomaticV4(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
                return false;
            byte[] octets = address.GetAddressBytes();
            return octets[0] == 169 && octets[1] == 254;
        }

        public static Finding CheckProxy()
        {
            bool enabled;
            bool script;
            string configured;
            using (RegistryKey settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            {
                object switchValue = settings == null ? null : settings.GetValue("ProxyEnable");
                enabled = switchValue is int && (int)switchValue != 0;
                configured = settings == null ? "" : settings.GetValue("ProxyServer") as string ?? "";
                script = settings != null && !string.IsNullOrWhiteSpace(settings.GetValue("AutoConfigURL") as string);
            }

            if (!enabled)
                return new Finding("系统代理", script ? CheckState.Info : CheckState.Clear,
                    script ? "当前用户配置了代理脚本。可在网络排查中测试实际使用系统代理的路径。" : "当前用户未开启手动代理或填写代理脚本地址。");

            Uri endpoint;
            if (!TryManualHttpsProxy(configured, out endpoint))
                return new Finding("系统代理", CheckState.Info, "手动代理已开启，未识别到可用于 HTTPS 的固定代理地址。",
                    "打开代理设置确认当前配置。", "proxySettings");

            IPAddress address;
            string host = endpoint.DnsSafeHost;
            bool localName = host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            bool localAddress = IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
            if (!localName && !localAddress)
                return new Finding("系统代理", CheckState.Info, "当前用户开启了非本机的手动代理。可在网络排查中测试实际连接。");

            IPAddress[] addresses = localName ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : new[] { address };
            LocalConnection[] connection = addresses.Select(ip => ConnectLocal(ip, endpoint.Port)).ToArray();
            if (connection.Contains(LocalConnection.Connected))
                return new Finding("系统代理", CheckState.Clear, "已连通系统代理配置指向的本机端口 " + endpoint.Port + "。");
            if (connection.Contains(LocalConnection.Restricted))
                return new Finding("系统代理", CheckState.Unavailable, "当前环境限制了安诊连接本机代理端口。",
                    "可以在代理客户端里核对正在使用的端口。", "proxySettings");

            return new Finding("系统代理", CheckState.Warning,
                "手动代理指向本机端口 " + endpoint.Port + "，本次 TCP 连接未成功。",
                "确认代理客户端正在运行，且端口与 Windows 代理设置一致。若你本来就不使用代理，可在设置里检查是否还开着手动代理。",
                "proxySettings");
        }

        internal static bool TryManualHttpsProxy(string value, out Uri endpoint)
        {
            endpoint = null;
            string server = (value ?? "").Trim();
            if (server.Contains("="))
            {
                server = null;
                foreach (string entry in value.Split(';'))
                {
                    string[] pair = entry.Split(new[] { '=' }, 2);
                    if (pair.Length == 2 && pair[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase))
                    {
                        server = pair[1].Trim();
                        break;
                    }
                }
            }
            return !string.IsNullOrWhiteSpace(server)
                && Uri.TryCreate(server.Contains("://") ? server : "http://" + server, UriKind.Absolute, out endpoint)
                && (endpoint.Scheme == "http" || endpoint.Scheme == "https" || endpoint.Scheme == "socks" || endpoint.Scheme == "socks5")
                && endpoint.Port > 0;
        }

        private static LocalConnection ConnectLocal(IPAddress ip, int port)
        {
            try
            {
                using (var client = new TcpClient(ip.AddressFamily))
                    return client.ConnectAsync(ip, port).Wait(550) && client.Connected ? LocalConnection.Connected : LocalConnection.Failed;
            }
            catch (Exception error)
            {
                var socket = error.GetBaseException() as SocketException;
                if (socket == null)
                    throw;
                return socket.SocketErrorCode == SocketError.AccessDenied ? LocalConnection.Restricted : LocalConnection.Failed;
            }
        }

        internal sealed class HttpsResult
        {
            public int? Status;
            public string Failure;
            public bool Restricted;
            public bool Incomplete;
            public int? RejectedHeadStatus;
            public string GetFailure;
            public bool Unavailable { get { return Restricted || Incomplete; } }
            public bool Reached { get { return Status.HasValue && Status.Value != 407; } }
            public bool UsableAsReference { get { return Status >= 200 && Status < 400; } }
            public bool HeadRejected { get { return !RejectedHeadStatus.HasValue && (Status == 405 || Status == 501); } }

            public string Describe()
            {
                if (!Status.HasValue)
                    return Failure;
                string response = "HTTP " + Status.Value;
                if (RejectedHeadStatus.HasValue)
                    return response + "（GET 补测，HEAD 返回 HTTP " + RejectedHeadStatus.Value + "）";
                return HeadRejected ? response + "（HEAD 不受支持" +
                    (GetFailure == null ? "，未取得 GET 结果" : "，GET 补测：" + GetFailure.TrimEnd('。')) + "）" : response;
            }

            public Finding ToFinding(string name)
            {
                if (Status == 407)
                    return new Finding(name, CheckState.Warning, "返回 HTTP 407，代理要求身份验证。",
                        "检查当前网络或代理账号的配置。", "proxySettings", "返回 HTTP 407，代理要求身份验证。");
                if (Unavailable)
                    return new Finding(name, CheckState.Unavailable, Failure, shareDetail: Failure);
                if (!Reached)
                    return new Finding(name, CheckState.Warning, Failure, shareDetail: Failure);
                if (HeadRejected)
                    return new Finding(name, CheckState.Info, Describe() + "。HTTPS 已收到响应，网页能否正常打开仍待确认。",
                        "可在浏览器打开原始地址核对。", shareDetail: Describe());
                if (Status >= 400)
                    return new Finding(name, CheckState.Info, "收到 " + Describe() + "，网站或中间服务返回了错误。",
                        "用浏览器打开原始地址，核对是否需要登录、是否受访问限制，或是否只有该网站异常。", shareDetail: Describe());
                return new Finding(name, CheckState.Clear, "收到 " + Describe() + " 响应。", shareDetail: Describe());
            }
        }

        private static HttpsResult ReadHttps(Uri target, bool useWindowsProxy, CancellationToken stop)
        {
            return ProbeMethods((method, timeout) => ReadHttpsResponse(target, useWindowsProxy, method, timeout, stop), stop);
        }

        internal static HttpsResult ProbeMethods(Func<string, int, HttpsResult> request, CancellationToken stop)
        {
            const int budget = 3700;
            var elapsed = Stopwatch.StartNew();
            HttpsResult head = request("HEAD", budget);
            int remaining = budget - (int)elapsed.ElapsedMilliseconds;
            if (!head.HeadRejected || remaining <= 0 || stop.IsCancellationRequested)
                return head;

            HttpsResult get = request("GET", remaining);
            if (get.Status.HasValue)
            {
                get.RejectedHeadStatus = head.Status;
                return get;
            }
            head.GetFailure = get.Failure;
            return head;
        }

        private static HttpsResult ReadHttpsResponse(Uri target, bool useWindowsProxy, string method, int timeout, CancellationToken stop)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(target);
                request.Method = method;
                request.Proxy = useWindowsProxy ? WebRequest.GetSystemWebProxy() : null;
                request.AllowAutoRedirect = false;
                request.Timeout = timeout;
                request.ReadWriteTimeout = timeout;
                if (method == "GET")
                    request.KeepAlive = false;
                using (stop.Register(request.Abort))
                using (var response = (HttpWebResponse)request.GetResponse())
                    return new HttpsResult { Status = (int)response.StatusCode };
            }
            catch (WebException error)
            {
                using (WebResponse response = error.Response)
                {
                    var http = response as HttpWebResponse;
                    if (http != null)
                        return new HttpsResult { Status = (int)http.StatusCode };
                }

                var socket = error.GetBaseException() as SocketException;
                if (socket != null && socket.SocketErrorCode == SocketError.AccessDenied)
                    return new HttpsResult { Restricted = true, Failure = "当前运行环境限制了网络连接。" };
                if (error.Status == WebExceptionStatus.RequestCanceled)
                    return new HttpsResult { Incomplete = true, Failure = "HTTPS 探测已停止。" };

                string reason;
                switch (error.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure: reason = "目标域名解析失败。"; break;
                    case WebExceptionStatus.ProxyNameResolutionFailure: reason = "代理主机名解析失败。"; break;
                    case WebExceptionStatus.ConnectFailure: reason = "无法建立连接。"; break;
                    case WebExceptionStatus.Timeout: reason = "等待响应超时。"; break;
                    case WebExceptionStatus.TrustFailure: reason = "Windows 无法验证证书。"; break;
                    case WebExceptionStatus.SecureChannelFailure: reason = "TLS 握手失败。"; break;
                    default: reason = "没有收到 HTTPS 响应（" + error.Status + "）。"; break;
                }
                return new HttpsResult { Failure = reason };
            }
        }

        internal static Finding CheckDns(Uri target)
        {
            IPAddress ip;
            if (IPAddress.TryParse(target.DnsSafeHost, out ip))
                return new Finding("本机 DNS", CheckState.Info, "目标是 IP 地址，可以直接进行 HTTPS 测试。");

            try
            {
                IPAddress[] ips = Dns.GetHostAddresses(target.IdnHost);
                if (ips.Length > 0)
                    return new Finding("本机 DNS", CheckState.Clear, target.IdnHost + " 返回 " + ips.Length + " 个地址。结果可能来自本机缓存。",
                        shareDetail: "解析得到 " + ips.Length + " 个地址。结果可能来自本机缓存。");
            }
            catch (SocketException error)
            {
                if (error.SocketErrorCode == SocketError.AccessDenied)
                    return new Finding("本机 DNS", CheckState.Unavailable, "当前运行环境限制了 DNS 查询。");
                return new Finding("本机 DNS", CheckState.Warning, target.IdnHost + " 查询失败（" + error.SocketErrorCode + "）。",
                    "先确认域名拼写，再检查当前网络的 DNS 设置。", "networkSettings", "解析失败（" + error.SocketErrorCode + "）。");
            }
            return new Finding("本机 DNS", CheckState.Warning, "查询完成，但未返回 IP 地址。",
                "先确认域名拼写，再检查当前网络的 DNS 设置。", "networkSettings");
        }

        private static Task<Finding> ReadLocal(string name, Func<Finding> read)
        {
            return Diagnostics.Probe(read, 4500, message => new Finding(name, CheckState.Unavailable, message));
        }

        internal static async Task<HttpsResult> TestHttps(Uri target, bool useWindowsProxy,
            CancellationToken stop = default(CancellationToken))
        {
            using (var requestStop = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                CancellationToken requestToken = requestStop.Token;
                HttpsResult result = await Diagnostics.Probe(() => ReadHttps(target, useWindowsProxy, requestToken), 5200,
                    message => new HttpsResult { Incomplete = true, Failure = message }, stop).ConfigureAwait(false);
                if (result.Incomplete) requestStop.Cancel();
                return result;
            }
        }

        public static async Task<Report> RunAsync(Uri target)
        {
            Task<Finding> link = ReadLocal("网络连接", CheckLink);
            Task<Finding> proxy = ReadLocal("系统代理", CheckProxy);
            Task<Finding> dns = ReadLocal("本机 DNS", () => CheckDns(target));
            Task<HttpsResult> direct = TestHttps(target, false);
            Task<HttpsResult> system = TestHttps(target, true);
            bool customTarget = target != ReferenceSite;
            Task<HttpsResult> reference = customTarget ? TestHttps(ReferenceSite, true) : system;
            await Task.WhenAll(link, proxy, dns, direct, system, reference);

            var findings = new List<Finding> { link.Result, proxy.Result, dns.Result,
                system.Result.ToFinding("目标 HTTPS · 使用系统代理设置"),
                direct.Result.ToFinding("目标 HTTPS · 应用不使用代理") };
            if (customTarget)
            {
                findings.Add(new Finding("对照站点", reference.Result.Unavailable ? CheckState.Unavailable :
                    reference.Result.UsableAsReference ? CheckState.Clear : CheckState.Info,
                    ReferenceSite.IdnHost + "（使用系统代理设置） · " + reference.Result.Describe()));
            }

            string summary = Summarize(link.Result.State, proxy.Result.State, dns.Result.State,
                direct.Result, system.Result, customTarget && reference.Result.UsableAsReference);
            return new Report(summary, findings.ToArray(), summary);
        }

        private static string Summarize(CheckState link, CheckState proxy, CheckState dns, HttpsResult direct, HttpsResult system, bool referenceReached)
        {
            if (system.Status == 407)
            {
                string directResponse = direct.Reached ? "应用不使用代理时返回 " + direct.Describe() + "。" : "";
                return directResponse + "使用系统代理设置时返回 HTTP 407，代理要求身份验证。先检查代理账号或当前网络的访问要求。";
            }
            if (direct.Unavailable || system.Unavailable)
            {
                string working = direct.Reached ? "不使用代理的请求已收到响应。" : system.Reached ? "使用系统代理设置的请求已收到响应。" : "";
                return working + "有 HTTPS 探测未完成。可查看各路径的检测记录，再用同一目标复查。";
            }
            if (direct.Status.HasValue && system.Status.HasValue && direct.Status != system.Status)
                return "应用不使用代理时返回 " + direct.Describe() + "，使用系统代理设置时返回 " + system.Describe() + "。";
            if (direct.HeadRejected || system.HeadRejected)
                return "应用不使用代理：" + direct.Describe().TrimEnd('。') + "；系统代理设置：" +
                    system.Describe().TrimEnd('。') + "。请用浏览器打开原始地址核对。";
            if (direct.Reached && !system.Reached)
                return proxy == CheckState.Warning ? "不使用代理时收到响应，本机代理端口连接失败。优先检查代理客户端和端口。" :
                    "不使用代理时收到响应，使用系统代理设置时失败。优先核对代理或自动配置脚本。";
            if (!direct.Reached && system.Reached)
                return dns == CheckState.Warning ? "系统路径收到响应，但本机 DNS 查询失败。若仅个别应用打不开，检查该应用的连接方式。" :
                    "系统路径收到响应，但安诊不使用代理的请求失败。若仅个别应用打不开，先检查该应用的代理设置。";
            if (system.Reached)
                return system.Status >= 400 ? "两条路径均返回 " + system.Describe() + "。用浏览器打开原始地址，核对访问限制或站点状态。" :
                    "两种方式均收到目标地址的 HTTPS 响应。如果只有某个浏览器或应用打不开，优先检查其代理和扩展设置。";
            if (referenceReached)
                return dns == CheckState.Warning ? "对照站点能响应，目标域名的本机 DNS 查询失败。先核对域名，再用另一台设备访问同一站点。" :
                    "对照站点能响应，两种方式均未收到目标地址响应。先核对网址，再用另一台设备访问同一站点。";
            if (link == CheckState.Error)
                return "Windows 未报告已连接的网卡。先恢复 Wi-Fi 或有线连接，再复查。";
            if (dns == CheckState.Warning)
                return "本机 DNS 查询未成功，HTTPS 也未收到响应。先核对域名和网络连接，再查看当前 DNS 设置。";
            if (proxy == CheckState.Warning)
                return "本机代理端口连接失败，两种 HTTPS 测试都没收到响应。先检查代理客户端，再核对当前网络。";
            return "HTTPS 测试未收到响应。对照下方的连接、TLS 或超时信息，并检查同一网络下其他设备是否正常。";
        }
    }
}
