using TvAIrPlugin;

namespace EncodeBridge.Services;

/// <summary>
/// Holds one Host-owned power inhibition lease per nonterminal EncodeBridge queue item.
/// Windows power requests and cleanup on plugin shutdown belong exclusively to TvAIr Host.
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

    public void Restore(IEnumerable<string> queueItemIds)
    {
        // Acquire all outstanding leases before scheduler resumes. On failure release those
        // acquired during this restore and surface the failure instead of pretending protection.
        var ids = queueItemIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        lock (_gate)
        {
            ThrowIfDisposed();
            var added = new List<string>();
            try
            {
                foreach (var id in ids)
                {
                    if (_leases.ContainsKey(id)) continue;
                    var lease = _api.Acquire("EncodeWorkflow");
                    _leases.Add(id, lease ?? throw new InvalidOperationException("Host returned a null power lease."));
                    added.Add(id);
                }
            }
            catch
            {
                foreach (var id in added)
                {
                    var lease = _leases[id];
                    _leases.Remove(id);
                    lease.Dispose();
                }
                throw;
            }
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            if (added.Count > 0)
                _diagnostic?.Invoke($"ENCODEBRIDGE_POWER_GUARD state=RESTORED owners={_leases.Count} source=startup_queue owner=Host boundary=queue_accept_to_terminal");
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
