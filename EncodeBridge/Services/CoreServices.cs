using System.Security.Cryptography;
using System.Text;
using EncodeBridge.Adapters;
using EncodeBridge.Domain;
using EncodeBridge.Storage;
using EncodeBridge.Transport;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;

namespace EncodeBridge.Services;

internal sealed class AdapterRegistry
{
    private readonly Dictionary<EncodeToolKind, IEncodeAdapter> _map;

    public AdapterRegistry()
    {
        IEncodeAdapter[] adapters =
        {
            new FfmpegAdapter(), new NvEncAdapter(), new QsvEncAdapter(), new VceEncAdapter(),
            new HandBrakeAdapter(), new AmatsukazeAdapter(), new TmpgEncAdapter(), new CustomCliAdapter()
        };
        _map = adapters.ToDictionary(adapter => adapter.Kind);
    }

    public IEncodeAdapter Get(EncodeToolKind kind) => _map.TryGetValue(kind, out var adapter)
        ? adapter
        : throw new InvalidOperationException("対応する変換ツールがありません。");
}

internal sealed class EncodeScheduler
{
    private readonly ITvAirPluginRuntimeContext _ctx;
    private readonly EncodeBridgeStorage _storage;
    private readonly EncodeBridgeStore _store;
    private readonly AdapterRegistry _adapters;
    private readonly EncodeBridgePowerGuard _powerGuard;
    private readonly TsServiceInspector _inspector = new();
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    private readonly Action<string>? _diagnostic;

    public EncodeScheduler(ITvAirPluginRuntimeContext ctx, EncodeBridgeStorage storage,
        EncodeBridgeStore store, AdapterRegistry adapters, EncodeBridgePowerGuard powerGuard,
        Action<string>? diagnostic = null)
    {
        _ctx = ctx;
        _storage = storage;
        _store = store;
        _adapters = adapters;
        _powerGuard = powerGuard;
        _diagnostic = diagnostic;
    }
#else
    public EncodeScheduler(ITvAirPluginRuntimeContext ctx, EncodeBridgeStorage storage,
        EncodeBridgeStore store, AdapterRegistry adapters, EncodeBridgePowerGuard powerGuard)
    {
        _ctx = ctx;
        _storage = storage;
        _store = store;
        _adapters = adapters;
        _powerGuard = powerGuard;
    }
#endif

    public void Evaluate()
    {
        var historyBeforeReconcile = _store.History.Count;
        ReconcileJobs();
        var maxConcurrent = Math.Clamp(_store.Settings.MaxConcurrentEncodes,
            EncodeBridgeDefaults.MinConcurrentEncodes, EncodeBridgeDefaults.MaxConcurrentEncodesLimit);
        var waiting = _store.Queue.Where(x => x.State == EncodeQueueState.Waiting)
            .OrderBy(x => x.AddedAt).ToArray();
        var runningCount = _store.Queue.Count(x => x.State == EncodeQueueState.Running);
        if (waiting.Length > 0 && runningCount < maxConcurrent)
        {
            if (_store.Settings.PauseStartWhileRecording && _ctx.System.GetStatus().ActiveRecordingCount > 0)
            {
                foreach (var item in waiting)
                    item.WaitingReason = "録画中のため開始を待っています。";
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_EVALUATE result=DEFERRED reason=recording waiting={waiting.Length} running={runningCount}");
#endif
            }
            else if (_store.Settings.PauseStartWhileEpg && _ctx.Epg.GetStatus().IsRunning)
            {
                foreach (var item in waiting)
                    item.WaitingReason = "番組情報を取得中のため開始を待っています。";
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_EVALUATE result=DEFERRED reason=epg waiting={waiting.Length} running={runningCount}");
#endif
            }
            else
            {
                foreach (var item in waiting)
                {
                    if (_store.Queue.Count(x => x.State == EncodeQueueState.Running) >= maxConcurrent)
                        break;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    var consumed = TryStart(item);
                    if (!consumed)
                        _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_ITEM result=DEFERRED queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId} reason={item.WaitingReason}");
#else
                    TryStart(item);
#endif
                }
            }
        }
        // Persist a live queue or a changed history; an unchanged idle poll does not
        // rewrite the stored history.
        var purged = PurgeHistory();
        if (_store.Queue.Count != 0 || purged != 0 || _store.History.Count != historyBeforeReconcile)
            _storage.Save(_store);
    }

    public EncodeQueueCommandResult Retry(string id)
    {
        var h = _store.History.FirstOrDefault(x => x.QueueItemId == id && (x.Result == EncodeQueueState.Failed || x.Result == EncodeQueueState.Stopped || x.Result == EncodeQueueState.Unverified));
        if (h is null) return EncodeQueueCommandResult.Rejected("再実行できる履歴がありません。");
        // A history row remains immutable. A retry is a new attempt with its own
        // identity, so abandoning/removing the new waiting item cannot erase it.
        if (_store.Queue.Any(x => RecordingFileIdentity.SameRecording(x.Recording, h.Recording)))
            return EncodeQueueCommandResult.Rejected("この録画ファイルは変換待ちまたは変換中です。");
        var previousAttempt = h.AttemptNumber > 0 ? h.AttemptNumber : 1;
        var retryProfile = h.Profile;

        // A retry uses the current enabled profile and executable where possible.
        // If the original profile was deleted, use the current default; the existing
        // history snapshot remains intact.
        var currentProfile = EncodeProfilePolicy.ResolveEnabledById(_store, h.Profile.ProfileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        var profileSource = "original-current";
#endif
        if (currentProfile is null)
        {
            currentProfile = EncodeProfilePolicy.ResolveDefault(_store);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            if (currentProfile is not null) profileSource = "current-default";
#endif
        }

        if (currentProfile is not null)
        {
            var currentTool = EncodeProfilePolicy.ResolveTool(_store, currentProfile.ToolId);
            if (currentTool is not null)
            {
                retryProfile = EncodeProfileSnapshotFactory.Create(_store, currentProfile, currentTool);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_RETRY_PROFILE result=RESNAPSHOT queueItemId={h.QueueItemId} recordingId={h.RecordingId} source={profileSource} profileId={currentProfile.ProfileId} tool={currentTool.Kind}");
#endif
            }
        }

        var q = new EncodeQueueItem
        {
            QueueItemId = Guid.NewGuid().ToString("N"),
            Recording = h.Recording,
            Profile = retryProfile,
            AttemptNumber = checked(previousAttempt + 1),
            State = EncodeQueueState.Waiting,
            AddedAt = DateTimeOffset.Now
        };
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _powerGuard.Acquire(q.QueueItemId,"Retry");
#else
        _powerGuard.Acquire(q.QueueItemId);
#endif
        _store.Queue.Add(q);
        try { _storage.Save(_store); }
        catch
        {
            _store.Queue.Remove(q);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _powerGuard.Release(q.QueueItemId, "retry_save_failed");
#else
            _powerGuard.Release(q.QueueItemId);
#endif
            throw;
        }
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_ACCEPTED queueItemId={q.QueueItemId} recordingId={q.Recording.RecordingId} powerHeld={_powerGuard.IsHeld(q.QueueItemId)} boundary=retry_queue_accept");
#endif
        Evaluate();
        // Read the authoritative post-evaluation state, not merely the acceptance
        // of the retry request. It may already have failed, started or remained waiting.
        var queued = _store.Queue.FirstOrDefault(x => x.QueueItemId == q.QueueItemId);
        if (queued is not null)
        {
            if (queued.State == EncodeQueueState.Running)
                return EncodeQueueCommandResult.Started();
            if (queued.State == EncodeQueueState.Waiting)
                return EncodeQueueCommandResult.Waiting(queued.WaitingReason);
        }
        var terminal = _store.History.FirstOrDefault(x => x.QueueItemId == q.QueueItemId
            && x.AttemptNumber == q.AttemptNumber);
        if (terminal is not null)
        {
            // Very short external jobs may reach a terminal state inside Evaluate.
            // Report the actual outcome rather than treating every terminal as an error.
            if (terminal.Result == EncodeQueueState.Completed)
                return EncodeQueueCommandResult.Completed();
            if (terminal.Result == EncodeQueueState.HandedOff)
                return EncodeQueueCommandResult.HandedOff();
            return EncodeQueueCommandResult.Rejected(string.IsNullOrWhiteSpace(terminal.FailureMessage)
                ? "変換を完了できませんでした。履歴を確認してください。"
                : $"変換を完了できませんでした。{terminal.FailureMessage}");
        }
        return EncodeQueueCommandResult.Rejected("再実行の状態を確認できませんでした。");
    }

    public EncodeQueueCommandResult Stop(string id)
    {
        var item = _store.Queue.FirstOrDefault(x => x.QueueItemId == id && x.State == EncodeQueueState.Running);
        if (item is null || string.IsNullOrWhiteSpace(item.ExternalJobId))
            return EncodeQueueCommandResult.Rejected("停止できる変換がありません。");
        return _ctx.ExternalJobs.Cancel(item.ExternalJobId)
            ? EncodeQueueCommandResult.StopRequested()
            : EncodeQueueCommandResult.Rejected("停止を要求できませんでした。");
    }

    public EncodeQueueCommandResult RemoveWaiting(string id)
    {
        var item = _store.Queue.FirstOrDefault(x => x.QueueItemId == id && x.State == EncodeQueueState.Waiting);
        if (item is null)
            return EncodeQueueCommandResult.Rejected("削除できる待機中の変換がありません。");
        _store.Queue.Remove(item);
        try { _storage.Save(_store); }
        catch
        {
            _store.Queue.Add(item);
            throw;
        }
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _powerGuard.Release(item.QueueItemId, "waiting_removed");
#else
        _powerGuard.Release(item.QueueItemId);
#endif
        return EncodeQueueCommandResult.Removed();
    }

    // A waiting item owns no running encoder. Release its power lease until the
    // next successful dispatch; keep its durable queue entry and original output path.
    private bool DeferWaiting(EncodeQueueItem item, string reason)
    {
        item.WaitingReason = reason;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _powerGuard.Release(item.QueueItemId, "waiting_before_dispatch");
#else
        _powerGuard.Release(item.QueueItemId);
#endif
        return false;
    }

    private static bool HasSufficientOutputSpace(string directory, int minimumFreeSpaceGb)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return !string.IsNullOrWhiteSpace(root) &&
                new DriveInfo(root).AvailableFreeSpace >= (long)minimumFreeSpaceGb * 1024 * 1024 * 1024;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }

    // The normal output and adapter-provided output obey the same storage policy.
    // Never silently redirect to another disk or overwrite an existing file.
    private string? ValidateOutputStorage(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return "保存先を使用できません。";
        if (File.Exists(outputPath))
            return "出力ファイルが既に存在します。";
        if (!HasSufficientOutputSpace(directory, _store.Settings.MinimumFreeSpaceGb))
            return "保存先の空き容量を確認してください。";
        return null;
    }

    private bool TryStart(EncodeQueueItem item)
    {
        var dispatchAttempted = false;
        item.WaitingReason = string.Empty;
        // A legacy or frozen snapshot can still contain a mismatched executable.
        // Fail it without launching anything; an explicit retry can then adopt
        // the corrected saved profile, while the original TS remains untouched.
        var identityError = EncodeToolIdentity.IdentityError(item.Profile.ToolKind, item.Profile.ExecutablePath);
        if (identityError is not null)
        {
            Fail(item, identityError, null);
            return true;
        }
        if (!File.Exists(item.Recording.SourceFilePath))
        {
            Fail(item, "録画ファイルが見つかりません。", null);
            return true;
        }

        item.ExpectedOutputPath = ResolveOutputPath(item);
        var storageError = ValidateOutputStorage(item.ExpectedOutputPath);
        if (storageError is not null) return DeferWaiting(item, storageError);

        try
        {
            var service = _inspector.Inspect(item.Recording.SourceFilePath, item.Recording.ServiceId);
            var adapter = _adapters.Get(item.Profile.ToolKind);
            var check = adapter.ValidateTool(item.Profile);
            if (!check.Succeeded) return DeferWaiting(item, check.Message);
            check = adapter.Preflight(item, service);
            if (!check.Succeeded) return DeferWaiting(item, check.Message);

            var plan = adapter.BuildProcess(item, service);
            storageError = ValidateOutputStorage(plan.ExpectedOutputPath);
            if (storageError is not null) return DeferWaiting(item, storageError);

            // A lease must be held before the durable external dispatch boundary.
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _powerGuard.Acquire(item.QueueItemId, "output_ready");
#else
            _powerGuard.Acquire(item.QueueItemId);
#endif
            item.ClientRequestId = $"encodebridge:{item.QueueItemId}:{item.AttemptNumber}";
            item.ExpectedOutputPath = plan.ExpectedOutputPath;
            _storage.Save(_store);
            if (!plan.HandoffOnly && !string.IsNullOrWhiteSpace(plan.PreparedArtifactPath) && File.Exists(plan.PreparedArtifactPath))
                File.Delete(plan.PreparedArtifactPath);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_EXTERNAL_COMMAND recordingId={item.Recording.RecordingId} tool={item.Profile.ToolKind} exe={DiagnosticQuote(plan.ExecutablePath)} cwd={DiagnosticQuote(plan.WorkingDirectory ?? string.Empty)} args={DiagnosticCommandLine(plan.Arguments)} output={DiagnosticQuote(plan.ExpectedOutputPath)} staging={DiagnosticQuote(plan.PreparedArtifactPath ?? string.Empty)}");
#endif
            dispatchAttempted = true;
            var job = _ctx.ExternalJobs.Enqueue(CreateExternalJobRequest(item, item.ClientRequestId,
                new TvAirExternalProcessSpecDto
                {
                    ExecutablePath = plan.ExecutablePath,
                    Arguments = plan.Arguments,
                    WorkingDirectory = plan.WorkingDirectory
                }));
            if (!job.Accepted)
            {
                Fail(item, job.Message ?? "変換ツールを開始できませんでした。", job.ExitCode);
                return true;
            }

            item.ExternalJobId = job.JobId;
            item.State = EncodeQueueState.Running;
            item.StartedAt = job.StartedAt ?? DateTimeOffset.Now;
            item.HandoffOnly = plan.HandoffOnly;
            item.PreparedArtifactPath = plan.PreparedArtifactPath;
            _storage.Save(_store);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_EXTERNAL_JOB result=STARTED queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId} jobId={item.ExternalJobId} tool={item.Profile.ToolKind}");
#endif
            return true;
        }
        catch (Exception ex)
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_ITEM result={(dispatchAttempted ? "DISPATCH_UNVERIFIED" : "FAILED")} queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId} error={ex.GetType().Name}:{ex.Message}");
#endif
            if (dispatchAttempted)
            {
                // Once dispatch was attempted, an exception cannot prove that
                // the Host did not start the encoder. Never dispatch it again.
                item.State = EncodeQueueState.Running;
                throw new InvalidOperationException("変換開始の結果を確認できません。自動では再実行しません。", ex);
            }
            Fail(item, ex.Message, null);
            return true;
        }
    }

    // Encoding and post-processing are separate external jobs with the same
    // recording attribution and cancellation semantics.
    private static TvAirExternalJobRequestDto CreateExternalJobRequest(EncodeQueueItem item,
        string requestId, TvAirExternalProcessSpecDto process) => new()
    {
        Kind = TvAirExternalJobKind.EncodeRecording,
        ReservationId = item.Recording.ReservationId,
        RecordingId = string.IsNullOrWhiteSpace(item.Recording.RecordingFileId)
            ? item.Recording.RecordingId : null,
        ClientRequestId = requestId,
        Process = process
    };

    private void ReconcileJobs()
    {
        foreach (var item in _store.Queue.Where(x => x.State == EncodeQueueState.Running).ToArray())
        {
            if (string.IsNullOrWhiteSpace(item.ExternalJobId))
            {
                Finish(item, EncodeQueueState.Unverified, null, EncodeBridgeDefaults.UnverifiedResultMessage);
                continue;
            }
            TvAirExternalJobDto? job;
            try { job = _ctx.ExternalJobs.Get(item.ExternalJobId); }
            catch (Exception)
            {
                // An unavailable status query is not a terminal job result.
                // Keep the queue item and power lease until status can be reconciled.
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_EXTERNAL_STATUS result=DEFERRED queueItemId={item.QueueItemId} reason=status_unavailable");
#endif
                continue;
            }
            if (job is null)
            {
                Finish(item, EncodeQueueState.Unverified, null, EncodeBridgeDefaults.UnverifiedResultMessage);
                continue;
            }
            if (job.StateCode is TvAirExternalJobState.Queued or TvAirExternalJobState.Running)
                continue;

            if (job.StateCode == TvAirExternalJobState.Succeeded)
            {
                if (item.PostEncodeBatchRunning)
                {
                    Finish(item, EncodeQueueState.Completed, job.ExitCode, string.Empty);
                }
                else if (item.HandoffOnly)
                {
                    Finish(item, EncodeQueueState.HandedOff, job.ExitCode, string.Empty);
                }
                else if (TryFinalizePreparedOutput(item, out var finalizeError))
                {
                    item.OutputFinalized = true;
                    _storage.Save(_store);
                    if (!item.Profile.PostEncodeBatchEnabled || string.IsNullOrWhiteSpace(item.Profile.PostEncodeBatch))
                        Finish(item, EncodeQueueState.Completed, job.ExitCode, string.Empty);
                    else if (!TryStartPostEncodeBatch(item, out var postBatchError))
                        Finish(item, EncodeQueueState.Failed, null, postBatchError);
                }
                else
                {
                    Finish(item, EncodeQueueState.Failed, job.ExitCode, finalizeError);
                }
            }
            else if (job.StateCode == TvAirExternalJobState.Cancelled)
            {
                Finish(item, EncodeQueueState.Stopped, job.ExitCode, string.Empty);
            }
            else
            {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_EXTERNAL_FAILURE recordingId={item.Recording.RecordingId} jobId={job.JobId} phase={(item.PostEncodeBatchRunning ? "post_batch" : "encode")} tool={item.Profile.ToolKind} exitCode={job.ExitCode?.ToString() ?? "-"} failureCode={DiagnosticText(job.FailureCode)} message={DiagnosticText(job.Message)} stderr={DiagnosticText(job.StandardErrorTail)} stdout={DiagnosticText(job.StandardOutputTail)}");
#endif
                var failure = item.PostEncodeBatchRunning
                    ? (job.Message ?? job.StandardErrorTail ?? "変換後バッチを完了できませんでした。")
                    : BuildJobFailureMessage(item, job);
                Finish(item, EncodeQueueState.Failed, job.ExitCode, failure);
            }
        }
    }

    private bool TryStartPostEncodeBatch(EncodeQueueItem item, out string error)
    {
        error = string.Empty;
        string? createdBatchPath = null;
        var dispatchAttempted = false;
        try
        {
            var batchBody = item.Profile.PostEncodeBatch ?? string.Empty;
            if (batchBody.Length > EncodeBridgeDefaults.MaxPostEncodeBatchLength)
            {
                error = "変換後バッチの記述が長すぎます。";
                return false;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "EncodeBridge", "post-encode");
            Directory.CreateDirectory(tempDir);
            var batchPath = Path.Combine(tempDir, $"{item.QueueItemId}-{item.AttemptNumber}.cmd");
            createdBatchPath = batchPath;
            var expanded = ExpandPostEncodeBatch(batchBody, item);
            var script = "@echo off\r\nsetlocal DisableDelayedExpansion\r\n" + expanded + "\r\nexit /b %errorlevel%\r\n";
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var commandEncoding = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            File.WriteAllText(batchPath, script, commandEncoding);

            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comspec))
                comspec = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            if (!File.Exists(comspec))
            {
                error = "Windowsのコマンドプロセッサが見つかりません。";
                TryDelete(batchPath);
                return false;
            }

            var requestId = $"encodebridge:{item.QueueItemId}:{item.AttemptNumber}:post";
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POST_BATCH result=STARTING queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId} scriptLength={batchBody.Length} source={DiagnosticQuote(item.Recording.SourceFilePath)} output={DiagnosticQuote(item.ExpectedOutputPath)}");
#endif
            dispatchAttempted = true;
            var job = _ctx.ExternalJobs.Enqueue(CreateExternalJobRequest(item, requestId,
                new TvAirExternalProcessSpecDto
                {
                    ExecutablePath = comspec,
                    Arguments = new[] { "/d", "/s", "/c", batchPath },
                    WorkingDirectory = Path.GetDirectoryName(item.Recording.SourceFilePath)
                }));
            if (!job.Accepted)
            {
                TryDelete(batchPath);
                error = job.Message ?? "変換後バッチを開始できませんでした。";
                return false;
            }

            item.ExternalJobId = job.JobId;
            item.ClientRequestId = requestId;
            item.PostEncodeBatchRunning = true;
            item.PostEncodeBatchFilePath = batchPath;
            _storage.Save(_store);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POST_BATCH result=STARTED queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId} jobId={job.JobId}");
#endif
            return true;
        }
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
            if (dispatchAttempted)
            {
                // A dispatch exception cannot prove the Host rejected the job.
                // Preserve the script and power lease rather than claiming failure.
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_POST_BATCH result=DISPATCH_UNVERIFIED queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId}");
#endif
                throw;
            }
            TryDelete(createdBatchPath);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_POST_BATCH result=FAILED_TO_START queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId} error={DiagnosticQuote(ex.GetType().Name + ":" + ex.Message)}");
#endif
            CleanupPostEncodeBatch(item);
            error = "変換後バッチを開始できませんでした。";
            return false;
        }
    }

    private static string ExpandPostEncodeBatch(string batchBody, EncodeQueueItem item)
    {
        var source = item.Recording.SourceFilePath;
        var output = item.ExpectedOutputPath;
        return batchBody
            .Replace("{source}", BatchPathLiteral(source), StringComparison.OrdinalIgnoreCase)
            .Replace("{output}", BatchPathLiteral(output), StringComparison.OrdinalIgnoreCase)
            .Replace("{sourceDir}", BatchPathLiteral(Path.GetDirectoryName(source) ?? string.Empty), StringComparison.OrdinalIgnoreCase)
            .Replace("{outputDir}", BatchPathLiteral(Path.GetDirectoryName(output) ?? string.Empty), StringComparison.OrdinalIgnoreCase)
            .Replace("{sourceName}", BatchPathLiteral(Path.GetFileName(source)), StringComparison.OrdinalIgnoreCase)
            .Replace("{outputName}", BatchPathLiteral(Path.GetFileName(output)), StringComparison.OrdinalIgnoreCase);
    }

    private static string BatchPathLiteral(string value)
        => "\"" + (value ?? string.Empty).Replace("%", "%%", StringComparison.Ordinal) + "\"";

    private void CleanupPostEncodeBatch(EncodeQueueItem item)
    {
        TryDelete(item.PostEncodeBatchFilePath);
        item.PostEncodeBatchFilePath = string.Empty;
        item.PostEncodeBatchRunning = false;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }


    private bool TryFinalizePreparedOutput(EncodeQueueItem item, out string error)
    {
        error = string.Empty;
        var prepared = item.PreparedArtifactPath;
        if (string.IsNullOrWhiteSpace(prepared))
        {
            if (File.Exists(item.ExpectedOutputPath)) return true;
            error = "変換結果のファイルが見つかりません。";
            return false;
        }

        if (!File.Exists(prepared))
        {
            if (File.Exists(item.ExpectedOutputPath)) return true;
            error = "変換結果の一時ファイルが見つかりません。";
            return false;
        }

        try
        {
            if (File.Exists(item.ExpectedOutputPath))
            {
                error = "出力ファイルが既に存在するため、変換結果を確定できません。";
                return false;
            }

            File.Move(prepared, item.ExpectedOutputPath);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_OUTPUT_FINALIZE result=OK recordingId={item.Recording.RecordingId} tool={item.Profile.ToolKind} staging={DiagnosticQuote(prepared)} output={DiagnosticQuote(item.ExpectedOutputPath)}");
#endif
            return true;
        }
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_OUTPUT_FINALIZE result=FAILED recordingId={item.Recording.RecordingId} tool={item.Profile.ToolKind} staging={DiagnosticQuote(prepared)} output={DiagnosticQuote(item.ExpectedOutputPath)} error={DiagnosticQuote(ex.GetType().Name + ":" + ex.Message)}");
#endif
            error = "変換結果を保存先へ確定できませんでした。";
            return false;
        }
    }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    private static string DiagnosticCommandLine(IEnumerable<string> arguments)
        => string.Join(" ", arguments.Select(DiagnosticQuote));

    private static string DiagnosticQuote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        return "\"" + escaped + "\"";
    }

    private static string DiagnosticText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > 4000) text = text[^4000..];
        return DiagnosticQuote(text);
    }

#endif

    private string BuildJobFailureMessage(EncodeQueueItem item, TvAirExternalJobDto job)
    {
        var baseMessage = job.Message ?? job.StandardErrorTail ?? "変換を完了できませんでした。";
        if (item.Profile.ToolKind != EncodeToolKind.TmpgEnc || string.IsNullOrWhiteSpace(item.PreparedArtifactPath))
            return baseMessage;

        var logPath = item.PreparedArtifactPath + ".log";
        try
        {
            if (!File.Exists(logPath))
            {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_TMPGENC_PROJECTCREATOR result=FAILED recordingId={item.Recording.RecordingId} exitCode={job.ExitCode?.ToString() ?? "-"} logState=missing");
#endif
                return $"{baseMessage} / TMPGEnc Project Creatorログが生成されませんでした。";
            }

            var lines = File.ReadAllLines(logPath);
            var tail = string.Join(" | ", lines.Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(8));
            if (string.IsNullOrWhiteSpace(tail))
            {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_TMPGENC_PROJECTCREATOR result=FAILED recordingId={item.Recording.RecordingId} exitCode={job.ExitCode?.ToString() ?? "-"} logState=empty");
#endif
                return $"{baseMessage} / TMPGEnc Project Creatorログは空でした。";
            }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            var diagnosticTail = tail.Replace('\r', ' ').Replace('\n', ' ');
            if (diagnosticTail.Length > 1200) diagnosticTail = diagnosticTail[^1200..];
            _diagnostic?.Invoke($"ENCODEBRIDGE_TMPGENC_PROJECTCREATOR result=FAILED recordingId={item.Recording.RecordingId} exitCode={job.ExitCode?.ToString() ?? "-"} logState=captured detail={diagnosticTail}");
#endif
            return $"{baseMessage} / TMPGEnc Project Creator: {tail}";
        }
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_TMPGENC_PROJECTCREATOR result=FAILED recordingId={item.Recording.RecordingId} exitCode={job.ExitCode?.ToString() ?? "-"} logState=read_error error={ex.GetType().Name}:{ex.Message}");
#endif
            return $"{baseMessage} / TMPGEnc Project Creatorログを読み取れませんでした。";
        }
    }

    // The file path supplied by the Host remains authoritative. The selected output
    // directory is never silently redirected to another drive when a volume is full.
    private static string BuildOutputPath(EncodeQueueItem item)
    {
        var source = item.Recording.SourceFilePath;
        var sourceDirectory = Path.GetDirectoryName(source)!;
        var directory = item.Profile.OutputDirectoryMode switch
        {
            OutputDirectoryMode.Fixed => item.Profile.OutputDirectory,
            OutputDirectoryMode.SourceSubdirectory => Path.Combine(sourceDirectory, item.Profile.SourceSubdirectoryName),
            _ => sourceDirectory
        };
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + "." + item.Profile.Container.TrimStart('.'));
    }

    // Relay segments can have identical names on different source volumes while a
    // fixed output directory is shared. Reserve the destination across active and
    // completed jobs; never overwrite the output of another physical source file.
    private string ResolveOutputPath(EncodeQueueItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ExpectedOutputPath))
            return item.ExpectedOutputPath;

        var normal = BuildOutputPath(item);
        if (!OutputClaimedByAnotherSource(normal, item)) return normal;

        // Stable across restarts and independent of queue order. Hash the physical
        // source path, not the reservation ID (which is shared by relay segments).
        var canonicalSource = Path.GetFullPath(item.Recording.SourceFilePath).ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSource));
        var suffix = Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
        var directory = Path.GetDirectoryName(normal)!;
        var filename = Path.GetFileNameWithoutExtension(normal);
        var extension = Path.GetExtension(normal);
        var alternate = Path.Combine(directory, filename + "_" + suffix + extension);
        return alternate;
    }

    private bool OutputClaimedByAnotherSource(string output, EncodeQueueItem item)
    {
        return _store.Queue.Any(x => !ReferenceEquals(x, item)
            && string.Equals(string.IsNullOrWhiteSpace(x.ExpectedOutputPath) ? BuildOutputPath(x) : x.ExpectedOutputPath, output, StringComparison.OrdinalIgnoreCase)
            && !RecordingFileIdentity.SameSourcePath(x.Recording.SourceFilePath, item.Recording.SourceFilePath))
            || _store.History.Any(x => string.Equals(x.OutputFilePath, output, StringComparison.OrdinalIgnoreCase)
            && !RecordingFileIdentity.SameSourcePath(x.SourceFilePath, item.Recording.SourceFilePath));
    }

    internal int RecoverPreviousSessionQueue(DateTimeOffset sessionStartedAt)
    {
        var resolved = 0;
        var previousDispatched = new List<EncodeQueueItem>();
        // Only an unstarted, undispatched waiting item may continue automatically.
        // After dispatch (even before the first job acknowledgement), never start
        // another encoder on restart; preserve the source and staging files.
        foreach (var item in _store.Queue.ToArray())
        {
            if (item.State == EncodeQueueState.Waiting &&
                string.IsNullOrWhiteSpace(item.ClientRequestId) &&
                string.IsNullOrWhiteSpace(item.ExternalJobId) && item.StartedAt is null)
                continue;

            TvAirExternalJobDto? job = null;
            if (!string.IsNullOrWhiteSpace(item.ExternalJobId))
            {
                try { job = _ctx.ExternalJobs.Get(item.ExternalJobId); }
                catch { /* Unavailable job status is not proof of failure. */ }
            }
            if (job is not null && !string.IsNullOrWhiteSpace(item.ClientRequestId) &&
                !string.IsNullOrWhiteSpace(job.ClientRequestId) &&
                !string.Equals(item.ClientRequestId, job.ClientRequestId, StringComparison.Ordinal))
                job = null;

            var result = EncodeQueueState.Unverified;
            var message = EncodeBridgeDefaults.UnverifiedResultMessage;
            int? exit = job?.ExitCode;
            if (job?.StateCode == TvAirExternalJobState.Failed)
            {
                result = EncodeQueueState.Failed;
                message = job.Message ?? "前回の変換は失敗しました。";
            }
            else if (job?.StateCode == TvAirExternalJobState.Cancelled)
            {
                result = EncodeQueueState.Stopped;
                message = "前回の変換は停止しました。";
            }
            else if (job?.StateCode == TvAirExternalJobState.Succeeded)
            {
                if (item.PostEncodeBatchRunning && File.Exists(item.ExpectedOutputPath))
                {
                    result = EncodeQueueState.Completed;
                    message = string.Empty;
                }
                else if (item.HandoffOnly)
                {
                    result = EncodeQueueState.HandedOff;
                    message = string.Empty;
                }
                else if (!item.PostEncodeBatchRunning && item.OutputFinalized &&
                    (!item.Profile.PostEncodeBatchEnabled || string.IsNullOrWhiteSpace(item.Profile.PostEncodeBatch)) &&
                    File.Exists(item.ExpectedOutputPath))
                {
                    result = EncodeQueueState.Completed;
                    message = string.Empty;
                }
            }
            _store.Queue.Remove(item);
            _store.History.Add(ToHistory(item, result, exit, message,
                job?.FinishedAt ?? sessionStartedAt));
            previousDispatched.Add(item);
            resolved++;
        }
        if (resolved > 0)
        {
            _storage.Save(_store);
            // Remove orphaned temporary scripts only after the recovery history
            // is committed. No media/staging file is deleted or overwritten.
            foreach (var item in previousDispatched)
                CleanupPostEncodeBatch(item);
        }
        return resolved;
    }

    private static EncodeHistoryItem ToHistory(EncodeQueueItem item, EncodeQueueState state,
        int? exit, string message, DateTimeOffset finishedAt) => new()
    {
        QueueItemId = item.QueueItemId, RecordingId = item.Recording.RecordingId,
        Title = item.Recording.Title, ServiceName = item.Recording.ServiceName,
        ActualStart = item.Recording.ActualStart, SourceFilePath = item.Recording.SourceFilePath,
        OutputFilePath = item.ExpectedOutputPath, ProfileName = item.Profile.ProfileName,
        ToolName = item.Profile.ToolName, Result = state, StartedAt = item.StartedAt,
        FinishedAt = finishedAt, ExitCode = exit, FailureMessage = message,
        AttemptNumber = item.AttemptNumber, Recording = item.Recording, Profile = item.Profile
    };

    private void Fail(EncodeQueueItem item, string message, int? exitCode)
        => Finish(item, EncodeQueueState.Failed, exitCode, message);

    private void Finish(EncodeQueueItem item, EncodeQueueState state, int? exitCode, string message)
    {
        CleanupPostEncodeBatch(item);
        item.State = state;
        item.LastError = message;
        _store.Queue.Remove(item);
        _store.History.Add(ToHistory(item, state, exitCode, message, DateTimeOffset.Now));
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _powerGuard.Release(item.QueueItemId, $"terminal:{state}");
#else
        _powerGuard.Release(item.QueueItemId);
#endif
        try { _adapters.Get(item.Profile.ToolKind).Cleanup(item); }
        catch { /* Adapter cleanup must not replace the recorded terminal outcome. */ }
    }
    private int PurgeHistory()
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Clamp(_store.Settings.HistoryRetentionDays, EncodeBridgeDefaults.MinHistoryRetentionDays, EncodeBridgeDefaults.MaxHistoryRetentionDays));
        return _store.History.RemoveAll(x => x.FinishedAt < cutoff);
    }
}
