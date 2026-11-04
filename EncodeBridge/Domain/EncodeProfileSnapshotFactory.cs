using System;

namespace EncodeBridge.Domain;

// Queued jobs retain a frozen copy of the profile at their own acceptance boundary.
// This is the single path for journal admission and explicit retry.
internal static class EncodeProfileSnapshotFactory
{
    public static EncodeProfileSnapshot Create(EncodeBridgeStore store, EncodeProfile profile, EncodeTool tool)
    {
        var snapshot = new EncodeProfileSnapshot
        {
            ProfileId = profile.ProfileId,
            ProfileName = ProfileDisplay.Label(store, profile),
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
            AdditionalArguments = new(profile.AdditionalArguments),
            PostEncodeBatch = profile.PostEncodeBatch,
            PostEncodeBatchEnabled = profile.PostEncodeBatchEnabled && profile.PostEncodeBatchRiskAccepted,
            PostEncodeBatchRiskAccepted = profile.PostEncodeBatchRiskAccepted
        };
        foreach (var setting in tool.Settings)
            snapshot.AdapterSettings.TryAdd(setting.Key, setting.Value);
        return snapshot;
    }
}
