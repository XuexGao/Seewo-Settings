// SeewoFrameSource.h
//
// Frame production for the Seewo virtual camera.
//
// Two sources of pixels, in priority order:
//
//   1. A live producer publishing BGRA frames through the shared section
//      described in SeewoIpc.h (seewo::kVcamSectionName, falling back to the
//      Local\ variant).  This is the normal case: the Seewo assistant app runs
//      in the interactive session and pushes captured desktop frames.
//
//   2. A synthesised, visibly animated test pattern, used whenever no producer
//      is attached, the header is invalid, or the last published frame is older
//      than two seconds.  The pattern is generated directly at the negotiated
//      output size so it never has to be scaled.
//
// Live frames are converted to the negotiated output subtype, with a bilinear
// scaler for the case where the producer's frame size differs from the
// negotiated size.
//
// This class owns no COM objects; the stream owns an instance and calls
// ProduceFrame() from its worker thread.
#pragma once

#ifndef SEEWO_FRAME_SOURCE_H
#define SEEWO_FRAME_SOURCE_H

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>

#include <cstdint>
#include <vector>

#include "SeewoIpc.h"

namespace seewo {

// The output subtypes this source can negotiate.
enum class FrameOutputFormat {
  kRgb32,  // MFVideoFormat_RGB32 - BGRA byte order in memory
  kNv12,   // MFVideoFormat_NV12  - Y plane then interleaved U/V plane
};

class SeewoFrameSource {
 public:
  SeewoFrameSource() = default;
  ~SeewoFrameSource();

  SeewoFrameSource(const SeewoFrameSource&) = delete;
  SeewoFrameSource& operator=(const SeewoFrameSource&) = delete;

  // Fixes the output geometry and pixel format.  Safe to call again when the
  // negotiated media type changes; all scratch buffers are reallocated.
  HRESULT Configure(UINT width, UINT height, REFGUID subtype);

  // True once Configure() has succeeded.
  bool IsConfigured() const;

  // Produces exactly one frame into `sample`, stamping it with `sampleTime` and
  // `duration`.  Never blocks for long: the shared section is only read, and a
  // missing producer simply falls back to the test pattern.
  HRESULT ProduceFrame(IMFSample* sample, LONGLONG sampleTime, LONGLONG duration);

  // Releases the shared section view.  Idempotent.
  void Shutdown();

  // Diagnostics for the README / debugging: how many frames came from a live
  // producer versus the built-in pattern.
  uint64_t liveFrameCount() const;
  uint64_t patternFrameCount() const;

 private:
  // --- shared section -------------------------------------------------------
  // Opens the section if it is not already open.  Failure is expected and not
  // fatal: it just means "no producer yet".
  void EnsureChannelLocked();
  // Drops the current view so the next EnsureChannelLocked() reopens it.  Used
  // when the producer restarts and the old mapping goes stale.
  void DropChannelLocked();
  // Copies the newest valid producer frame into m_outputBgra, scaling if the
  // producer geometry differs from the output geometry.  Returns true when a
  // usable live frame was obtained.
  bool TryTakeLiveFrameLocked();

  // --- synthesis ------------------------------------------------------------
  // Renders the animated test pattern straight into m_outputBgra at the output
  // geometry.
  void RenderTestPatternLocked(uint64_t tickMs);

  // --- conversion -----------------------------------------------------------
  // Bilinear scale of a BGRA image into m_outputBgra.  A same-size request is a
  // row-wise copy.
  void ScaleBgraLocked(const uint8_t* src, UINT srcWidth, UINT srcHeight,
                       int srcStride);
  // Writes m_outputBgra into the sample, as RGB32 or NV12.
  HRESULT CopyOutputToSampleLocked(IMFSample* sample) const;

  SRWLOCK lock_ = SRWLOCK_INIT;

  bool configured_ = false;
  bool shutdown_ = false;
  UINT width_ = 0;
  UINT height_ = 0;
  FrameOutputFormat format_ = FrameOutputFormat::kRgb32;

  // The canonical output image, always width_ * height_ * 4 bytes, top-down BGRA.
  std::vector<uint8_t> outputBgra_;

  // The shared section, opened lazily.
  ChannelHandle channel_;
  // Tick count of the last attempt to open the section, so a missing producer
  // does not cost a CreateFileMapping call on every frame.
  uint64_t lastOpenAttemptMs_ = 0;
  // Consecutive frames that did not yield a live frame.  After enough of them
  // the section is dropped and reopened, which is how a producer restart is
  // detected.
  uint32_t consecutiveMisses_ = 0;

  // Animation state for the test pattern, driven off the system tick count.
  uint64_t patternFrame_ = 0;
  uint64_t lastPatternTickMs_ = 0;

  uint64_t liveFrames_ = 0;
  uint64_t patternFrames_ = 0;
};

}  // namespace seewo

#endif  // SEEWO_FRAME_SOURCE_H
