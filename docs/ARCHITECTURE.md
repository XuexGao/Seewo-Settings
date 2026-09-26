# 架构设计

本文记录各模块的技术选型依据与关键设计决策。README 面向使用者，本文面向维护者——
重点说明**为什么这样做**，以及哪些看起来可行的方案被排除了、原因是什么。

---

## 目录

- [总体结构](#总体结构)
- [模块一：虚拟摄像头](#模块一虚拟摄像头)
- [模块二：摄像头与麦克风调用提醒](#模块二摄像头与麦克风调用提醒)
- [模块三：防止屏幕画面被捕获](#模块三防止屏幕画面被捕获)
- [模块四：希沃软件管控](#模块四希沃软件管控)
- [模块五：定时任务](#模块五定时任务)
- [跨模块关注点](#跨模块关注点)
- [构建与发布](#构建与发布)

---

## 总体结构

```
┌──────────────────────────────────────────────────────────────┐
│ SeewoAssistant.exe  (WinUI 3, 非打包, 自包含单文件)            │
│                                                              │
│  Pages/          六个功能页面 + ModulePageBase                 │
│  Services/       托盘图标、横幅、通知、日志、AppServices（组合根）│
└───────────────────────────┬──────────────────────────────────┘
                            │ 项目引用
┌───────────────────────────▼──────────────────────────────────┐
│ SeewoAssistant.Core  (net8.0-windows, 无 UI 依赖)             │
│                                                              │
│  Abstractions/   日志与通知接口（供 UI 实现，Core 不反向依赖）   │
│  Configuration/  设置模型 + 原子化持久化                        │
│  Interop/        集中式 P/Invoke 声明                          │
│  Models/         动作、规则、窗口、隐私事件                     │
│  Services/       五个模块的业务逻辑                            │
└───────────────────────────┬──────────────────────────────────┘
                            │ 命名共享内存 + 命名事件
┌───────────────────────────▼──────────────────────────────────┐
│ native/  (C++17, 仅 Windows SDK)                              │
│                                                              │
│  SeewoCommon/              共享 IPC 契约（唯一真源）            │
│  SeewoVirtualCamera/       MF 自定义媒体源（Win11）            │
│  SeewoVirtualCamera.DShow/ DirectShow 源滤镜（Win10 回退）     │
│  SeewoVirtualCamera.Setup/ 注册与摄像头实例管理 CLI            │
│  SeewoCaptureGuard.Payload/跨进程防截屏注入载荷                │
└──────────────────────────────────────────────────────────────┘
```

**分层的三条理由：**

1. **Core 不依赖 UI。** 所有业务逻辑都在 `SeewoAssistant.Core`，它是一个
   `net8.0-windows` 类库，不引用 Windows App SDK。这样纯逻辑（cron 解析、帧缓冲缩放、
   颜色解析、路径处理）可以在没有 WinUI 运行时的环境下直接单测。
2. **组合根是显式的一个类，不是 DI 容器。** 服务图很小、固定、有序，而且**关闭顺序有意义**
   （必须先恢复被挂起的进程、卸载注入载荷，再停帧泵、最后存设置）。`AppServices` 用普通构造
   把这层顺序写死，比容器配置更容易读懂和验证。
3. **原生代码与托管代码通过明确定义的 IPC 契约通信**，契约集中在
   `native/SeewoCommon/SeewoIpc.h` 一处，C# 侧镜像在
   `VirtualCameraFrameChannel.cs` 与 `GuardChannel.cs`。契约变更必须同时改两边并提升版本号。

---

## 模块一：虚拟摄像头

### 核心约束（决定了整个设计）

`MFCreateVirtualCamera` / `IMFVirtualCamera` 是 **Windows 11 内部版本 22000** 引入的。
Windows 10 上 `mfsensorgroup.dll` 根本不存在。微软官方在 Windows 10 上**没有**用户态虚拟
摄像头方案，唯一替代是 AVStream 内核驱动，而那需要 EV 代码签名——已被明确排除。

因此采用**双后端 + 运行时探测**：

| 系统 | 后端 | 可见范围 |
| --- | --- | --- |
| Win11 22000+ | `MFCreateVirtualCamera` + 自研 MF 自定义媒体源 | 设备管理器、Windows 设置、所有应用（含 UWP） |
| Win10 19041+ | DirectShow 源滤镜 | DirectShow 应用；不出现在 Windows 设置，UWP 不可见 |
| 更低 | 无 | UI 明确显示「不支持」并说明原因 |

探测方式不是比版本号，而是 `LoadLibrary("mfsensorgroup.dll")` + `GetProcAddress
("MFCreateVirtualCamera")`。版本号可能被兼容性垫片欺骗，导出表不会。

### 帧传输：为什么用共享内存而不是 RPC

媒体源运行在 **FrameServer 服务进程**里（可能是会话 0），UI 在交互会话。官方示例用 RPC，
但共享内存更适合这里的负载：

- 帧是**定长、高频、单向**的数据流（30fps × 8MB）。RPC 的封送开销在这个量级上不可忽略。
- 布局固定，双方可以**各自映射同一节**，生产者写完发一个事件即可，无需往返。
- 消费者（媒体源）可以完全**非阻塞**读取：读不到就用内置画面。

节名 `Global\SeewoAssistant.VCam.Frame.v1`，回退 `...v1.local`。选择 `Global\` 是为了跨会话，
但创建 `Global\` 对象需要 `SeCreateGlobalPrivilege`（服务有，普通交互用户没有），
所以两边都实现「先试 Global，失败回退 Local」。版本后缀 `v1` 保证旧生产者写的数据不会被新
消费者误读。

### 媒体验证与降级

媒体源对每一帧做完整校验：`magic` / `version` / `sourceState == Live` / 尺寸上限 /
`payloadBytes` 一致性，并拒绝 `timestampMs` 与 `GetTickCount64()` 相差超过 2 秒的帧。
通过后还要**复读 `frameIndex` 检测撕裂**（生产者可能在拷贝途中覆盖），必要时重试一次。

任何一项不满足都**不是错误**，而是回退到内置动态测试画面（彩条 + 灰阶 + 移动扫描条）。
这条降级路径保证了：UI 未启动、UI 崩溃、推送停下、图片读取失败——**摄像头永远有画面，不会黑屏**。
这一点是刻意的：虚拟摄像头一旦黑屏，使用它的会议软件会显示异常，用户会以为是软件坏了。

### 不引入 WIL / C++/WinRT

官方示例基于 `Microsoft.Windows.CppWinRT` 和 `Microsoft.Windows.ImplementationLibrary`
两个 NuGet 包。这里改用 Windows SDK 自带的 `Microsoft::WRL`，理由：

- 减少 CI 失败面。两个 NuGet 包意味着额外的还原步骤和版本兼容性风险。
- 纯 SDK 依赖让 `msbuild xxx.vcxproj` 直接可用，不需要先 `nuget restore`。

### 不依赖 WDK 头文件

`PINNAME_VIDEO_CAPTURE` 原本来自 `ksmedia.h`，但 `ks.h` / `ksmedia.h` 是 **WDK** 头文件，
不是 Windows SDK 头文件，只有装了 WDK 的机器才有。而且它们会**主动破坏构建**：
`ks.h` 会带入 `cguid.h`，后者用 `__uuidof` 声明 `DEFINE_GUID`，与 Media Foundation 自身的
`__uuidof` 用法冲突，产生 `error C2059: syntax error: '__uuidof'`。

只用到这一个 GUID，所以直接在 `SeewoKsGuid.h` 里定义（`DECLSPEC_SELECTANY`，避免
需要 `INITGUID` 和单独的定义文件）。DLL 现在只依赖纯 Windows SDK。

### 注册与实例创建是独立进程

`SeewoVirtualCamera.Setup.exe` 负责 COM 注册和摄像头实例管理，而不是主程序直接做：

1. `MFCreateVirtualCamera` **不能在 UI 线程调用**——它会做 Capability Access Manager 的
   授权检查，而该检查本身需要 UI 线程，从 UI 线程调用会死锁。控制台工具没有 UI 线程。
2. 该 API 只存在于 Win11。把它隔离在独立进程里，主程序在 Win10 上能正常启动。
3. 注册写 `HKLM`，需要提权。把提权需求限制在这个小工具里，主程序保持标准用户运行。

实例创建用 `MFVirtualCameraAccess_CurrentUser`，**不需要管理员**；只有注册需要。

---

## 模块二：摄像头与麦克风调用提醒

### 技术选型（这是本模块最关键的部分）

| 方案 | 结论 |
| --- | --- |
| 全局 API Hook（`SetWindowsHookEx` / Detours） | ❌ **排除。** 摄像头/麦克风访问**不在被观测进程的用户态 API 里完成**，而是由 Media Foundation 转交给 FrameServer 服务。在观察者进程里 Hook 看不到别的进程的设备访问 |
| ETW 事件追踪 | ⚠️ **不作为主方案。** 能承载这类信息的 Provider 不是稳定的公开契约；替代方案（内核文件 Provider）产生海量无关事件，与「资源占用最小」的要求冲突 |
| **Capability Access Manager 注册表监控** | ✅ **采用。** 有文档的位置，`RegNotifyChangeKeyValue` 事件驱动，**空闲 CPU 占用为 0**，Win10 2004+ 与 Win11 都支持，**不需要管理员**，而且这正是系统自己读取的同一份数据 |
| 内核句柄扫描 | ✅ **作为可选增强。** 见下 |

### 主方案：同意存储

监控
`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{webcam,microphone}`。
每个应用一个子键，`LastUsedTimeStart` / `LastUsedTimeStop` 记录最近一次使用区间，
`LastUsedTimeStop == 0` 表示**当前正在使用**。

桌面应用的键名是路径把反斜杠换成 `#` 后的结果（`C:#Program Files#Zoom#bin#Zoom.exe`），
需要还原显示。打包应用（Store 应用）直接以包族名为键名。

**用快照差分而不是逐键订阅。** 系统在一次状态转换里会同时重写两个时间戳值，逐键订阅会产生
重复事件。所以每次收到「这个键树变了」的通知后重读整个存储并与上一次快照对比，只报告真正的
状态跳变。

**重新武装。** 同意存储的键在某个能力**从未被使用过**的机器上根本不存在，而且切换隐私设置时会被
重建。所以有一个 30 秒的慢速重试定时器负责重新武装，直到成功。UI 上明确说明这一点，避免用户
把「未找到系统记录」误解为故障。

### 可选增强：句柄扫描

注册表只告诉你是哪个应用、什么时候，**不告诉你 PID**。需要精确归因时（比如想显示图标或对进程
直接操作），用 `NtQuerySystemInformation(SystemExtendedHandleInformation)` 枚举句柄，
复制到本进程后 `NtQueryObject` 取对象名，筛出名字包含摄像头设备接口符号链接的 PID。

代价：需要 `SeDebugPrivilege`（即管理员）。所以它是**可选**的，而且**只在注册表变化时触发**，
不轮询——每分钟最多几次，开销可以忽略。未开启时监控仍然报告应用路径，只是没有 PID。

### 提醒：双通道

Toast 可能被「专注助手」/ 免打扰静默吞掉，而摄像头/麦克风提醒恰恰是最不能被静默的场景。
所以提供两个通道：

1. Windows Toast（Windows App SDK `AppNotificationManager`）。选它而不是 WinRT 的
   `ToastNotificationManager`，因为后者要求通过开始菜单快捷方式注册 AppUserModelID，
   而非打包应用没有快捷方式。
2. 可选的**置顶醒目横幅**：原生 Win32 分层窗口，`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW |
   WS_EX_TOPMOST`。用原生窗口而不是第二个 XAML 窗口，因为它必须能从任意线程创建和关闭、
   绝不抢焦点、且不出现在任务栏或 Alt+Tab。

---

## 模块三：防止屏幕画面被捕获

### 核心限制

`SetWindowDisplayAffinity` **只能作用于调用进程自己拥有的窗口**。内核在 `win32kfull.sys`
里校验 `HWND` 是否属于调用方进程，跨进程调用直接返回 `ERROR_ACCESS_DENIED`。
这是设计使然，没有用户态的绕过办法。

所以有两个作用域：

| 作用域 | 实现 | 风险 |
| --- | --- | --- |
| **本进程窗口**（默认） | 直接调用 API | 无。永远可用，不会被拦截 |
| **跨进程**（默认关闭，需显式开启） | 注入载荷 DLL 代为调用 | 可能被杀软主动防御拦截 |

### 为什么用 DLL 注入而不是 shellcode

参考项目 [NoMoreCapture](https://github.com/lilith-is-all-you-need/NoMoreCapture) 用远程
shellcode 加手工 PE 导出表解析来定位 API 地址。紧凑，但：

- **不可调试。** 崩溃时没有堆栈，没有符号。
- **杀软看到的正是它被训练要拦截的模式。** 远程 shellcode 是恶意软件最典型的特征之一。

本项目改为注入一个**真实、可检查、可版本管理、自我卸载**的 DLL。能力相同，但崩溃有堆栈、
可以附加调试器、可以用 `dumpbin` 检查。代价（可能触发主动防御）是无法回避的——这是技术本身的
固有属性，所以该功能默认关闭、开启需二次确认、只对单个窗口生效。

### 载荷设计

- **`DllMain` 几乎为空**，只调 `DisableThreadLibraryCalls`。任何加载器锁内的实际工作都可能
  死锁宿主进程。
- **真正的入口是导出函数 `SeewoCaptureGuardEntry`**，由注入器用 `CreateRemoteThread`
  启动，运行在 loader lock 之外。
- **静态 CRT（`/MT`）**，可以注入到没装 VC++ 运行时的进程。
- **自我卸载**（`FreeLibraryAndExitThread`），宿主进程不留残留。
- **10 分钟生命周期上限**，即使注入器异常退出也不会永久驻留。
- **SEH 包裹**，任何意外异常转成退出码，绝不让宿主崩溃。（注意 MSVC 的 C2712：带析构函数的
  对象和 `__try` 不能在同一函数里，所以实际逻辑在独立的 `RunPayloadLoop()` 中。）
- **无文件/注册表/网络访问**。这个 DLL 刻意「无聊」。

### 跨位数注入

32 位目标进程需要 32 位载荷和 32 位的 `LoadLibraryW` 地址。目标进程里 `LoadLibraryW` 的
地址是这样算出来的：读取目标进程实际的 `kernel32.dll` 基址，加上**匹配位数的磁盘二进制**里
该导出的 RVA。这是注入器与目标位数不同时唯一正确的做法——直接用自己的地址会失败。
`PeExportReader` 是一个自包含的 PE 解析器，只为这个用途存在。

位数探测优先用 `IsWow64Process2`（Win10 1709+），它直接报告目标的机器类型，在 ARM64 主机上
也没有歧义。

### 安全护栏

硬编码拒绝注入 `dwm.exe`、`explorer.exe`、`csrss.exe`、`winlogon.exe`、`lsass.exe`、
`services.exe`、`svchost.exe` 等。注入桌面合成器或外壳可能导致黑屏，注入关键系统进程会导致
蓝屏。`Progman` / `WorkerW` 桌面窗口同样排除。

### 注入地址解析的一个坑

`GetExitCodeThread` 返回 **32 位 `DWORD`**，装不下 64 位 `HMODULE`。不能拿远程线程的退出码
当模块句柄——在 64 位下会被截断。正确做法是在目标进程里枚举模块找到刚加载的 DLL 基址
（`EnumProcessModulesEx` + `GetModuleBaseNameW`）。

---

## 模块四：希沃软件管控

### 挂起 / 恢复

用 `ntdll` 的 `NtSuspendProcess` / `NtResumeProcess`。它们是**用户态冻结进程的唯一途径**，
自 XP 起稳定，且完全可逆。

注意：挂起会把进程的所有线程停在任意指令处，所以对正在写用户关心文件的进程需要谨慎。
退出程序时**自动恢复所有本会话挂起的进程**（`ResumeAllSuspended`），不会让用户留下一个
卡死的应用。

### 发现而不是猜测

**不硬编码希沃进程名清单。** 希沃产品线在版本之间会改名，猜错的结果是功能静默失效——用户以为
已经管控了，实际上什么都没发生。

改为**扫描本机** `Program Files`、`Program Files (x86)`、`ProgramData`、`%LOCALAPPDATA%`，
按 `seewo` / `easinote` / `希沃` / `swproxy` / `swupdate` 等关键字匹配**目录名、文件名和文件
描述**，只把**实际存在**的可执行文件列为规则，并区分「已识别」和「可能是」两类置信度。

为避免全盘扫描，只下钻名字已经匹配关键字的子目录。

### 禁止联网

用 Windows 防火墙 COM API（`INetFwPolicy2`）而不是 `netsh advfirewall`：前者有结构化的错误
报告，不需要解析本地化的控制台输出（非英文系统上会失效）。

**创建入站和出站两条规则。** 只阻断出站的话程序仍然可以被连接，不符合「禁止联网」的通常含义。
覆盖域/专用/公用三种网络配置文件。

所有规则名带 `SeewoAssistant Block ` 前缀，程序**只操作自己创建的规则**，绝不触碰用户或其他
软件创建的规则。

### 禁止开机自启

覆盖全部四种真实机制，因为只做一种会漏：

| 机制 | 实现 |
| --- | --- |
| 计划任务 | `schtasks.exe /Change /TN ... /DISABLE`（希沃主要靠这个） |
| 注册表 Run 键 | 改名保留（见下） |
| 启动文件夹 | 重命名文件 |
| 自动启动服务 | `sc.exe config <name> start= disabled` |

**禁用注册表项时是改名而不是删除**：加 `.seewoassistant-disabled` 后缀，原始命令完整保留，
可精确还原。这也是 Windows 任务管理器自己的做法。

选 `schtasks.exe` 而不是 Task Scheduler COM API，是因为后者要改任务状态需要导出并回写整个
任务 XML，命令行的 `/Change` 是更稳定、更有文档保证的接口。

---

## 模块五：定时任务

### cron 解析器

自研 5 字段解析器（`分 时 日 月 周`），纯 C# 无依赖，覆盖 `*`、`5`、`1,3,5`、`1-5`、
`*/15`、`0-30/10` 和 `@daily` 等宏。

**两个容易搞错的地方，都专门处理了：**

1. **`0` 和 `7` 都表示周日**，而且这条规则要作用于范围内部：`5-7` 是周五到周日，
   不能因为 `7` 折成 `0` 之后看起来「起止颠倒」就报错。
2. **日/周语义遵循 Vixie cron**：当「日」和「周」**都**被限定时，**任一**匹配即可。
   `0 9 1 * 1` 表示「每月 1 日**或**每周一」，而不是「既是 1 日又是周一」。
   只限定其中一个时，就只由那一个决定。搞错这一点，表达式含义会完全变样。

### 调度行为

- **每 30 秒 tick 一次。** 用轮询而不是「精确武装到下一次触发时刻的定时器」，是因为后者在系统
  休眠唤醒、夏令时切换、时钟调整后都需要重新武装。30 秒轮询的开销不可测量，但对这些情况全部
  免疫——这个取舍是刻意的。
- **错过的触发不补跑。** 机器在 08:00 睡着、09:00 唤醒，08:00 那次不会延迟执行。
  这是有意设计（补跑会让一堆积压的动作在唤醒瞬间同时爆发），UI 上明确说明。
- **不并发。** 上一个实例还在运行时，下一次触发被跳过并记录，不会并发执行同一个任务。

`GetNextOccurrence` 用整月/整日/整时跳跃而不是逐分钟推进，所以即使搜索四年也不会慢。
四年上限覆盖所有闰年组合；`0 0 30 2 *` 这类不可能日期返回 null 而不是死循环。

### 电源操作

关机/重启用 `InitiateSystemShutdownExW` 而不是 `ExitWindowsEx`，因为它支持**倒计时和屏上
提示**，用户有机会取消一个定时的关机。注销用 `ExitWindowsEx`，锁定用 `LockWorkStation`。

需要 `SeShutdownPrivilege`，标准用户**持有该特权但默认禁用**，所以要先通过
`AdjustTokenPrivileges` 启用。注意 `AdjustTokenPrivileges` 即使什么都没启用也返回成功，
所以要检查 `GetLastError()` 才是真实结果。

### 为什么没有「定时开机」

**因为它无法可靠实现。** 处于完全关机（S5）状态的电脑上没有操作系统在运行，软件无从唤醒它。
唯二可靠的途径是固件的 RTC 定时唤醒和 Wake-on-LAN，两者都在 Windows 之外配置。

与其提供一个不可靠的按钮，UI 上如实说明了这一点并给出具体的 BIOS/网卡配置指引。

---

## 跨模块关注点

### IPC 契约是唯一真源

`native/SeewoCommon/SeewoIpc.h` 定义所有共享内存布局和名称，C# 侧在
`VirtualCameraFrameChannel.cs` 和 `GuardChannel.cs` 里镜像。**两边必须同步修改**，
且改动时提升版本号（节名带 `v1` 后缀）——这样旧生产者写的数据不会被新消费者误读。

`static_assert` 在编译期校验结构体能放进节里。

### 权限策略

程序以 `asInvoker` 运行，**不主动请求提权**。对常驻托盘程序来说，开机就弹 UAC 是更差的取舍。
需要管理员的操作会给出明确提示，标题栏还有「未以管理员身份运行」徽章。

| 功能 | 需要管理员 |
| --- | --- |
| 隐私监控、本进程窗口保护、创建虚拟摄像头实例 | 否 |
| 注册虚拟摄像头 COM 组件 | 是（写 HKLM） |
| 跨进程防截屏 | 通常需要 |
| 防火墙规则、修改计划任务/服务、定时关机 | 是 |
| 挂起/结束其他用户的进程 | 是 |

### 退出时的清理顺序

`AppServices.DisposeAsync` 的顺序是有意义的，不能随意调换：

1. 停调度器（不再触发新动作）
2. 停隐私监控（不再产生事件）
3. **恢复所有被挂起的进程**（否则用户留下一个冻结的应用）
4. **请求注入载荷卸载**（否则 DLL 残留在别人进程里）
5. 停虚拟摄像头帧泵
6. 保存设置
7. 关闭日志

另外 `SeewoControlService` 只恢复**本会话自己挂起的**进程，不会去动用户手动挂起的进程。

### 日志

`FileLogger` 把写入排队，由单个后台线程落盘，所以从注册表监视线程或调度器 tick 里记日志不会
因为磁盘 I/O 阻塞。文件在 2MB 处滚动，保留 5 个，上限 10MB。内存里保留最近 1000 行供诊断页
显示。日志队列是**有界**的：宁可丢日志行，也不阻塞调用方或无限制增长。

### 设置持久化

写临时文件再 `File.Move` 覆盖目标，所以中断的保存不会留下截断的配置文件。损坏的文件会被
**改名保留**而不是删除，用户可以自行恢复。

---

## 构建与发布

### CI 为什么这样组织

单一 `windows-latest` job，但每个组件**独立构建并独立报告结果**，最后有一个统一的 gate 步骤
决定成败。原因：最初把构建串在一起时，MF 项目的失败会掩盖后面所有组件的诊断信息，一次 CI
只能发现一层问题。改成每个步骤 `continue-on-error` + 末尾 gate 之后，一次运行能看到全部错误。

gate 不只是检查退出码，还**断言每个预期二进制文件确实存在**。这一步抓到了两个真实问题：
MSBuild 路径漂移（所有产物落到 `native/<Project>/artifacts`）和 `Get-ChildItem -Include`
静默不复制任何文件。

### 几个踩过的坑（都已修复，记录以免重犯）

| 问题 | 原因 | 处理 |
| --- | --- | --- |
| 所有原生产物落到错误目录 | MSBuild 直接构建 `.vcxproj` 时把 `SolutionDir` 设为**项目目录**，不是留空 | 从 `MSBuildProjectDirectory` 推导仓库根，两种调用方式都正确 |
| `error MSB4025: 项目文件无法加载` | 脚本化编辑把 `\a` 写成了真正的 BEL 字节（0x07） | 修复文件，并加了一个控制字符 + XML 校验步骤，把不透明的失败变精确 |
| `error C2059: '__uuidof'` | WDK 头文件 `ks.h` 带入 `cguid.h`，与 MF 的 `__uuidof` 冲突 | 不依赖 WDK，只定义需要的那一个 GUID |
| `LNK2001: CLSID_SeewoVirtualCamera` | 头文件在全局作用域声明，`.cpp` 在 `namespace seewo` 内定义 | 定义移到全局作用域 |
| `PublishSingleFile requires EnableMsixTooling` | 非打包应用也必须开启它，用于生成嵌入的 `resources.pri` | 开启，并保留一个自包含文件夹发布作为回退 |

### 产物

`SeewoAssistant-win-x64.zip`，自包含单文件，解压即用，无需安装 .NET 运行时或 Windows App SDK
运行时：

```
SeewoAssistant.exe                 主程序
SeewoVirtualCamera.Setup.exe       虚拟摄像头管理工具
native/x64/                        x64 原生组件
native/x86/                        32 位 DirectShow 滤镜与注入载荷
scripts/Install-Native.ps1         安装/卸载/状态查询
README.md  LICENSE
```

只打包 `.dll` 和 `.exe`。链接器同时产生的 `.pdb` / `.exp` / `.lib` 是构建中间产物，
打进去会让下载体积增加约两倍，对用户没有任何用处。

推送 `v*` 标签时会额外创建 GitHub Release 并附上压缩包。

### 已修复的坑（按发现顺序记录）

这些都是实际发生并已修复的，记录下来以免重犯：

| 现象 | 根因 | 处理 |
| --- | --- | --- |
| 所有原生产物落到错误目录 | MSBuild 直接构建 `.vcxproj` 时把 `SolutionDir` 设为**项目目录**，不是留空 | 从 `MSBuildProjectDirectory` 推导仓库根 |
| `MSB4025: 项目文件无法加载` | 脚本化编辑把 `\a` 写成了真正的 BEL 字节（0x07） | 加了一个控制字符 + XML 校验步骤，把不透明的失败变精确 |
| `C2059: '__uuidof'` | WDK 头文件 `ks.h` 带入 `cguid.h`，与 MF 的 `__uuidof` 冲突 | 不依赖 WDK，只定义需要的那一个 GUID |
| `LNK2001: CLSID_SeewoVirtualCamera` | 头文件在全局作用域声明，`.cpp` 在 `namespace seewo` 内定义 | 定义移到全局作用域 |
| `LNK4070: /OUT 与输出文件名不符` | `.def` 里多余的 `LIBRARY` 语句把 `/OUT:` 写进了 `.exp` | 删掉 `LIBRARY`，`EXPORTS` 才是关键 |
| `PublishSingleFile requires EnableMsixTooling` | 非打包应用也必须开启它，用于生成嵌入的 `resources.pri` | 开启，并保留自包含文件夹发布作为回退 |
| 页脚一直显示「隐私监控：已停止」 | `UpdateStatusBar()` 只在构造函数里调了一次，而服务是在之后才启动的 | 2 秒定时刷新，且只在值真的变化时更新 |
| 窗口在 1024×768 上超出屏幕底部 | 固定 `Resize(1180, 820)`，没有参考工作区 | 按工作区收缩并留边距 |
| 隐私页声称「已找到摄像头和麦克风的授权记录」 | `IsStoreReady` 在**任一**设备武装时即为真 | 服务报告实际已武装的设备，页面据此命名 |
| 11 个按钮对辅助技术没有名称 | 内容是 `StackPanel` 的 `Button` 无法自动推导自动化名称 | 显式加 `AutomationProperties.Name`（同时也是无障碍修复） |
| `install` 创建的摄像头立刻消失 | 用了 Session 生命周期，进程一退出就销毁 | 改为 System 生命周期 |
| 安装工具输出乱码 | 源文件是无 BOM 的 UTF-8，MSVC 按系统 ANSI 代码页读取，中文字面量被读成 CP1252 | 所有原生项目加 `/utf-8` |
| 重定向输出出现 NUL 字节 | 工具强制 `stdout` 进入 `_O_U16TEXT` | 检测句柄：控制台用 `WriteConsoleW`，重定向用 UTF-8 |
| 应用里显示安装工具输出为乱码 | 读取子进程输出时未指定编码 | 指定 UTF-8 |
| `schtasks`/`sc` 输出乱码 | 它们写的是 OEM 代码页，不是 UTF-8 | 按 OEM 代码页解码 |
| CI 检查误报乱码 | PowerShell 解码原生命令输出的方式不受 `[Console]::OutputEncoding` 控制 | 改为把 stdout 重定向到文件，再用严格 UTF-8 解码器读字节 |

### 已验证与未验证

**CI 已验证（每次 push 自动运行两个 job）：**

1. 全部 6 个原生项目（x64 与 Win32）编译通过，无警告
2. 全部托管项目编译通过
3. 43 个单元测试全部通过
4. 自包含单文件发布成功，发行包结构完整（缺文件即失败）
5. **应用真的能启动**：冒烟测试运行发布的 exe，用 UI Automation 遍历全部 7 个页面并逐一截图，
   点击「运行自检」和「刷新窗口列表」，确认进程存活且日志中没有 ERROR
6. 安装工具的输出是合法 UTF-8 且包含预期内容（防乱码回归）
7. 媒体源 COM 注册正确：CLSID 从定义它的头文件读取（避免重复字面量写错），
   校验 `InProcServer32` 指向存在的、正确的 DLL，且 `ThreadingModel` 为 `Both`
8. 虚拟摄像头确实被创建并出现在系统设备枚举中

**尚未验证：**

- **画面内容。** 取帧命令（`capture`）能打开摄像头并读帧，但 CI 虚拟机没有视频栈，
  摄像头可以枚举却无法激活（退出码 7）。命令会区分「环境限制」和「真的没有画面」
  （退出码 8/9），但真实画面仍需在**有摄像头的 Windows 机器**上运行
  `SeewoVirtualCamera.Setup.exe capture --frames 60 --out frame.png` 才能确认。
- **各种第三方应用的实际兼容性。** Zoom / Teams / 微信 / OBS 能否选中并使用这个摄像头，
  只能在真机上验证。
- **跨进程注入在真实杀软环境下的表现。** CI 无法模拟杀软主动防御。
- **真实希沃软件。** CI 机器上没有安装希沃，扫描、挂起、断网、禁自启都只走通了代码路径，
  没有在真实目标上执行过。
- **实际观感。** 截图能确认布局正确、无崩溃，但不能替代人眼对视觉细节的判断。

**结论：** CI 绿灯说明「能编译、能链接、逻辑测试通过、应用能启动、界面能渲染」。
它**不**说明运行时行为在真实设备上完全正确。上面列出的项目需要一台真实的 Windows 机器。
