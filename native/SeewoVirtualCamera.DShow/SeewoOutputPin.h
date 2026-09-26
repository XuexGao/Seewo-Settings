// SeewoOutputPin.h
//
// The single capture output pin of the Seewo DirectShow virtual camera.
//
// This is a *push source* pin: once the filter graph is running, the pin's own
// worker thread fills samples from SeewoFrameSource and hands them to the
// downstream IMemInputPin at the negotiated frame interval. There is no
// IAsyncReader and no allocator-driven pull model, because a live camera has no
// "reader" to pull from -- frames arrive when they arrive.
//
// Deliberate design note: this pin does NOT derive from Microsoft's CBasePin or
// CBaseOutputPin. See README.md for the reasoning. Everything the interfaces
// require is implemented here directly.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <objbase.h>
#include <ole2.h>
#include <ocidl.h>
#include <dshow.h>
#include <strmif.h>
#include <uuids.h>
#include <amvideo.h>
#include <dvdmedia.h>
#include <strsafe.h>

#include <atomic>
#include <cstdint>
#include <cwchar>

#include "SeewoFrameSource.h"

namespace seewo {
namespace dshow {

// Array length helper. The DirectShow base classes provide NUMELMS, but we do
// not take a dependency on them, so we spell it ourselves. strmif.h has been
// known to define this too, so guard rather than risk a C4005 redefinition.
#ifndef NUMELMS
#define NUMELMS(array) (sizeof(array) / sizeof((array)[0]))
#endif

class SeewoDShowFilter;

// Opaque owner of one streaming session. Defined in SeewoOutputPin.cpp. The
// streaming thread holds a reference to it, which is what lets the pin tear down
// safely even when the thread is stuck inside a downstream Receive() call.
struct StreamContext;

// ---------------------------------------------------------------------------
// Media type helpers
// ---------------------------------------------------------------------------

// Deep-copies a media type, including the format block. pUnk is intentionally
// not carried over: a media type we hand out must not keep a filter alive.
AM_MEDIA_TYPE* DuplicateMediaType(const AM_MEDIA_TYPE* source);

// Frees only the format block, leaving the struct itself alone.
void FreeMediaTypeBlock(AM_MEDIA_TYPE& mediaType);

// Frees a media type allocated by DuplicateMediaType or by CoTaskMemAlloc.
void DeleteMediaType(AM_MEDIA_TYPE* mediaType);

// ---------------------------------------------------------------------------
// Advertised video formats
// ---------------------------------------------------------------------------

// One row of the format table: subtype x geometry x frame rate.
struct VideoFormat {
    const GUID* subtype;
    uint32_t width;
    uint32_t height;
    uint32_t fps;
};

// Total number of advertised formats (3 subtypes x 4 sizes x 2 rates).
int VideoFormatCount();

// Bounds-checked accessor. Returns nullptr when index is out of range.
const VideoFormat* VideoFormatAt(int index);

// Bytes per pixel of the packed representation (YUY2 counts 2).
uint32_t VideoFormatBytesPerPixel(const GUID& subtype);

// Row stride in bytes, rounded up to the next 4-byte boundary, which is what a
// VIDEOINFOHEADER promises for a bottom-up DIB.
uint32_t VideoFormatStride(const VideoFormat& format);

// Size of one frame in bytes as advertised in lSampleSize / biSizeImage.
uint32_t VideoFormatImageSize(const VideoFormat& format);

// Maps a media subtype GUID onto the converter enum. Returns false for anything
// we do not advertise.
bool SubtypeToPixelFormat(const GUID& subtype, PixelSubtype* out);

// Builds a complete AM_MEDIA_TYPE for a table entry, allocating the
// VIDEOINFOHEADER with CoTaskMemAlloc. Returns false on allocation failure.
bool MakeMediaType(const VideoFormat& format, AM_MEDIA_TYPE* out);

// Validates a media type against the table and, on success, reports the matching
// entry. Accepts any of the advertised geometry/rate combinations and also
// tolerates a format block that is larger than VIDEOINFOHEADER.
bool MatchMediaType(const AM_MEDIA_TYPE* mediaType, VideoFormat* out);

// ---------------------------------------------------------------------------
// Hand-written IEnumMediaTypes
// ---------------------------------------------------------------------------

// Snapshots the format table at construction time. Snapshotting rather than
// pointing back at the pin means the enumerator stays valid after the pin is
// disconnected or released, which several capture stacks rely on.
class MediaTypeEnumerator final : public IEnumMediaTypes {
public:
    MediaTypeEnumerator();
    ~MediaTypeEnumerator();

    // IUnknown
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;

    // IEnumMediaTypes
    STDMETHOD(Next)(ULONG count, AM_MEDIA_TYPE** mediaTypes,
                    ULONG* fetched) override;
    STDMETHOD(Skip)(ULONG count) override;
    STDMETHOD(Reset)() override;
    STDMETHOD(Clone)(IEnumMediaTypes** enumerator) override;

private:
    std::atomic<LONG> refCount_{1};
    int position_ = 0;
};

// ---------------------------------------------------------------------------
// Output pin
// ---------------------------------------------------------------------------

class SeewoOutputPin final : public IPin,
                             public IMemInputPin,
                             public IAMStreamConfig,
                             public IAMBufferNegotiation,
                             public IQualityControl {
public:
    explicit SeewoOutputPin(SeewoDShowFilter* filter);
    ~SeewoOutputPin();

    // IUnknown. The pin is a non-delegating COM object: it has its own lifetime,
    // and the filter holds one reference to it for as long as the filter exists.
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;

    // IPin
    STDMETHOD(Connect)(IPin* receivePin, const AM_MEDIA_TYPE* mediaType) override;
    STDMETHOD(ReceiveConnection)(IPin* connector,
                                 const AM_MEDIA_TYPE* mediaType) override;
    STDMETHOD(Disconnect)() override;
    STDMETHOD(ConnectedTo)(IPin** pin) override;
    STDMETHOD(ConnectionMediaType)(AM_MEDIA_TYPE* mediaType) override;
    STDMETHOD(QueryPinInfo)(PIN_INFO* info) override;
    STDMETHOD(QueryDirection)(PIN_DIRECTION* direction) override;
    STDMETHOD(QueryId)(LPWSTR* id) override;
    STDMETHOD(QueryAccept)(const AM_MEDIA_TYPE* mediaType) override;
    STDMETHOD(EnumMediaTypes)(IEnumMediaTypes** enumerator) override;
    STDMETHOD(QueryInternalConnections)(IPin** pins, ULONG* count) override;
    STDMETHOD(EndOfStream)() override;
    STDMETHOD(BeginFlush)() override;
    STDMETHOD(EndFlush)() override;
    STDMETHOD(NewSegment)(REFERENCE_TIME start, REFERENCE_TIME stop,
                          double rate) override;

    // IMemInputPin. A source pin exposes these so downstream can negotiate the
    // allocator through us, but we never accept samples: Receive is a sink for
    // the rare peer that pushes back.
    STDMETHOD(GetAllocator)(IMemAllocator** allocator) override;
    STDMETHOD(NotifyAllocator)(IMemAllocator* allocator, BOOL readOnly) override;
    STDMETHOD(GetAllocatorRequirements)(ALLOCATOR_PROPERTIES* properties) override;
    STDMETHOD(Receive)(IMediaSample* sample) override;
    STDMETHOD(ReceiveMultiple)(IMediaSample** samples, LONG count,
                               LONG* processed) override;
    STDMETHOD(ReceiveCanBlock)() override;

    // IAMStreamConfig
    STDMETHOD(SetFormat)(AM_MEDIA_TYPE* mediaType) override;
    STDMETHOD(GetFormat)(AM_MEDIA_TYPE** mediaType) override;
    STDMETHOD(GetNumberOfCapabilities)(int* count, int* size) override;
    STDMETHOD(GetStreamCaps)(int index, AM_MEDIA_TYPE** mediaType,
                             BYTE* capabilities) override;

    // IAMBufferNegotiation
    STDMETHOD(SuggestAllocatorProperties)(
        const ALLOCATOR_PROPERTIES* properties) override;
    STDMETHOD(GetAllocatorProperties)(ALLOCATOR_PROPERTIES* properties) override;

    // IQualityControl. A live source has nothing to adapt, so this is a
    // well-behaved sink that accepts and ignores every message.
    STDMETHOD(Notify)(IBaseFilter* sender, Quality quality) override;
    STDMETHOD(SetSink)(IQualityControl* sink) override;

    // --- Filter-facing streaming control -------------------------------------
    // Called by SeewoDShowFilter. All three are safe to call in any order and
    // from any thread. `startPaused` lets the graph's Pause() bring the session
    // up without racing a frame out before the pause flag is set.
    HRESULT StartStreaming(bool startPaused);
    HRESULT StopStreaming();
    HRESULT PauseStreaming();
    void SetSyncSource(IReferenceClock* clock);
    // --- Diagnostics ----------------------------------------------------------
    bool IsConnected() const;
    bool IsStreaming() const;

private:
    // Sizes and commits `allocator`, then tells downstream about it. Called with
    // no lock held: every one of these is a call into a peer COM object.
    HRESULT PrepareAllocator(IMemAllocator* allocator);

    // Releases the streaming session, waiting a bounded time for the thread.
    void TeardownStreaming();

    // Undoes a session whose start failed partway through. `context` must be
    // held by the caller's own reference. If the pin still owns the session slot
    // it is cleared and the slot reference dropped; if a concurrent Disconnect
    // already retired it, only the worker is stopped.
    void AbandonSession(StreamContext* context);

    // Non-owning: the filter outlives its pin because the filter holds the pin's
    // initial reference.
    SeewoDShowFilter* filter_ = nullptr;

    mutable CRITICAL_SECTION lock_{};
    bool lockInitialised_ = false;

    std::atomic<LONG> refCount_{1};

    // Connection state.
    IPin* connectedPin_ = nullptr;      // add-ref'd
    IMemInputPin* inputPin_ = nullptr;  // add-ref'd, same object as above
    IMemAllocator* allocator_ = nullptr;  // add-ref'd
    bool allocatorProvidedByPeer_ = false;
    bool connected_ = false;
    AM_MEDIA_TYPE connectionType_{};
    bool hasConnectionType_ = false;

    // Format requested through IAMStreamConfig before (or after) connection.
    AM_MEDIA_TYPE requestedType_{};
    bool hasRequestedType_ = false;

    // The table entry the pin will stream with. Initialised to the first
    // advertised format (1080p30 RGB32) in the constructor so
    // GetAllocatorRequirements and GetFormat can answer before any negotiation.
    VideoFormat format_{};
    bool hasFormat_ = false;

    // Buffer negotiation hints from IAMBufferNegotiation.
    ALLOCATOR_PROPERTIES suggestedProperties_{};
    bool hasSuggestedProperties_ = false;

    IReferenceClock* clock_ = nullptr;  // add-ref'd, may be null
    IQualityControl* qualitySink_ = nullptr;  // add-ref'd, may be null

    // Streaming session.
    StreamContext* context_ = nullptr;
    std::atomic<bool> streaming_{false};
};

}  // namespace dshow
}  // namespace seewo
