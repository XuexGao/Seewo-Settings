// SeewoDShowFilter.h
//
// The Seewo Virtual Camera DirectShow capture source filter.
//
// The filter is intentionally tiny: it owns exactly one output pin and delegates
// every streaming decision to that pin. Its job is to satisfy the graph builder:
//
//   * IBaseFilter        - enumerate the one pin, expose the filter's clock
//   * IMediaFilter       - Stop / Pause / Run drive the pin's streaming session
//   * IAMFilterMiscFlags - report AM_FILTER_MISC_FLAGS_IS_SOURCE so capture apps
//                          know this is a live source and not a file reader
//   * ISpecifyPropertyPages - return an empty page list (no UI)
//   * IPersist           - the mandatory filter identity
//
// IBaseFilter already derives from IMediaFilter, which derives from IPersist, so
// those three are listed as base classes only once, through IBaseFilter. Naming
// IPersist again would create a second, ambiguous IPersist subobject.
//
// As with the pin, this does NOT derive from Microsoft's CBaseFilter. See
// README.md.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <objbase.h>
#include <ole2.h>
#include <ocidl.h>
#include <dshow.h>
#include <strmif.h>
#include <uuids.h>

#include <atomic>

#include "SeewoOutputPin.h"

namespace seewo {
namespace dshow {

// {B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}
extern const CLSID CLSID_SeewoVirtualCamera;

// The registry-friendly name. Used for the CLSID FriendlyName value and the
// video-input-category instance key.
extern const wchar_t kFriendlyName[];

// CLSID_VideoInputDeviceCategory = {860BB310-5D01-11d0-BD3B-00A0C911CE86}
extern const CLSID CLSID_VideoInputDeviceCategory_Seewo;

class SeewoDShowFilter final : public IBaseFilter,
                               public IAMFilterMiscFlags,
                               public ISpecifyPropertyPages {
public:
    SeewoDShowFilter();
    ~SeewoDShowFilter();

    // --- IUnknown -------------------------------------------------------------
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;

    // --- IPersist -------------------------------------------------------------
    STDMETHOD(GetClassID)(CLSID* clsid) override;

    // --- IMediaFilter ---------------------------------------------------------
    STDMETHOD(Stop)() override;
    STDMETHOD(Pause)() override;
    STDMETHOD(Run)(REFERENCE_TIME start) override;
    STDMETHOD(GetState)(DWORD milliseconds, FILTER_STATE* state) override;
    STDMETHOD(SetSyncSource)(IReferenceClock* clock) override;
    STDMETHOD(GetSyncSource)(IReferenceClock** clock) override;

    // --- IBaseFilter ----------------------------------------------------------
    STDMETHOD(EnumPins)(IEnumPins** enumerator) override;
    STDMETHOD(FindPin)(LPCWSTR id, IPin** pin) override;
    STDMETHOD(QueryFilterInfo)(FILTER_INFO* info) override;
    STDMETHOD(JoinFilterGraph)(IFilterGraph* graph, LPCWSTR name) override;
    STDMETHOD(QueryVendorInfo)(LPWSTR* vendorInfo) override;

    // --- IAMFilterMiscFlags ---------------------------------------------------
    STDMETHOD_(ULONG, GetMiscFlags)() override;

    // --- ISpecifyPropertyPages ------------------------------------------------
    STDMETHOD(GetPages)(CAUUID* pages) override;

    // --- Diagnostics ----------------------------------------------------------
    FILTER_STATE State() const {
        return state_.load(std::memory_order_acquire);
    }

private:
    std::atomic<LONG> refCount_{1};
    mutable CRITICAL_SECTION lock_{};

    SeewoOutputPin* pin_ = nullptr;  // add-ref'd, owns the filter's lifetime
    std::atomic<FILTER_STATE> state_{State_Stopped};

    IFilterGraph* graph_ = nullptr;  // add-ref'd
    WCHAR name_[128] = {};
};

// ---------------------------------------------------------------------------
// Class factory
// ---------------------------------------------------------------------------

class SeewoClassFactory final : public IClassFactory {
public:
    SeewoClassFactory();
    ~SeewoClassFactory();

    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;
    STDMETHOD(CreateInstance)(IUnknown* outer, REFIID riid, void** ppv) override;
    STDMETHOD(LockServer)(BOOL lock) override;

private:
    std::atomic<LONG> refCount_{1};
};

// ---------------------------------------------------------------------------
// DLL lifetime tracking (defined in dllmain.cpp)
// ---------------------------------------------------------------------------

void LockModule();
void UnlockModule();
bool ModuleCanUnload();

// Object census. DllCanUnloadNow must not return S_OK while a live filter
// instance exists, even if no class factory or server lock is outstanding:
// unloading the DLL would unmap the code of an object the graph still holds.
void AddRefObject();
void ReleaseObject();

// Declared here so dllmain.cpp can define them inside the namespace while the
// exported entry points stay outside it.
bool IsProcessElevated();
HRESULT RegisterFilter();
HRESULT UnregisterFilter();

}  // namespace dshow
}  // namespace seewo
