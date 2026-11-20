<p align="center"><img src="assets/safetriage.png" width="96" alt="safeTriage 图标"></p>

# 安诊 safeTriage 0.4.0

Windows 故障排查助手。检查电脑和网络异常，查看检测依据与处理建议，复查后对比前后变化。

采用 C# / WPF，面向 Windows 11 x64，依赖 .NET Framework 4.8。运行时无第三方包依赖。

## 下载

[Windows x64 安装版](https://github.com/charm-xr/tool/releases/download/safetriage-v0.4.0/safeTriage-Setup-Windows-x64.exe)带中文安装向导，可选择安装目录和桌面快捷方式。安装后可从开始菜单打开，或通过 Windows「已安装的应用」卸载。

[Windows x64 便携版](https://github.com/charm-xr/tool/releases/download/safetriage-v0.4.0/safeTriage-Windows-x64.exe)可直接运行。两种下载的 SHA-256 校验文件见 [安诊 0.4.0 Release](https://github.com/charm-xr/tool/releases/tag/safetriage-v0.4.0)。

## 功能

| 功能 | 具体行为 |
| --- | --- |
| 一键排查 | 检查系统盘空间、短时 CPU 和内存占用、网卡、系统代理，以及近七天的蓝屏、异常关机和更新失败记录。 |
| 网络排查 | 对比系统代理设置和应用不使用代理的连接路径。间歇断网记录持续约 60 秒，逐轮检查网关、DNS 和 HTTPS，可提前结束并标记故障时刻。 |
| 专项排查 | 检查默认声音输出、音频服务、USB 识别与问题代码、近期启动计时。存储排查提供各本地卷余量、物理磁盘健康读数和近七天的存储事件。卡顿记录持续 30 秒，可标记故障并查看相关负载。 |
| 故障时间线 | 按本机时间查找前后各 10 分钟的应用闪退、蓝屏、异常重启、显示驱动、硬件和更新记录。 |
| 复查与报告 | 同一会话内对比同一项目的两次结果，可预览并保存脱敏文本报告。 |

## 检测边界

检测过程只读。报告导出省略域名、IP、路径和设备、进程名称。日志、物理磁盘健康、服务状态和性能计数的可用性取决于当前 Windows 环境及权限。

单次网络检测只访问网站首页。自选网站会和 `www.microsoft.com` 对照，两条测试路径均可能经过 VPN。卡顿报告同时展示系统物理磁盘吞吐量。具体进程的磁盘活动可从报告打开 Windows 资源监视器核对。

## 本地构建

在 `safeTriage` 目录用 Windows PowerShell 或 PowerShell 运行：

```powershell
.\build.ps1 -OutputName safeTriage-Windows-x64.exe
```

构建依赖 Windows 的 .NET Framework 4.8 x64 编译器和 WPF 组件。项目源码位于 [src/](src/)。

使用 Inno Setup 6.7 或更高版本生成安装包：

```powershell
.\tools\build-installer.ps1 -AppPath .\safeTriage-Windows-x64.exe
```

安装包和 SHA-256 文件输出到 `release/`。
