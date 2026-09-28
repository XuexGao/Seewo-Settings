using System.Runtime.InteropServices;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;

namespace SeewoAssistant.Core.Services.CaptureGuard;

/// <summary>
/// Mirror of the <c>seewo::GuardRequest</c> contract in
/// <c>native/SeewoCommon/SeewoIpc.h</c>. Both sides must change together; bump the
/// version in both places when the layout changes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct GuardRequestNative
{
    public uint Magic;
    public uint Version;
    public uint Command;
    public uint Affinity;
    public ulong TargetHwnd;
    public ulong RequesterPid;
    public uint RequestId;
    public int Result;
    public uint LastError;
    public uint Completed;
    public uint PayloadPid;
}

/// <summary>Commands understood by the injected payload.</summary>
internal enum GuardCommandNative : uint
{
    None = 0,
    SetAffinity = 1,
    Ping = 2,
    Unload = 3,
}

/// <summary>
/// The request channel between this process and an injected payload. The injector
/// creates the section, writes a request, signals the event, and polls
/// <c>Completed</c> for the response.
/// </summary>
internal sealed class GuardChannel : IDisposable
{
    // Must match native/SeewoCommon/SeewoIpc.h byte for byte, including the
    // Global\ / Local\ prefix. See SharedChannelContractTests.
    internal const string SectionName = @"Global\SeewoAssistant.CaptureGuard.v1";
    internal const string LocalSectionName = @"Local\SeewoAssistant.CaptureGuard.v1";
    internal const string RequestEventName = @"Global\SeewoAssistant.CaptureGuard.Request.v1";
    internal const string LocalRequestEventName = @"Local\SeewoAssistant.CaptureGuard.Request.v1";

    private const uint Magic = 0x53434731;  // 'SCG1'
    private const uint Version = 1;
    private const int SectionBytes = 4096;

    /// <summary>How long to wait for the payload to answer one request.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IAppLogger _logger;
    private readonly object _gate = new();

    private nint _mapping;
    private nint _view;
    private nint _requestEvent;
    private uint _nextRequestId;
    private bool _disposed;

    internal GuardChannel(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    internal bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _view != nint.Zero;
            }
        }
    }

    /// <summary>Opens the channel, creating the objects if this is the first use.</summary>
    internal bool Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_view != nint.Zero)
            {
                return true;
            }

            return TryOpen(SectionName, RequestEventName, global: true)
                || TryOpen(LocalSectionName, LocalRequestEventName, global: false);
        }
    }

    private bool TryOpen(string sectionName, string eventName, bool global)
    {
        var mapping = NativeMethods.CreateFileMappingW(
            new nint(-1), nint.Zero, NativeMethods.PAGE_READWRITE_MAP, 0, SectionBytes, sectionName);

        if (mapping == nint.Zero)
        {
            _logger.Debug($"Capture guard: CreateFileMappingW({sectionName}) failed, error {Marshal.GetLastWin32Error()}.");
            return false;
        }

        var view = NativeMethods.MapViewOfFile(
            mapping, NativeMethods.FILE_MAP_ALL_ACCESS, 0, 0, (nuint)SectionBytes);

        if (view == nint.Zero)
        {
            _logger.Warn($"Capture guard: MapViewOfFile({sectionName}) failed, error {Marshal.GetLastWin32Error()}.");
            NativeMethods.CloseHandle(mapping);
            return false;
        }

        // The payload must be able to open this event by name, so it is created with
        // a null security descriptor, which yields the default DACL of this user.
        // The payload runs as the target process's user, which for a same-user
        // injection is this user, so this is sufficient.
        var requestEvent = NativeMethods.CreateEventW(nint.Zero, false, false, eventName);
        if (requestEvent == nint.Zero)
        {
            _logger.Warn($"Capture guard: CreateEventW({eventName}) failed, error {Marshal.GetLastWin32Error()}.");
            NativeMethods.UnmapViewOfFile(view);
            NativeMethods.CloseHandle(mapping);
            return false;
        }

        _mapping = mapping;
        _view = view;
        _requestEvent = requestEvent;

        var header = new GuardRequestNative
        {
            Magic = Magic,
            Version = Version,
            Command = (uint)GuardCommandNative.None,
            RequesterPid = (ulong)Environment.ProcessId,
        };
        Marshal.StructureToPtr(header, _view, fDeleteOld: false);

        _logger.Info($"Capture guard channel opened ({sectionName}, global={global}).");
        return true;
    }

    /// <summary>
    /// Sends one command and waits for the payload to answer.
    /// </summary>
    /// <returns>The payload's result, or null when it did not answer in time.</returns>
    internal (bool Success, uint LastError, uint PayloadPid)? Send(
        GuardCommandNative command,
        ulong targetHwnd = 0,
        uint affinity = 0)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_view == nint.Zero && !Open())
            {
                return null;
            }

            var requestId = ++_nextRequestId;

            var request = new GuardRequestNative
            {
                Magic = Magic,
                Version = Version,
                Command = (uint)command,
                Affinity = affinity,
                TargetHwnd = targetHwnd,
                RequesterPid = (ulong)Environment.ProcessId,
                RequestId = requestId,
                Result = 0,
                LastError = 0,
                Completed = 0,
                PayloadPid = 0,
            };

            Marshal.StructureToPtr(request, _view, fDeleteOld: false);
            NativeMethods.SetEvent(_requestEvent);

            var deadline = DateTime.UtcNow + RequestTimeout;

            while (DateTime.UtcNow < deadline)
            {
                var current = Marshal.PtrToStructure<GuardRequestNative>(_view);

                // Only accept a response that belongs to this request; a stale
                // completion from a previous call must not be mistaken for ours.
                if (current.RequestId == requestId && current.Completed == 1)
                {
                    return (current.Result != 0, current.LastError, current.PayloadPid);
                }

                Thread.Sleep(20);
            }

            _logger.Warn($"Capture guard: no response to {command} within {RequestTimeout.TotalSeconds:0}s.");
            return null;
        }
    }

    /// <summary>Reads the payload's PID without sending a command, to test liveness.</summary>
    internal uint PeekPayloadPid()
    {
        lock (_gate)
        {
            if (_view == nint.Zero)
            {
                return 0;
            }

            return Marshal.PtrToStructure<GuardRequestNative>(_view).PayloadPid;
        }
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
                NativeMethods.UnmapViewOfFile(_view);
                _view = nint.Zero;
            }

            if (_mapping != nint.Zero)
            {
                NativeMethods.CloseHandle(_mapping);
                _mapping = nint.Zero;
            }

            if (_requestEvent != nint.Zero)
            {
                NativeMethods.CloseHandle(_requestEvent);
                _requestEvent = nint.Zero;
            }
        }
    }
}
