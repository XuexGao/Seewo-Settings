#include "pch.h"
#include "SeewoIpc.h"

namespace seewo {

namespace {

// SECURITY_DESCRIPTOR built from SDDL:
//   D:              DACL
//   (A;;GA;;;WD)    allow generic-all to Everyone (interactive users + services)
//   (A;;GA;;;SY)    allow generic-all to LocalSystem
//   (A;;GA;;;BA)    allow generic-all to Built-in Administrators
// Everyone already covers the other two, but listing them keeps the intent clear
// and survives a future tightening of the Everyone ACE.
constexpr wchar_t kSharedSddl[] = L"D:(A;;GA;;;WD)(A;;GA;;;SY)(A;;GA;;;BA)";

// Applies a permissive DACL to a freshly created kernel object. Returns false and
// sets the last error when the security descriptor cannot be applied.
bool ApplySharedDacl(HANDLE object) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!::ConvertStringSecurityDescriptorToSecurityDescriptorW(
            kSharedSddl, SDDL_REVISION_1, &descriptor, nullptr)) {
        return false;
    }

    SECURITY_ATTRIBUTES attributes{};
    attributes.nLength = sizeof(attributes);
    attributes.lpSecurityDescriptor = descriptor;
    attributes.bInheritHandle = FALSE;

    const BOOL ok = ::SetKernelObjectSecurity(
        object, DACL_SECURITY_INFORMATION, descriptor);

    ::LocalFree(descriptor);
    return ok != FALSE;
}

// Creates the section body. If `mustCreate` is false and the name already exists,
// opens it instead.
ChannelHandle OpenOrCreate(const wchar_t* name, uint32_t bytes, bool global) {
    ChannelHandle channel;
    channel.bytes = bytes;
    channel.usedGlobalNamespace = global;

    SECURITY_ATTRIBUTES attributes{};
    attributes.nLength = sizeof(attributes);
    attributes.bInheritHandle = FALSE;

    // ConvertStringSecurityDescriptorToSecurityDescriptorW needs the string form,
    // and CreateFileMappingW accepts an SDDL-backed SECURITY_ATTRIBUTES directly
    // only through a descriptor, so build one up front.
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (::ConvertStringSecurityDescriptorToSecurityDescriptorW(
            kSharedSddl, SDDL_REVISION_1, &descriptor, nullptr)) {
        attributes.lpSecurityDescriptor = descriptor;
    }

    ::SetLastError(ERROR_SUCCESS);
    HANDLE mapping = ::CreateFileMappingW(
        INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, bytes, name);

    if (mapping == nullptr) {
        if (descriptor != nullptr) {
            ::LocalFree(descriptor);
        }
        return channel;
    }

    channel.created = (::GetLastError() != ERROR_ALREADY_EXISTS);

    if (descriptor != nullptr) {
        ::LocalFree(descriptor);
    }

    // A section created by another account may have a DACL that excludes us. Try
    // to widen it; ignore failure because an existing mapping we can already open
    // is still usable.
    if (!channel.created) {
        ApplySharedDacl(mapping);
    }

    void* view = ::MapViewOfFile(
        mapping, FILE_MAP_ALL_ACCESS, 0, 0, static_cast<SIZE_T>(bytes));
    if (view == nullptr) {
        // Fall back to read-only mapping so a consumer with a restrictive DACL can
        // at least observe frames.
        view = ::MapViewOfFile(
            mapping, FILE_MAP_READ, 0, 0, static_cast<SIZE_T>(bytes));
    }

    if (view == nullptr) {
        ::CloseHandle(mapping);
        return channel;
    }

    channel.mapping = mapping;
    channel.view = view;
    return channel;
}

}  // namespace

void ChannelHandle::Close() {
    if (view != nullptr) {
        ::UnmapViewOfFile(view);
        view = nullptr;
    }
    if (mapping != nullptr) {
        ::CloseHandle(mapping);
        mapping = nullptr;
    }
    bytes = 0;
}

ChannelHandle CreateOrOpenSharedSection(const wchar_t* globalName,
                                       const wchar_t* localName,
                                       uint32_t bytes) {
    // Try the machine-wide namespace first so a session 0 service and an
    // interactive process can meet. SeCreateGlobalPrivilege is required to create
    // objects there; if it is missing, fall back to the per-session namespace.
    if (globalName != nullptr) {
        ChannelHandle channel = OpenOrCreate(globalName, bytes, true);
        if (channel.valid()) {
            return channel;
        }
    }

    if (localName != nullptr) {
        return OpenOrCreate(localName, bytes, false);
    }

    return ChannelHandle{};
}

HANDLE CreateOrOpenSharedEvent(const wchar_t* name) {
    if (name == nullptr) {
        ::SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }

    PSECURITY_DESCRIPTOR descriptor = nullptr;
    SECURITY_ATTRIBUTES attributes{};
    attributes.nLength = sizeof(attributes);
    attributes.bInheritHandle = FALSE;
    if (::ConvertStringSecurityDescriptorToSecurityDescriptorW(
            kSharedSddl, SDDL_REVISION_1, &descriptor, nullptr)) {
        attributes.lpSecurityDescriptor = descriptor;
    }

    HANDLE event = ::CreateEventW(&attributes, FALSE, FALSE, name);

    if (descriptor != nullptr) {
        ::LocalFree(descriptor);
    }
    return event;
}

const wchar_t* SharedObjectSddl() {
    return kSharedSddl;
}

void CopyString(wchar_t* destination, size_t capacity, const wchar_t* source) {
    if (destination == nullptr || capacity == 0) {
        return;
    }
    if (source == nullptr) {
        destination[0] = L'\0';
        return;
    }
    ::StringCchCopyW(destination, capacity, source);
}

}  // namespace seewo
