// SeewoMediaSource.cpp
//
// See the header for the threading model and interface notes.
#include "SeewoMediaSource.h"

#include <mfobjects.h>
#include <mferror.h>
// PINNAME_VIDEO_CAPTURE (used for MF_DEVICESTREAM_STREAM_CATEGORY) is declared in
// ksmedia.h, which requires ks.h and then ksproxy.h to be included first.  The
// PINNAME_* GUIDs themselves come from ksguid.lib, which the project links.
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>

#include <new>

namespace seewo {

namespace {

// The virtual camera is live: it produces frames in real time and cannot seek.
// MFMEDIASOURCE_CAN_PAUSE is deliberately not set, because Pause() is rejected.
constexpr DWORD kSourceCharacteristics = MFMEDIASOURCE_IS_LIVE;

// How many attributes the source store starts with.  The store grows on demand.
constexpr UINT32 kSourceAttributeCount = 8;

// MF_PD_DURATION for a live source.  Media Foundation wants a duration even for
// a live source; ~10 years in 100 ns units is effectively "unbounded" without
// risking overflow in a consumer that multiplies it.
constexpr LONGLONG kPresentationDuration = 3153600000000000LL;

// The macros below keep the IMFAttributes forwarders short.  The store is
// created in Initialize() and only cleared in Shutdown(), so the null check is
// defensive.
#define SEEWO_FORWARD_ATTR(call)                 \
  do {                                           \
    IMFAttributes* store = attributes_.Get();    \
    if (store == nullptr) {                      \
      return MF_E_SHUTDOWN;                      \
    }                                            \
    return store->call;                          \
  } while (false)

}  // namespace

SeewoMediaSource::SeewoMediaSource() noexcept {}

SeewoMediaSource::~SeewoMediaSource() {
  // Defensive: a caller that drops the last reference without calling
  // Shutdown() must not leave the stream's work queue running.
  Shutdown();
}

HRESULT SeewoMediaSource::Initialize(IMFAttributes* activateAttributes) {
  ::AcquireSRWLockExclusive(&lock_);

  if (initialized_) {
    ::ReleaseSRWLockExclusive(&lock_);
    return MF_E_ALREADY_INITIALIZED;
  }

  HRESULT hr = ::MFCreateEventQueue(&eventQueue_);

  if (SUCCEEDED(hr)) {
    hr = ::MFCreateAttributes(&attributes_, kSourceAttributeCount);
  }
  if (SUCCEEDED(hr)) {
    // Anything the registration tool set on the activate object is visible on
    // the source too.  A store holding a value type that cannot be copied is not
    // fatal: the source's own keys are what matter.
    if (activateAttributes != nullptr) {
      const HRESULT copyResult =
          activateAttributes->CopyAllItems(attributes_.Get());
      if (FAILED(copyResult)) {
        // Deliberately ignored; the source attributes below are still set.
      }
    }
  }
  if (SUCCEEDED(hr)) {
    hr = attributes_->SetString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,
                                L"Seewo Virtual Camera");
  }
  if (SUCCEEDED(hr)) {
    hr = attributes_->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY,
                              PINNAME_VIDEO_CAPTURE);
  }
  if (SUCCEEDED(hr)) {
    hr = attributes_->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
  }
  if (SUCCEEDED(hr)) {
    hr = attributes_->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES,
                                static_cast<UINT32>(MFFrameSourceTypes_Color));
  }

  // One video stream, selected by default.
  if (SUCCEEDED(hr)) {
    Microsoft::WRL::ComPtr<SeewoMediaStream> stream =
        Microsoft::WRL::Make<SeewoMediaStream>();
    if (stream == nullptr) {
      hr = E_OUTOFMEMORY;
    } else {
      hr = stream->Initialize(this, 0);
      if (SUCCEEDED(hr)) {
        try {
          streams_.push_back(stream);
        } catch (const std::bad_alloc&) {
          hr = E_OUTOFMEMORY;
        }
      }
    }
  }

  if (SUCCEEDED(hr)) {
    std::vector<IMFStreamDescriptor*> descriptors;
    try {
      descriptors.reserve(streams_.size());
      for (const auto& stream : streams_) {
        Microsoft::WRL::ComPtr<IMFStreamDescriptor> descriptor;
        hr = stream->GetStreamDescriptor(&descriptor);
        if (FAILED(hr)) {
          break;
        }
        descriptors.push_back(descriptor.Detach());
      }
    } catch (const std::bad_alloc&) {
      hr = E_OUTOFMEMORY;
    }

    if (SUCCEEDED(hr)) {
      hr = ::MFCreatePresentationDescriptor(
          static_cast<DWORD>(descriptors.size()), descriptors.data(),
          &presentationDescriptor_);
    }
    for (IMFStreamDescriptor* descriptor : descriptors) {
      if (descriptor != nullptr) {
        descriptor->Release();
      }
    }
  }

  if (SUCCEEDED(hr)) {
    // The single stream is selected by default, so a caller that simply passes
    // the presentation descriptor back to Start() gets video without having to
    // touch it.
    hr = presentationDescriptor_->SelectStream(0);
  }
  if (SUCCEEDED(hr)) {
    hr = presentationDescriptor_->SetUINT64(MF_PD_DURATION,
                                            static_cast<UINT64>(kPresentationDuration));
  }
  if (SUCCEEDED(hr)) {
    state_ = SourceState::kStopped;
    initialized_ = true;
  }

  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

HRESULT SeewoMediaSource::CheckShutdownLocked() const {
  if (shutdown_) {
    return MF_E_SHUTDOWN;
  }
  if (eventQueue_ == nullptr) {
    return E_UNEXPECTED;
  }
  return S_OK;
}

// ---------------------------------------------------------------------------
// IUnknown
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaSource::QueryInterface(REFIID riid, void** ppvObject) {
  if (ppvObject == nullptr) {
    return E_POINTER;
  }
  *ppvObject = nullptr;

  // Walk the interface chains explicitly.  IMFMediaSourceEx -> IMFMediaSource ->
  // IMFMediaEventGenerator, plus IMFGetService and IMFAttributes.  All of them
  // are implemented by this single object, so they share one identity and one
  // reference count.
  if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IMFMediaSourceEx)) {
    *ppvObject = static_cast<IMFMediaSourceEx*>(this);
  } else if (IsEqualIID(riid, IID_IMFMediaSource)) {
    *ppvObject = static_cast<IMFMediaSource*>(this);
  } else if (IsEqualIID(riid, IID_IMFMediaEventGenerator)) {
    *ppvObject = static_cast<IMFMediaEventGenerator*>(this);
  } else if (IsEqualIID(riid, IID_IMFAttributes)) {
    *ppvObject = static_cast<IMFAttributes*>(this);
  } else if (IsEqualIID(riid, IID_IMFGetService)) {
    *ppvObject = static_cast<IMFGetService*>(this);
  } else {
    // Anything else (IPersist, IMFSampleAllocatorControl, ...) is not offered.
    return E_NOINTERFACE;
  }

  AddRef();
  return S_OK;
}

// ---------------------------------------------------------------------------
// IMFMediaEventGenerator
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaSource::BeginGetEvent(IMFAsyncCallback* pCallback,
                                               IUnknown* punkState) {
  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue = eventQueue_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  return queue->BeginGetEvent(pCallback, punkState);
}

IFACEMETHODIMP SeewoMediaSource::EndGetEvent(IMFAsyncResult* pResult,
                                             IMFMediaEvent** ppEvent) {
  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue = eventQueue_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  return queue->EndGetEvent(pResult, ppEvent);
}

IFACEMETHODIMP SeewoMediaSource::GetEvent(DWORD dwFlags,
                                          IMFMediaEvent** ppEvent) {
  // GetEvent can block until an event is available, so the lock is released
  // before the call; the local reference keeps the queue alive.
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue;
  {
    ::AcquireSRWLockExclusive(&lock_);
    const HRESULT hr = CheckShutdownLocked();
    if (FAILED(hr)) {
      ::ReleaseSRWLockExclusive(&lock_);
      return hr;
    }
    queue = eventQueue_;
    ::ReleaseSRWLockExclusive(&lock_);
  }

  return queue->GetEvent(dwFlags, ppEvent);
}

IFACEMETHODIMP SeewoMediaSource::QueueEvent(MediaEventType met,
                                            REFGUID guidExtendedType,
                                            HRESULT hrStatus,
                                            const PROPVARIANT* pvValue) {
  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue = eventQueue_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  return queue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue);
}

// ---------------------------------------------------------------------------
// IMFMediaSource
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaSource::CreatePresentationDescriptor(
    IMFPresentationDescriptor** ppPresentationDescriptor) {
  if (ppPresentationDescriptor == nullptr) {
    return E_POINTER;
  }
  *ppPresentationDescriptor = nullptr;

  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFPresentationDescriptor> descriptor;
  if (SUCCEEDED(hr)) {
    if (presentationDescriptor_ == nullptr) {
      hr = E_UNEXPECTED;
    } else {
      // Hand out a clone so callers cannot mutate the source's own descriptor.
      hr = presentationDescriptor_->Clone(&descriptor);
    }
  }
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }

  *ppPresentationDescriptor = descriptor.Detach();
  return S_OK;
}

IFACEMETHODIMP SeewoMediaSource::GetCharacteristics(DWORD* pdwCharacteristics) {
  if (pdwCharacteristics == nullptr) {
    return E_POINTER;
  }
  *pdwCharacteristics = 0;

  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  if (SUCCEEDED(hr)) {
    *pdwCharacteristics = kSourceCharacteristics;
  }
  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

IFACEMETHODIMP SeewoMediaSource::Pause() {
  // A live source cannot be paused; MFMEDIASOURCE_CAN_PAUSE is not advertised.
  return MF_E_INVALID_STATE_TRANSITION;
}

IFACEMETHODIMP SeewoMediaSource::Shutdown() {
  std::vector<Microsoft::WRL::ComPtr<SeewoMediaStream>> streams;
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue;

  ::AcquireSRWLockExclusive(&lock_);
  if (shutdown_) {
    ::ReleaseSRWLockExclusive(&lock_);
    return S_OK;
  }
  shutdown_ = true;
  state_ = SourceState::kShutdown;

  queue = eventQueue_;
  eventQueue_.Reset();

  streams.swap(streams_);
  presentationDescriptor_.Reset();
  attributes_.Reset();
  ::ReleaseSRWLockExclusive(&lock_);

  // Shut the streams down outside the lock: their Shutdown() waits for the work
  // queue to drain, and doing that while holding the source lock could deadlock
  // against a work item that is calling back into the source.
  for (const auto& stream : streams) {
    if (stream != nullptr) {
      stream->Shutdown();
    }
  }

  if (queue != nullptr) {
    queue->Shutdown();
  }
  return S_OK;
}

IFACEMETHODIMP SeewoMediaSource::Start(
    IMFPresentationDescriptor* pPresentationDescriptor,
    const GUID* pguidTimeFormat, const PROPVARIANT* pvarStartPosition) {
  if (pPresentationDescriptor == nullptr || pvarStartPosition == nullptr) {
    return E_INVALIDARG;
  }
  if (pguidTimeFormat != nullptr && *pguidTimeFormat != GUID_NULL) {
    return MF_E_UNSUPPORTED_TIME_FORMAT;
  }

  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();

  if (SUCCEEDED(hr)) {
    hr = ValidatePresentationDescriptorLocked(pPresentationDescriptor);
  }

  bool anySelected = false;

  if (SUCCEEDED(hr)) {
    DWORD count = 0;
    hr = pPresentationDescriptor->GetStreamDescriptorCount(&count);
    for (DWORD i = 0; SUCCEEDED(hr) && i < count; ++i) {
      BOOL selected = FALSE;
      Microsoft::WRL::ComPtr<IMFStreamDescriptor> descriptor;
      hr = pPresentationDescriptor->GetStreamDescriptorByIndex(i, &selected,
                                                               &descriptor);
      if (FAILED(hr)) {
        break;
      }

      DWORD streamId = 0;
      hr = descriptor->GetStreamIdentifier(&streamId);
      if (FAILED(hr)) {
        break;
      }

      DWORD index = 0;
      bool wasSelected = false;
      hr = FindStreamDescriptorByIndexLocked(streamId, &index, &wasSelected);
      if (FAILED(hr)) {
        break;
      }

      Microsoft::WRL::ComPtr<SeewoMediaStream> stream =
          FindStreamByIdLocked(streamId);
      if (stream == nullptr) {
        hr = E_UNEXPECTED;
        break;
      }

      if (selected) {
        anySelected = true;
        hr = presentationDescriptor_->SelectStream(index);
        if (FAILED(hr)) {
          break;
        }

        Microsoft::WRL::ComPtr<IMFMediaTypeHandler> handler;
        Microsoft::WRL::ComPtr<IMFMediaType> mediaType;
        hr = descriptor->GetMediaTypeHandler(&handler);
        if (SUCCEEDED(hr)) {
          hr = handler->GetCurrentMediaType(&mediaType);
        }
        if (FAILED(hr)) {
          break;
        }

        // Tell the caller about the stream before it starts producing.
        Microsoft::WRL::ComPtr<IUnknown> unknown;
        hr = stream.As(&unknown);
        if (SUCCEEDED(hr)) {
          hr = eventQueue_->QueueEventParamUnk(
              firstStartDone_ ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK,
              unknown.Get());
        }
        if (FAILED(hr)) {
          break;
        }

        // Start() queues MEStreamStarted on the stream's own event queue.
        hr = stream->Start(mediaType.Get(), true);
        if (FAILED(hr)) {
          break;
        }
      } else if (wasSelected) {
        hr = presentationDescriptor_->DeselectStream(index);
        if (SUCCEEDED(hr)) {
          hr = stream->Stop(true);
        }
        if (FAILED(hr)) {
          break;
        }
      }
    }
  }

  if (SUCCEEDED(hr) && !anySelected) {
    // Nothing to play: Media Foundation treats this as a bad request.
    hr = MF_E_INVALIDREQUEST;
  }

  if (SUCCEEDED(hr)) {
    PROPVARIANT startTime;
    ::PropVariantInit(&startTime);
    startTime.vt = VT_I8;
    startTime.hVal.QuadPart = ::MFGetSystemTime();
    hr = eventQueue_->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK,
                                         &startTime);
    ::PropVariantClear(&startTime);
    if (SUCCEEDED(hr)) {
      state_ = SourceState::kStarted;
      firstStartDone_ = true;
    }
  }

  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

IFACEMETHODIMP SeewoMediaSource::Stop() {
  std::vector<Microsoft::WRL::ComPtr<SeewoMediaStream>> streams;

  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();
  if (SUCCEEDED(hr) && state_ != SourceState::kStarted) {
    hr = MF_E_INVALID_STATE_TRANSITION;
  }
  if (SUCCEEDED(hr)) {
    streams = streams_;
  }
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }

  // Each stream queues MEStreamStopped on its own queue.
  for (const auto& stream : streams) {
    if (stream != nullptr) {
      stream->Stop(true);
    }
  }

  ::AcquireSRWLockExclusive(&lock_);
  hr = CheckShutdownLocked();
  if (SUCCEEDED(hr)) {
    state_ = SourceState::kStopped;
    hr = eventQueue_->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK,
                                         nullptr);
  }
  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

HRESULT SeewoMediaSource::ValidatePresentationDescriptorLocked(
    IMFPresentationDescriptor* descriptor) {
  if (descriptor == nullptr) {
    return E_INVALIDARG;
  }

  DWORD count = 0;
  HRESULT hr = descriptor->GetStreamDescriptorCount(&count);
  if (FAILED(hr)) {
    return hr;
  }
  if (count != streams_.size()) {
    return MF_E_INVALIDREQUEST;
  }

  // Every stream in the descriptor must match one of ours, and at least one must
  // be selected.
  bool anySelected = false;
  for (DWORD i = 0; i < count; ++i) {
    BOOL selected = FALSE;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> streamDescriptor;
    hr = descriptor->GetStreamDescriptorByIndex(i, &selected, &streamDescriptor);
    if (FAILED(hr)) {
      return hr;
    }

    DWORD streamId = 0;
    hr = streamDescriptor->GetStreamIdentifier(&streamId);
    if (FAILED(hr)) {
      return hr;
    }

    DWORD index = 0;
    bool wasSelected = false;
    hr = FindStreamDescriptorByIndexLocked(streamId, &index, &wasSelected);
    if (FAILED(hr)) {
      return hr;
    }
    if (selected) {
      anySelected = true;
    }
  }

  return anySelected ? S_OK : MF_E_INVALIDREQUEST;
}

HRESULT SeewoMediaSource::FindStreamDescriptorByIndexLocked(DWORD streamId,
                                                            DWORD* index,
                                                            bool* selected) {
  if (index == nullptr || selected == nullptr) {
    return E_POINTER;
  }
  *index = 0;
  *selected = false;

  if (presentationDescriptor_ == nullptr) {
    return E_UNEXPECTED;
  }

  DWORD count = 0;
  HRESULT hr = presentationDescriptor_->GetStreamDescriptorCount(&count);
  if (FAILED(hr)) {
    return hr;
  }

  for (DWORD i = 0; i < count; ++i) {
    BOOL isSelected = FALSE;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> descriptor;
    hr = presentationDescriptor_->GetStreamDescriptorByIndex(i, &isSelected,
                                                             &descriptor);
    if (FAILED(hr)) {
      return hr;
    }

    DWORD id = 0;
    hr = descriptor->GetStreamIdentifier(&id);
    if (FAILED(hr)) {
      return hr;
    }

    if (id == streamId) {
      *index = i;
      *selected = (isSelected != FALSE);
      return S_OK;
    }
  }

  return MF_E_NOT_FOUND;
}

Microsoft::WRL::ComPtr<SeewoMediaStream>
SeewoMediaSource::FindStreamByIdLocked(DWORD streamId) const {
  for (const auto& stream : streams_) {
    if (stream != nullptr && stream->Id() == streamId) {
      return stream;
    }
  }
  return nullptr;
}

// ---------------------------------------------------------------------------
// IMFMediaSourceEx
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaSource::GetSourceAttributes(
    IMFAttributes** ppAttributes) {
  if (ppAttributes == nullptr) {
    return E_POINTER;
  }
  *ppAttributes = nullptr;

  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();
  if (SUCCEEDED(hr)) {
    if (attributes_ == nullptr) {
      hr = E_UNEXPECTED;
    } else {
      // The store is handed out directly: IMFMediaSourceEx documents these as
      // the live attribute stores, not copies.
      *ppAttributes = attributes_.Get();
      attributes_.Get()->AddRef();
    }
  }
  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

IFACEMETHODIMP SeewoMediaSource::GetStreamAttributes(
    DWORD dwStreamIdentifier, IMFAttributes** ppAttributes) {
  if (ppAttributes == nullptr) {
    return E_POINTER;
  }
  *ppAttributes = nullptr;

  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<SeewoMediaStream> stream;
  if (SUCCEEDED(hr)) {
    stream = FindStreamByIdLocked(dwStreamIdentifier);
    if (stream == nullptr) {
      hr = MF_E_NOT_FOUND;
    }
  }
  if (SUCCEEDED(hr)) {
    IMFAttributes* store = stream->Attributes();
    if (store == nullptr) {
      hr = E_UNEXPECTED;
    } else {
      store->AddRef();
      *ppAttributes = store;
    }
  }
  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

IFACEMETHODIMP SeewoMediaSource::SetD3DManager(IUnknown* pManager) {
  // The source produces CPU memory buffers, so there is no D3D manager to set.
  // Accepting and ignoring the call is correct here; the interface requires the
  // method to exist, and failing it would make the FrameServer refuse the
  // source.
  (void)pManager;
  return S_OK;
}

// ---------------------------------------------------------------------------
// IMFAttributes (delegated)
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaSource::GetItem(REFGUID guidKey,
                                         PROPVARIANT* pValue) {
  SEEWO_FORWARD_ATTR(GetItem(guidKey, pValue));
}

IFACEMETHODIMP SeewoMediaSource::GetItemType(REFGUID guidKey,
                                             MF_ATTRIBUTE_TYPE* pType) {
  SEEWO_FORWARD_ATTR(GetItemType(guidKey, pType));
}

IFACEMETHODIMP SeewoMediaSource::CompareItem(REFGUID guidKey,
                                             REFPROPVARIANT Value,
                                             BOOL* pbResult) {
  SEEWO_FORWARD_ATTR(CompareItem(guidKey, Value, pbResult));
}

IFACEMETHODIMP SeewoMediaSource::Compare(IMFAttributes* pTheirs,
                                         MF_ATTRIBUTES_MATCH_TYPE MatchType,
                                         BOOL* pbResult) {
  SEEWO_FORWARD_ATTR(Compare(pTheirs, MatchType, pbResult));
}

IFACEMETHODIMP SeewoMediaSource::GetUINT32(REFGUID guidKey, UINT32* punValue) {
  SEEWO_FORWARD_ATTR(GetUINT32(guidKey, punValue));
}

IFACEMETHODIMP SeewoMediaSource::GetUINT64(REFGUID guidKey, UINT64* punValue) {
  SEEWO_FORWARD_ATTR(GetUINT64(guidKey, punValue));
}

IFACEMETHODIMP SeewoMediaSource::GetDouble(REFGUID guidKey, double* pfValue) {
  SEEWO_FORWARD_ATTR(GetDouble(guidKey, pfValue));
}

IFACEMETHODIMP SeewoMediaSource::GetGUID(REFGUID guidKey, GUID* pguidValue) {
  SEEWO_FORWARD_ATTR(GetGUID(guidKey, pguidValue));
}

IFACEMETHODIMP SeewoMediaSource::GetStringLength(REFGUID guidKey,
                                                 UINT32* pcchLength) {
  SEEWO_FORWARD_ATTR(GetStringLength(guidKey, pcchLength));
}

IFACEMETHODIMP SeewoMediaSource::GetString(REFGUID guidKey, LPWSTR pwszValue,
                                           UINT32 cchBufSize,
                                           UINT32* pcchLength) {
  SEEWO_FORWARD_ATTR(GetString(guidKey, pwszValue, cchBufSize, pcchLength));
}

IFACEMETHODIMP SeewoMediaSource::GetAllocatedString(REFGUID guidKey,
                                                    LPWSTR* ppwszValue,
                                                    UINT32* pcchLength) {
  SEEWO_FORWARD_ATTR(GetAllocatedString(guidKey, ppwszValue, pcchLength));
}

IFACEMETHODIMP SeewoMediaSource::GetBlobSize(REFGUID guidKey,
                                             UINT32* pcbBlobSize) {
  SEEWO_FORWARD_ATTR(GetBlobSize(guidKey, pcbBlobSize));
}

IFACEMETHODIMP SeewoMediaSource::GetBlob(REFGUID guidKey, UINT8* pBuf,
                                         UINT32 cbBufSize, UINT32* pcbBlobSize) {
  SEEWO_FORWARD_ATTR(GetBlob(guidKey, pBuf, cbBufSize, pcbBlobSize));
}

IFACEMETHODIMP SeewoMediaSource::GetAllocatedBlob(REFGUID guidKey,
                                                  UINT8** ppBuf,
                                                  UINT32* pcbSize) {
  SEEWO_FORWARD_ATTR(GetAllocatedBlob(guidKey, ppBuf, pcbSize));
}

IFACEMETHODIMP SeewoMediaSource::GetUnknown(REFGUID guidKey, REFIID riid,
                                            LPVOID* ppv) {
  SEEWO_FORWARD_ATTR(GetUnknown(guidKey, riid, ppv));
}

IFACEMETHODIMP SeewoMediaSource::SetItem(REFGUID guidKey,
                                         REFPROPVARIANT Value) {
  SEEWO_FORWARD_ATTR(SetItem(guidKey, Value));
}

IFACEMETHODIMP SeewoMediaSource::DeleteItem(REFGUID guidKey) {
  SEEWO_FORWARD_ATTR(DeleteItem(guidKey));
}

IFACEMETHODIMP SeewoMediaSource::DeleteAllItems() {
  SEEWO_FORWARD_ATTR(DeleteAllItems());
}

IFACEMETHODIMP SeewoMediaSource::SetUINT32(REFGUID guidKey, UINT32 unValue) {
  SEEWO_FORWARD_ATTR(SetUINT32(guidKey, unValue));
}

IFACEMETHODIMP SeewoMediaSource::SetUINT64(REFGUID guidKey, UINT64 unValue) {
  SEEWO_FORWARD_ATTR(SetUINT64(guidKey, unValue));
}

IFACEMETHODIMP SeewoMediaSource::SetDouble(REFGUID guidKey, double fValue) {
  SEEWO_FORWARD_ATTR(SetDouble(guidKey, fValue));
}

IFACEMETHODIMP SeewoMediaSource::SetGUID(REFGUID guidKey, REFGUID guidValue) {
  SEEWO_FORWARD_ATTR(SetGUID(guidKey, guidValue));
}

IFACEMETHODIMP SeewoMediaSource::SetString(REFGUID guidKey, LPCWSTR wszValue) {
  SEEWO_FORWARD_ATTR(SetString(guidKey, wszValue));
}

IFACEMETHODIMP SeewoMediaSource::SetBlob(REFGUID guidKey, const UINT8* pBuf,
                                         UINT32 cbBufSize) {
  SEEWO_FORWARD_ATTR(SetBlob(guidKey, pBuf, cbBufSize));
}

IFACEMETHODIMP SeewoMediaSource::SetUnknown(REFGUID guidKey,
                                            IUnknown* pUnknown) {
  SEEWO_FORWARD_ATTR(SetUnknown(guidKey, pUnknown));
}

IFACEMETHODIMP SeewoMediaSource::LockStore() {
  SEEWO_FORWARD_ATTR(LockStore());
}

IFACEMETHODIMP SeewoMediaSource::UnlockStore() {
  SEEWO_FORWARD_ATTR(UnlockStore());
}

IFACEMETHODIMP SeewoMediaSource::GetCount(UINT32* pcItems) {
  SEEWO_FORWARD_ATTR(GetCount(pcItems));
}

IFACEMETHODIMP SeewoMediaSource::GetItemByIndex(UINT32 unIndex, GUID* pguidKey,
                                                PROPVARIANT* pValue) {
  SEEWO_FORWARD_ATTR(GetItemByIndex(unIndex, pguidKey, pValue));
}

IFACEMETHODIMP SeewoMediaSource::CopyAllItems(IMFAttributes* pDest) {
  SEEWO_FORWARD_ATTR(CopyAllItems(pDest));
}

#undef SEEWO_FORWARD_ATTR

// ---------------------------------------------------------------------------
// IMFGetService
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaSource::GetService(REFGUID guidService, REFIID riid,
                                            LPVOID* ppvObject) {
  if (ppvObject == nullptr) {
    return E_POINTER;
  }
  *ppvObject = nullptr;

  // Media Foundation's service model: the source is the top of the graph, so it
  // answers for the services it can provide and defers the rest to
  // MFGetService on itself.  The only service this source genuinely supports is
  // IID_IMFMediaSource itself, which MFGetService resolves through
  // QueryInterface.
  if (IsEqualGUID(guidService, MF_MEDIASOURCE_SERVICE) ||
      IsEqualGUID(guidService, IID_IMFMediaSource) ||
      IsEqualGUID(guidService, IID_IMFMediaSourceEx) ||
      IsEqualGUID(guidService, IID_IMFMediaEventGenerator) ||
      IsEqualGUID(guidService, IID_IMFAttributes)) {
    return QueryInterface(riid, ppvObject);
  }

  // IID_IUnknown / IID_IMFGetService / IID_IMFTopologyServiceLookup style
  // requests fall through to a plain QueryInterface attempt, which is what the
  // FrameServer expects for an in-process source.
  if (IsEqualGUID(guidService, IID_IUnknown) ||
      IsEqualGUID(guidService, IID_IMFGetService)) {
    return QueryInterface(riid, ppvObject);
  }

  return MF_E_UNSUPPORTED_SERVICE;
}

}  // namespace seewo
