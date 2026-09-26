// SeewoMediaStream.h
//
// The single video stream of the Seewo virtual camera.
//
// The stream is an IMFMediaStream2 (which brings IMFMediaStream and
// IMFMediaEventGenerator with it) plus an IMFMediaTypeHandler, so a caller can
// inspect and change the negotiated format directly on the stream as well as
// through the stream descriptor.
//
// WRL is given only the most derived interface of each chain (IMFMediaStream2,
// IMFMediaTypeHandler, IMFAsyncCallback); RuntimeClass's QueryInterface resolves
// the base interfaces of the listed ones too.
//
// Threading model
// ---------------
// RequestSample() is called on the FrameServer's pipeline thread and must not
// block.  It only records the request token and posts a work item to a private
// MF work queue; the actual frame generation happens on that queue's worker
// thread.  All mutable state is guarded by a single SRWLOCK.
#pragma once

#ifndef SEEWO_MEDIA_STREAM_H
#define SEEWO_MEDIA_STREAM_H

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <wrl.h>
#include <wrl/implements.h>

#include <vector>

#include "SeewoFrameSource.h"

namespace seewo {

class SeewoMediaSource;

// The WRL base.  Only the most derived interface of each chain is listed; base
// interfaces (IMFMediaStream, IMFMediaEventGenerator) are resolved by the
// explicit QueryInterface below.
using SeewoMediaStreamRuntimeClass = Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
    IMFMediaStream2,
    IMFMediaTypeHandler,
    IMFAsyncCallback>;

class SeewoMediaStream : public SeewoMediaStreamRuntimeClass {
 public:
  SeewoMediaStream() noexcept;
  ~SeewoMediaStream() override;

  // IUnknown.  Resolves the whole IMFMediaStream2 chain so a caller can ask for
  // IMFMediaStream or IMFMediaEventGenerator directly.
  IFACEMETHODIMP QueryInterface(REFIID riid, void** ppvObject) override;

  // Creates the media types, the stream descriptor, the event queue and the
  // private work queue.  `source` is stored as a weak back pointer (the source
  // owns the stream, so a strong reference would be a cycle).
  HRESULT Initialize(SeewoMediaSource* source, DWORD streamId);

  // --- IMFMediaEventGenerator ---------------------------------------------
  IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback,
                               IUnknown* punkState) override;
  IFACEMETHODIMP EndGetEvent(IMFAsyncResult* pResult,
                             IMFMediaEvent** ppEvent) override;
  IFACEMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
  IFACEMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType,
                            HRESULT hrStatus,
                            const PROPVARIANT* pvValue) override;

  // --- IMFMediaStream ------------------------------------------------------
  IFACEMETHODIMP GetMediaSource(IMFMediaSource** ppMediaSource) override;
  IFACEMETHODIMP GetStreamDescriptor(
      IMFStreamDescriptor** ppStreamDescriptor) override;
  IFACEMETHODIMP RequestSample(IUnknown* pToken) override;

  // --- IMFMediaStream2 -----------------------------------------------------
  IFACEMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
  IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* pState) override;

  // --- IMFMediaTypeHandler -------------------------------------------------
  // Delegated to the stream descriptor's handler, except that a successful
  // SetCurrentMediaType also reconfigures the frame source so the next sample
  // is produced in the new format.
  IFACEMETHODIMP GetMajorType(GUID* pguidMajorType) override;
  IFACEMETHODIMP IsMediaTypeSupported(IMFMediaType* pMediaType,
                                      IMFMediaType** ppMediaType) override;
  IFACEMETHODIMP GetMediaTypeCount(DWORD* pdwTypeCount) override;
  IFACEMETHODIMP GetMediaTypeByIndex(DWORD dwIndex,
                                     IMFMediaType** ppType) override;
  IFACEMETHODIMP SetCurrentMediaType(IMFMediaType* pMediaType) override;
  IFACEMETHODIMP GetCurrentMediaType(IMFMediaType** ppMediaType) override;

  // --- IMFAsyncCallback ----------------------------------------------------
  // The work item posted by RequestSample().
  IFACEMETHODIMP GetParameters(DWORD* pdwFlags, DWORD* pdwQueue) override;
  IFACEMETHODIMP Invoke(IMFAsyncResult* pAsyncResult) override;

  // --- non-interface -------------------------------------------------------
  DWORD Id() const { return streamId_; }

  // Selects the stream, adopts the media type, configures the frame source and
  // moves to MF_STREAM_STATE_RUNNING.  `sendEvent` controls whether
  // MEStreamStarted is queued.
  HRESULT Start(IMFMediaType* pMediaType, bool sendEvent);
  // Moves to MF_STREAM_STATE_STOPPED and deselects the stream.
  HRESULT Stop(bool sendEvent);
  // Idempotent.  Tears down the event queue, the work queue and the frame
  // source.  Safe to call from any thread.
  HRESULT Shutdown();
  // The stream's own attribute store (MF_DEVICESTREAM_* keys).
  IMFAttributes* Attributes() const { return attributes_.Get(); }

 private:
  // Caller must hold lock_.
  HRESULT CheckShutdownLocked() const;
  HRESULT CheckStreamStateLocked() const;
  // Builds the supported media types and the stream descriptor.
  HRESULT CreateStreamDescriptorLocked();
  HRESULT SetStreamAttributesLocked(IMFAttributes* store);
  HRESULT SetStreamDescriptorAttributesLocked(IMFAttributes* store);
  // Applies a media type to the frame source.  Caller must hold lock_.
  HRESULT ConfigureFrameSourceLocked(IMFMediaType* mediaType);
  // Reads MF_MT_FRAME_RATE (defaulting to 30/1) into frameDuration_.
  void UpdateFrameDurationLocked(IMFMediaType* mediaType);

  // Body of the work item.  Runs on the MF work queue thread.
  HRESULT ProduceSample();

  // The frame generator.  It has its own lock, so it is safe to touch from the
  // work-queue thread while the pipeline thread calls into the stream.
  SeewoFrameSource frameSource_;

  // Guards everything below.
  SRWLOCK lock_ = SRWLOCK_INIT;

  // Weak back pointer: the source owns this stream.
  SeewoMediaSource* parent_ = nullptr;

  Microsoft::WRL::ComPtr<IMFMediaEventQueue> eventQueue_;
  Microsoft::WRL::ComPtr<IMFStreamDescriptor> streamDescriptor_;
  Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
  Microsoft::WRL::ComPtr<IMFMediaTypeHandler> typeHandler_;
  Microsoft::WRL::ComPtr<IMFMediaType> currentType_;

  // Request tokens handed to RequestSample(), in arrival order.  The work item
  // pops one per run.
  std::vector<Microsoft::WRL::ComPtr<IUnknown>> tokens_;

  DWORD streamId_ = 0;
  DWORD workQueueId_ = 0;
  bool workQueueValid_ = false;
  // Thread currently inside Invoke().  Shutdown() must not wait for a work queue
  // to drain when it is called from that very thread, which happens when the
  // work item holds the last reference to the stream.
  DWORD workThreadId_ = 0;
  bool selected_ = false;
  bool shutdown_ = false;
  MF_STREAM_STATE state_ = MF_STREAM_STATE_STOPPED;

  LONGLONG frameDuration_ = 333333;  // 100 ns units, 30 fps
  LONGLONG nextSampleTime_ = 0;

  // Negotiated geometry, mirrored here so the sample buffer can be sized without
  // reaching into the frame source.
  UINT frameWidth_ = 0;
  UINT frameHeight_ = 0;
  FrameOutputFormat frameFormat_ = FrameOutputFormat::kNv12;
};

}  // namespace seewo

#endif  // SEEWO_MEDIA_STREAM_H
