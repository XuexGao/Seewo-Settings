// SeewoVirtualCameraActivate.h
//
// The COM class object (coclass) that the Media Foundation FrameServer creates
// when it wants the Seewo virtual camera.  It is an IMFActivate (which is an
// IMFAttributes) plus an IClassFactory, and it owns the media source it hands
// out.
//
// Everything in this project is built on the Windows SDK WRL (Microsoft::WRL)
// only: no C++/WinRT, no WIL, no NuGet, no vcpkg.
#pragma once

#ifndef SEEWO_VIRTUAL_CAMERA_ACTIVATE_H
#define SEEWO_VIRTUAL_CAMERA_ACTIVATE_H

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <wrl.h>
#include <wrl/implements.h>

// {A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}
// The CLSID of the virtual camera media source.  The registration tool passes
// this value to MFCreateVirtualCamera() and writes the matching InprocServer32
// key; the FrameServer then calls DllGetClassObject() with it.
extern const CLSID CLSID_SeewoVirtualCamera;

namespace seewo {

// ---------------------------------------------------------------------------
// Module lifetime
// ---------------------------------------------------------------------------
// DllCanUnloadNow must not report "unloadable" while any object this DLL handed
// out is still alive.  WRL's own module object count only tracks objects created
// through Module::CreateInstance, and this DLL creates its objects with
// Make<>/MakeAndInitialize<>, so a private counter is maintained instead.  It is
// incremented from the constructors and decremented from the destructors, which
// makes it conservative: the DLL is never unloaded while an object exists.
void AddModuleObject() noexcept;
void ReleaseModuleObject() noexcept;
LONG GetModuleObjectCount() noexcept;

// ---------------------------------------------------------------------------
// SeewoVirtualCameraActivate
// ---------------------------------------------------------------------------
// IMFActivate inherits from IMFAttributes.  Rather than reimplementing the
// attribute store, a real IMFAttributes is created with MFCreateAttributes() and
// every IMFAttributes call is forwarded to it verbatim.  Only the most derived
// interface (IMFActivate) is listed for WRL: RuntimeClass's QueryInterface
// resolves base interfaces of the listed ones as well, so asking this object for
// IMFAttributes or IUnknown succeeds through the same COM identity.
class __declspec(uuid("A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60"))
    SeewoVirtualCameraActivate
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          IMFActivate> {
 public:
  SeewoVirtualCameraActivate() noexcept;
  ~SeewoVirtualCameraActivate() override;

  // Creates the backing attribute store.  Called by the class factory before the
  // object is returned to COM.
  HRESULT Initialize();

  // --- IMFActivate ---------------------------------------------------------
  IFACEMETHODIMP ActivateObject(REFIID riid, void** ppvObject) override;
  IFACEMETHODIMP ShutdownObject() override;
  IFACEMETHODIMP DetachObject() override;

  // --- IMFAttributes (delegated) -------------------------------------------
  IFACEMETHODIMP GetItem(REFGUID guidKey, PROPVARIANT* pValue) override;
  IFACEMETHODIMP GetItemType(REFGUID guidKey, MF_ATTRIBUTE_TYPE* pType) override;
  IFACEMETHODIMP CompareItem(REFGUID guidKey, REFPROPVARIANT Value,
                             BOOL* pbResult) override;
  IFACEMETHODIMP Compare(IMFAttributes* pTheirs, MF_ATTRIBUTES_MATCH_TYPE MatchType,
                         BOOL* pbResult) override;
  IFACEMETHODIMP GetUINT32(REFGUID guidKey, UINT32* punValue) override;
  IFACEMETHODIMP GetUINT64(REFGUID guidKey, UINT64* punValue) override;
  IFACEMETHODIMP GetDouble(REFGUID guidKey, double* pfValue) override;
  IFACEMETHODIMP GetGUID(REFGUID guidKey, GUID* pguidValue) override;
  IFACEMETHODIMP GetStringLength(REFGUID guidKey, UINT32* pcchLength) override;
  IFACEMETHODIMP GetString(REFGUID guidKey, LPWSTR pwszValue, UINT32 cchBufSize,
                           UINT32* pcchLength) override;
  IFACEMETHODIMP GetAllocatedString(REFGUID guidKey, LPWSTR* ppwszValue,
                                    UINT32* pcchLength) override;
  IFACEMETHODIMP GetBlobSize(REFGUID guidKey, UINT32* pcbBlobSize) override;
  IFACEMETHODIMP GetBlob(REFGUID guidKey, UINT8* pBuf, UINT32 cbBufSize,
                         UINT32* pcbBlobSize) override;
  IFACEMETHODIMP GetAllocatedBlob(REFGUID guidKey, UINT8** ppBuf,
                                  UINT32* pcbSize) override;
  IFACEMETHODIMP GetUnknown(REFGUID guidKey, REFIID riid, LPVOID* ppv) override;
  IFACEMETHODIMP SetItem(REFGUID guidKey, REFPROPVARIANT Value) override;
  IFACEMETHODIMP DeleteItem(REFGUID guidKey) override;
  IFACEMETHODIMP DeleteAllItems() override;
  IFACEMETHODIMP SetUINT32(REFGUID guidKey, UINT32 unValue) override;
  IFACEMETHODIMP SetUINT64(REFGUID guidKey, UINT64 unValue) override;
  IFACEMETHODIMP SetDouble(REFGUID guidKey, double fValue) override;
  IFACEMETHODIMP SetGUID(REFGUID guidKey, REFGUID guidValue) override;
  IFACEMETHODIMP SetString(REFGUID guidKey, LPCWSTR wszValue) override;
  IFACEMETHODIMP SetBlob(REFGUID guidKey, const UINT8* pBuf,
                         UINT32 cbBufSize) override;
  IFACEMETHODIMP SetUnknown(REFGUID guidKey, IUnknown* pUnknown) override;
  IFACEMETHODIMP LockStore() override;
  IFACEMETHODIMP UnlockStore() override;
  IFACEMETHODIMP GetCount(UINT32* pcItems) override;
  IFACEMETHODIMP GetItemByIndex(UINT32 unIndex, GUID* pguidKey,
                                PROPVARIANT* pValue) override;
  IFACEMETHODIMP CopyAllItems(IMFAttributes* pDest) override;

 private:
  // Guards attributes_/source_ against concurrent ActivateObject/ShutdownObject.
  SRWLOCK lock_ = SRWLOCK_INIT;
  Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
  // The media source this activate object produced.  Kept alive (and shut down)
  // here so repeated ActivateObject() calls hand out the same instance, as
  // IMFActivate requires.
  Microsoft::WRL::ComPtr<IMFMediaSource> source_;
};

// ---------------------------------------------------------------------------
// SeewoVirtualCameraActivateFactory
// ---------------------------------------------------------------------------
// The class factory returned by DllGetClassObject.  It creates the activate
// object and calls Initialize() before exposing it, which WRL's stock
// ClassFactory<T> would not do.
class SeewoVirtualCameraActivateFactory
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          IClassFactory> {
 public:
  SeewoVirtualCameraActivateFactory() noexcept;
  ~SeewoVirtualCameraActivateFactory() override;

  IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid,
                                void** ppvObject) override;
  IFACEMETHODIMP LockServer(BOOL fLock) override;

 private:
  LONG lockCount_ = 0;
};

}  // namespace seewo

#endif  // SEEWO_VIRTUAL_CAMERA_ACTIVATE_H
