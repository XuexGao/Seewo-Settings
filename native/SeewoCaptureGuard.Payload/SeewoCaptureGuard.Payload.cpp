// SeewoCaptureGuard.Payload
//
// Injected payload that applies SetWindowDisplayAffinity on behalf of the target
// process, because the kernel refuses cross-process calls to that API.
//
// Design notes:
//   * DllMain does essentially nothing. All real work happens in
//     SeewoCaptureGuardEntry, which the injector starts on a remote thread, i.e.
//     outside the loader lock. Touching the loader lock inside DllMain would risk
//     deadlocking the host process.
//   * The payload self-unloads with FreeLibraryAndExitThread so the host process is
//     left exactly as it was found.
//   * Compiled with the static CRT so it can be injected into processes that do not
//     have the VC++ runtime installed.
//   * No file, registry or network access of any kind. This DLL is deliberately
//     boring so it does not look like anything other than what it is.
//
// SEH note: MSVC rejects __try in a function that requires C++ object unwinding
// (C2712). Every object with a destructor therefore lives in RunPayloadLoop(), and
// SeewoCaptureGuardEntry does nothing but guard the call to it.

#include <windows.h>
#include <cstdint>

#include "SeewoIpc.h"

namespace {

// How long to wait for the shared section to appear before giving up. The injector
// creates it before starting the remote thread, so this only covers a race.
constexpr DWORD kChannelWaitMs = 5000;

// Upper bound on the payload's lifetime. A payload that is never told to unload
// must not linger forever in a third-party process.
constexpr ULONGLONG kMaxLifetimeMs = 10ULL * 60ULL * 1000ULL;

// How long to block on the request event before re-checking the lifetime budget.
constexpr DWORD kRequestWaitMs = 1000;

HMODULE g_module = nullptr;

// Set once the loop is running so a second entry call is a no-op instead of
// starting a duplicate loop.
volatile LONG g_running = 0;

// The signature of SetWindowDisplayAffinity, resolved dynamically so the payload
// works even on a system whose user32 does not export it.
using SetWindowDisplayAffinityFn = BOOL(WINAPI*)(HWND, DWORD);

SetWindowDisplayAffinityFn ResolveSetWindowDisplayAffinity() {
    HMODULE user32 = ::GetModuleHandleW(L"user32.dll");
    if (user32 == nullptr) {
        user32 = ::LoadLibraryW(L"user32.dll");
    }
    if (user32 == nullptr) {
        return nullptr;
    }
    return reinterpret_cast<SetWindowDisplayAffinityFn>(
        ::GetProcAddress(user32, "SetWindowDisplayAffinity"));
}

// Publishes a completion result. `completed` is written last so the injector never
// observes a result before it is final.
void CompleteRequest(volatile seewo::GuardRequest* request,
                     int32_t result,
                     uint32_t lastError) {
    request->result = result;
    request->lastError = lastError;
    ::MemoryBarrier();
    ::InterlockedExchange(
        reinterpret_cast<volatile LONG*>(&request->completed), 1);
}

// Attaches to the request channel. Returns false when it never appeared.
bool AttachToChannel(seewo::ChannelHandle* channel, HANDLE* requestEvent) {
    const ULONGLONG deadline = ::GetTickCount64() + kChannelWaitMs;

    for (;;) {
        *channel = seewo::CreateOrOpenSharedSection(
            seewo::kGuardSectionName, seewo::kGuardLocalSectionName,
            seewo::kGuardSectionBytes);
        if (channel->valid()) {
            break;
        }
        if (::GetTickCount64() >= deadline) {
            return false;
        }
        ::Sleep(100);
    }

    *requestEvent = seewo::CreateOrOpenSharedEvent(seewo::kGuardRequestEventName);
    return *requestEvent != nullptr;
}

// The real payload body. Contains the only objects with destructors in this file.
DWORD RunPayloadLoop() {
    seewo::ChannelHandle channel;
    HANDLE requestEvent = nullptr;

    if (!AttachToChannel(&channel, &requestEvent)) {
        channel.Close();
        return 2;
    }

    auto* request = static_cast<volatile seewo::GuardRequest*>(channel.view);

    // Advertise that the payload is alive and where it is running. The injector
    // polls this to distinguish "loaded and waiting" from "LoadLibrary succeeded
    // but the entry point never ran".
    ::InterlockedExchange(
        reinterpret_cast<volatile LONG*>(&request->payloadPid),
        static_cast<LONG>(::GetCurrentProcessId()));

    const SetWindowDisplayAffinityFn setAffinity = ResolveSetWindowDisplayAffinity();

    const ULONGLONG lifetimeDeadline = ::GetTickCount64() + kMaxLifetimeMs;
    bool unloadRequested = false;

    while (!unloadRequested && ::GetTickCount64() < lifetimeDeadline) {
        const DWORD wait = ::WaitForSingleObject(requestEvent, kRequestWaitMs);
        if (wait != WAIT_OBJECT_0) {
            continue;  // Timeout: re-check the lifetime budget.
        }

        if (request->magic != seewo::kGuardMagic ||
            request->version != seewo::kGuardVersion) {
            continue;
        }

        const uint32_t command = request->command;

        if (command == static_cast<uint32_t>(seewo::GuardCommand::Ping)) {
            CompleteRequest(request, TRUE, 0);
            continue;
        }

        if (command == static_cast<uint32_t>(seewo::GuardCommand::Unload)) {
            CompleteRequest(request, TRUE, 0);
            unloadRequested = true;
            continue;
        }

        if (command == static_cast<uint32_t>(seewo::GuardCommand::SetAffinity)) {
            if (setAffinity == nullptr) {
                CompleteRequest(request, FALSE, ERROR_CALL_NOT_IMPLEMENTED);
                continue;
            }

            const HWND target =
                reinterpret_cast<HWND>(static_cast<uintptr_t>(request->targetHwnd));

            // A window that has gone away is not worth crashing over; report it the
            // same way the API itself would.
            if (target == nullptr || !::IsWindow(target)) {
                CompleteRequest(request, FALSE, ERROR_INVALID_WINDOW_HANDLE);
                continue;
            }

            ::SetLastError(ERROR_SUCCESS);
            const BOOL ok = setAffinity(target, request->affinity);
            const uint32_t error =
                ok ? 0u : static_cast<uint32_t>(::GetLastError());
            CompleteRequest(request, ok, error);
            continue;
        }

        // GuardCommand::None and anything unknown: acknowledge so the injector is
        // never left waiting for a response that will not come.
        CompleteRequest(request, FALSE, ERROR_INVALID_PARAMETER);
    }

    ::CloseHandle(requestEvent);
    channel.Close();
    return 0;
}

}  // namespace

extern "C" __declspec(dllexport) DWORD WINAPI SeewoCaptureGuardEntry(LPVOID reserved) {
    (void)reserved;

    // Only one loop per process. If the injector somehow calls in twice, the second
    // caller leaves immediately.
    if (::InterlockedCompareExchange(&g_running, 1, 0) != 0) {
        return 1;
    }

    DWORD exitCode = 0;

    // This function deliberately declares no object with a destructor so SEH is
    // legal here. Nothing in the payload may take the host process down, so every
    // unexpected fault is converted into an exit code.
    __try {
        exitCode = RunPayloadLoop();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        exitCode = 4;
    }

    // Self-unload. FreeLibraryAndExitThread never returns, which is exactly what is
    // wanted: the thread disappears along with the module.
    if (g_module != nullptr) {
        ::FreeLibraryAndExitThread(g_module, exitCode);
    }

    return exitCode;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved) {
    (void)reserved;

    switch (reason) {
        case DLL_PROCESS_ATTACH:
            g_module = module;
            // No thread attach/detach notifications: the payload creates no threads
            // and does not need them.
            ::DisableThreadLibraryCalls(module);
            break;

        case DLL_PROCESS_DETACH:
            // Only reached when the host is exiting or the module is force-unloaded.
            g_module = nullptr;
            break;

        default:
            break;
    }

    return TRUE;
}
