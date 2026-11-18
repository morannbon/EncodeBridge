namespace EncodeBridge.Domain;

// Built-in adapters generate tool-specific command lines. A readable file path
// does not prove that the selected encoder understands those arguments.
// Recognize supported executable names at one boundary shared by the editor,
// persisted configurations and queued snapshots. Custom CLI owns its arguments.
internal static class EncodeToolIdentity
{
    public static string? ConfigurationError(EncodeToolKind kind, string? executablePath)
    {
        var path = executablePath?.Trim() ?? string.Empty;
        if (path.Length == 0)
            return "実行ファイルを指定してください。";
        var mismatch = IdentityError(kind, path);
        if (mismatch is not null) return mismatch;
        return File.Exists(path) ? null : "指定した実行ファイルが見つかりません。";
    }

    // No disk access is necessary at dispatch. Incompatible frozen snapshots
    // are permanent configuration errors, not temporary encoder outages.
    public static string? IdentityError(EncodeToolKind kind, string? executablePath)
    {
        if (!Enum.IsDefined(kind))
            return "変換ツールの種類を確認してください。";
        if (kind == EncodeToolKind.Custom || string.IsNullOrWhiteSpace(executablePath))
            return null;

        var name = Path.GetFileNameWithoutExtension(executablePath.Trim());
        var identified = Recognize(name);
        if (identified is null)
            return $"実行ファイル名から{ProfileDisplay.ToolName(kind)}を確認できません。対応する実行ファイルを選択してください。";
        if (identified.Value != kind)
            return $"選択された実行ファイルは{ProfileDisplay.ToolName(identified.Value)}用です。変換ツールを「{ProfileDisplay.ToolName(identified.Value)}」に変更するか、{ProfileDisplay.ToolName(kind)}の実行ファイルを選び直してください。";
        return null;
    }

    private static EncodeToolKind? Recognize(string name)
    {
        if (name.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase)) return EncodeToolKind.Ffmpeg;
        if (name.StartsWith("nvencC", StringComparison.OrdinalIgnoreCase)) return EncodeToolKind.NvEncC;
        if (name.StartsWith("qsvencC", StringComparison.OrdinalIgnoreCase)) return EncodeToolKind.QsvEncC;
        if (name.StartsWith("vceencC", StringComparison.OrdinalIgnoreCase)) return EncodeToolKind.VceEncC;
        if (name.StartsWith("handbrakecli", StringComparison.OrdinalIgnoreCase)) return EncodeToolKind.HandBrake;
        if (name.StartsWith("amatsukaze", StringComparison.OrdinalIgnoreCase)) return EncodeToolKind.Amatsukaze;
        if (name.StartsWith("tmpgenc", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("tvmw", StringComparison.OrdinalIgnoreCase)) return EncodeToolKind.TmpgEnc;
        return null;
    }
}
