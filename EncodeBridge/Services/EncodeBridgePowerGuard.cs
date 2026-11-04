using TvAIrPlugin;

namespace EncodeBridge.Services;

/// <summary>
/// Holds Host-owned power inhibition leases across accepted file admission and
/// running external jobs. Deferred work releases its lease until dispatch.
/// Windows power requests and shutdown cleanup belong exclusively to TvAIr Host.
/// </summary>
internal sealed class EncodeBridgePowerGuard : IDisposable
{
    private readonly ITvAirPowerInhibitionApi _api;
    private readonly object _gate = new();
    private readonly Dictionary<string, IDisposable> _leases = new(StringComparer.Ordinal);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    private readonly Action<string>? _diagnostic;
    public EncodeBridgePowerGuard(ITvAirPowerInhibitionApi api, Action<string>? diagnostic = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _diagnostic = diagnostic;
    }
#else
    public EncodeBridgePowerGuard(ITvAirPowerInhibitionApi api)
        => _api = api ?? throw new ArgumentNullException(nameof(api));
#endif
    private bool _disposed;

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
            if (_leases.ContainsKey(queueItemId)) return;
            // Do not pass queue IDs, paths, program names or user data to the Host operation label.
            // Acquire must succeed before the new queue item is accepted.
            var lease = _api.Acquire("EncodeWorkflow");
            _leases.Add(queueItemId, lease ?? throw new InvalidOperationException("Host returned a null power lease."));
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POWER_GUARD state=ACQUIRED queueItemId={queueItemId} source={source} owners={_leases.Count} owner=Host boundary=queue_accept_to_terminal");
#endif
        }
    }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    public void AcquireAdmission(string reservationId, string source)
#else
    public void AcquireAdmission(string reservationId)
#endif
    {
        if (string.IsNullOrWhiteSpace(reservationId)) throw new ArgumentException("Reservation identity is required.", nameof(reservationId));
        lock (_gate)
        {
            ThrowIfDisposed();
            var key = "admission:" + reservationId;
            if (_leases.ContainsKey(key)) return;
            var lease = _api.Acquire("EncodeFileAdmission");
            _leases.Add(key, lease ?? throw new InvalidOperationException("Host returned a null admission power lease."));
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POWER_GUARD state=ADMISSION_ACQUIRED source={source} owners={_leases.Count} boundary=finalized_event_to_journal_decision");
#endif
        }
    }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    public void ReleaseAdmission(string reservationId, string reason)
#else
    public void ReleaseAdmission(string reservationId)
#endif
    {
        if (string.IsNullOrWhiteSpace(reservationId)) return;
        lock (_gate)
        {
            var key = "admission:" + reservationId;
            if (_disposed || !_leases.TryGetValue(key, out var lease)) return;
            lease.Dispose();
            _leases.Remove(key);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POWER_GUARD state=ADMISSION_RELEASED reason={reason} owners={_leases.Count} boundary=journal_decision");
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
            if (_disposed || !_leases.TryGetValue(queueItemId, out var lease)) return;
            // Dispose before relinquishing ownership so a failed Host release is not hidden.
            lease.Dispose();
            _leases.Remove(queueItemId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POWER_GUARD state=RELEASED queueItemId={queueItemId} reason={terminalReason} owners={_leases.Count} owner=Host boundary=terminal");
#endif
        }
    }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    public bool IsHeld(string queueItemId)
    {
        lock (_gate) return !_disposed && _leases.ContainsKey(queueItemId);
    }
#endif
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
            foreach (var lease in _leases.Values) lease.Dispose();
            _leases.Clear();
        }
    }
}
