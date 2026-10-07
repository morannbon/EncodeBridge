using System.Text.Json;
using EncodeBridge.Adapters;
using EncodeBridge.Domain;
using EncodeBridge.Services;
using EncodeBridge.Storage;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Events;
using TvAIrPlugin.Pickers;

namespace EncodeBridge.Runtime;

internal sealed class EncodeBridgeRuntime : IDisposable
{
    private readonly ITvAirPluginRuntimeContext _ctx; private readonly EncodeBridgeStorage _storage; private readonly RuleResolver _rules=new(); private readonly EncodeScheduler _scheduler; private readonly EncodeBridgePowerGuard _powerGuard; private readonly List<IDisposable> _subs=new(); private readonly object _gate=new();
    private sealed class UiWindowRefreshState
    {
        public string LogicalContentRoute { get; set; } = "/plugin/encodebridge";
        public long NextRefreshTick { get; set; }
    }

    private const int UiRefreshIdleDelayMs = 60_000;

    private static long NextUiRefreshTick() => Environment.TickCount64 + UiRefreshIdleDelayMs;
    private readonly object _uiRefreshGate=new(); private readonly Dictionary<string,UiWindowRefreshState> _uiWindows=new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim _uiRefreshStop=new(false); private readonly AutoResetEvent _uiRefreshScheduleChanged=new(false); private Thread? _uiRefreshThread; private int _uiRefreshInProgress;
    private long _uiRefreshTickSequence;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    private int _uiRefreshLoopGeneration;
#endif
    public EncodeBridgeStore Store {get;}
    public bool StorageHealthy {get;}
    public string StorageError {get;}
    public EncodeBridgeRuntime(ITvAirPluginRuntimeContext ctx)
    {
        _ctx=ctx;_storage=new EncodeBridgeStorage(ctx.Storage); try{Store=_storage.Load();StorageHealthy=true;StorageError=string.Empty;}catch(Exception ex){Store=new EncodeBridgeStore();Store.Settings.AutoEncodeEnabled=false;StorageHealthy=false;StorageError=ex.Message;}
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _powerGuard=new EncodeBridgePowerGuard(WriteDeveloperDiagnostic);
        _scheduler=new EncodeScheduler(ctx,_storage,Store,new AdapterRegistry(),_powerGuard,WriteDeveloperDiagnostic);
#else
        _powerGuard=new EncodeBridgePowerGuard();
        _scheduler=new EncodeScheduler(ctx,_storage,Store,new AdapterRegistry(),_powerGuard);
#endif
    }
    public void Start()
    {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        WriteDeveloperDiagnostic($"ENCODEBRIDGE_STATE_LOAD storageHealthy={StorageHealthy} tools={Store.Tools.Count} profiles={Store.Profiles.Count} rules={Store.Rules.Count} queue={Store.Queue.Count} history={Store.History.Count} auto={Store.Settings.AutoEncodeEnabled}");
#endif
        if (StorageHealthy && Store.Queue.Count > 0)
            _powerGuard.Restore(Store.Queue.Select(x => x.QueueItemId));
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.RecordingResultFinalized), OnRecording));
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.ExternalJobChanged), _ => EvaluateIfStorageHealthy()));
        // RecordingResultFinalized is the only recording-registration boundary.
        // Completion/failure events are scheduler wake-ups only so an already waiting item can resume.
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.RecordingCompleted), _ => EvaluateIfStorageHealthy()));
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.RecordingFailed), _ => EvaluateIfStorageHealthy()));
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.EpgCompleted), _ => EvaluateIfStorageHealthy()));
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.RuntimeWindowLifecycleChanged), OnRuntimeWindowLifecycleChanged));
        EvaluateIfStorageHealthy();
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        Interlocked.Increment(ref _uiRefreshLoopGeneration);
#endif
        _uiRefreshThread = new Thread(UiRefreshLoop)
        {
            IsBackground = true,
            Name = "EncodeBridge.UiRefresh"
        };
        _uiRefreshThread.Start();
    }
    private void OnRecording(PluginEventEnvelope e)
    {
        lock(_gate)
        {
        if(!StorageHealthy||!Store.Settings.AutoEncodeEnabled)return; var dto=ConvertPayload<TvAirEventDto>(e.Payload)?.RecordingResult??ConvertPayload<TvAirRecordingResultDto>(e.Payload); if(dto is null||!dto.ResultFinalized||!dto.Succeeded||dto.FileCreated!=true||string.IsNullOrWhiteSpace(dto.FilePath))return; if(Store.ProcessedRecordingIds.Contains(dto.RecordingId))return;
        var rec=new RecordingSnapshot{RecordingId=dto.RecordingId,ReservationId=dto.ReservationId,SourceFilePath=dto.FilePath!,NetworkId=dto.NetworkId,TransportStreamId=dto.TransportStreamId,ServiceId=dto.ServiceId,EventId=dto.EventId,Title=dto.EventTitle,ServiceName=dto.ServiceName,Genre=dto.Genre,GenreCodes=dto.GenreCodes,ScheduledStart=dto.ScheduledStartTime,ActualStart=dto.ActualStartTime,ActualEnd=dto.ActualEndTime};
        var profile=_rules.Resolve(Store,rec); if(profile is null)return; var tool=Store.Tools.FirstOrDefault(x=>x.ToolId==profile.ToolId); if(tool is null)return;
        var snap=new EncodeProfileSnapshot{ProfileId=profile.ProfileId,ProfileName=profile.Name,ToolId=tool.ToolId,ToolKind=tool.Kind,ToolName=tool.DisplayName,ExecutablePath=tool.ExecutablePath,VideoCodec=profile.VideoCodec,Quality=profile.Quality,AudioTracks=profile.AudioTracks,AudioCodec=profile.AudioCodec,Subtitles=profile.Subtitles,Container=profile.Container,OutputDirectoryMode=profile.OutputDirectoryMode,OutputDirectory=profile.OutputDirectory,SourceSubdirectoryName=profile.SourceSubdirectoryName,AdapterSettings=new(profile.AdapterSettings,StringComparer.OrdinalIgnoreCase),AdditionalArguments=new(profile.AdditionalArguments)};
        foreach(var kv in tool.Settings)snap.AdapterSettings.TryAdd(kv.Key,kv.Value);
        var queueItem=new EncodeQueueItem{Recording=rec,Profile=snap};
        // Power ownership must overlap TvAIr's RecordingResultFinalized delivery.
        // Acquire before this callback can return and before the queue item is accepted.
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _powerGuard.Acquire(queueItem.QueueItemId,"RecordingResultFinalized");
#else
        _powerGuard.Acquire(queueItem.QueueItemId);
#endif
        Store.Queue.Add(queueItem);
        Store.ProcessedRecordingIds.Add(dto.RecordingId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        WriteDeveloperDiagnostic($"ENCODEBRIDGE_QUEUE_ACCEPTED queueItemId={queueItem.QueueItemId} recordingId={dto.RecordingId} powerHeld={_powerGuard.IsHeld(queueItem.QueueItemId)} boundary=recording_result_finalized_callback_before_return");
#endif
        _storage.Save(Store);
        _scheduler.Evaluate();
        }
    }
    private static T? ConvertPayload<T>(object? value){try{if(value is T t)return t;if(value is JsonElement e)return e.Deserialize<T>();return value is null?default:JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));}catch{return default;}}
    public void Save()
    {
        lock(_gate)
        {
            if (!StorageHealthy)
                throw new InvalidOperationException("保存データの読み込みに失敗しているため、現在の状態を上書き保存できません。TvAIrを終了して保存データを確認してください。");
            _storage.Save(Store);
            _scheduler.Evaluate();
        }
    }

    private void EvaluateIfStorageHealthy()
    {
        lock(_gate)
        {
            if (!StorageHealthy) return;
            _scheduler.Evaluate();
        }
    }
    public Task<PluginPathPickerResult> PickExecutableAsync(string ownerWindowId, CancellationToken cancellationToken)
        => _ctx.PathPicker.PickFileAsync(new PluginFilePickerRequest
        {
            Title = "変換ツールの実行ファイルを選択",
            OwnerWindowId = ownerWindowId,
            Filters = new[]
            {
                new PluginFileFilter { Label = "実行ファイル", Patterns = new[] { "*.exe" } },
                new PluginFileFilter { Label = "すべてのファイル", Patterns = new[] { "*.*" } }
            }
        }, cancellationToken);

    public Task<PluginPathPickerResult> PickOutputFolderAsync(string ownerWindowId, CancellationToken cancellationToken)
        => _ctx.PathPicker.PickFolderAsync(new PluginFolderPickerRequest
        {
            Title = "変換後の保存先を選択",
            OwnerWindowId = ownerWindowId
        }, cancellationToken);

    public Task<PluginPathPickerResult> PickTmpgEncEncodeTemplateAsync(string ownerWindowId, CancellationToken cancellationToken)
        => _ctx.PathPicker.PickFileAsync(new PluginFilePickerRequest
        {
            Title = "TMPGEncの出力テンプレートを選択",
            OwnerWindowId = ownerWindowId,
            Filters = new[]
            {
                new PluginFileFilter { Label = "TMPGEnc 出力テンプレート", Patterns = new[] { "*.*" } }
            }
        }, cancellationToken);

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    public void WriteDeveloperDiagnostic(string message)
    {
        // Developer diagnostics must never be able to break RenderHtml or an action path.
        try
        {
            _ctx.Logs.Write(new TvAirLogWriteDto { Level = "Info", Category = "EncodeBridge", Message = message });
        }
        catch
        {
            // Diagnostic output is best-effort only. Product behavior remains authoritative.
        }
    }
#endif

    public void RegisterUiWindow(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        // Render itself is interaction evidence. Always defer the next scheduled refresh
        // so a Host/action-driven redraw never gets followed by an immediate periodic redraw.
        lock (_uiRefreshGate)
        {
            _uiWindows[windowId] = new UiWindowRefreshState
            {
                LogicalContentRoute = "/plugin/encodebridge",
                NextRefreshTick = NextUiRefreshTick()
            };
        }
        _uiRefreshScheduleChanged.Set();
    }

    public void MarkUiInteraction(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        lock (_uiRefreshGate)
        {
            if (_uiWindows.TryGetValue(windowId, out var state))
                state.NextRefreshTick = NextUiRefreshTick();
        }
        _uiRefreshScheduleChanged.Set();
    }

    private void OnRuntimeWindowLifecycleChanged(PluginEventEnvelope e)
    {
        var dto = ConvertPayload<TvAirEventDto>(e.Payload)?.RuntimeWindowLifecycle
                  ?? ConvertPayload<TvAirRuntimeWindowLifecycleDto>(e.Payload);
        if (dto is null || !string.Equals(dto.PluginId, "encodebridge", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(dto.WindowInstanceId))
            return;

        lock (_uiRefreshGate)
        {
            if (dto.State == TvAIrPlugin.Windows.PluginWindowLifecycleState.Shown)
            {
                _uiWindows[dto.WindowInstanceId] = new UiWindowRefreshState
                {
                    LogicalContentRoute = "/plugin/encodebridge",
                    NextRefreshTick = NextUiRefreshTick()
                };
            }
            else if (dto.State is TvAIrPlugin.Windows.PluginWindowLifecycleState.Hidden or TvAIrPlugin.Windows.PluginWindowLifecycleState.Closing or TvAIrPlugin.Windows.PluginWindowLifecycleState.Closed)
                _uiWindows.Remove(dto.WindowInstanceId);
        }
        _uiRefreshScheduleChanged.Set();
    }


    private void UiRefreshLoop()
    {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        var generation = Volatile.Read(ref _uiRefreshLoopGeneration);
        var threadId = Environment.CurrentManagedThreadId;
        var endReason = "unknown";
        WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH_LOOP state=START threadId={threadId} generation={generation} cancellationRequested={_uiRefreshStop.IsSet} windows={SnapshotUiWindowIdsForDiagnostic()}");
#endif
        var waitHandles = new WaitHandle[] { _uiRefreshStop.WaitHandle, _uiRefreshScheduleChanged };
        try
        {
            while (true)
            {
                int? waitDurationMs = null;
                lock (_uiRefreshGate)
                {
                    if (_uiWindows.Count > 0)
                    {
                        var nowTick = Environment.TickCount64;
                        var nextTick = _uiWindows.Values.Min(x => x.NextRefreshTick);
                        var remainingMs = nextTick - nowTick;
                        // Positive remaining time must never collapse to a zero-duration wait.
                        // This is the canonical guard against deadline-boundary busy loops.
                        waitDurationMs = remainingMs <= 0
                            ? 0
                            : (int)Math.Max(1, Math.Min(int.MaxValue, remainingMs));
                    }
                }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH_WAIT state=BEGIN mode={(waitDurationMs.HasValue ? "idle_deadline" : "hidden_pause")} delayMs={(waitDurationMs.HasValue ? waitDurationMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "infinite")} threadId={threadId} generation={generation} cancellationRequested={_uiRefreshStop.IsSet} windows={SnapshotUiWindowIdsForDiagnostic()}");
#endif
                var waitResult = waitDurationMs.HasValue
                    ? WaitHandle.WaitAny(waitHandles, waitDurationMs.Value)
                    : WaitHandle.WaitAny(waitHandles);

                if (waitResult == 0)
                {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    endReason = "cancel";
#endif
                    break;
                }
                if (waitResult == 1)
                    continue;

                var tickSequence = Interlocked.Increment(ref _uiRefreshTickSequence);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                RefreshDueUiWindows(tickSequence, generation);
#else
                RefreshDueUiWindows(tickSequence);
#endif
            }
        }
        catch (Exception ex)
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            endReason = "exception";
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH_LOOP state=EXCEPTION threadId={threadId} generation={generation} cancellationRequested={_uiRefreshStop.IsSet} exception={ex.GetType().FullName} message={SanitizeDiagnosticValue(ex.Message)}");
#else
            _ = ex;
#endif
        }
        finally
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH_LOOP state=END threadId={threadId} generation={generation} cancellationRequested={_uiRefreshStop.IsSet} reason={endReason} windows={SnapshotUiWindowIdsForDiagnostic()}");
#endif
        }
    }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    private void RefreshDueUiWindows(long tickSequence, int generation)
#else
    private void RefreshDueUiWindows(long tickSequence)
#endif
    {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        var threadId = Environment.CurrentManagedThreadId;
        var inProgressBefore = Volatile.Read(ref _uiRefreshInProgress);
#endif
        if (Interlocked.Exchange(ref _uiRefreshInProgress, 1) != 0)
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH state=SKIPPED reason=refresh_in_progress refreshInProgress={inProgressBefore} cancellationRequested={_uiRefreshStop.IsSet} threadId={threadId} generation={generation} tickSequence={tickSequence} windows={SnapshotUiWindowIdsForDiagnostic()}");
#endif
            return;
        }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        var refreshStarted = DateTimeOffset.UtcNow;
#endif
        try
        {
            var nowTick = Environment.TickCount64;
            KeyValuePair<string,UiWindowRefreshState>[] windows;
            lock (_uiRefreshGate)
            {
                windows = _uiWindows
                    .Where(x => x.Value.NextRefreshTick <= nowTick)
                    .ToArray();
                // Advance the authoritative deadline before any Host call. No elapsed deadline
                // is replayed or caught up later, even after sleep/hidden/resume transitions.
                foreach (var window in windows)
                    window.Value.NextRefreshTick = nowTick + UiRefreshIdleDelayMs;
            }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH state=BEGIN refreshInProgress={inProgressBefore} cancellationRequested={_uiRefreshStop.IsSet} threadId={threadId} generation={generation} tickSequence={tickSequence} windows={FormatWindowIdsForDiagnostic(windows.Select(x => x.Key))} idleThresholdSeconds=60");
#endif
            if (windows.Length == 0)
                return;

            foreach (var window in windows)
            {
                var id = window.Key;
                var logicalContentRoute = window.Value.LogicalContentRoute;
                var contentRoute = logicalContentRoute + "?refreshRevision=" + tickSequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                var windowRefreshStarted = DateTimeOffset.UtcNow;
#endif
                try
                {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH_WINDOW state=BEGIN window={id} logicalContentRoute={SanitizeDiagnosticValue(logicalContentRoute)} contentRoute={SanitizeDiagnosticValue(contentRoute)} cancellationRequested={_uiRefreshStop.IsSet} threadId={threadId} generation={generation} tickSequence={tickSequence}");
#endif
                    var result = _ctx.Windows.RefreshToolWindow(new TvAirToolWindowRefreshRequestDto { WindowId = id, ContentRoute = contentRoute });
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    var windowRefreshEnded = DateTimeOffset.UtcNow;
                    WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH_WINDOW state=END window={id} logicalContentRoute={SanitizeDiagnosticValue(logicalContentRoute)} contentRoute={SanitizeDiagnosticValue(contentRoute)} result={(result.Succeeded ? "OK" : "FAILED")} elapsedMs={(windowRefreshEnded - windowRefreshStarted).TotalMilliseconds:0} cancellationRequested={_uiRefreshStop.IsSet} threadId={threadId} generation={generation} tickSequence={tickSequence} error={result.Error?.Code.ToString() ?? "-"}");
#endif
                    if (!result.Succeeded && result.Error?.Code == TvAirErrorCode.EntityNotFound)
                    {
                        lock (_uiRefreshGate) _uiWindows.Remove(id);
                        _uiRefreshScheduleChanged.Set();
                    }
                }
                catch (Exception ex)
                {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    var windowRefreshEnded = DateTimeOffset.UtcNow;
                    WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH_WINDOW state=END window={id} logicalContentRoute={SanitizeDiagnosticValue(logicalContentRoute)} contentRoute={SanitizeDiagnosticValue(contentRoute)} result=FAILED elapsedMs={(windowRefreshEnded - windowRefreshStarted).TotalMilliseconds:0} cancellationRequested={_uiRefreshStop.IsSet} threadId={threadId} generation={generation} tickSequence={tickSequence} error={ex.GetType().Name}");
#else
                    _ = ex;
#endif
                }
            }
        }
        finally
        {
            Volatile.Write(ref _uiRefreshInProgress, 0);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            var refreshEnded = DateTimeOffset.UtcNow;
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_UI_REFRESH state=END elapsedMs={(refreshEnded - refreshStarted).TotalMilliseconds:0} refreshInProgress=0 cancellationRequested={_uiRefreshStop.IsSet} threadId={threadId} generation={generation} tickSequence={tickSequence} windows={SnapshotUiWindowIdsForDiagnostic()}");
#endif
        }
    }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    private string SnapshotUiWindowIdsForDiagnostic()
    {
        lock (_uiRefreshGate) return FormatWindowIdsForDiagnostic(_uiWindows.Keys.ToArray());
    }

    private static string FormatWindowIdsForDiagnostic(IEnumerable<string> ids)
    {
        var values = ids.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return values.Length == 0 ? "-" : string.Join(",", values);
    }

    private static string SanitizeDiagnosticValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
#endif

    public bool Stop(string id){lock(_gate)return _scheduler.Stop(id);} public bool Remove(string id){lock(_gate)return _scheduler.RemoveWaiting(id);} public bool Retry(string id){lock(_gate)return _scheduler.Retry(id);}
    public void Dispose()
    {
        _uiRefreshStop.Set();
        _uiRefreshScheduleChanged.Set();
        var thread = _uiRefreshThread;
        _uiRefreshThread = null;
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(2));
        if (thread is null || !thread.IsAlive)
        {
            _uiRefreshStop.Dispose();
            _uiRefreshScheduleChanged.Dispose();
        }
        foreach(var s in _subs)s.Dispose();
        _subs.Clear();
        _powerGuard.Dispose();
    }
}
