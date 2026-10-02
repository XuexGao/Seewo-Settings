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

**`capture` 只适用于 Windows 11 / Media Foundation 后端。** 它通过 `MFEnumDeviceSources`
枚举设备，而 Windows 10 的 DirectShow 滤镜不会出现在那个列表里 —— 无论安装得多成功都读不到。
在 Win10 上运行它必然返回 7，工具现在会直接说明这一点。

Windows 10 上请改用：

```powershell
ffmpeg -list_devices true -f dshow -i dummy        # 列表中应出现 SeewoAssistant Virtual Camera
ffmpeg -f dshow -i video="SeewoAssistant Virtual Camera" -frames:v 60 frame.png
```

或在 OBS / 微信 / 钉钉的摄像头下拉框里选它。

`capture` 的退出码：

| 码 | 含义 |
| --- | --- |
| 0 | 成功读到帧（输出里有帧数、实测帧率、亮度、画面是否变化） |
| 4 | COM / Media Foundation 初始化失败 |
| 7 | 没找到摄像头、无法激活（**虚拟机、远程会话常见**），或系统是 Win10 走了 DShow 后端 |
| 8 | 已激活但没有输出帧，或流提前结束 |
| 9 | 所有帧全黑（设备存在但无有效画面） |

7 通常是环境限制或后端不匹配，8 和 9 是真实缺陷。

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
现在没有常驻注入的 DLL 了，但这条仍然适用于所有原生组件。

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

### 12. Windows PowerShell 5.1 按 ANSI 代码页读取无 BOM 的 .ps1

`scripts/*.ps1` **必须带 UTF-8 BOM**。中文 Windows 10 默认的 `powershell.exe` 是 5.1，
它读取无 BOM 的 `.ps1` 时按系统 ANSI 代码页（中文系统是 GBK）解码。GBK 是双字节编码，
某个汉字的尾字节 `0x9C` 会和紧随的闭合引号 `0x27` 组成一个双字节字符，**字符串永不闭合**，
继而级联出十几条语法错误。CI 用 PowerShell 7（默认 UTF-8）所以从没发现。

同一个脚本还有第二层故障：`-not $IsWindows -and <版本判断>`。`$IsWindows` 是 PowerShell 6+
才有的自动变量，而脚本开了 `Set-StrictMode -Version Latest`，读未定义变量会抛
`VariableIsUndefined`；`-and` **先求值左操作数**，所以版本判断还没来得及保护它就先抛了。

**第三条坑：行首的 `+` 续行。** PowerShell 5.1 的解析器**不接受**括号表达式里以 `+` 开头的续行：

```powershell
# 5.1 报 "Missing closing ')' in expression."
Write-Fail ('第一段'
            + '第二段')

# 正确：数组 + -join（PowerShell 2.0 起就支持）
Write-Fail (@('第一段'; '第二段') -join '')
```

PowerShell 7 接受前一种写法，所以本地看不出来。

**规则：** 新增 `.ps1` 必须带 BOM；版本相关的短路判断要把版本判断放前面；
跨行拼接用 `@(...) -join ''`，不要把 `+` 放在行首。
`build.yml` 有一步会检查 BOM 并用 **5.1 的 parser** 解析 —— 这一步确实抓到过真实的语法错误，
不要因为 PowerShell 7 能跑就以为没问题。

### 13. 后台线程不能碰 XAML 控件

WinUI 3 强制 UI 线程亲和性，在 `Task.Run` 里读任何 XAML 控件都会抛
`RPC_E_WRONG_THREAD`。这个错误**编译期完全看不出来**，而且后果很严重：跨进程防截屏正是
因为 `Task.Run` 里读了 `SelectedMode`（该属性读 `ModeRadio.SelectedIndex`）而 100% 失败 ——
同时「保护本程序窗口」走另一条路径一直正常，所以功能看起来是好的。

**规则：** 进入 `Task.Run` 之前把需要的数据（枚举、句柄、字符串）读进局部变量，只让纯数据
跨界。`BackgroundThreadContractTests` 会扫描每个 `Task.Run` 体；它不只是找控件名，还会先
收集每个文件里**读取了控件的成员**（属性也算），因为出事的正是这种一层间接。
扫描前会剥掉注释，否则解释这个 bug 的注释本身会被误报。

### 14. 双向绑定的 ToggleSwitch 会在生成容器时触发 Toggled

`IsOn="{Binding X, Mode=TwoWay}"` + `Toggled="On...Toggled"` 这个组合有个**时序陷阱**：

WinUI 创建列表行容器是**延迟的**（下一次布局）。绑定把 `IsOn` 设成条目真实值时，
**`Toggled` 会触发** —— 用户根本没点。而在刷新集合的方法里同步地 set/clear 一个
`_suppressXToggle` 标志**不可能**覆盖到它：方法早就返回了，标志早已恢复为 false。

后果实测过：只点一次「扫描开机自启项」，被禁用的希沃自启项就被**静默重新启用**
（7 个 HKLM Run 值 + 10 余个计划任务被改写）。

**正确做法（三者缺一不可）：**

1. 视图模型**自己持有**显示值，**不要**直通到条目：`public bool Enabled { get; set; }`
   （在构造函数里从条目初始化）。直通会让「显示值」和「存储值」永远相等，比较失去意义。
2. 处理器读**控件自身的值** `toggle.IsOn`，而不是绑定属性 —— 这样不依赖绑定是否已回写。
3. 与**条目存储值**比较：一致就是绑定在追赶（忽略），不一致才是用户操作。
   存储值只在**应用成功后**才改，所以真实点击一定不一致。

`StartupToggleContractTests` 把这三条都钉住了。三个列表（希沃规则、希沃自启项、定时任务）
都用了这个模式。

**注意：** 这**不是**测试脚本的问题。脚本按容器排除 StartupList 是对的、也确实生效了
（报告里跳过 60 个控件）；写入来自应用自身的这个 bug。修脚本只会把问题藏起来，
真实用户点「扫描」照样中招。

### 15. 每个「无处可退」的操作都必须有一条不依赖失败组件的退路

「一键隐藏所有窗口」会**连本程序自己的窗口一起隐藏** —— 这是有意的，因为需求是
「隐藏除系统程序外的一切」，而本程序不是系统程序。能这么做的前提是**托盘图标还在**：
它的窗口是 `HWND_MESSAGE`（消息窗口），不会被 `EnumWindows` 枚举到，所以菜单始终可达。

这个前提**可能不成立**：`Shell_NotifyIcon` 在某些会话里会被拒绝，`TrayIcon.TryCreate()`
返回 `null`。原代码只记了一条警告就继续跑，于是：

- 「隐藏所有窗口」按钮仍然可用
- `HideAll()` 把本程序窗口也隐藏了
- **托盘图标不存在** —— 唯一的恢复入口没了
- 用户只能去任务管理器结束进程

修法：`WindowHiderService.KeepOwnWindowVisible`，托盘创建失败时置位，本程序窗口不再隐藏。

**规则：** 端到端列举一遍「这个操作失败后用户怎么退出来」，如果退路依赖另一个可能失败的
组件，就必须为那条路径单独兜底。**两处都要改**（提供能力的一方 + 真正会去设置它的一方），
只做一半等于没做 —— `WindowHiderContractTests` 两半都钉住了。

### 16. ListViewItem 没有自己的可访问名称

`ListViewItem` 不会从内容派生 automation name，所以每个列表行对辅助技术和 UI 自动化来说
都是「未命名列表项」，测试也无法按名字定位 —— 实测行只暴露类型名（`SeewoPage+ProcessItem`）。

**隐式 `Style` 不管用。** `XamlControlsResources` 自己就定义了隐式 `ListViewItem` 样式，
后合并的隐式样式在容器生成时**不会可靠地覆盖它**：第一版修复正是这么写的，测试报告确认
行仍然只有类型名。正确做法是给样式加 `x:Key`，再用每个 `ListView` 的
`ItemContainerStyle` 指定它 —— 那是直接作用于生成的容器。

**`BasedOn` 是必须的。** 只写 `AutomationProperties.Name` 和
`HorizontalContentAlignment` 的样式**没有 `Template`**，套上去之后每一行都渲染不出内容，
比原来缺名字严重得多。必须继承 `DefaultListViewItemStyle`（WinUI 自己的 generic.xaml
就是这么用的）：

```xml
<Style x:Key="AccessibleListViewItemStyle" TargetType="ListViewItem"
       BasedOn="{StaticResource DefaultListViewItemStyle}">
    <Setter Property="AutomationProperties.Name" Value="{Binding}" />
</Style>
```

条目类要重写 `ToString()` 返回用户看到的那行文字，`{Binding}` 才绑得到有意义的内容。

### 17. Toast 有硬性元素上限和场景前提

`ToastGeneric` 模板**最多 3 个文本元素**，第 4 个会让 `BuildNotification()` 抛
`ArgumentException`，消息是「Maximum number of text elements added」。曾因此把标题、
正文、时间、进程 ID 加成 4 条，导致**恰恰在解析出 PID（也就是最需要提醒）时通知失败**。

`AppNotificationScenario.Urgent` 还有两个前提：**至少一个按钮**，以及**音频元素必须存在**
（静音要用 `MuteAudio()`，直接不加音频元素会被拒绝）。另外不是所有系统都支持 urgent，
要先问 `IsUrgentScenarioSupported()`（静态方法）。

**规则：** 文本元素数 ≤ 3；先加按钮再设场景；用 `MuteAudio()` 静音；
urgent 用 `IsUrgentScenarioSupported()` 把关。

### 18. 结束服务托管的进程前必须先停服务

Seewo 有些组件以 Windows 服务方式运行，进程归 SCM 所有。直接 `TerminateProcess`
会被 SCM 当成异常退出并**立刻拉起替代进程**，所以「杀不掉」；重启时还会把依赖的服务
一并带起来，就是用户看到的「把沉睡的进程喊醒了」。

**规则：** `Terminate` 先用 `Win32_Service` 按 `ProcessId` 找到对应服务，
`sc stop` 之后再结束进程。停不掉时要如实说明进程会回来，不要报一个不成立的「成功」。

### 19. `ShowWindow` 的返回值不是「是否成功」

`ShowWindow` 返回的是**窗口之前是否可见**。恢复一个被隐藏的窗口时它返回 `FALSE`，
但这**正是成功的表现**。把它当成功标志会让计数恒为 0，界面显示「没有需要恢复的窗口」，
而桌面其实还藏着。

**规则：** `ShowWindow` 的返回值只用来判断「之前的状态」；需要知道新状态就另外查。

### 20. 不要给原生 `.def` 文件写 `LIBRARY`

`LIBRARY` 会把 `/OUT:` 写进生成的 `.exp`，与实际输出路径不符，产生 `LNK4070`。
`EXPORTS` 才是关键。

### 21. 界面能看到的状态，必须能从这个界面离开

「取消保护」曾经和「保护」用**同一个开关**把关：`blocked = 非本进程 && 未允许跨进程`。
保护确实需要它，但**取消保护也需要**——而跨进程默认是关的。于是：

- 窗口在上一轮会话里被保护了（affinity 存在窗口上，**能扛过本程序重启**）
- 重启后跨进程是关的 → 两个按钮都灰
- 界面自己那行字还在说「当前保护状态：穿透隐身」
- 用户除了重启那个被保护的程序，**没有任何出路**，界面上也没解释

**规则：** 凡是界面向用户**声明**了的状态，都要能从这个界面退出去。做可用性判断时问一句
「这个条件是'能不能做这件事'，还是'能不能做那件事'」——把两件事共用一个条件，就会出现
这种单向门。修的时候注意**两半都要做**：把按钮点亮（`isProtected`）**和**让点击能成功
（提示并当场打开开关、持久化选择）。只点亮按钮，用户只是从「点不动」变成「点了报错」。

### 22. 设计变了，界面文案不会自己跟着变

跨进程防截屏早期是**注入常驻载荷 DLL**，后来改成**一次性机器码桩**（写进目标进程、
调用返回即释放）。实现改完了，但三处界面还在说：

> 已找到跨进程载荷 SeewoCaptureGuard.Payload.dll，功能可用。
> 未找到 SeewoCaptureGuard.Payload.dll……请确认使用的是完整发行包。

这个文件**已经不存在了**。更糟的是「未找到」分支把用户指向「重新下载发行包」，
而真实原因是**系统版本太低**——排查方向直接被带偏。

同一批里还有一句更危险的：风险对话框说「关闭本程序……载荷会自行卸载」。
一次性桩没有这回事，**关掉程序保护依然留在那个窗口上**——相信这句话的用户，
正好会掉进上面第 21 条的单向门。

**规则：** 改实现（尤其是删掉某个组件）时，`grep` 那个组件的名字，把每处提到的文案一起改。
把「找不到某个文件」当成失败原因之前，先确认那个文件**还应该存在**。
`CaptureGuardUxContractTests.NoSourceClaimsAPayloadDllExists` 会扫描出货代码，
防止这类幽灵引用回来。

---

### 23. 配置项写进去了，不等于生效了

`AppSettings.FollowSystemTheme` 存在了很久，设置页有开关，读写两端都齐全——
**但没有任何一处读它**。`grep` 的结果只有三行：定义、`IsOn = `、`= IsOn`。
开关划来划去，主题纹丝不动，而所有测试都是绿的，因为它确实"正确"地存进了 JSON。

同一批里还有：顶栏那个主题按钮只改 `RequestedTheme`，**不落盘**，
所以每次重启都回到系统默认。用户看到的是"我设过了，它又变回去了"。

**规则：** 加一个配置项时，`grep` 一遍**读端**在哪。只有写端、没有读端的设置是幽灵功能，
而它比"没做这个功能"更糟——界面上明明白白摆着一个能点但没用的开关。
`ShellChromeContractTests.TheChosenThemeIsAppliedAndSurvivesARestart` 钉住"存了要读、
改了要存"这两半。

顺带一条同源的：设置项改名/删字段时，**旧 JSON 里的键不会报错，只会被静默忽略**。
`followSystemTheme:false` 在字段删掉之后会被 `System.Text.Json` 直接跳过，
于是所有曾经关掉它的用户被无声重置成"跟随系统"。要在加载时按原始 JSON 读旧键做迁移
（`AppSettings.MigrateLegacyTheme`），并且**显式的新键优先于旧键**——
否则一个同时含两个键的文件会被旧值永久钉死。

---

### 24. 模态弹窗会静默吃掉自动化点击

把页面说明从常驻段落改成首次进入的一次性弹窗之后，冒烟测试**没有报错**，
只是每一页都被判定为"控件点不到"：`ContentDialog` 是模态的，盖住整页，
后续所有 `Invoke` 都打在弹窗上。这类失败最像"功能坏了"，实际是测试不知道有这个功能。

**规则：** 自动化脚本要能主动关掉应用弹出的对话框。三个反直觉的点：

1. **关闭按钮的文案不能和页面上的真实按钮重名。** 最初的关闭助手匹配
   `关闭/取消/确定`，而「取消」「确定」正是截屏页和定时页上**真实存在**的按钮，
   它们又恰好因为"会改宿主配置"被冒烟测试明确排除在外——关闭助手差点去点它们。
   所以说明弹窗的关闭按钮叫「知道了」，一个任何页面都不用的词，
   并由 `PageIntroContractTests.TheIntroCloseButtonCannotCollideWithAPageButton` 钉住。
2. **要在导航之前也关一次。** 第一页的说明是在**启动时**弹的，
   那时循环还没开始跑；只在导航之后关，第一次导航的点击就已经被吃掉了。
3. **记录"已读"必须发生在 `ShowAsync` 之前。** `ContentDialog` 需要 `XamlRoot`，
   而 `OnNavigatedTo` 阶段页面还没进视觉树，此时 `ShowAsync` 会抛异常。
   先记录再显示，最坏结果是用户少看一次说明；反过来则是每次进页面都弹、且关不掉。
   现在改成等 `Loaded` 事件（那时 `XamlRoot` 一定可用），并保留"先记录"的顺序。

---

## 跨语言契约

`native/SeewoCommon/SeewoIpc.h` 是唯一真源，C# 侧在
`VirtualCameraFrameChannel.cs`（`FrameChannelContract`）里镜像。

**通道：**

| 通道 | 共享节 | 事件 | 用途 |
| --- | --- | --- | --- |
| 虚拟摄像头帧 | `Global\SeewoAssistant.VCam.Frame.v1` | `Global\...DataReady.v1` | UI 写 BGRA 帧，媒体源读 |

它遵循以下模式：先试 `Global\`，失败回退 `Local\`（创建 `Global\` 对象需要
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

**跨进程调用用一次性机器码桩，而不是常驻 DLL：** 早期实现注入一个常驻 DLL 并靠命名共享
内存通道通信，有两个致命问题：两边必须就命名空间和内存布局达成一致，不一致时**不报错**，
只是永远等不到回应（用户看到的是「超时」）；而且 DLL 会在第三方进程里驻留最多 10 分钟，
正是杀软启发式规则针对的模式。现在改为写入约 30 字节的位置无关机器码，在目标进程里直接调用
`SetWindowDisplayAffinity`，调用返回后立即释放。参考项目
[lilith-is-all-you-need/NoMoreCapture](https://github.com/lilith-is-all-you-need/NoMoreCapture)
用的就是这个方法，从 Windows 7 到 11 都能用且误报率很低。

**桩的字节必须精确：** 32 位 15 字节，64 位 36 字节。64 位必须预留 32 字节 shadow space
（`sub rsp, 0x28`）并保持 16 字节对齐，否则被调用方会覆盖调用者的栈帧，**目标进程直接崩溃**。
`NativeInteropContractTests` 里固定了这些操作码。

**跨位数注入：** 目标是 32 位时，`SetWindowDisplayAffinity` 的地址 = 目标进程的
`user32.dll` 基址 + **匹配位数的磁盘二进制**里该导出的 RVA。`PeExportReader` 就是为此存在的
自包含 PE 解析器。位数探测优先用 `IsWow64Process2`。

**注入不能在 UI 线程：** 它会等待远程线程完成。放在 UI 线程会冻结窗口。

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
| 虚拟摄像头的实际画面 | CI 虚拟机没有视频栈；且 Win10 走 DirectShow，`capture` 读不到 | Win11 跑 `capture`；Win10 用 `ffmpeg -f dshow` 或 OBS 选该设备 |
| Zoom/Teams/微信/OBS 兼容性 | 需要安装这些应用 | 真机逐个选用该摄像头 |
| 跨进程注入对真实杀软 | 无法模拟主动防御 | 在开着 Defender/360/火绒的机器上测试 |
| 希沃软件的扫描/挂起/断网/禁自启 | CI 上没装希沃 | 在装了希沃的教室机上测试 |
| 挂起后希沃的实际行为 | 同上 | 同上 |
| 视觉细节 | 截图能确认布局无崩溃，不能替代人眼 | 看 `test.yml` 的截图 artifact |
| 主题三态的实际观感 | 截图只能证明渲染成功，看不出配色是否协调 | 真机切换「跟随系统/浅色/深色」，检查标题栏按钮、卡片、Mica 是否都可读 |
| 安装包（exe）能否装成功 | 本机是 Linux，没有 Inno Setup，`.iss` **从未经过 ISCC 编译** | 打一个 tag 跑一次 `build.yml`，在真机确认安装、快捷方式、卸载、以及升级时旧版本被替换 |
| 「跟随系统」是否随系统实时变化 | 需要改系统主题 | 真机切系统主题，界面应立刻跟随；切成固定浅/深色后应不再跟随 |

**CI 绿灯的含义：** 能编译、能链接、单测通过、应用能启动、7 个页面能渲染、无 ERROR 日志、
COM 注册正确、摄像头能被创建和枚举；PowerShell 脚本带 BOM 且能被 5.1 解析；隐藏/恢复窗口的
计数非零；系统通知成功发出；没有未命名控件；没有任何 `Task.Run` 触碰 UI 控件；
三个列表的双向绑定开关都不依赖时序标志；没有托盘图标时本程序窗口不会被隐藏；
受保护的窗口总能从界面取消保护；界面不再提到已经不存在的载荷 DLL；顶栏不重复底栏状态；
选定的主题会被真正应用到窗口并在重启后保留；三态主题（跟随/浅/深）都可达；
每个页面都有唯一的一次性说明键且说明可被重新调出；说明弹窗的关闭按钮不与页面按钮重名。
**不**包含上表任何一项。

**CI 绿灯 ≠ 功能正确。** 三轮真机测试各自发现了 CI 全绿时的问题：通道名不一致、
PE 导出表偏移、UI 线程违规，以及最典型的一次 —— 只点一次「扫描开机自启项」就静默改写了
宿主机 7 个 Run 值和 10 余个计划任务，而冒烟测试报的是「全部通过」。
**真机测试是必要的，不是可选的。**

发现上表问题时的处理方式：先加一条能复现的自动化断言（如果可能），再修。这次的一批 bug 里，
共享通道名字不一致、安装包缺 DLL、希沃扫描中止、注入阻塞 UI 线程都是可以自动化的，
相应的断言已经加上。
