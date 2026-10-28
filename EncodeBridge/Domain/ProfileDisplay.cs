using System;
using System.Linq;

namespace EncodeBridge.Domain;

internal static class ProfileDisplay
{
    // The persisted ProfileId is the identity. Labels distinguish otherwise identical presets
    // without exposing file paths or replacing the stable binding identifier.
    public static string Label(EncodeBridgeStore store, EncodeProfile profile)
    {
        string BaseName(EncodeProfile p)
        {
            var tool = store.Tools.FirstOrDefault(t => string.Equals(t.ToolId, p.ToolId, StringComparison.OrdinalIgnoreCase));
            return Name(tool?.Kind ?? EncodeToolKind.Ffmpeg, p.Quality, p.VideoCodec, p.Container);
        }
        var name = BaseName(profile);
        var matching = store.Profiles.Where(p => string.Equals(BaseName(p), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count <= 1) return name;
        var number = matching.FindIndex(p => string.Equals(p.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase)) + 1;
        return number > 0 ? $"{name} ({number})" : name;
    }

    public static string Name(EncodeToolKind kind, QualityPreset quality, VideoCodecMode video, string container)
    {
        var tool = kind switch
        {
            EncodeToolKind.Ffmpeg => "FFmpeg",
            EncodeToolKind.NvEncC => "NVEncC",
            EncodeToolKind.QsvEncC => "QSVEncC",
            EncodeToolKind.VceEncC => "VCEEncC",
            EncodeToolKind.HandBrake => "HandBrakeCLI",
            EncodeToolKind.Amatsukaze => "Amatsukaze",
            EncodeToolKind.TmpgEnc => "TMPGEnc",
            EncodeToolKind.Custom => "その他CLI",
            _ => kind.ToString()
        };
        var qualityName = quality switch
        {
            QualityPreset.High => "高画質",
            QualityPreset.Compact => "少容量",
            _ => "標準"
        };
        var videoName = video switch
        {
            VideoCodecMode.H265 => "H.265",
            VideoCodecMode.H264 => "H.264",
            VideoCodecMode.Av1 => "AV1",
            _ => video.ToString()
        };
        var output = string.IsNullOrWhiteSpace(container) ? "MKV" : container.Trim().ToUpperInvariant();
        return $"{tool}・{qualityName}・{videoName}・{output}";
    }
}
