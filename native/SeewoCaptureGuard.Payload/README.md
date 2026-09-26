# SeewoCaptureGuard.Payload

跨进程防截屏的注入载荷。

## 为什么需要它

`SetWindowDisplayAffinity` 只能作用于**调用进程自己拥有**的窗口。在 `win32kfull.sys`
里内核会校验 `HWND` 是否属于调用方进程，跨进程调用直接返回
`ERROR_ACCESS_DENIED`。这是设计使然，不是 bug，也没有用户态的办法绕过。

所以要让第三方窗口在截图/录屏里消失，只有一条路：**让目标进程自己去调用这个 API**。
本 DLL 就是被注入到目标进程里、替它完成这次调用的载荷。

参考项目 [NoMoreCapture](https://github.com/lilith-is-all-you-need/NoMoreCapture) 用的是
远程 shellcode；本项目改用 **DLL 注入**，因为 DLL 可调试、可版本管理、崩溃时堆栈可读，
并且能干净卸载。

## 工作流程

```
SeewoAssistant (UI 进程)
        │
        │ 1. OpenProcess / VirtualAllocEx / WriteProcessMemory
        │ 2. CreateRemoteThread(LoadLibraryW, "SeewoCaptureGuard.Payload.dll")
        ▼
目标进程 ──► DllMain(DLL_PROCESS_ATTACH)   ← 只做 DisableThreadLibraryCalls
        │
        │ 3. CreateRemoteThread(GetProcAddress(dll, "SeewoCaptureGuardEntry"))
        ▼
   SeewoCaptureGuardEntry()                ← 在 loader lock 之外执行
        │
        │ 4. 打开共享节 Global\SeewoAssistant.CaptureGuard.v1
        │    写入 payloadPid，告诉注入器"我起来了"
        │ 5. 循环等待命名事件，读 GuardRequest
        │ 6. SetWindowDisplayAffinity(targetHwnd, affinity)
        │ 7. 回写 result / lastError / completed
        │ 8. 收到 Unload，或超过 10 分钟生命周期上限
        ▼
   FreeLibraryAndExitThread()              ← 自我卸载，宿主进程不留痕迹
```

请求/响应的内存布局定义在 `native/SeewoCommon/SeewoIpc.h` 的 `seewo::GuardRequest`，
C# 侧对应 `SeewoAssistant.Core/Services/CaptureGuard/CaptureGuardInterop.cs`。
**两边必须同步修改**，改动时记得同步提升 `kGuardVersion`。

## 安全设计

| 措施 | 说明 |
| --- | --- |
| `DllMain` 几乎为空 | 只调 `DisableThreadLibraryCalls`。任何加载器锁内的实际工作都可能死锁宿主进程 |
| 入口函数用 SEH 包裹 | 任何意外异常都转成退出码，绝不让宿主崩溃 |
| 静态 CRT（`/MT`） | 可以注入到没装 VC++ 运行时的进程 |
| 生命周期上限 10 分钟 | 即使注入器异常退出，载荷也不会永久驻留 |
| 自我卸载 | `FreeLibraryAndExitThread`，线程和模块一起消失 |
| 无文件/注册表/网络访问 | 这个 DLL 刻意"无聊"，它只做一件事 |
| 重复注入保护 | 进程内 `InterlockedCompareExchange` 哨兵，第二次进入直接返回 |

## 绝对不要注入的进程

注入器内置了硬编码黑名单，以下进程**一律拒绝**：

| 进程 | 原因 |
| --- | --- |
| `dwm.exe` | 桌面窗口管理器。注入它可能直接黑屏 |
| `explorer.exe` | 桌面与任务栏宿主 |
| `csrss.exe` | 关键系统进程，注入会导致立即蓝屏 |
| `winlogon.exe` | 登录会话管理 |
| `services.exe` / `lsass.exe` | 服务控制与安全认证 |
| `System` / `Registry` | 内核伪进程，无法注入 |
| `Progman` / `WorkerW` 窗口 | 桌面背景层，动它会打穿 DWM 合成 |

## 杀软误报说明

跨进程注入是杀软主动防御（HIPS）的**重点监控行为**，这是技术本身的固有属性，
不是本项目的缺陷。实测可能被 Windows Defender、360、火绒等拦截。

因此本功能在 SeewoAssistant 里**默认关闭**，并且：

- 开启时弹出明确的二次确认，写清楚会发生什么；
- 只对用户显式选中的单个窗口生效，不做"全局模式"批量注入；
- 失败时如实报告被拦截，不静默重试。

## 调试

1. 用 Process Explorer 找到目标进程，确认 `SeewoCaptureGuard.Payload.dll` 已加载。
2. 在 Visual Studio 里附加到目标进程，在 `SeewoCaptureGuardEntry` 下断点。
   注意：断在 `DllMain` 里会让整个进程卡住，这是正常的加载器锁行为。
3. 共享节可以用 WinDbg 的 `!object \BaseNamedObjects` 确认存在。
4. 载荷退出码含义：

| 退出码 | 含义 |
| --- | --- |
| 0 | 正常退出（收到 Unload 或达到生命周期上限） |
| 1 | 进程内已有一个载荷实例在运行 |
| 2 | 5 秒内没等到共享节，注入器可能没创建 |
| 3 | 5 秒内没等到命名事件 |
| 4 | 发生未预期的异常，已被 SEH 捕获 |

## 构建

```
msbuild SeewoCaptureGuard.Payload.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild SeewoCaptureGuard.Payload.vcxproj /p:Configuration=Release /p:Platform=Win32
```

x64 和 Win32 两个版本都要构建：注入器会用 `IsWow64Process2` 探测目标进程位数，
再选择对应位数的载荷。位数不匹配的 `LoadLibraryW` 会静默失败。
