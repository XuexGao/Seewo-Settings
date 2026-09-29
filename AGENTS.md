# AGENTS.md

面向在此仓库工作的 AI 代理（以及需要深入了解实现的人）。README.md 只讲「这是什么、
怎么用」；本文讲「为什么这样做、哪里有坑、怎么验证」。

**动手前请先读「陷阱清单」和「验证方法」两节。** 这个项目已经踩过一批只在真机上才会暴露的
问题，重复踩一遍的代价很高。

---

## 目录

- [仓库结构与职责](#仓库结构与职责)
- [构建与验证](#构建与验证)
- [陷阱清单](#陷阱清单)
- [跨语言契约](#跨语言契约)
- [各模块的关键实现决策](#各模块的关键实现决策)
- [修改指南](#修改指南)
- [尚未验证的部分](#尚未验证的部分)

---

## 仓库结构与职责

```
src/SeewoAssistant/          WinUI 3 主程序（UI 层）
src/SeewoAssistant.Core/     全部业务逻辑，不依赖 Windows App SDK
src/SeewoAssistant.Tests/    xunit 测试
native/SeewoCommon/          跨语言契约的唯一真源（SeewoIpc.h）
native/SeewoVirtualCamera/   MF 自定义媒体源（Win11）
native/SeewoVirtualCamera.DShow/  DirectShow 源滤镜（Win10 回退）
native/SeewoVirtualCamera.Setup/  注册 + 摄像头管理 + 取帧 CLI
native/SeewoCaptureGuard.Payload/ 跨进程防截屏注入载荷
scripts/                     用户脚本
scripts/ci/                  CI 专用脚本
.github/workflows/build.yml  编译、打包、发 Release
.github/workflows/test.yml   运行应用并验证行为
```

**分层规则：**

- `Core` 不能引用 Windows App SDK。它只依赖 `net8.0-windows` 的 BCL、Win32 P/Invoke、
  注册表和 WMI。这样纯逻辑可以在没有 WinUI 运行时的环境下单测。
- UI 依赖 Core，Core 通过接口（`IAppLogger`、`IPrivacyNotifier`）反向通知 UI。
- `AppServices` 是显式组合根，不是 DI 容器。服务图小、固定、且**关闭顺序有意义**。

---

## 构建与验证

### 本地构建

需要 Visual Studio 2022（C++ 桌面 + .NET 桌面工作负载）、.NET 8 SDK、Windows SDK 10.0.26100。

```powershell
# 原生（每个项目单独构建，便于定位失败）
msbuild native/SeewoVirtualCamera/SeewoVirtualCamera.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoVirtualCamera.DShow/SeewoVirtualCamera.DShow.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoVirtualCamera.DShow/SeewoVirtualCamera.DShow.vcxproj /p:Configuration=Release /p:Platform=Win32
msbuild native/SeewoVirtualCamera.Setup/SeewoVirtualCamera.Setup.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoCaptureGuard.Payload/SeewoCaptureGuard.Payload.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoCaptureGuard.Payload/SeewoCaptureGuard.Payload.vcxproj /p:Configuration=Release /p:Platform=Win32

# 托管
dotnet build src/SeewoAssistant.Core/SeewoAssistant.Core.csproj -c Release
dotnet test  src/SeewoAssistant.Tests/SeewoAssistant.Tests.csproj -c Release
dotnet publish src/SeewoAssistant/SeewoAssistant.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:WindowsPackageType=None `
  -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true -p:Platform=x64 -o out
```

**不要**用 `dotnet build SeewoAssistant.sln`：解决方案里含 `.vcxproj`，`dotnet build` 不能可靠
驱动它们。

### 两个 workflow 的分工

| Workflow | 回答的问题 | 失败意味着 |
| --- | --- | --- |
| `build.yml` | 能编译、能链接、单测通过、包结构完整吗 | 代码有编译错误 |
| `test.yml` | 应用能启动、能渲染、功能可用吗 | 有用户可见的真实缺陷 |

`test.yml` 通过 `workflow_run` 触发，用 `run-id` 从 Build run 下载产物——它自己**不**产生
artifact，所以下载必须指定 `run-id`，否则会拿到空目录。

**改动 UI 后必须看 `test.yml` 的截图 artifact。** 只让 `build.yml` 变绿是没有意义的。

### 冒烟测试做了什么

`scripts/ci/Smoke-Test.ps1` 用 UI Automation 启动发布的 exe，遍历 7 个页面逐一截图，
点击关键按钮，检查日志中是否有 ERROR，并在应用非正常退出时失败。

截图用 `BitBlt` 抓合成桌面，**不是** `PrintWindow`：WinUI 3 通过 DirectComposition 合成，
`PrintWindow` 对它经常返回全黑位图。

按钮用 `AutomationProperties.Name` 定位。**内容是 `StackPanel` 的 `Button` 必须显式设置
这个属性**，否则自动化名称是空的（同时屏幕阅读器也读不出来）。

### 在有真机的 Windows 上验证虚拟摄像头

CI 虚拟机没有视频栈，摄像头能注册、能枚举，但无法激活。要在真机上确认画面：

```powershell
SeewoVirtualCamera.Setup.exe install
SeewoVirtualCamera.Setup.exe capture --frames 60 --out frame.png
```

`capture` 的退出码：

| 码 | 含义 |
| --- | --- |
| 0 | 成功读到帧（输出里有帧数、实测帧率、亮度、画面是否变化） |
| 4 | COM / Media Foundation 初始化失败 |
| 7 | 没找到摄像头，或摄像头无法激活（**虚拟机、远程会话常见**） |
| 8 | 已激活但没有输出帧，或流提前结束 |
| 9 | 所有帧全黑（设备存在但无有效画面） |

7 通常是环境问题，8 和 9 是真实缺陷。

---

## 陷阱清单

这些都是实际发生过、且**不会在编译期暴露**的问题。改相关代码前请先读。

### 1. 跨语言名字必须逐字一致

`native/SeewoCommon/SeewoIpc.h` 与 C# 里的常量是一对必须匹配的字面量。不一致时**没有任何
报错**：两边都成功创建自己的对象，然后永远看不到对方。

已经因此出过一次事故：原生侧没有 `Global\` 前缀，托管侧有。没有前缀的名字会落在调用方的
会话命名空间里（等价于 `Local\`），于是虚拟摄像头永远收不到推送的画面、跨进程防截屏永远等
不到回应。

**改动时必须同时改两边，并让 `SharedChannelContractTests` 通过。** 该测试会解析原生头文件
并逐项比对，还会断言每个名字都带前缀。

### 2. 惰性枚举的异常不在调用点

```csharp
// 错误：try 只包住了赋值，异常在 foreach 里抛出
try { files = Directory.EnumerateFiles(dir, "*.exe", AllDirectories); }
catch (UnauthorizedAccessException) { continue; }
foreach (var f in files) { ... }   // <-- 这里才抛，未被保护
```

`EnumerateFiles` 是惰性的，遇到无权限子目录时在**枚举过程中**抛异常。这曾导致希沃扫描在
第一个受保护目录上整体中止，什么都扫不出来。

正确做法见 `SeewoControlService.EnumerateExecutablesSafely`：用显式栈手动驱动遍历，每一步
单独 try/catch。

### 3. 原生回调里的托管异常会直接杀进程

在 `WndProc` 这类原生回调中抛出的托管异常会穿过原生栈帧，**`App.UnhandledException`
捕获不到**，进程直接终止。`TrayIcon` 和 `BannerWindow` 的窗口过程都必须自己 try/catch。

同理，后台线程的异常不会走 `UnhandledException`，需要挂 `AppDomain.UnhandledException`
和 `TaskScheduler.UnobservedTaskException`（已挂）。

### 4. DllImport 的 CharSet 不能省

```csharp
// 错误：默认是 Ansi，会把 string 转成窄字符缓冲传给宽字符 API
[DllImport("user32.dll")] static extern int DrawTextW(nint hdc, string text, ...);

// 正确
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(...);
```

曾经因此让横幅显示黑块后闪退：被调用方按宽字符读取一个小得多的 ANSI 缓冲，越界。

**所有带 `string`/`StringBuilder` 参数的 `*W` 函数都必须写 `CharSet = CharSet.Unicode`。**

### 5. 源码字符集：原生项目必须加 `/utf-8`

MSVC 默认按**系统 ANSI 代码页**读取源文件。本仓库的源文件是无 BOM 的 UTF-8，在英文系统上
中文字面量会被读成 CP1252，输出乱码。所有 `.vcxproj` 都必须有
`<AdditionalOptions>/utf-8 %(AdditionalOptions)</AdditionalOptions>`。

### 6. 控制台输出与重定向输出的编码不同

- 控制台窗口要 UTF-16，且只能通过 `WriteConsoleW` 写。
- 重定向（管道、CI 捕获）要 UTF-8 字节；写 UTF-16 会产生交错的 NUL 字节。

`native/SeewoVirtualCamera.Setup/main.cpp` 的 `Print()` 会检测句柄类型并选择路径。
**不要**用 `_O_U16TEXT` 一刀切，那正是之前乱码的原因。

读取子进程输出时也要配对：

| 子进程 | 编码 |
| --- | --- |
| `SeewoVirtualCamera.Setup.exe` | UTF-8（它检测到重定向就写 UTF-8） |
| `schtasks.exe` / `sc.exe` | **OEM 代码页**，不是 UTF-8 |
| `powershell.exe` | 显式要求 UTF-8 再按 UTF-8 读 |

### 7. 静态 CRT

所有原生项目都用 `/MT`（`MultiThreaded`）。发行包承诺「解压即用、无需安装运行时」，
用 `/MD` 就会依赖 VC++ 可再发行组件，缺失时报错是「找不到 VCRUNTIME140.dll」。
注入载荷更必须用 `/MT`，因为它要注入到可能没装运行时的进程里。

### 8. MSBuild 的 `SolutionDir` 不可靠

直接构建 `.vcxproj` 时，MSBuild 会把 `SolutionDir` 设成**项目目录**（不是留空）。四个原生
项目的 `OutDir` 都从 `$(MSBuildProjectDirectory)\..\..\` 推导仓库根，**不要**改回
`$(SolutionDir)`。

### 9. 发行包布局与查找路径必须对应

`SeewoVirtualCamera.Setup.exe` 按自己的所在目录查找 `SeewoVirtualCamera.dll`。发行包把
exe 放在根目录、DLL 只放在 `native/x64/` 时，安装永远失败（「找不到媒体源 DLL」）。
现在 DLL 与 exe 同目录，且工具会多找几个位置。**改打包布局时必须同步检查 `build.yml` 里
`$required` 列表和工具的查找列表。**

### 10. `PublishSingleFile` 需要 `EnableMsixTooling`

即使是 `WindowsPackageType=None` 的非打包应用，单文件发布也需要它来生成嵌入的
`resources.pri`。关掉会直接编译失败。

### 11. 后端选择必须贯穿到底

「按系统版本选后端」不能只在探测函数里做。这个项目踩过两次：

- **注册**：`RegisterAsync` 一律跑 `Setup.exe install`（MF 路径），Windows 10 上
  `MFCreateVirtualCamera` 不存在，工具返回 3，安装 100% 失败。
- **按钮可用性**：同一套判断写了两遍，第二遍仍用旧规则，于是后端感知的逻辑在
  任意按钮按下后就被悄悄改回去了。

**规则：** 任何与后端相关的行为都要 `switch (DetectCapability().Backend)`；
判断只写一处，其他地方调用它。

### 12. 跨进程共享对象的命名空间必须显式协商

注入器和载荷**不能各自独立探测命名空间**。注入器通常非提权，`Global\` 建不出来只能
退到 `Local\`；载荷若先试 `Global\`，在恰好有 `SeCreateGlobalPrivilege` 的目标进程里
会**新建自己的** `Global\` 对象，于是两边各自等一个永远不会被 signal 的事件。

现象是「载荷已加载但没有响应」/ 超时，而且**只在部分目标上失败**，取决于目标权限——
最难查的一类间歇性问题。

**规则：** 注入器把自己用的命名空间通过 `CreateRemoteThread` 的参数传给载荷；
`0` 表示「没有提示」（旧版注入器），载荷才回退到自行探测。

### 13. Toast 有硬性元素上限和场景前提

`ToastGeneric` 模板**最多 3 个文本元素**，第 4 个会让 `BuildNotification()` 抛
`ArgumentException`，消息是「Maximum number of text elements added」。曾因此把标题、
正文、时间、进程 ID 加成 4 条，导致**恰恰在解析出 PID（也就是最需要提醒）时通知失败**。

`AppNotificationScenario.Urgent` 还有两个前提：**至少一个按钮**，以及**音频元素必须存在**
（静音要用 `MuteAudio()`，直接不加音频元素会被拒绝）。另外不是所有系统都支持 urgent，
要先问 `IsUrgentScenarioSupported()`（静态方法）。

**规则：** 文本元素数 ≤ 3；先加按钮再设场景；用 `MuteAudio()` 静音；
urgent 用 `IsUrgentScenarioSupported()` 把关。

### 14. 结束服务托管的进程前必须先停服务

Seewo 有些组件以 Windows 服务方式运行，进程归 SCM 所有。直接 `TerminateProcess`
会被 SCM 当成异常退出并**立刻拉起替代进程**，所以「杀不掉」；重启时还会把依赖的服务
一并带起来，就是用户看到的「把沉睡的进程喊醒了」。

**规则：** `Terminate` 先用 `Win32_Service` 按 `ProcessId` 找到对应服务，
`sc stop` 之后再结束进程。停不掉时要如实说明进程会回来，不要报一个不成立的「成功」。

### 15. 不要给原生 `.def` 文件写 `LIBRARY`

`LIBRARY` 会把 `/OUT:` 写进生成的 `.exp`，与实际输出路径不符，产生 `LNK4070`。
`EXPORTS` 才是关键。

---

## 跨语言契约

`native/SeewoCommon/SeewoIpc.h` 是唯一真源，C# 侧在
`VirtualCameraFrameChannel.cs`（`FrameChannelContract`）和 `GuardChannel.cs` 里镜像。

**两条通道：**

| 通道 | 共享节 | 事件 | 用途 |
| --- | --- | --- | --- |
| 虚拟摄像头帧 | `Global\SeewoAssistant.VCam.Frame.v1` | `Global\...DataReady.v1` | UI 写 BGRA 帧，媒体源读 |
| 防截屏请求 | `Global\SeewoAssistant.CaptureGuard.v1` | `Global\...Request.v1` | 注入器发请求，载荷回结果 |

两条都遵循同一模式：先试 `Global\`，失败回退 `Local\`（创建 `Global\` 对象需要
`SeCreateGlobalPrivilege`，服务有、普通交互用户没有）。

**改动契约时：**

1. 同时改 `SeewoIpc.h` 和对应的 C# 常量
2. 提升名字里的版本后缀（`v1` → `v2`），这样旧生产者写的数据不会被新消费者误读
3. 跑 `SharedChannelContractTests`
4. 如果布局变了，检查两边的 `static_assert` / 尺寸常量

**帧数据布局：** 64 字节头（magic、version、宽高、stride、格式、帧序号、状态、时间戳），
之后是 BGRA 像素。媒体源会校验 magic/version/尺寸/时间戳新鲜度（2 秒），并复读帧序号检测撕裂。

---

## 各模块的关键实现决策

### 模块一：虚拟摄像头

**为什么有两个后端：** `MFCreateVirtualCamera` 是 Windows 11 内部版本 22000 才有的 API，
Windows 10 上不存在。微软在 Win10 上没有用户态方案，唯一替代是 AVStream 内核驱动（需要 EV
签名，已排除）。所以 Win11 用官方 MF 方案，Win10 用 DirectShow 源滤镜。

**能力探测用导出表而不是版本号：** `LoadLibrary("mfsensorgroup.dll")` +
`GetProcAddress("MFCreateVirtualCamera")`。版本号可能被兼容性垫片欺骗。

**帧传输用共享内存而不是 RPC：** 媒体源运行在 FrameServer 服务进程（可能是会话 0），
帧是定长、高频、单向数据流。共享内存让消费者可以完全非阻塞读取，读不到就用内置画面。

**媒体源永不黑屏：** 共享节不存在、内容过期、格式非法时，回退到内置动态测试画面（彩条 +
灰阶 + 移动扫描条）。这是刻意的：虚拟摄像头黑屏会让会议软件表现异常，用户会以为软件坏了。

**注册与实例创建是独立进程：** `MFCreateVirtualCamera` 不能在 UI 线程调用（它做 Capability
Access Manager 检查，而该检查本身需要 UI 线程，会死锁）。隔离在控制台工具里，主程序在 Win10
上也能正常启动。

**媒体源不依赖 WDK 头文件：** `PINNAME_VIDEO_CAPTURE` 直接定义在 `SeewoKsGuid.h`。引入
`ks.h`/`ksmedia.h` 会带入 `cguid.h`，与 MF 的 `__uuidof` 冲突（`C2059`）。

### 模块二：隐私监控

**为什么监控同意存储而不是 Hook：** 摄像头/麦克风访问由 Media Foundation 转交给 FrameServer
服务，在被观测进程的用户态 API 里根本看不到。ETW 的 Provider 不是稳定公开契约。同意存储
（`CapabilityAccessManager\ConsentStore`）是有文档的位置，`RegNotifyChangeKeyValue` 事件驱动，
空闲零 CPU，不需要管理员权限，而且正是系统自己读的同一份数据。

**用快照差分而不是逐键订阅：** 系统在一次状态转换里同时重写两个时间戳，逐键订阅会产生重复事件。

**重新武装：** 同意存储的键在某个能力从未被使用过的机器上不存在。有 30 秒重试定时器。
UI 上必须说明这是正常的，否则用户会以为是故障。

**PID 归因是可选的：** 注册表只给应用路径，不给 PID。句柄扫描需要 `SeDebugPrivilege`，
只在注册表变化时触发。

### 模块三：防截屏

**内核限制：** `SetWindowDisplayAffinity` 只能作用于调用进程自己的窗口，跨进程调用被
`win32kfull.sys` 拒绝（`ERROR_ACCESS_DENIED`）。这是设计使然。

**为什么用 DLL 注入而不是 shellcode：** shellcode 不可调试、崩溃无堆栈，且正是杀软被训练
要拦的模式。注入一个真实、可检查、自我卸载的 DLL 能力相同但可维护。

**载荷设计约束：**

- `DllMain` 只调 `DisableThreadLibraryCalls`，不做任何实际工作（loader lock）
- 真正入口是导出函数 `SeewoCaptureGuardEntry`，由 `CreateRemoteThread` 启动
- 静态 CRT（`/MT`）
- `FreeLibraryAndExitThread` 自我卸载，10 分钟生命周期上限
- SEH 包裹（注意 `C2712`：带析构函数的对象和 `__try` 不能同函数，所以逻辑在独立函数里）

**跨位数注入：** 目标是 32 位时，`LoadLibraryW` 的地址 = 目标进程的 `kernel32.dll` 基址 +
**匹配位数的磁盘二进制**里该导出的 RVA。`PeExportReader` 就是为此存在的自包含 PE 解析器。
位数探测优先用 `IsWow64Process2`。

**`GetExitCodeThread` 只有 32 位**，装不下 64 位 `HMODULE`。不能拿远程线程退出码当模块句柄，
必须枚举目标进程模块。

**注入不能在 UI 线程：** 它最多等载荷 10 秒。放在 UI 线程会冻结窗口，用户感知为「超时」。

**安全护栏：** 硬编码拒绝 `dwm.exe`、`explorer.exe`、`csrss.exe`、`winlogon.exe`、
`lsass.exe`、`services.exe`、`svchost.exe` 等（注入桌面合成器可能黑屏，注入关键进程会蓝屏）。

### 模块四：希沃管控

**发现而不是猜测：** 不硬编码进程名（希沃版本间会改名，猜错就静默失效）。三个来源合并：
卸载注册表键（最权威，覆盖 64/32 位视图和 HKCU）、运行中的进程、限深度的目录遍历。

**挂起/恢复用 `NtSuspendProcess`/`NtResumeProcess`** —— 用户态冻结进程的唯一途径。
退出程序时自动恢复本会话挂起的进程。

**防火墙用 `INetFwPolicy2` COM 而不是 `netsh`：** 后者需要解析本地化输出。
创建**入站 + 出站**两条规则（只挡出站的话程序仍可被连接）。

**禁用注册表启动项是改名保留**（加 `.seewoassistant-disabled` 后缀），不是删除，可精确还原。
这也是任务管理器自己的做法。

### 模块五：定时任务

**cron 语义遵循 Vixie：** 「日」和「周」**都**被限定时，**任一**匹配即可。
`0 9 1 * 1` = 每月 1 日**或**每周一。周字段 `0` 和 `7` 都表示周日，且这条规则要作用于范围内部
（`5-7` 是周五到周日，不能因为 `7` 折成 `0` 看起来「起止颠倒」就报错）。

**30 秒 tick 轮询而不是精确武装定时器：** 后者在休眠唤醒、夏令时、时钟调整后都要重新武装。

**错过的触发不补跑**（有意设计，UI 上要说明）。**不并发**：上个实例还在跑就跳过并记录。

**不支持定时开机：** S5 状态下没有 OS 在运行，软件无法唤醒。只能靠 BIOS RTC 或 Wake-on-LAN。
UI 上如实说明并给出配置指引，不要提供不可靠的按钮。

**关机用 `InitiateSystemShutdownExW`**（支持倒计时和屏上提示），注销用 `ExitWindowsEx`，
锁定用 `LockWorkStation`。`SeShutdownPrivilege` 标准用户持有但默认禁用，需
`AdjustTokenPrivileges` 启用；注意它即使什么都没启用也返回成功，要检查 `GetLastError()`。

---

## 修改指南

### 加一个新动作（模块五）

1. `Models/Actions.cs` 的 `ActionKind` 加枚举值
2. `Scheduling/ActionExecutor.cs` 的 switch 加分支
3. `Pages/SchedulePage.xaml.cs` 的 `ActionCatalog` 加条目（标签 + 分组），
   如需要参数则在 `ActionCard.RebuildParameters` 加字段
4. 如果有参数，在 `ActionCard.Describe` 加说明文字

### 加一个新页面

1. 继承 `ModulePageBase`（XAML 根元素用 `<local:ModulePageBase>`）
2. 在 `MainWindow.xaml` 的 `NavigationView.MenuItems` 加项，`Tag` 唯一
3. 在 `MainWindow.OnNavigationSelectionChanged` 的 switch 加映射
4. **所有内容是 `StackPanel` 的按钮加 `AutomationProperties.Name`**，否则冒烟测试点不到
5. 事件订阅在 `OnNavigatedFrom` 里取消

### 改 IPC 契约

见[跨语言契约](#跨语言契约)。**必须**跑 `SharedChannelContractTests`。

### 改原生项目

- 保持 `/MT`、`/utf-8`、`/W4`
- 不要引入 NuGet、C++/WinRT、WIL 或 WDK 头文件
- 不要给 `.def` 写 `LIBRARY`
- `OutDir` 保持从 `$(MSBuildProjectDirectory)` 推导
- 新的中文源文件确认是 UTF-8

### 改 CI

- 新增构建步骤时，同步更新 `build.yml` 的 `$required` 断言列表
- 新增页面时，同步更新 `Smoke-Test.ps1` 的 `$pages` 列表
- 断言要检查**文件真的存在**，不要只信退出码

---

## 尚未验证的部分

**自动化测试不能替代真机验证。** 以下都只在 CI 上走通了代码路径，没有在真实设备/真实软件上
执行过：

| 项目 | 为什么无法自动验证 | 怎么验证 |
| --- | --- | --- |
| 虚拟摄像头的实际画面 | CI 虚拟机没有视频栈，摄像头能枚举但无法激活（退出码 7） | 真机跑 `SeewoVirtualCamera.Setup.exe capture --frames 60 --out frame.png` |
| Zoom/Teams/微信/OBS 兼容性 | 需要安装这些应用 | 真机逐个选用该摄像头 |
| 跨进程注入对真实杀软 | 无法模拟主动防御 | 在开着 Defender/360/火绒的机器上测试 |
| 希沃软件的扫描/挂起/断网/禁自启 | CI 上没装希沃 | 在装了希沃的教室机上测试 |
| 挂起后希沃的实际行为 | 同上 | 同上 |
| 视觉细节 | 截图能确认布局无崩溃，不能替代人眼 | 看 `test.yml` 的截图 artifact |

**CI 绿灯的含义：** 能编译、能链接、单测通过、应用能启动、7 个页面能渲染、无 ERROR 日志、
COM 注册正确、摄像头能被创建和枚举。**不**包含上表任何一项。

发现上表问题时的处理方式：先加一条能复现的自动化断言（如果可能），再修。这次的一批 bug 里，
共享通道名字不一致、安装包缺 DLL、希沃扫描中止、注入阻塞 UI 线程都是可以自动化的，
相应的断言已经加上。
