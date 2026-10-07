using EncodeBridge.Domain;

namespace EncodeBridge.Adapters;

internal sealed class FfmpegAdapter : EncodeAdapterBase
{
    public override EncodeToolKind Kind => EncodeToolKind.Ffmpeg;
    public override ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo s)
    {
        var a = new List<string>{"-hide_banner","-nostdin","-n","-i",item.Recording.SourceFilePath,
            "-map",$"0:p:{item.Recording.ServiceId}:v:0","-map",$"0:p:{item.Recording.ServiceId}:a?"};
        var canCopySubtitles = item.Profile.Subtitles != SubtitleMode.Drop
            && !string.Equals(item.Profile.Container, "mp4", StringComparison.OrdinalIgnoreCase);
        if (canCopySubtitles) { a.Add("-map"); a.Add($"0:p:{item.Recording.ServiceId}:s?"); }
        a.AddRange(new[]{"-c:v", FfmpegCodec(item.Profile), "-crf", Quality(item.Profile).ToString(System.Globalization.CultureInfo.InvariantCulture)});
        if (item.Profile.AudioCodec == AudioCodecMode.PassthroughIfPossible) a.AddRange(new[]{"-c:a","copy"});
        else a.AddRange(new[]{"-c:a", item.Profile.AudioCodec == AudioCodecMode.Opus ? "libopus" : "aac"});
        if (canCopySubtitles) a.AddRange(new[]{"-c:s","copy"});
        a.AddRange(new[]{"-metadata",$"title={item.Recording.Title}","-metadata",$"comment={item.Recording.ServiceName} {item.Recording.ScheduledStart:yyyy-MM-dd HH:mm}"});
        a.AddRange(item.Profile.AdditionalArguments);
        a.Add(Output(item));
        return new(item.Profile.ExecutablePath,a,Path.GetDirectoryName(item.Profile.ExecutablePath),Output(item),false);
    }
}

internal abstract class RigayaAdapterBase : EncodeAdapterBase
{
    public override AdapterCheck Preflight(EncodeQueueItem item, TsServiceInfo s)
    {
        if (s.ProgramCount > 1)
            return AdapterCheck.Fail("この録画ファイルでは対象番組を安全に選択できません。");
        return AdapterCheck.Ok();
    }

    protected ExternalProcessPlan BuildRigaya(EncodeQueueItem item, TsServiceInfo s, string qualityOption)
    {
        var finalOutput = Output(item);
        var outputDirectory = Path.GetDirectoryName(finalOutput) ?? string.Empty;
        var extension = Path.GetExtension(finalOutput);
        var stagingOutput = Path.Combine(outputDirectory, $".encodebridge-{item.QueueItemId}-{item.AttemptNumber}{extension}");
        var args = new List<string>{"-i",item.Recording.SourceFilePath,"-o",stagingOutput,"--codec",Codec(item.Profile),qualityOption,Quality(item.Profile).ToString(System.Globalization.CultureInfo.InvariantCulture)};
        if (s.VideoPids.Count > 0) args.AddRange(new[]{"--video-streamid", s.VideoPids[0].ToString(System.Globalization.CultureInfo.InvariantCulture)});
        if (item.Profile.AudioTracks == AudioTrackMode.All && item.Profile.AudioCodec == AudioCodecMode.PassthroughIfPossible) args.Add("--audio-copy");
        else if (item.Profile.AudioCodec == AudioCodecMode.Aac) args.AddRange(new[]{"--audio-codec","aac"});
        else if (item.Profile.AudioCodec == AudioCodecMode.Opus) args.AddRange(new[]{"--audio-codec","opus"});
        if (item.Profile.Subtitles != SubtitleMode.Drop && !string.Equals(item.Profile.Container, "mp4", StringComparison.OrdinalIgnoreCase)) args.Add("--sub-copy");
        args.AddRange(new[]{"--metadata",$"title={item.Recording.Title}"});
        args.AddRange(item.Profile.AdditionalArguments);
        return new(item.Profile.ExecutablePath,args,Path.GetDirectoryName(item.Profile.ExecutablePath),finalOutput,false,stagingOutput);
    }
}

internal sealed class NvEncAdapter : RigayaAdapterBase
{
    public override EncodeToolKind Kind => EncodeToolKind.NvEncC;
    public override ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo serviceInfo) => BuildRigaya(item,serviceInfo,"--qvbr");
}
internal sealed class QsvEncAdapter : RigayaAdapterBase
{
    public override EncodeToolKind Kind => EncodeToolKind.QsvEncC;
    public override ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo serviceInfo) => BuildRigaya(item,serviceInfo,"--icq");
}
internal sealed class VceEncAdapter : RigayaAdapterBase
{
    public override EncodeToolKind Kind => EncodeToolKind.VceEncC;
    public override ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo serviceInfo) => BuildRigaya(item,serviceInfo,"--qvbr");
}

internal sealed class HandBrakeAdapter : EncodeAdapterBase
{
    public override EncodeToolKind Kind => EncodeToolKind.HandBrake;
    public override AdapterCheck Preflight(EncodeQueueItem item, TsServiceInfo s)
        => s.ProgramCount > 1 ? AdapterCheck.Fail("この録画ファイルでは対象番組を安全に選択できません。") : AdapterCheck.Ok();

    public override ExternalProcessPlan BuildProcess(EncodeQueueItem item, TsServiceInfo s)
    {
        var a = new List<string>
        {
            "-i", item.Recording.SourceFilePath,
            "-o", Output(item),
            "--encoder", HandBrakeEncoder(item.Profile),
            "--quality", Quality(item.Profile).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--vfr",
            "--crop-mode", "none"
        };

        if (item.Profile.AudioTracks == AudioTrackMode.All)
            a.Add("--all-audio");

        if (item.Profile.AudioCodec == AudioCodecMode.PassthroughIfPossible)
        {
            a.AddRange(new[]
            {
                "--aencoder", "copy",
                "--audio-copy-mask", "aac,ac3,eac3,mp2,mp3,opus",
                "--audio-fallback", "av_aac"
            });
        }
        else
        {
            a.AddRange(new[] { "--aencoder", item.Profile.AudioCodec == AudioCodecMode.Opus ? "opus" : "av_aac" });
        }

        a.AddRange(item.Profile.AdditionalArguments);
        return new(item.Profile.ExecutablePath, a, Path.GetDirectoryName(item.Profile.ExecutablePath), Output(item), false);
    }

    private static string HandBrakeEncoder(EncodeProfileSnapshot profile) => profile.VideoCodec switch
    {
        VideoCodecMode.H264 => "x264",
        VideoCodecMode.Av1 => "svt_av1",
        _ => "x265"
    };
}
