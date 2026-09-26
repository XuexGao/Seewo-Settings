// dllmain.cpp
//
// DLL entry point, COM class factory plumbing, module lifetime tracking, and
// DirectShow filter registration for the Seewo Virtual Camera.
//
// Registration is what makes the filter visible as a camera. A DirectShow
// capture source must be registered in TWO places, and forgetting the second is
// the single most common reason a "virtual camera" never shows up:
//
//   1. HKCR\CLSID\{filter CLSID}          - the COM object itself, so
//      CoCreateInstance works.
//   2. HKCR\CLSID\{VideoInputDeviceCategory}\Instance\<Friendly Name> - the
//      device enumerator's list, which is what ICreateDevEnum walks when an app
//      asks for CLSID_VideoInputDeviceCategory. A filter that is only registered
//      in (1) is instantiable but invisible to every camera picker.
//
// HKCR is a merged view of HKCU\Software\Classes and HKLM\SOFTWARE\Classes. We
// write through HKCR because that is the documented registration location and it
// lands in HKLM for an elevated installer; the unregister path additionally
// cleans up any per-user keys so a machine-wide uninstall leaves nothing behind.
#include "SeewoDShowFilter.h"

#include <windows.h>
#include <objbase.h>
#include <shlwapi.h>
#include <strsafe.h>

#include <atomic>
#include <cwchar>
#include <new>
#include <string>

namespace seewo {
namespace dshow {

namespace {

std::atomic<LONG> g_moduleLocks{0};
std::atomic<LONG> g_objectCount{0};
HMODULE g_module = nullptr;

// CLSID_VideoInputDeviceCategory = {860BB310-5D01-11d0-BD3B-00A0C911CE86}
constexpr wchar_t kVideoInputDeviceCategoryGuid[] =
    L"{860BB310-5D01-11d0-BD3B-00A0C911CE86}";

// The two places a DirectShow capture source must appear so ICreateDevEnum finds
// it. HKCR is the documented, process-bitness-correct view; the explicit HKLM
// path makes the machine-wide registration unambiguous for installers and for
// anyone auditing the registry by hand.
constexpr wchar_t kCategoryRootHkcr[] = L"CLSID";
constexpr wchar_t kCategoryRootHklm[] = L"SOFTWARE\\Classes\\CLSID";

// ---------------------------------------------------------------------------
// Small registry helpers
// ---------------------------------------------------------------------------

// Creates (or opens) a key and optionally sets its default value.
HRESULT CreateKey(HKEY root, const wchar_t* path, REGSAM access, HKEY* key,
                  const wchar_t* defaultValue) {
    HKEY created = nullptr;
    const LONG result = ::RegCreateKeyExW(
        root, path, 0, nullptr, REG_OPTION_NON_VOLATILE, access, nullptr,
        &created, nullptr);
    if (result != ERROR_SUCCESS) {
        return HRESULT_FROM_WIN32(result);
    }

    if (defaultValue != nullptr) {
        const DWORD bytes =
            static_cast<DWORD>((wcslen(defaultValue) + 1) * sizeof(wchar_t));
        const LONG setResult = ::RegSetValueExW(
            created, nullptr, 0, REG_SZ,
            reinterpret_cast<const BYTE*>(defaultValue), bytes);
        if (setResult != ERROR_SUCCESS) {
            ::RegCloseKey(created);
            return HRESULT_FROM_WIN32(setResult);
        }
    }

    *key = created;
    return S_OK;
}

HRESULT SetStringValue(HKEY key, const wchar_t* name, const wchar_t* value) {
    const DWORD bytes =
        static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
    const LONG result = ::RegSetValueExW(
        key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value), bytes);
    return result == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(result);
}

// Recursively deletes a key. RegDeleteKeyExW with KEY_WOW64_* is not needed here
// because we register a 64-bit or 32-bit in-proc server from the matching
// process, and the registry view follows the process bitness automatically.
HRESULT DeleteKeyTree(HKEY root, const wchar_t* path) {
    HKEY key = nullptr;
    LONG result = ::RegOpenKeyExW(root, path, 0, KEY_READ | KEY_WRITE, &key);
    if (result != ERROR_SUCCESS) {
        return HRESULT_FROM_WIN32(result);
    }

    // Depth-first: enumerate children, recurse, then delete each one.
    for (;;) {
        wchar_t childName[512] = {};
        DWORD childLength = NUMELMS(childName);
        result = ::RegEnumKeyExW(key, 0, childName, &childLength, nullptr,
                                 nullptr, nullptr, nullptr);
        if (result != ERROR_SUCCESS) {
            break;
        }

        std::wstring childPath(path);
        childPath += L"\\";
        childPath += childName;
        DeleteKeyTree(root, childPath.c_str());
    }

    ::RegCloseKey(key);
    result = ::RegDeleteKeyExW(root, path, 0, 0);
    if (result != ERROR_SUCCESS) {
        return HRESULT_FROM_WIN32(result);
    }
    return S_OK;
}

// Turns a CLSID into the registry's {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx} form.
HRESULT ClsidToRegistryPath(const CLSID& clsid, wchar_t* buffer,
                            size_t capacity) {
    const HRESULT hr = ::StringFromGUID2(clsid, buffer,
                                         static_cast<int>(capacity));
    if (hr == 0) {
        return E_FAIL;
    }
    return S_OK;
}

}  // namespace

// ---------------------------------------------------------------------------
// Elevation check
// ---------------------------------------------------------------------------

// True when the current process token is elevated. Registration writes to
// HKLM\SOFTWARE\Classes, which a standard user cannot do, so this is checked
// before any registry access to return one clean E_ACCESSDENIED instead of a
// cascade of partial writes.
//
// This is a real token check rather than the shell's IsUserAnAdmin() helper:
// IsUserAnAdmin is deprecated, lives in shell32, and returns TRUE for a
// non-elevated administrator on a UAC-enabled machine -- which would let
// registration proceed and then fail halfway through.
bool IsProcessElevated() {
    HANDLE token = nullptr;
    if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token)) {
        return false;
    }

    TOKEN_ELEVATION elevation{};
    DWORD returned = 0;
    const BOOL ok = ::GetTokenInformation(token, TokenElevation, &elevation,
                                          sizeof(elevation), &returned);
    ::CloseHandle(token);

    if (!ok) {
        return false;
    }
    return elevation.TokenIsElevated != 0;
}

// ---------------------------------------------------------------------------
// Registration
// ---------------------------------------------------------------------------

HRESULT RegisterFilter() {
    wchar_t clsidPath[64] = {};
    HRESULT hr = ClsidToRegistryPath(CLSID_SeewoVirtualCamera, clsidPath,
                                     NUMELMS(clsidPath));
    if (FAILED(hr)) {
        return hr;
    }

    // 1. The COM in-proc server. The default value is the full DLL path, which
    //    is how COM finds us.
    {
        wchar_t inProcPath[256] = {};
        hr = StringCchPrintfW(inProcPath, NUMELMS(inProcPath),
                              L"CLSID\\%s\\InProcServer32", clsidPath);
        if (FAILED(hr)) {
            return hr;
        }

        wchar_t modulePath[MAX_PATH] = {};
        if (::GetModuleFileNameW(g_module, modulePath, NUMELMS(modulePath)) == 0) {
            return HRESULT_FROM_WIN32(::GetLastError());
        }

        HKEY key = nullptr;
        hr = CreateKey(HKEY_CLASSES_ROOT, inProcPath, KEY_WRITE, &key,
                       modulePath);
        if (FAILED(hr)) {
            return hr;
        }
        // Both: the filter is free-threaded with respect to its own state (the
        // pin serialises streaming), and Both lets an app create it from an MTA
        // without a proxy. This is what every modern capture source uses.
        hr = SetStringValue(key, L"ThreadingModel", L"Both");
        ::RegCloseKey(key);
        if (FAILED(hr)) {
            return hr;
        }
    }

    // 2. The friendly name for the COM object. Two spellings, because different
    //    consumers read different ones: the key's default value is what GraphEdit
    //    and property sheets show, and the named FriendlyName value is what
    //    several capture stacks read directly.
    {
        HKEY key = nullptr;
        hr = CreateKey(HKEY_CLASSES_ROOT, clsidPath, KEY_WRITE, &key,
                       kFriendlyName);
        if (FAILED(hr)) {
            return hr;
        }
        hr = SetStringValue(key, L"FriendlyName", kFriendlyName);
        ::RegCloseKey(key);
        if (FAILED(hr)) {
            return hr;
        }
    }

    // 3. The video-input-device category instance. This is the entry that makes
    //    the filter appear in Zoom, OBS, ffmpeg, and every other DirectShow
    //    capture app's device list -- ICreateDevEnum walks exactly this key.
    //
    //    It is written twice on purpose. HKCR is the documented, view-correct
    //    location (and an elevated process's HKCR writes land in
    //    HKLM\SOFTWARE\Classes), while the explicit HKLM path makes the
    //    machine-wide registration unambiguous for installers and for anyone
    //    auditing the registry by hand.
    struct CategoryRoot {
        HKEY root;
        const wchar_t* prefix;
        bool fatal;
    };

    const CategoryRoot categoryRoots[] = {
        {HKEY_CLASSES_ROOT, kCategoryRootHkcr, true},
        {HKEY_LOCAL_MACHINE, kCategoryRootHklm, false},
    };

    for (const CategoryRoot& entry : categoryRoots) {
        wchar_t instancePath[512] = {};
        hr = StringCchPrintfW(instancePath, NUMELMS(instancePath),
                              L"%s\\%s\\Instance\\%s", entry.prefix,
                              kVideoInputDeviceCategoryGuid, kFriendlyName);
        if (FAILED(hr)) {
            return hr;
        }

        HKEY key = nullptr;
        hr = CreateKey(entry.root, instancePath, KEY_WRITE, &key,
                       kFriendlyName);
        if (FAILED(hr)) {
            // The HKLM copy is a belt-and-braces duplicate; if HKCR already
            // succeeded, failing to write it again is not fatal.
            if (entry.fatal) {
                return hr;
            }
            continue;
        }
        hr = SetStringValue(key, L"CLSID", clsidPath);
        if (SUCCEEDED(hr)) {
            hr = SetStringValue(key, L"FriendlyName", kFriendlyName);
        }
        ::RegCloseKey(key);
        if (FAILED(hr) && entry.fatal) {
            return hr;
        }
    }

    // 4. The filter's own Instance key. Some enumerators look here for the
    //    device metadata before falling back to the category list.
    {
        wchar_t ownInstancePath[256] = {};
        hr = StringCchPrintfW(ownInstancePath, NUMELMS(ownInstancePath),
                              L"CLSID\\%s\\Instance\\%s", clsidPath,
                              kFriendlyName);
        if (SUCCEEDED(hr)) {
            HKEY key = nullptr;
            hr = CreateKey(HKEY_CLASSES_ROOT, ownInstancePath, KEY_WRITE, &key,
                           kFriendlyName);
            if (SUCCEEDED(hr)) {
                SetStringValue(key, L"CLSID", clsidPath);
                SetStringValue(key, L"FriendlyName", kFriendlyName);
                ::RegCloseKey(key);
            }
        }
    }

    return S_OK;
}

HRESULT UnregisterFilter() {
    wchar_t clsidPath[64] = {};
    HRESULT hr = ClsidToRegistryPath(CLSID_SeewoVirtualCamera, clsidPath,
                                     NUMELMS(clsidPath));
    if (FAILED(hr)) {
        return hr;
    }

    // Remove the category instances first. If these fail we still try to remove
    // the COM registration: leaving a device entry pointing at a missing DLL is
    // worse than leaving a stray CLSID key.
    const struct {
        HKEY root;
        const wchar_t* prefix;
    } categoryRoots[] = {
        {HKEY_CLASSES_ROOT, kCategoryRootHkcr},
        {HKEY_LOCAL_MACHINE, kCategoryRootHklm},
    };

    for (const auto& entry : categoryRoots) {
        wchar_t instancePath[512] = {};
        if (SUCCEEDED(StringCchPrintfW(instancePath, NUMELMS(instancePath),
                                       L"%s\\%s\\Instance\\%s", entry.prefix,
                                       kVideoInputDeviceCategoryGuid,
                                       kFriendlyName))) {
            DeleteKeyTree(entry.root, instancePath);
        }
    }

    // Build "CLSID\{...}" into a separate buffer. Passing the same buffer as
    // both the format argument and the destination is undefined behaviour, and
    // this is exactly the kind of overlap that corrupts the path on some
    // StringCchPrintf implementations.
    wchar_t fullClsidPath[128] = {};
    if (SUCCEEDED(StringCchPrintfW(fullClsidPath, NUMELMS(fullClsidPath),
                                   L"CLSID\\%s", clsidPath))) {
        DeleteKeyTree(HKEY_CLASSES_ROOT, fullClsidPath);
    }

    return S_OK;
}

}  // namespace dshow
}  // namespace seewo

// The exports below live inside the namespace so they can reach the module
// counters and the registration helpers; the .def file exports them by their
// undecorated names.
namespace seewo {
namespace dshow {

// ---------------------------------------------------------------------------
// Module lifetime
// ---------------------------------------------------------------------------

void LockModule() {
    g_moduleLocks.fetch_add(1);
}

void UnlockModule() {
    g_moduleLocks.fetch_sub(1);
}

bool ModuleCanUnload() {
    return g_moduleLocks.load() == 0 && g_objectCount.load() == 0;
}

void AddRefObject() {
    g_objectCount.fetch_add(1);
}

void ReleaseObject() {
    g_objectCount.fetch_sub(1);
}

// ---------------------------------------------------------------------------
// Exported entry points
// ---------------------------------------------------------------------------

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    *ppv = nullptr;

    if (clsid != CLSID_SeewoVirtualCamera) {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) SeewoClassFactory();
    if (factory == nullptr) {
        return E_OUTOFMEMORY;
    }

    const HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow() {
    // S_FALSE keeps the DLL loaded. Returning S_OK while a pin's streaming
    // thread is still unwinding would unmap the code it is executing.
    return ModuleCanUnload() ? S_OK : S_FALSE;
}

STDAPI DllRegisterServer() {
    // Fail fast and cleanly when not elevated, before touching the registry, so
    // a per-user install gets one clear error instead of a half-written set of
    // keys that makes the device appear but not work.
    if (!IsProcessElevated()) {
        return E_ACCESSDENIED;
    }

    const HRESULT hr = RegisterFilter();
    if (FAILED(hr)) {
        // Roll back so a partially registered filter never lingers.
        UnregisterFilter();
    }
    return hr;
}

STDAPI DllUnregisterServer() {
    if (!IsProcessElevated()) {
        return E_ACCESSDENIED;
    }
    return UnregisterFilter();
}

}  // namespace dshow
}  // namespace seewo

// ---------------------------------------------------------------------------
// DllMain
// ---------------------------------------------------------------------------

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)reserved;

    switch (reason) {
        case DLL_PROCESS_ATTACH:
            seewo::dshow::g_module = instance;
            // We do not need per-thread notifications, and disabling them avoids
            // a loader-lock callback on every thread creation.
            ::DisableThreadLibraryCalls(instance);
            break;

        case DLL_PROCESS_DETACH:
            // Deliberately empty. Doing work here (freeing cached objects,
            // stopping threads) during process teardown is a classic source of
            // hangs: the loader lock is held and other threads are already gone.
            // The filter's own destructor handles everything while the process
            // is still healthy.
            break;

        default:
            break;
    }

    return TRUE;
}
