// SeewoFrameSource.cpp
//
// Frame production: live shared-memory frames when a producer is attached, and
// an animated test pattern otherwise.  See the header for the design notes.
#include "SeewoFrameSource.h"

#include <mfobjects.h>
#include <mferror.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstring>
#include <new>

namespace seewo {

namespace {

// How long a published frame stays "fresh".  Beyond this the producer is
// considered gone (crashed, paused, or the UI closed) and the pattern takes over
// so that applications never see a frozen camera.
constexpr uint64_t kFrameStaleMs = 2000;

// Minimum gap between attempts to open a section that is not currently mapped.
constexpr uint64_t kReopenIntervalMs = 500;

// Number of consecutive frames without a live frame before the mapping is
// dropped and reopened.  At 30 fps this is about two seconds, matching
// kFrameStaleMs.
constexpr uint32_t kMissesBeforeReopen = 60;

// Test pattern layout: the top two thirds are colour bars, the bottom third is a
// greyscale ramp.
inline UINT ColorBarHeight(UINT height) { return (height * 2u) / 3u; }

// 75% amplitude SMPTE-style bars, stored as 0xAARRGGBB.
constexpr uint32_t kColorBars75[7] = {
    0xFFBFBFBF,  // 75% white
    0xFFBFBF00,  // 75% yellow
    0xFF00BFBF,  // 75% cyan
    0xFF00BF00,  // 75% green
    0xFFBF00BF,  // 75% magenta
    0xFFBF0000,  // 75% red
    0xFF0000BF,  // 75% blue
};
constexpr UINT kColorBarCount = 7;

// Writes one BGRA pixel from a 0xAARRGGBB value.
inline void StoreBgra(uint8_t* dst, uint32_t argb) {
  dst[0] = static_cast<uint8_t>(argb & 0xFF);          // B
  dst[1] = static_cast<uint8_t>((argb >> 8) & 0xFF);   // G
  dst[2] = static_cast<uint8_t>((argb >> 16) & 0xFF);  // R
  dst[3] = static_cast<uint8_t>((argb >> 24) & 0xFF);  // A
}

inline uint8_t ClampToByte(int value) {
  if (value < 0) {
    return 0;
  }
  if (value > 255) {
    return 255;
  }
  return static_cast<uint8_t>(value);
}

// --- BT.601 studio-swing conversion, identical to the reference sample's math --
inline uint8_t RgbToY(int r, int g, int b) {
  return ClampToByte(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
}
inline uint8_t RgbToU(int r, int g, int b) {
  return ClampToByte(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
}
inline uint8_t RgbToV(int r, int g, int b) {
  return ClampToByte(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
}

// Reads a BGRA pixel at a clamped coordinate.
inline void LoadBgra(const uint8_t* base, int stride, int x, int y, int width,
                     int height, uint8_t* bgr) {
  x = std::min(std::max(x, 0), width - 1);
  y = std::min(std::max(y, 0), height - 1);
  const uint8_t* p = base + static_cast<size_t>(y) * stride + x * 4;
  bgr[0] = p[0];
  bgr[1] = p[1];
  bgr[2] = p[2];
}

}  // namespace

SeewoFrameSource::~SeewoFrameSource() { Shutdown(); }

HRESULT SeewoFrameSource::Configure(UINT width, UINT height, REFGUID subtype) {
  if (width == 0 || height == 0) {
    return MF_E_INVALIDMEDIATYPE;
  }
  // The section layout caps the producer at 1920x1080, but the negotiated output
  // may legitimately be larger (the scaler handles it) or smaller.  Only reject
  // sizes that cannot be represented.
  if (width > 16384 || height > 16384) {
    return MF_E_INVALIDMEDIATYPE;
  }

  FrameOutputFormat format;
  if (subtype == MFVideoFormat_RGB32) {
    format = FrameOutputFormat::kRgb32;
  } else if (subtype == MFVideoFormat_NV12) {
    format = FrameOutputFormat::kNv12;
  } else {
    return MF_E_UNSUPPORTED_FORMAT;
  }

  ::AcquireSRWLockExclusive(&lock_);

  const size_t bytes = static_cast<size_t>(width) * height * 4u;
  HRESULT hr = S_OK;
  try {
    outputBgra_.assign(bytes, 0);
  } catch (const std::bad_alloc&) {
    hr = E_OUTOFMEMORY;
  }

  if (SUCCEEDED(hr)) {
    width_ = width;
    height_ = height;
    format_ = format;
    configured_ = true;
    shutdown_ = false;
    // Force the first frame to be rendered immediately rather than waiting for
    // the pattern's own pacing.
    lastPatternTickMs_ = 0;
    patternFrame_ = 0;
  }

  ::ReleaseSRWLockExclusive(&lock_);
  return hr;
}

bool SeewoFrameSource::IsConfigured() const {
  // Configure() is called once from the stream before any sample is requested;
  // the flag is read without the lock because it never transitions back to
  // false while the object is alive.
  return configured_;
}

void SeewoFrameSource::EnsureChannelLocked() {
  if (shutdown_ || channel_.valid()) {
    return;
  }

  const uint64_t now = ::GetTickCount64();
  if (lastOpenAttemptMs_ != 0 && (now - lastOpenAttemptMs_) < kReopenIntervalMs) {
    return;
  }
  lastOpenAttemptMs_ = now;

  // The Global\ namespace is what lets a session-0 FrameServer see a frame
  // published by the interactive-session assistant.  CreateOrOpenSharedSection
  // falls back to Local\ on its own when Global\ cannot be created or opened.
  channel_ = ::seewo::CreateOrOpenSharedSection(
      ::seewo::kVcamSectionName, ::seewo::kVcamLocalSectionName,
      ::seewo::kVcamSectionBytes);

  // Absence is the normal "no producer" case: never fail, just keep going with
  // the test pattern and try again later.
}

void SeewoFrameSource::DropChannelLocked() {
  if (channel_.valid()) {
    channel_.Close();
  }
  channel_ = ::seewo::ChannelHandle{};
  lastOpenAttemptMs_ = 0;
  consecutiveMisses_ = 0;
}

bool SeewoFrameSource::TryTakeLiveFrameLocked() {
  EnsureChannelLocked();
  if (!channel_.valid()) {
    return false;
  }

  const auto* header =
      reinterpret_cast<const ::seewo::VcamFrameHeader*>(channel_.view);

  // Validate the header.  A producer that has mapped the section but not yet
  // published leaves it zeroed, which fails the magic check.
  if (header->magic != ::seewo::kVcamMagic ||
      header->version != ::seewo::kVcamVersion) {
    return false;
  }
  if (header->sourceState !=
      static_cast<uint32_t>(::seewo::VcamSourceState::Live)) {
    return false;
  }
  if (header->format !=
      static_cast<uint32_t>(::seewo::VcamPixelFormat::Bgra32)) {
    return false;
  }

  const UINT srcWidth = header->width;
  const UINT srcHeight = header->height;
  const UINT srcStride = header->stride;
  if (srcWidth == 0 || srcHeight == 0) {
    return false;
  }
  if (srcWidth > ::seewo::kVcamMaxWidth || srcHeight > ::seewo::kVcamMaxHeight) {
    return false;
  }
  // The payload must be large enough for a full frame at the declared stride.
  if (srcStride < srcWidth * ::seewo::kVcamBytesPerPixel) {
    return false;
  }
  if (static_cast<uint64_t>(srcStride) * srcHeight >
      ::seewo::kVcamMaxFrameBytes) {
    return false;
  }
  if (header->payloadBytes != 0 &&
      header->payloadBytes < srcStride * srcHeight) {
    return false;
  }

  // Freshness: timestampMs is the producer's GetTickCount64(), so the two clocks
  // are directly comparable on the same machine.
  const uint64_t now = ::GetTickCount64();
  const uint64_t stamp = header->timestampMs;
  if (stamp == 0) {
    return false;
  }
  if (now >= stamp) {
    if ((now - stamp) > kFrameStaleMs) {
      return false;
    }
  } else if ((stamp - now) > kFrameStaleMs) {
    // The producer's clock is implausibly far ahead; do not trust the frame.
    return false;
  }

  const uint8_t* payload =
      static_cast<const uint8_t*>(channel_.view) + ::seewo::kVcamPayloadOffset;

  // The producer writes concurrently, so a torn frame is possible.  The frame
  // index is the producer's own sequencer: copy, then confirm it did not move.
  // Two attempts is enough; the next sample will simply pick up the newer frame.
  for (int attempt = 0; attempt < 2; ++attempt) {
    const uint32_t indexBefore = header->frameIndex;
    ScaleBgraLocked(payload, srcWidth, srcHeight, static_cast<int>(srcStride));
    if (header->frameIndex == indexBefore) {
      return true;
    }
  }

  // The frame was being rewritten the whole time.  Fall back to the pattern for
  // this sample rather than showing a torn image.
  return false;
}

void SeewoFrameSource::ScaleBgraLocked(const uint8_t* src, UINT srcWidth,
                                       UINT srcHeight, int srcStride) {
  uint8_t* dst = outputBgra_.data();
  const UINT dstWidth = width_;
  const UINT dstHeight = height_;

  if (srcWidth == dstWidth && srcHeight == dstHeight) {
    // Fast path: straight row copy, honouring a source stride that may be wider
    // than the visible width.
    const size_t rowBytes = static_cast<size_t>(dstWidth) * 4u;
    for (UINT y = 0; y < dstHeight; ++y) {
      ::memcpy(dst + static_cast<size_t>(y) * rowBytes,
               src + static_cast<size_t>(y) * srcStride, rowBytes);
    }
    return;
  }

  // General path: bilinear resample.  Sample centres are mapped so that the
  // source and destination images align edge to edge.
  const double xRatio = static_cast<double>(srcWidth) / dstWidth;
  const double yRatio = static_cast<double>(srcHeight) / dstHeight;
  const size_t dstRowBytes = static_cast<size_t>(dstWidth) * 4u;

  for (UINT dy = 0; dy < dstHeight; ++dy) {
    const double sy = (dy + 0.5) * yRatio - 0.5;
    int y0 = static_cast<int>(sy >= 0 ? sy : sy - 0.999999);
    const double fy = sy - y0;
    int y1 = y0 + 1;
    if (y0 < 0) {
      y0 = 0;
    }
    if (y1 > static_cast<int>(srcHeight) - 1) {
      y1 = static_cast<int>(srcHeight) - 1;
    }

    uint8_t* dstRow = dst + static_cast<size_t>(dy) * dstRowBytes;

    for (UINT dx = 0; dx < dstWidth; ++dx) {
      const double sx = (dx + 0.5) * xRatio - 0.5;
      int x0 = static_cast<int>(sx >= 0 ? sx : sx - 0.999999);
      const double fx = sx - x0;
      int x1 = x0 + 1;
      if (x0 < 0) {
        x0 = 0;
      }
      if (x1 > static_cast<int>(srcWidth) - 1) {
        x1 = static_cast<int>(srcWidth) - 1;
      }

      uint8_t p00[3];
      uint8_t p01[3];
      uint8_t p10[3];
      uint8_t p11[3];
      LoadBgra(src, srcStride, x0, y0, srcWidth, srcHeight, p00);
      LoadBgra(src, srcStride, x1, y0, srcWidth, srcHeight, p01);
      LoadBgra(src, srcStride, x0, y1, srcWidth, srcHeight, p10);
      LoadBgra(src, srcStride, x1, y1, srcWidth, srcHeight, p11);

      const double w00 = (1.0 - fx) * (1.0 - fy);
      const double w01 = fx * (1.0 - fy);
      const double w10 = (1.0 - fx) * fy;
      const double w11 = fx * fy;

      uint8_t* out = dstRow + static_cast<size_t>(dx) * 4u;
      for (int c = 0; c < 3; ++c) {
        const double value = p00[c] * w00 + p01[c] * w01 + p10[c] * w10 +
                             p11[c] * w11;
        out[c] = ClampToByte(static_cast<int>(value + 0.5));
      }
      out[3] = 0xFF;
    }
  }
}

void SeewoFrameSource::RenderTestPatternLocked(uint64_t tickMs) {
  uint8_t* dst = outputBgra_.data();
  const UINT width = width_;
  const UINT height = height_;
  const size_t rowBytes = static_cast<size_t>(width) * 4u;
  const UINT barHeight = ColorBarHeight(height);

  // The pattern advances on the wall clock, not on the sample count, so it keeps
  // moving at a steady rate even if the pipeline requests samples irregularly.
  if (lastPatternTickMs_ == 0) {
    lastPatternTickMs_ = tickMs;
  }
  const uint64_t elapsed = tickMs - lastPatternTickMs_;
  // 30 fps equivalent for the pattern's own animation clock.
  const uint64_t advanced = elapsed * 30u / 1000u;
  if (advanced > 0) {
    patternFrame_ += advanced;
    lastPatternTickMs_ = tickMs;
  }

  // --- top two thirds: 75% colour bars -------------------------------------
  for (UINT y = 0; y < barHeight && y < height; ++y) {
    uint8_t* row = dst + static_cast<size_t>(y) * rowBytes;
    for (UINT x = 0; x < width; ++x) {
      const UINT bar = (x * kColorBarCount) / (width == 0 ? 1 : width);
      const UINT index = bar < kColorBarCount ? bar : kColorBarCount - 1;
      StoreBgra(row + static_cast<size_t>(x) * 4u, kColorBars75[index]);
    }
  }

  // --- bottom third: greyscale ramp ----------------------------------------
  for (UINT y = barHeight; y < height; ++y) {
    uint8_t* row = dst + static_cast<size_t>(y) * rowBytes;
    for (UINT x = 0; x < width; ++x) {
      const uint8_t grey = static_cast<uint8_t>(
          width > 1 ? (static_cast<uint64_t>(x) * 255u) / (width - 1) : 0);
      const uint32_t argb = 0xFF000000u | (static_cast<uint32_t>(grey) << 16) |
                            (static_cast<uint32_t>(grey) << 8) | grey;
      StoreBgra(row + static_cast<size_t>(x) * 4u, argb);
    }
  }

  // --- animated overlays ----------------------------------------------------
  // A vertical sweep bar travelling left to right across the whole frame, and a
  // white block walking top to bottom.  Both are driven by patternFrame_ so
  // liveness is obvious at a glance.
  const UINT sweepWidth = std::max<UINT>(2u, width / 64u);
  const UINT travel = width + sweepWidth;
  const UINT sweepX =
      static_cast<UINT>((patternFrame_ * std::max<UINT>(2u, width / 90u)) %
                        (travel == 0 ? 1 : travel));

  const UINT blockSize = std::max<UINT>(8u, std::min(width, height) / 10u);
  const UINT blockX = (width > blockSize) ? (width - blockSize) / 2u : 0u;
  const UINT blockTravel = height + blockSize;
  const UINT blockY = static_cast<UINT>(
      (patternFrame_ * std::max<UINT>(2u, height / 60u)) %
      (blockTravel == 0 ? 1 : blockTravel));

  for (UINT y = 0; y < height; ++y) {
    uint8_t* row = dst + static_cast<size_t>(y) * rowBytes;

    // Sweep bar: only the columns in range are touched.
    const UINT xStart = (sweepX > sweepWidth) ? (sweepX - sweepWidth) : 0u;
    const UINT xEnd = std::min(width, sweepX);
    for (UINT x = xStart; x < xEnd; ++x) {
      uint8_t* pixel = row + static_cast<size_t>(x) * 4u;
      // 50% blend towards white so the bar reads on both the bars and the ramp.
      pixel[0] = ClampToByte(pixel[0] / 2 + 128);
      pixel[1] = ClampToByte(pixel[1] / 2 + 128);
      pixel[2] = ClampToByte(pixel[2] / 2 + 128);
      pixel[3] = 0xFF;
    }

    // White block.
    if (y >= blockY && y < blockY + blockSize) {
      const UINT blockEnd = std::min(width, blockX + blockSize);
      for (UINT x = blockX; x < blockEnd; ++x) {
        StoreBgra(row + static_cast<size_t>(x) * 4u, 0xFFFFFFFFu);
      }
    }
  }
}

HRESULT SeewoFrameSource::CopyOutputToSampleLocked(IMFSample* sample) const {
  Microsoft::WRL::ComPtr<IMFMediaBuffer> buffer;
  HRESULT hr = sample->GetBufferByIndex(0, &buffer);
  if (FAILED(hr)) {
    return hr;
  }

  const UINT width = width_;
  const UINT height = height_;

  if (format_ == FrameOutputFormat::kRgb32) {
    const DWORD needed = width * 4u * height;
    BYTE* dst = nullptr;
    LONG pitch = static_cast<LONG>(width * 4u);

    Microsoft::WRL::ComPtr<IMF2DBuffer> buffer2d;
    if (SUCCEEDED(buffer.As(&buffer2d))) {
      BYTE* scanline0 = nullptr;
      hr = buffer2d->Lock2D(&scanline0, &pitch);
      if (FAILED(hr)) {
        return hr;
      }
      // A negative pitch means bottom-up memory; the negotiated RGB32 type for
      // this source always declares a positive default stride, so a negative
      // pitch here would corrupt the image.  Reject rather than mis-render.
      if (pitch < 0) {
        buffer2d->Unlock2D();
        return MF_E_INVALIDMEDIATYPE;
      }
      for (UINT y = 0; y < height; ++y) {
        ::memcpy(scanline0 + static_cast<size_t>(y) * pitch,
                 outputBgra_.data() + static_cast<size_t>(y) * width * 4u,
                 static_cast<size_t>(width) * 4u);
      }
      buffer2d->Unlock2D();
    } else {
      DWORD maxLength = 0;
      DWORD currentLength = 0;
      hr = buffer->Lock(&dst, &maxLength, &currentLength);
      if (FAILED(hr)) {
        return hr;
      }
      if (maxLength < needed) {
        buffer->Unlock();
        return MF_E_BUFFERTOOSMALL;
      }
      ::memcpy(dst, outputBgra_.data(), needed);
      buffer->Unlock();
    }
    return buffer->SetCurrentLength(needed);
  }

  // NV12: one Y plane of width*height, then an interleaved UV plane of
  // width*height/2.  The negotiated type uses even dimensions, but the loops
  // clamp anyway so an odd size degrades gracefully instead of overrunning.
  const DWORD yPlaneBytes = width * height;
  const DWORD uvPlaneBytes = width * ((height + 1) / 2);
  const DWORD needed = yPlaneBytes + uvPlaneBytes;

  BYTE* base = nullptr;
  LONG pitch = static_cast<LONG>(width);
  bool twoD = false;

  Microsoft::WRL::ComPtr<IMF2DBuffer> buffer2d;
  if (SUCCEEDED(buffer.As(&buffer2d))) {
    hr = buffer2d->Lock2D(&base, &pitch);
    if (FAILED(hr)) {
      return hr;
    }
    if (pitch < static_cast<LONG>(width)) {
      buffer2d->Unlock2D();
      return MF_E_INVALIDMEDIATYPE;
    }
    twoD = true;
  } else {
    DWORD maxLength = 0;
    DWORD currentLength = 0;
    hr = buffer->Lock(&base, &maxLength, &currentLength);
    if (FAILED(hr)) {
      return hr;
    }
    if (maxLength < needed) {
      buffer->Unlock();
      return MF_E_BUFFERTOOSMALL;
    }
  }

  const size_t srcRowBytes = static_cast<size_t>(width) * 4u;
  const UINT chromaHeight = (height + 1) / 2;

  for (UINT y = 0; y < height; ++y) {
    const uint8_t* srcRow =
        outputBgra_.data() + static_cast<size_t>(y) * srcRowBytes;
    uint8_t* yRow = base + static_cast<size_t>(y) * pitch;
    for (UINT x = 0; x < width; ++x) {
      const uint8_t* p = srcRow + static_cast<size_t>(x) * 4u;
      yRow[x] = RgbToY(p[2], p[1], p[0]);
    }
  }

  // Chroma is subsampled 2x2.  Averaging the four luma-adjacent pixels keeps
  // coloured edges from fringing; the coefficients are the same BT.601 ones used
  // above, applied to the averaged RGB.
  for (UINT cy = 0; cy < chromaHeight; ++cy) {
    uint8_t* uvRow = base + static_cast<size_t>(height) * pitch +
                     static_cast<size_t>(cy) * pitch;
    const UINT y0 = std::min(cy * 2u, height - 1);
    const UINT y1 = std::min(cy * 2u + 1u, height - 1);
    const uint8_t* row0 = outputBgra_.data() + static_cast<size_t>(y0) * srcRowBytes;
    const uint8_t* row1 = outputBgra_.data() + static_cast<size_t>(y1) * srcRowBytes;

    for (UINT cx = 0; cx < width; cx += 2) {
      const UINT x0 = std::min(cx, width - 1);
      const UINT x1 = std::min(cx + 1u, width - 1);
      const uint8_t* a = row0 + static_cast<size_t>(x0) * 4u;
      const uint8_t* b = row0 + static_cast<size_t>(x1) * 4u;
      const uint8_t* c = row1 + static_cast<size_t>(x0) * 4u;
      const uint8_t* d = row1 + static_cast<size_t>(x1) * 4u;

      const int r = (a[2] + b[2] + c[2] + d[2] + 2) / 4;
      const int g = (a[1] + b[1] + c[1] + d[1] + 2) / 4;
      const int bl = (a[0] + b[0] + c[0] + d[0] + 2) / 4;

      uvRow[cx] = RgbToU(r, g, bl);
      if (cx + 1 < width) {
        uvRow[cx + 1] = RgbToV(r, g, bl);
      }
    }
  }

  if (twoD) {
    buffer2d->Unlock2D();
  } else {
    buffer->Unlock();
  }

  return buffer->SetCurrentLength(needed);
}

HRESULT SeewoFrameSource::ProduceFrame(IMFSample* sample, LONGLONG sampleTime,
                                       LONGLONG duration) {
  if (sample == nullptr) {
    return E_POINTER;
  }
  if (!configured_) {
    return MF_E_INVALIDMEDIATYPE;
  }

  ::AcquireSRWLockExclusive(&lock_);

  if (shutdown_) {
    ::ReleaseSRWLockExclusive(&lock_);
    return MF_E_SHUTDOWN;
  }

  bool live = false;
  if (!TryTakeLiveFrameLocked()) {
    // No producer, invalid header, or a stale/torn frame: synthesise.
    RenderTestPatternLocked(::GetTickCount64());
    ++consecutiveMisses_;
    ++patternFrames_;
    // The producer may have exited and restarted with a different mapping.  Drop
    // ours periodically so the next attempt reopens the name.
    if (consecutiveMisses_ >= kMissesBeforeReopen) {
      DropChannelLocked();
    }
  } else {
    live = true;
    consecutiveMisses_ = 0;
    ++liveFrames_;
  }

  HRESULT hr = CopyOutputToSampleLocked(sample);

  ::ReleaseSRWLockExclusive(&lock_);

  if (SUCCEEDED(hr)) {
    // Timestamps are stamped by the caller-supplied clock so the stream controls
    // cadence; the frame source only reports which path produced the pixels.
    hr = sample->SetSampleTime(sampleTime);
  }
  if (SUCCEEDED(hr)) {
    hr = sample->SetSampleDuration(duration);
  }

  if (SUCCEEDED(hr) && live) {
    // Nothing to do: the live path needs no bookkeeping beyond the counter.
  }
  return hr;
}

void SeewoFrameSource::Shutdown() {
  ::AcquireSRWLockExclusive(&lock_);
  if (!shutdown_) {
    shutdown_ = true;
    DropChannelLocked();
    outputBgra_.clear();
    outputBgra_.shrink_to_fit();
    configured_ = false;
  }
  ::ReleaseSRWLockExclusive(&lock_);
}

uint64_t SeewoFrameSource::liveFrameCount() const {
  ::AcquireSRWLockShared(const_cast<SRWLOCK*>(&lock_));
  const uint64_t value = liveFrames_;
  ::ReleaseSRWLockShared(const_cast<SRWLOCK*>(&lock_));
  return value;
}

uint64_t SeewoFrameSource::patternFrameCount() const {
  ::AcquireSRWLockShared(const_cast<SRWLOCK*>(&lock_));
  const uint64_t value = patternFrames_;
  ::ReleaseSRWLockShared(const_cast<SRWLOCK*>(&lock_));
  return value;
}

}  // namespace seewo
