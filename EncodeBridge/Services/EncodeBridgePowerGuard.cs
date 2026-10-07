using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EncodeBridge.Services;

/// <summary>
/// Owns EncodeBridge's process-lifetime SystemRequired request while at least one
/// accepted queue item is non-terminal. QueueItemId is the single ownership key.
/// </summary>
internal sealed class EncodeBridgePowerGuard : IDisposable
{
    private const uint ReasonContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x00000001;
    private readonly object _gate = new();
    private readonly HashSet<string> _owners = new(StringComparer.Ordinal);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    private readonly Action<string>? _diagnostic;
#endif
    private SafePowerRequestHandle? _requestHandle;
    private bool _systemRequiredSet;
    private bool _disposed;

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    public EncodeBridgePowerGuard(Action<string>? diagnostic = null)
        => _diagnostic = diagnostic;
#else
    public EncodeBridgePowerGuard() { }
#endif

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    public void Acquire(string queueItemId, string source)
#else
    public void Acquire(string queueItemId)
#endif
    {
        if (string.IsNullOrWhiteSpace(queueItemId))
            throw new ArgumentException("QueueItemId is required.", nameof(queueItemId));

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_owners.Contains(queueItemId))
                return;

            EnsureSystemRequiredLocked();
            _owners.Add(queueItemId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POWER_GUARD state=ACQUIRED queueItemId={queueItemId} source={source} owners={_owners.Count} requestType=SystemRequired boundary=queue_accept_to_terminal");
#endif
        }
    }

    public void Restore(IEnumerable<string> queueItemIds)
    {
        var ids = queueItemIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;

        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureSystemRequiredLocked();
            foreach (var id in ids) _owners.Add(id);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POWER_GUARD state=RESTORED owners={_owners.Count} source=startup_queue requestType=SystemRequired boundary=queue_accept_to_terminal");
#endif
        }
    }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    public void Release(string queueItemId, string terminalReason)
#else
    public void Release(string queueItemId)
#endif
    {
        if (string.IsNullOrWhiteSpace(queueItemId)) return;
        lock (_gate)
        {
            if (_disposed || !_owners.Remove(queueItemId)) return;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POWER_GUARD state=RELEASED queueItemId={queueItemId} reason={terminalReason} owners={_owners.Count} requestType=SystemRequired boundary=terminal");
#endif
            if (_owners.Count == 0)
                ClearSystemRequiredLocked();
        }
    }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    public bool IsHeld(string queueItemId)
    {
        lock (_gate) return !_disposed && _owners.Contains(queueItemId);
    }
#endif

    private void EnsureSystemRequiredLocked()
    {
        if (_systemRequiredSet) return;

        var reasonPtr = Marshal.StringToHGlobalUni("EncodeBridge external encode queue");
        try
        {
            var context = new ReasonContext
            {
                Version = ReasonContextVersion,
                Flags = PowerRequestContextSimpleString,
                Reason = new ReasonContextUnion { SimpleReasonString = reasonPtr }
            };
            var handle = PowerCreateRequest(ref context);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "PowerCreateRequest failed.");
            }

            if (!PowerSetRequest(handle.DangerousGetHandle(), PowerRequestType.SystemRequired))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "PowerSetRequest(SystemRequired) failed.");
            }

            _requestHandle = handle;
            _systemRequiredSet = true;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke("ENCODEBRIDGE_POWER_GUARD state=SYSTEM_REQUIRED_ON owners=0 requestType=SystemRequired boundary=queue_accept_to_terminal");
#endif
        }
        finally
        {
            Marshal.FreeHGlobal(reasonPtr);
        }
    }

    private void ClearSystemRequiredLocked()
    {
        if (!_systemRequiredSet) return;
        var handle = _requestHandle;
        _requestHandle = null;
        _systemRequiredSet = false;
        if (handle is not null && !handle.IsInvalid)
        {
            if (!PowerClearRequest(handle.DangerousGetHandle(), PowerRequestType.SystemRequired))
            {
                var error = Marshal.GetLastWin32Error();
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_POWER_GUARD state=CLEAR_FAILED error={error} requestType=SystemRequired");
#endif
            }
            handle.Dispose();
        }
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _diagnostic?.Invoke("ENCODEBRIDGE_POWER_GUARD state=SYSTEM_REQUIRED_OFF owners=0 requestType=SystemRequired boundary=no_nonterminal_queue");
#endif
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EncodeBridgePowerGuard));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _owners.Clear();
            ClearSystemRequiredLocked();
        }
    }

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DetailedReasonContext
    {
        public IntPtr LocalizedReasonModule;
        public uint LocalizedReasonId;
        public uint ReasonStringCount;
        public IntPtr ReasonStrings;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ReasonContextUnion
    {
        [FieldOffset(0)] public DetailedReasonContext Detailed;
        [FieldOffset(0)] public IntPtr SimpleReasonString;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public ReasonContextUnion Reason;
    }

    private sealed class SafePowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePowerRequestHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafePowerRequestHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
