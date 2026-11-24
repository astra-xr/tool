安诊 safeTriage 0.6.4，适用于 Windows 11 x64。

- 区分代理拒绝建立 HTTPS 隧道时的 CONNECT 状态和目标网页的 HTTP 状态。
- 可检测指定 HTTPS 路径，查询参数和片段会被忽略。探测请求使用安诊的产品名和版本号作为 User-Agent。
- 目标返回 HEAD 403、405 或 501 时，沿同一路径补测 GET。单次检测为 HEAD 和 GET 分别提供独立的等待时限。
- 展示 403 响应中可识别的服务标记，以及请求发送或响应接收失败的提示。

下载 `safeTriage-Setup-Windows-x64.exe` 使用中文安装向导，或下载 `safeTriage-Windows-x64.exe` 直接运行便携版。两个版本均附 SHA-256 校验文件。
