// SeewoFrameSource.h
//
// Frame production for the Seewo DirectShow virtual camera.
//
// The filter is a *push* source: the output pin's streaming thread asks this
// object for one frame per negotiated frame interval. Two providers are chained
// in priority order:
//
//   1. The Seewo shared-memory frame channel (see native/SeewoCommon/SeewoIpc.h).
//      This is the same channel the Media Foundation sibling source consumes, so
//      both virtual cameras show identical content.
//   2. A built-in moving test pattern. The device must never go dark just because
//      the assistant is not publishing, so the fallback is unconditional.
//
// This header deliberately depends only on the Windows SDK plus SeewoIpc.h. It
// has no DirectShow dependency at all, which keeps the pixel plumbing testable
// and makes the ordering of "channel first, pattern second" obvious.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include <cstdint>
#include <vector>

#include "SeewoIpc.h"

namespace seewo {
namespace dshow {

// The subset of DirectShow media subtypes this filter advertises, expressed as a
// plain enum so this translation unit never has to include strmif.h.
enum class PixelSubtype {
    Rgb32 = 0,
    Rgb24 = 1,
    Yuy2 = 2,
};

// Bytes per pixel of the *packed* representation. YUY2 is 2 bytes per pixel but
// is addressed in 4-byte macro pixels, which the converters below handle.
inline uint32_t SubtypeBytesPerPixel(PixelSubtype subtype) {
    switch (subtype) {
        case PixelSubtype::Rgb32:
            return 4;
        case PixelSubtype::Rgb24:
            return 3;
        case PixelSubtype::Yuy2:
            return 2;
    }
    return 4;
}

// Everything the frame source needs to know about the destination sample.
struct FrameRequest {
    // Negotiated geometry.
    uint32_t width = 0;
    uint32_t height = 0;
    PixelSubtype subtype = PixelSubtype::Rgb32;

    // Destination buffer and its true stride in bytes. A negative stride means a
    // bottom-up DIB (some downstream decoders hand those out); both are honoured.
    uint8_t* dest = nullptr;
    LONG destStride = 0;
    uint32_t destBytes = 0;
};

// Monotonic, frame-interval-aligned media clock.
//
// DirectShow wants 100 ns units on a live source, and downstream muxers are much
// happier when consecutive samples advance by exactly one frame interval rather
// than by whatever jitter QueryPerformanceCounter happens to return. We therefore
// quantise wall-clock elapsed time down to a frame boundary and then force strict
// monotonicity.
class FrameClock {
public:
    FrameClock();

    // Captures the QPC base. Called when the filter transitions to State_Running.
    void Start();

    // Returns the presentation time for the next sample, in 100 ns units.
    REFERENCE_TIME Next(int64_t frameInterval);

    // Clears the accumulated position; the next Start() begins from zero again.
    void Reset();

private:
    LARGE_INTEGER base_{};
    LARGE_INTEGER frequency_{};
    REFERENCE_TIME last_ = 0;
    bool started_ = false;
};

// Produces frames for the output pin.
//
// Threading: this object is owned by the output pin and is only ever touched from
// the pin's streaming thread (Produce) and from the pin's state-change methods
// (Configure/Reset), which are serialised by the pin's own critical section.
class FrameSource {
public:
    FrameSource();
    ~FrameSource();

    FrameSource(const FrameSource&) = delete;
    FrameSource& operator=(const FrameSource&) = delete;

    // (Re)configures the output geometry. Cheap when nothing changed; drops the
    // cached test pattern when the size changes.
    void Configure(uint32_t width, uint32_t height, PixelSubtype subtype);

    // Releases the shared-memory mapping and forgets the cached pattern. Called
    // from the pin when streaming stops so a stalled producer cannot pin the
    // section open across sessions.
    void Reset();

    // Produces exactly one frame into `request`. Returns true when the frame came
    // from the shared channel, false when the built-in pattern was used.
    bool Produce(FrameRequest& request, uint64_t frameIndex);

    // True when the last Produce() read a live frame from the shared channel.
    bool UsingSharedMemory() const { return lastFrameFromChannel_; }

    // Number of times the channel was successfully opened. Diagnostics only.
    uint64_t ChannelOpenCount() const { return channelOpenCount_; }

private:
    // Outcome of one attempt to read the shared channel.
    enum class ChannelRead {
        // A new, valid, fresh frame was converted into the destination.
        Delivered,
        // The channel is healthy but the producer has not published a new frame
        // since the last one we took. The existing frame is still valid, so it
        // is re-converted: a camera must never flicker back to the test pattern
        // just because the consumer is running faster than the producer.
        NoNewFrame,
        // The channel is absent, malformed, stale, or from an incompatible
        // build. The caller should drop the mapping and re-probe later.
        Dead,
    };

    // --- Shared-memory channel ------------------------------------------------
    bool TryOpenChannel();
    void CloseChannel();

    // Attempts to read a validated, fresh frame out of the mapping and convert it
    // into the request buffer.
    ChannelRead TryReadChannelFrame(const FrameRequest& request);

    // --- Built-in pattern -----------------------------------------------------
    void EnsurePattern(uint32_t width, uint32_t height);
    void RenderPattern(uint64_t frameIndex);

    // --- Conversion -----------------------------------------------------------
    // All converters take a tightly described BGRA source (top-down, stride in
    // bytes) and write into the request destination honouring its stride sign.
    static void ConvertBgraToRgb32(const FrameRequest& request,
                                   const uint8_t* source,
                                   uint32_t sourceStride,
                                   uint32_t width,
                                   uint32_t height);
    static void ConvertBgraToRgb24(const FrameRequest& request,
                                   const uint8_t* source,
                                   uint32_t sourceStride,
                                   uint32_t width,
                                   uint32_t height);
    static void ConvertBgraToYuy2(const FrameRequest& request,
                                  const uint8_t* source,
                                  uint32_t sourceStride,
                                  uint32_t width,
                                  uint32_t height);

    // Dispatches to the converter matching request.subtype.
    static void Convert(const FrameRequest& request,
                        const uint8_t* source,
                        uint32_t sourceStride,
                        uint32_t width,
                        uint32_t height);

    // Paints the whole destination with the neutral "no signal" level. Used
    // before a partial write so the uncovered region is never stale pixels.
    static void FillBackground(const FrameRequest& request);

    // --- State ----------------------------------------------------------------
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    PixelSubtype subtype_ = PixelSubtype::Rgb32;

    // Shared channel.
    ChannelHandle channel_;
    uint64_t channelOpenCount_ = 0;
    uint64_t lastChannelFrameIndex_ = 0;
    // Tick count of the last open attempt, so a missing producer is probed at
    // most once per kChannelReopenIntervalMs instead of once per frame.
    uint64_t lastOpenAttemptMs_ = 0;
    bool lastFrameFromChannel_ = false;

    // Cached test pattern, always BGRA top-down with stride == width * 4.
    // pattern_ is the pristine base and is never mutated after it is built;
    // frame_ is the per-frame scratch copy the moving sweep bar is drawn into.
    // Inverting the base in place would accumulate across frames and gradually
    // turn the whole image negative.
    std::vector<uint8_t> pattern_;
    std::vector<uint8_t> frame_;
    uint32_t patternWidth_ = 0;
    uint32_t patternHeight_ = 0;
};

}  // namespace dshow
}  // namespace seewo
