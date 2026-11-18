using EncodeBridge.Domain;

namespace EncodeBridge.Adapters;

internal interface IEncodeAdapter
{
    EncodeToolKind Kind { get; }
    AdapterCheck ValidateTool(EncodeProfileSnapshot profile);
    AdapterCheck Preflight(EncodeQueueItem item, TsServiceInfo serviceInfo);
    ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo serviceInfo);
    void Cleanup(EncodeQueueItem item);
}

internal abstract class EncodeAdapterBase : IEncodeAdapter
{
    public abstract EncodeToolKind Kind { get; }
    public virtual AdapterCheck ValidateTool(EncodeProfileSnapshot profile)
    {
        if (profile.ToolKind != Kind)
            return AdapterCheck.Fail("変換ツールと変換設定の種類が一致しません。");
        var error = EncodeToolIdentity.ConfigurationError(profile.ToolKind, profile.ExecutablePath);
        return error is null ? AdapterCheck.Ok() : AdapterCheck.Fail(error);
    }
    public virtual AdapterCheck Preflight(EncodeQueueItem item, TsServiceInfo serviceInfo) => AdapterCheck.Ok();
    public abstract ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo serviceInfo);
    public virtual void Cleanup(EncodeQueueItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.PreparedArtifactPath))
        {
            try { if (File.Exists(item.PreparedArtifactPath)) File.Delete(item.PreparedArtifactPath); } catch { }
        }
    }

    protected static string Output(EncodeQueueItem item) => item.ExpectedOutputPath;
    protected static string Codec(EncodeProfileSnapshot p) => p.VideoCodec switch { VideoCodecMode.H264 => "h264", VideoCodecMode.Av1 => "av1", _ => "hevc" };
    protected static string FfmpegCodec(EncodeProfileSnapshot p) => p.VideoCodec switch { VideoCodecMode.H264 => "libx264", VideoCodecMode.Av1 => "libsvtav1", _ => "libx265" };
    protected static int Quality(EncodeProfileSnapshot p) => p.Quality switch { QualityPreset.High => 19, QualityPreset.Compact => 27, _ => 23 };
}
