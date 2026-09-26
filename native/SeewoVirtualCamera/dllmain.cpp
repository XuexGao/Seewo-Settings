// dllmain.cpp
//
// COM in-process server entry points for the Seewo virtual camera media source.
//
// This DLL deliberately does not create or register the camera: that is the job
// of the separate registration tool, which calls MFCreateVirtualCamera() (a
// Windows 11 22000+ API).  Keeping the two apart means this module can load on
// Windows 10 and contains no Windows 11-only import.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>

#include "SeewoWrl.h"

#include "SeewoVirtualCameraActivate.h"

// The module handle, kept for the lifetime of the DLL.  Some Media Foundation
// helpers (and diagnostic tooling) want it.
HINSTANCE g_hInst = nullptr;

namespace {

using Microsoft::WRL::InProc;
using Microsoft::WRL::Module;

// Ensures Media Foundation is started and the WRL module is created exactly once
// in this process.  Media Foundation is never shut down: the DLL can be loaded
// and unloaded repeatedly by the FrameServer, and calling MFShutdown() while
// another component in the same process still holds references would be
// harmful.  Returns a real failure HRESULT so DllGetClassObject can report it
// instead of handing out a broken class object.
//
// A function-local static initialised by an immediately-invoked lambda is used
// rather than InitOnceExecuteOnce: C++11 guarantees the initialisation runs
// exactly once and is thread-safe, and it avoids the PVOID-context casting that
// InitOnceExecuteOnce's callback signature forces.
HRESULT EnsureRuntimeInitialized() {
  static const HRESULT startupResult = []() -> HRESULT {
    // MFSTARTUP_LITE is enough: this DLL uses the core Media Foundation platform
    // (media types, samples, event queues, work queues) and does not need the
    // full pipeline, which would also spin up work queues of its own.
    const HRESULT hr = ::MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) {
      return hr;
    }

    // The WRL module singleton is created lazily by GetModule() and GetClassObject()
    // themselves, so there is nothing else to do here.  GetModule() returns a
    // module reference rather than an HRESULT, hence the deliberate discard.
    (void)Module<InProc>::GetModule();

    return S_OK;
  }();

  return startupResult;
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ulReasonForCall,
                      LPVOID /*lpReserved*/) {
  switch (ulReasonForCall) {
    case DLL_PROCESS_ATTACH:
      g_hInst = hModule;
      // Nothing heavy here: DllMain runs under the loader lock, so no COM, no
      // Media Foundation, no thread creation.
      ::DisableThreadLibraryCalls(hModule);
      break;

    case DLL_PROCESS_DETACH:
      g_hInst = nullptr;
      break;

    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    default:
      break;
  }
  return TRUE;
}

// ---------------------------------------------------------------------------
// DllGetClassObject
// ---------------------------------------------------------------------------
// The FrameServer asks for CLSID_SeewoVirtualCamera.  Any other CLSID falls
// through to WRL's module, which knows about classes declared with
// CoCreatableClass - none are, so it returns CLASS_E_CLASSNOTAVAILABLE.
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
  if (ppv == nullptr) {
    return E_POINTER;
  }
  *ppv = nullptr;

  const HRESULT hr = EnsureRuntimeInitialized();
  if (FAILED(hr)) {
    return hr;
  }

  if (IsEqualCLSID(rclsid, CLSID_SeewoVirtualCamera)) {
    Microsoft::WRL::ComPtr<seewo::SeewoVirtualCameraActivateFactory> factory =
        Microsoft::WRL::Make<seewo::SeewoVirtualCameraActivateFactory>();
    if (factory == nullptr) {
      return E_OUTOFMEMORY;
    }
    return factory->QueryInterface(riid, ppv);
  }

  return Module<InProc>::GetModule().GetClassObject(rclsid, riid, ppv);
}

// ---------------------------------------------------------------------------
// DllCanUnloadNow
// ---------------------------------------------------------------------------
// Safe to unload only when no object from this DLL is outstanding and no client
// has locked the server.  The private counter in SeewoVirtualCameraActivate.cpp
// covers both the objects and LockServer(), and WRL's module object count covers
// anything created through Module::CreateInstance.
STDAPI DllCanUnloadNow() {
  if (seewo::GetModuleObjectCount() > 0) {
    return S_FALSE;
  }
  if (Module<InProc>::GetModule().GetObjectCount() > 0) {
    return S_FALSE;
  }
  return S_OK;
}
