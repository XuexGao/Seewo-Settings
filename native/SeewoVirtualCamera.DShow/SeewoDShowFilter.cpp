// SeewoDShowFilter.cpp
//
// The Seewo Virtual Camera filter object, its class factory, and the one-element
// IEnumPins implementation.
//
// Everything here is deliberately thin. The filter exists so the graph builder
// has something to instantiate and register; the interesting work (format
// negotiation, allocation, frame pacing) lives in SeewoOutputPin.
//
// Reference counting note, because this is where source filters usually break:
// the filter holds exactly one reference on its pin, and the pin holds a raw
// back-pointer to the filter for PIN_INFO and IQualityControl::Notify. That
// direction is safe because the pin cannot outlive the filter's reference to it.
// The pin never AddRefs the filter, which is what prevents the classic
// filter<->pin reference cycle that leaks a whole graph.
#include "SeewoDShowFilter.h"

#include <new>
#include <cstring>
#include <strsafe.h>

namespace seewo {
namespace dshow {

// {B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}
const CLSID CLSID_SeewoVirtualCamera = {
    0xB8F5C3D2,
    0x6E4A,
    0x4B99,
    {0x8C, 0x7F, 0x2D, 0x3E, 0x4F, 0x5A, 0x6B, 0x71}};

// {860BB310-5D01-11d0-BD3B-00A0C911CE86}
const CLSID CLSID_VideoInputDeviceCategory_Seewo = {
    0x860BB310,
    0x5D01,
    0x11D0,
    {0xBD, 0x3B, 0x00, 0xA0, 0xC9, 0x11, 0xCE, 0x86}};

const wchar_t kFriendlyName[] = L"Seewo Virtual Camera";

namespace {

// The pin's stable identifier. Capture apps that look up a device by pin name
// (rather than by filter) use this string, so it must not change between builds.
constexpr wchar_t kPinId[] = L"Capture";

// ---------------------------------------------------------------------------
// IEnumPins over the filter's single output pin
// ---------------------------------------------------------------------------

class PinEnumerator final : public IEnumPins {
public:
    explicit PinEnumerator(SeewoOutputPin* pin) : pin_(pin) {
        if (pin_ != nullptr) {
            pin_->AddRef();
        }
    }

    ~PinEnumerator() {
        if (pin_ != nullptr) {
            pin_->Release();
        }
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (ppv == nullptr) {
            return E_POINTER;
        }
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IEnumPins) {
            *ppv = static_cast<IEnumPins*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override {
        return static_cast<ULONG>(refCount_.fetch_add(1) + 1);
    }

    STDMETHODIMP_(ULONG) Release() override {
        const LONG remaining = refCount_.fetch_sub(1) - 1;
        if (remaining == 0) {
            delete this;
        }
        return static_cast<ULONG>(remaining);
    }

    STDMETHODIMP Next(ULONG count, IPin** pins, ULONG* fetched) override {
        if (pins == nullptr) {
            return E_POINTER;
        }
        if (count > 1 && fetched == nullptr) {
            return E_POINTER;
        }

        ULONG produced = 0;
        // The filter has exactly one pin, so at most one entry is ever produced.
        if (count > 0 && position_ == 0 && pin_ != nullptr) {
            pin_->AddRef();
            pins[0] = pin_;
            produced = 1;
            position_ = 1;
        }

        if (fetched != nullptr) {
            *fetched = produced;
        }
        return produced == count ? S_OK : S_FALSE;
    }

    STDMETHODIMP Skip(ULONG count) override {
        position_ += static_cast<int>(count);
        if (position_ > 1) {
            position_ = 1;
            return S_FALSE;
        }
        return S_OK;
    }

    STDMETHODIMP Reset() override {
        position_ = 0;
        return S_OK;
    }

    STDMETHODIMP Clone(IEnumPins** enumerator) override {
        if (enumerator == nullptr) {
            return E_POINTER;
        }
        *enumerator = nullptr;

        auto* clone = new (std::nothrow) PinEnumerator(pin_);
        if (clone == nullptr) {
            return E_OUTOFMEMORY;
        }
        clone->position_ = position_;
        *enumerator = static_cast<IEnumPins*>(clone);
        return S_OK;
    }

private:
    std::atomic<LONG> refCount_{1};
    SeewoOutputPin* pin_ = nullptr;
    int position_ = 0;
};

// A CoTaskMem copy of a wide string, for the LPWSTR out-parameters.
HRESULT CopyToTaskMem(const wchar_t* source, LPWSTR* destination) {
    if (destination == nullptr) {
        return E_POINTER;
    }
    *destination = nullptr;
    if (source == nullptr) {
        return S_OK;
    }

    const size_t bytes = (wcslen(source) + 1) * sizeof(wchar_t);
    auto* buffer = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
    if (buffer == nullptr) {
        return E_OUTOFMEMORY;
    }
    std::memcpy(buffer, source, bytes);
    *destination = buffer;
    return S_OK;
}

}  // namespace

// ---------------------------------------------------------------------------
// SeewoDShowFilter
// ---------------------------------------------------------------------------

SeewoDShowFilter::SeewoDShowFilter() {
    ::InitializeCriticalSection(&lock_);
    ::StringCchCopyW(name_, NUMELMS(name_), kFriendlyName);

    // Counted so DllCanUnloadNow refuses to unload the DLL while a filter
    // instance is alive, even between a Run and its matching Stop.
    AddRefObject();

    pin_ = new (std::nothrow) SeewoOutputPin(this);
    // pin_ stays nullptr if the allocation failed; every method below tolerates
    // that rather than crashing inside a graph.
}

SeewoDShowFilter::~SeewoDShowFilter() {
    // Stop the pin before releasing it so the streaming thread is already
    // unwound by the time the graph's reference count drops to zero.
    if (pin_ != nullptr) {
        pin_->StopStreaming();
        pin_->Release();
        pin_ = nullptr;
    }
    if (graph_ != nullptr) {
        graph_->Release();
        graph_ = nullptr;
    }
    ::DeleteCriticalSection(&lock_);

    // Paired with the constructor, and after the pin has dropped its own module
    // lock, so the counts unwind in the order they were taken.
    ReleaseObject();
}

STDMETHODIMP SeewoDShowFilter::QueryInterface(REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    *ppv = nullptr;

    if (riid == IID_IUnknown || riid == IID_IBaseFilter) {
        *ppv = static_cast<IBaseFilter*>(this);
    } else if (riid == IID_IMediaFilter) {
        *ppv = static_cast<IMediaFilter*>(this);
    } else if (riid == IID_IPersist) {
        *ppv = static_cast<IPersist*>(this);
    } else if (riid == IID_IAMFilterMiscFlags) {
        *ppv = static_cast<IAMFilterMiscFlags*>(this);
    } else if (riid == IID_ISpecifyPropertyPages) {
        *ppv = static_cast<ISpecifyPropertyPages*>(this);
    } else {
        return E_NOINTERFACE;
    }

    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) SeewoDShowFilter::AddRef() {
    return static_cast<ULONG>(refCount_.fetch_add(1) + 1);
}

STDMETHODIMP_(ULONG) SeewoDShowFilter::Release() {
    const LONG remaining = refCount_.fetch_sub(1) - 1;
    if (remaining == 0) {
        delete this;
    }
    return static_cast<ULONG>(remaining);
}

// --- IPersist ---------------------------------------------------------------

STDMETHODIMP SeewoDShowFilter::GetClassID(CLSID* clsid) {
    if (clsid == nullptr) {
        return E_POINTER;
    }
    *clsid = CLSID_SeewoVirtualCamera;
    return S_OK;
}

// --- IMediaFilter -----------------------------------------------------------

STDMETHODIMP SeewoDShowFilter::Stop() {
    if (pin_ == nullptr) {
        return E_UNEXPECTED;
    }

    ::EnterCriticalSection(&lock_);
    state_ = State_Stopped;
    ::LeaveCriticalSection(&lock_);

    // The pin's stop path is bounded: it signals the worker and waits with a
    // timeout, so a downstream filter that never returns from Receive() cannot
    // hang the graph here.
    return pin_->StopStreaming();
}

STDMETHODIMP SeewoDShowFilter::Pause() {
    if (pin_ == nullptr) {
        return E_UNEXPECTED;
    }

    ::EnterCriticalSection(&lock_);
    const FILTER_STATE previous = state_;
    ::LeaveCriticalSection(&lock_);

    // Pausing from Stopped is the graph's way of asking us to allocate buffers
    // and get ready without delivering frames yet. Starting the session already
    // paused gives the graph a committed allocator, which is exactly what a
    // capture app checks before it calls Run.
    HRESULT hr = S_OK;
    if (previous == State_Stopped) {
        hr = pin_->StartStreaming(/*startPaused=*/true);
    } else {
        hr = pin_->PauseStreaming();
    }

    if (SUCCEEDED(hr)) {
        ::EnterCriticalSection(&lock_);
        state_ = State_Paused;
        ::LeaveCriticalSection(&lock_);
    }
    return hr;
}

STDMETHODIMP SeewoDShowFilter::Run(REFERENCE_TIME start) {
    (void)start;  // A live source ignores the requested stream start time.

    if (pin_ == nullptr) {
        return E_UNEXPECTED;
    }

    // StartStreaming is idempotent: from Paused it clears the pause flag and
    // resumes the existing session without reallocating or renegotiating.
    const HRESULT hr = pin_->StartStreaming(/*startPaused=*/false);
    if (FAILED(hr)) {
        return hr;
    }

    ::EnterCriticalSection(&lock_);
    state_ = State_Running;
    ::LeaveCriticalSection(&lock_);
    return S_OK;
}

STDMETHODIMP SeewoDShowFilter::GetState(DWORD milliseconds,
                                        FILTER_STATE* state) {
    if (state == nullptr) {
        return E_POINTER;
    }

    // Our transitions are synchronous: by the time Stop/Pause/Run return, the
    // state has already changed. There is no intermediate state to wait on, so
    // `milliseconds` is honoured trivially.
    (void)milliseconds;

    ::EnterCriticalSection(&lock_);
    *state = state_;
    ::LeaveCriticalSection(&lock_);
    return S_OK;
}

STDMETHODIMP SeewoDShowFilter::SetSyncSource(IReferenceClock* clock) {
    if (pin_ == nullptr) {
        return E_UNEXPECTED;
    }
    // The graph's reference clock is the pin's pacing source. A null clock means
    // "run as fast as you can", which for a live source we interpret as the
    // fallback waitable-timer cadence.
    pin_->SetSyncSource(clock);
    return S_OK;
}

STDMETHODIMP SeewoDShowFilter::GetSyncSource(IReferenceClock** clock) {
    if (clock == nullptr) {
        return E_POINTER;
    }
    *clock = nullptr;

    ::EnterCriticalSection(&lock_);
    IFilterGraph* graph = graph_;
    if (graph != nullptr) {
        graph->AddRef();
    }
    ::LeaveCriticalSection(&lock_);

    if (graph == nullptr) {
        // No graph: the filter has no sync source of its own. This is the
        // documented answer for a source filter that does not implement one.
        return S_OK;
    }

    // Delegate to the graph, which owns the clock. Doing it this way means a
    // capture app that calls SetSyncSource(NULL) on the graph sees the effect
    // here without us having to mirror the graph's state. IFilterGraph itself
    // does not expose GetSyncSource; the graph object implements IMediaFilter.
    IMediaFilter* mediaFilter = nullptr;
    const HRESULT hr =
        graph->QueryInterface(IID_IMediaFilter,
                              reinterpret_cast<void**>(&mediaFilter));
    graph->Release();
    if (FAILED(hr) || mediaFilter == nullptr) {
        return S_OK;
    }

    const HRESULT result = mediaFilter->GetSyncSource(clock);
    mediaFilter->Release();
    return result;
}

// --- IBaseFilter ------------------------------------------------------------

STDMETHODIMP SeewoDShowFilter::EnumPins(IEnumPins** enumerator) {
    if (enumerator == nullptr) {
        return E_POINTER;
    }
    *enumerator = nullptr;

    auto* created = new (std::nothrow) PinEnumerator(pin_);
    if (created == nullptr) {
        return E_OUTOFMEMORY;
    }
    *enumerator = static_cast<IEnumPins*>(created);
    return S_OK;
}

STDMETHODIMP SeewoDShowFilter::FindPin(LPCWSTR id, IPin** pin) {
    if (id == nullptr || pin == nullptr) {
        return E_POINTER;
    }
    *pin = nullptr;

    if (pin_ == nullptr) {
        return VFW_E_NOT_FOUND;
    }

    if (::wcscmp(id, kPinId) != 0 && ::wcscmp(id, L"1") != 0) {
        return VFW_E_NOT_FOUND;
    }

    pin_->AddRef();
    *pin = pin_;
    return S_OK;
}

STDMETHODIMP SeewoDShowFilter::QueryFilterInfo(FILTER_INFO* info) {
    if (info == nullptr) {
        return E_POINTER;
    }

    IFilterGraph* graph = nullptr;
    {
        ::EnterCriticalSection(&lock_);
        graph = graph_;
        ::StringCchCopyW(info->achName, NUMELMS(info->achName), name_);
        ::LeaveCriticalSection(&lock_);
    }

    // FILTER_INFO::pGraph is an AddRef'd out-parameter, exactly as
    // CBaseFilter::QueryFilterInfo does it. Returning a borrowed pointer here
    // would make a caller that follows the documented contract release a
    // reference we never handed out, dropping the graph while it is still in
    // use. The AddRef is taken outside the lock so a re-entrant AddRef can
    // never deadlock us.
    info->pGraph = graph;
    if (graph != nullptr) {
        graph->AddRef();
    }
    return S_OK;
}

STDMETHODIMP SeewoDShowFilter::JoinFilterGraph(IFilterGraph* graph,
                                               LPCWSTR name) {
    ::EnterCriticalSection(&lock_);

    if (graph != nullptr) {
        graph->AddRef();
    }
    if (graph_ != nullptr) {
        graph_->Release();
    }
    graph_ = graph;

    if (name != nullptr) {
        ::StringCchCopyW(name_, NUMELMS(name_), name);
    } else if (graph == nullptr) {
        // Leaving the graph: drop any instance name and fall back to the
        // canonical friendly name so a later QueryFilterInfo is still sane.
        ::StringCchCopyW(name_, NUMELMS(name_), kFriendlyName);
    }

    ::LeaveCriticalSection(&lock_);
    return S_OK;
}

STDMETHODIMP SeewoDShowFilter::QueryVendorInfo(LPWSTR* vendorInfo) {
    return CopyToTaskMem(L"Seewo", vendorInfo);
}

// --- IAMFilterMiscFlags -----------------------------------------------------

STDMETHODIMP_(ULONG) SeewoDShowFilter::GetMiscFlags() {
    // This is what tells capture applications (and the graph builder) that we
    // are a live source: no EOS, timestamps advance in real time, and the graph
    // should not wait for us to finish.
    return AM_FILTER_MISC_FLAGS_IS_SOURCE;
}

// --- ISpecifyPropertyPages --------------------------------------------------

STDMETHODIMP SeewoDShowFilter::GetPages(CAUUID* pages) {
    if (pages == nullptr) {
        return E_POINTER;
    }
    // No property pages. Returning an empty, non-null array is the documented
    // way to say "this filter has no UI" -- returning E_NOTIMPL would make some
    // property-sheet hosts show an error dialog instead of simply no pages.
    pages->cElems = 0;
    pages->pElems = nullptr;
    return S_OK;
}

// ---------------------------------------------------------------------------
// SeewoClassFactory
// ---------------------------------------------------------------------------

SeewoClassFactory::SeewoClassFactory() = default;
SeewoClassFactory::~SeewoClassFactory() = default;

STDMETHODIMP SeewoClassFactory::QueryInterface(REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    *ppv = nullptr;

    if (riid == IID_IUnknown || riid == IID_IClassFactory) {
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) SeewoClassFactory::AddRef() {
    return static_cast<ULONG>(refCount_.fetch_add(1) + 1);
}

STDMETHODIMP_(ULONG) SeewoClassFactory::Release() {
    const LONG remaining = refCount_.fetch_sub(1) - 1;
    if (remaining == 0) {
        delete this;
    }
    return static_cast<ULONG>(remaining);
}

STDMETHODIMP SeewoClassFactory::CreateInstance(IUnknown* outer, REFIID riid,
                                               void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    *ppv = nullptr;

    // Aggregation is not supported: a DirectShow filter has no meaningful outer
    // unknown, and silently ignoring the request would confuse the caller.
    if (outer != nullptr) {
        return CLASS_E_NOAGGREGATION;
    }

    auto* filter = new (std::nothrow) SeewoDShowFilter();
    if (filter == nullptr) {
        return E_OUTOFMEMORY;
    }

    const HRESULT hr = filter->QueryInterface(riid, ppv);
    filter->Release();  // QueryInterface took its own reference on success
    return hr;
}

STDMETHODIMP SeewoClassFactory::LockServer(BOOL lock) {
    if (lock) {
        LockModule();
    } else {
        UnlockModule();
    }
    return S_OK;
}

}  // namespace dshow
}  // namespace seewo
