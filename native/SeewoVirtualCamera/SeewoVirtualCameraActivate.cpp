// SeewoVirtualCameraActivate.cpp
//
// Implementation of the coclass / class factory.  See the header for the design
// notes.  Only the Windows SDK (WRL + Media Foundation) is used.
#include "SeewoVirtualCameraActivate.h"

#include "SeewoMediaSource.h"

#include <new>

namespace seewo {

// {A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}
extern const CLSID CLSID_SeewoVirtualCamera = {
    0xA7E4B2C1,
    0x5D3F,
    0x4A88,
    {0x9B, 0x6E, 0x1C, 0x2D, 0x3E, 0x4F, 0x5A, 0x60}};

namespace {

// Process-wide object count.  See the header for why this exists instead of
// relying on WRL's module object count.
volatile LONG g_moduleObjectCount = 0;

// Common argument checks shared by the IMFAttributes forwarders.
inline HRESULT CheckAttributeStore(IMFAttributes* store) {
  return (store == nullptr) ? E_OUTOFMEMORY : S_OK;
}

}  // namespace

void AddModuleObject() noexcept {
  ::InterlockedIncrement(&g_moduleObjectCount);
}

void ReleaseModuleObject() noexcept {
  ::InterlockedDecrement(&g_moduleObjectCount);
}

LONG GetModuleObjectCount() noexcept {
  return ::InterlockedCompareExchange(&g_moduleObjectCount, 0, 0);
}

// ---------------------------------------------------------------------------
// SeewoVirtualCameraActivate
// ---------------------------------------------------------------------------

SeewoVirtualCameraActivate::SeewoVirtualCameraActivate() noexcept {
  AddModuleObject();
}

SeewoVirtualCameraActivate::~SeewoVirtualCameraActivate() {
  ReleaseModuleObject();
}

HRESULT SeewoVirtualCameraActivate::Initialize() {
  // A 4-entry store is plenty for the handful of keys the FrameServer may set on
  // an activate object (MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES and
  // friends).
  Microsoft::WRL::ComPtr<IMFAttributes> attributes;
  HRESULT hr = ::MFCreateAttributes(&attributes, 4);
  if (FAILED(hr)) {
    return hr;
  }

  ::AcquireSRWLockExclusive(&lock_);
  attributes_ = attributes;
  ::ReleaseSRWLockExclusive(&lock_);

  return S_OK;
}

IFACEMETHODIMP SeewoVirtualCameraActivate::ActivateObject(REFIID riid,
                                                          void** ppvObject) {
  if (ppvObject == nullptr) {
    return E_POINTER;
  }
  *ppvObject = nullptr;

  ::AcquireSRWLockExclusive(&lock_);

  // IMFActivate::ActivateObject must return the same object for every call, so
  // the first call creates and caches it and later calls just re-query.
  HRESULT hr = S_OK;
  if (source_ == nullptr) {
    Microsoft::WRL::ComPtr<SeewoMediaSource> source =
        Microsoft::WRL::Make<SeewoMediaSource>();
    if (source == nullptr) {
      hr = E_OUTOFMEMORY;
    } else {
      hr = source->Initialize(attributes_.Get());
      if (SUCCEEDED(hr)) {
        hr = source.As(&source_);
      }
    }
  }

  if (SUCCEEDED(hr)) {
    hr = source_.Get()->QueryInterface(riid, ppvObject);
  }

  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

IFACEMETHODIMP SeewoVirtualCameraActivate::ShutdownObject() {
  Microsoft::WRL::ComPtr<IMFMediaSource> source;
  {
    ::AcquireSRWLockExclusive(&lock_);
    source = source_;
    source_.Reset();
    ::ReleaseSRWLockExclusive(&lock_);
  }

  if (source != nullptr) {
    // Shutdown() is idempotent and the source releases its own worker threads
    // and shared-memory view, so propagating its HRESULT is the right thing to
    // do here.
    return source->Shutdown();
  }
  return S_OK;
}

IFACEMETHODIMP SeewoVirtualCameraActivate::DetachObject() {
  ::AcquireSRWLockExclusive(&lock_);
  // Hand ownership of the live source back to the caller: drop our reference
  // without shutting it down.  The source stays usable for as long as the
  // caller keeps it alive.
  source_.Reset();
  ::ReleaseSRWLockExclusive(&lock_);
  return S_OK;
}

// --- IMFAttributes ---------------------------------------------------------
// Every method forwards to the real attribute store created by
// MFCreateAttributes().  The store is created in Initialize() and never reset,
// so a null check is defensive only.

#define SEEWO_FORWARD_ATTRIBUTES(call)                       \
  do {                                                       \
    IMFAttributes* store = attributes_.Get();                \
    HRESULT _hr = CheckAttributeStore(store);                \
    if (FAILED(_hr)) {                                       \
      return _hr;                                            \
    }                                                        \
    return store->call;                                      \
  } while (false)

IFACEMETHODIMP SeewoVirtualCameraActivate::GetItem(REFGUID guidKey,
                                                   PROPVARIANT* pValue) {
  SEEWO_FORWARD_ATTRIBUTES(GetItem(guidKey, pValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetItemType(
    REFGUID guidKey, MF_ATTRIBUTE_TYPE* pType) {
  SEEWO_FORWARD_ATTRIBUTES(GetItemType(guidKey, pType));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::CompareItem(REFGUID guidKey,
                                                       REFPROPVARIANT Value,
                                                       BOOL* pbResult) {
  SEEWO_FORWARD_ATTRIBUTES(CompareItem(guidKey, Value, pbResult));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::Compare(
    IMFAttributes* pTheirs, MF_ATTRIBUTES_MATCH_TYPE MatchType, BOOL* pbResult) {
  SEEWO_FORWARD_ATTRIBUTES(Compare(pTheirs, MatchType, pbResult));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetUINT32(REFGUID guidKey,
                                                     UINT32* punValue) {
  SEEWO_FORWARD_ATTRIBUTES(GetUINT32(guidKey, punValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetUINT64(REFGUID guidKey,
                                                     UINT64* punValue) {
  SEEWO_FORWARD_ATTRIBUTES(GetUINT64(guidKey, punValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetDouble(REFGUID guidKey,
                                                     double* pfValue) {
  SEEWO_FORWARD_ATTRIBUTES(GetDouble(guidKey, pfValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetGUID(REFGUID guidKey,
                                                   GUID* pguidValue) {
  SEEWO_FORWARD_ATTRIBUTES(GetGUID(guidKey, pguidValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetStringLength(REFGUID guidKey,
                                                           UINT32* pcchLength) {
  SEEWO_FORWARD_ATTRIBUTES(GetStringLength(guidKey, pcchLength));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetString(REFGUID guidKey,
                                                     LPWSTR pwszValue,
                                                     UINT32 cchBufSize,
                                                     UINT32* pcchLength) {
  SEEWO_FORWARD_ATTRIBUTES(
      GetString(guidKey, pwszValue, cchBufSize, pcchLength));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetAllocatedString(
    REFGUID guidKey, LPWSTR* ppwszValue, UINT32* pcchLength) {
  SEEWO_FORWARD_ATTRIBUTES(
      GetAllocatedString(guidKey, ppwszValue, pcchLength));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetBlobSize(REFGUID guidKey,
                                                       UINT32* pcbBlobSize) {
  SEEWO_FORWARD_ATTRIBUTES(GetBlobSize(guidKey, pcbBlobSize));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetBlob(REFGUID guidKey, UINT8* pBuf,
                                                   UINT32 cbBufSize,
                                                   UINT32* pcbBlobSize) {
  SEEWO_FORWARD_ATTRIBUTES(GetBlob(guidKey, pBuf, cbBufSize, pcbBlobSize));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetAllocatedBlob(REFGUID guidKey,
                                                            UINT8** ppBuf,
                                                            UINT32* pcbSize) {
  SEEWO_FORWARD_ATTRIBUTES(GetAllocatedBlob(guidKey, ppBuf, pcbSize));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetUnknown(REFGUID guidKey,
                                                      REFIID riid,
                                                      LPVOID* ppv) {
  SEEWO_FORWARD_ATTRIBUTES(GetUnknown(guidKey, riid, ppv));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::SetItem(REFGUID guidKey,
                                                   REFPROPVARIANT Value) {
  SEEWO_FORWARD_ATTRIBUTES(SetItem(guidKey, Value));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::DeleteItem(REFGUID guidKey) {
  SEEWO_FORWARD_ATTRIBUTES(DeleteItem(guidKey));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::DeleteAllItems() {
  SEEWO_FORWARD_ATTRIBUTES(DeleteAllItems());
}

IFACEMETHODIMP SeewoVirtualCameraActivate::SetUINT32(REFGUID guidKey,
                                                     UINT32 unValue) {
  SEEWO_FORWARD_ATTRIBUTES(SetUINT32(guidKey, unValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::SetUINT64(REFGUID guidKey,
                                                     UINT64 unValue) {
  SEEWO_FORWARD_ATTRIBUTES(SetUINT64(guidKey, unValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::SetDouble(REFGUID guidKey,
                                                     double fValue) {
  SEEWO_FORWARD_ATTRIBUTES(SetDouble(guidKey, fValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::SetGUID(REFGUID guidKey,
                                                   REFGUID guidValue) {
  SEEWO_FORWARD_ATTRIBUTES(SetGUID(guidKey, guidValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::SetString(REFGUID guidKey,
                                                     LPCWSTR wszValue) {
  SEEWO_FORWARD_ATTRIBUTES(SetString(guidKey, wszValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::SetBlob(REFGUID guidKey,
                                                   const UINT8* pBuf,
                                                   UINT32 cbBufSize) {
  SEEWO_FORWARD_ATTRIBUTES(SetBlob(guidKey, pBuf, cbBufSize));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::SetUnknown(REFGUID guidKey,
                                                      IUnknown* pUnknown) {
  SEEWO_FORWARD_ATTRIBUTES(SetUnknown(guidKey, pUnknown));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::LockStore() {
  SEEWO_FORWARD_ATTRIBUTES(LockStore());
}

IFACEMETHODIMP SeewoVirtualCameraActivate::UnlockStore() {
  SEEWO_FORWARD_ATTRIBUTES(UnlockStore());
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetCount(UINT32* pcItems) {
  SEEWO_FORWARD_ATTRIBUTES(GetCount(pcItems));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::GetItemByIndex(
    UINT32 unIndex, GUID* pguidKey, PROPVARIANT* pValue) {
  SEEWO_FORWARD_ATTRIBUTES(GetItemByIndex(unIndex, pguidKey, pValue));
}

IFACEMETHODIMP SeewoVirtualCameraActivate::CopyAllItems(IMFAttributes* pDest) {
  SEEWO_FORWARD_ATTRIBUTES(CopyAllItems(pDest));
}

#undef SEEWO_FORWARD_ATTRIBUTES

// ---------------------------------------------------------------------------
// SeewoVirtualCameraActivateFactory
// ---------------------------------------------------------------------------

SeewoVirtualCameraActivateFactory::SeewoVirtualCameraActivateFactory() noexcept {
  AddModuleObject();
}

SeewoVirtualCameraActivateFactory::~SeewoVirtualCameraActivateFactory() {
  ReleaseModuleObject();
}

IFACEMETHODIMP SeewoVirtualCameraActivateFactory::CreateInstance(
    IUnknown* pUnkOuter, REFIID riid, void** ppvObject) {
  if (ppvObject == nullptr) {
    return E_POINTER;
  }
  *ppvObject = nullptr;

  // This object is not usable for aggregation: it has no inner unknown and no
  // tear-off support, so refuse rather than silently misbehave.
  if (pUnkOuter != nullptr) {
    return CLASS_E_NOAGGREGATION;
  }

  Microsoft::WRL::ComPtr<SeewoVirtualCameraActivate> activate =
      Microsoft::WRL::Make<SeewoVirtualCameraActivate>();
  if (activate == nullptr) {
    return E_OUTOFMEMORY;
  }

  HRESULT hr = activate->Initialize();
  if (FAILED(hr)) {
    return hr;
  }

  return activate->QueryInterface(riid, ppvObject);
}

IFACEMETHODIMP SeewoVirtualCameraActivateFactory::LockServer(BOOL fLock) {
  // The factory object itself is kept alive by COM for as long as the class
  // object is registered, so a counter is all that is required.  The counter is
  // reflected into the module object count so that DllCanUnloadNow() respects a
  // locked server.  An unmatched unlock is tolerated rather than failed: COM is
  // allowed to call LockServer(FALSE) defensively, and failing it would make the
  // frame server treat the class object as broken.
  if (fLock) {
    ::InterlockedIncrement(&lockCount_);
    AddModuleObject();
  } else if (::InterlockedDecrement(&lockCount_) >= 0) {
    ReleaseModuleObject();
  } else {
    ::InterlockedIncrement(&lockCount_);  // restore the clamped-at-zero value
  }
  return S_OK;
}

}  // namespace seewo
