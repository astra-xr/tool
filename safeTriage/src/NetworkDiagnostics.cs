using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SafeTriage
{
    internal static class NetworkDiagnostics
    {
        private enum LocalConnection { Connected, Failed, Restricted }
        private static readonly Uri ReferenceSite = new Uri("https://www.microsoft.com/");
        private static readonly string UserAgent = "safeTriage/" + typeof(NetworkDiagnostics).Assembly.GetName().Version.ToString(3);
        private const int RecordingHttpsBudget = 3700;
        private const int ManualHeadTimeout = 8000;
        private const int ManualGetTimeout = 12000;

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

            target = new UriBuilder("https", parsed.IdnHost, parsed.IsDefaultPort ? -1 : parsed.Port, parsed.AbsolutePath).Uri;
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
            public int? ProxyTunnelStatus;
            public string Failure;
            public bool Restricted;
            public bool Incomplete;
            public int? InitialHeadStatus;
            public string GetFailure;
            public string ForbiddenEvidence;
            public bool Unavailable { get { return Restricted || Incomplete; } }
            public bool Reached { get { return Status.HasValue && Status.Value != 407; } }
            public bool UsableAsReference { get { return Status >= 200 && Status < 400; } }
            public bool NeedsGetFallback { get { return !InitialHeadStatus.HasValue && (Status == 403 || Status == 405 || Status == 501); } }

            public string Describe()
            {
                string status = DescribeStatus();
                return string.IsNullOrEmpty(ForbiddenEvidence) ? status : status + "。" + ForbiddenEvidence;
            }

            public string DescribeStatus()
            {
                if (ProxyTunnelStatus.HasValue)
                {
                    string tunnel = "代理 CONNECT 返回 HTTP " + ProxyTunnelStatus.Value;
                    return InitialHeadStatus.HasValue ? tunnel + "（GET 补测，HEAD 返回 HTTP " + InitialHeadStatus.Value + "）" : tunnel;
                }
                if (!Status.HasValue)
                    return Failure;
                string response = "HTTP " + Status.Value;
                if (InitialHeadStatus.HasValue)
                {
                    bool redirect = Status == 301 || Status == 302 || Status == 303 || Status == 307 || Status == 308;
                    return response + (redirect ? "（GET 补测收到重定向，HEAD 返回 HTTP " : "（GET 补测，HEAD 返回 HTTP ") +
                        InitialHeadStatus.Value + "）";
                }
                if (!NeedsGetFallback)
                    return response;
                return response + "（HEAD 返回，" + (GetFailure == null ? "GET 补测未完成" : "GET 补测未取得 HTTP 响应，" + GetFailure.TrimEnd('。')) + "）";
            }

            public Finding ToFinding(string name)
            {
                if (Status == 407 || ProxyTunnelStatus.HasValue)
                {
                    string detail = ProxyTunnelStatus.HasValue ? DescribeStatus() : "返回 " + DescribeStatus();
                    detail += Status == 407 || ProxyTunnelStatus == 407 ? "，代理要求身份验证。" : "，代理未建立到目标站点的 HTTPS 隧道。";
                    if (!string.IsNullOrEmpty(ForbiddenEvidence))
                        detail += ForbiddenEvidence + "。";
                    return new Finding(name, CheckState.Warning, detail,
                        "核对代理客户端的连接日志和当前代理配置。", "proxySettings", detail);
                }
                if (Unavailable)
                    return new Finding(name, CheckState.Unavailable, Failure, shareDetail: Failure);
                if (!Reached)
                    return new Finding(name, CheckState.Warning, Failure, shareDetail: Failure);
                if (NeedsGetFallback)
                    return new Finding(name, CheckState.Info, Describe() + "。HTTPS 已收到响应，网页能否正常打开仍待确认。",
                        "可在浏览器打开原始地址核对。", shareDetail: Describe());
                if (Status >= 400)
                    return new Finding(name, CheckState.Info, "收到 " + Describe() + "，网站或中间服务返回了错误。",
                        "用浏览器打开原始地址，核对是否需要登录、是否受访问限制，或是否只有该网站异常。", shareDetail: Describe());
                return new Finding(name, CheckState.Clear, "收到 " + Describe() + " 响应。", shareDetail: Describe());
            }
        }

        internal static HttpsResult ProbeMethods(Func<string, int, HttpsResult> request, CancellationToken stop, bool recording)
        {
            var elapsed = Stopwatch.StartNew();
            HttpsResult head = request("HEAD", recording ? RecordingHttpsBudget : ManualHeadTimeout);
            int getTimeout = recording ? RecordingHttpsBudget - (int)elapsed.ElapsedMilliseconds : ManualGetTimeout;
            if (!head.NeedsGetFallback || getTimeout <= 0 || stop.IsCancellationRequested)
                return head;

            HttpsResult get = request("GET", getTimeout);
            if (get.Status.HasValue || get.ProxyTunnelStatus.HasValue)
            {
                get.InitialHeadStatus = head.Status;
                if (!string.IsNullOrEmpty(head.ForbiddenEvidence))
                    get.ForbiddenEvidence = string.IsNullOrEmpty(get.ForbiddenEvidence) ? head.ForbiddenEvidence :
                        head.ForbiddenEvidence + "。" + get.ForbiddenEvidence;
                return get;
            }
            head.GetFailure = get.Failure;
            return head;
        }

        internal static string ForbiddenMarkers(WebHeaderCollection headers, string body)
        {
            var markers = new List<string>();
            string server = (headers["Server"] ?? "").Trim();
            foreach (string product in new[] { "AkamaiGHost", "Microsoft-Azure-Application-Gateway", "Microsoft-IIS",
                "Microsoft-HTTPAPI", "cloudflare", "CloudFront", "envoy", "squid", "nginx", "Apache" })
            {
                if (server.Equals(product, StringComparison.OrdinalIgnoreCase) || server.StartsWith(product + "/", StringComparison.OrdinalIgnoreCase))
                    markers.Add("Server=" + product);
            }
            if (headers["X-Azure-Ref"] != null) markers.Add("X-Azure-Ref");
            if (headers["X-MSEdge-Ref"] != null) markers.Add("X-MSEdge-Ref");
            if (string.Equals(headers["cf-mitigated"], "challenge", StringComparison.OrdinalIgnoreCase)) markers.Add("cf-mitigated=challenge");
            if (Regex.IsMatch(headers["X-Squid-Error"] ?? "", @"^ERR_ACCESS_DENIED(?:\s|$)", RegexOptions.IgnoreCase))
                markers.Add("X-Squid-Error=ERR_ACCESS_DENIED");
            if (Regex.IsMatch(body, @"https?://errors\.edgesuite\.net/", RegexOptions.IgnoreCase))
                markers.Add("错误页含 errors.edgesuite.net");
            if (Regex.IsMatch(body, @"\b(?:missing user-agent|user-agent header is required)\b", RegexOptions.IgnoreCase))
                markers.Add("错误页提及缺少 User-Agent");
            return markers.Count == 0 ? "未识别到服务标记" : string.Join("、", markers);
        }

        private static string ErrorBodyPrefix(HttpWebResponse response)
        {
            // Only inspect an already buffered body or a stream with a bounded read.
            try
            {
                Stream stream = response.GetResponseStream();
                if (stream == null || (!stream.CanTimeout && !(stream is MemoryStream)))
                    return "";
                if (stream.CanTimeout) stream.ReadTimeout = 300;
                var buffer = new byte[4096];
                int count = stream.Read(buffer, 0, buffer.Length);
                return Encoding.ASCII.GetString(buffer, 0, count);
            }
            catch (IOException) { return ""; }
            catch (WebException) { return ""; }
            catch (InvalidOperationException) { return ""; }
            catch (NotSupportedException) { return ""; }
        }

        internal static HttpsResult ReadHttpsResponse(Uri target, IWebProxy proxy, string method, int timeout, CancellationToken stop, bool recording)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(target);
                request.Method = method;
                request.Proxy = proxy;
                request.UserAgent = UserAgent;
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
                    {
                        if (string.Equals(http.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                            return new HttpsResult { ProxyTunnelStatus = (int)http.StatusCode };
                        var result = new HttpsResult { Status = (int)http.StatusCode };
                        if (!recording && result.Status == 403)
                            result.ForbiddenEvidence = method + " 拒绝响应：" + ForbiddenMarkers(http.Headers,
                                method == "GET" && !stop.IsCancellationRequested ? ErrorBodyPrefix(http) : "");
                        return result;
                    }
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
                    case WebExceptionStatus.SendFailure: reason = "HTTPS 请求未能完整发出。"; break;
                    case WebExceptionStatus.ReceiveFailure: reason = "未能完整接收 HTTPS 响应。"; break;
                    case WebExceptionStatus.Timeout:
                        reason = "等待响应超时，探测时限 " + (timeout / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + " 秒。";
                        break;
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

        internal static async Task<HttpsResult> TestHttps(Uri target, bool useWindowsProxy, bool recording = false,
            CancellationToken stop = default(CancellationToken))
        {
            const int completionGrace = 1500;
            int limit = (recording ? RecordingHttpsBudget : ManualHeadTimeout + ManualGetTimeout) + completionGrace;
            using (var requestStop = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                CancellationToken requestToken = requestStop.Token;
                HttpsResult result = await Diagnostics.Probe(() =>
                {
                    IWebProxy proxy = useWindowsProxy ? WebRequest.GetSystemWebProxy() : null;
                    return ProbeMethods((method, timeout) => ReadHttpsResponse(target, proxy, method, timeout, requestToken, recording),
                        requestToken, recording);
                }, limit,
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
            if (system.Status == 407 || system.ProxyTunnelStatus.HasValue)
            {
                string directResponse = direct.Reached ? "应用不使用代理时返回 " + direct.DescribeStatus() + "。" : "";
                string systemAdvice = system.Status == 407 || system.ProxyTunnelStatus == 407 ? "，请检查代理身份验证。" : "，请核对代理客户端的连接日志。";
                return directResponse + "系统代理路径的结果为 " + system.DescribeStatus() + systemAdvice;
            }
            if (direct.Unavailable || system.Unavailable)
            {
                string working = direct.Reached ? "不使用代理的请求已收到响应。" : system.Reached ? "使用系统代理设置的请求已收到响应。" : "";
                return working + "有 HTTPS 探测未完成。可查看各路径的检测记录，再用同一目标复查。";
            }
            if (direct.Status.HasValue && system.Status.HasValue && direct.DescribeStatus() != system.DescribeStatus())
                return "应用不使用代理时返回 " + direct.DescribeStatus() + "，使用系统代理设置时返回 " + system.DescribeStatus() + "。";
            if (direct.NeedsGetFallback || system.NeedsGetFallback)
                return "应用不使用代理时，" + direct.DescribeStatus().TrimEnd('。') + "。使用系统代理设置时，" +
                    system.DescribeStatus().TrimEnd('。') + "。请用浏览器打开同一地址核对。";
            if (direct.Reached && !system.Reached)
                return proxy == CheckState.Warning ? "不使用代理时收到响应，本机代理端口连接失败。优先检查代理客户端和端口。" :
                    "不使用代理时收到响应，使用系统代理设置时失败。优先核对代理或自动配置脚本。";
            if (!direct.Reached && system.Reached)
                return dns == CheckState.Warning ? "系统路径收到响应，但本机 DNS 查询失败。若仅个别应用打不开，检查该应用的连接方式。" :
                    "系统路径收到响应，但安诊不使用代理的请求失败。若仅个别应用打不开，先检查该应用的代理设置。";
            if (system.Reached)
                return system.Status >= 400 ? "两条探测路径均收到 " + system.DescribeStatus() + "。用浏览器打开同一地址，核对访问限制或站点状态。" :
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
