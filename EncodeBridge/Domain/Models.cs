using TvAIrPlugin;

namespace EncodeBridge.Domain;

internal static class EncodeBridgeDefaults
{
    public const int SchemaVersion = 2;
    public const bool AutoEncodeEnabled = false;
    public const int MaxConcurrentEncodes = 1;
    public const int MinConcurrentEncodes = 1;
    public const int MaxConcurrentEncodesLimit = 4;
    public const bool PauseStartWhileRecording = true;
    public const bool PauseStartWhileEpg = true;
    public const int MinimumFreeSpaceGb = 10;
    public const int MinFreeSpaceGb = 1;
    public const int HistoryRetentionDays = 30;
    public const int MinHistoryRetentionDays = 1;
    public const int MaxHistoryRetentionDays = 36500;
    public const EncodeToolKind DefaultToolKind = EncodeToolKind.Ffmpeg;
    public const VideoCodecMode DefaultVideoCodec = VideoCodecMode.H265;
    public const QualityPreset DefaultQuality = QualityPreset.Standard;
    public const string DefaultContainer = "mkv";
    public const string DefaultSourceSubdirectory = "Encoded";
    public const long MinRecordingBytes = 1024L * 1024L;
    public const int MaxPostEncodeBatchLength = 32768;
    public const string UnverifiedResultMessage = "変換の結果を確認できません。変換後ファイルを確認してください。";
}

internal enum EncodeToolKind { Ffmpeg, NvEncC, QsvEncC, VceEncC, HandBrake, Amatsukaze, TmpgEnc, Custom }
// Numeric enum values are part of the persisted queue/history format.
internal enum EncodeQueueState { Waiting, Running, HandedOff, Completed, Failed, Stopped, Unverified }

// The result of a user's queue command is separate from the eventual encoder result.
// In particular, accepting a retry into the queue does not mean an encoder has started.
internal enum EncodeQueueCommandState { Rejected, Started, Waiting, StopRequested, Removed, Completed, HandedOff }

internal readonly record struct EncodeQueueCommandResult(EncodeQueueCommandState State, string Message)
{
    // A waiting retry was accepted into a durable queue. It is a successful
    // command, not a claim that an encoder process has started.
    public bool Succeeded => State is EncodeQueueCommandState.Started or EncodeQueueCommandState.Waiting
        or EncodeQueueCommandState.StopRequested or EncodeQueueCommandState.Removed
        or EncodeQueueCommandState.Completed or EncodeQueueCommandState.HandedOff;

    public static EncodeQueueCommandResult Rejected(string message)
        => new(EncodeQueueCommandState.Rejected, message);
    public static EncodeQueueCommandResult Started()
        => new(EncodeQueueCommandState.Started, "変換を開始しました。");
    public static EncodeQueueCommandResult Waiting(string? reason)
        => new(EncodeQueueCommandState.Waiting,
            string.IsNullOrWhiteSpace(reason)
                ? "変換待ちに登録しました。"
                : $"変換待ちに登録しました。{reason}");
    public static EncodeQueueCommandResult Completed()
        => new(EncodeQueueCommandState.Completed, "変換が完了しました。");
    public static EncodeQueueCommandResult HandedOff()
        => new(EncodeQueueCommandState.HandedOff, "変換アプリへ引き渡しました。");
    public static EncodeQueueCommandResult StopRequested()
        => new(EncodeQueueCommandState.StopRequested, "停止を要求しました。");
    public static EncodeQueueCommandResult Removed()
        => new(EncodeQueueCommandState.Removed, "待機中の変換を削除しました。");
}
internal enum EncodeRuleKind { TitleContains, Channel, Genre, Default }
internal enum OutputDirectoryMode { SameAsSource, Fixed, SourceSubdirectory }
internal enum AudioTrackMode { All, MainOnly }
internal enum AudioCodecMode { PassthroughIfPossible, Aac, Opus }
internal enum SubtitleMode { KeepIfPossible, Drop, BurnIn }
internal enum VideoCodecMode { H264, H265, Av1 }
internal enum QualityPreset { High, Standard, Compact }

internal sealed class EncodeBridgeSettings
{
    public bool AutoEncodeEnabled { get; set; } = EncodeBridgeDefaults.AutoEncodeEnabled;
    public bool PostEncodeBatchRiskAccepted { get; set; }
    public int MaxConcurrentEncodes { get; set; } = EncodeBridgeDefaults.MaxConcurrentEncodes;
    public bool PauseStartWhileRecording { get; set; } = EncodeBridgeDefaults.PauseStartWhileRecording;
    public bool PauseStartWhileEpg { get; set; } = EncodeBridgeDefaults.PauseStartWhileEpg;
    public int MinimumFreeSpaceGb { get; set; } = EncodeBridgeDefaults.MinimumFreeSpaceGb;
    public int HistoryRetentionDays { get; set; } = EncodeBridgeDefaults.HistoryRetentionDays;
}

internal sealed class EncodeTool
{
    public string ToolId { get; set; } = Guid.NewGuid().ToString("N");
    public EncodeToolKind Kind { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class EncodeProfile
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public VideoCodecMode VideoCodec { get; set; } = EncodeBridgeDefaults.DefaultVideoCodec;
    public QualityPreset Quality { get; set; } = EncodeBridgeDefaults.DefaultQuality;
    public AudioTrackMode AudioTracks { get; set; } = AudioTrackMode.All;
    public AudioCodecMode AudioCodec { get; set; } = AudioCodecMode.PassthroughIfPossible;
    public SubtitleMode Subtitles { get; set; } = SubtitleMode.KeepIfPossible;
    public string Container { get; set; } = EncodeBridgeDefaults.DefaultContainer;
    public OutputDirectoryMode OutputDirectoryMode { get; set; } = OutputDirectoryMode.SameAsSource;
    public string OutputDirectory { get; set; } = string.Empty;
    public bool OutputDirectoryChoiceConfirmed { get; set; }
    public string SourceSubdirectoryName { get; set; } = EncodeBridgeDefaults.DefaultSourceSubdirectory;
    public Dictionary<string, string> AdapterSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AdditionalArguments { get; set; } = new();
    public string PostEncodeBatch { get; set; } = string.Empty;
    public bool PostEncodeBatchEnabled { get; set; }
    public bool PostEncodeBatchRiskAccepted { get; set; }
}

internal sealed class EncodeRule
{
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public EncodeRuleKind Kind { get; set; }
    public string MatchValue { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
}

internal sealed class RecordingSnapshot
{
    public string RecordingId { get; set; } = string.Empty;
    public string RecordingFileId { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public string SourceFilePath { get; set; } = string.Empty;
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string GenreCodes { get; set; } = string.Empty;
    public DateTimeOffset ScheduledStart { get; set; }
    public DateTimeOffset? ActualStart { get; set; }
    public DateTimeOffset? ActualEnd { get; set; }
}

internal sealed class EncodeProfileSnapshot
{
    public string ProfileId { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public EncodeToolKind ToolKind { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public VideoCodecMode VideoCodec { get; set; }
    public QualityPreset Quality { get; set; }
    public AudioTrackMode AudioTracks { get; set; }
    public AudioCodecMode AudioCodec { get; set; }
    public SubtitleMode Subtitles { get; set; }
    public string Container { get; set; } = EncodeBridgeDefaults.DefaultContainer;
    public OutputDirectoryMode OutputDirectoryMode { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public string SourceSubdirectoryName { get; set; } = EncodeBridgeDefaults.DefaultSourceSubdirectory;
    public Dictionary<string, string> AdapterSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AdditionalArguments { get; set; } = new();
    public string PostEncodeBatch { get; set; } = string.Empty;
    public bool PostEncodeBatchEnabled { get; set; }
    public bool PostEncodeBatchRiskAccepted { get; set; }
}

internal sealed class EncodeQueueItem
{
    public string QueueItemId { get; set; } = Guid.NewGuid().ToString("N");
    public RecordingSnapshot Recording { get; set; } = new();
    public EncodeProfileSnapshot Profile { get; set; } = new();
    public EncodeQueueState State { get; set; } = EncodeQueueState.Waiting;
    public int AttemptNumber { get; set; } = 1;
    public string ClientRequestId { get; set; } = string.Empty;
    public string ExternalJobId { get; set; } = string.Empty;
    public string ExpectedOutputPath { get; set; } = string.Empty;
    public string WaitingReason { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public string LastError { get; set; } = string.Empty;
    public string? PreparedArtifactPath { get; set; }
    public bool HandoffOnly { get; set; }
    // Persisted at the output-file commit boundary, before post-processing.
    // Never infer completion merely from a same-named pre-existing MP4.
    public bool OutputFinalized { get; set; }
    public bool PostEncodeBatchRunning { get; set; }
    public string PostEncodeBatchFilePath { get; set; } = string.Empty;
}

internal sealed class EncodeHistoryItem
{
    public string QueueItemId { get; set; } = string.Empty;
    public string RecordingId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public DateTimeOffset? ActualStart { get; set; }
    public string SourceFilePath { get; set; } = string.Empty;
    public string OutputFilePath { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public EncodeQueueState Result { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public int? ExitCode { get; set; }
    public string FailureMessage { get; set; } = string.Empty;
    public int AttemptNumber { get; set; } = 1;
    public RecordingSnapshot Recording { get; set; } = new();
    public EncodeProfileSnapshot Profile { get; set; } = new();
}

internal sealed class EncodeBridgeStore
{
    public int SchemaVersion { get; set; } = EncodeBridgeDefaults.SchemaVersion;
    public EncodeBridgeSettings Settings { get; set; } = new();
    public List<EncodeTool> Tools { get; set; } = new();
    public List<EncodeProfile> Profiles { get; set; } = new();
    public List<EncodeRule> Rules { get; set; } = new();
    public List<EncodeQueueItem> Queue { get; set; } = new();
    public List<EncodeHistoryItem> History { get; set; } = new();
    public HashSet<string> ProcessedRecordingFileIds { get; set; } = new(StringComparer.Ordinal);
    public List<TvAirFinalizedRecordingFileDto> PendingFinalizedFiles { get; set; } = new();
    public string? FinalizedJournalId { get; set; }
    public long FinalizedNextSequence { get; set; }
    public string? FinalizedNextRecordingFileId { get; set; }
}

internal sealed record ExternalProcessPlan(string ExecutablePath, IReadOnlyList<string> Arguments, string? WorkingDirectory, string ExpectedOutputPath, bool HandoffOnly, string? PreparedArtifactPath = null);
internal sealed record AdapterCheck(bool Succeeded, string Message)
{
    public static AdapterCheck Ok() => new(true, string.Empty);
    public static AdapterCheck Fail(string message) => new(false, message);
}

internal sealed class TsServiceInfo
{
    public ushort ServiceId { get; init; }
    public ushort PmtPid { get; init; }
    public IReadOnlyList<ushort> VideoPids { get; init; } = Array.Empty<ushort>();
    public IReadOnlyList<ushort> AudioPids { get; init; } = Array.Empty<ushort>();
    public IReadOnlyList<ushort> CaptionPids { get; init; } = Array.Empty<ushort>();
    public int ProgramCount { get; init; }
}
