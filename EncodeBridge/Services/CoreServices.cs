using EncodeBridge.Adapters;
using EncodeBridge.Domain;
using EncodeBridge.Storage;
using EncodeBridge.Transport;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;

namespace EncodeBridge.Services;

internal sealed class RuleResolver
{
    public EncodeProfile? Resolve(EncodeBridgeStore store, RecordingSnapshot r)
    {
        foreach (var rule in store.Rules.Where(x=>x.Enabled).OrderBy(x=>x.Order))
        {
            var match = rule.Kind switch
            {
                EncodeRuleKind.TitleContains => r.Title.Contains(rule.MatchValue,StringComparison.OrdinalIgnoreCase),
                EncodeRuleKind.Channel => string.Equals(r.ServiceName,rule.MatchValue,StringComparison.OrdinalIgnoreCase),
                EncodeRuleKind.Genre => r.Genre.Contains(rule.MatchValue,StringComparison.OrdinalIgnoreCase),
                EncodeRuleKind.Default => true,
                _ => false
            };
            if (match) return store.Profiles.FirstOrDefault(x=>x.Enabled && x.ProfileId==rule.ProfileId);
        }
        return store.Profiles.FirstOrDefault(x=>x.Enabled);
    }
}

internal sealed class AdapterRegistry
{
    private readonly Dictionary<EncodeToolKind,IEncodeAdapter> _map;
    public AdapterRegistry() => _map = new IEncodeAdapter[]{new FfmpegAdapter(),new NvEncAdapter(),new QsvEncAdapter(),new VceEncAdapter(),new HandBrakeAdapter(),new AmatsukazeAdapter(),new TmpgEncAdapter(),new CustomCliAdapter()}.ToDictionary(x=>x.Kind);
    public IEncodeAdapter Get(EncodeToolKind kind)=>_map.TryGetValue(kind,out var a)?a:throw new InvalidOperationException("対応する変換ツールがありません。");
}

internal sealed class EncodeScheduler
{
    private readonly ITvAirPluginRuntimeContext _ctx; private readonly EncodeBridgeStorage _storage; private readonly EncodeBridgeStore _store; private readonly AdapterRegistry _adapters; private readonly EncodeBridgePowerGuard _powerGuard; private readonly TsServiceInspector _inspector=new();
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
    private readonly Action<string>? _diagnostic;
    public EncodeScheduler(ITvAirPluginRuntimeContext ctx,EncodeBridgeStorage storage,EncodeBridgeStore store,AdapterRegistry adapters,EncodeBridgePowerGuard powerGuard,Action<string>? diagnostic=null){_ctx=ctx;_storage=storage;_store=store;_adapters=adapters;_powerGuard=powerGuard;_diagnostic=diagnostic;}
#else
    public EncodeScheduler(ITvAirPluginRuntimeContext ctx,EncodeBridgeStorage storage,EncodeBridgeStore store,AdapterRegistry adapters,EncodeBridgePowerGuard powerGuard){_ctx=ctx;_storage=storage;_store=store;_adapters=adapters;_powerGuard=powerGuard;}
#endif

    public void Evaluate()
    {
        ReconcileJobs();
        var maxConcurrent=Math.Clamp(_store.Settings.MaxConcurrentEncodes,1,4);
        var waiting=_store.Queue.Where(x=>x.State==EncodeQueueState.Waiting).OrderBy(x=>x.AddedAt).ToArray();
        if(waiting.Length>0 && _store.Queue.Count(x=>x.State==EncodeQueueState.Running)<maxConcurrent)
        {
            if(_store.Settings.PauseStartWhileRecording&&_ctx.System.GetStatus().ActiveRecordingCount>0)
            {
                foreach(var item in waiting)item.WaitingReason="録画中のため開始を待っています。";
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_EVALUATE result=DEFERRED reason=recording waiting={waiting.Length} running={_store.Queue.Count(x=>x.State==EncodeQueueState.Running)}");
#endif
            }
            else if(_store.Settings.PauseStartWhileEpg&&_ctx.Epg.GetStatus().IsRunning)
            {
                foreach(var item in waiting)item.WaitingReason="番組情報を取得中のため開始を待っています。";
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_EVALUATE result=DEFERRED reason=epg waiting={waiting.Length} running={_store.Queue.Count(x=>x.State==EncodeQueueState.Running)}");
#endif
            }
            else
            {
                foreach(var item in waiting)
                {
                    if(_store.Queue.Count(x=>x.State==EncodeQueueState.Running)>=maxConcurrent)break;
                    var consumed=TryStart(item);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    if(!consumed)
                        _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_ITEM result=DEFERRED queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId} reason={item.WaitingReason}");
#endif
                }
            }
        }
        PurgeHistory(); _storage.Save(_store);
    }

    public bool Retry(string id)
    {
        var h = _store.History.FirstOrDefault(x => x.QueueItemId == id && (x.Result == EncodeQueueState.Failed || x.Result == EncodeQueueState.Stopped));
        if (h is null) return false;
        var previousAttempt = h.AttemptNumber > 0 ? h.AttemptNumber : 1;
        var retryProfile = h.Profile;

        // Retry with the current configuration. Prefer the original profile when it still
        // exists; if it was deleted, fall back to the profile currently selected for
        // post-recording automatic conversion (the Default rule). This prevents a retry
        // from silently reusing an obsolete snapshot after the user rebuilt the profile.
        var currentProfile = _store.Profiles.FirstOrDefault(x => x.Enabled && x.ProfileId == h.Profile.ProfileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        var profileSource = "original-current";
#endif
        if (currentProfile is null)
        {
            var defaultRule = _store.Rules
                .Where(x => x.Enabled && x.Kind == EncodeRuleKind.Default)
                .OrderBy(x => x.Order)
                .FirstOrDefault();
            if (defaultRule is not null)
            {
                currentProfile = _store.Profiles.FirstOrDefault(x => x.Enabled && x.ProfileId == defaultRule.ProfileId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                profileSource = "current-default";
#endif
            }
        }

        if (currentProfile is not null)
        {
            var currentTool = _store.Tools.FirstOrDefault(x => x.ToolId == currentProfile.ToolId);
            if (currentTool is not null)
            {
                retryProfile = SnapshotProfile(currentProfile, currentTool);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_RETRY_PROFILE result=RESNAPSHOT queueItemId={h.QueueItemId} recordingId={h.RecordingId} source={profileSource} profileId={currentProfile.ProfileId} tool={currentTool.Kind}");
#endif
            }
        }

        var q = new EncodeQueueItem
        {
            QueueItemId = h.QueueItemId,
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
        _store.History.Remove(h);
        _store.Queue.Add(q);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_ACCEPTED queueItemId={q.QueueItemId} recordingId={q.Recording.RecordingId} powerHeld={_powerGuard.IsHeld(q.QueueItemId)} boundary=retry_queue_accept");
#endif
        _storage.Save(_store);
        Evaluate();
        return true;
    }

    private static EncodeProfileSnapshot SnapshotProfile(EncodeProfile profile, EncodeTool tool)
    {
        var snapshot = new EncodeProfileSnapshot
        {
            ProfileId = profile.ProfileId,
            ProfileName = profile.Name,
            ToolId = tool.ToolId,
            ToolKind = tool.Kind,
            ToolName = tool.DisplayName,
            ExecutablePath = tool.ExecutablePath,
            VideoCodec = profile.VideoCodec,
            Quality = profile.Quality,
            AudioTracks = profile.AudioTracks,
            AudioCodec = profile.AudioCodec,
            Subtitles = profile.Subtitles,
            Container = profile.Container,
            OutputDirectoryMode = profile.OutputDirectoryMode,
            OutputDirectory = profile.OutputDirectory,
            SourceSubdirectoryName = profile.SourceSubdirectoryName,
            AdapterSettings = new(profile.AdapterSettings, StringComparer.OrdinalIgnoreCase),
            AdditionalArguments = new(profile.AdditionalArguments)
        };
        foreach (var kv in tool.Settings)
            snapshot.AdapterSettings.TryAdd(kv.Key, kv.Value);
        return snapshot;
    }

    public bool Stop(string id){var q=_store.Queue.FirstOrDefault(x=>x.QueueItemId==id&&x.State==EncodeQueueState.Running); if(q is null||string.IsNullOrWhiteSpace(q.ExternalJobId))return false; return _ctx.ExternalJobs.Cancel(q.ExternalJobId);}
    public bool RemoveWaiting(string id){var q=_store.Queue.FirstOrDefault(x=>x.QueueItemId==id&&x.State==EncodeQueueState.Waiting); if(q is null)return false; _store.Queue.Remove(q);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _powerGuard.Release(q.QueueItemId,"waiting_removed");
#else
        _powerGuard.Release(q.QueueItemId);
#endif
        _storage.Save(_store);return true;}

    private bool TryStart(EncodeQueueItem item)
    {
        item.WaitingReason=string.Empty;
        if(!File.Exists(item.Recording.SourceFilePath)){Fail(item,"録画ファイルが見つかりません。",null);return true;}
        item.ExpectedOutputPath=BuildOutputPath(item);
        var outDir=Path.GetDirectoryName(item.ExpectedOutputPath)!; if(!Directory.Exists(outDir)){item.WaitingReason="保存先を使用できません。";return false;}
        if(File.Exists(item.ExpectedOutputPath)){item.WaitingReason="出力ファイルが既に存在します。";return false;}
        try{var drive=new DriveInfo(Path.GetPathRoot(outDir)!); if(drive.AvailableFreeSpace < (long)_store.Settings.MinimumFreeSpaceGb*1024*1024*1024){item.WaitingReason="保存先の空き容量を確認してください。";return false;}}catch{item.WaitingReason="保存先を使用できません。";return false;}
        try
        {
            var service=_inspector.Inspect(item.Recording.SourceFilePath,item.Recording.ServiceId); var adapter=_adapters.Get(item.Profile.ToolKind); var chk=adapter.ValidateTool(item.Profile); if(!chk.Succeeded){item.WaitingReason=chk.Message;return false;} chk=adapter.Preflight(item,service); if(!chk.Succeeded){item.WaitingReason=chk.Message;return false;}
            item.ClientRequestId=$"encodebridge:{item.QueueItemId}:{item.AttemptNumber}"; _storage.Save(_store);
            var plan=adapter.BuildProcess(item,service); item.ExpectedOutputPath=plan.ExpectedOutputPath;
            if (!plan.HandoffOnly && !string.IsNullOrWhiteSpace(plan.PreparedArtifactPath) && File.Exists(plan.PreparedArtifactPath)) File.Delete(plan.PreparedArtifactPath);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_EXTERNAL_COMMAND recordingId={item.Recording.RecordingId} tool={item.Profile.ToolKind} exe={DiagnosticQuote(plan.ExecutablePath)} cwd={DiagnosticQuote(plan.WorkingDirectory ?? string.Empty)} args={DiagnosticCommandLine(plan.Arguments)} output={DiagnosticQuote(plan.ExpectedOutputPath)} staging={DiagnosticQuote(plan.PreparedArtifactPath ?? string.Empty)}");
#endif
            var job=_ctx.ExternalJobs.Enqueue(new TvAirExternalJobRequestDto{Kind=TvAirExternalJobKind.EncodeRecording,ReservationId=item.Recording.ReservationId,RecordingId=item.Recording.RecordingId,ClientRequestId=item.ClientRequestId,Process=new TvAirExternalProcessSpecDto{ExecutablePath=plan.ExecutablePath,Arguments=plan.Arguments,WorkingDirectory=plan.WorkingDirectory}});
            if(!job.Accepted){Fail(item,job.Message??"変換ツールを開始できませんでした。",job.ExitCode);return true;}
            item.ExternalJobId=job.JobId; item.State=EncodeQueueState.Running; item.StartedAt=job.StartedAt??DateTimeOffset.Now; item.HandoffOnly=plan.HandoffOnly; item.PreparedArtifactPath=plan.PreparedArtifactPath; _storage.Save(_store);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_EXTERNAL_JOB result=STARTED queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId} jobId={item.ExternalJobId} tool={item.Profile.ToolKind}");
#endif
            return true;
        }
        catch(Exception ex)
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_QUEUE_ITEM result=FAILED queueItemId={item.QueueItemId} recordingId={item.Recording.RecordingId} error={ex.GetType().Name}:{ex.Message}");
#endif
            Fail(item,ex.Message,null);
            return true;
        }
    }

    private void ReconcileJobs()
    {
        foreach(var item in _store.Queue.Where(x=>x.State==EncodeQueueState.Running).ToArray())
        {
            if(string.IsNullOrWhiteSpace(item.ExternalJobId)){Finish(item,EncodeQueueState.Stopped,null,"前回の変換は完了していません。");continue;}
            var job=_ctx.ExternalJobs.Get(item.ExternalJobId); if(job is null){Finish(item,EncodeQueueState.Stopped,null,"前回の変換は完了していません。");continue;}
            if(job.StateCode is TvAirExternalJobState.Queued or TvAirExternalJobState.Running) continue;
            var handoff=item.HandoffOnly;
            if(job.StateCode==TvAirExternalJobState.Succeeded)
            {
                if(handoff) Finish(item,EncodeQueueState.HandedOff,job.ExitCode,string.Empty);
                else if(TryFinalizePreparedOutput(item, out var finalizeError)) Finish(item,EncodeQueueState.Completed,job.ExitCode,string.Empty);
                else Finish(item,EncodeQueueState.Failed,job.ExitCode,finalizeError);
            }
            else if(job.StateCode==TvAirExternalJobState.Cancelled) Finish(item,EncodeQueueState.Stopped,job.ExitCode,string.Empty);
            else
            {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                _diagnostic?.Invoke($"ENCODEBRIDGE_EXTERNAL_FAILURE recordingId={item.Recording.RecordingId} jobId={job.JobId} tool={item.Profile.ToolKind} exitCode={job.ExitCode?.ToString() ?? "-"} failureCode={DiagnosticText(job.FailureCode)} message={DiagnosticText(job.Message)} stderr={DiagnosticText(job.StandardErrorTail)} stdout={DiagnosticText(job.StandardOutputTail)}");
#endif
                Finish(item,EncodeQueueState.Failed,job.ExitCode,BuildJobFailureMessage(item,job));
            }
        }
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
        catch (Exception ex)
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
        catch (Exception ex)
        {
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            _diagnostic?.Invoke($"ENCODEBRIDGE_TMPGENC_PROJECTCREATOR result=FAILED recordingId={item.Recording.RecordingId} exitCode={job.ExitCode?.ToString() ?? "-"} logState=read_error error={ex.GetType().Name}:{ex.Message}");
#endif
            return $"{baseMessage} / TMPGEnc Project Creatorログを読み取れませんでした。";
        }
    }

    private string BuildOutputPath(EncodeQueueItem item){var src=item.Recording.SourceFilePath;var dir=item.Profile.OutputDirectoryMode switch{OutputDirectoryMode.Fixed=>item.Profile.OutputDirectory,OutputDirectoryMode.SourceSubdirectory=>Path.Combine(Path.GetDirectoryName(src)!,item.Profile.SourceSubdirectoryName),_=>Path.GetDirectoryName(src)!};var ext="."+item.Profile.Container.TrimStart('.');return Path.Combine(dir,Path.GetFileNameWithoutExtension(src)+ext);}
    private void Fail(EncodeQueueItem item,string msg,int? exit)=>Finish(item,EncodeQueueState.Failed,exit,msg);
    private void Finish(EncodeQueueItem item,EncodeQueueState state,int? exit,string msg){item.State=state;item.LastError=msg;_store.Queue.Remove(item);_store.History.Add(new EncodeHistoryItem{QueueItemId=item.QueueItemId,RecordingId=item.Recording.RecordingId,Title=item.Recording.Title,ServiceName=item.Recording.ServiceName,ActualStart=item.Recording.ActualStart,SourceFilePath=item.Recording.SourceFilePath,OutputFilePath=item.ExpectedOutputPath,ProfileName=item.Profile.ProfileName,ToolName=item.Profile.ToolName,Result=state,StartedAt=item.StartedAt,FinishedAt=DateTimeOffset.Now,ExitCode=exit,FailureMessage=msg,AttemptNumber=item.AttemptNumber,Recording=item.Recording,Profile=item.Profile});
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        _powerGuard.Release(item.QueueItemId,$"terminal:{state}");
#else
        _powerGuard.Release(item.QueueItemId);
#endif
        try{_adapters.Get(item.Profile.ToolKind).Cleanup(item);}catch{} }
    private void PurgeHistory(){var cutoff=DateTimeOffset.Now.AddDays(-Math.Clamp(_store.Settings.HistoryRetentionDays,1,36500));_store.History.RemoveAll(x=>x.FinishedAt<cutoff);}
}
