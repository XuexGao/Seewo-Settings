using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;

namespace SeewoAssistant.Core.Services.CaptureGuard;

/// <summary>Whether a process is 32-bit or 64-bit.</summary>
internal enum ProcessBitness
{
    /// <summary>Could not be determined; injection must not be attempted.</summary>
    Unknown,

    /// <summary>32-bit, including a 32-bit process under WOW64.</summary>
    X86,

    /// <summary>64-bit.</summary>
    X64,
}

/// <summary>
/// Applies <c>SetWindowDisplayAffinity</c> to a window owned by another process by
/// running a single machine-code stub inside that process.
/// </summary>
/// <remarks>
/// <para>
/// Windows only permits <c>SetWindowDisplayAffinity</c> on a window the calling process
/// owns, so a cross-process change requires executing the call in the target. This class
/// does that with a short position-independent stub - about 30 bytes - written into the
/// target's memory and run on a remote thread.
/// </para>
/// <para>
/// This replaces an earlier design that injected a full DLL and then talked to it over a
/// named shared-memory channel. That design had two failure modes that were very hard to
/// diagnose, and both are gone here:
/// </para>
/// <list type="bullet">
/// <item><description>
/// It required the two sides to agree on a namespace (<c>Global\</c> versus
/// <c>Local\</c>) and on a shared-memory layout. A disagreement produced no error at all
/// - the injector simply waited for a reply that could never arrive.
/// </description></item>
/// <item><description>
/// It left a DLL loaded in a third-party process for up to ten minutes, which is exactly
/// the pattern antivirus heuristics are trained to flag.
/// </description></item>
/// </list>
/// <para>
/// The stub is self-contained: it needs no imports, no relocations and no shared state,
/// and it is freed as soon as the call returns. The approach is modelled on the reference
/// project <c>NoMoreCapture</c>, which uses it successfully across Windows 7 to 11 with a
/// very low antivirus false-positive rate.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ShellcodeAffinitySetter
{
    private readonly IAppLogger _logger;

    public ShellcodeAffinitySetter(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Processes that must never be injected into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Injecting the desktop compositor can black out the session, and injecting a
    /// critical system process bugchecks the machine. The reference project skips only
    /// <c>explorer.exe</c>, <c>dwm.exe</c>, <c>cmd.exe</c> and <c>conhost.exe</c>; this
    /// list is deliberately longer because the cost of being wrong is a crashed machine
    /// rather than a failed operation.
    /// </para>
    /// <para>
    /// It is also where the old payload injector's guard now lives, so removing that
    /// class did not remove the protection.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> UnsafeProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Kernel pseudo-processes.
        "system",
        "registry",
        "idle",
        "memcompression",

        // Session and security infrastructure: injecting these bugchecks the machine.
        "smss.exe",
        "csrss.exe",
        "wininit.exe",
        "winlogon.exe",
        "services.exe",
        "lsass.exe",
        "lsaiso.exe",
        "svchost.exe",

        // The desktop compositor and the shell: injecting these can black the screen.
        "dwm.exe",
        "explorer.exe",
        "sihost.exe",
        "shellexperiencehost.exe",
        "startmenuexperiencehost.exe",
        "searchhost.exe",

        // Audio and font hosts, plus Defender: no benefit, real risk.
        "audiodg.exe",
        "fontdrvhost.exe",
        "securityhealthservice.exe",
        "msmpeng.exe",
        "wudfhost.exe",

        // Console hosts: injecting them serves no purpose.
        "cmd.exe",
        "conhost.exe",
    };

    /// <summary>
    /// Sets the display affinity of a window belonging to another process.
    /// </summary>
    /// <param name="processId">The process that owns the window.</param>
    /// <param name="windowHandle">The target window.</param>
    /// <param name="affinity">The <c>WDA_*</c> value to apply.</param>
    /// <returns>A result describing what happened.</returns>
    public ActionResult SetAffinity(int processId, nint windowHandle, uint affinity)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("跨进程防截屏只能在 Windows 上使用。");
        }

        if (processId <= 4 || windowHandle == nint.Zero)
        {
            return ActionResult.Fail("目标进程或窗口无效。");
        }

        if (IsUnsafeProcess(processId, out var reason))
        {
            return ActionResult.Fail(reason);
        }

        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION |
            NativeMethods.PROCESS_VM_READ |
            NativeMethods.PROCESS_VM_OPERATION |
            NativeMethods.PROCESS_VM_WRITE |
            NativeMethods.PROCESS_CREATE_THREAD,
            false,
            (uint)processId);

        if (process == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();

            return ActionResult.Fail(
                error == NativeMethods.ERROR_ACCESS_DENIED
                    ? "打开目标进程被拒绝。请以管理员身份运行本程序后重试。"
                    : $"打开目标进程失败，Win32 错误 {error}。");
        }

        nint remoteCode = nint.Zero;

        try
        {
            // The address of SetWindowDisplayAffinity inside the target. user32.dll is
            // mapped at the same base in every process of the same bitness only for
            // kernel32 historically, so the target's own user32 base is read and the
            // export RVA - taken from the matching on-disk binary - is added to it.
            var user32Base = FindRemoteModuleBase(process, "user32.dll");

            if (user32Base == nint.Zero)
            {
                return ActionResult.Fail("无法在目标进程中定位 user32.dll。");
            }

            var bitness = GetProcessBitness(process);
            var rva = PeExportReader.GetExportRva(GetOnDiskUser32Path(bitness), "SetWindowDisplayAffinity");

            if (rva == 0)
            {
                // Two very different causes, previously conflated into one message that
                // blamed the OS. The build check is separate so a parse failure is not
                // reported as "your Windows is too old" - which is what sent the last
                // investigation in the wrong direction while the real cause was an
                // off-by-four in the export-table parser.
                var tooOld = !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

                return ActionResult.Fail(
                    tooOld
                        ? "SetWindowDisplayAffinity 需要 Windows 10 版本 2004（内部版本 19041）或更高，" +
                          "当前系统版本低于该要求，无法使用跨进程防截屏。"
                        : "无法从 user32.dll 解析 SetWindowDisplayAffinity 的导出地址。" +
                          "系统版本满足要求，因此这是导出表解析失败而非系统不支持，" +
                          "请附上日志反馈以便定位。");
            }

            var apiAddress = user32Base + rva;

            var stub = bitness == ProcessBitness.X86
                ? BuildX86Stub(windowHandle, apiAddress, affinity)
                : BuildX64Stub(windowHandle, apiAddress, affinity);

            remoteCode = NativeMethods.VirtualAllocEx(
                process, nint.Zero, (nuint)stub.Length,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_EXECUTE_READWRITE);

            if (remoteCode == nint.Zero)
            {
                return ActionResult.Fail($"在目标进程中分配内存失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
            }

            if (!NativeMethods.WriteProcessMemory(
                    process, remoteCode, stub, (nuint)stub.Length, out var written) ||
                written != (nuint)stub.Length)
            {
                return ActionResult.Fail($"写入目标进程内存失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
            }

            var thread = NativeMethods.CreateRemoteThread(
                process, nint.Zero, 0, remoteCode, nint.Zero, 0, nint.Zero);

            if (thread == nint.Zero)
            {
                var error = Marshal.GetLastWin32Error();

                return ActionResult.Fail(
                    error == NativeMethods.ERROR_ACCESS_DENIED
                        ? "创建远程线程被拒绝。这通常意味着被杀软或 EDR 的主动防御拦截了。"
                        : $"创建远程线程失败，Win32 错误 {error}。" +
                          (error == 5 ? " 这通常意味着被杀软主动防御拦截。" : string.Empty));
            }

            try
            {
                // The stub calls the API and returns, so a short wait is enough. A
                // timeout means the target is suspended or blocked, which is worth
                // reporting rather than treating as success.
                var wait = NativeMethods.WaitForSingleObject(thread, 2000);

                if (wait != NativeMethods.WAIT_OBJECT_0)
                {
                    return ActionResult.Fail(
                        "目标进程没有在 2 秒内完成调用。它可能被挂起或正在忙，请稍后重试。");
                }

                _logger.Info(
                    $"Applied display affinity 0x{affinity:X} to window 0x{windowHandle:X} " +
                    $"in PID {processId} via remote stub.");

                return ActionResult.Ok("已通过远程调用设置窗口的防截屏属性。");
            }
            finally
            {
                NativeMethods.CloseHandle(thread);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Cross-process affinity call failed for PID {processId}.", ex);
            return ActionResult.Fail($"跨进程调用失败：{ex.Message}", ex);
        }
        finally
        {
            if (remoteCode != nint.Zero)
            {
                NativeMethods.VirtualFreeEx(process, remoteCode, 0, NativeMethods.MEM_RELEASE);
            }

            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>
    /// Builds the 32-bit stub: push affinity; push hwnd; mov eax, api; call eax; ret.
    /// </summary>
    /// <remarks>
    /// <c>stdcall</c> with two arguments pushes them right to left and the callee cleans
    /// up, so no explicit stack adjustment is needed.
    /// </remarks>
    private static byte[] BuildX86Stub(nint windowHandle, nint apiAddress, uint affinity)
    {
        return
        [
            0x6A, (byte)(affinity & 0xFF),                  // push affinity
            0x68,                                           // push hwnd
            (byte)(windowHandle & 0xFF),
            (byte)((windowHandle >> 8) & 0xFF),
            (byte)((windowHandle >> 16) & 0xFF),
            (byte)((windowHandle >> 24) & 0xFF),
            0xB8,                                           // mov eax, apiAddress
            (byte)(apiAddress & 0xFF),
            (byte)((apiAddress >> 8) & 0xFF),
            (byte)((apiAddress >> 16) & 0xFF),
            (byte)((apiAddress >> 24) & 0xFF),
            0xFF, 0xD0,                                     // call eax
            0xC3,                                           // ret
        ];
    }

    /// <summary>
    /// Builds the 64-bit stub for the Microsoft x64 calling convention.
    /// </summary>
    /// <remarks>
    /// The first two integer arguments go in <c>rcx</c> and <c>rdx</c>, and the caller
    /// must reserve 32 bytes of shadow space and keep the stack 16-byte aligned at the
    /// call. 0x28 is 32 bytes of shadow space plus the 8 bytes that re-aligns the stack
    /// after the return address has been pushed.
    /// </remarks>
    private static byte[] BuildX64Stub(nint windowHandle, nint apiAddress, uint affinity)
    {
        return
        [
            0x48, 0x83, 0xEC, 0x28,                         // sub rsp, 0x28
            0x48, 0xB9,                                     // mov rcx, windowHandle
            (byte)(windowHandle & 0xFF),
            (byte)((windowHandle >> 8) & 0xFF),
            (byte)((windowHandle >> 16) & 0xFF),
            (byte)((windowHandle >> 24) & 0xFF),
            (byte)((windowHandle >> 32) & 0xFF),
            (byte)((windowHandle >> 40) & 0xFF),
            (byte)((windowHandle >> 48) & 0xFF),
            (byte)((windowHandle >> 56) & 0xFF),
            0xBA,                                           // mov edx, affinity
            (byte)(affinity & 0xFF),
            (byte)((affinity >> 8) & 0xFF),
            (byte)((affinity >> 16) & 0xFF),
            (byte)((affinity >> 24) & 0xFF),
            0x48, 0xB8,                                     // mov rax, apiAddress
            (byte)(apiAddress & 0xFF),
            (byte)((apiAddress >> 8) & 0xFF),
            (byte)((apiAddress >> 16) & 0xFF),
            (byte)((apiAddress >> 24) & 0xFF),
            (byte)((apiAddress >> 32) & 0xFF),
            (byte)((apiAddress >> 40) & 0xFF),
            (byte)((apiAddress >> 48) & 0xFF),
            (byte)((apiAddress >> 56) & 0xFF),
            0xFF, 0xD0,                                     // call rax
            0x48, 0x83, 0xC4, 0x28,                         // add rsp, 0x28
            0xC3,                                           // ret
        ];
    }

    /// <summary>Path to the user32.dll matching the target's architecture.</summary>
    private static string GetOnDiskUser32Path(ProcessBitness bitness) =>
        bitness == ProcessBitness.X86
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "user32.dll")
            : Path.Combine(Environment.SystemDirectory, "user32.dll");

    /// <summary>
    /// True when a process name is on the never-inject list.
    /// </summary>
    /// <param name="processName">A name with or without the <c>.exe</c> suffix.</param>
    internal static bool IsForbiddenProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return true;
        }

        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : processName + ".exe";

        return UnsafeProcessNames.Contains(name);
    }

    /// <summary>True when a process must not be injected into.</summary>
    private static bool IsUnsafeProcess(int processId, out string reason)
    {
        reason = string.Empty;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            var name = process.ProcessName + ".exe";

            if (UnsafeProcessNames.Contains(name))
            {
                reason = $"拒绝注入 {name}：注入桌面合成器可能导致屏幕黑屏，注入命令行宿主没有意义。";
                return true;
            }

            return false;
        }
        catch (ArgumentException)
        {
            reason = $"进程 {processId} 不存在或已退出。";
            return true;
        }
        catch (InvalidOperationException)
        {
            reason = $"进程 {processId} 已退出。";
            return true;
        }
    }

    /// <summary>Finds a loaded module's base address inside another process.</summary>
    private static nint FindRemoteModuleBase(nint process, string moduleName)
    {
        uint needed = 0;

        // A generous first guess; the call reports the real size when it is too small.
        var handles = new nint[1024];

        if (!NativeMethods.EnumProcessModulesEx(
                process, handles, (uint)(handles.Length * nint.Size), out needed, NativeMethods.LIST_MODULES_ALL))
        {
            return nint.Zero;
        }

        var count = (int)(needed / (uint)nint.Size);
        var buffer = new System.Text.StringBuilder(260);

        for (var i = 0; i < count && i < handles.Length; i++)
        {
            if (buffer.Capacity > 0)
            {
                buffer.Clear();
            }

            if (NativeMethods.GetModuleBaseNameW(process, handles[i], buffer, (uint)buffer.Capacity) == 0)
            {
                continue;
            }

            if (string.Equals(buffer.ToString(), moduleName, StringComparison.OrdinalIgnoreCase))
            {
                return handles[i];
            }
        }

        return nint.Zero;
    }

    /// <summary>Determines whether a process is 32-bit.</summary>
    private static ProcessBitness GetProcessBitness(nint process)
    {
        if (!NativeMethods.IsWow64Process2(process, out var processMachine, out _))
        {
            return nint.Size == 8 ? ProcessBitness.X64 : ProcessBitness.X86;
        }

        // IMAGE_FILE_MACHINE_UNKNOWN means the process runs natively.
        const ushort ImageFileMachineUnknown = 0;

        return processMachine == ImageFileMachineUnknown
            ? ProcessBitness.X64
            : ProcessBitness.X86;
    }
}
