using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;

namespace SeewoAssistant.Core.Services.VirtualCamera;

/// <summary>
/// Mirror of <c>native/SeewoCommon/SeewoIpc.h</c>. Any change here must be made
/// there too, and the section name version suffix must be bumped so a stale
/// producer cannot be misread by a newer consumer.
/// </summary>
internal static class FrameChannelContract
{
    // These must match native/SeewoCommon/SeewoIpc.h byte for byte, including the
    // Global\ / Local\ prefix. A name without a prefix is created in the caller's
    // session namespace, so a prefix mismatch means the two sides create two
    // different objects and silently never communicate.
    // SharedChannelContractTests asserts these values.
    internal const string SectionName = @"Global\SeewoAssistant.VCam.Frame.v1";
    internal const string LocalSectionName = @"Local\SeewoAssistant.VCam.Frame.v1";
    internal const string DataEventName = @"Global\SeewoAssistant.VCam.DataReady.v1";
    internal const string LocalDataEventName = @"Local\SeewoAssistant.VCam.DataReady.v1";

    internal const uint Magic = 0x53435631;   // 'SCV1'
    internal const uint Version = 1;

    internal const int MaxWidth = 1920;
    internal const int MaxHeight = 1080;
    internal const int BytesPerPixel = 4;     // BGRA
    internal const int MaxFrameBytes = MaxWidth * MaxHeight * BytesPerPixel;
    internal const int PayloadOffset = 64;
    internal const int SectionBytes = PayloadOffset + MaxFrameBytes;

    internal const uint FormatBgra32 = 0;

    /// <summary>Producer states, matching <c>seewo::VcamSourceState</c>.</summary>
    internal const uint StateIdle = 0;
    internal const uint StateLive = 1;
}

/// <summary>
/// Header layout, matching <c>seewo::VcamFrameHeader</c> (packed to 8 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct FrameHeader
{
    public uint Magic;
    public uint Version;
    public uint Width;
    public uint Height;
    public uint Stride;
    public uint Format;
    public uint FrameIndex;
    public uint SourceState;
    public ulong TimestampMs;
    public uint PayloadBytes;
    public uint Reserved0;
    public ulong Reserved1;
}

/// <summary>
/// Writes BGRA frames into the shared section the native media source reads from.
/// </summary>
/// <remarks>
/// The media source lives inside the FrameServer service, so this is the only
/// channel between the UI and the camera. The section is created lazily on first
/// publish and re-created automatically if the consumer side disappears.
/// </remarks>
public sealed class VirtualCameraFrameChannel : IDisposable
{
    private readonly IAppLogger _logger;
    private readonly object _gate = new();

    private SafeFileHandle? _mapping;
    private nint _view;
    private nint _dataEvent;
    private uint _frameIndex;
    private bool _disposed;
    private bool _loggedGlobalFallback;

    public VirtualCameraFrameChannel(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True once the shared section has been created successfully.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _view != nint.Zero;
            }
        }
    }

    /// <summary>Number of frames successfully published since construction.</summary>
    public long PublishedFrames { get; private set; }

    /// <summary>
    /// Publishes one BGRA frame. <paramref name="pixels"/> must hold at least
    /// <c>stride * height</c> bytes.
    /// </summary>
    /// <returns>True when the frame was written.</returns>
    public bool Publish(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        }

        if (width > FrameChannelContract.MaxWidth || height > FrameChannelContract.MaxHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Frame exceeds the {FrameChannelContract.MaxWidth}x{FrameChannelContract.MaxHeight} channel limit.");
        }

        var required = (long)stride * height;
        if (pixels.Length < required)
        {
            throw new ArgumentException(
                $"Pixel buffer holds {pixels.Length} bytes but {required} are required for {width}x{height} stride {stride}.",
                nameof(pixels));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!EnsureOpen())
            {
                return false;
            }

            // Copy row by row: the caller's stride may differ from the section's,
            // and a straight memcpy of the whole buffer would shear the image.
            unsafe
            {
                var destination = (byte*)_view + FrameChannelContract.PayloadOffset;
                fixed (byte* source = pixels)
                {
                    for (var y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            source + ((long)y * stride),
                            destination + ((long)y * stride),
                            stride,
                            stride);
                    }
                }
            }

            var header = new FrameHeader
            {
                Magic = FrameChannelContract.Magic,
                Version = FrameChannelContract.Version,
                Width = (uint)width,
                Height = (uint)height,
                Stride = (uint)stride,
                Format = FrameChannelContract.FormatBgra32,
                FrameIndex = ++_frameIndex,
                SourceState = FrameChannelContract.StateLive,
                TimestampMs = (ulong)Environment.TickCount64,
                PayloadBytes = (uint)required,
            };

            Marshal.StructureToPtr(header, _view, fDeleteOld: false);

            // Signal last so the consumer never reads a half-written header.
            if (_dataEvent != nint.Zero)
            {
                NativeMethods.SetEvent(_dataEvent);
            }

            PublishedFrames++;
            return true;
        }
    }

    /// <summary>
    /// Marks the channel as idle so the media source falls back to its built-in
    /// pattern instead of showing the last frame forever.
    /// </summary>
    public void MarkIdle()
    {
        lock (_gate)
        {
            if (_view == nint.Zero)
            {
                return;
            }

            var header = Marshal.PtrToStructure<FrameHeader>(_view);
            header.SourceState = FrameChannelContract.StateIdle;
            Marshal.StructureToPtr(header, _view, fDeleteOld: false);
        }
    }

    private bool EnsureOpen()
    {
        if (_view != nint.Zero)
        {
            return true;
        }

        // Try the machine-wide namespace first, then fall back to the per-session
        // one. The native consumer does the same, so both sides agree even when
        // SeCreateGlobalPrivilege is unavailable to one of them.
        if (TryOpen(FrameChannelContract.SectionName, FrameChannelContract.DataEventName, isGlobal: true))
        {
            return true;
        }

        if (!_loggedGlobalFallback)
        {
            _loggedGlobalFallback = true;
            _logger.Info(
                "Global\\ frame channel unavailable; using the per-session channel. " +
                "This is expected for a non-elevated process and still works when the " +
                "camera is used from the same session.");
        }

        return TryOpen(FrameChannelContract.LocalSectionName, FrameChannelContract.LocalDataEventName, isGlobal: false);
    }

    private bool TryOpen(string sectionName, string eventName, bool isGlobal)
    {
        // CreateFileMappingW with INVALID_HANDLE_VALUE creates a page-file backed
        // section. A null security descriptor is fine here: the default DACL of the
        // creating user is used, and the consumer runs as a service that already has
        // the privileges needed. If a stricter DACL blocks the service, the global
        // attempt fails and the local one is used instead.
        var mapping = NativeMethods.CreateFileMappingW(
            new nint(-1),
            nint.Zero,
            NativeMethods.PAGE_READWRITE_MAP,
            0,
            FrameChannelContract.SectionBytes,
            sectionName);

        if (mapping == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.Debug($"CreateFileMappingW({sectionName}) failed with Win32 error {error}.");
            return false;
        }

        var view = NativeMethods.MapViewOfFile(
            mapping,
            NativeMethods.FILE_MAP_ALL_ACCESS,
            0,
            0,
            (nuint)FrameChannelContract.SectionBytes);

        if (view == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.Warn($"MapViewOfFile({sectionName}) failed with Win32 error {error}.");
            NativeMethods.CloseHandle(mapping);
            return false;
        }

        _mapping = new SafeFileHandle(mapping, ownsHandle: true);
        _view = view;

        // The event is best effort: the media source polls the header anyway, so a
        // missing event only costs a little latency.
        var handle = NativeMethods.CreateEventW(nint.Zero, false, false, eventName);
        if (handle == nint.Zero)
        {
            handle = NativeMethods.OpenEventW(
                NativeMethods.EVENT_MODIFY_STATE | NativeMethods.SYNCHRONIZE, false, eventName);
        }

        _dataEvent = handle;

        // Publish a valid but idle header so a consumer that attaches first sees a
        // well-formed section instead of zeroed memory.
        var header = new FrameHeader
        {
            Magic = FrameChannelContract.Magic,
            Version = FrameChannelContract.Version,
            SourceState = FrameChannelContract.StateIdle,
            Format = FrameChannelContract.FormatBgra32,
        };
        Marshal.StructureToPtr(header, _view, fDeleteOld: false);

        _logger.Info($"Virtual camera frame channel opened ({sectionName}, global={isGlobal}).");
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_view != nint.Zero)
            {
                // Leave the channel in the idle state so the camera shows its test
                // pattern rather than freezing on the last pushed frame.
                try
                {
                    var header = Marshal.PtrToStructure<FrameHeader>(_view);
                    header.SourceState = FrameChannelContract.StateIdle;
                    Marshal.StructureToPtr(header, _view, fDeleteOld: false);
                }
                catch (AccessViolationException)
                {
                    // The consumer may have torn the section down already.
                }

                NativeMethods.UnmapViewOfFile(_view);
                _view = nint.Zero;
            }

            _mapping?.Dispose();
            _mapping = null;

            if (_dataEvent != nint.Zero)
            {
                NativeMethods.CloseHandle(_dataEvent);
                _dataEvent = nint.Zero;
            }
        }
    }
}
