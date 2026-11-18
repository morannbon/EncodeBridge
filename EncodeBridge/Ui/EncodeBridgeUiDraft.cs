using System;
using System.Collections.Generic;

namespace EncodeBridge;

internal enum ProfileCommitClaim { Accepted, Completed, InProgress, Invalid }

internal readonly record struct ProfileSaveFeedback(string ProfileId, bool Created, bool Published, bool Verified);

internal static class EncodeBridgeUiDraft
{
    private static readonly object Gate = new();
    private sealed class WindowDraft
    {
        public Dictionary<string, string> Tool { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Profile { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool PostBatchConsentPending { get; set; }
        public string ValidationError { get; set; } = string.Empty;
        public string ActiveCommitToken { get; set; } = string.Empty;
        public string ProcessingCommitToken { get; set; } = string.Empty;
        public string CompletedCommitToken { get; set; } = string.Empty;
        public ProfileSaveFeedback? SaveFeedback { get; set; }
    }

    private static readonly Dictionary<string, WindowDraft> ByWindow = new(StringComparer.Ordinal);

    private static WindowDraft GetWindow(string windowId, bool create)
    {
        var key = windowId ?? string.Empty;
        if (ByWindow.TryGetValue(key, out var draft)) return draft;
        if (!create) return new WindowDraft();
        draft = new WindowDraft();
        ByWindow[key] = draft;
        return draft;
    }

    public static string GetValidationError(string windowId)
    {
        lock (Gate) return GetWindow(windowId, false).ValidationError;
    }

    public static void SetValidationError(string windowId, string message)
    {
        lock (Gate) GetWindow(windowId, true).ValidationError = message;
    }

    public static void ClearValidationError(string windowId)
    {
        lock (Gate)
        {
            var draft = GetWindow(windowId, false);
            draft.ValidationError = string.Empty;
            Cleanup(windowId, draft);
        }
    }

    // A form-instance token stays stable across picker/navigation round trips.
    // Only an explicit new/edit action issues a new token. Replayed POSTs cannot
    // create a second profile even if the Host redirects after a successful save.
    public static string GetProfileCommitToken(string windowId)
    {
        lock (Gate) return GetWindow(windowId, false).ActiveCommitToken;
    }

    public static ProfileCommitClaim CheckProfileCommit(string windowId, string token)
    {
        lock (Gate)
        {
            var draft = GetWindow(windowId, false);
            if (string.IsNullOrWhiteSpace(token)) return ProfileCommitClaim.Invalid;
            if (string.Equals(draft.CompletedCommitToken, token, StringComparison.Ordinal))
                return ProfileCommitClaim.Completed;
            if (string.Equals(draft.ProcessingCommitToken, token, StringComparison.Ordinal))
                return ProfileCommitClaim.InProgress;
            return string.Equals(draft.ActiveCommitToken, token, StringComparison.Ordinal)
                ? ProfileCommitClaim.Accepted : ProfileCommitClaim.Invalid;
        }
    }

    public static ProfileCommitClaim ClaimProfileCommit(string windowId, string token)
    {
        lock (Gate)
        {
            var draft = GetWindow(windowId, false);
            if (string.IsNullOrWhiteSpace(token)) return ProfileCommitClaim.Invalid;
            if (string.Equals(draft.CompletedCommitToken, token, StringComparison.Ordinal))
                return ProfileCommitClaim.Completed;
            if (string.Equals(draft.ProcessingCommitToken, token, StringComparison.Ordinal))
                return ProfileCommitClaim.InProgress;
            if (!string.Equals(draft.ActiveCommitToken, token, StringComparison.Ordinal))
                return ProfileCommitClaim.Invalid;
            draft.ProcessingCommitToken = token;
            return ProfileCommitClaim.Accepted;
        }
    }

    public static void AbortProfileCommit(string windowId, string token)
    {
        lock (Gate)
        {
            var draft = GetWindow(windowId, false);
            if (string.Equals(draft.ProcessingCommitToken, token, StringComparison.Ordinal))
                draft.ProcessingCommitToken = string.Empty;
        }
    }

    public static void CompleteProfileCommit(string windowId, string token, string profileId, bool created, bool verified)
    {
        lock (Gate)
        {
            var draft = GetWindow(windowId, true);
            if (!string.Equals(draft.ProcessingCommitToken, token, StringComparison.Ordinal))
                throw new InvalidOperationException("プロファイルの保存操作を確認できませんでした。");
            draft.ProcessingCommitToken = string.Empty;
            draft.CompletedCommitToken = token;
            draft.SaveFeedback = new ProfileSaveFeedback(profileId, created, Published: true, Verified: verified);
            // A later new/edit action might already have issued a new form token.
            // Do not discard that newer user's draft.
            if (string.Equals(draft.ActiveCommitToken, token, StringComparison.Ordinal))
            {
                draft.ActiveCommitToken = string.Empty;
                draft.Profile.Clear();
                draft.Tool.Clear();
                draft.PostBatchConsentPending = false;
                draft.ValidationError = string.Empty;
            }
        }
    }

    public static void MarkProfilePublishFailure(string windowId, string profileId)
    {
        lock (Gate)
        {
            var draft = GetWindow(windowId, false);
            if (draft.SaveFeedback is { } feedback &&
                string.Equals(feedback.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                draft.SaveFeedback = feedback with { Published = false };
        }
    }

    public static ProfileSaveFeedback? GetProfileSaveFeedback(string windowId)
    {
        lock (Gate) return GetWindow(windowId, false).SaveFeedback;
    }

    public static void ClearProfileSaveFeedback(string windowId)
    {
        lock (Gate)
        {
            var draft = GetWindow(windowId, false);
            draft.SaveFeedback = null;
            Cleanup(windowId, draft);
        }
    }

    public static Dictionary<string, string> SnapshotTool(string windowId)
    {
        lock (Gate) return new Dictionary<string, string>(GetWindow(windowId, false).Tool, StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, string> SnapshotProfile(string windowId)
    {
        lock (Gate) return new Dictionary<string, string>(GetWindow(windowId, false).Profile, StringComparer.OrdinalIgnoreCase);
    }

    public static void CaptureTool(string windowId, IReadOnlyDictionary<string, string> payload)
    {
        lock (Gate) Capture(GetWindow(windowId, true).Tool, payload);
    }

    public static void CaptureProfile(string windowId, IReadOnlyDictionary<string, string> payload)
    {
        lock (Gate)
        {
            var profile = GetWindow(windowId, true).Profile;
            Capture(profile, payload);
            profile["postEncodeBatchEnabled"] = IsChecked(payload, "postEncodeBatchEnabled") ? "true" : "false";
        }
    }

    private static bool IsChecked(IReadOnlyDictionary<string, string> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value)) return false;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPostBatchConsentPending(string windowId)
    {
        lock (Gate) return GetWindow(windowId, false).PostBatchConsentPending;
    }

    public static void SetPostBatchConsentPending(string windowId, bool pending)
    {
        lock (Gate) GetWindow(windowId, true).PostBatchConsentPending = pending;
    }

    public static void SetPostBatchEnabled(string windowId, bool enabled)
    {
        lock (Gate) GetWindow(windowId, true).Profile["postEncodeBatchEnabled"] = enabled ? "true" : "false";
    }

    public static void SetToolKind(string windowId, string kind)
    {
        lock (Gate) GetWindow(windowId, true).Tool["kind"] = kind;
    }

    public static void SetToolPath(string windowId, string path)
    {
        lock (Gate) GetWindow(windowId, true).Tool["path"] = path?.Trim() ?? string.Empty;
    }

    public static void SetProfileOutput(string windowId, string path)
    {
        lock (Gate)
        {
            var profile = GetWindow(windowId, true).Profile;
            profile["outputDir"] = path?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(path)) profile["outputMode"] = "Fixed";
        }
    }

    public static void SetProfileTool(string windowId, string toolId)
    {
        lock (Gate) GetWindow(windowId, true).Profile["toolId"] = toolId?.Trim() ?? string.Empty;
    }

    public static void SetProfile(string windowId, IReadOnlyDictionary<string, string> values)
    {
        lock (Gate)
        {
            var profile = GetWindow(windowId, true).Profile;
            profile.Clear();
            foreach (var pair in values) profile[pair.Key] = pair.Value ?? string.Empty;
            var draft = GetWindow(windowId, true);
            draft.ActiveCommitToken = Guid.NewGuid().ToString("N");
            draft.SaveFeedback = null;
        }
    }

    public static void ClearTool(string windowId)
    {
        lock (Gate)
        {
            var draft = GetWindow(windowId, false);
            draft.Tool.Clear();
            Cleanup(windowId, draft);
        }
    }

    public static void ClearProfile(string windowId)
    {
        lock (Gate)
        {
            var draft = GetWindow(windowId, false);
            draft.Profile.Clear();
            draft.ActiveCommitToken = string.Empty;
            draft.PostBatchConsentPending = false;
            draft.ValidationError = string.Empty;
            Cleanup(windowId, draft);
        }
    }

    private static void Cleanup(string windowId, WindowDraft draft)
    {
        if (draft.Tool.Count == 0 && draft.Profile.Count == 0 && !draft.PostBatchConsentPending && string.IsNullOrEmpty(draft.ValidationError)
            && string.IsNullOrEmpty(draft.ActiveCommitToken) && string.IsNullOrEmpty(draft.ProcessingCommitToken)
            && string.IsNullOrEmpty(draft.CompletedCommitToken) && draft.SaveFeedback is null)
            ByWindow.Remove(windowId ?? string.Empty);
    }

    private static void Capture(Dictionary<string, string> target, IReadOnlyDictionary<string, string> payload)
    {
        foreach (var pair in payload)
        {
            if (IsHostKey(pair.Key)) continue;
            var key = NormalizeFieldName(pair.Key);
            if (string.IsNullOrWhiteSpace(key)) continue;
            target[key] = pair.Value;
        }
    }

    private static bool IsHostKey(string key)
    {
        var k = key ?? string.Empty;
        return k.Equals("command", StringComparison.OrdinalIgnoreCase)
            || k.Equals("action", StringComparison.OrdinalIgnoreCase)
            || k.Equals("pluginId", StringComparison.OrdinalIgnoreCase)
            || k.Equals("route", StringComparison.OrdinalIgnoreCase)
            || k.Equals("routeSegment", StringComparison.OrdinalIgnoreCase)
            || k.Equals("actionToken", StringComparison.OrdinalIgnoreCase)
            || k.Equals("windowId", StringComparison.OrdinalIgnoreCase)
            || k.Equals("responseMode", StringComparison.OrdinalIgnoreCase)
            || k.Equals("safeEvent", StringComparison.OrdinalIgnoreCase)
            || k.Equals("safeEventAction", StringComparison.OrdinalIgnoreCase)
            || k.Equals("safeEventInteractionId", StringComparison.OrdinalIgnoreCase)
            || k.Equals("safeEventSource", StringComparison.OrdinalIgnoreCase)
            || k.Equals("refresh-target", StringComparison.OrdinalIgnoreCase)
            || k.Equals("refreshTarget", StringComparison.OrdinalIgnoreCase)
            || k.Equals("preserve-scroll", StringComparison.OrdinalIgnoreCase)
            || k.Equals("preserveScroll", StringComparison.OrdinalIgnoreCase)
            || k.Equals("feedbackRequested", StringComparison.OrdinalIgnoreCase)
            || k.Equals("commitToken", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFieldName(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        var k = key.Trim();
        foreach (var prefix in new[] { "tool-form.", "tool-form:", "tool-form[", "profile-form.", "profile-form:", "profile-form[", "form.", "form:", "form[" })
        {
            if (!k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            k = k[prefix.Length..];
            if (k.EndsWith("]", StringComparison.Ordinal)) k = k[..^1];
            break;
        }
        return k;
    }
}
