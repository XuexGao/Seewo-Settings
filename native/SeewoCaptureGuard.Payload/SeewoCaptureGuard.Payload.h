// SeewoCaptureGuard.Payload.h
//
// Private contract for the capture-guard injected payload.
//
// The payload speaks the request channel declared in ../SeewoCommon/SeewoIpc.h:
// a seewo::GuardRequest record at offset 0 of the section named
// seewo::kGuardSectionName (falling back to seewo::kGuardLocalSectionName), signalled
// through the auto-reset event seewo::kGuardRequestEventName.
//
// Everything this header adds is layered on top of that contract:
//
//   1. The exported entry point the injector starts with CreateRemoteThread.
//   2. An optional, payload-owned diagnostics block published at
//      kGuardStatusOffset. It exists so a debugger (or the injector) can tell *why*
//      a payload instance failed to come up, instead of only seeing that it did.
//
// The diagnostics block is strictly optional. seewo::GuardRequest::payloadPid remains
// the authoritative "a payload is attached to this process" signal, and a consumer
// that never looks at offset 64 still works. The injector never writes the block.
#pragma once

#include <windows.h>

#include <cstdint>

#include "SeewoIpc.h"

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------
//
// The injector locates this export by name in the freshly loaded module and passes
// its address to CreateRemoteThread, so the signature must stay exactly
// LPTHREAD_START_ROUTINE compatible.
//
// The export name is published through SeewoCaptureGuard.Payload.<arch>.def rather
// than __declspec(dllexport) alone, because on x86 the WINAPI (__stdcall) convention
// decorates the symbol to _SeewoCaptureGuardEntry@4 and only a .def can advertise the
// undecorated name GetProcAddress("SeewoCaptureGuardEntry") resolves. The x86 build
// keeps __declspec(dllexport) as well; the x64 build relies on the .def alone, since
// declaring both there would export the same name twice (linker warning LNK4197).
#if defined(_M_IX86)
#define SEEWO_CAPTUREGUARD_PAYLOAD_ENTRY_API __declspec(dllexport)
#else
#define SEEWO_CAPTUREGUARD_PAYLOAD_ENTRY_API
#endif

extern "C" SEEWO_CAPTUREGUARD_PAYLOAD_ENTRY_API DWORD WINAPI
SeewoCaptureGuardEntry(LPVOID reserved);

namespace seewo {

// ---------------------------------------------------------------------------
// Payload diagnostics block (optional, payload-owned, read-only for everyone else)
// ---------------------------------------------------------------------------
//
// Placed at offset 64: naturally 8-byte aligned for the 64-bit members below, and
// clear of GuardRequest, which is 56 bytes on both x86 and x64.
inline constexpr uint32_t kGuardStatusOffset = 64;

inline constexpr uint32_t kGuardStatusMagic = 0x53434753;  // 'SCGS'
inline constexpr uint32_t kGuardStatusVersion = 1;

enum class GuardPayloadState : uint32_t {
    // Nothing has published the block (the payload never attached, or it already
    // detached and the injector zeroed the section).
    Absent = 0,
    // The payload is attaching; the block may be partially filled.
    Initializing = 1,
    // The payload is attached and serving requests. This is the "ready" state.
    Running = 2,
    // The payload retired cleanly; exitCode says why.
    Stopped = 3,
    // The payload faulted and unwound through its SEH handler; exitCode is the
    // exception code or the failure that led there.
    Faulted = 4,
};

#pragma pack(push, 8)
struct GuardPayloadStatus {
    uint32_t magic;            // kGuardStatusMagic once the block is live
    uint32_t version;          // kGuardStatusVersion
    uint32_t payloadPid;       // mirrors GuardRequest::payloadPid
    uint32_t state;            // GuardPayloadState
    uint32_t exitCode;         // 0 while running, else the payload's final exit code
    uint32_t requestCount;     // requests handled since the payload attached
    uint32_t lastCommand;      // last GuardCommand handled
    uint32_t lastResult;       // result of the last request
    uint64_t attachedTickMs;   // GetTickCount64() when the payload came up
    uint64_t heartbeatTickMs;  // refreshed every self-check interval
    uint64_t reserved0;
};
#pragma pack(pop)

static_assert(sizeof(GuardPayloadStatus) == 56,
              "GuardPayloadStatus layout changed; bump kGuardStatusVersion");
static_assert(kGuardStatusOffset % 8 == 0,
              "the diagnostics block must stay 8-byte aligned");
static_assert(kGuardStatusOffset >= sizeof(GuardRequest),
              "the diagnostics block must not overlap GuardRequest");
static_assert(kGuardStatusOffset + sizeof(GuardPayloadStatus) <= kGuardSectionBytes,
              "kGuardSectionBytes is too small for the diagnostics block");

}  // namespace seewo
