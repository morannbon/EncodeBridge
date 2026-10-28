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

    private readonly ManualResetEventSlim _filePollStop = new(false);
    private readonly AutoResetEvent _filePollWake = new(false);
    private Thread? _filePollThread;
    private bool _fileJournalBlocked;
    // Protected between synchronous finalized callback and durable journal admission.
    // Keyed by the Host reservation identity; never start an encoder from this event.
    private readonly HashSet<string> _admissionLeaseIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completedAdmissionIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposing;
    public string FileJournalError { get; private set; } = string.Empty;
    private const long MinRecordingBytes = 1024L * 1024L;
    private const int FilePollIntervalMs = 15000;
    // Only files finalized after this plugin session started may be admitted.
    private DateTimeOffset _sessionStartedAt;

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
        _powerGuard=new EncodeBridgePowerGuard(ctx.PowerInhibition, WriteDeveloperDiagnostic);
        _scheduler=new EncodeScheduler(ctx,_storage,Store,new AdapterRegistry(),_powerGuard,WriteDeveloperDiagnostic);
#else
        _powerGuard=new EncodeBridgePowerGuard(ctx.PowerInhibition);
        _scheduler=new EncodeScheduler(ctx,_storage,Store,new AdapterRegistry(),_powerGuard);
#endif
    }
    public void Start()
    {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        WriteDeveloperDiagnostic($"ENCODEBRIDGE_STATE_LOAD storageHealthy={StorageHealthy} tools={Store.Tools.Count} profiles={Store.Profiles.Count} rules={Store.Rules.Count} queue={Store.Queue.Count} history={Store.History.Count} auto={Store.Settings.AutoEncodeEnabled}");
#endif
        // Startup is a new execution boundary. Never resume, replay or post-process work
        // left by a previous process. Existing TS/MP4/staging files are left untouched.
        _sessionStartedAt = DateTimeOffset.Now;
        if (StorageHealthy)
        {
            var abandoned = Store.Queue.ToArray();
            foreach (var item in abandoned)
            {
                Store.History.Add(new EncodeHistoryItem
                {
                    QueueItemId = item.QueueItemId,
                    RecordingId = item.Recording.RecordingId,
                    Title = item.Recording.Title,
                    ServiceName = item.Recording.ServiceName,
                    ActualStart = item.Recording.ActualStart,
                    SourceFilePath = item.Recording.SourceFilePath,
                    OutputFilePath = item.ExpectedOutputPath,
                    ProfileName = item.Profile.ProfileName,
                    ToolName = item.Profile.ToolName,
                    Result = EncodeQueueState.Stopped,
                    StartedAt = item.StartedAt,
                    FinishedAt = _sessionStartedAt,
                    AttemptNumber = item.AttemptNumber,
                    FailureMessage = "前回の実行は中断されました。自動再開しません。",
                    Recording = item.Recording,
                    Profile = item.Profile
                });
            }
            Store.Queue.Clear();
            var pendingCount = Store.PendingFinalizedFiles.Count;
            Store.PendingFinalizedFiles.Clear();
            if (abandoned.Length > 0 || pendingCount > 0) _storage.Save(Store);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_STARTUP_RECOVERY result=ABANDONED queue={abandoned.Length} pending={pendingCount} policy=no_auto_resume sessionStart={_sessionStartedAt:O}");
#endif
        }
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.RecordingResultFinalized), OnRecordingResultFinalized));
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.ExternalJobChanged), _ => EvaluateIfStorageHealthy()));
        // RecordingFiles.ListFinalized is the only new-file admission route.
        // Completion/failure events are scheduler wake-ups only for existing queue items.
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.RecordingCompleted), _ => EvaluateIfStorageHealthy()));
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.RecordingFailed), _ => EvaluateIfStorageHealthy()));
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.EpgCompleted), _ => EvaluateIfStorageHealthy()));
        _subs.Add(_ctx.Events.Subscribe(nameof(TvAirEventType.RuntimeWindowLifecycleChanged), OnRuntimeWindowLifecycleChanged));
        PublishProfileSelections();
        EvaluateIfStorageHealthy();
        _filePollThread = new Thread(FinalizedFilePollLoop) { IsBackground = true, Name = "EncodeBridge.FinalizedFiles" };
        _filePollThread.Start();
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
    private void OnRecordingResultFinalized(PluginEventEnvelope envelope)
    {
        // Synchronous callback: the recording-side Host guard is still held here.
        // Only protect files that the Host says were created; the journal remains authoritative.
        var result = (envelope.Payload as TvAirEventDto)?.RecordingResult;
        var reservationId = result?.ReservationId;
        if (!_disposing && StorageHealthy && Store.Settings.AutoEncodeEnabled &&
            result?.FileCreated == true && result.ResultFinalized &&
            !IsExplicitManualRecordingStop(result.EndReason) &&
            !string.IsNullOrWhiteSpace(reservationId))
        {
            lock (_gate)
            {
                if (!_disposing && !_admissionLeaseIds.Contains(reservationId) && !_completedAdmissionIds.Contains(reservationId))
                {
                    // Acquire before callback returns: Host releases recording lease after dispatch.
                    // Failure must propagate, not be reported as successful power handoff.
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    _powerGuard.AcquireAdmission(reservationId, "finalized_callback");
#else
                    _powerGuard.AcquireAdmission(reservationId);
#endif
                    _admissionLeaseIds.Add(reservationId);
                }
            }
        }
        try { _filePollWake.Set(); } catch (ObjectDisposedException) { }
    }

    private void CompleteAdmission(TvAirFinalizedRecordingFileDto file)
    {
        if (!string.IsNullOrWhiteSpace(file.ReservationId) && _admissionLeaseIds.Contains(file.ReservationId))
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _powerGuard.ReleaseAdmission(file.ReservationId, "journal_decision");
#else
            _powerGuard.ReleaseAdmission(file.ReservationId);
#endif
            _admissionLeaseIds.Remove(file.ReservationId);
            _completedAdmissionIds.Add(file.ReservationId);
        }
    }

    // SDK 1.1.16: durable journal is the sole new-recording admission route.
    // The legacy event is only a wake-up, because journal import is asynchronous.
    private void FinalizedFilePollLoop()
    {
        while (!_filePollStop.IsSet)
        {
            try { PollFinalizedFiles(); }
            #if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                // On unexpected journal/storage failures, stop instead of risking an in-memory cursor skip.
                _fileJournalBlocked = true;
                lock (_gate)
                {
                    foreach (var id in _admissionLeaseIds.ToArray())
                    {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                        _powerGuard.ReleaseAdmission(id, "journal_blocked");
#else
                        _powerGuard.ReleaseAdmission(id);
#endif
                    }
                    _admissionLeaseIds.Clear();
                }
                FileJournalError = "録画ファイル確定台帳の取得を停止しました。保存状態とHost Contract 1.1.18を確認してください。";
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                WriteDeveloperDiagnostic($"ENCODEBRIDGE_FINALIZED_POLL result=BLOCKED type={ex.GetType().Name} message={DiagnosticQuote(ex.Message)} cursor={Store.FinalizedNextSequence}");
#endif
            }
            if (WaitHandle.WaitAny(new[] { _filePollStop.WaitHandle, _filePollWake }, FilePollIntervalMs) == 0) break;
        }
    }

    private void PollFinalizedFiles()
    {
        lock (_gate)
        {
            if (!StorageHealthy || _fileJournalBlocked) return;
            // Recheck pending files before fetching newer journal entries.
            // A missing drive must not prevent later, unrelated recordings from being admitted.
            if (Store.Settings.AutoEncodeEnabled)
            {
                var pendingChanged = false;
                var completedPending = new List<TvAirFinalizedRecordingFileDto>();
                foreach (var pending in Store.PendingFinalizedFiles.Take(32).ToArray())
                {
                    if (!TryAcceptFinalizedFile(pending)) continue;
                    Store.PendingFinalizedFiles.Remove(pending);
                    pendingChanged = true;
                    completedPending.Add(pending);
                }
                if (pendingChanged)
                {
                    _storage.Save(Store);
                    foreach (var completed in completedPending) CompleteAdmission(completed);
                }
            }
            _scheduler.Evaluate();
            // Bounded work per poll; never load the full Host journal into memory.
            for (var pageNumber = 0; pageNumber < 4; pageNumber++)
            {
                var page = _ctx.RecordingFiles.ListFinalized(new TvAirFinalizedRecordingFileQueryDto
                {
                    JournalId = Store.FinalizedJournalId,
                    AfterSequence = Store.FinalizedNextSequence,
                    AfterRecordingFileId = Store.FinalizedNextRecordingFileId,
                    Limit = 64
                });
                if (string.IsNullOrWhiteSpace(page.JournalId))
                    throw new InvalidDataException("Host returned an empty recording file journal ID.");
                if (Store.FinalizedJournalId is not null && !string.Equals(Store.FinalizedJournalId, page.JournalId, StringComparison.Ordinal))
                    throw new InvalidDataException("Host recording file journal changed; explicit reconciliation is required.");
                if (page.Items.Count == 0) return;
                var pageChanged = false;
                var completedFiles = new List<TvAirFinalizedRecordingFileDto>();
                foreach (var file in page.Items)
                {
                    if (file.Sequence <= Store.FinalizedNextSequence || string.IsNullOrWhiteSpace(file.RecordingFileId) || !file.FileReleased)
                        throw new InvalidDataException("Invalid finalized recording file entry or sequence.");
                    // Past sessions are never replayed, even when the Host journal cursor
                    // points before this startup. Still advance the durable cursor normally.
                    if (file.ReleasedAt <= _sessionStartedAt)
                    {
                        MarkFinalizedFileProcessed(file.RecordingFileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                        WriteDeveloperDiagnostic($"ENCODEBRIDGE_FILE_DECISION result=SKIPPED reason=previous_session fileId={file.RecordingFileId}");
#endif
                    }
                    else if (!TryAcceptFinalizedFile(file))
                    {
                        if (Store.PendingFinalizedFiles.Count >= 256)
                        {
                            if (pageChanged) _storage.Save(Store);
                            _scheduler.Evaluate();
                            return; // fixed memory/storage bound: don't skip this file
                        }
                        Store.PendingFinalizedFiles.Add(file);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                        WriteDeveloperDiagnostic($"ENCODEBRIDGE_FILE_DECISION result=DEFERRED service={DiagnosticQuote(file.ServiceName)} title={DiagnosticQuote(file.ProgramTitle)} fileId={file.RecordingFileId}");
#endif
                    }
                    else
                    {
                        // Queue lease precedes admission release; durable storage commit below.
                        completedFiles.Add(file);
                    }
                    // A persisted queue, a deferred record, or a persisted skip precedes the cursor.
                    Store.FinalizedJournalId = page.JournalId;
                    Store.FinalizedNextSequence = file.Sequence;
                    Store.FinalizedNextRecordingFileId = file.RecordingFileId;
                    pageChanged = true;
                }
                if (pageChanged)
                {
                    // Use the Host-provided page anchor as the exact pagination position.
                    if (page.NextSequence < Store.FinalizedNextSequence || string.IsNullOrWhiteSpace(page.NextRecordingFileId))
                        throw new InvalidDataException("Invalid Host finalized-file page cursor.");
                    Store.FinalizedJournalId = page.JournalId;
                    Store.FinalizedNextSequence = page.NextSequence;
                    Store.FinalizedNextRecordingFileId = page.NextRecordingFileId;
                    _storage.Save(Store); // atomic queue / deferral / skip / cursor storage transaction
                    foreach (var completed in completedFiles) CompleteAdmission(completed);
                }
                _scheduler.Evaluate();
                if (!page.HasMore) return;
            }
        }
    }

    private void MarkFinalizedFileProcessed(string recordingFileId)
    {
        // The journal sequence/cursor is authoritative. Keep only a bounded recent-ID cache.
        if (Store.ProcessedRecordingFileIds.Count >= 4096)
            Store.ProcessedRecordingFileIds.Clear();
        Store.ProcessedRecordingFileIds.Add(recordingFileId);
    }

    // The finalized-file journal supplies EndReason as a Host string, not an enum.
    // Exclude only explicit user/manual-stop reasons; never classify an unexplained
    // WriterExited or a generic failed/canceled result as manual.
    private static bool IsExplicitManualRecordingStop(string? endReason)
    {
        if (string.IsNullOrWhiteSpace(endReason)) return false;
        var normalized = new string(endReason.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized is "manual" or "manualstop" or "manuallystopped" or
            "userstop" or "userstopped" or "userrequestedstop" or
            "userinitiatedstop" or "stoppedbyuser" or "stopbyuser" or
            "manualrecordingstop" or "recordingmanualstop";
    }

    private bool TryAcceptFinalizedFile(TvAirFinalizedRecordingFileDto file)
    {
        if (Store.ProcessedRecordingFileIds.Contains(file.RecordingFileId)) return true;
        if (IsExplicitManualRecordingStop(file.EndReason))
        {
            // A manual stop is an explicit user decision: do not auto-convert or
            // leave it pending for a later run, even when a profile is selected.
            MarkFinalizedFileProcessed(file.RecordingFileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_FILE_DECISION result=SKIPPED reason=manual_recording_stop service={DiagnosticQuote(file.ServiceName)} title={DiagnosticQuote(file.ProgramTitle)} endReason={DiagnosticQuote(file.EndReason)}");
#endif
            return true;
        }
        if (!Store.Settings.AutoEncodeEnabled)
        {
            // OFF means explicit non-admission, not a backlog to convert on the next ON.
            MarkFinalizedFileProcessed(file.RecordingFileId);
            return true;
        }
        // The Host journal EndReason can be empty or generic even when the actual
        // reservation terminal state is Cancelled (confirmed on 2026-10-25).
        // Resolve authoritative reservation metadata BEFORE any automatic admission,
        // including journal replay after restart and deferred retry. Missing metadata
        // stays pending, rather than converting a potentially user-stopped recording.
        var rec = new RecordingSnapshot
        {
            RecordingFileId = file.RecordingFileId,
            RecordingId = file.RecordingFileId,
            ReservationId = file.ReservationId,
            SourceFilePath = file.FilePath,
            ServiceName = file.ServiceName,
            Title = file.ProgramTitle,
            ActualEnd = file.ReleasedAt
        };
        if (!TryResolveReservationIdentity(file, rec, out var cancelledAfterStart)) return false;
        if (cancelledAfterStart)
        {
            MarkFinalizedFileProcessed(file.RecordingFileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_FILE_DECISION result=SKIPPED reason=cancelled_recording_after_start service={DiagnosticQuote(file.ServiceName)} title={DiagnosticQuote(file.ProgramTitle)} endReason={DiagnosticQuote(file.EndReason)}");
#endif
            return true;
        }
        var bindings = file.PluginBindings.Where(x =>
            string.Equals(x.PluginId, "encodebridge", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.FeatureId, "encode-profile", StringComparison.OrdinalIgnoreCase)).ToArray();
        var isKeywordReservation = string.Equals(file.ReservationSource, "Keyword", StringComparison.OrdinalIgnoreCase);
        // 【Host担当者向け・予約種別別の適用契約（変更禁止）】
        // (1) 「自動検索予約」だけが個別オプトイン方式。Hostが渡す ReservationSource == "Keyword"
        //     で判定し、当該自動検索ルールで「EncodeBridgeを利用」がチェック済み、かつ
        //     そのルールの「変換プロファイル」が選択済みのときだけ変換する。
        //     Hostから encodebridge / encode-profile の PluginBinding が正確に1件届き、
        //     SelectionId が空でないことが両条件成立の証拠。チェックなし・選択なし・
        //     重複Bindingはすべて SKIP。デフォルト設定で穴埋めしてはならない。
        // (2) 自動検索「以外」は通常予約。番組表、AI-rhythm、プログラム予約、今すぐ録画、
        //     source=Manual 等は「EncodeBridgeを利用」の個別チェックを要求しない。
        //     EncodeBridge全体の自動変換ONおよび通常の対象判定を満たせば、
        //     EncodeBridgeのデフォルトプロファイルに従って変換する。
        //     source=Manual はAI-rhythm専用の意味ではない。
        // (3) Hostは自動検索ルール専用のPluginBindingを通常予約へコピーしてはならない。
        //     UI上のオプトイン設定は自動検索ルールにのみ適用し、通常予約の変換を
        //     Binding欠如を理由にスキップしないこと。
        // ※ この分岐は予約種別と変換プロファイルの選択規則。サイズ1MB以上、
        //    手動録画停止除外、重複除外、全体自動変換OFF等の共通条件は別途適用される。
        if (isKeywordReservation && (bindings.Length != 1 || string.IsNullOrWhiteSpace(bindings[0].SelectionId)))
        {
            MarkFinalizedFileProcessed(file.RecordingFileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_FILE_DECISION result=SKIPPED reason=reservation_not_opted_in service={DiagnosticQuote(file.ServiceName)} title={DiagnosticQuote(file.ProgramTitle)} reservationSource={DiagnosticQuote(file.ReservationSource)} bindingCount={bindings.Length}");
#endif
            return true;
        }
        if (string.IsNullOrWhiteSpace(file.FilePath)) return false;
        long size;
        try
        {
            var info = new FileInfo(file.FilePath);
            if (!info.Exists) return false;
            // Exclusive read probe avoids encoder racing an external writer.
            using (var reader = new FileStream(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                size = reader.Length;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        if (size < MinRecordingBytes)
        {
            MarkFinalizedFileProcessed(file.RecordingFileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_FILE_DECISION result=SKIPPED reason=below_1mb service={DiagnosticQuote(file.ServiceName)} title={DiagnosticQuote(file.ProgramTitle)} bytes={size}");
#endif
            return true;
        }
        // Migration safeguard: a file already enqueued by pre-1.1.16 event handling is not converted twice.
        if (Store.Queue.Any(x => string.Equals(x.Recording.SourceFilePath, file.FilePath, StringComparison.OrdinalIgnoreCase)) ||
            Store.History.Any(x => string.Equals(x.SourceFilePath, file.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            MarkFinalizedFileProcessed(file.RecordingFileId);
            return true;
        }
        // 【プロファイル決定】自動検索（ReservationSource == Keyword）のみ、そのルールが
        // 明示選択したBinding.SelectionIdと一致する有効プロファイルを使用する。
        // 無効・削除済み等で見つからなければSKIP。デフォルトにはフォールバックしない。
        // 自動検索以外（Manualを含む）はBindingに関係なくデフォルトを使用する。
        var profile = isKeywordReservation
            ? Store.Profiles.FirstOrDefault(x => x.Enabled &&
                string.Equals(x.ProfileId, bindings[0].SelectionId, StringComparison.OrdinalIgnoreCase))
            : _rules.Resolve(Store, rec);
        var tool = profile is null ? null : Store.Tools.FirstOrDefault(x => x.ToolId == profile.ToolId);
        if (tool is null || profile is null || string.IsNullOrWhiteSpace(tool.ExecutablePath))
        {
            // Persist the unavailable configured profile decision without guessing an alternative profile.
            MarkFinalizedFileProcessed(file.RecordingFileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_FILE_DECISION result=SKIPPED reason=profile_or_tool_unavailable service={DiagnosticQuote(file.ServiceName)} title={DiagnosticQuote(file.ProgramTitle)} reservationSource={DiagnosticQuote(file.ReservationSource)} profileSource={(isKeywordReservation ? "binding" : "default")} bindingCount={bindings.Length}");
#endif
            return true;
        }
        var snap = new EncodeProfileSnapshot
        {
            ProfileId = profile.ProfileId, ProfileName = ProfileDisplay.Label(Store, profile),
            ToolId = tool.ToolId, ToolKind = tool.Kind, ToolName = tool.DisplayName,
            ExecutablePath = tool.ExecutablePath, VideoCodec = profile.VideoCodec,
            Quality = profile.Quality, AudioTracks = profile.AudioTracks, AudioCodec = profile.AudioCodec,
            Subtitles = profile.Subtitles, Container = profile.Container,
            OutputDirectoryMode = profile.OutputDirectoryMode, OutputDirectory = profile.OutputDirectory,
            SourceSubdirectoryName = profile.SourceSubdirectoryName,
            AdapterSettings = new(profile.AdapterSettings, StringComparer.OrdinalIgnoreCase),
            AdditionalArguments = new(profile.AdditionalArguments),
            PostEncodeBatch = profile.PostEncodeBatch,
            PostEncodeBatchEnabled = profile.PostEncodeBatchEnabled && profile.PostEncodeBatchRiskAccepted,
            PostEncodeBatchRiskAccepted = profile.PostEncodeBatchRiskAccepted
        };
        foreach (var kv in tool.Settings) snap.AdapterSettings.TryAdd(kv.Key, kv.Value);
        // Keep resident queued work bounded; excess journal records remain durably deferred.
        if (Store.Queue.Count >= 256) return false;
        var queueItem = new EncodeQueueItem { Recording = rec, Profile = snap, AdmissionPolicyVerified = true };
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _powerGuard.Acquire(queueItem.QueueItemId, "FinalizedFileJournal");
#else
        _powerGuard.Acquire(queueItem.QueueItemId);
#endif
        Store.Queue.Add(queueItem);
        MarkFinalizedFileProcessed(file.RecordingFileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        WriteDeveloperDiagnostic($"ENCODEBRIDGE_FILE_DECISION result=QUEUED service={DiagnosticQuote(file.ServiceName)} title={DiagnosticQuote(file.ProgramTitle)} bytes={size} fileId={file.RecordingFileId} queueItemId={queueItem.QueueItemId} reservationSource={DiagnosticQuote(file.ReservationSource)} profileSource={(isKeywordReservation ? "binding" : "default")} bindingCount={bindings.Length}");
#endif
        return true;
    }

    private bool TryResolveReservationIdentity(TvAirFinalizedRecordingFileDto file, RecordingSnapshot rec, out bool cancelledAfterStart)
    {
        cancelledAfterStart = false;
        try
        {
            var reservation = _ctx.Reservations.Get(file.ReservationId);
            if (reservation is null)
                reservation = _ctx.Reservations.ListHistory(new TvAirReservationHistoryQueryDto
                {
                    ReservationId = file.ReservationId, Limit = 1
                }).FirstOrDefault(x => string.Equals(x.ReservationId, file.ReservationId, StringComparison.OrdinalIgnoreCase));
            if (reservation is null && !string.IsNullOrWhiteSpace(file.RecoveryParentReservationId))
            {
                reservation = _ctx.Reservations.Get(file.RecoveryParentReservationId) ??
                    _ctx.Reservations.ListHistory(new TvAirReservationHistoryQueryDto
                    {
                        ReservationId = file.RecoveryParentReservationId, Limit = 1
                    }).FirstOrDefault(x => string.Equals(x.ReservationId, file.RecoveryParentReservationId, StringComparison.OrdinalIgnoreCase));
            }
            if (reservation is null || reservation.ServiceId <= 0 || reservation.ServiceId > ushort.MaxValue)
                return false;
            // A finalized file whose reservation has terminal status Cancelled
            // must never be admitted to automatic conversion.
            // This protects the manual-stop case when journal EndReason is not a
            // known manual-stop literal. It also conservatively excludes other
            // cancelled-after-start results rather than guessing user intent.
            cancelledAfterStart = string.Equals(reservation.Status, "Cancelled", StringComparison.OrdinalIgnoreCase);
            rec.NetworkId = (ushort)Math.Clamp(reservation.NetworkId, 0, ushort.MaxValue);
            rec.TransportStreamId = (ushort)Math.Clamp(reservation.TransportStreamId, 0, ushort.MaxValue);
            rec.ServiceId = (ushort)reservation.ServiceId;
            rec.EventId = (ushort)Math.Clamp(reservation.EventNumber, 0, ushort.MaxValue);
            rec.ScheduledStart = reservation.ScheduledStart ?? reservation.Start;
            rec.ActualStart = reservation.RecordingStartedAt;
            return true;
        }
        #if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
        #else
        catch (Exception)
        #endif
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            WriteDeveloperDiagnostic($"ENCODEBRIDGE_FILE_METADATA result=DEFERRED service={DiagnosticQuote(file.ServiceName)} title={DiagnosticQuote(file.ProgramTitle)} error={ex.GetType().Name}");
#endif
            return false;
        }
    }

    public void PublishProfileSelections()
    {
        if (!StorageHealthy)
        {
            _ctx.PluginSelections.Publish("encode-profile", "変換プロファイル", false, Array.Empty<TvAirPluginSelectionOptionDto>());
            return;
        }

        var options = Store.Profiles
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.ProfileId))
            .Select(x => new
            {
                Profile = x,
                Tool = Store.Tools.FirstOrDefault(t => string.Equals(t.ToolId, x.ToolId, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => x.Tool is not null)
            .Select(x => new TvAirPluginSelectionOptionDto
            {
                SelectionId = x.Profile.ProfileId,
                DisplayName = ProfileDisplay.Label(Store, x.Profile)
            })
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.SelectionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // 【Host向けUI契約】この選択肢は「自動検索ルールの個別設定」のために公開する。
        // 自動検索ルールではチェックON＋ここからのプロファイル選択の両方が必須。
        // 通常予約（番組表・AI-rhythm・プログラム予約・今すぐ録画等）にこのチェック欄を
        // 追加したり、未選択を理由に通常予約をスキップしたりしてはならない。
        // 通常予約はEncodeBridge内のデフォルト設定で自動変換する。
        var enabled = Store.Settings.AutoEncodeEnabled && options.Length > 0;
        _ctx.PluginSelections.Publish("encode-profile", "変換プロファイル", enabled, options);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        WriteDeveloperDiagnostic($"ENCODEBRIDGE_PROFILE_SELECTION_PUBLISH result=OK enabled={enabled} options={options.Length} auto={Store.Settings.AutoEncodeEnabled} storageHealthy={StorageHealthy}");
#endif
    }

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

    private static string DiagnosticQuote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        return "\"" + escaped + "\"";
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

    private static T? ConvertPayload<T>(object? payload) where T : class
    {
        if (payload is T typed) return typed;
        if (payload is null) return null;
        try
        {
            if (payload is JsonElement element)
                return element.Deserialize<T>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload is string json)
                return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(payload), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
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
        lock (_gate) _disposing = true;
        foreach (var subscription in _subs) subscription.Dispose();
        _subs.Clear();
        _filePollStop.Set();
        _filePollWake.Set();
        var pollThread = _filePollThread;
        if (pollThread is not null && pollThread.IsAlive && pollThread != Thread.CurrentThread)
            pollThread.Join(TimeSpan.FromSeconds(2));
        if (pollThread is null || !pollThread.IsAlive) { _filePollStop.Dispose(); _filePollWake.Dispose(); }
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
        try { _ctx.PluginSelections.Remove("encode-profile"); } catch { }
        lock (_gate) _admissionLeaseIds.Clear();
        _powerGuard.Dispose();
    }
}
