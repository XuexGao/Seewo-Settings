// SeewoMediaSource.h
//
// The virtual camera media source: one video stream, live, no seeking.
//
// IMFMediaSourceEx already derives from IMFMediaSource, which derives from
// IMFMediaEventGenerator.  IMFGetService and IMFAttributes are additional
// interfaces exposed alongside it, so the FrameServer can ask for services and
// so the source attribute store is reachable through a plain QueryInterface.
// Every method is implemented; nothing returns E_NOTIMPL.
//
// WRL is given only the most derived interface of each chain (IMFMediaSourceEx,
// IMFGetService, IMFAttributes) because RuntimeClass's QueryInterface resolves
// the base interfaces of the listed ones as well.
//
// Threading model
// ---------------
// A single SRWLOCK guards all mutable state.  The source's event queue is a real
// IMFMediaEventQueue created by MFCreateEventQueue; it has its own internal
// locking, so events may be queued from the stream's work-queue thread while the
// pipeline thread is blocked in GetEvent().
#pragma once

#ifndef SEEWO_MEDIA_SOURCE_H
#define SEEWO_MEDIA_SOURCE_H

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <wrl.h>
#include <wrl/implements.h>

#include <vector>

#include "SeewoMediaStream.h"

namespace seewo {

class SeewoMediaSource
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          IMFMediaSourceEx,
          IMFGetService,
          IMFAttributes> {
 public:
  SeewoMediaSource() noexcept;
  ~SeewoMediaSource() override;

  // Builds the stream, the presentation descriptor and the event queue.
  // `activateAttributes` may be null; it is copied into the source attribute
  // store so a registration tool can publish extra keys through IMFActivate.
  HRESULT Initialize(IMFAttributes* activateAttributes);

  // --- IMFMediaEventGenerator ---------------------------------------------
  IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback,
                               IUnknown* punkState) override;
  IFACEMETHODIMP EndGetEvent(IMFAsyncResult* pResult,
                             IMFMediaEvent** ppEvent) override;
  IFACEMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
  IFACEMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType,
                            HRESULT hrStatus,
                            const PROPVARIANT* pvValue) override;

  // --- IMFMediaSource ------------------------------------------------------
  IFACEMETHODIMP CreatePresentationDescriptor(
      IMFPresentationDescriptor** ppPresentationDescriptor) override;
  IFACEMETHODIMP GetCharacteristics(DWORD* pdwCharacteristics) override;
  IFACEMETHODIMP Pause() override;
  IFACEMETHODIMP Shutdown() override;
  IFACEMETHODIMP Start(IMFPresentationDescriptor* pPresentationDescriptor,
                       const GUID* pguidTimeFormat,
                       const PROPVARIANT* pvarStartPosition) override;
  IFACEMETHODIMP Stop() override;

  // --- IMFMediaSourceEx ----------------------------------------------------
  IFACEMETHODIMP GetSourceAttributes(IMFAttributes** ppAttributes) override;
  IFACEMETHODIMP GetStreamAttributes(DWORD dwStreamIdentifier,
                                     IMFAttributes** ppAttributes) override;
  IFACEMETHODIMP SetD3DManager(IUnknown* pManager) override;

  // --- IMFAttributes (delegated to a real store) ---------------------------
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

  // --- IMFGetService -------------------------------------------------------
  IFACEMETHODIMP GetService(REFGUID guidService, REFIID riid,
                            LPVOID* ppvObject) override;

 private:
  enum class SourceState { kInvalid, kStopped, kStarted, kShutdown };

  // Caller must hold lock_.
  HRESULT CheckShutdownLocked() const;
  // Confirms that the presentation descriptor matches this source's streams and
  // that at least one stream is selected.
  HRESULT ValidatePresentationDescriptorLocked(
      IMFPresentationDescriptor* descriptor);
  // Finds the stream with `streamId` in the presentation descriptor.
  HRESULT FindStreamDescriptorByIndexLocked(DWORD streamId, DWORD* index,
                                            bool* selected);
  Microsoft::WRL::ComPtr<SeewoMediaStream> FindStreamByIdLocked(
      DWORD streamId) const;

  SRWLOCK lock_ = SRWLOCK_INIT;

  SourceState state_ = SourceState::kInvalid;
  bool initialized_ = false;
  bool shutdown_ = false;

  Microsoft::WRL::ComPtr<IMFMediaEventQueue> eventQueue_;
  Microsoft::WRL::ComPtr<IMFPresentationDescriptor> presentationDescriptor_;
  Microsoft::WRL::ComPtr<IMFAttributes> attributes_;

  std::vector<Microsoft::WRL::ComPtr<SeewoMediaStream>> streams_;
};

}  // namespace seewo

#endif  // SEEWO_MEDIA_SOURCE_H
