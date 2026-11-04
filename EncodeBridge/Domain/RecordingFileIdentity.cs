namespace EncodeBridge.Domain;

// Recording-file identity is the physical path or the Host's stable file ID.
// Both must be checked: the same source can appear with different journal IDs.
internal static class RecordingFileIdentity
{
    public static bool SameSourcePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool SameRecording(RecordingSnapshot left, RecordingSnapshot right) =>
        SameSourcePath(left.SourceFilePath, right.SourceFilePath) ||
        (!string.IsNullOrWhiteSpace(left.RecordingFileId) &&
         !string.IsNullOrWhiteSpace(right.RecordingFileId) &&
         string.Equals(left.RecordingFileId, right.RecordingFileId, StringComparison.OrdinalIgnoreCase));
}
