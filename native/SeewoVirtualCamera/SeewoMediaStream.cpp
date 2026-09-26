// SeewoMediaStream.cpp
//
// See the header for the threading model.  The key point is that RequestSample()
// never generates pixels: it records the token and posts a work item, so the
// FrameServer's calling thread is never blocked by frame production.
//
#include <windows.h>

#include "SeewoKsGuid.h"

#include "SeewoMediaStream.h"

#include "SeewoMediaSource.h"

#include <mfobjects.h>
#include <mferror.h>

#include <algorithm>
#include <new>

namespace seewo {

namespace {

// 1920x1080 is the largest frame the shared-memory protocol can carry and is the
// natural default for a desktop camera.  720p is offered as a lighter
// alternative; the frame source scales whatever the producer publishes.
constexpr UINT kDefaultWidth = 1920;
constexpr UINT kDefaultHeight = 1080;
constexpr UINT kAltWidth = 1280;
constexpr UINT kAltHeight = 720;
constexpr UINT kFrameRateNumerator = 30;
constexpr UINT kFrameRateDenominator = 1;

// Initial capacity of the stream's attribute store; it grows on demand.
constexpr UINT32 kStreamAttributeCount = 8;

// Builds one complete video media type.
HRESULT CreateVideoType(REFGUID subtype, UINT width, UINT height,
                        IMFMediaType** type) {
  Microsoft::WRL::ComPtr<IMFMediaType> mediaType;
  HRESULT hr = ::MFCreateMediaType(&mediaType);
  if (FAILED(hr)) {
    return hr;
  }

  hr = mediaType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
  if (SUCCEEDED(hr)) {
    hr = mediaType->SetGUID(MF_MT_SUBTYPE, subtype);
  }
  if (SUCCEEDED(hr)) {
    hr = mediaType->SetUINT32(MF_MT_INTERLACE_MODE,
                              MFVideoInterlace_Progressive);
  }
  if (SUCCEEDED(hr)) {
    hr = mediaType->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
  }
  if (SUCCEEDED(hr)) {
    hr = MFSetAttributeSize(mediaType.Get(), MF_MT_FRAME_SIZE, width, height);
  }
  if (SUCCEEDED(hr)) {
    hr = MFSetAttributeRatio(mediaType.Get(), MF_MT_FRAME_RATE,
                             kFrameRateNumerator, kFrameRateDenominator);
  }
  if (SUCCEEDED(hr)) {
    hr = MFSetAttributeRatio(mediaType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
  }
  // frame bytes * 8 bits * frame rate
  const UINT32 frameBytes = (subtype == MFVideoFormat_NV12)
                                ? (width * height * 3u) / 2u
                                : width * height * 4u;

  if (SUCCEEDED(hr)) {
    hr = mediaType->SetUINT32(MF_MT_AVG_BITRATE,
                              frameBytes * 8u * kFrameRateNumerator);
  }
  if (SUCCEEDED(hr)) {
    // MF_MT_DEFAULT_STRIDE is deliberately not set.  Its sign convention for
    // RGB formats (positive = bottom-up) is easy to get backwards, and Media
    // Foundation computes a correct value for a progressive, all-samples-
    // independent type.  The frame source handles either scanline order when it
    // writes into the sample buffer.
    //
    // MF_MT_SAMPLE_SIZE is only meaningful alongside MF_MT_FIXED_SIZE_SAMPLES, so
    // both are set together.
    hr = mediaType->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
    if (SUCCEEDED(hr)) {
      hr = mediaType->SetUINT32(MF_MT_SAMPLE_SIZE, frameBytes);
    }
  }
  if (SUCCEEDED(hr)) {
    *type = mediaType.Detach();
  }
  return hr;
}

// Applies the standard "this is a capture stream" attributes to an attribute
// store.  PINNAME_VIDEO_CAPTURE and the MF_DEVICESTREAM_* keys come from
// ksmedia.h / mfidl.h and are what the FrameServer keys off when deciding that a
// stream is a colour capture stream.
HRESULT ApplyCaptureStreamAttributes(IMFAttributes* store, DWORD streamId) {
  if (store == nullptr) {
    return E_INVALIDARG;
  }

  HRESULT hr = store->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY,
                              PINNAME_VIDEO_CAPTURE);
  if (SUCCEEDED(hr)) {
    hr = store->SetUINT32(MF_DEVICESTREAM_STREAM_ID, streamId);
  }
  if (SUCCEEDED(hr)) {
    hr = store->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
  }
  if (SUCCEEDED(hr)) {
    hr = store->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES,
                          static_cast<UINT32>(MFFrameSourceTypes_Color));
  }
  return hr;
}

}  // namespace

SeewoMediaStream::SeewoMediaStream() noexcept {}

SeewoMediaStream::~SeewoMediaStream() {
  // Defensive: the source normally calls Shutdown() explicitly, but a caller
  // that drops the last reference without doing so must not leak the work queue.
  Shutdown();
}

HRESULT SeewoMediaStream::Initialize(SeewoMediaSource* source, DWORD streamId) {
  if (source == nullptr) {
    return E_INVALIDARG;
  }

  ::AcquireSRWLockExclusive(&lock_);

  parent_ = source;
  streamId_ = streamId;

  HRESULT hr = CreateStreamDescriptorLocked();

  if (SUCCEEDED(hr)) {
    hr = ::MFCreateAttributes(&attributes_, kStreamAttributeCount);
  }
  if (SUCCEEDED(hr)) {
    hr = SetStreamAttributesLocked(attributes_.Get());
  }
  if (SUCCEEDED(hr)) {
    hr = ::MFCreateEventQueue(&eventQueue_);
  }
  if (SUCCEEDED(hr)) {
    // A private work queue for frame production.
    //
    // The two APIs have different shapes:
    //   HRESULT MFAllocateSerialWorkQueue(DWORD dwWorkQueue, DWORD* pdwWorkQueue)
    //   HRESULT MFAllocateWorkQueue(DWORD* pdwWorkQueue)
    // Both return an HRESULT and write the identifier through an out-parameter.
    //
    // A serial queue is preferred: it serialises work items FIFO on top of a
    // multithreaded pool, so frame production cannot stall Media Foundation's
    // own callbacks and two streams can never race.
    hr = ::MFAllocateSerialWorkQueue(MFASYNC_CALLBACK_QUEUE_MULTITHREADED,
                                     &workQueueId_);
    workQueueValid_ = SUCCEEDED(hr) && workQueueId_ != 0;

    if (!workQueueValid_) {
      // Fall back to a plain work queue if a dedicated serial one could not be
      // created; MFPutWorkItem works with either.
      DWORD fallbackQueueId = 0;
      const HRESULT hrQueue = ::MFAllocateWorkQueue(&fallbackQueueId);
      if (SUCCEEDED(hrQueue) && fallbackQueueId != 0) {
        workQueueId_ = fallbackQueueId;
        workQueueValid_ = true;
        hr = S_OK;
      } else {
        hr = FAILED(hrQueue) ? hrQueue : E_FAIL;
      }
    }
  }

  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

HRESULT SeewoMediaStream::CreateStreamDescriptorLocked() {
  // Two subtypes at two sizes gives four entry points, which is what most
  // virtual camera consumers expect to be able to enumerate.
  const struct {
    REFGUID subtype;
    UINT width;
    UINT height;
  } kTypes[] = {
      {MFVideoFormat_NV12, kDefaultWidth, kDefaultHeight},
      {MFVideoFormat_RGB32, kDefaultWidth, kDefaultHeight},
      {MFVideoFormat_NV12, kAltWidth, kAltHeight},
      {MFVideoFormat_RGB32, kAltWidth, kAltHeight},
  };
  // ARRAYSIZE yields a size_t; narrow it explicitly so /W4 does not report a
  // possible loss of data.
  const DWORD count = static_cast<DWORD>(ARRAYSIZE(kTypes));

  std::vector<Microsoft::WRL::ComPtr<IMFMediaType>> types;
  std::vector<IMFMediaType*> raw;
  try {
    types.resize(count);
    raw.resize(count, nullptr);
  } catch (const std::bad_alloc&) {
    return E_OUTOFMEMORY;
  }

  for (DWORD i = 0; i < count; ++i) {
    HRESULT hr = CreateVideoType(kTypes[i].subtype, kTypes[i].width,
                                 kTypes[i].height, &types[i]);
    if (FAILED(hr)) {
      return hr;
    }
    raw[i] = types[i].Get();
  }

  HRESULT hr = ::MFCreateStreamDescriptor(streamId_, count, raw.data(),
                                          &streamDescriptor_);
  if (FAILED(hr)) {
    return hr;
  }

  hr = streamDescriptor_->GetMediaTypeHandler(&typeHandler_);
  if (FAILED(hr)) {
    return hr;
  }

  // The first type (1920x1080 NV12) is the default.  It is also the layout the
  // shared-memory producer uses, so the common case needs no scaling.
  hr = typeHandler_->SetCurrentMediaType(raw[0]);
  if (FAILED(hr)) {
    return hr;
  }
  currentType_ = types[0];

  return SetStreamDescriptorAttributesLocked(streamDescriptor_.Get());
}

HRESULT SeewoMediaStream::SetStreamAttributesLocked(IMFAttributes* store) {
  return ApplyCaptureStreamAttributes(store, streamId_);
}

HRESULT SeewoMediaStream::SetStreamDescriptorAttributesLocked(
    IMFAttributes* store) {
  return ApplyCaptureStreamAttributes(store, streamId_);
}

HRESULT SeewoMediaStream::ConfigureFrameSourceLocked(IMFMediaType* mediaType) {
  if (mediaType == nullptr) {
    return E_INVALIDARG;
  }

  GUID subtype = GUID_NULL;
  HRESULT hr = mediaType->GetGUID(MF_MT_SUBTYPE, &subtype);
  if (FAILED(hr)) {
    return hr;
  }

  UINT32 width = 0;
  UINT32 height = 0;
  hr = MFGetAttributeSize(mediaType, MF_MT_FRAME_SIZE, &width, &height);
  if (FAILED(hr)) {
    return hr;
  }
  if (width == 0 || height == 0) {
    return MF_E_INVALIDMEDIATYPE;
  }

  hr = frameSource_.Configure(width, height, subtype);
  if (FAILED(hr)) {
    return hr;
  }

  frameWidth_ = width;
  frameHeight_ = height;
  frameFormat_ = (subtype == MFVideoFormat_RGB32) ? FrameOutputFormat::kRgb32
                                                  : FrameOutputFormat::kNv12;
  UpdateFrameDurationLocked(mediaType);
  return S_OK;
}

void SeewoMediaStream::UpdateFrameDurationLocked(IMFMediaType* mediaType) {
  UINT32 numerator = 0;
  UINT32 denominator = 0;
  LONGLONG duration = frameDuration_;

  if (mediaType != nullptr &&
      SUCCEEDED(MFGetAttributeRatio(mediaType, MF_MT_FRAME_RATE, &numerator,
                                    &denominator)) &&
      numerator != 0 && denominator != 0) {
    // 100 ns units per frame.
    duration = (10000000LL * denominator) / numerator;
  }

  if (duration <= 0) {
    duration = 333333;
  }
  frameDuration_ = duration;
}

// ---------------------------------------------------------------------------
// IMFMediaEventGenerator
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaStream::BeginGetEvent(IMFAsyncCallback* pCallback,
                                               IUnknown* punkState) {
  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue = eventQueue_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (SUCCEEDED(hr)) {
    hr = queue->BeginGetEvent(pCallback, punkState);
  }
  return hr;
}

IFACEMETHODIMP SeewoMediaStream::EndGetEvent(IMFAsyncResult* pResult,
                                             IMFMediaEvent** ppEvent) {
  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue = eventQueue_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (SUCCEEDED(hr)) {
    hr = queue->EndGetEvent(pResult, ppEvent);
  }
  return hr;
}

IFACEMETHODIMP SeewoMediaStream::GetEvent(DWORD dwFlags,
                                          IMFMediaEvent** ppEvent) {
  // GetEvent may block until an event arrives, so the lock must not be held
  // across the call.  Take a reference to the queue under the lock and use it
  // outside.
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

IFACEMETHODIMP SeewoMediaStream::QueueEvent(MediaEventType met,
                                            REFGUID guidExtendedType,
                                            HRESULT hrStatus,
                                            const PROPVARIANT* pvValue) {
  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue = eventQueue_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (SUCCEEDED(hr)) {
    hr = queue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue);
  }
  return hr;
}

// ---------------------------------------------------------------------------
// IMFMediaStream
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaStream::GetMediaSource(IMFMediaSource** ppMediaSource) {
  if (ppMediaSource == nullptr) {
    return E_POINTER;
  }
  *ppMediaSource = nullptr;

  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  SeewoMediaSource* parent = parent_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  if (parent == nullptr) {
    return E_UNEXPECTED;
  }

  // The source is a classic-COM WRL object, so QueryInterface through its
  // IUnknown works from here.
  return parent->QueryInterface(IID_PPV_ARGS(ppMediaSource));
}

IFACEMETHODIMP SeewoMediaStream::GetStreamDescriptor(
    IMFStreamDescriptor** ppStreamDescriptor) {
  if (ppStreamDescriptor == nullptr) {
    return E_POINTER;
  }
  *ppStreamDescriptor = nullptr;

  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFStreamDescriptor> descriptor = streamDescriptor_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  if (descriptor == nullptr) {
    return E_UNEXPECTED;
  }

  *ppStreamDescriptor = descriptor.Detach();
  return S_OK;
}

IFACEMETHODIMP SeewoMediaStream::RequestSample(IUnknown* pToken) {
  ::AcquireSRWLockExclusive(&lock_);

  HRESULT hr = CheckShutdownLocked();
  if (SUCCEEDED(hr)) {
    hr = CheckStreamStateLocked();
  }
  if (SUCCEEDED(hr) && !workQueueValid_) {
    hr = E_UNEXPECTED;
  }

  bool queued = false;
  if (SUCCEEDED(hr)) {
    try {
      tokens_.push_back(Microsoft::WRL::ComPtr<IUnknown>(pToken));
      queued = true;
    } catch (const std::bad_alloc&) {
      hr = E_OUTOFMEMORY;
    }
  }

  if (SUCCEEDED(hr)) {
    // Post the work item while still holding the lock: this guarantees that the
    // token is in the queue before the worker can run, and keeps request order.
    hr = ::MFPutWorkItem(workQueueId_, this, nullptr);
    if (FAILED(hr) && queued) {
      // The post failed, so nothing will ever consume the token.  Take it back
      // out rather than letting a later request produce an extra sample.
      tokens_.pop_back();
    }
  }

  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

// ---------------------------------------------------------------------------
// IMFMediaStream2
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaStream::SetStreamState(MF_STREAM_STATE state) {
  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();

  if (SUCCEEDED(hr)) {
    if (state == state_) {
      ::ReleaseSRWLockExclusive(&lock_);
      return S_OK;
    }

    switch (state) {
      case MF_STREAM_STATE_RUNNING:
        // Running requires a negotiated media type and a selected stream; the
        // source selects and configures the stream during Start(), so reaching
        // here without one is a caller error.
        if (!selected_ || currentType_ == nullptr) {
          hr = MF_E_INVALIDREQUEST;
        } else {
          hr = ConfigureFrameSourceLocked(currentType_.Get());
          if (SUCCEEDED(hr)) {
            state_ = MF_STREAM_STATE_RUNNING;
            nextSampleTime_ = 0;
          }
        }
        break;

      case MF_STREAM_STATE_PAUSED:
        // The spec only allows Running -> Paused.  Stopped -> Paused is a
        // caller error.
        if (state_ != MF_STREAM_STATE_RUNNING) {
          hr = MF_E_INVALID_STATE_TRANSITION;
        } else {
          state_ = MF_STREAM_STATE_PAUSED;
        }
        break;

      case MF_STREAM_STATE_STOPPED:
        state_ = MF_STREAM_STATE_STOPPED;
        selected_ = false;
        tokens_.clear();
        break;

      default:
        hr = MF_E_INVALID_STATE_TRANSITION;
        break;
    }
  }

  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

IFACEMETHODIMP SeewoMediaStream::GetStreamState(MF_STREAM_STATE* pState) {
  if (pState == nullptr) {
    return E_POINTER;
  }

  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  if (SUCCEEDED(hr)) {
    *pState = state_;
  }
  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

// ---------------------------------------------------------------------------
// IMFMediaTypeHandler
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaStream::GetMajorType(GUID* pguidMajorType) {
  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaTypeHandler> handler = typeHandler_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  if (handler == nullptr) {
    return E_UNEXPECTED;
  }
  return handler->GetMajorType(pguidMajorType);
}

IFACEMETHODIMP SeewoMediaStream::IsMediaTypeSupported(IMFMediaType* pMediaType,
                                                      IMFMediaType** ppMediaType) {
  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaTypeHandler> handler = typeHandler_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  if (handler == nullptr) {
    return E_UNEXPECTED;
  }
  return handler->IsMediaTypeSupported(pMediaType, ppMediaType);
}

IFACEMETHODIMP SeewoMediaStream::GetMediaTypeCount(DWORD* pdwTypeCount) {
  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaTypeHandler> handler = typeHandler_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  if (handler == nullptr) {
    return E_UNEXPECTED;
  }
  return handler->GetMediaTypeCount(pdwTypeCount);
}

IFACEMETHODIMP SeewoMediaStream::GetMediaTypeByIndex(DWORD dwIndex,
                                                     IMFMediaType** ppType) {
  ::AcquireSRWLockExclusive(&lock_);
  const HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaTypeHandler> handler = typeHandler_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  if (handler == nullptr) {
    return E_UNEXPECTED;
  }
  return handler->GetMediaTypeByIndex(dwIndex, ppType);
}

IFACEMETHODIMP SeewoMediaStream::SetCurrentMediaType(IMFMediaType* pMediaType) {
  if (pMediaType == nullptr) {
    return E_POINTER;
  }

  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();

  if (SUCCEEDED(hr)) {
    if (state_ == MF_STREAM_STATE_RUNNING) {
      // Changing format mid-stream would tear the sample geometry out from under
      // an in-flight work item.
      hr = MF_E_INVALIDREQUEST;
    } else if (typeHandler_ == nullptr) {
      hr = E_UNEXPECTED;
    } else {
      hr = typeHandler_->SetCurrentMediaType(pMediaType);
      if (SUCCEEDED(hr)) {
        hr = ConfigureFrameSourceLocked(pMediaType);
      }
      if (SUCCEEDED(hr)) {
        currentType_ = pMediaType;
      }
    }
  }

  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

IFACEMETHODIMP SeewoMediaStream::GetCurrentMediaType(IMFMediaType** ppMediaType) {
  if (ppMediaType == nullptr) {
    return E_POINTER;
  }
  *ppMediaType = nullptr;

  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();
  Microsoft::WRL::ComPtr<IMFMediaType> current;
  if (SUCCEEDED(hr)) {
    current = currentType_;
  }
  ::ReleaseSRWLockExclusive(&lock_);

  if (FAILED(hr)) {
    return hr;
  }
  if (current == nullptr) {
    return MF_E_NO_MORE_TYPES;
  }

  *ppMediaType = current.Detach();
  return S_OK;
}

// ---------------------------------------------------------------------------
// IMFAsyncCallback (the work item)
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaStream::GetParameters(DWORD* pdwFlags,
                                               DWORD* pdwQueue) {
  if (pdwFlags == nullptr || pdwQueue == nullptr) {
    return E_POINTER;
  }
  // No special dispatch flags: this callback does ordinary work on an ordinary
  // work-queue thread.
  *pdwFlags = 0;
  *pdwQueue = workQueueId_;
  return S_OK;
}

IFACEMETHODIMP SeewoMediaStream::Invoke(IMFAsyncResult* pAsyncResult) {
  // The result object is unused: everything needed is in the token queue.
  (void)pAsyncResult;

  // Record the worker thread so a Shutdown() that happens to run on it (which is
  // what occurs when the work item holds the last reference) does not try to
  // wait for its own work queue to drain.
  ::AcquireSRWLockExclusive(&lock_);
  const DWORD previousThreadId = workThreadId_;
  workThreadId_ = ::GetCurrentThreadId();
  ::ReleaseSRWLockExclusive(&lock_);

  const HRESULT hr = ProduceSample();

  ::AcquireSRWLockExclusive(&lock_);
  workThreadId_ = previousThreadId;
  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

// ---------------------------------------------------------------------------
// non-interface
// ---------------------------------------------------------------------------

HRESULT SeewoMediaStream::Start(IMFMediaType* pMediaType, bool sendEvent) {
  if (pMediaType == nullptr) {
    return E_INVALIDARG;
  }

  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();

  if (SUCCEEDED(hr)) {
    selected_ = true;
    if (currentType_ == nullptr) {
      currentType_ = pMediaType;
    }
    hr = ConfigureFrameSourceLocked(pMediaType);
  }

  if (SUCCEEDED(hr)) {
    state_ = MF_STREAM_STATE_RUNNING;
    nextSampleTime_ = 0;
  }

  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue = eventQueue_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (SUCCEEDED(hr) && sendEvent) {
    hr = queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
  }
  return hr;
}

HRESULT SeewoMediaStream::Stop(bool sendEvent) {
  ::AcquireSRWLockExclusive(&lock_);
  HRESULT hr = CheckShutdownLocked();

  if (SUCCEEDED(hr)) {
    state_ = MF_STREAM_STATE_STOPPED;
    selected_ = false;
    tokens_.clear();
  }

  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue = eventQueue_;
  ::ReleaseSRWLockExclusive(&lock_);

  if (SUCCEEDED(hr) && sendEvent && queue != nullptr) {
    hr = queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
  }
  return hr;
}

HRESULT SeewoMediaStream::Shutdown() {
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue;
  bool workQueueValid = false;
  DWORD workQueueId = 0;
  bool onWorkThread = false;

  ::AcquireSRWLockExclusive(&lock_);
  if (!shutdown_) {
    shutdown_ = true;
    state_ = MF_STREAM_STATE_STOPPED;
    selected_ = false;
    parent_ = nullptr;
    tokens_.clear();

    queue = eventQueue_;
    eventQueue_.Reset();

    workQueueValid = workQueueValid_;
    workQueueId = workQueueId_;
    workQueueValid_ = false;
    onWorkThread = (workThreadId_ != 0 &&
                    workThreadId_ == ::GetCurrentThreadId());

    streamDescriptor_.Reset();
    typeHandler_.Reset();
    currentType_.Reset();
    attributes_.Reset();
  }
  ::ReleaseSRWLockExclusive(&lock_);

  if (queue != nullptr) {
    queue->Shutdown();
  }

  // Unlocking the work queue makes Media Foundation release its reference to
  // this callback object and drain queued items.  It must be skipped when this
  // call is running on the work thread itself, because the queue would then be
  // waiting for the very call that is unlocking it.
  if (workQueueValid && !onWorkThread) {
    ::MFUnlockWorkQueue(workQueueId);
  }

  frameSource_.Shutdown();
  return S_OK;
}

HRESULT SeewoMediaStream::CheckShutdownLocked() const {
  return shutdown_ ? MF_E_SHUTDOWN : S_OK;
}

// ---------------------------------------------------------------------------
// IUnknown
// ---------------------------------------------------------------------------

IFACEMETHODIMP SeewoMediaStream::QueryInterface(REFIID riid, void** ppvObject) {
  if (ppvObject == nullptr) {
    return E_POINTER;
  }
  *ppvObject = nullptr;

  // IMFMediaStream2 -> IMFMediaStream -> IMFMediaEventGenerator, plus the
  // IMFMediaTypeHandler and IMFAsyncCallback faces of this object.
  if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IMFMediaStream2)) {
    *ppvObject = static_cast<IMFMediaStream2*>(this);
  } else if (IsEqualIID(riid, IID_IMFMediaStream)) {
    *ppvObject = static_cast<IMFMediaStream*>(this);
  } else if (IsEqualIID(riid, IID_IMFMediaEventGenerator)) {
    *ppvObject = static_cast<IMFMediaEventGenerator*>(this);
  } else if (IsEqualIID(riid, IID_IMFMediaTypeHandler)) {
    *ppvObject = static_cast<IMFMediaTypeHandler*>(this);
  } else if (IsEqualIID(riid, IID_IMFAsyncCallback)) {
    *ppvObject = static_cast<IMFAsyncCallback*>(this);
  } else {
    return E_NOINTERFACE;
  }

  AddRef();
  return S_OK;
}

HRESULT SeewoMediaStream::CheckStreamStateLocked() const {
  // Only a running stream produces samples.  Paused and stopped streams reject
  // the request, which is how the source signals back-pressure to the pipeline.
  if (state_ != MF_STREAM_STATE_RUNNING || !selected_) {
    return MF_E_INVALIDREQUEST;
  }
  return S_OK;
}

HRESULT SeewoMediaStream::ProduceSample() {
  // Snapshot the state needed to build the sample.  If the stream stopped or was
  // shut down between the request and this work item running, silently drop it:
  // the pipeline will re-request when it wants more.
  Microsoft::WRL::ComPtr<IUnknown> token;
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue;
  LONGLONG sampleTime = 0;
  LONGLONG duration = 0;
  UINT width = 0;
  UINT height = 0;
  FrameOutputFormat format = FrameOutputFormat::kNv12;
  bool produce = false;

  ::AcquireSRWLockExclusive(&lock_);
  if (!shutdown_ && state_ == MF_STREAM_STATE_RUNNING && selected_ &&
      !tokens_.empty()) {
    token = tokens_.front();
    // pop_front() on a vector is a move of the whole tail; the queue is at most
    // a handful of entries deep (one per outstanding pipeline request), so this
    // is cheaper than a std::deque here and avoids another allocation.
    tokens_.erase(tokens_.begin());
    queue = eventQueue_;
    duration = frameDuration_;
    width = frameWidth_;
    height = frameHeight_;
    format = frameFormat_;
    // Timestamps come from MFGetSystemTime() and advance by exactly one frame
    // interval per sample so playback stays smooth even if production jitters.
    if (nextSampleTime_ == 0) {
      nextSampleTime_ = ::MFGetSystemTime();
    }
    sampleTime = nextSampleTime_;
    nextSampleTime_ += frameDuration_;
    produce = (width != 0 && height != 0 && queue != nullptr);
  }
  ::ReleaseSRWLockExclusive(&lock_);

  if (!produce) {
    return S_OK;
  }

  // A zero-sized frame would make the buffer size meaningless; the state machine
  // prevents it, but check anyway so a bad media type cannot underflow the math
  // below.
  if (width == 0 || height == 0) {
    return MF_E_INVALIDMEDIATYPE;
  }

  const DWORD bufferBytes =
      (format == FrameOutputFormat::kRgb32)
          ? (width * height * 4u)
          : (width * height * 3u) / 2u;

  Microsoft::WRL::ComPtr<IMFMediaBuffer> buffer;
  HRESULT hr = ::MFCreateMemoryBuffer(bufferBytes, &buffer);
  if (FAILED(hr)) {
    queue->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr);
    return S_OK;
  }

  Microsoft::WRL::ComPtr<IMFSample> sample;
  hr = ::MFCreateSample(&sample);
  if (SUCCEEDED(hr)) {
    hr = sample->AddBuffer(buffer.Get());
  }
  if (SUCCEEDED(hr)) {
    hr = frameSource_.ProduceFrame(sample.Get(), sampleTime, duration);
  }
  if (SUCCEEDED(hr) && token != nullptr) {
    // The token must ride along on the sample; the pipeline matches requests to
    // samples with it.
    hr = sample->SetUnknown(MFSampleExtension_Token, token.Get());
  }
  if (SUCCEEDED(hr)) {
    hr = sample->SetSampleTime(sampleTime);
  }
  if (SUCCEEDED(hr)) {
    hr = sample->SetSampleDuration(duration);
  }
  if (SUCCEEDED(hr)) {
    hr = queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
  }

  if (FAILED(hr)) {
    // Report the failure downstream rather than dropping the request silently,
    // so the pipeline can decide whether to retry or stop.
    queue->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr);
  }

  return S_OK;
}

}  // namespace seewo
