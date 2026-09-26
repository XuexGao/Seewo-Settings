// SeewoVirtualCamera.Setup
//
// Console tool that registers the virtual camera media source and creates or
// removes camera instances.
//
// Why this is a separate process rather than part of the app:
//   1. MFCreateVirtualCamera must not run on a UI thread. It performs a Capability
//      Access Manager consent check, which itself needs the UI thread; calling it
//      from a UI thread deadlocks the app. A console tool has no UI thread to
//      deadlock.
//   2. MFCreateVirtualCamera lives in mfsensorgroup.dll, which only exists on
//      Windows 11 22000+. Loading it here means the main app starts normally on
//      Windows 10 instead of failing at load time.
//   3. Registration writes to HKLM, so it needs elevation; isolating that in a
//      small tool keeps the main app running as a standard user.

#include <windows.h>
#include <shellapi.h>
#include <winternl.h>   // RTL_OSVERSIONINFOW, used by the version check
#include <mfapi.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <mfvirtualcamera.h>
#include <shlwapi.h>
#include <strsafe.h>

#include <cstdarg>
#include <cstdio>
#include <cwchar>   // wcslen, used when writing to the console
#include <cstring>  // memcmp, used by the frame comparison
#include <string>
#include <vector>

#include "SeewoVirtualCamera.Setup.Capture.h"

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "advapi32.lib")

namespace {

// Must match native/SeewoVirtualCamera/SeewoVirtualCameraActivate.h
constexpr wchar_t kMediaSourceClsid[] = L"{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}";
constexpr wchar_t kMediaSourceDllName[] = L"SeewoVirtualCamera.dll";
constexpr wchar_t kDefaultFriendlyName[] = L"SeewoAssistant Virtual Camera";

// MFCreateVirtualCamera is resolved at runtime so this executable still starts on
// Windows 10, where mfsensorgroup.dll does not exist.
using MFCreateVirtualCameraFn = HRESULT(WINAPI*)(
    MFVirtualCameraType type,
    MFVirtualCameraLifetime lifetime,
    MFVirtualCameraAccess access,
    LPCWSTR friendlyName,
    LPCWSTR sourceId,
    const GUID* categories,
    ULONG categoryCount,
    IMFVirtualCamera** virtualCamera);

// Writes one line of text to stdout in a form the consumer can actually read.
//
// This is more involved than a plain fwprintf because the two consumers need
// different encodings:
//
//   * A console window wants UTF-16 and renders it correctly, but only through
//     WriteConsoleW. Putting the console into _O_U16TEXT mode achieves the same
//     thing and then breaks the other case completely.
//   * A redirected stream (a pipe, or PowerShell capturing the output during CI)
//     is read as bytes. Writing UTF-16 there produces interleaved NUL bytes that
//     decode as mojibake - which is exactly what happened in the build log.
//
// So the handle is tested once and the appropriate path is used from then on.
bool StdoutIsConsole() {
    static const bool isConsole = []() {
        const HANDLE handle = ::GetStdHandle(STD_OUTPUT_HANDLE);
        if (handle == nullptr || handle == INVALID_HANDLE_VALUE) {
            return false;
        }
        DWORD mode = 0;
        return ::GetConsoleMode(handle, &mode) != FALSE;
    }();
    return isConsole;
}

void Print(const wchar_t* format, ...) {
    wchar_t buffer[2048] = {};

    va_list args;
    va_start(args, format);
    const int written = ::_vsnwprintf_s(buffer, _TRUNCATE, format, args);
    va_end(args);

    if (written < 0) {
        return;  // Truncated; nothing useful to print.
    }

    if (StdoutIsConsole()) {
        const HANDLE handle = ::GetStdHandle(STD_OUTPUT_HANDLE);
        DWORD consumed = 0;

        // The buffer may exceed what WriteConsoleW accepts in one call, so loop.
        const wchar_t* cursor = buffer;
        size_t remaining = ::wcslen(buffer);

        while (remaining > 0) {
            const DWORD chunk = static_cast<DWORD>(
                remaining > 8192 ? 8192 : remaining);
            if (!::WriteConsoleW(handle, cursor, chunk, &consumed, nullptr) || consumed == 0) {
                break;
            }
            cursor += consumed;
            remaining -= consumed;
        }

        ::WriteConsoleW(handle, L"\r\n", 2, &consumed, nullptr);
        return;
    }

    // Redirected: emit UTF-8 so the bytes decode correctly in a log or a pipe.
    // PowerShell 7 reads redirected native output as UTF-8 by default, and CI log
    // viewers expect UTF-8 as well.
    const int utf8Length = ::WideCharToMultiByte(
        CP_UTF8, 0, buffer, -1, nullptr, 0, nullptr, nullptr);

    if (utf8Length <= 1) {  // 1 because the count includes the terminator.
        return;
    }

    std::string utf8(static_cast<size_t>(utf8Length - 1), '\0');
    ::WideCharToMultiByte(
        CP_UTF8, 0, buffer, -1, utf8.data(), utf8Length, nullptr, nullptr);

    ::fwrite(utf8.data(), 1, utf8.size(), stdout);
    ::fputs("\n", stdout);
    ::fflush(stdout);
}

void PrintError(const wchar_t* operation, DWORD error) {
    wchar_t buffer[512] = {};
    ::FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                     nullptr, error, 0, buffer, ARRAYSIZE(buffer), nullptr);

    // Strip the trailing CR/LF FormatMessage adds.
    std::wstring message = buffer;
    while (!message.empty() && (message.back() == L'\n' || message.back() == L'\r')) {
        message.pop_back();
    }

    Print(L"[错误] %ls 失败 (Win32 %lu): %ls", operation, error, message.c_str());
}

void PrintHResult(const wchar_t* operation, HRESULT hr) {
    Print(L"[错误] %ls 失败 (HRESULT 0x%08X)", operation, static_cast<unsigned>(hr));
}

// Directory containing this executable, which is also where the media source DLL
// and its dependencies live.
std::wstring GetExecutableDirectory() {
    wchar_t path[MAX_PATH] = {};
    const DWORD length = ::GetModuleFileNameW(nullptr, path, ARRAYSIZE(path));

    if (length == 0 || length >= ARRAYSIZE(path)) {
        return L".";
    }

    std::wstring result(path, length);
    const size_t slash = result.find_last_of(L'\\');
    return slash == std::wstring::npos ? L"." : result.substr(0, slash);
}

// ---------------------------------------------------------------------- registry

bool RegisterComServer(const std::wstring& dllPath, bool registerServer) {
    // The media source is an in-proc COM server registered per-machine so the
    // FrameServer service, which runs as a service account, can load it.
    HKEY classesKey = nullptr;

    if (::RegCreateKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Classes\\CLSID",
                          0, nullptr, 0, KEY_WRITE, nullptr, &classesKey, nullptr) != ERROR_SUCCESS) {
        PrintError(L"打开 HKLM\\SOFTWARE\\Classes\\CLSID", ::GetLastError());
        return false;
    }

    const std::wstring clsidKeyPath = std::wstring(kMediaSourceClsid);
    const std::wstring inProcPath = clsidKeyPath + L"\\InProcServer32";

    if (registerServer) {
        HKEY clsidKey = nullptr;
        if (::RegCreateKeyExW(classesKey, clsidKeyPath.c_str(), 0, nullptr, 0,
                              KEY_WRITE, nullptr, &clsidKey, nullptr) != ERROR_SUCCESS) {
            PrintError(L"创建 CLSID 键", ::GetLastError());
            ::RegCloseKey(classesKey);
            return false;
        }
        ::RegCloseKey(clsidKey);

        HKEY inProcKey = nullptr;
        if (::RegCreateKeyExW(classesKey, inProcPath.c_str(), 0, nullptr, 0,
                              KEY_WRITE, nullptr, &inProcKey, nullptr) != ERROR_SUCCESS) {
            PrintError(L"创建 InProcServer32 键", ::GetLastError());
            ::RegCloseKey(classesKey);
            return false;
        }

        const DWORD pathBytes =
            static_cast<DWORD>((dllPath.size() + 1) * sizeof(wchar_t));

        ::RegSetValueExW(inProcKey, nullptr, 0, REG_SZ,
                         reinterpret_cast<const BYTE*>(dllPath.c_str()), pathBytes);

        // "Both" lets the media source be created from an STA or an MTA. FrameServer
        // creates it from a multi-threaded apartment, but the same DLL is also
        // loaded by this tool for diagnostics.
        const wchar_t threadingModel[] = L"Both";
        ::RegSetValueExW(inProcKey, L"ThreadingModel", 0, REG_SZ,
                         reinterpret_cast<const BYTE*>(threadingModel),
                         static_cast<DWORD>(sizeof(threadingModel)));

        ::RegCloseKey(inProcKey);

        Print(L"[完成] 已注册媒体源 COM 组件：%ls", kMediaSourceClsid);
        Print(L"        DLL 路径：%ls", dllPath.c_str());
    } else {
        // Delete the whole CLSID subtree. RegDeleteTree removes children first.
        const LSTATUS status = ::RegDeleteTreeW(classesKey, clsidKeyPath.c_str());
        if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND) {
            PrintError(L"删除 CLSID 键", status);
            ::RegCloseKey(classesKey);
            return false;
        }

        Print(L"[完成] 已注销媒体源 COM 组件：%ls", kMediaSourceClsid);
    }

    ::RegCloseKey(classesKey);
    return true;
}

// ---------------------------------------------------------------------- virtual camera

// Loads mfsensorgroup.dll and resolves MFCreateVirtualCamera. Returns nullptr on
// Windows 10, where the export does not exist.
MFCreateVirtualCameraFn ResolveMFCreateVirtualCamera(std::wstring* failureReason) {
    HMODULE module = ::LoadLibraryW(L"mfsensorgroup.dll");
    if (module == nullptr) {
        *failureReason =
            L"当前系统没有 mfsensorgroup.dll。MFCreateVirtualCamera 需要 Windows 11 "
            L"内部版本 22000 或更高版本。";
        return nullptr;
    }

    auto function = reinterpret_cast<MFCreateVirtualCameraFn>(
        ::GetProcAddress(module, "MFCreateVirtualCamera"));

    if (function == nullptr) {
        *failureReason =
            L"mfsensorgroup.dll 中没有 MFCreateVirtualCamera 导出。"
            L"该 API 需要 Windows 11 内部版本 22000 或更高版本。";
    }

    // The module is deliberately not freed: it stays loaded for the process
    // lifetime, which is correct because the function pointer stays in use.
    return function;
}

int CreateCamera(const std::wstring& friendlyName, bool systemLifetime, bool allUsers) {
    std::wstring reason;
    auto createVirtualCamera = ResolveMFCreateVirtualCamera(&reason);

    if (createVirtualCamera == nullptr) {
        Print(L"[错误] %ls", reason.c_str());
        return 3;
    }

    const HRESULT comResult = ::CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comResult) && comResult != RPC_E_CHANGED_MODE) {
        PrintHResult(L"CoInitializeEx", comResult);
        return 4;
    }

    IMFVirtualCamera* camera = nullptr;

    const HRESULT hr = createVirtualCamera(
        MFVirtualCameraType_SoftwareCameraSource,
        systemLifetime ? MFVirtualCameraLifetime_System : MFVirtualCameraLifetime_Session,
        allUsers ? MFVirtualCameraAccess_AllUsers : MFVirtualCameraAccess_CurrentUser,
        friendlyName.c_str(),
        kMediaSourceClsid,
        nullptr,  // default categories
        0,
        &camera);

    if (FAILED(hr)) {
        PrintHResult(L"MFCreateVirtualCamera", hr);

        if (hr == E_ACCESSDENIED) {
            Print(L"        可能原因：摄像头隐私设置被关闭，或创建 AllUsers 摄像头时未以管理员身份运行。");
            Print(L"        请检查「设置 → 隐私和安全性 → 相机」。");
        } else if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) {
            Print(L"        可能原因：媒体源 COM 组件尚未注册。请先运行 install。");
        }

        return 5;
    }

    // Start makes the camera enumerable. Without it the object exists but no
    // application can see the device, so a failure here means the camera was not
    // actually created even though MFCreateVirtualCamera returned success.
    const HRESULT startResult = camera->Start(nullptr);

    if (FAILED(startResult)) {
        PrintHResult(L"IMFVirtualCamera::Start", startResult);

        // Spell out the realistic causes. A bare exit code is not actionable, and
        // this is the step most likely to fail on a machine that is otherwise fine.
        Print(L"        摄像头未能注册到系统。常见原因：");
        Print(L"        1. 系统相机访问被关闭：设置 → 隐私和安全性 → 相机 → 允许应用访问你的相机。");
        Print(L"        2. FrameServer 服务未运行：以管理员身份运行 services.msc，启动「Windows Camera Frame Server」。");
        Print(L"        3. 该环境没有可用的视频设备（服务器核心安装、虚拟机或远程会话常见）。");
        Print(L"        4. 媒体源 DLL 依赖缺失：用 dumpbin /DEPENDENTS SeewoVirtualCamera.dll 检查。");
        Print(L"        COM 组件本身已注册成功，注册表状态不受影响；可在环境具备条件后单独运行 create。");

        camera->Release();
        return 6;
    }

    Print(L"[完成] 已创建虚拟摄像头「%ls」。", friendlyName.c_str());
    Print(L"       生命周期：%ls", systemLifetime ? L"系统（重启后仍存在）" : L"会话（本进程退出即消失）");
    Print(L"       访问范围：%ls", allUsers ? L"所有用户" : L"当前用户");
    Print(L"       现在可以在「设置 → 蓝牙和其他设备 → 摄像头」以及 Zoom / Teams / 微信中看到它。");

    // The camera stays alive because lifetime is System, or because the session
    // object is still referenced by the FrameServer registration until this process
    // exits. Releasing the interface here is correct: the registration persists.
    camera->Release();
    return 0;
}

int RemoveCamera(const std::wstring& friendlyName) {
    std::wstring reason;
    auto createVirtualCamera = ResolveMFCreateVirtualCamera(&reason);

    if (createVirtualCamera == nullptr) {
        Print(L"[错误] %ls", reason.c_str());
        return 3;
    }

    const HRESULT comResult = ::CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comResult) && comResult != RPC_E_CHANGED_MODE) {
        PrintHResult(L"CoInitializeEx", comResult);
        return 4;
    }

    // Opening the same parameters returns the existing camera, which can then be
    // removed. This is how the API is designed to work.
    IMFVirtualCamera* camera = nullptr;

    const HRESULT hr = createVirtualCamera(
        MFVirtualCameraType_SoftwareCameraSource,
        MFVirtualCameraLifetime_System,
        MFVirtualCameraAccess_CurrentUser,
        friendlyName.c_str(),
        kMediaSourceClsid,
        nullptr,
        0,
        &camera);

    if (FAILED(hr)) {
        PrintHResult(L"MFCreateVirtualCamera（查找已有摄像头）", hr);
        return 5;
    }

    const HRESULT removeResult = camera->Remove();
    camera->Release();

    if (FAILED(removeResult)) {
        PrintHResult(L"IMFVirtualCamera::Remove", removeResult);
        return 6;
    }

    Print(L"[完成] 已移除虚拟摄像头「%ls」。", friendlyName.c_str());
    return 0;
}

int ListCameras() {
    // Enumerating is done through the standard device enumeration APIs rather than
    // IMFVirtualCamera, because that is what a consuming application sees.
    IMFAttributes* attributes = nullptr;
    HRESULT hr = ::MFCreateAttributes(&attributes, 1);

    if (FAILED(hr)) {
        PrintHResult(L"MFCreateAttributes", hr);
        return 4;
    }

    attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);

    IMFActivate** devices = nullptr;
    UINT32 count = 0;

    hr = ::MFEnumDeviceSources(attributes, &devices, &count);
    attributes->Release();

    if (FAILED(hr)) {
        PrintHResult(L"MFEnumDeviceSources", hr);
        return 5;
    }

    Print(L"共发现 %u 个视频输入设备：", count);

    for (UINT32 i = 0; i < count; i++) {
        wchar_t* name = nullptr;
        UINT32 nameLength = 0;

        if (SUCCEEDED(devices[i]->GetAllocatedString(
                MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &nameLength))) {
            // The pipeline appends this suffix to virtual cameras, so it doubles as
            // a marker for which devices this app created.
            const bool isVirtual =
                name != nullptr && ::StrStrIW(name, L"Virtual Camera") != nullptr;

            Print(L"  [%u] %ls%ls", i + 1, name, isVirtual ? L"  ← 虚拟摄像头" : L"");
            ::CoTaskMemFree(name);
        }

        devices[i]->Release();
    }

    ::CoTaskMemFree(devices);
    return 0;
}

void PrintUsage() {
    Print(L"SeewoVirtualCamera.Setup — 虚拟摄像头注册与管理工具");
    Print(L"");
    Print(L"用法：");
    Print(L"  SeewoVirtualCamera.Setup.exe install     注册媒体源 COM 组件（需要管理员）");
    Print(L"  SeewoVirtualCamera.Setup.exe uninstall   注销组件并移除所有摄像头（需要管理员）");
    Print(L"  SeewoVirtualCamera.Setup.exe create [--name <名称>] [--session] [--all-users]");
    Print(L"  SeewoVirtualCamera.Setup.exe remove  [--name <名称>]");
    Print(L"  SeewoVirtualCamera.Setup.exe list        列出系统中的视频输入设备");
    Print(L"  SeewoVirtualCamera.Setup.exe check       检查本机是否支持虚拟摄像头 API");
    Print(L"  SeewoVirtualCamera.Setup.exe capture [--frames <数量>] [--out <PNG 路径>]");
    Print(L"                                           从虚拟摄像头真实读帧，验证它确实在输出画面");
    Print(L"                                           （默认读 60 帧；指定 --out 会把首帧存成 PNG）");
    Print(L"");
    Print(L"说明：");
    Print(L"  MFCreateVirtualCamera 需要 Windows 11 内部版本 22000 或更高版本。");
    Print(L"  Windows 10 请改用 DirectShow 版本（SeewoVirtualCamera.DShow.dll）。");
}

int CheckSupport() {
    // RtlGetVersion is used instead of GetVersionExW, which lies unless the
    // executable carries a compatibility manifest.
    using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);

    RTL_OSVERSIONINFOW version = {};
    version.dwOSVersionInfoSize = sizeof(version);

    HMODULE ntdll = ::GetModuleHandleW(L"ntdll.dll");
    if (ntdll != nullptr) {
        auto rtlGetVersion = reinterpret_cast<RtlGetVersionFn>(
            ::GetProcAddress(ntdll, "RtlGetVersion"));
        if (rtlGetVersion != nullptr) {
            rtlGetVersion(&version);
        }
    }

    Print(L"系统版本：%lu.%lu.%lu",
          version.dwMajorVersion, version.dwMinorVersion, version.dwBuildNumber);

    std::wstring reason;
    auto createVirtualCamera = ResolveMFCreateVirtualCamera(&reason);

    if (createVirtualCamera == nullptr) {
        Print(L"[结果] 不支持 Media Foundation 虚拟摄像头。");
        Print(L"       %ls", reason.c_str());
        Print(L"       可改用 DirectShow 虚拟摄像头（Windows 10 回退方案）。");
        return 1;
    }

    Print(L"[结果] 支持 MFCreateVirtualCamera，可以使用 Windows 11 原生虚拟摄像头。");
    return 0;
}

// ---------------------------------------------------------------------- arguments

struct Options {
    std::wstring Command;
    std::wstring Name = kDefaultFriendlyName;
    bool SessionLifetime = false;
    bool AllUsers = false;

    // Used by "capture".
    int FrameCount = 60;
    std::wstring OutputPath;
};

Options ParseCommandLine() {
    Options options;

    int argc = 0;
    wchar_t** argv = ::CommandLineToArgvW(::GetCommandLineW(), &argc);

    if (argv == nullptr) {
        return options;
    }

    std::vector<std::wstring> args;
    for (int i = 0; i < argc; i++) {
        args.emplace_back(argv[i]);
    }

    ::LocalFree(argv);

    // args[0] is the executable path.
    for (size_t i = 1; i < args.size(); i++) {
        const std::wstring& arg = args[i];

        if (arg == L"--name" && i + 1 < args.size()) {
            options.Name = args[++i];
        } else if (arg == L"--session") {
            options.SessionLifetime = true;
        } else if (arg == L"--all-users") {
            options.AllUsers = true;
        } else if (arg == L"--frames" && i + 1 < args.size()) {
            try {
                options.FrameCount = std::stoi(args[++i]);
            } catch (...) {
                // A bad value falls back to the default rather than failing the run;
                // the count is reported either way.
                options.FrameCount = 60;
            }
            if (options.FrameCount <= 0) {
                options.FrameCount = 1;
            }
        } else if (arg == L"--out" && i + 1 < args.size()) {
            options.OutputPath = args[++i];
        } else if (arg == L"--help" || arg == L"-h" || arg == L"/?") {
            options.Command = L"help";
        } else if (options.Command.empty()) {
            options.Command = arg;
        }
    }

    return options;
}

}  // namespace

int wmain() {
    // Every message is wide, and most of them are Chinese. Switching stdout to
    // Print() picks the right encoding for a console versus a redirected stream, so
    // the stream mode is deliberately left alone here. Unbuffered output is kept so
    // messages appear as they happen; buffering would otherwise make a slow install
    // look hung.
    ::setvbuf(stdout, nullptr, _IONBF, 0);

    const Options options = ParseCommandLine();

    if (options.Command.empty() || options.Command == L"help") {
        PrintUsage();
        return 0;
    }

    if (options.Command == L"check") {
        return CheckSupport();
    }

    if (options.Command == L"list") {
        return ListCameras();
    }

    if (options.Command == L"install") {
        const std::wstring directory = GetExecutableDirectory();
        const std::wstring dllPath = directory + L"\\" + kMediaSourceDllName;

        if (::GetFileAttributesW(dllPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
            Print(L"[错误] 找不到媒体源 DLL：%ls", dllPath.c_str());
            Print(L"       请确认 SeewoVirtualCamera.dll 与本工具在同一目录下。");
            return 2;
        }

        if (!RegisterComServer(dllPath, true)) {
            Print(L"       注册 COM 组件需要管理员权限，请以管理员身份运行。");
            return 1;
        }

        // Registering the server alone does not create a camera, so install does
        // both - that is what a user expects from "install".
        //
        // The camera is created with System lifetime, not Session lifetime. A
        // session-lifetime camera is torn down as soon as the object is released or
        // this process exits, which would make "install" appear to succeed and then
        // leave no camera behind at all. System lifetime is what persists across
        // reboots and what a "create" without --session also produces, so install and
        // create agree.
        //
        // Failure here is reported but does not undo the COM registration: the
        // server is registered and usable, and the camera can be created later with
        // "create". Returning non-zero still tells the caller something went wrong.
        const int createResult = CreateCamera(options.Name, /*systemLifetime=*/true, options.AllUsers);
        return createResult == 0 ? 0 : createResult;
    }

    if (options.Command == L"uninstall") {
        // Remove the camera before unregistering the server, otherwise the camera
        // would be left pointing at a CLSID that no longer resolves.
        RemoveCamera(options.Name);

        if (!RegisterComServer(L"", false)) {
            Print(L"       注销 COM 组件需要管理员权限，请以管理员身份运行。");
            return 1;
        }

        return 0;
    }

    if (options.Command == L"create") {
        return CreateCamera(options.Name, !options.SessionLifetime, options.AllUsers);
    }

    if (options.Command == L"remove") {
        return RemoveCamera(options.Name);
    }

    if (options.Command == L"capture") {
        std::wstring summary;
        std::wstring error;

        // An empty name selector means "the first virtual camera found", which is
        // what a caller wants when it does not care which one.
        const int result = CaptureFrames(
            options.Name, options.FrameCount, options.OutputPath, &summary, &error);

        // Print through the same helper as everything else so the encoding rules
        // are applied consistently.
        if (!summary.empty()) {
            Print(L"%ls", summary.c_str());
        }

        if (result != 0 && !error.empty()) {
            Print(L"[错误] %ls", error.c_str());
        }

        return result;
    }

    Print(L"[错误] 未知命令：%ls", options.Command.c_str());
    PrintUsage();
    return 2;
}
