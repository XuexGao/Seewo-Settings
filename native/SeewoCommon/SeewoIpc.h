// SeewoIpc.h
//
// Cross-process contracts shared by the managed SeewoAssistant app and the native
// components (virtual camera media source, capture-guard payload).
//
// Both channels use the same shape:
//   * a named shared memory section holding a small fixed header plus a payload
//   * named auto-reset events for signalling
//
// The Global\ namespace is preferred because the Media Foundation FrameServer runs
// in session 0 while the UI runs in the interactive session. Creating an object in
// Global\ requires SeCreateGlobalPrivilege, which services hold but plain
// interactive users do not, so every helper here falls back to Local\ and lets the
// caller retry. In practice whichever side can create the object does so, and the
// other side attaches.
#pragma once

#include <windows.h>
#include <cstdint>

namespace seewo {

// ---------------------------------------------------------------------------
// Virtual camera frame channel
// ---------------------------------------------------------------------------

// Bump the version suffix whenever a layout below changes so a stale producer
// from an older build can never be misread by a newer consumer.
// The namespace prefix is part of the name and must match the managed side exactly.
// A name without a prefix is created in the caller's session namespace, i.e. it is
// effectively "Local\", so omitting "Global\" here previously made the two sides
// create two different objects that could never see each other.
inline constexpr wchar_t kVcamSectionName[] = L"Global\\SeewoAssistant.VCam.Frame.v1";
inline constexpr wchar_t kVcamLocalSectionName[] = L"Local\\SeewoAssistant.VCam.Frame.v1";
inline constexpr wchar_t kVcamDataEventName[] = L"Global\\SeewoAssistant.VCam.DataReady.v1";
inline constexpr wchar_t kVcamLocalDataEventName[] = L"Local\\SeewoAssistant.VCam.DataReady.v1";
inline constexpr wchar_t kVcamRequestEventName[] = L"Global\\SeewoAssistant.VCam.SampleRequest.v1";
inline constexpr wchar_t kVcamLocalRequestEventName[] = L"Local\\SeewoAssistant.VCam.SampleRequest.v1";

inline constexpr uint32_t kVcamMagic = 0x53435631;  // 'SCV1'
inline constexpr uint32_t kVcamVersion = 1;

// Keep the negotiated frame within these bounds so the section size is fixed and
// both sides can map it without a round trip.
inline constexpr uint32_t kVcamMaxWidth = 1920;
inline constexpr uint32_t kVcamMaxHeight = 1080;
inline constexpr uint32_t kVcamBytesPerPixel = 4;  // RGB32 / BGRA
inline constexpr uint32_t kVcamMaxFrameBytes =
    kVcamMaxWidth * kVcamMaxHeight * kVcamBytesPerPixel;

// Byte offset of the pixel payload inside the section.
inline constexpr uint32_t kVcamPayloadOffset = 64;
inline constexpr uint32_t kVcamSectionBytes =
    kVcamPayloadOffset + kVcamMaxFrameBytes;

enum class VcamPixelFormat : uint32_t {
    Bgra32 = 0,  // B,G,R,A byte order, matches MFVideoFormat_ARGB32 memory layout
};

enum class VcamSourceState : uint32_t {
    // No producer has published anything yet; the media source synthesises its
    // built-in pattern so the camera still produces frames.
    Idle = 0,
    // A producer is attached and publishing frames.
    Live = 1,
};

#pragma pack(push, 8)
struct VcamFrameHeader {
    uint32_t magic;        // kVcamMagic
    uint32_t version;      // kVcamVersion
    uint32_t width;
    uint32_t height;
    uint32_t stride;       // bytes per row of the payload
    uint32_t format;       // VcamPixelFormat
    uint32_t frameIndex;   // monotonic, incremented by the producer
    uint32_t sourceState;  // VcamSourceState
    uint64_t timestampMs;  // producer clock at publish time
    uint32_t payloadBytes;
    uint32_t reserved0;
    uint64_t reserved1;
};
#pragma pack(pop)

static_assert(sizeof(VcamFrameHeader) <= kVcamPayloadOffset,
              "kVcamPayloadOffset must leave room for the frame header");

// ---------------------------------------------------------------------------
// Capture guard request channel
// ---------------------------------------------------------------------------

inline constexpr wchar_t kGuardSectionName[] = L"Global\\SeewoAssistant.CaptureGuard.v1";
inline constexpr wchar_t kGuardLocalSectionName[] =
    L"Local\\SeewoAssistant.CaptureGuard.v1";
inline constexpr wchar_t kGuardRequestEventName[] =
    L"Global\\SeewoAssistant.CaptureGuard.Request.v1";
inline constexpr wchar_t kGuardLocalRequestEventName[] =
    L"Local\\SeewoAssistant.CaptureGuard.Request.v1";

// The namespace the injector used, passed to the payload as its thread parameter so
// both sides attach to the same objects. Left to itself the payload probes Global
// first and, inside a target that holds SeCreateGlobalPrivilege, creates a second set
// of objects instead of opening the injector's - after which the two wait on different
// events and the injection appears to time out.
//
// Zero is deliberately unused: a NULL thread parameter means "probe for yourself",
// which is what an older injector passes, so a mismatched pair still works.
inline constexpr uintptr_t kGuardNamespaceGlobal = 1;
inline constexpr uintptr_t kGuardNamespaceLocal = 2;

inline constexpr uint32_t kGuardMagic = 0x53434731;  // 'SCG1'
inline constexpr uint32_t kGuardVersion = 1;
inline constexpr uint32_t kGuardSectionBytes = 4096;

enum class GuardCommand : uint32_t {
    None = 0,
    // Apply a display affinity to the window named in targetHwnd.
    SetAffinity = 1,
    // Write a liveness value into completed so the injector can tell the payload
    // loaded and is running.
    Ping = 2,
    // Ask the payload to release itself from the host process.
    Unload = 3,
};

#pragma pack(push, 8)
struct GuardRequest {
    uint32_t magic;        // kGuardMagic
    uint32_t version;      // kGuardVersion
    uint32_t command;      // GuardCommand
    uint32_t affinity;     // WDA_* value for SetAffinity
    uint64_t targetHwnd;   // HWND to operate on
    uint64_t requesterPid; // PID that asked for the operation
    uint32_t requestId;    // echoed back so the injector can match responses
    int32_t result;        // BOOL from the API call
    uint32_t lastError;    // GetLastError() when result is FALSE
    uint32_t completed;    // 0 = pending, 1 = the payload finished the request
    uint32_t payloadPid;   // PID the payload is running in
};
#pragma pack(pop)

static_assert(sizeof(GuardRequest) <= kGuardSectionBytes,
              "kGuardSectionBytes is too small for GuardRequest");

// ---------------------------------------------------------------------------
// Shared memory helpers
// ---------------------------------------------------------------------------

// Result of opening or creating a named channel.
struct ChannelHandle {
    HANDLE mapping = nullptr;
    void* view = nullptr;
    uint32_t bytes = 0;
    bool created = false;
    bool usedGlobalNamespace = false;

    bool valid() const { return view != nullptr; }
    explicit operator bool() const { return valid(); }

    void Close();
};

// Creates (or opens, if it already exists) a shared section with a DACL that lets
// services and other sessions attach. Tries the Global\ namespace first and falls
// back to Local\ when the caller lacks SeCreateGlobalPrivilege.
//
// Returns a valid ChannelHandle on success, otherwise an invalid one with
// GetLastError() set.
ChannelHandle CreateOrOpenSharedSection(const wchar_t* globalName,
                                       const wchar_t* localName,
                                       uint32_t bytes);

// Creates an auto-reset event that other sessions/services may open by name.
// Returns nullptr on failure.
HANDLE CreateOrOpenSharedEvent(const wchar_t* name);

// Builds the security descriptor string used for every shared object here:
// full access for Everyone, which covers interactive users, LocalSystem, and the
// service accounts that host FrameServer.
const wchar_t* SharedObjectSddl();

// Copies a string into a fixed buffer, always NUL terminating.
void CopyString(wchar_t* destination, size_t capacity, const wchar_t* source);

}  // namespace seewo
