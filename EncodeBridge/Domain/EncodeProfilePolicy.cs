using System;
using System.Linq;

namespace EncodeBridge.Domain;

// The default rule belongs only to regular reservations. Keyword reservations
// require their explicitly selected, enabled profile; no default fallback.
internal static class EncodeProfilePolicy
{
    public static EncodeRule? EffectiveDefaultRule(EncodeBridgeStore store) =>
        store.Rules.Where(r => r.Enabled && r.Kind == EncodeRuleKind.Default)
            .OrderBy(r => r.Order).FirstOrDefault();

    public static EncodeProfile? ResolveEnabledById(EncodeBridgeStore store, string? profileId) =>
        store.Profiles.FirstOrDefault(profile => profile.Enabled &&
            string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    public static EncodeTool? ResolveTool(EncodeBridgeStore store, string? toolId) =>
        store.Tools.FirstOrDefault(tool =>
            string.Equals(tool.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    public static EncodeProfile? ResolveDefault(EncodeBridgeStore store)
        => ResolveEnabledById(store, EffectiveDefaultRule(store)?.ProfileId);

    public static EncodeProfile? ResolveForRecording(EncodeBridgeStore store, bool isKeywordReservation, string? selectedProfileId)
        => isKeywordReservation ? ResolveEnabledById(store, selectedProfileId) : ResolveDefault(store);

    public static bool IsAutoConfigurationReady(EncodeBridgeStore store)
    {
        var profile = ResolveDefault(store);
        var tool = ResolveTool(store, profile?.ToolId);
        return tool is not null && EncodeToolIdentity.ConfigurationError(tool.Kind, tool.ExecutablePath) is null;
    }

    public static void EnsureInitialDefault(EncodeBridgeStore store, EncodeProfile profile)
    {
        if (store.Rules.Any(rule => rule.Kind == EncodeRuleKind.Default)) return;
        store.Rules.Add(new EncodeRule
        {
            Order = int.MaxValue, Kind = EncodeRuleKind.Default, ProfileId = profile.ProfileId
        });
    }

    public static void SelectDefault(EncodeBridgeStore store, string profileId)
    {
        var profile = ResolveEnabledById(store, profileId)
            ?? throw new InvalidOperationException("有効な変換プロファイルを選択してください。");
        var tool = ResolveTool(store, profile.ToolId)
            ?? throw new InvalidOperationException("変換ツールが登録されていません。実行ファイルを指定してください。");
        var toolError = EncodeToolIdentity.ConfigurationError(tool.Kind, tool.ExecutablePath);
        if (toolError is not null) throw new InvalidOperationException(toolError);

        // Reuse a disabled default rule before adding a new one. Exactly one
        // enabled default rule owns both the UI selection and automatic admission.
        var rule = EffectiveDefaultRule(store) ?? store.Rules
            .Where(candidate => candidate.Kind == EncodeRuleKind.Default)
            .OrderBy(candidate => candidate.Order).FirstOrDefault();
        if (rule is null)
        {
            rule = new EncodeRule { Order = int.MaxValue, Kind = EncodeRuleKind.Default };
            store.Rules.Add(rule);
        }
        rule.Enabled = true;
        rule.ProfileId = profileId;
        foreach (var other in store.Rules.Where(candidate =>
            candidate.Kind == EncodeRuleKind.Default && !ReferenceEquals(candidate, rule)))
            other.Enabled = false;
    }
}
