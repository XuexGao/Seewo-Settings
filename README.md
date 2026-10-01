# 希沃助手 (SeewoAssistant)

一个基于 **WinUI 3** 的 Windows 桌面工具集，用来自主掌控教室/办公电脑上的摄像头、麦克风和
屏幕内容，并对希沃（Seewo）系列软件做统一管控。

界面为简体中文，浅色为主并跟随系统深色主题，使用 Mica 背景与卡片式布局。

> **开发者请注意：** 实现细节、设计取舍、已知陷阱和验证方法都在
> **[AGENTS.md](AGENTS.md)**。本文只讲「这是什么、怎么用」。

---

## 它能做什么

| 模块 | 一句话说明 |
| --- | --- |
| **虚拟摄像头** | 注册一个虚拟摄像头，让 Zoom、Teams、微信、OBS 可以选择它，并推送你准备的画面 |
| **隐私监控** | 任何程序开始使用摄像头或麦克风时立即提醒 |
| **防截屏保护** | 让指定窗口在截图、录屏、屏幕共享中隐身或变成黑块 |
| **希沃软件管控** | 挂起、结束、阻断联网、禁止开机自启 |
| **定时任务** | 把以上功能自由组合成定时序列，另有关机、重启、注销 |
| **一键隐藏窗口** | 隐藏所有程序窗口，只留 Windows 自己的界面，随时可恢复 |

### 虚拟摄像头

推送三种画面来源：**纯色**、**本地图片**（按比例缩放居中，不变形）、**动态测试画面**
（彩条 + 移动扫描条，用于确认摄像头存活）。

按系统版本自动选择后端：

| 系统 | 实现 | 可见范围 |
| --- | --- | --- |
| Windows 11 内部版本 22000+ | 官方 `MFCreateVirtualCamera` + 自研 Media Foundation 媒体源 | 设备管理器、Windows「设置」、所有应用 |
| Windows 10 版本 2004+ | DirectShow 源滤镜 | Zoom、OBS、QQ/微信、ffmpeg 等 DirectShow 应用 |

> **Windows 10 说明：** `MFCreateVirtualCamera` 是 Windows 11 22000 才引入的 API，
> Windows 10 上不存在，所以 Win10 使用 DirectShow 方案。**这是完整可用的方案**，
> Zoom、OBS、QQ、微信、钉钉、ffmpeg、PotPlayer 等绝大多数软件都能选到它。
>
> 它有两个固有限制：不出现在 Windows「设置 → 蓝牙和其他设备 → 摄像头」里，
> UWP 应用（系统「相机」应用等）看不到它。这不是安装失败。
>
> 验证：安装后运行 `ffmpeg -list_devices true -f dshow -i dummy`，
> 列表中应出现「SeewoAssistant Virtual Camera」。

**摄像头不会黑屏。** 没有推送画面、推送停止、或 UI 进程退出时，媒体源会自动回退到内置的
动态测试画面。

### 隐私监控

监控 Windows 自己的隐私授权记录（也就是「设置 → 隐私和安全性」里显示的那份数据），
任何程序开始使用摄像头或麦克风时弹出提醒。**空闲时不占用 CPU，且不需要管理员权限。**

提醒有两种形式，可同时开启：

- Windows 系统通知
- 置顶醒目横幅（不会被「专注助手」静默吞掉）

支持按程序名或路径设置不提醒名单。

### 防截屏保护

两种保护模式：

- **穿透隐身**（`WDA_EXCLUDEFROMCAPTURE`）—— 窗口中在截图/录屏中完全不出现
- **黑块遮蔽**（`WDA_MONITOR`）—— 截图中显示为纯黑方块，保留占位

> **重要限制：** Windows 的 `SetWindowDisplayAffinity` **只能作用于调用进程自己的窗口**，
> 这是操作系统的硬性规定。因此：
>
> - **本程序自己的窗口**：稳定可用，不需要注入，不会被安全软件拦截
> - **其他程序的窗口**：需要在目标进程内执行一次调用（Windows 只允许进程操作自己的窗口），
>   默认**关闭**，需手动开启。开启后仍可能被杀毒软件拦截，或因为目标进程权限更高而失败
>
> 界面里把这一点写在了最显眼的位置，不会让人误以为它对所有窗口都有效。

窗口选择支持可搜索的列表，以及 Spy++ 式的雷达取景（拖到目标窗口上释放即可选中）。

### 一键隐藏窗口

隐藏当前所有程序窗口，只保留 Windows 自身的界面（任务栏、桌面、输入法、辅助功能）。
窗口**不会被关闭**，随时可以点「恢复所有窗口」还原。

适合上课或演示前快速清屏。程序自己的窗口会保持可见，所以这个操作永远可以撤销；
退出程序时也会自动恢复所有被隐藏的窗口。

判定「系统窗口」用的是明确的名单加上路径和窗口类名检查，而不是启发式规则——
隐藏任务栏或桌面会让电脑没法用，所以每一条豁免都是刻意写下的。

### 希沃软件管控

扫描本机**实际安装**的希沃组件（不硬编码进程名，希沃版本间会改名），然后可以：

- **挂起 / 恢复**进程（完全冻结，可逆）
- **结束**进程
- **禁止联网**（入站 + 出站双向防火墙规则）
- **禁止开机自启**（覆盖计划任务、注册表 Run 键、启动文件夹、系统服务四种机制）

禁用注册表启动项是**改名保留**而不是删除，随时可以完整还原。

### 定时任务

内置 cron 表达式解析器（`分 时 日 月 周`），支持 `*`、`1,3,5`、`1-5`、`*/15` 和 `@daily`
等宏。可以组合以上任何功能，例如：

```
0 8 * * 1-5     工作日 08:00 → 恢复希沃 + 启动虚拟摄像头推送校徽 + 开启防截屏
30 17 * * *     每天 17:30 → 挂起希沃 + 停止虚拟摄像头 + 关闭防截屏 + 关机
```

另有关机、重启、注销、锁定工作站。

> **关于「定时开机」：** 本程序不提供。处于完全关机状态的电脑上没有操作系统在运行，
> 软件无法唤醒它。只能靠 BIOS 的 RTC 定时唤醒或 Wake-on-LAN，两者都在 Windows 之外配置。
> 界面里说明了这一点并给出配置指引，而不是提供一个不可靠的按钮。

**调度行为：** 任务由本程序内的调度器触发，所以程序必须正在运行。**错过的触发不会补跑**
（机器在 08:00 处于睡眠、09:00 唤醒时，08:00 那次不会延迟执行）。

---

## 系统要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 版本 2004（内部版本 19041）或更高；Windows 11 推荐 |
| 架构 | x64 |
| 运行时 | **无需安装。** 发行包是自包含的，不依赖 .NET 运行时、Windows App SDK 运行时或 VC++ 可再发行组件 |
| 权限 | 基础功能无需管理员；部分功能需要，见下 |

### 哪些功能需要管理员权限

程序以普通用户权限运行，**不会**主动弹 UAC（对常驻托盘的程序来说那是个更差的取舍）。
需要管理员的操作会给出明确提示，标题栏也会显示当前权限状态。

| 功能 | 需要管理员 |
| --- | --- |
| 隐私监控、本程序窗口的防截屏、创建虚拟摄像头实例 | 否 |
| 注册虚拟摄像头组件 | 是（写注册表） |
| 跨进程防截屏 | 通常需要 |
| 防火墙规则、修改计划任务、定时关机 | 是 |
| 挂起 / 结束其他用户的进程 | 是 |

---

## 下载与安装

从 [Releases](../../releases) 下载 `SeewoAssistant-win-x64.zip`，解压到任意目录，
双击 `SeewoAssistant.exe` 即可运行，无需安装。

**首次使用虚拟摄像头**需要注册原生组件（写注册表，需要管理员权限）。
以管理员身份打开 PowerShell：

```powershell
cd <解压目录>\scripts
.\Install-Native.ps1 -Action Install     # 安装
.\Install-Native.ps1 -Action Status      # 查看当前状态
.\Install-Native.ps1 -Action Uninstall   # 卸载
```

脚本会按系统版本自动选择后端。

**关于 `SeewoAssistant-native-components.zip`：** 发行页面还有一个单独的包，里面只有
`x64/` 和 `x86/` 的原生 DLL（虚拟摄像头媒体源、DirectShow 滤镜、安装工具）。

**通常不需要它。** 主包 `SeewoAssistant-win-x64.zip` 已经包含全部原生组件，解压即用。
这个独立包只在这两种情况下有用：

- 主包中的原生组件被杀毒软件误删或损坏，可以解压后把 `x64/`、`x86/` 覆盖到
  `<解压目录>\native\` 下恢复；
- 需要单独分发原生组件（例如只给已有安装补一个 DirectShow 滤镜）。

### 操作方式

- **关闭主窗口**：程序隐藏到托盘，后台服务继续运行。要完全退出，请右键托盘图标选择「退出」。
- **托盘图标**：单击打开窗口；右键显示菜单（打开、开关监控、开关虚拟摄像头、退出）。

---

## 从源码构建

需要 Visual Studio 2022（C++ 桌面开发 + .NET 桌面开发工作负载）、.NET 8 SDK、
Windows SDK 10.0.26100。

```powershell
# 原生组件（每个项目单独构建）
msbuild native/SeewoVirtualCamera/SeewoVirtualCamera.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoVirtualCamera.DShow/SeewoVirtualCamera.DShow.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild native/SeewoVirtualCamera.DShow/SeewoVirtualCamera.DShow.vcxproj /p:Configuration=Release /p:Platform=Win32
msbuild native/SeewoVirtualCamera.Setup/SeewoVirtualCamera.Setup.vcxproj /p:Configuration=Release /p:Platform=x64

# 测试
dotnet test src/SeewoAssistant.Tests/SeewoAssistant.Tests.csproj -c Release

# 主程序
dotnet publish src/SeewoAssistant/SeewoAssistant.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
  -p:EnableMsixTooling=true -p:Platform=x64 -o artifacts/publish/win-x64
```

也可以用 `SeewoAssistant.sln` 在 Visual Studio 中打开。

### 自动化构建与测试

CI 分成两个 workflow，因为「能编译」和「能用」是两回事：

| Workflow | 做什么 |
| --- | --- |
| [`build.yml`](.github/workflows/build.yml) | 编译全部原生与托管项目、跑单元测试、打包发行版 |
| [`test.yml`](.github/workflows/test.yml) | **真的启动应用**，遍历所有页面截图，点击关键按钮，检查日志有无错误 |

`test.yml` 会把截图和日志作为 artifact 上传——**改动界面后请查看这些截图**。
推送 `v*` 标签时会额外创建 GitHub Release。

---

## 权限说明

本项目涉及的能力都比较敏感，边界如下：

- **不做**任何数据上传。程序没有网络客户端，除防火墙规则外不发起任何连接。
- **不修改**系统文件、不写驱动、不装服务、不常驻开机自启（除非你在设置里显式开启）。
- 跨进程注入**默认关闭**，开启时需二次确认，且只对显式选中的**单个**窗口生效。
- 跨进程调用只写入约 30 字节的一次性机器码，调用返回后**立即释放**，目标进程不留任何常驻代码或数据。
- 退出程序时会**自动恢复所有被挂起的进程**。
- 配置、日志和崩溃报告都在 `%LOCALAPPDATA%\SeewoAssistant`，卸载脚本不会自动删除。

**关于杀软误报：** 跨进程注入是杀软主动防御的重点监控行为，这是技术本身的固有属性。
被拦截时程序会如实报告，不会静默重试。

---

## 项目结构

```
Seewo-Settings/
├─ src/
│  ├─ SeewoAssistant/          WinUI 3 主程序（界面）
│  ├─ SeewoAssistant.Core/     全部业务逻辑（可单测）
│  └─ SeewoAssistant.Tests/    单元测试
├─ native/                     原生组件（C++17，仅 Windows SDK）
│  ├─ SeewoCommon/             跨语言通信契约
│  ├─ SeewoVirtualCamera/      MF 自定义媒体源
│  ├─ SeewoVirtualCamera.DShow/ DirectShow 源滤镜
│  ├─ SeewoVirtualCamera.Setup/ 注册与摄像头管理工具
├─ scripts/Install-Native.ps1  安装 / 卸载 / 状态查询
├─ .github/workflows/          build.yml（构建）、test.yml（验证）
├─ AGENTS.md                   实现细节与开发指南
└─ docs/ARCHITECTURE.md        架构与设计取舍
```

---

## 已知限制

这些限制是技术性的，不是实现疏漏：

1. **Windows 10 上虚拟摄像头不出现在系统设置里。** DirectShow 方案的固有限制。
2. **`WDA_EXCLUDEFROMCAPTURE` 需要内部版本 19041。** 更低版本会降级为黑块遮蔽。
3. **防截屏不是安全特性。** 微软明确说明它不保证严格保护窗口内容（比如用手机拍屏幕）。
   它防的是操作系统公开的截图/录屏 API，不是物理拍摄。
4. **跨进程防截屏可能被安全软件拦截。** 见上文。
5. **定时任务只在程序运行时触发。** 未实现写入 Windows 任务计划程序。
6. **不支持定时开机。** 见上文说明。
7. **摄像头句柄归因需要管理员权限。** 未提权时仍会报告应用路径，只是没有 PID。
8. **虚拟摄像头的画面内容尚未在真机上验证。** 自动化测试能证明摄像头被注册、被枚举、
   组件注册正确，但 CI 机器没有视频设备，无法读取真实画面。请在装有摄像头的 Windows 上运行：
   ```powershell
   # Windows 11（Media Foundation 后端）：
   SeewoVirtualCamera.Setup.exe capture --frames 60 --out frame.png

   # Windows 10（DirectShow 后端）——capture 读不到 DShow 设备，用 ffmpeg：
   ffmpeg -list_devices true -f dshow -i dummy
   ```
   `capture` 只能读 Media Foundation 设备，Windows 10 的 DirectShow 滤镜不在其中，
   在那台机器上运行它一定会报「没有找到虚拟摄像头」。这不是安装失败。
9. **第三方应用兼容性尚未实测。** Zoom / Teams / 微信 / OBS 能否选中并使用这个摄像头，
   需要逐个确认。
10. **跨进程注入对真实杀软的表现尚未验证。**
11. **希沃相关功能尚未在装了希沃的机器上实测。**

第 8–11 项是「代码路径已走通、但缺少真机验证」，不是已知的缺陷。
详见 [AGENTS.md 的「尚未验证的部分」](AGENTS.md#尚未验证的部分)。

---

## 常见问题

**Q：虚拟摄像头装好了，但 Zoom 里看不到？**
先确认用的是哪个后端。Windows 10 的 DirectShow 方案不会出现在 Windows「设置」的摄像头列表里，
但应该出现在 Zoom 的摄像头下拉框中。用 `ffmpeg -list_devices true -f dshow -i dummy` 可以确认。
Windows 11 上如果看不到，运行 `SeewoVirtualCamera.Setup.exe list` 检查设备是否已创建，
并确认「设置 → 隐私和安全性 → 相机」中的相机访问没有被关闭。

**Q：摄像头有画面但一直是彩条，不是我要推的图片？**
说明媒体源没有读到推送的帧，正在使用内置测试画面。检查页面上的「已发送帧数」是否在增长，
以及分辨率是否超过 1920×1080 上限。

**Q：隐私监控显示「尚未找到系统记录」？**
这是正常的。系统的授权记录只在**某个程序第一次使用摄像头之后**才会出现。
先用任意程序开一次摄像头即可，或点页面上的「测试提醒」验证提醒链路。

**Q：关闭窗口后程序不见了？**
程序隐藏到了托盘（这是设计行为，后台监控继续运行）。单击托盘图标可以恢复窗口。
要完全退出，右键托盘图标选择「退出」。

**Q：跨进程防截屏提示被杀软拦截？**
这是该技术的固有代价。可以尝试以管理员身份运行，或只使用本程序窗口保护
（那条路径永远可用且零风险）。

**Q：程序崩溃了，怎么反馈？**
崩溃时程序会在 `%LOCALAPPDATA%\SeewoAssistant\crashes` 下生成一份自包含的报告，
并把路径复制到剪贴板，直接粘贴即可。

---

## 许可证

[MIT](LICENSE)
