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
#include <wrl.h>
#include <wrl/module.h>

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
HRESULT EnsureRuntimeInitialized() {
  static INIT_ONCE once = INIT_ONCE_STATIC_INIT;
  static HRESULT startupResult = E_PENDING;

  struct Starter {
    static BOOL CALLBACK Callback(PINIT_ONCE, PVOID, PVOID* context) {
      HRESULT* result = static_cast<HRESULT*>(context);
      // MFSTARTUP_LITE is enough: this DLL uses the core Media Foundation
      // platform (media types, samples, event queues, work queues) and does not
      // need the full pipeline, which would also spin up work queues of its own.
      *result = ::MFStartup(MF_VERSION, MFSTARTUP_LITE);
      if (SUCCEEDED(*result)) {
        // WRL's module owns the class-object bookkeeping and the object count
        // consulted by DllCanUnloadNow.  Create() is idempotent.
        const HRESULT createResult = Module<InProc>::Create();
        if (FAILED(createResult)) {
          *result = createResult;
        }
      }
      return TRUE;
    }
  };

  ::InitOnceExecuteOnce(&once, Starter::Callback, &startupResult, nullptr);
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
