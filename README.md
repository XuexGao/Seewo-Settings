# 希沃助手 (SeewoAssistant)

一个基于 **WinUI 3** 的 Windows 桌面工具集，用来自主掌控教室/办公电脑上的摄像头、麦克风和屏幕内容，并对希沃（Seewo）系列软件做统一管控。

界面为简体中文，浅色为主并跟随系统深色主题，使用 Mica 背景与卡片式布局。

---

## 目录

- [功能模块](#功能模块)
  - [模块一：虚拟摄像头](#模块一虚拟摄像头)
  - [模块二：摄像头与麦克风调用提醒](#模块二摄像头与麦克风调用提醒)
  - [模块三：防止屏幕画面被捕获](#模块三防止屏幕画面被捕获)
  - [模块四：希沃软件管控](#模块四希沃软件管控)
  - [模块五：定时任务](#模块五定时任务)
- [系统要求](#系统要求)
- [下载与安装](#下载与安装)
- [从源码构建](#从源码构建)
- [权限说明](#权限说明)
- [项目结构](#项目结构)
- [已知限制](#已知限制)
- [常见问题](#常见问题)

---

## 功能模块

### 模块一：虚拟摄像头

向系统注册一个虚拟摄像头，让 Zoom、Teams、微信、OBS 等应用可以选择它，并推送你准备的画面。

支持三种画面来源：

| 来源 | 说明 |
| --- | --- |
| 纯色 | 任意 `#RRGGBB` 颜色，内置常用色快选 |
| 本地图片 | PNG / JPG / BMP / GIF / TIFF / WebP，按比例缩放居中，不变形 |
| 动态测试画面 | 彩条 + 灰阶渐变 + 移动扫描条，用于确认摄像头是否存活、肉眼观察延迟 |

**两套后端实现，按系统版本自动选择：**

| 系统 | 实现 | 可见范围 |
| --- | --- | --- |
| Windows 11 内部版本 22000+ | 官方 `MFCreateVirtualCamera` + 自研 Media Foundation 自定义媒体源 COM 组件 | 设备管理器、Windows「设置 → 摄像头」、所有应用（含 UWP） |
| Windows 10 版本 2004（19041）+ | DirectShow 源滤镜 | DirectShow 系应用（Zoom、OBS、QQ/微信、ffmpeg 等）；**不出现在 Windows「设置」的摄像头列表中，UWP 应用不可见** |

> **为什么 Windows 10 只能用 DirectShow？**
> `MFCreateVirtualCamera` / `IMFVirtualCamera` 是 Windows 11 内部版本 22000 才引入的 API，
> Windows 10 上根本不存在这个导出。微软官方在 Windows 10 上**没有**用户态虚拟摄像头方案，
> 唯一的替代是 AVStream 内核驱动——那需要 EV 代码签名，本项目明确不采用。
> 因此 Windows 10 使用 DirectShow 源滤镜作为回退。

**帧传输机制：** 媒体源运行在 `FrameServer` 服务进程（可能是会话 0），UI 在交互会话，两者通过
命名共享内存节（`Global\SeewoAssistant.VCam.Frame.v1`）+ 命名事件通信。UI 侧写入 BGRA 帧，
媒体源每个 `RequestSample` 读取并转换为协商的输出格式（NV12 / RGB32）。

**关键设计：** 媒体源不依赖 UI 进程存活。共享节不存在、内容过期（超过 2 秒）或格式非法时，
媒体源自动回退到内置动态测试画面，**保证任何情况下摄像头都有画面输出，不会黑屏**。

---

### 模块二：摄像头与麦克风调用提醒

任何程序开始使用摄像头或麦克风时，立即弹出提醒。

**技术选型依据：**

| 方案 | 结论 |
| --- | --- |
| 全局 API Hook | ❌ **排除。** 摄像头/麦克风访问不是在被观测进程的用户态 API 里完成的，而是由 Media Foundation 转交给 FrameServer 服务。在观察者进程里 Hook 根本看不到别的进程的设备访问 |
| ETW 事件追踪 | ⚠️ **不作为主方案。** 能承载这类信息的 Provider 不是稳定的公开契约；替代方案（内核文件 Provider）会产生海量无关事件，与「资源占用最小」的要求冲突 |
| **Capability Access Manager 注册表监控** | ✅ **采用。** 这是**有文档的**位置，通过 `RegNotifyChangeKeyValue` 事件驱动，**空闲时 CPU 占用为 0**，Windows 10 2004+ 和 Windows 11 都支持，**不需要管理员权限**，而且这正是系统自己读取的同一份数据 |

**工作原理：** 监控
`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{webcam,microphone}`。
每个应用一个子键，`LastUsedTimeStart` / `LastUsedTimeStop` 记录最近一次使用区间，
`LastUsedTimeStop == 0` 表示**当前正在使用**。桌面应用的键名是路径把反斜杠换成 `#`
（`C:#Program Files#Zoom#bin#Zoom.exe`），会还原显示。

注册表只告诉你是哪个应用、什么时候，不告诉你 PID。需要精确归因时，可选的**句柄扫描**
（`NtQuerySystemInformation` + 复制句柄 + `NtQueryObject`）能找出持有摄像头设备接口句柄的进程，
但需要 `SeDebugPrivilege`（即管理员）。该扫描**只在注册表变化时触发**，不轮询。

**提醒方式：** 双通道，因为 Toast 可能被「专注助手」静默吞掉，而摄像头/麦克风提醒恰恰是
最不能被静默的场景：

1. Windows Toast 通知（Windows App SDK `AppNotificationManager`，非打包应用无需创建开始菜单快捷方式）
2. 可选的置顶醒目横幅（原生 Win32 分层窗口，`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`，不抢焦点、不进 Alt+Tab）

**白名单：** 按可执行文件名或完整路径排除，比如允许 Zoom 静默使用。

---

### 模块三：防止屏幕画面被捕获

让指定窗口在截图、录屏、屏幕共享中「隐身」或变成黑块。

**核心限制（必须理解）：** `SetWindowDisplayAffinity` **只能作用于调用进程自己拥有的窗口**。
内核在 `win32kfull.sys` 里会校验 `HWND` 是否属于调用方进程，跨进程调用直接返回
`ERROR_ACCESS_DENIED`。这是设计使然，没有用户态的绕过办法。

因此提供两种作用域：

| 作用域 | 实现 | 风险 |
| --- | --- | --- |
| **本进程窗口**（默认） | 直接调用 `SetWindowDisplayAffinity` | 无。永远可用，不会被拦截 |
| **跨进程**（默认关闭，需显式开启） | 把载荷 DLL 注入目标进程，由它代为调用 | 可能被杀软主动防御拦截；这是技术固有属性 |

**两种保护模式：**

- `WDA_EXCLUDEFROMCAPTURE`（0x11）——**穿透隐身**，窗口中在截图/录屏中完全不出现
- `WDA_MONITOR`（0x01）——**黑块遮蔽**，截图中显示为纯黑方块，保留占位

> `WDA_EXCLUDEFROMCAPTURE` 需要 Windows 10 版本 2004（内部版本 19041）及以上。
> 更低版本的系统会把它当作 `WDA_MONITOR` 处理，程序会检测并如实提示，不会假装提供完整保护。

**跨进程方案为什么不用 shellcode：** 参考项目
[NoMoreCapture](https://github.com/lilith-is-all-you-need/NoMoreCapture) 用远程 shellcode 加手工 PE
导出表解析来定位 API 地址，紧凑但不可调试——崩溃时没有堆栈，而且杀软看到的正是它被训练要拦截的模式。
本项目改为注入一个**可检查、可版本管理、自我卸载**的真实 DLL（`SeewoCaptureGuard.Payload.dll`），
能力相同但可维护性高得多。代价（可能触发主动防御）是无法回避的，所以该功能默认关闭。

**安全护栏：** 硬编码拒绝注入 `dwm.exe`、`explorer.exe`、`csrss.exe`、`winlogon.exe`、
`lsass.exe`、`services.exe`、`svchost.exe` 等关键进程（注入桌面合成器或外壳可能导致黑屏，
注入关键系统进程会导致蓝屏）。位数不匹配（32 位目标需要 32 位载荷）会提前检测并明确报错。

**窗口选择：** 可搜索的窗口列表（显示标题、进程名、类名、句柄、当前保护状态），
外加 **Spy++ 式雷达取景**——按住拖到目标窗口上释放即可选中。

---

### 模块四：希沃软件管控

| 动作 | 实现 |
| --- | --- |
| 挂起 / 恢复 | `NtSuspendProcess` / `NtResumeProcess`（`ntdll` 未文档化 API，是用户态冻结进程的唯一途径，自 XP 起稳定） |
| 结束进程 | `TerminateProcess` |
| 禁止联网 | `INetFwPolicy2` COM 创建**入站 + 出站**双向阻断规则（只用出站的话程序仍可被连接，不符合「禁止联网」的通常含义） |
| 禁止开机自启 | 覆盖全部四种真实机制：计划任务（`schtasks.exe`）、注册表 Run 键（含 WOW6432Node）、启动文件夹快捷方式、自动启动服务（`sc.exe config`） |

**发现而不是猜测：** 不硬编码希沃进程名清单（希沃产品线版本间会改名，猜错就会静默失效），
而是**扫描本机** `Program Files`、`Program Files (x86)`、`ProgramData`、`%LOCALAPPDATA%`，
按 `seewo` / `easinote` / `希沃` / `swproxy` / `swupdate` 等关键字匹配目录名、文件名和文件描述，
只把**实际存在**的可执行文件列为规则，并区分「已识别」和「可能是」。

**可逆性：** 禁用注册表 Run 项时是**改名**（加 `.seewoassistant-disabled` 后缀）而不是删除，
原始命令完整保留，可精确还原——这也是 Windows 任务管理器自己的做法。

---

### 模块五：定时任务

把以上任何功能自由组合成定时序列。

内置 **5 字段 cron 解析器**（`分 时 日 月 周`），支持 `*`、`5`、`1,3,5`、`1-5`、`*/15`、`0-30/10`
以及 `@daily` / `@hourly` / `@weekly` / `@monthly` / `@yearly` 宏。周字段同时接受 `0` 和 `7` 表示周日。

**日/周语义遵循 Vixie cron：** 当「日」和「周」**都**被限定时，**任一**匹配即可。
即 `0 9 1 * 1` 表示「每月 1 日**或**每周一」，而不是「既是 1 日又是周一」。
只限定其中一个时，就只由那一个决定。这一点如果搞错，表达式含义会完全变样。

**示例：**

```
0 8 * * 1-5     工作日 08:00 → 恢复希沃软件 + 启动虚拟摄像头推送校徽 + 开启防截屏
30 17 * * *     每天 17:30 → 挂起希沃软件 + 停止虚拟摄像头 + 关闭防截屏 + 关机
0 9 * * 1       每周一 09:00 → 扫描并禁用希沃开机自启
```

**调度行为（如实说明）：**

- 每 30 秒 tick 一次。用轮询而不是「精确武装到下一次触发时刻的定时器」，是因为后者在系统
  休眠唤醒、夏令时切换、时钟调整后都需要重新武装；30 秒轮询的开销不可测量，但对这些情况全部免疫。
- **错过的触发不会补跑。** 如果机器在 08:00 处于睡眠、09:00 才唤醒，08:00 那次不会延迟执行，
  只有下一个计划时刻会触发。
- 上一个实例还在运行时，下一次触发会被**跳过**并记录，不会并发执行。

**系统级电源操作：** 关机 / 重启使用 `InitiateSystemShutdownExW`（支持倒计时和屏上提示，
用户有机会取消），注销使用 `ExitWindowsEx`，锁定使用 `LockWorkStation`。
需要 `SeShutdownPrivilege`，程序会按需通过 `AdjustTokenPrivileges` 提权。

> **关于「定时开机」：** 本程序**不提供**这个功能，因为它无法可靠实现。
> 处于完全关机（S5）状态的电脑上没有操作系统在运行，软件无从唤醒它。
> 唯二可靠的途径是 **BIOS/UEFI 的 RTC 定时唤醒**和 **Wake-on-LAN**，两者都在 Windows 之外配置。
> 与其提供一个不可靠的按钮，不如把这一点说清楚并在界面上给出配置指引。

---

## 系统要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 版本 2004（内部版本 19041）或更高；Windows 11 推荐 |
| 架构 | x64 或 ARM64 |
| 运行时 | **无需安装。** 发行包是自包含单文件，不依赖 .NET 运行时或 Windows App SDK 运行时 |
| 权限 | 基础功能无需管理员；部分功能需要，见下 |

### 各功能对管理员权限的依赖

| 功能 | 是否需要管理员 |
| --- | --- |
| 隐私监控（摄像头/麦克风提醒） | 否 |
| 保护**本程序自己**的窗口 | 否 |
| 创建虚拟摄像头实例 | 否 |
| 注册虚拟摄像头 COM 组件 | **是**（写 HKLM） |
| 跨进程防截屏（注入） | 通常需要（目标进程属于其他用户时） |
| 防火墙规则（禁止联网） | **是** |
| 修改计划任务 / 服务启动类型 | **是** |
| 定时关机 / 重启 | **是** |
| 挂起 / 结束其他用户的进程 | **是** |

程序以 `asInvoker` 运行，**不会**主动请求提权（对常驻托盘程序来说那是个更差的取舍）。
未提权时相关操作会给出明确提示，而不是静默失败；标题栏和诊断页会显示当前权限状态。

---

## 下载与安装

从 [Releases](../../releases) 下载对应架构的压缩包：

- `SeewoAssistant-win-x64.zip`
- `SeewoAssistant-win-arm64.zip`

解压到任意目录，双击 `SeewoAssistant.exe` 即可运行，无需安装。

**首次使用虚拟摄像头**需要注册原生组件（写 HKLM，需要管理员）。以管理员身份打开 PowerShell：

```powershell
cd <解压目录>\scripts
.\Install-Native.ps1 -Action Install
```

该脚本会按系统版本自动选择后端：Windows 11 注册 Media Foundation 媒体源，
Windows 10 注册 DirectShow 滤镜。查看当前状态：

```powershell
.\Install-Native.ps1 -Action Status
```

卸载：

```powershell
.\Install-Native.ps1 -Action Uninstall
```

---

## 从源码构建

### 本地构建

需要 **Visual Studio 2022**（含「使用 C++ 的桌面开发」和「.NET 桌面开发」工作负载）、
**.NET 8 SDK**、**Windows 11 SDK 10.0.26100**。

```powershell
# 原生组件
msbuild native/SeewoVirtualCamera/SeewoVirtualCamera.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoVirtualCamera.DShow/SeewoVirtualCamera.DShow.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoVirtualCamera.DShow/SeewoVirtualCamera.DShow.vcxproj /p:Configuration=Release /p:Platform=Win32
msbuild native/SeewoVirtualCamera.Setup/SeewoVirtualCamera.Setup.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoCaptureGuard.Payload/SeewoCaptureGuard.Payload.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoCaptureGuard.Payload/SeewoCaptureGuard.Payload.vcxproj /p:Configuration=Release /p:Platform=Win32

# 测试
dotnet test src/SeewoAssistant.Tests/SeewoAssistant.Tests.csproj -c Release

# 主程序
dotnet publish src/SeewoAssistant/SeewoAssistant.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
  -o artifacts/publish/win-x64
```

也可以直接用 `SeewoAssistant.sln` 在 Visual Studio 中打开。

### GitHub Actions

`.github/workflows/build.yml` 在每次 push 和 PR 时：

1. 编译全部 5 个原生项目（x64 与 Win32）
2. 构建托管项目并运行单元测试
3. `dotnet publish` 出自包含单文件 exe（win-x64 与 win-arm64）
4. 组装成 `SeewoAssistant.exe` + `SeewoVirtualCamera.Setup.exe` + `native/x64` + `native/x86` + `scripts` 的目录结构
5. 打包为 zip 并作为 artifact 上传

推送 `v*` 标签时会额外创建 GitHub Release 并附上这些压缩包。

---

## 权限说明

本项目涉及的能力都比较敏感，这里把边界讲清楚：

- **不做**任何数据上传。程序没有网络客户端，除防火墙规则外不发起任何连接。
- **不修改**系统文件、不写驱动、不装服务、不常驻开机自启（除非用户在设置里显式开启）。
- 跨进程注入**默认关闭**，开启时需要二次确认，且只对用户显式选中的**单个**窗口生效，
  不做「全局模式」批量注入。
- 注入的载荷 DLL 会**自我卸载**（`FreeLibraryAndExitThread`），宿主进程不留残留；
  并有 10 分钟生命周期上限，即使注入器异常退出也不会永久驻留。
- 退出程序时会**恢复所有被挂起的进程**，不会让用户留下一个卡死的应用。
- 配置和日志都在 `%LOCALAPPDATA%\SeewoAssistant`，卸载脚本不会自动删除，由用户自行决定。

**关于杀软误报：** 跨进程注入是杀软主动防御（HIPS）的重点监控行为，这是技术本身的固有属性。
实测可能被 Windows Defender、360、火绒等拦截。程序在被拦截时会如实报告，不会静默重试。

---

## 项目结构

```
Seewo-Settings/
├─ .github/workflows/build.yml      CI：构建原生组件、跑测试、打包产物
├─ SeewoAssistant.sln
├─ src/
│  ├─ SeewoAssistant/               WinUI 3 主程序
│  │  ├─ Pages/                     六个功能页面
│  │  ├─ Services/                  托盘图标、横幅、通知、日志、服务容器
│  │  ├─ Styles/Theme.xaml          卡片、标题等共用样式
│  │  └─ Assets/app.ico
│  ├─ SeewoAssistant.Core/          全部业务逻辑与 P/Invoke 封装（可单测）
│  │  ├─ Abstractions/              日志、通知接口
│  │  ├─ Configuration/             设置模型与原子化持久化
│  │  ├─ Interop/NativeMethods.cs   集中式 P/Invoke 声明
│  │  ├─ Models/                    动作、规则、窗口、隐私事件
│  │  └─ Services/
│  │     ├─ VirtualCamera/          帧缓冲、缩放、WIC 解码、共享内存、帧泵
│  │     ├─ Privacy/                同意存储监控、注册表通知、句柄扫描
│  │     ├─ CaptureGuard/           窗口枚举、亲和性、PE 解析、注入器
│  │     ├─ Seewo/                  进程控制、防火墙、启动项
│  │     ├─ Power/                  关机、重启、注销、锁定
│  │     └─ Scheduling/             cron 解析、动作执行、调度器
│  └─ SeewoAssistant.Tests/         xunit 测试
├─ native/
│  ├─ SeewoCommon/                  共享 IPC 契约（SeewoIpc.h/.cpp）
│  ├─ SeewoVirtualCamera/           MF 自定义媒体源 COM 组件
│  ├─ SeewoVirtualCamera.Setup/     注册与摄像头实例管理 CLI
│  ├─ SeewoVirtualCamera.DShow/     DirectShow 源滤镜（Win10 回退）
│  └─ SeewoCaptureGuard.Payload/    跨进程防截屏注入载荷
├─ scripts/Install-Native.ps1       安装 / 卸载 / 状态查询
└─ docs/
```

---

## 已知限制

这些限制是技术性的，不是实现疏漏，在此如实列出：

1. **Windows 10 上虚拟摄像头不出现在系统设置里。** 这是 DirectShow 方案的固有限制，
   只有 Windows 11 的官方 API 才能做到。
2. **`WDA_EXCLUDEFROMCAPTURE` 需要内部版本 19041。** 更低版本会降级为黑块遮蔽。
3. **`SetWindowDisplayAffinity` 不是安全特性。** 微软明确说明它不保证严格保护窗口内容
   （比如有人用手机拍屏幕）。它防的是操作系统公开的截图/录屏 API，不是物理拍摄。
4. **跨进程防截屏可能被安全软件拦截。** 见上文。
5. **定时任务只在程序运行时触发。** 目前未实现写入 Windows 任务计划程序让任务在程序关闭时也能执行。
6. **不支持定时开机。** 见上文说明。
7. **注入仅支持同位数进程。** 64 位程序无法向 32 位进程注入 DLL，反之亦然；
   程序会提前检测并提示使用对应位数的版本。
8. **摄像头句柄归因需要管理员权限。** 未提权时监控仍会报告应用路径，只是没有 PID。

---

## 常见问题

**Q：虚拟摄像头装好了，但 Zoom 里看不到？**
先确认用的是哪个后端。Windows 10 的 DirectShow 方案**不会**出现在 Windows「设置」的摄像头列表里，
但应该出现在 Zoom 的摄像头下拉框中（Zoom 同时支持 Media Foundation 和 DirectShow）。
用 `ffmpeg -list_devices true -f dshow -i dummy` 可以确认 DirectShow 是否识别到它。
Windows 11 上如果看不到，运行 `SeewoVirtualCamera.Setup.exe list` 检查设备是否已创建，
并确认「设置 → 隐私和安全性 → 相机」中的相机访问没有被关闭。

**Q：摄像头有画面但一直是彩条，不是我要推的图片？**
说明媒体源没有读到共享内存中的帧，正在使用内置测试画面。检查：图片是否成功推送
（页面会显示已发送帧数）、分辨率是否超过 1920×1080 上限、以及 UI 进程是否还在运行。
推送失败时页面会给出具体原因。

**Q：隐私监控显示「尚未找到系统记录」？**
这是正常的。注册表键 `ConsentStore\webcam` 只在**某个程序第一次使用摄像头之后**才会被系统创建。
先用任意程序开一次摄像头，或点页面上的「测试提醒」按钮验证提醒链路。

**Q：跨进程防截屏提示被杀软拦截？**
这是该技术的固有代价。可以尝试：把 `SeewoCaptureGuard.Payload.dll` 加入杀软白名单、
以管理员身份运行、或只使用本进程窗口保护（那条路径永远可用且零风险）。

**Q：挂起希沃软件后它还能联网？**
挂起是冻结进程的所有线程，网络连接会保持但不再有数据收发。若要彻底阻断，
请同时使用「禁止联网」功能创建防火墙规则。

**Q：定时任务为什么没有在关机状态下执行？**
任务由程序内的调度器触发，程序必须正在运行。另外错过的触发不会补跑，这是有意设计的
（见[模块五](#模块五定时任务)）。

---

## 许可证

[MIT](LICENSE)
