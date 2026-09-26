// SeewoOutputPin.cpp
//
// Implementation of the Seewo DirectShow virtual camera output pin.
//
// Structure of this file:
//   1. Media type helpers (duplicate / free / build / match)
//   2. The format table and its accessors
//   3. MediaTypeEnumerator
//   4. StreamContext + the streaming worker thread
//   5. SeewoOutputPin
//
// Threading model, which is the part most likely to hang a media player if it is
// wrong:
//
//   * The pin has one CRITICAL_SECTION guarding its connection state. It is held
//     only for short state mutations, never across a call into a peer's code, so
//     a re-entrant QueryAccept/ConnectionMediaType from downstream cannot
//     deadlock us.
//   * Each Run creates a fresh StreamContext that owns its own refs on the
//     allocator, the downstream IMemInputPin, the reference clock, and its own
//     FrameSource. The streaming thread holds one reference on that context. That
//     is what makes teardown safe: even if the thread is wedged inside a
//     downstream Receive() call, the context outlives the pin and nothing is
//     freed underneath it.
//   * StopStreaming signals the stop event and waits for the thread with a
//     bounded timeout. If the timeout expires we deliberately leak the thread
//     handle rather than block the graph. A source filter that blocks the graph
//     teardown is exactly the hang this design exists to avoid.
#include "SeewoOutputPin.h"

#include <mmsystem.h>

#include <algorithm>
#include <cstring>
#include <memory>
#include <new>

#include "SeewoDShowFilter.h"

namespace seewo {
namespace dshow {

namespace {

// ---------------------------------------------------------------------------
// Small utilities
// ---------------------------------------------------------------------------

// RAII for the pin's critical section. EnterCriticalSection on a section that
// was never initialised is undefined, so the pin initialises it in its
// constructor before any method can run.
class PinLock {
public:
    explicit PinLock(CRITICAL_SECTION& section) : section_(section) {
        ::EnterCriticalSection(&section_);
    }
    ~PinLock() { ::LeaveCriticalSection(&section_); }

    PinLock(const PinLock&) = delete;
    PinLock& operator=(const PinLock&) = delete;

private:
    CRITICAL_SECTION& section_;
};

// A live camera produces frames continuously; the graph never waits on us, so
// four buffers is enough to absorb a slow renderer without adding latency.
constexpr long kDefaultBufferCount = 4;

// How long StopStreaming waits for the worker to leave a downstream Receive().
// Generous enough for a busy renderer, short enough that a wedged filter does not
// hang the player's UI thread.
constexpr DWORD kStreamJoinTimeoutMs = 3000;

// Frame interval used when the connection media type is somehow unreadable.
constexpr int64_t kDefaultFrameInterval = 333333;  // 30 fps

// High-resolution waitable timers need Windows 10 1803 or later. This is a plain
// SDK constant, not a Windows 11 API; on older builds CreateWaitableTimerExW
// fails with ERROR_INVALID_PARAMETER and we degrade to a normal timer.
HANDLE CreateFrameTimer() {
    HANDLE timer = ::CreateWaitableTimerExW(
        nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
        TIMER_ALL_ACCESS);
    if (timer == nullptr) {
        timer = ::CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
    }
    if (timer == nullptr) {
        timer = ::CreateWaitableTimerW(nullptr, FALSE, nullptr);
    }
    return timer;
}

// Converts a 100 ns duration to the negative relative due time SetWaitableTimer
// expects. Durations below one millisecond are rounded up to 1 ms because a
// relative due time of zero means "do not signal".
LARGE_INTEGER RelativeDueTime(int64_t hundredNanoseconds) {
    LARGE_INTEGER due{};
    int64_t value = hundredNanoseconds;
    if (value < 10000) {
        value = 10000;
    }
    due.QuadPart = -value;
    return due;
}

// Raises the system timer resolution for the lifetime of a streaming session.
//
// The high-resolution waitable timer created by CreateFrameTimer already gives
// sub-millisecond granularity on Windows 10 1803+, but that path can fail (and
// does on older builds), in which case the fallback timer is quantised to the
// default ~15.6 ms tick -- far too coarse for a 33.3 ms frame interval. Requesting
// 1 ms for the duration of the session is the standard fix, and RAII keeps the
// request balanced so we never leave the machine with a raised timer resolution
// after the camera stops.
class TimerResolutionScope {
public:
    TimerResolutionScope() : raised_(::timeBeginPeriod(1) == TIMERR_NOERROR) {}
    ~TimerResolutionScope() {
        if (raised_) {
            ::timeEndPeriod(1);
        }
    }

    TimerResolutionScope(const TimerResolutionScope&) = delete;
    TimerResolutionScope& operator=(const TimerResolutionScope&) = delete;

private:
    bool raised_;
};

}  // namespace

// ---------------------------------------------------------------------------
// 1. Media type helpers
// ---------------------------------------------------------------------------

AM_MEDIA_TYPE* DuplicateMediaType(const AM_MEDIA_TYPE* source) {
    if (source == nullptr) {
        return nullptr;
    }

    auto* copy = static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
    if (copy == nullptr) {
        return nullptr;
    }
    *copy = *source;
    copy->pUnk = nullptr;
    copy->pbFormat = nullptr;

    if (source->cbFormat != 0 && source->pbFormat != nullptr) {
        copy->pbFormat =
            static_cast<BYTE*>(CoTaskMemAlloc(source->cbFormat));
        if (copy->pbFormat == nullptr) {
            CoTaskMemFree(copy);
            return nullptr;
        }
        std::memcpy(copy->pbFormat, source->pbFormat, source->cbFormat);
        copy->cbFormat = source->cbFormat;
    } else {
        copy->cbFormat = 0;
        copy->pbFormat = nullptr;
    }

    return copy;
}

void FreeMediaTypeBlock(AM_MEDIA_TYPE& mediaType) {
    if (mediaType.pbFormat != nullptr) {
        CoTaskMemFree(mediaType.pbFormat);
        mediaType.pbFormat = nullptr;
    }
    mediaType.cbFormat = 0;
}

void DeleteMediaType(AM_MEDIA_TYPE* mediaType) {
    if (mediaType == nullptr) {
        return;
    }
    FreeMediaTypeBlock(*mediaType);
    CoTaskMemFree(mediaType);
}

// ---------------------------------------------------------------------------
// 2. Format table
// ---------------------------------------------------------------------------

namespace {

// Subtype order matters: RGB32 first because it is the cheapest for us to
// produce (a straight copy) and the most widely accepted by renderers.
const GUID* const kSubtypes[] = {
    &MEDIASUBTYPE_RGB32,
    &MEDIASUBTYPE_RGB24,
    &MEDIASUBTYPE_YUY2,
};

constexpr int kSubtypeCount =
    static_cast<int>(sizeof(kSubtypes) / sizeof(kSubtypes[0]));

struct SizeEntry {
    uint32_t width;
    uint32_t height;
};

const SizeEntry kSizes[] = {
    {1920, 1080},
    {1280, 720},
    {640, 480},
    {320, 240},
};

constexpr int kSizeCount = static_cast<int>(sizeof(kSizes) / sizeof(kSizes[0]));

const uint32_t kRates[] = {30, 15};

constexpr int kRateCount = static_cast<int>(sizeof(kRates) / sizeof(kRates[0]));

constexpr int kFormatCount = kSubtypeCount * kSizeCount * kRateCount;

// Built once on first use. Static storage duration means it is torn down by the
// CRT, which is fine because nothing in this DLL holds a pointer to it across
// DllMain.
const VideoFormat* FormatTable() {
    static const VideoFormat* table = [] {
        auto* formats = new VideoFormat[kFormatCount];
        int index = 0;
        for (int subtype = 0; subtype < kSubtypeCount; ++subtype) {
            for (int size = 0; size < kSizeCount; ++size) {
                for (int rate = 0; rate < kRateCount; ++rate) {
                    formats[index].subtype = kSubtypes[subtype];
                    formats[index].width = kSizes[size].width;
                    formats[index].height = kSizes[size].height;
                    formats[index].fps = kRates[rate];
                    ++index;
                }
            }
        }
        return formats;
    }();
    return table;
}

}  // namespace

int VideoFormatCount() { return kFormatCount; }

const VideoFormat* VideoFormatAt(int index) {
    if (index < 0 || index >= kFormatCount) {
        return nullptr;
    }
    return FormatTable() + index;
}

uint32_t VideoFormatBytesPerPixel(const GUID& subtype) {
    if (subtype == MEDIASUBTYPE_RGB32) {
        return 4;
    }
    if (subtype == MEDIASUBTYPE_RGB24) {
        return 3;
    }
    if (subtype == MEDIASUBTYPE_YUY2) {
        return 2;
    }
    return 0;
}

uint32_t VideoFormatStride(const VideoFormat& format) {
    const uint32_t bpp = VideoFormatBytesPerPixel(*format.subtype);
    // DIB rows are DWORD aligned, and every advertised width is already a
    // multiple of four, but the rounding keeps the helper correct if the table
    // ever grows a non-aligned size.
    return ((format.width * bpp) + 3u) & ~3u;
}

uint32_t VideoFormatImageSize(const VideoFormat& format) {
    return VideoFormatStride(format) * format.height;
}

bool SubtypeToPixelFormat(const GUID& subtype, PixelSubtype* out) {
    if (out == nullptr) {
        return false;
    }
    if (subtype == MEDIASUBTYPE_RGB32) {
        *out = PixelSubtype::Rgb32;
        return true;
    }
    if (subtype == MEDIASUBTYPE_RGB24) {
        *out = PixelSubtype::Rgb24;
        return true;
    }
    if (subtype == MEDIASUBTYPE_YUY2) {
        *out = PixelSubtype::Yuy2;
        return true;
    }
    return false;
}

bool MakeMediaType(const VideoFormat& format, AM_MEDIA_TYPE* out) {
    if (out == nullptr || format.subtype == nullptr) {
        return false;
    }

    std::memset(out, 0, sizeof(*out));
    out->majortype = MEDIATYPE_Video;
    out->subtype = *format.subtype;
    out->bFixedSizeSamples = TRUE;
    out->bTemporalCompression = FALSE;
    out->lSampleSize = VideoFormatImageSize(format);
    out->formattype = FORMAT_VideoInfo;
    out->pUnk = nullptr;

    auto* header = static_cast<VIDEOINFOHEADER*>(
        CoTaskMemAlloc(sizeof(VIDEOINFOHEADER)));
    if (header == nullptr) {
        return false;
    }
    std::memset(header, 0, sizeof(*header));

    header->rcSource.left = 0;
    header->rcSource.top = 0;
    header->rcSource.right = static_cast<LONG>(format.width);
    header->rcSource.bottom = static_cast<LONG>(format.height);
    header->rcTarget = header->rcSource;
    header->dwBitRate = VideoFormatImageSize(format) * 8u * format.fps;
    header->dwBitErrorRate = 0;
    header->AvgTimePerFrame =
        static_cast<REFERENCE_TIME>(10000000LL / static_cast<int64_t>(format.fps));

    header->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    header->bmiHeader.biWidth = static_cast<LONG>(format.width);
    // Positive height: the buffer is a bottom-up DIB, which is what every
    // DirectShow video renderer expects for RGB. The converters write RGB
    // bottom-up to match; YUV is top-down by convention.
    header->bmiHeader.biHeight = static_cast<LONG>(format.height);
    header->bmiHeader.biPlanes = 1;
    header->bmiHeader.biBitCount =
        static_cast<WORD>(VideoFormatBytesPerPixel(*format.subtype) * 8u);
    header->bmiHeader.biCompression =
        format.subtype == &MEDIASUBTYPE_YUY2 ? MAKEFOURCC('Y', 'U', 'Y', '2')
                                             : BI_RGB;
    header->bmiHeader.biSizeImage = VideoFormatImageSize(format);
    header->bmiHeader.biXPelsPerMeter = 0;
    header->bmiHeader.biYPelsPerMeter = 0;
    header->bmiHeader.biClrUsed = 0;
    header->bmiHeader.biClrImportant = 0;

    out->cbFormat = sizeof(VIDEOINFOHEADER);
    out->pbFormat = reinterpret_cast<BYTE*>(header);
    return true;
}

bool MatchMediaType(const AM_MEDIA_TYPE* mediaType, VideoFormat* out) {
    if (mediaType == nullptr) {
        return false;
    }

    if (mediaType->majortype != MEDIATYPE_Video) {
        return false;
    }
    // FORMAT_VideoInfo only. FORMAT_VideoInfo2 is a different, incompatible
    // format block and we do not advertise it, so accepting it would mean
    // interpreting a VIDEOINFOHEADER2 as a VIDEOINFOHEADER.
    if (mediaType->formattype != FORMAT_VideoInfo) {
        return false;
    }
    if (mediaType->pbFormat == nullptr ||
        mediaType->cbFormat < sizeof(VIDEOINFOHEADER)) {
        return false;
    }

    PixelSubtype ignored = PixelSubtype::Rgb32;
    if (!SubtypeToPixelFormat(mediaType->subtype, &ignored)) {
        return false;
    }

    const auto* header =
        reinterpret_cast<const VIDEOINFOHEADER*>(mediaType->pbFormat);
    const LONG width = header->bmiHeader.biWidth;
    const LONG height = header->bmiHeader.biHeight;
    // Accept both orientations: a downstream filter may have flipped the sign to
    // ask for a top-down buffer, which is a layout detail, not a format change.
    const uint32_t absHeight = static_cast<uint32_t>(height < 0 ? -height : height);
    const uint32_t absWidth = static_cast<uint32_t>(width < 0 ? -width : width);

    if (absWidth == 0 || absHeight == 0) {
        return false;
    }

    REFERENCE_TIME interval = header->AvgTimePerFrame;
    if (interval <= 0) {
        interval = kDefaultFrameInterval;
    }

    for (int i = 0; i < kFormatCount; ++i) {
        const VideoFormat* candidate = VideoFormatAt(i);
        if (candidate == nullptr || candidate->subtype == nullptr) {
            continue;
        }
        if (mediaType->subtype != *candidate->subtype) {
            continue;
        }
        if (candidate->width != absWidth || candidate->height != absHeight) {
            continue;
        }
        const REFERENCE_TIME expected =
            static_cast<REFERENCE_TIME>(10000000LL / candidate->fps);
        // One percent tolerance: some filters round 29.97 and 30 together, and
        // rejecting a 33.36 ms interval for a 33.33 ms format helps nobody.
        const REFERENCE_TIME slack = expected / 100;
        if (interval > expected + slack || interval < expected - slack) {
            continue;
        }
        if (out != nullptr) {
            *out = *candidate;
        }
        return true;
    }

    return false;
}

// ---------------------------------------------------------------------------
// 3. MediaTypeEnumerator
// ---------------------------------------------------------------------------

MediaTypeEnumerator::MediaTypeEnumerator() = default;

MediaTypeEnumerator::~MediaTypeEnumerator() = default;

STDMETHODIMP MediaTypeEnumerator::QueryInterface(REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    *ppv = nullptr;

    if (riid == IID_IUnknown || riid == IID_IEnumMediaTypes) {
        *ppv = static_cast<IEnumMediaTypes*>(this);
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) MediaTypeEnumerator::AddRef() {
    return static_cast<ULONG>(refCount_.fetch_add(1) + 1);
}

STDMETHODIMP_(ULONG) MediaTypeEnumerator::Release() {
    const LONG remaining = refCount_.fetch_sub(1) - 1;
    if (remaining == 0) {
        delete this;
    }
    return static_cast<ULONG>(remaining);
}

STDMETHODIMP MediaTypeEnumerator::Next(ULONG count, AM_MEDIA_TYPE** mediaTypes,
                                       ULONG* fetched) {
    if (mediaTypes == nullptr) {
        return E_POINTER;
    }
    if (count > 1 && fetched == nullptr) {
        // The caller must be able to learn how many entries were produced.
        return E_POINTER;
    }

    ULONG produced = 0;
    while (produced < count && position_ < kFormatCount) {
        const VideoFormat* format = VideoFormatAt(position_);
        AM_MEDIA_TYPE* mediaType = nullptr;
        if (format != nullptr) {
            mediaType = static_cast<AM_MEDIA_TYPE*>(
                CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
            if (mediaType != nullptr && !MakeMediaType(*format, mediaType)) {
                CoTaskMemFree(mediaType);
                mediaType = nullptr;
            }
        }
        if (mediaType == nullptr) {
            // Out of memory part way through. Roll back the entries already
            // handed out so the caller never sees a partially filled array it
            // would then try to free.
            for (ULONG i = 0; i < produced; ++i) {
                DeleteMediaType(mediaTypes[i]);
                mediaTypes[i] = nullptr;
            }
            if (fetched != nullptr) {
                *fetched = 0;
            }
            return E_OUTOFMEMORY;
        }

        mediaTypes[produced] = mediaType;
        ++produced;
        ++position_;
    }

    if (fetched != nullptr) {
        *fetched = produced;
    }
    return produced == count ? S_OK : S_FALSE;
}

STDMETHODIMP MediaTypeEnumerator::Skip(ULONG count) {
    position_ += static_cast<int>(count);
    if (position_ > kFormatCount) {
        position_ = kFormatCount;
        return S_FALSE;
    }
    return S_OK;
}

STDMETHODIMP MediaTypeEnumerator::Reset() {
    position_ = 0;
    return S_OK;
}

STDMETHODIMP MediaTypeEnumerator::Clone(IEnumMediaTypes** enumerator) {
    if (enumerator == nullptr) {
        return E_POINTER;
    }
    *enumerator = nullptr;

    auto* clone = new (std::nothrow) MediaTypeEnumerator();
    if (clone == nullptr) {
        return E_OUTOFMEMORY;
    }
    clone->position_ = position_;
    *enumerator = static_cast<IEnumMediaTypes*>(clone);
    return S_OK;
}

// ---------------------------------------------------------------------------
// 4. StreamContext and the worker thread
// ---------------------------------------------------------------------------

// One streaming session. Created by StartStreaming, owned jointly by the pin
// (while it is the current session) and by the worker thread (which holds its
// own reference so the object survives a wedged downstream Receive).
struct StreamContext {
    std::atomic<LONG> refCount{1};

    // Thread plumbing. Owned by the context.
    HANDLE thread = nullptr;
    HANDLE stopEvent = nullptr;   // manual reset: signalled to end the session
    HANDLE frameEvent = nullptr;  // auto reset: signalled by the frame timer
    HANDLE timer = nullptr;

    std::atomic<bool> stop{false};
    std::atomic<bool> paused{false};
    std::atomic<bool> pauseRequested{false};

    // Negotiated stream, snapshotted so the thread never reads pin state.
    VideoFormat format{};
    PixelSubtype pixelFormat = PixelSubtype::Rgb32;
    int64_t frameInterval = kDefaultFrameInterval;
    LONG sampleStride = 0;
    uint32_t sampleBytes = 0;

    // Independently owned references: the pin may be destroyed while the thread
    // is still unwinding a downstream call.
    IMemAllocator* allocator = nullptr;
    IMemInputPin* inputPin = nullptr;
    std::shared_ptr<FrameSource> source;

    // The reference clock can be replaced after the session started (the graph
    // sets its sync source between Pause and Run), so the pointer is guarded by
    // its own lock rather than being read raw from the worker.
    CRITICAL_SECTION clockLock{};
    bool clockLockInitialised = false;
    IReferenceClock* clock = nullptr;

    std::atomic<uint64_t> framesDelivered{0};
    std::atomic<uint64_t> framesFromChannel{0};
    std::atomic<uint64_t> receiveFailures{0};
};

namespace {

void AddRefStream(StreamContext* context) {
    if (context != nullptr) {
        context->refCount.fetch_add(1);
    }
}

void ReleaseStream(StreamContext* context);

// Releases everything the context owns. Called exactly once, from the thread
// that drops the last reference.
void DestroyStreamContext(StreamContext* context) {
    if (context == nullptr) {
        return;
    }

    if (context->stopEvent != nullptr) {
        ::CloseHandle(context->stopEvent);
        context->stopEvent = nullptr;
    }
    if (context->frameEvent != nullptr) {
        ::CloseHandle(context->frameEvent);
        context->frameEvent = nullptr;
    }
    if (context->timer != nullptr) {
        ::CloseHandle(context->timer);
        context->timer = nullptr;
    }
    // The thread handle is closed by whoever joined it. If the thread was never
    // joined (join timed out) the handle stays open for the process lifetime by
    // design: closing it would be safe, but leaking it makes the "we gave up on
    // a wedged downstream" case obvious in a handle count.
    if (context->allocator != nullptr) {
        context->allocator->Release();
        context->allocator = nullptr;
    }
    if (context->inputPin != nullptr) {
        context->inputPin->Release();
        context->inputPin = nullptr;
    }
    if (context->clockLockInitialised) {
        ::EnterCriticalSection(&context->clockLock);
        if (context->clock != nullptr) {
            context->clock->Release();
            context->clock = nullptr;
        }
        ::LeaveCriticalSection(&context->clockLock);
        ::DeleteCriticalSection(&context->clockLock);
        context->clockLockInitialised = false;
    }
    context->source.reset();

    delete context;
}

void ReleaseStream(StreamContext* context) {
    if (context == nullptr) {
        return;
    }
    if (context->refCount.fetch_sub(1) - 1 == 0) {
        DestroyStreamContext(context);
    }
}

// Fills one sample from the frame source and hands it downstream.
// Returns true when a frame was delivered.
bool DeliverOneFrame(StreamContext* context, FrameClock& clock,
                     int64_t frameInterval, uint64_t frameIndex) {
    IMediaSample* sample = nullptr;
    HRESULT hr = context->allocator->GetBuffer(&sample, nullptr, nullptr, 0);
    if (FAILED(hr) || sample == nullptr) {
        if (sample != nullptr) {
            sample->Release();
        }
        // A full allocator is normal backpressure, not an error. Wait a frame
        // interval's worth of time before retrying so we do not spin.
        ::Sleep(static_cast<DWORD>(
            std::max<int64_t>(1, frameInterval / 10000)));
        return false;
    }

    BYTE* buffer = nullptr;
    hr = sample->GetPointer(&buffer);
    if (FAILED(hr) || buffer == nullptr) {
        sample->Release();
        return false;
    }

    const LONG actualSize = sample->GetSize();
    if (actualSize <= 0) {
        sample->Release();
        return false;
    }

    FrameRequest request;
    request.width = context->format.width;
    request.height = context->format.height;
    request.subtype = context->pixelFormat;
    request.dest = buffer;
    // RGB samples are bottom-up DIBs (negative stride), YUV is top-down. This
    // matches the biHeight we advertised in MakeMediaType.
    request.destStride =
        context->pixelFormat == PixelSubtype::Yuy2
            ? context->sampleStride
            : -context->sampleStride;
    request.destBytes = static_cast<uint32_t>(actualSize);

    const bool fromChannel = context->source->Produce(request, frameIndex);

    // The sample buffer may be smaller than the negotiated image if a peer's
    // allocator shrank it. Produce() clamped the write to fit, so the only thing
    // left is to tell the sample how much data is real.
    uint32_t written = context->sampleBytes;
    if (written > static_cast<uint32_t>(actualSize)) {
        written = static_cast<uint32_t>(actualSize);
    }
    sample->SetActualDataLength(static_cast<LONG>(written));

    const REFERENCE_TIME start = clock.Next(frameInterval);
    const REFERENCE_TIME end = start + frameInterval;
    sample->SetTime(&start, &end);
    sample->SetMediaTime(nullptr, nullptr);
    // A live source marks every sample as a sync point: there is no keyframe
    // structure to respect and some muxers refuse to start without one.
    sample->SetSyncPoint(TRUE);
    sample->SetDiscontinuity(FALSE);
    sample->SetPreroll(FALSE);

    hr = context->inputPin->Receive(sample);
    sample->Release();

    if (FAILED(hr)) {
        context->receiveFailures.fetch_add(1);
        if (fromChannel) {
            context->framesFromChannel.fetch_add(1);
        }
        return false;
    }

    if (fromChannel) {
        context->framesFromChannel.fetch_add(1);
    }
    context->framesDelivered.fetch_add(1);
    return true;
}

// The streaming loop.
void RunStream(StreamContext* context) {
    // Raise the thread priority above normal so a busy UI thread cannot starve
    // frame delivery, but stay below time-critical: a webcam has no business
    // preempting audio.
    ::SetThreadPriority(::GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);

    // Balance timeBeginPeriod for exactly as long as the session lives.
    TimerResolutionScope timerResolution;

    FrameClock clock;
    clock.Start();

    uint64_t frameIndex = 0;
    bool haveStreamStart = false;
    REFERENCE_TIME streamStart = 0;
    IReferenceClock* streamClock = nullptr;
    bool wasPaused = false;

    while (!context->stop.load(std::memory_order_acquire)) {
        if (context->paused.load(std::memory_order_acquire)) {
            // Stay responsive to Stop while paused. Rebasing the clock on resume
            // prevents a burst of catch-up frames after a long pause.
            wasPaused = true;
            ::WaitForSingleObject(context->stopEvent, 20);
            continue;
        }
        if (wasPaused) {
            wasPaused = false;
            haveStreamStart = false;
            clock.Start();
        }

        // --- Pacing -----------------------------------------------------------
        // Read the negotiated interval once per iteration. The pin snapshots it
        // at start, but a Stop/Start cycle rebuilds the context, so this is only
        // ever changed while we are not running.
        const int64_t frameInterval = context->frameInterval;

        // Preferred: the graph's reference clock. Fall back to a plain frame
        // interval when no clock was supplied or it has no valid time yet.
        int64_t waitFor = frameInterval;
        IReferenceClock* graphClock = nullptr;
        {
            ::EnterCriticalSection(&context->clockLock);
            graphClock = context->clock;
            if (graphClock != nullptr) {
                graphClock->AddRef();
            }
            ::LeaveCriticalSection(&context->clockLock);
        }
        // A changed sync source invalidates the segment origin: rebasing avoids
        // a burst of catch-up frames (or a long stall) after the graph swaps it.
        if (graphClock != streamClock) {
            haveStreamStart = false;
        }
        if (streamClock != nullptr) {
            streamClock->Release();
        }
        streamClock = graphClock;

        if (graphClock != nullptr) {
            REFERENCE_TIME now = 0;
            if (SUCCEEDED(graphClock->GetTime(&now))) {
                if (!haveStreamStart) {
                    streamStart = now;
                    haveStreamStart = true;
                }
                const REFERENCE_TIME target =
                    streamStart +
                    static_cast<REFERENCE_TIME>(frameIndex) * frameInterval;
                waitFor = static_cast<int64_t>(target - now);
            }
        }

        if (context->timer != nullptr) {
            if (waitFor > 0) {
                LARGE_INTEGER due = RelativeDueTime(waitFor);
                if (::SetWaitableTimer(context->timer, &due, 0, nullptr, nullptr,
                                       FALSE)) {
                    HANDLE waits[2] = {context->stopEvent, context->frameEvent};
                    ::WaitForMultipleObjects(2, waits, FALSE, 100);
                } else {
                    ::WaitForSingleObject(
                        context->stopEvent,
                        static_cast<DWORD>(
                            std::max<int64_t>(1, waitFor / 10000)));
                }
            }
        } else if (waitFor > 0) {
            // No waitable timer at all (an extremely unlikely allocation
            // failure). Sleep the remaining interval, but never longer than one
            // frame: a plain Sleep is not interruptible by the stop event, so
            // chunking it keeps StopStreaming responsive.
            DWORD sleepMs =
                static_cast<DWORD>(std::max<int64_t>(1, waitFor / 10000));
            if (sleepMs > 20) {
                sleepMs = 20;
            }
            ::Sleep(sleepMs);
        }

        if (context->stop.load(std::memory_order_acquire)) {
            break;
        }

        if (!DeliverOneFrame(context, clock, frameInterval, frameIndex)) {
            // Do not advance the frame index on failure: the test pattern's
            // sweep bar should not skip ahead when a buffer was unavailable.
            continue;
        }
        ++frameIndex;
    }

    if (context->allocator != nullptr) {
        // Best effort: decommit so a downstream that is still holding the
        // allocator can reuse it cleanly if the graph restarts.
        context->allocator->Decommit();
    }

    // Drop the FrameSource here, on the thread that is allowed to touch it. If
    // we left it to DestroyStreamContext, the context could be destroyed on a
    // different thread while this one is still winding down.
    context->source.reset();

    if (streamClock != nullptr) {
        streamClock->Release();
        streamClock = nullptr;
    }
}

DWORD WINAPI StreamThreadProc(void* parameter) {
    auto* context = static_cast<StreamContext*>(parameter);
    // The creating thread transferred its reference to us; drop it on the way
    // out. This is what keeps the context alive across a wedged Receive().
    RunStream(context);
    ReleaseStream(context);
    return 0;
}

// Signals stop and waits a bounded time for the thread to exit. Returns true
// when the thread was joined and its handle closed.
bool StopAndJoinStream(StreamContext* context, DWORD timeoutMs) {
    if (context == nullptr) {
        return true;
    }

    context->stop.store(true, std::memory_order_release);
    if (context->stopEvent != nullptr) {
        ::SetEvent(context->stopEvent);
    }
    if (context->timer != nullptr) {
        ::CancelWaitableTimer(context->timer);
    }

    if (context->thread == nullptr) {
        return true;
    }

    const DWORD result = ::WaitForSingleObject(context->thread, timeoutMs);
    if (result == WAIT_OBJECT_0) {
        ::CloseHandle(context->thread);
        context->thread = nullptr;
        return true;
    }

    // The downstream filter is not returning from Receive(). Rather than hang
    // the graph -- and with it the user's player -- we abandon the wait. The
    // thread keeps its own reference on the context, sees `stop` set, and exits
    // on its own as soon as the downstream call returns.
    return false;
}

}  // namespace

// ---------------------------------------------------------------------------
// 5. SeewoOutputPin
// ---------------------------------------------------------------------------

SeewoOutputPin::SeewoOutputPin(SeewoDShowFilter* filter) : filter_(filter) {
    ::InitializeCriticalSection(&lock_);
    lockInitialised_ = true;

    // Seed the negotiated format with the first advertised entry so
    // GetAllocatorRequirements and GetFormat have a truthful answer before any
    // connection is made. Assigned in the body rather than the member-init list
    // because format_ is declared after the other members and /W4 flags
    // out-of-order initialisation.
    const VideoFormat* first = VideoFormatAt(0);
    if (first != nullptr) {
        format_ = *first;
    }

    // Every live pin pins the DLL in memory. DllCanUnloadNow consults this, so
    // COM can never unload us while a streaming thread is executing our code.
    LockModule();
}

SeewoOutputPin::~SeewoOutputPin() {
    // Stop streaming first: the worker holds refs on the allocator and the
    // downstream pin, and those must be released before we tear the connection
    // down. TeardownStreaming never blocks indefinitely.
    TeardownStreaming();

    if (clock_ != nullptr) {
        clock_->Release();
        clock_ = nullptr;
    }
    if (qualitySink_ != nullptr) {
        qualitySink_->Release();
        qualitySink_ = nullptr;
    }
    if (allocator_ != nullptr) {
        allocator_->Release();
        allocator_ = nullptr;
    }
    if (inputPin_ != nullptr) {
        inputPin_->Release();
        inputPin_ = nullptr;
    }
    if (connectedPin_ != nullptr) {
        connectedPin_->Release();
        connectedPin_ = nullptr;
    }

    FreeMediaTypeBlock(connectionType_);
    FreeMediaTypeBlock(requestedType_);

    if (lockInitialised_) {
        ::DeleteCriticalSection(&lock_);
        lockInitialised_ = false;
    }

    // Paired with the constructor. The module cannot unload before this runs,
    // because DllCanUnloadNow returns S_FALSE while the lock is held.
    UnlockModule();
}

// --- IUnknown ---------------------------------------------------------------

STDMETHODIMP SeewoOutputPin::QueryInterface(REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    *ppv = nullptr;

    // The pin is a non-delegating object: it hands out its own interfaces and
    // never forwards to the filter. Forwarding would make the pin's lifetime
    // depend on the filter's, which is exactly the coupling that lets a graph
    // hold a dangling pin during teardown.
    if (riid == IID_IUnknown || riid == IID_IPin) {
        *ppv = static_cast<IPin*>(this);
    } else if (riid == IID_IMemInputPin) {
        *ppv = static_cast<IMemInputPin*>(this);
    } else if (riid == IID_IAMStreamConfig) {
        *ppv = static_cast<IAMStreamConfig*>(this);
    } else if (riid == IID_IAMBufferNegotiation) {
        *ppv = static_cast<IAMBufferNegotiation*>(this);
    } else if (riid == IID_IQualityControl) {
        *ppv = static_cast<IQualityControl*>(this);
    } else {
        return E_NOINTERFACE;
    }

    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) SeewoOutputPin::AddRef() {
    return static_cast<ULONG>(refCount_.fetch_add(1) + 1);
}

STDMETHODIMP_(ULONG) SeewoOutputPin::Release() {
    const LONG remaining = refCount_.fetch_sub(1) - 1;
    if (remaining == 0) {
        delete this;
    }
    return static_cast<ULONG>(remaining);
}

// --- IPin: connection -------------------------------------------------------

STDMETHODIMP SeewoOutputPin::Connect(IPin* receivePin,
                                     const AM_MEDIA_TYPE* mediaType) {
    if (receivePin == nullptr) {
        return E_POINTER;
    }

    PIN_DIRECTION direction = PINDIR_INPUT;
    if (FAILED(receivePin->QueryDirection(&direction)) ||
        direction != PINDIR_INPUT) {
        return VFW_E_INVALID_DIRECTION;
    }

    // Phase 1a: decide the candidate format without holding the lock, because
    // MatchMediaType is pure and DuplicateMediaType only allocates.
    VideoFormat format{};
    AM_MEDIA_TYPE chosen{};
    bool haveChosen = false;

    {
        PinLock guard(lock_);
        if (connected_) {
            return VFW_E_ALREADY_CONNECTED;
        }
    }

    if (mediaType != nullptr) {
        if (!MatchMediaType(mediaType, &format)) {
            return VFW_E_TYPE_NOT_ACCEPTED;
        }
        AM_MEDIA_TYPE* copy = DuplicateMediaType(mediaType);
        if (copy == nullptr) {
            return E_OUTOFMEMORY;
        }
        // Steal the format block rather than copying twice: `chosen` takes over
        // pbFormat and the bare struct allocation is freed.
        chosen = *copy;
        CoTaskMemFree(copy);
        haveChosen = true;
    } else {
        // No type supplied: walk our table and take the first one the peer
        // accepts. QueryAccept calls into the peer, so this loop must run with
        // no lock held -- a peer that re-enters the pin (some renderers query
        // the connection media type during QueryAccept) would otherwise
        // deadlock against us.
        for (int i = 0; i < VideoFormatCount(); ++i) {
            const VideoFormat* candidate = VideoFormatAt(i);
            if (candidate == nullptr) {
                continue;
            }
            AM_MEDIA_TYPE probe{};
            if (!MakeMediaType(*candidate, &probe)) {
                continue;
            }
            if (receivePin->QueryAccept(&probe) == S_OK) {
                format = *candidate;
                chosen = probe;
                haveChosen = true;
                break;
            }
            FreeMediaTypeBlock(probe);
        }
    }

    if (!haveChosen) {
        return VFW_E_NO_ACCEPTABLE_TYPES;
    }

    // Phase 2: hand the type to the peer. Still no lock held, so a re-entrant
    // call back into this pin cannot deadlock.
    HRESULT hr = receivePin->ReceiveConnection(this, &chosen);
    if (FAILED(hr)) {
        FreeMediaTypeBlock(chosen);
        return hr;
    }

    IMemInputPin* inputPin = nullptr;
    hr = receivePin->QueryInterface(IID_IMemInputPin,
                                    reinterpret_cast<void**>(&inputPin));
    if (FAILED(hr) || inputPin == nullptr) {
        // The peer accepted the connection but cannot receive samples. Undo it
        // rather than leave the graph in a half-connected state.
        receivePin->Disconnect();
        FreeMediaTypeBlock(chosen);
        return VFW_E_INVALID_DIRECTION;
    }

    // Phase 3: commit. If somebody beat us to it, undo cleanly.
    {
        PinLock guard(lock_);
        if (connected_) {
            FreeMediaTypeBlock(chosen);
            inputPin->Release();
            receivePin->Disconnect();
            return VFW_E_ALREADY_CONNECTED;
        }

        FreeMediaTypeBlock(connectionType_);
        connectionType_ = chosen;  // takes ownership of pbFormat
        hasConnectionType_ = true;

        // If the caller never called IAMStreamConfig::SetFormat, adopt the
        // negotiated type as the requested one so GetFormat reports the truth.
        if (!hasRequestedType_) {
            AM_MEDIA_TYPE* copy = DuplicateMediaType(&connectionType_);
            if (copy != nullptr) {
                requestedType_ = *copy;
                CoTaskMemFree(copy);
                hasRequestedType_ = true;
            }
        }

        format_ = format;
        hasFormat_ = true;
        connectedPin_ = receivePin;
        connectedPin_->AddRef();
        inputPin_ = inputPin;  // ownership transferred from QueryInterface
        connected_ = true;
    }

    return S_OK;
}

STDMETHODIMP SeewoOutputPin::ReceiveConnection(IPin* connector,
                                               const AM_MEDIA_TYPE* mediaType) {
    // We are an output pin. Receiving a connection is the input pin's job.
    (void)connector;
    (void)mediaType;
    return VFW_E_INVALID_DIRECTION;
}

STDMETHODIMP SeewoOutputPin::Disconnect() {
    // Take the state under the lock, then call out without it.
    IPin* peer = nullptr;
    IMemInputPin* input = nullptr;
    IMemAllocator* allocator = nullptr;
    {
        PinLock guard(lock_);
        if (!connected_) {
            return S_FALSE;
        }
        peer = connectedPin_;
        connectedPin_ = nullptr;
        input = inputPin_;
        inputPin_ = nullptr;
        allocator = allocator_;
        allocator_ = nullptr;
        connected_ = false;
        hasConnectionType_ = false;
    }

    // Stop first: the worker is using the allocator and the input pin.
    TeardownStreaming();

    if (peer != nullptr) {
        peer->Disconnect();
        peer->Release();
    }
    if (input != nullptr) {
        input->Release();
    }
    if (allocator != nullptr) {
        allocator->Release();
    }

    FreeMediaTypeBlock(connectionType_);
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::ConnectedTo(IPin** pin) {
    if (pin == nullptr) {
        return E_POINTER;
    }
    *pin = nullptr;

    PinLock guard(lock_);
    if (!connected_ || connectedPin_ == nullptr) {
        return VFW_E_NOT_CONNECTED;
    }
    *pin = connectedPin_;
    (*pin)->AddRef();
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::ConnectionMediaType(AM_MEDIA_TYPE* mediaType) {
    if (mediaType == nullptr) {
        return E_POINTER;
    }
    std::memset(mediaType, 0, sizeof(*mediaType));

    PinLock guard(lock_);
    if (!connected_ || !hasConnectionType_) {
        return VFW_E_NOT_CONNECTED;
    }
    AM_MEDIA_TYPE* copy = DuplicateMediaType(&connectionType_);
    if (copy == nullptr) {
        return E_OUTOFMEMORY;
    }
    *mediaType = *copy;
    CoTaskMemFree(copy);
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::QueryPinInfo(PIN_INFO* info) {
    if (info == nullptr) {
        return E_POINTER;
    }

    // PIN_INFO::pFilter is an AddRef'd out-parameter, exactly as
    // CBasePin::QueryPinInfo does it. Handing back a borrowed pointer would make
    // a caller that follows the documented contract release a reference we never
    // gave it, freeing the filter while the graph is still using it. The AddRef
    // is taken without our lock held so it can never deadlock us.
    SeewoDShowFilter* filter = filter_;
    info->pFilter = filter;
    if (filter != nullptr) {
        filter->AddRef();
    }
    info->dir = PINDIR_OUTPUT;
    ::StringCchCopyW(info->achName, NUMELMS(info->achName),
                     L"Capture");
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::QueryDirection(PIN_DIRECTION* direction) {
    if (direction == nullptr) {
        return E_POINTER;
    }
    *direction = PINDIR_OUTPUT;
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::QueryId(LPWSTR* id) {
    if (id == nullptr) {
        return E_POINTER;
    }
    *id = nullptr;

    const wchar_t* name = L"Capture";
    const size_t bytes = (wcslen(name) + 1) * sizeof(wchar_t);
    auto* buffer = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
    if (buffer == nullptr) {
        return E_OUTOFMEMORY;
    }
    std::memcpy(buffer, name, bytes);
    *id = buffer;
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::QueryAccept(const AM_MEDIA_TYPE* mediaType) {
    return MatchMediaType(mediaType, nullptr) ? S_OK : S_FALSE;
}

STDMETHODIMP SeewoOutputPin::EnumMediaTypes(IEnumMediaTypes** enumerator) {
    if (enumerator == nullptr) {
        return E_POINTER;
    }
    *enumerator = nullptr;

    auto* created = new (std::nothrow) MediaTypeEnumerator();
    if (created == nullptr) {
        return E_OUTOFMEMORY;
    }
    *enumerator = static_cast<IEnumMediaTypes*>(created);
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::QueryInternalConnections(IPin** pins, ULONG* count) {
    (void)pins;
    if (count == nullptr) {
        return E_POINTER;
    }
    // A one-pin filter has no internal connections to report. Returning a count
    // of zero with a null array is the documented answer for that case.
    *count = 0;
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::EndOfStream() {
    // A live source never ends. Accept and ignore: some downstream filters
    // forward EOS back up the chain during teardown and would treat a failure
    // here as a broken graph.
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::BeginFlush() {
    // Downstream asked us to stop delivering. Pause rather than tear the session
    // down so EndFlush can resume without renegotiating the allocator.
    PinLock guard(lock_);
    if (context_ != nullptr) {
        context_->paused.store(true, std::memory_order_release);
        context_->pauseRequested.store(true, std::memory_order_release);
    }
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::EndFlush() {
    PinLock guard(lock_);
    if (context_ != nullptr) {
        context_->pauseRequested.store(false, std::memory_order_release);
        context_->paused.store(false, std::memory_order_release);
    }
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::NewSegment(REFERENCE_TIME start,
                                        REFERENCE_TIME stop, double rate) {
    // Live sources do not segment; the graph's segment start is already folded
    // into the timestamps we produce. Accept and ignore.
    (void)start;
    (void)stop;
    (void)rate;
    return S_OK;
}

// --- IMemInputPin -----------------------------------------------------------

STDMETHODIMP SeewoOutputPin::GetAllocator(IMemAllocator** allocator) {
    if (allocator == nullptr) {
        return E_POINTER;
    }
    *allocator = nullptr;

    PinLock guard(lock_);
    if (allocator_ == nullptr) {
        // Lazily create our own. Downstream sometimes asks for the allocator
        // before it has provided one, and handing back a usable allocator is
        // friendlier than failing.
        IMemAllocator* created = nullptr;
        const HRESULT hr = CoCreateInstance(
            CLSID_MemoryAllocator, nullptr, CLSCTX_INPROC_SERVER,
            IID_IMemAllocator, reinterpret_cast<void**>(&created));
        if (FAILED(hr) || created == nullptr) {
            return VFW_E_NOT_CONNECTED;
        }
        allocator_ = created;
        allocatorProvidedByPeer_ = false;
    }

    allocator_->AddRef();
    *allocator = allocator_;
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::NotifyAllocator(IMemAllocator* allocator,
                                             BOOL readOnly) {
    (void)readOnly;  // A source only writes; the read-only hint does not apply.

    PinLock guard(lock_);
    if (allocator == nullptr) {
        return E_POINTER;
    }
    if (connected_) {
        // The graph already picked an allocator for this connection. Changing it
        // mid-stream would invalidate samples the worker is holding.
        return allocator == allocator_ ? S_OK : VFW_E_ALREADY_CONNECTED;
    }

    allocator->AddRef();
    if (allocator_ != nullptr) {
        allocator_->Release();
    }
    allocator_ = allocator;
    allocatorProvidedByPeer_ = true;
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::GetAllocatorRequirements(
    ALLOCATOR_PROPERTIES* properties) {
    if (properties == nullptr) {
        return E_POINTER;
    }
    std::memset(properties, 0, sizeof(*properties));

    PinLock guard(lock_);
    const uint32_t imageSize = VideoFormatImageSize(format_);
    properties->cBuffers = kDefaultBufferCount;
    properties->cbBuffer = static_cast<LONG>(imageSize);
    properties->cbAlign = 1;
    properties->cbPrefix = 0;
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::Receive(IMediaSample* sample) {
    (void)sample;
    // We are a source. Anything arriving here is a peer that has confused its
    // direction; accepting it silently is the least disruptive response and is
    // what a well-behaved source pin does.
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::ReceiveMultiple(IMediaSample** samples, LONG count,
                                             LONG* processed) {
    (void)samples;
    if (processed != nullptr) {
        *processed = count;
    }
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::ReceiveCanBlock() {
    // We never block the upstream side: there is no upstream side.
    return S_FALSE;
}

// --- IAMStreamConfig --------------------------------------------------------

STDMETHODIMP SeewoOutputPin::SetFormat(AM_MEDIA_TYPE* mediaType) {
    if (mediaType == nullptr) {
        return E_POINTER;
    }

    VideoFormat format{};
    if (!MatchMediaType(mediaType, &format)) {
        return VFW_E_INVALIDMEDIATYPE;
    }

    AM_MEDIA_TYPE* copy = DuplicateMediaType(mediaType);
    if (copy == nullptr) {
        return E_OUTOFMEMORY;
    }

    PinLock guard(lock_);
    if (connected_) {
        // Changing the format under a live connection would desynchronise the
        // allocator and the downstream filter's expectations. The caller must
        // disconnect first, which is the normal capture-app sequence.
        const bool same = hasConnectionType_ &&
                          connectionType_.subtype == copy->subtype &&
                          connectionType_.cbFormat == copy->cbFormat &&
                          std::memcmp(connectionType_.pbFormat, copy->pbFormat,
                                      copy->cbFormat) == 0;
        if (!same) {
            DeleteMediaType(copy);
            return VFW_E_WRONG_STATE;
        }
    }

    FreeMediaTypeBlock(requestedType_);
    requestedType_ = *copy;
    CoTaskMemFree(copy);
    hasRequestedType_ = true;
    format_ = format;
    hasFormat_ = true;
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::GetFormat(AM_MEDIA_TYPE** mediaType) {
    if (mediaType == nullptr) {
        return E_POINTER;
    }
    *mediaType = nullptr;

    PinLock guard(lock_);

    const AM_MEDIA_TYPE* source = nullptr;
    if (connected_ && hasConnectionType_) {
        source = &connectionType_;
    } else if (hasRequestedType_) {
        source = &requestedType_;
    }

    if (source != nullptr) {
        AM_MEDIA_TYPE* copy = DuplicateMediaType(source);
        if (copy == nullptr) {
            return E_OUTOFMEMORY;
        }
        *mediaType = copy;
        return S_OK;
    }

    const VideoFormat* first = VideoFormatAt(0);
    if (first == nullptr) {
        return E_FAIL;
    }
    auto* created =
        static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
    if (created == nullptr) {
        return E_OUTOFMEMORY;
    }
    if (!MakeMediaType(*first, created)) {
        CoTaskMemFree(created);
        return E_OUTOFMEMORY;
    }
    *mediaType = created;
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::GetNumberOfCapabilities(int* count, int* size) {
    if (count == nullptr || size == nullptr) {
        return E_POINTER;
    }
    *count = VideoFormatCount();
    *size = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::GetStreamCaps(int index,
                                           AM_MEDIA_TYPE** mediaType,
                                           BYTE* capabilities) {
    if (mediaType == nullptr || capabilities == nullptr) {
        return E_POINTER;
    }
    *mediaType = nullptr;

    const VideoFormat* format = VideoFormatAt(index);
    if (format == nullptr) {
        return E_INVALIDARG;
    }

    auto* created =
        static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
    if (created == nullptr) {
        return E_OUTOFMEMORY;
    }
    if (!MakeMediaType(*format, created)) {
        CoTaskMemFree(created);
        return E_OUTOFMEMORY;
    }

    const REFERENCE_TIME interval =
        static_cast<REFERENCE_TIME>(10000000LL / static_cast<int64_t>(format->fps));

    // VIDEO_STREAM_CONFIG_CAPS describes a *range* of supported settings for one
    // format. Each entry here is fully constrained, so min == max everywhere.
    //
    // The struct has exactly these members and no more: the interlace flags,
    // picture aspect ratio, and Min/MaxSampleSize fields people reach for here
    // belong to VIDEOINFOHEADER2, not to this structure.
    auto* caps = reinterpret_cast<VIDEO_STREAM_CONFIG_CAPS*>(capabilities);
    std::memset(caps, 0, sizeof(*caps));
    caps->guid = FORMAT_VideoInfo;
    caps->VideoStandard = 0;  // AnalogVideo_None
    caps->InputSize.cx = static_cast<LONG>(format->width);
    caps->InputSize.cy = static_cast<LONG>(format->height);
    caps->MinCroppingSize = caps->InputSize;
    caps->MaxCroppingSize = caps->InputSize;
    caps->CropGranularityX = 1;
    caps->CropGranularityY = 1;
    caps->CropAlignX = 0;
    caps->CropAlignY = 0;
    caps->MinOutputSize.cx = static_cast<LONG>(format->width);
    caps->MinOutputSize.cy = static_cast<LONG>(format->height);
    caps->MaxOutputSize = caps->MinOutputSize;
    caps->OutputGranularityX = 1;
    caps->OutputGranularityY = 1;
    caps->StretchTapsX = 0;
    caps->StretchTapsY = 0;
    caps->ShrinkTapsX = 0;
    caps->ShrinkTapsY = 0;
    caps->MinFrameInterval = interval;
    caps->MaxFrameInterval = interval;
    caps->MinBitsPerSecond = 0;
    caps->MaxBitsPerSecond = 0;

    *mediaType = created;
    return S_OK;
}

// --- IAMBufferNegotiation ---------------------------------------------------

STDMETHODIMP SeewoOutputPin::SuggestAllocatorProperties(
    const ALLOCATOR_PROPERTIES* properties) {
    if (properties == nullptr) {
        return E_POINTER;
    }

    PinLock guard(lock_);
    if (connected_ && allocator_ != nullptr) {
        // Already streaming or committed: the hint arrived too late to matter.
        return VFW_E_ALREADY_CONNECTED;
    }

    suggestedProperties_ = *properties;
    // Sanitise: a zero or negative buffer count or size is a hint we cannot
    // honour, and passing it straight through would make SetProperties fail.
    if (suggestedProperties_.cBuffers < 1) {
        suggestedProperties_.cBuffers = kDefaultBufferCount;
    }
    if (suggestedProperties_.cbBuffer < 0) {
        suggestedProperties_.cbBuffer = 0;
    }
    if (suggestedProperties_.cbAlign <= 0) {
        suggestedProperties_.cbAlign = 1;
    }
    if (suggestedProperties_.cbPrefix < 0) {
        suggestedProperties_.cbPrefix = 0;
    }
    hasSuggestedProperties_ = true;
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::GetAllocatorProperties(
    ALLOCATOR_PROPERTIES* properties) {
    if (properties == nullptr) {
        return E_POINTER;
    }

    PinLock guard(lock_);
    if (allocator_ != nullptr) {
        const HRESULT hr = allocator_->GetProperties(properties);
        if (SUCCEEDED(hr)) {
            return hr;
        }
    }
    return GetAllocatorRequirements(properties);
}

// --- IQualityControl --------------------------------------------------------

STDMETHODIMP SeewoOutputPin::Notify(IBaseFilter* sender, Quality quality) {
    // A live source has no quality-of-service knob: we cannot drop frames to
    // reduce bitrate without breaking the camera's real-time contract. The
    // correct behaviour is to accept the message and pass it to our own sink if
    // one was set, then do nothing.
    (void)sender;
    (void)quality;

    IQualityControl* sink = nullptr;
    {
        PinLock guard(lock_);
        sink = qualitySink_;
        if (sink != nullptr) {
            sink->AddRef();
        }
    }
    if (sink != nullptr) {
        sink->Notify(filter_, quality);
        sink->Release();
    }
    return S_OK;
}

STDMETHODIMP SeewoOutputPin::SetSink(IQualityControl* sink) {
    PinLock guard(lock_);
    if (sink != nullptr) {
        sink->AddRef();
    }
    if (qualitySink_ != nullptr) {
        qualitySink_->Release();
    }
    qualitySink_ = sink;
    return S_OK;
}

// --- Filter-facing streaming control ----------------------------------------

HRESULT SeewoOutputPin::StartStreaming(bool startPaused) {
    // Already running: nothing to do. Repeated Run() calls are normal.
    if (streaming_.load(std::memory_order_acquire)) {
        PinLock guard(lock_);
        if (context_ != nullptr) {
            context_->paused.store(startPaused, std::memory_order_release);
            context_->pauseRequested.store(false, std::memory_order_release);
        }
        return S_OK;
    }

    // Phase 1: allocate the context and take independent references on the
    // objects the worker will use. COM calls (SetProperties, Commit,
    // NotifyAllocator) happen outside the pin lock, because a peer is free to
    // call back into the pin from inside them and a non-recursive critical
    // section would deadlock.
    auto* context = new (std::nothrow) StreamContext();
    if (context == nullptr) {
        return E_OUTOFMEMORY;
    }

    IMemAllocator* allocator = nullptr;
    {
        PinLock guard(lock_);
        if (!connected_ || inputPin_ == nullptr) {
            delete context;
            return VFW_E_NOT_CONNECTED;
        }

        // GetAllocator may lazily create ours; if it fails there is no session.
        if (allocator_ == nullptr) {
            IMemAllocator* created = nullptr;
            HRESULT hr = CoCreateInstance(
                CLSID_MemoryAllocator, nullptr, CLSCTX_INPROC_SERVER,
                IID_IMemAllocator, reinterpret_cast<void**>(&created));
            if (FAILED(hr) || created == nullptr) {
                hr = inputPin_->GetAllocator(&created);
                if (FAILED(hr) || created == nullptr) {
                    delete context;
                    return VFW_E_NOT_CONNECTED;
                }
                allocatorProvidedByPeer_ = true;
            }
            allocator_ = created;
        }

        allocator = allocator_;
        allocator->AddRef();

        context->format = format_;
        context->pixelFormat = PixelSubtype::Rgb32;
        if (!SubtypeToPixelFormat(connectionType_.subtype,
                                  &context->pixelFormat)) {
            context->pixelFormat = PixelSubtype::Rgb32;
        }

        REFERENCE_TIME interval = kDefaultFrameInterval;
        if (connectionType_.pbFormat != nullptr &&
            connectionType_.cbFormat >= sizeof(VIDEOINFOHEADER)) {
            const auto* header = reinterpret_cast<const VIDEOINFOHEADER*>(
                connectionType_.pbFormat);
            if (header->AvgTimePerFrame > 0) {
                interval = header->AvgTimePerFrame;
            }
        }
        context->frameInterval = static_cast<int64_t>(interval);
        context->sampleStride = static_cast<LONG>(VideoFormatStride(format_));
        context->sampleBytes = VideoFormatImageSize(format_);

        context->inputPin = inputPin_;
        context->inputPin->AddRef();

        // The clock lock must exist before the thread starts, because the worker
        // takes it on every frame and SetSyncSource may retarget the session.
        ::InitializeCriticalSection(&context->clockLock);
        context->clockLockInitialised = true;
        if (clock_ != nullptr) {
            clock_->AddRef();
            context->clock = clock_;
        }

        // Snapshot the negotiation hints while we still hold the lock.
        ALLOCATOR_PROPERTIES requested{};
        requested.cBuffers = kDefaultBufferCount;
        requested.cbBuffer = static_cast<LONG>(context->sampleBytes);
        requested.cbAlign = 1;
        requested.cbPrefix = 0;
        if (hasSuggestedProperties_) {
            if (suggestedProperties_.cBuffers > 0) {
                requested.cBuffers = suggestedProperties_.cBuffers;
            }
            if (suggestedProperties_.cbBuffer > requested.cbBuffer) {
                requested.cbBuffer = suggestedProperties_.cbBuffer;
            }
            if (suggestedProperties_.cbAlign > 0) {
                requested.cbAlign = suggestedProperties_.cbAlign;
            }
            if (suggestedProperties_.cbPrefix > 0) {
                requested.cbPrefix = suggestedProperties_.cbPrefix;
            }
        }

        context->allocator = allocator;
        context->allocator->AddRef();

        // A Pause() transition brings the session up already paused, so the
        // worker cannot slip a frame out before the graph is ready for it.
        context->paused.store(startPaused, std::memory_order_release);

        // Commit the session under the lock so two concurrent Run() calls cannot
        // both start a worker. The flag is set before the peer calls below.
        streaming_.store(true, std::memory_order_release);
        context_ = context;
    }

    // Phase 2: peer calls, no lock held. `context` is kept alive by the local
    // reference taken here, so a concurrent Disconnect that retires the session
    // can drop the pin's reference without freeing the object under us.
    AddRefStream(context);

    HRESULT hr = PrepareAllocator(allocator);
    if (FAILED(hr)) {
        AbandonSession(context);
        ReleaseStream(context);
        // The local allocator reference taken in phase 1.
        allocator->Release();
        return hr;
    }

    context->source = std::shared_ptr<FrameSource>(
        new (std::nothrow) FrameSource());
    if (!context->source) {
        AbandonSession(context);
        ReleaseStream(context);
        allocator->Release();
        return E_OUTOFMEMORY;
    }

    context->stopEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    context->frameEvent = ::CreateEventW(nullptr, FALSE, FALSE, nullptr);
    context->timer = CreateFrameTimer();
    if (context->stopEvent == nullptr || context->frameEvent == nullptr) {
        AbandonSession(context);
        ReleaseStream(context);
        allocator->Release();
        return E_FAIL;
    }

    // The thread owns a reference from the moment it is created. Taking it
    // *before* CreateThread is deliberate: the thread can start running and even
    // finish before CreateThread returns, so it must not be the one to acquire
    // its own reference.
    AddRefStream(context);
    HANDLE thread =
        ::CreateThread(nullptr, 0, StreamThreadProc, context, 0, nullptr);
    if (thread == nullptr) {
        // Drop the thread's reference, then fall through to the normal cleanup,
        // which drops the pin's reference and the local one.
        ReleaseStream(context);
        AbandonSession(context);
        ReleaseStream(context);
        allocator->Release();
        return E_FAIL;
    }

    {
        PinLock guard(lock_);
        context->thread = thread;
        if (context_ != context) {
            // A concurrent Disconnect retired the session while we were
            // starting. Stop the thread we just created; the session's own
            // reference keeps `context` alive until it exits.
            StopAndJoinStream(context, kStreamJoinTimeoutMs);
        }
    }

    // Release the local references: the pin and the worker each hold one now.
    ReleaseStream(context);
    allocator->Release();
    return S_OK;
}

HRESULT SeewoOutputPin::PauseStreaming() {
    PinLock guard(lock_);
    if (context_ != nullptr) {
        // A flush-induced pause must survive a Pause() transition; only set the
        // pause when no flush is outstanding.
        if (!context_->pauseRequested.load(std::memory_order_acquire)) {
            context_->paused.store(true, std::memory_order_release);
        }
    }
    return S_OK;
}

HRESULT SeewoOutputPin::StopStreaming() {
    TeardownStreaming();
    return S_OK;
}

void SeewoOutputPin::SetSyncSource(IReferenceClock* clock) {
    PinLock guard(lock_);
    if (clock != nullptr) {
        clock->AddRef();
    }
    if (clock_ != nullptr) {
        clock_->Release();
    }
    clock_ = clock;

    // If a session is already live, retarget it too. The graph sets the sync
    // source after Pause and before Run, and our Pause starts the session, so
    // without this the worker would pace itself off the fallback timer forever.
    // The worker takes the context's own lock for one AddRef, so the swap is
    // safe against a thread that is mid-frame.
    if (context_ != nullptr && context_->clockLockInitialised) {
        ::EnterCriticalSection(&context_->clockLock);
        if (clock != nullptr) {
            clock->AddRef();
        }
        if (context_->clock != nullptr) {
            context_->clock->Release();
        }
        context_->clock = clock;
        ::LeaveCriticalSection(&context_->clockLock);
    }
}

bool SeewoOutputPin::IsConnected() const {
    PinLock guard(lock_);
    return connected_;
}

bool SeewoOutputPin::IsStreaming() const {
    return streaming_.load(std::memory_order_acquire);
}

// --- Internals --------------------------------------------------------------

HRESULT SeewoOutputPin::PrepareAllocator(IMemAllocator* allocator) {
    // Snapshot the pin state we need under the lock, then make the peer calls
    // without it. Disconnect may have retired the connection in the meantime, in
    // which case there is nothing to prepare.
    IMemInputPin* inputPin = nullptr;
    uint32_t imageSize = 0;
    ALLOCATOR_PROPERTIES requested{};
    {
        PinLock guard(lock_);
        if (inputPin_ == nullptr) {
            return VFW_E_NOT_CONNECTED;
        }
        inputPin = inputPin_;
        inputPin->AddRef();

        imageSize = VideoFormatImageSize(format_);
        requested.cBuffers = kDefaultBufferCount;
        requested.cbBuffer = static_cast<LONG>(imageSize);
        requested.cbAlign = 1;
        requested.cbPrefix = 0;

        if (hasSuggestedProperties_) {
            // Honour the peer's hint, but never below what one frame needs.
            if (suggestedProperties_.cBuffers > 0) {
                requested.cBuffers = suggestedProperties_.cBuffers;
            }
            if (suggestedProperties_.cbBuffer > requested.cbBuffer) {
                requested.cbBuffer = suggestedProperties_.cbBuffer;
            }
            if (suggestedProperties_.cbAlign > 0) {
                requested.cbAlign = suggestedProperties_.cbAlign;
            }
            if (suggestedProperties_.cbPrefix > 0) {
                requested.cbPrefix = suggestedProperties_.cbPrefix;
            }
        }
    }

    if (allocator == nullptr) {
        inputPin->Release();
        return VFW_E_NOT_CONNECTED;
    }

    ALLOCATOR_PROPERTIES actual{};
    HRESULT hr = allocator->SetProperties(&requested, &actual);
    if (FAILED(hr)) {
        // A peer-supplied allocator (the VMR, for instance) often refuses
        // SetProperties because it has already sized its pool. That is fine as
        // long as the existing buffers can hold one frame.
        hr = allocator->GetProperties(&actual);
        if (FAILED(hr) || actual.cbBuffer < static_cast<LONG>(imageSize)) {
            inputPin->Release();
            return VFW_E_BUFFER_UNDERFLOW;
        }
    } else if (actual.cbBuffer < static_cast<LONG>(imageSize)) {
        // The allocator rounded our request down below one frame. Nothing can
        // stream from buffers this small.
        inputPin->Release();
        return VFW_E_BUFFER_UNDERFLOW;
    }

    // The read-only flag is FALSE: we are the writer.
    hr = inputPin->NotifyAllocator(allocator, FALSE);
    if (FAILED(hr) && hr != E_NOTIMPL) {
        // A peer that does not care about allocator notification returns
        // E_NOTIMPL; anything else is a genuine refusal.
        inputPin->Release();
        return hr;
    }
    inputPin->Release();

    hr = allocator->Commit();
    if (FAILED(hr)) {
        return hr;
    }

    return S_OK;
}

void SeewoOutputPin::AbandonSession(StreamContext* context) {
    if (context == nullptr) {
        return;
    }

    bool owned = false;
    {
        PinLock guard(lock_);
        if (context_ == context) {
            context_ = nullptr;
            streaming_.store(false, std::memory_order_release);
            owned = true;
        }
    }

    // Stop the worker if one was created. StopAndJoinStream is safe to call on a
    // context with no thread: it just signals the stop event.
    StopAndJoinStream(context, kStreamJoinTimeoutMs);

    if (owned) {
        // Drop the pin's reference. The caller still holds its own, so the
        // context cannot be destroyed before this function returns.
        ReleaseStream(context);
    }
}

void SeewoOutputPin::TeardownStreaming() {
    StreamContext* context = nullptr;
    {
        PinLock guard(lock_);
        context = context_;
        context_ = nullptr;
        streaming_.store(false, std::memory_order_release);
    }

    if (context == nullptr) {
        return;
    }

    StopAndJoinStream(context, kStreamJoinTimeoutMs);
    // Drop the pin's reference. If the join timed out the thread still holds its
    // own reference and the context stays alive until that call returns.
    ReleaseStream(context);
}

}  // namespace dshow
}  // namespace seewo
