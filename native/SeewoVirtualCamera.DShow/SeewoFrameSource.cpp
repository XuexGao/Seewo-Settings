// SeewoFrameSource.cpp
//
// Implementation of the DirectShow virtual camera's frame production.
//
// Two things matter more than anything else in this file:
//
//   * The shared channel is *never* allowed to fail the filter. If the assistant
//     is not running, if the producer is in another session, if the section is
//     torn down mid-stream -- in every case we silently fall back to the built-in
//     pattern and keep producing frames at the negotiated cadence. A virtual
//     camera that stops delivering because a helper process died shows up in
//     Zoom as a frozen or black window, which is worse than a test pattern.
//
//   * The producer writes into the same mapping we read, with no lock between us.
//     We therefore use a seqlock-style read: sample the header's frame index
//     before and after the copy and retry if it moved. The header fields we trust
//     are read once into locals before use so a torn write cannot make us index
//     outside the mapping.
#include "SeewoFrameSource.h"

#include <algorithm>
#include <cstddef>
#include <cstring>

namespace seewo {
namespace dshow {

namespace {

// A frame whose producer timestamp is older than this is considered stale. The
// assistant publishes at 30 fps, so two seconds is roughly sixty missed frames:
// comfortably past scheduler hiccups, well short of "the producer is gone".
constexpr uint64_t kStaleFrameMs = 2000;

// How often we are willing to retry opening the shared section while it is
// missing. The section is cheap to probe, but doing it on every frame at 30 fps
// across several player instances is pointless churn.
constexpr uint64_t kChannelReopenIntervalMs = 1000;

// How many times to re-read a frame when the producer overwrote it mid-copy.
constexpr int kMaxTearRetries = 3;

// Clamp a 0..255 integer without pulling in <algorithm> noise at the call site.
inline uint8_t Clamp8(int value) {
    if (value < 0) {
        return 0;
    }
    if (value > 255) {
        return 255;
    }
    return static_cast<uint8_t>(value);
}

// BT.601 limited-range conversion, the same coefficients the Media Foundation
// sibling uses. Inputs are 0..255 RGB, output is 16..235 luma and 16..240 chroma.
inline uint8_t RgbToY(int r, int g, int b) {
    return Clamp8(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
}

inline uint8_t RgbToU(int r, int g, int b) {
    return Clamp8(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
}

inline uint8_t RgbToV(int r, int g, int b) {
    return Clamp8(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
}

// 75% amplitude colour bars, the standard SMPTE ordering. Kept as a plain table
// so the pattern generator reads clearly.
struct Rgb {
    uint8_t r;
    uint8_t g;
    uint8_t b;
};

constexpr int kBarCount = 8;
constexpr Rgb kBars[kBarCount] = {
    {191, 191, 191},  // 75% white
    {191, 191, 0},    // 75% yellow
    {0, 191, 191},    // 75% cyan
    {0, 191, 0},      // 75% green
    {191, 0, 191},    // 75% magenta
    {191, 0, 0},      // 75% red
    {0, 0, 191},      // 75% blue
    {0, 0, 0},        // 75% black
};

}  // namespace

// ---------------------------------------------------------------------------
// FrameClock
// ---------------------------------------------------------------------------

FrameClock::FrameClock() {
    ::QueryPerformanceFrequency(&frequency_);
    if (frequency_.QuadPart <= 0) {
        // QPC is documented to always succeed on Windows XP and later, but a
        // degenerate frequency would divide by zero below; fall back to a
        // millisecond-ish cadence that still produces monotonic timestamps.
        frequency_.QuadPart = 10000000;
    }
    ::QueryPerformanceCounter(&base_);
}

void FrameClock::Start() {
    ::QueryPerformanceCounter(&base_);
    last_ = 0;
    started_ = true;
}

void FrameClock::Reset() {
    base_.QuadPart = 0;
    last_ = 0;
    started_ = false;
}

int64_t FrameClock::Next(int64_t frameInterval) {
    if (!started_) {
        Start();
    }

    if (frameInterval <= 0) {
        frameInterval = 333333;  // 30 fps
    }

    LARGE_INTEGER now{};
    ::QueryPerformanceCounter(&now);

    // Convert the elapsed tick count to 100 ns units. The intermediate product
    // fits comfortably in 64 bits: QPC ticks since boot are far below 2^63/10^7.
    const int64_t elapsedTicks = now.QuadPart - base_.QuadPart;
    int64_t elapsed100ns = 0;
    if (elapsedTicks > 0) {
        elapsed100ns = (elapsedTicks * 10000000LL) / frequency_.QuadPart;
    }

    // Quantise down to a frame boundary so downstream never sees 33.2 ms, then
    // 33.1 ms, then 33.4 ms. Strict monotonicity is enforced afterwards: some
    // capture stacks drop or reorder equal timestamps.
    int64_t quantised = (elapsed100ns / frameInterval) * frameInterval;
    if (quantised <= last_) {
        quantised = last_ + frameInterval;
    }
    last_ = quantised;

    return quantised;
}

// ---------------------------------------------------------------------------
// FrameSource
// ---------------------------------------------------------------------------

FrameSource::FrameSource() = default;
FrameSource::~FrameSource() {
    CloseChannel();
}

void FrameSource::Configure(uint32_t width, uint32_t height,
                            PixelSubtype subtype) {
    if (width == width_ && height == height_ && subtype == subtype_) {
        return;
    }

    width_ = width;
    height_ = height;
    subtype_ = subtype;

    // Force the pattern to be rebuilt at the new geometry.
    pattern_.clear();
    patternWidth_ = 0;
    patternHeight_ = 0;
}

void FrameSource::Reset() {
    CloseChannel();
    lastChannelFrameIndex_ = 0;
    lastFrameFromChannel_ = false;
}

bool FrameSource::TryOpenChannel() {
    // Rate limit probes. When the channel has never been opened we still respect
    // the interval, because a filter can be instantiated and asked for frames
    // long before the assistant starts.
    const uint64_t now = ::GetTickCount64();
    if (lastOpenAttemptMs_ != 0 &&
        now - lastOpenAttemptMs_ < kChannelReopenIntervalMs) {
        return false;
    }
    lastOpenAttemptMs_ = now;

    // Global\ first (a session-0 FrameServer or a service-hosted producer), then
    // the per-session fallback. CreateOrOpenSharedSection never throws; it
    // returns an invalid handle and sets the last error when neither works.
    ChannelHandle channel = CreateOrOpenSharedSection(
        kVcamSectionName, kVcamLocalSectionName, kVcamSectionBytes);

    if (!channel.valid()) {
        return false;
    }

    channel_ = channel;
    ++channelOpenCount_;
    return true;
}

void FrameSource::CloseChannel() {
    channel_.Close();
}

FrameSource::ChannelRead FrameSource::TryReadChannelFrame(
    const FrameRequest& request) {
    if (!channel_.valid()) {
        return ChannelRead::Dead;
    }

    auto* base = static_cast<uint8_t*>(channel_.view);
    if (base == nullptr) {
        return ChannelRead::Dead;
    }

    for (int attempt = 0; attempt < kMaxTearRetries; ++attempt) {
        // Snapshot the header once. `header` is a copy: the producer may be
        // writing to the mapping while we read, and we must not re-read a field
        // after validating it.
        VcamFrameHeader header{};
        std::memcpy(&header, base, sizeof(header));

        if (header.magic != kVcamMagic || header.version != kVcamVersion) {
            // Either nothing has published yet (zeroed page) or a producer from
            // an incompatible build owns the section. Refuse to guess.
            return ChannelRead::Dead;
        }

        if (header.sourceState !=
            static_cast<uint32_t>(VcamSourceState::Live)) {
            return ChannelRead::Dead;
        }

        if (header.format !=
            static_cast<uint32_t>(VcamPixelFormat::Bgra32)) {
            return ChannelRead::Dead;
        }

        if (header.width == 0 || header.height == 0 ||
            header.width > kVcamMaxWidth || header.height > kVcamMaxHeight) {
            return ChannelRead::Dead;
        }

        // The producer's stride must at least cover the declared width and must
        // fit inside the section together with the payload offset.
        const uint64_t minimumStride =
            static_cast<uint64_t>(header.width) * kVcamBytesPerPixel;
        if (header.stride < minimumStride) {
            return ChannelRead::Dead;
        }

        const uint64_t requiredBytes =
            static_cast<uint64_t>(header.stride) * header.height;
        if (requiredBytes > kVcamMaxFrameBytes) {
            return ChannelRead::Dead;
        }

        // Staleness: a producer that stopped publishing (crashed, suspended, or
        // simply switched itself off) leaves a perfectly valid but ancient frame
        // behind. Treat it as absent so the test pattern takes over.
        const uint64_t now = ::GetTickCount64();
        if (header.timestampMs == 0 || now < header.timestampMs ||
            now - header.timestampMs > kStaleFrameMs) {
            return ChannelRead::Dead;
        }

        // The producer only bumps the index when it publishes. Re-sending the
        // frame we already delivered is correct for a live camera: the consumer
        // runs at its own cadence and must not see the picture flicker back to
        // the fallback pattern just because the producer is slower.
        const bool alreadyDelivered =
            (header.frameIndex == lastChannelFrameIndex_ &&
             lastChannelFrameIndex_ != 0);

        const uint8_t* payload = base + kVcamPayloadOffset;
        Convert(request, payload, header.stride, header.width, header.height);

        // Seqlock check: if the producer replaced the frame while we were
        // copying, the buffer is a mix of two frames. Re-read from scratch.
        uint32_t indexAfter = 0;
        std::memcpy(&indexAfter, base + offsetof(VcamFrameHeader, frameIndex),
                    sizeof(indexAfter));
        if (indexAfter != header.frameIndex) {
            continue;
        }

        lastChannelFrameIndex_ = header.frameIndex;
        return alreadyDelivered ? ChannelRead::NoNewFrame
                                : ChannelRead::Delivered;
    }

    return ChannelRead::Dead;
}

void FrameSource::EnsurePattern(uint32_t width, uint32_t height) {
    if (patternWidth_ == width && patternHeight_ == height &&
        !pattern_.empty()) {
        return;
    }

    patternWidth_ = width;
    patternHeight_ = height;
    pattern_.assign(static_cast<size_t>(width) * height * 4u, 0);

    // Static portion: 75% bars over the top three quarters, a greyscale ramp
    // across the bottom quarter. Only the moving sweep bar changes per frame, so
    // this is computed once per geometry rather than per frame.
    const uint32_t barHeight = (height * 3u) / 4u;

    for (uint32_t y = 0; y < height; ++y) {
        uint8_t* row = pattern_.data() + static_cast<size_t>(y) * width * 4u;

        if (y < barHeight) {
            for (uint32_t x = 0; x < width; ++x) {
                const uint32_t bar =
                    std::min<uint32_t>((x * kBarCount) / width, kBarCount - 1);
                const Rgb& colour = kBars[bar];
                uint8_t* pixel = row + static_cast<size_t>(x) * 4u;
                // Memory layout is B, G, R, A -- matching MFVideoFormat_ARGB32
                // and DirectShow's MEDIASUBTYPE_RGB32.
                pixel[0] = colour.b;
                pixel[1] = colour.g;
                pixel[2] = colour.r;
                pixel[3] = 0xFF;
            }
        } else {
            for (uint32_t x = 0; x < width; ++x) {
                const uint8_t level = static_cast<uint8_t>(
                    width > 1 ? (x * 255u) / (width - 1u) : 0u);
                uint8_t* pixel = row + static_cast<size_t>(x) * 4u;
                pixel[0] = level;
                pixel[1] = level;
                pixel[2] = level;
                pixel[3] = 0xFF;
            }
        }
    }
}

void FrameSource::RenderPattern(uint64_t frameIndex) {
    if (pattern_.empty() || width_ == 0 || height_ == 0) {
        return;
    }

    // Start from the pristine bars/ramp, then draw the sweep bar on top. The
    // copy is the price of not accumulating the inversion across frames; at
    // 1080p it is ~8 MB per frame, which memcpy handles at memory bandwidth.
    if (frame_.size() != pattern_.size()) {
        frame_.resize(pattern_.size());
    }
    std::memcpy(frame_.data(), pattern_.data(), pattern_.size());

    // The sweep bar moves left to right and wraps. It is drawn as a column of
    // inverted pixels so it is visible against both the colour bars and the
    // greyscale ramp, and unmistakably "live".
    const uint32_t sweepWidth = std::max<uint32_t>(width_ / 32u, 4u);
    const uint32_t span = width_ + sweepWidth;
    const uint32_t sweepX = static_cast<uint32_t>(frameIndex % span);

    const uint32_t left = sweepX >= sweepWidth ? sweepX - sweepWidth : 0u;
    const uint32_t right = std::min<uint32_t>(sweepX, width_);

    for (uint32_t y = 0; y < height_; ++y) {
        uint8_t* row = frame_.data() + static_cast<size_t>(y) * width_ * 4u;
        for (uint32_t x = left; x < right; ++x) {
            uint8_t* pixel = row + static_cast<size_t>(x) * 4u;
            pixel[0] = static_cast<uint8_t>(255u - pixel[0]);
            pixel[1] = static_cast<uint8_t>(255u - pixel[1]);
            pixel[2] = static_cast<uint8_t>(255u - pixel[2]);
            pixel[3] = 0xFF;
        }
    }
}

void FrameSource::ConvertBgraToRgb32(const FrameRequest& request,
                                     const uint8_t* source,
                                     uint32_t sourceStride, uint32_t width,
                                     uint32_t height) {
    // RGB32 in DirectShow is BGRA in memory, so this is a straight row copy with
    // only the destination stride handled. A negative destination stride means a
    // bottom-up DIB: image row 0 belongs at buffer row request.height - 1, which
    // is why the destination height -- not the copied height -- drives the
    // mapping. Using the copied height would place a partial image at the wrong
    // vertical offset.
    const uint32_t rowBytes = width * 4u;
    const bool bottomUp = request.destStride < 0;
    const LONG absStride =
        bottomUp ? -request.destStride : request.destStride;

    for (uint32_t y = 0; y < height; ++y) {
        const uint32_t destRow = bottomUp ? (request.height - 1u - y) : y;
        uint8_t* dst = request.dest + static_cast<size_t>(destRow) * absStride;
        const uint8_t* src = source + static_cast<size_t>(y) * sourceStride;
        std::memcpy(dst, src, rowBytes);
    }
}

void FrameSource::ConvertBgraToRgb24(const FrameRequest& request,
                                     const uint8_t* source,
                                     uint32_t sourceStride, uint32_t width,
                                     uint32_t height) {
    const bool bottomUp = request.destStride < 0;
    const LONG absStride =
        bottomUp ? -request.destStride : request.destStride;

    for (uint32_t y = 0; y < height; ++y) {
        const uint32_t destRow = bottomUp ? (request.height - 1u - y) : y;
        uint8_t* dst = request.dest + static_cast<size_t>(destRow) * absStride;
        const uint8_t* src = source + static_cast<size_t>(y) * sourceStride;

        for (uint32_t x = 0; x < width; ++x) {
            const uint8_t* pixel = src + static_cast<size_t>(x) * 4u;
            // Drop alpha, keep BGR ordering.
            dst[0] = pixel[0];
            dst[1] = pixel[1];
            dst[2] = pixel[2];
            dst += 3;
        }
    }
}

void FrameSource::ConvertBgraToYuy2(const FrameRequest& request,
                                    const uint8_t* source,
                                    uint32_t sourceStride, uint32_t width,
                                    uint32_t height) {
    // YUY2 packs two horizontally adjacent pixels into four bytes:
    //   Y0 U Y1 V
    // Chroma is shared, so it is computed once per pair. An odd final column
    // duplicates the last pixel's luma rather than reading past the source row.
    const bool bottomUp = request.destStride < 0;
    const LONG absStride =
        bottomUp ? -request.destStride : request.destStride;
    const uint32_t pairCount = (width + 1u) / 2u;

    for (uint32_t y = 0; y < height; ++y) {
        const uint32_t destRow = bottomUp ? (request.height - 1u - y) : y;
        uint8_t* dst = request.dest + static_cast<size_t>(destRow) * absStride;
        const uint8_t* src = source + static_cast<size_t>(y) * sourceStride;

        for (uint32_t pair = 0; pair < pairCount; ++pair) {
            const uint32_t x0 = pair * 2u;
            const uint32_t x1 = std::min<uint32_t>(x0 + 1u, width - 1u);

            const uint8_t* p0 = src + static_cast<size_t>(x0) * 4u;
            const uint8_t* p1 = src + static_cast<size_t>(x1) * 4u;

            const int b0 = p0[0];
            const int g0 = p0[1];
            const int r0 = p0[2];
            const int b1 = p1[0];
            const int g1 = p1[1];
            const int r1 = p1[2];

            const uint8_t y0 = RgbToY(r0, g0, b0);
            const uint8_t y1 = RgbToY(r1, g1, b1);
            // Average the pair before subsampling the chroma; averaging the
            // already-converted U/V would round twice and shift saturated edges.
            const uint8_t u = RgbToU((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);
            const uint8_t v = RgbToV((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);

            dst[0] = y0;
            dst[1] = u;
            dst[2] = y1;
            dst[3] = v;
            dst += 4;
        }
    }
}

void FrameSource::FillBackground(const FrameRequest& request) {
    // Paint the entire destination with a neutral dark grey (luma 16, chroma
    // 128 -- the classic "no signal" level) before a partial write. Without this
    // a producer publishing a smaller geometry than the one negotiated would
    // leave the previous frame's pixels visible in the uncovered region.
    if (request.dest == nullptr || request.width == 0 || request.height == 0) {
        return;
    }

    const bool bottomUp = request.destStride < 0;
    const LONG absStride = bottomUp ? -request.destStride : request.destStride;
    if (absStride <= 0) {
        return;
    }

    // Bound every write by the caller's reported buffer size so a
    // smaller-than-advertised allocator cannot be overrun.
    const uint64_t maxBytes = request.destBytes;

    for (uint32_t y = 0; y < request.height; ++y) {
        const uint32_t destRow = bottomUp ? (request.height - 1u - y) : y;
        const uint64_t rowOffset = static_cast<uint64_t>(destRow) * absStride;
        if (rowOffset >= maxBytes) {
            break;
        }
        uint8_t* row = request.dest + rowOffset;
        const uint64_t rowBudget = maxBytes - rowOffset;

        switch (request.subtype) {
            case PixelSubtype::Rgb32: {
                const uint64_t needed = static_cast<uint64_t>(request.width) * 4u;
                if (needed > rowBudget) {
                    return;
                }
                for (uint32_t x = 0; x < request.width; ++x) {
                    row[x * 4u + 0] = 16;
                    row[x * 4u + 1] = 16;
                    row[x * 4u + 2] = 16;
                    row[x * 4u + 3] = 0xFF;
                }
                break;
            }
            case PixelSubtype::Rgb24: {
                const uint64_t needed = static_cast<uint64_t>(request.width) * 3u;
                if (needed > rowBudget) {
                    return;
                }
                std::memset(row, 16, static_cast<size_t>(needed));
                break;
            }
            case PixelSubtype::Yuy2: {
                const uint32_t pairs = (request.width + 1u) / 2u;
                const uint64_t needed = static_cast<uint64_t>(pairs) * 4u;
                if (needed > rowBudget) {
                    return;
                }
                for (uint32_t pair = 0; pair < pairs; ++pair) {
                    row[pair * 4u + 0] = 16;   // Y0
                    row[pair * 4u + 1] = 128;  // U
                    row[pair * 4u + 2] = 16;   // Y1
                    row[pair * 4u + 3] = 128;  // V
                }
                break;
            }
        }
    }
}

void FrameSource::Convert(const FrameRequest& request, const uint8_t* source,
                          uint32_t sourceStride, uint32_t width,
                          uint32_t height) {
    // A producer may publish a different size than the one we negotiated (for
    // example the assistant switched capture resolution). Scaling would cost a
    // filter and a lot of CPU; instead we take the overlapping top-left region
    // and fill the remainder with the neutral background above. That keeps the
    // frame recognisable without lying about the negotiated geometry.
    const uint32_t copyWidth = std::min(width, request.width);
    const uint32_t copyHeight = std::min(height, request.height);

    if (copyWidth < request.width || copyHeight < request.height) {
        FillBackground(request);
    }

    if (copyWidth == 0 || copyHeight == 0) {
        return;
    }

    switch (request.subtype) {
        case PixelSubtype::Rgb32:
            ConvertBgraToRgb32(request, source, sourceStride, copyWidth,
                               copyHeight);
            break;
        case PixelSubtype::Rgb24:
            ConvertBgraToRgb24(request, source, sourceStride, copyWidth,
                               copyHeight);
            break;
        case PixelSubtype::Yuy2:
            ConvertBgraToYuy2(request, source, sourceStride, copyWidth,
                              copyHeight);
            break;
    }
}

bool FrameSource::Produce(FrameRequest& request, uint64_t frameIndex) {
    if (request.dest == nullptr || request.width == 0 || request.height == 0) {
        lastFrameFromChannel_ = false;
        return false;
    }

    // Make sure we are configured for what the pin actually negotiated. The pin
    // calls Configure() on connection, but a downstream reconnection can race
    // ahead of it, and Configure() is a cheap no-op when nothing changed.
    Configure(request.width, request.height, request.subtype);

    if (!channel_.valid()) {
        TryOpenChannel();
    }

    if (channel_.valid()) {
        const ChannelRead read = TryReadChannelFrame(request);
        if (read == ChannelRead::Delivered || read == ChannelRead::NoNewFrame) {
            // NoNewFrame still produced a valid picture, so the channel is
            // considered live either way.
            lastFrameFromChannel_ = true;
            return true;
        }

        // The channel exists but is unusable (stale, malformed, mid-teardown).
        // Drop the mapping so the next frame re-probes it; keeping a dead
        // section open would stop a restarted assistant from ever being noticed.
        CloseChannel();
    }

    lastFrameFromChannel_ = false;

    EnsurePattern(request.width, request.height);
    RenderPattern(frameIndex);
    Convert(request, frame_.data(), request.width * 4u, request.width,
            request.height);
    return false;
}

}  // namespace dshow
}  // namespace seewo
