using System.Net;
using EncodeBridge.Domain;
using EncodeBridge.Runtime;
using TvAIrPlugin;
using TvAIrPlugin.Pickers;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Windows;

namespace EncodeBridge;

public sealed class EncodeBridgePlugin : ITvAirRuntimeCapabilityPlugin, ITvAirRuntimeUiPlugin, ITvAirRuntimeLifecyclePlugin
{
    private const string DraftToolId = "__draft_tool__";
    private const string PluginId = "encodebridge";
    private const string MainUiId = "encodebridge.main";
    private const string WindowId = "encodebridge.window";
    private EncodeBridgeRuntime? _runtime;

    public TvAirPluginRuntimeDescriptor Descriptor { get; } = new()
    {
        PluginId = PluginId,
        DisplayName = "EncodeBridge",
        Version = "1.0.1",
        SdkContractVersion = "1.1.16",
        RequiredPermissions = BuildRequiredPermissions(),
        Windows = new[]
        {
            new PluginWindowDefinition
            {
                WindowDefinitionId = WindowId,
                Title = "EncodeBridge v1.0.1",
                Multiplicity = PluginWindowMultiplicity.Single,
                InitialSize = new PluginWindowSize(820,720),
                MinimumSize = new PluginWindowSize(620,520),
                SizeReference = PluginWindowSizeReference.ContentArea,
                RefreshMode = PluginWindowRefreshMode.Rerender,
                CloseBehavior = PluginWindowCloseBehavior.Hide,
                BackgroundExecution = PluginWindowBackgroundExecution.Continue,
                StatePersistence = PluginWindowStatePersistence.PlacementAndUiState
            }
        },
        Runtimes = new[]
        {
            new PluginRuntimeDefinition
            {
                RuntimeDefinitionId = "encodebridge.runtime",
                StartAutomatically = true,
                KeepAliveWithoutSurface = true
            }
        },
        MenuActions = new[]
        {
            new PluginMenuActionDefinition
            {
                ActionId = "encodebridge.open",
                Label = "EncodeBridge",
                Kind = PluginMenuActionKind.ToolWindow,
                Route = "encodebridge",
                WindowDefinitionId = WindowId,
                Priority = 100
            }
        },
        UiDefinitions = new[]
        {
            new RuntimeUiDefinition
            {
                UiDefinitionId = MainUiId,
                Route = "encodebridge",
                Kind = RuntimeUiKind.ToolWindow,
                WindowDefinitionId = WindowId,
                SupportedActions = new[]
                {
                    "toggle-auto", "save-basic", "pick-tool-path", "pick-output-dir",
                    "add-tool", "remove-tool", "new-profile", "add-profile", "edit-profile", "save-profile", "confirm-post-batch-save", "cancel-post-batch-save", "cancel-edit-profile", "remove-profile", "set-default-profile",
                    "remove-waiting", "stop", "retry", "delete-history", "remove-history-item", "confirm-delete-history", "cancel-delete-history"
                },
                ThemeUpdateMode = RuntimeUiThemeUpdateMode.HostRerender
            }
        },
        Lifecycle = new PluginLifecycleDefinition
        {
            StartAutomatically = true,
            StopOnHostShutdown = true
        }
    };

    private static PluginPermission[] BuildRequiredPermissions()
    {
        var permissions = new List<PluginPermission>
        {
            PluginPermission.ShowUi,
            PluginPermission.OpenToolWindow,
            PluginPermission.UseWindowApi,
            PluginPermission.UseActionApi,
            PluginPermission.UseSafeEvent,
            PluginPermission.UsePowerInhibition,
            PluginPermission.UsePathPicker,
            PluginPermission.UsePluginStorage,
            PluginPermission.ReadPluginStorage,
            PluginPermission.WritePluginStorage,
            PluginPermission.ReadRecordingStatus,
            PluginPermission.ReadRecordingHistory,
            PluginPermission.ReadReservations,
            PluginPermission.ReadSystemStatus,
            PluginPermission.ReadHostContracts,
            PluginPermission.ReadEpgStatus,
            PluginPermission.LaunchExternalProcess
        };
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        permissions.Add(PluginPermission.WriteLogs);
#endif
        return permissions.ToArray();
    }

    public void Initialize(ITvAirPluginRuntimeContext context) => _runtime = new EncodeBridgeRuntime(context);
    public void OnStart() => _runtime?.Start();
    public void OnStop() => _runtime?.Dispose();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> PendingHistoryClear = new(StringComparer.Ordinal);

    public string RenderHtml(RuntimeUiRenderContext context)
    {
        var rt = _runtime;
        if (rt is null)
            return "<div>EncodeBridgeを開始できません。</div>";

        rt.RegisterUiWindow(context.CurrentWindowId);
        var s = rt.Store;
        var waiting = s.Queue.Count(x => x.State == EncodeQueueState.Waiting);
        var running = s.Queue.Count(x => x.State == EncodeQueueState.Running);
        var handed = s.History.Count(x => x.Result == EncodeQueueState.HandedOff);
        var completed = s.History.Count(x => x.Result == EncodeQueueState.Completed);
        var failed = s.History.Count(x => x.Result == EncodeQueueState.Failed);
        var configured = IsAutoConfigurationReady(s);

        var toolDraft = EncodeBridgeUiDraft.SnapshotTool(context.CurrentWindowId);
        var profileDraft = EncodeBridgeUiDraft.SnapshotProfile(context.CurrentWindowId);
        var toolPath = Draft(toolDraft, "path");
        var outputDir = Draft(profileDraft, "outputDir");
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_RENDER_DRAFT window={context.CurrentWindowId} toolPathPresent={!string.IsNullOrWhiteSpace(toolPath)} profileOutputPresent={!string.IsNullOrWhiteSpace(outputDir)}");
#endif

        var rows = string.Join("", s.Queue.OrderBy(x => x.AddedAt).Select(q => RenderQueueRow(context, q)));
        var historyRows = string.Join("", s.History.OrderByDescending(x => x.FinishedAt).Take(30).Select(h => RenderHistoryRow(context, h)));
        var defaultRule = EffectiveDefaultRule(s);
        var draftProfileId = Draft(profileDraft, "profileId");
        var creatingNewProfile = string.Equals(Draft(profileDraft, "newProfile"), "true", StringComparison.OrdinalIgnoreCase);
        var activeProfile = creatingNewProfile
            ? null
            : (!string.IsNullOrWhiteSpace(draftProfileId)
                ? s.Profiles.FirstOrDefault(x => string.Equals(x.ProfileId, draftProfileId, StringComparison.OrdinalIgnoreCase))
                : (!string.IsNullOrWhiteSpace(defaultRule?.ProfileId)
                    ? s.Profiles.FirstOrDefault(x => string.Equals(x.ProfileId, defaultRule.ProfileId, StringComparison.OrdinalIgnoreCase))
                    : s.Profiles.FirstOrDefault()));
        var editingProfile = creatingNewProfile || !string.IsNullOrWhiteSpace(draftProfileId);
        var activeTool = activeProfile is null
            ? null
            : s.Tools.FirstOrDefault(x => string.Equals(x.ToolId, activeProfile.ToolId, StringComparison.OrdinalIgnoreCase));
        var selectedDraftTool = s.Tools.FirstOrDefault(x => string.Equals(x.ToolId, Draft(profileDraft, "toolId"), StringComparison.OrdinalIgnoreCase));
        var toolKindValue = FirstNonBlank(Draft(toolDraft, "kind"), selectedDraftTool?.Kind.ToString() ?? activeTool?.Kind.ToString() ?? "Ffmpeg");
        var toolPathValue = FirstNonBlank(toolPath, selectedDraftTool?.ExecutablePath ?? activeTool?.ExecutablePath ?? string.Empty);
        var profileVideoValue = FirstNonBlank(Draft(profileDraft, "video"), activeProfile?.VideoCodec.ToString() ?? "H265");
        var profileQualityValue = FirstNonBlank(Draft(profileDraft, "quality"), activeProfile?.Quality.ToString() ?? "Standard");
        var profileContainerValue = FirstNonBlank(Draft(profileDraft, "container"), activeProfile?.Container ?? "mkv");
        var profilePostBatchValue = profileDraft.ContainsKey("postEncodeBatch") ? Draft(profileDraft, "postEncodeBatch") : (activeProfile?.PostEncodeBatch ?? string.Empty);
        var profilePostBatchEnabled = profileDraft.ContainsKey("postEncodeBatchEnabled")
            ? GetBool(profileDraft, "postEncodeBatchEnabled", false)
            : (activeProfile?.PostEncodeBatchEnabled ?? false);
        var profileOutputValue = FirstNonBlank(outputDir,
            activeProfile?.OutputDirectoryMode == OutputDirectoryMode.Fixed ? activeProfile.OutputDirectory : string.Empty);
        var profileCommitAction = activeProfile is null ? "add-profile" : "save-profile";
        var postBatchConsentPending = EncodeBridgeUiDraft.IsPostBatchConsentPending(context.CurrentWindowId)
            && profilePostBatchEnabled;
        var postBatchConsentPanel = postBatchConsentPending
            ? "<div class=\"batch-consent\"><b>変換後バッチを有効にしますか？</b><div>削除・移動・上書きなどのコマンドを実行できます。内容と実行結果は自己責任で使用してください。</div></div>"
            : string.Empty;
        var profileCommitButtons = postBatchConsentPending
            ? "<button class=\"button-primary\" type=\"submit\" name=\"action\" value=\"confirm-post-batch-save\">自己責任を了承します</button><button class=\"button-secondary\" type=\"submit\" name=\"action\" value=\"cancel-post-batch-save\">キャンセル</button>"
            : $"<button class=\"button-primary\" type=\"submit\" name=\"action\" value=\"{profileCommitAction}\">保存</button>";
        var profileIdHidden = activeProfile is null ? string.Empty : Hidden("profileId", activeProfile.ProfileId);
        
        var availableToolOptions = string.Join("", s.Tools.Where(t => File.Exists(t.ExecutablePath)).Select(t => $"<option value=\"{H(t.ToolId)}\"{Selected(Draft(profileDraft, "toolId"), t.ToolId)}>{H(ToolDisplayName(t.Kind))}</option>"));
        var existingToolChooser = s.Tools.Count == 0 ? string.Empty : $"<div class=\"profile-field\"><label class=\"field-label\" for=\"existing-tool\">登録済みの実行ファイルを使用</label><div class=\"field-control field-control-with-action\"><select id=\"existing-tool\" name=\"existingToolId\"><option value=\"\">選択してください</option>{availableToolOptions}</select><button class=\"button-secondary\" type=\"submit\" name=\"action\" value=\"use-registered-tool\">選択</button></div></div>";
        var draftError = EncodeBridgeUiDraft.GetValidationError(context.CurrentWindowId);
        var error = !rt.StorageHealthy
            ? $"<div class=\"notice danger\">{H(rt.StorageError)}</div>"
            : string.IsNullOrWhiteSpace(rt.FileJournalError) ? string.Empty : $"<div class=\"notice danger\">{H(rt.FileJournalError)}</div>";
        var profileError = editingProfile && !string.IsNullOrWhiteSpace(draftError)
            ? $"<div class=\"notice danger\" role=\"alert\">{H(draftError)}</div>"
            : string.Empty;
        var css = BuildCss(context);
        var formHead = FormHidden(context);
        var editorMarkup = editingProfile ? $"""
<div class="profile-editor-shell">
{profileError}
<div class="profile-editor-title">{(creatingNewProfile ? "新しいプロファイル" : $"プロファイルを編集　{H(ProfileDisplay.Name(activeTool?.Kind ?? EncodeToolKind.Ffmpeg, activeProfile?.Quality ?? QualityPreset.Standard, activeProfile?.VideoCodec ?? VideoCodecMode.H265, activeProfile?.Container ?? "mkv"))}")}</div>
<form method="post" action="{H(context.ActionEndpoint)}">{formHead}{profileIdHidden}{Hidden("toolId", FirstNonBlank(Draft(profileDraft, "toolId"), DraftToolId))}
<div class="profile-card unified-config-card">
<div class="profile-fields"><div class="editor-group-title">変換ツール・実行ファイル</div>
{existingToolChooser}
<div class="profile-field"><label class="field-label" for="profile-kind">変換ツール</label><div class="field-control"><select id="profile-kind" name="kind"><option value="Ffmpeg"{Selected(toolKindValue, "Ffmpeg")}>FFmpeg</option><option value="NvEncC"{Selected(toolKindValue, "NvEncC")}>NVEncC</option><option value="QsvEncC"{Selected(toolKindValue, "QsvEncC")}>QSVEncC</option><option value="VceEncC"{Selected(toolKindValue, "VceEncC")}>VCEEncC</option><option value="HandBrake"{Selected(toolKindValue, "HandBrake")}>HandBrakeCLI</option><option value="Amatsukaze"{Selected(toolKindValue, "Amatsukaze")}>Amatsukaze</option><option value="TmpgEnc"{Selected(toolKindValue, "TmpgEnc")}>TMPGEnc</option><option value="Custom"{Selected(toolKindValue, "Custom")}>その他CLI</option></select></div></div>
<div class="profile-field"><label class="field-label" for="profile-path">実行ファイル</label><div class="field-control field-control-with-action"><input id="profile-path" class="path-readonly" name="path" value="{H(toolPathValue)}" readonly><button class="button-secondary" type="submit" name="action" value="pick-tool-path">参照</button></div></div>
<div class="editor-group-title">変換設定</div><div class="profile-pair-grid">
<div class="profile-field"><label class="field-label" for="profile-quality">品質</label><div class="field-control"><select id="profile-quality" name="quality"><option value="High"{Selected(profileQualityValue, "High")}>高画質</option><option value="Standard"{Selected(profileQualityValue, "Standard")}>標準</option><option value="Compact"{Selected(profileQualityValue, "Compact")}>少容量</option></select></div></div>
<div class="profile-field"><label class="field-label" for="profile-video">映像</label><div class="field-control"><select id="profile-video" name="video"><option value="H265"{Selected(profileVideoValue, "H265")}>H.265</option><option value="H264"{Selected(profileVideoValue, "H264")}>H.264</option><option value="Av1"{Selected(profileVideoValue, "Av1")}>AV1</option></select></div></div>
<div class="profile-field"><label class="field-label" for="profile-container">出力形式</label><div class="field-control"><select id="profile-container" name="container"><option value="mkv"{Selected(profileContainerValue, "mkv")}>MKV</option><option value="mp4"{Selected(profileContainerValue, "mp4")}>MP4</option></select></div></div>
</div>
<div class="editor-group-title">保存先・変換後の処理</div><div class="profile-field"><label class="field-label" for="profile-output-dir">保存先</label><div class="field-control field-control-with-action"><input id="profile-output-dir" class="path-readonly" name="outputDir" value="{H(profileOutputValue)}" readonly><button class="button-secondary" type="submit" name="action" value="pick-output-dir">参照</button></div></div>
<div class="profile-field batch-section"><label class="field-label" for="profile-post-batch">変換成功後の処理</label><div class="field-control batch-field-control"><div class="batch-risk-note">任意のWindowsコマンドを実行できます。削除・移動・上書き等の記述内容と実行結果はサポート対象外です。</div><label class="batch-enable"><input type="checkbox" name="postEncodeBatchEnabled" value="true" {(profilePostBatchEnabled ? "checked" : "")}> 変換成功後にバッチを実行する</label>{postBatchConsentPanel}<textarea id="profile-post-batch" class="batch-script" name="postEncodeBatch" rows="5" maxlength="{EncodeBridgeDefaults.MaxPostEncodeBatchLength}">{H(profilePostBatchValue)}</textarea><div class="field-help">&#123;source&#125;=元TS、&#123;output&#125;=変換後ファイル。ほかに &#123;sourceDir&#125; &#123;outputDir&#125; &#123;sourceName&#125; &#123;outputName&#125; も使用できます。</div></div></div>
</div>
<div class="profile-actions"><button class="button-secondary" type="submit" name="action" value="cancel-edit-profile">キャンセル</button>{profileCommitButtons}</div>
</div></form></div>
""" : string.Empty;
        var savedProfiles = RenderRegisteredProfiles(context, s, defaultRule?.ProfileId ?? string.Empty,
            creatingNewProfile ? string.Empty : draftProfileId, editorMarkup);
        var clearPending = PendingHistoryClear.ContainsKey(context.CurrentWindowId);
        var historyClearAction = clearPending
            ? $"<form class=\"history-clear-confirm\" method=\"post\" action=\"{H(context.ActionEndpoint)}\">{formHead}<span>履歴をすべて削除しますか？</span><button class=\"button-danger\" type=\"submit\" name=\"action\" value=\"delete-history\">すべて削除する</button><button class=\"button-secondary\" type=\"submit\" name=\"action\" value=\"cancel-delete-history\">キャンセル</button></form>"
            : $"<form method=\"post\" action=\"{H(context.ActionEndpoint)}\">{formHead}<button class=\"button-danger\" type=\"submit\" name=\"action\" value=\"confirm-delete-history\">履歴をすべて削除</button></form>";


        var autoLabel = s.Settings.AutoEncodeEnabled ? "ON" : "OFF";
        var autoHint = configured
            ? (s.Settings.AutoEncodeEnabled ? "録画終了後に自動で引き渡します。" : "変換設定が保存済みです。自動変換をONにできます。")
            : "変換設定を保存すると自動変換をONにできます。";
        var autoDisabled = !configured && !s.Settings.AutoEncodeEnabled ? " disabled" : string.Empty;

        return $"""
<div id="encodebridge-root" class="eb-root">
<style>{css}</style>
<table class="head-table" role="presentation"><tr>
<td><h1>EncodeBridge <span class="app-version">v1.0.1</span></h1><div class="sub">録画終了後の変換を外部ツールへ引き渡します。</div></td>
<td class="head-action"><form method="post" action="{H(context.ActionEndpoint)}" class="auto-toggle-form">{formHead}<input type="hidden" name="enabled" value="{(!s.Settings.AutoEncodeEnabled).ToString().ToLowerInvariant()}"><span class="auto-toggle-label">自動変換</span><button class="auto-switch {(s.Settings.AutoEncodeEnabled ? "is-on" : "is-off")}" type="submit" name="action" value="toggle-auto" role="switch" aria-checked="{s.Settings.AutoEncodeEnabled.ToString().ToLowerInvariant()}" aria-label="自動変換を{(s.Settings.AutoEncodeEnabled ? "OFF" : "ON")}に切り替える" title="{(s.Settings.AutoEncodeEnabled ? "自動変換 ON" : "自動変換 OFF")}"{autoDisabled}><span class="auto-toggle-track" aria-hidden="true"><span class="auto-toggle-thumb"></span></span></button><span class="auto-toggle-state {(s.Settings.AutoEncodeEnabled ? "is-on" : "is-off")}">{autoLabel}</span></form></td>
</tr></table>
<div class="auto-hint">{H(autoHint)}</div>
{error}
<table class="summary" role="presentation"><tr>
<td><b>{waiting}</b><span>待機</span></td><td><b>{running}</b><span>処理中</span></td><td><b>{handed}</b><span>引き渡し済み</span></td><td><b>{completed}</b><span>完了</span></td><td><b>{failed}</b><span>失敗</span></td>
</tr></table>

<div class="section"><h2>現在の処理</h2>{(rows.Length == 0 ? "<div class=\"empty\">現在の処理はありません。</div>" : rows)}</div>

<div class="section"><h2>基本設定</h2>
<form method="post" action="{H(context.ActionEndpoint)}">{formHead}
<div class="basic-card">
<div class="basic-grid">
<label class="basic-field"><span class="field-label">同時に変換</span><span class="field-control compact-control"><input name="maxConcurrent" type="number" min="1" max="4" value="{s.Settings.MaxConcurrentEncodes}"><span class="field-unit">件</span></span></label>
<label class="basic-field"><span class="field-label">最低空き容量</span><span class="field-control compact-control"><input name="minFreeGb" type="number" min="1" value="{s.Settings.MinimumFreeSpaceGb}"><span class="field-unit">GB</span></span></label>
<label class="basic-field"><span class="field-label">履歴保持</span><span class="field-control compact-control"><input name="historyDays" type="number" min="1" value="{s.Settings.HistoryRetentionDays}"><span class="field-unit">日</span></span></label>
</div>
<div class="basic-checks"><label class="check"><input name="pauseRecording" type="checkbox" value="true" {(s.Settings.PauseStartWhileRecording ? "checked" : "")}> 録画中は新しい変換を開始しない</label><label class="check"><input name="pauseEpg" type="checkbox" value="true" {(s.Settings.PauseStartWhileEpg ? "checked" : "")}> 番組情報の取得中は新しい変換を開始しない</label></div>
<div class="basic-actions"><button class="commit save" type="submit" name="action" value="save-basic">保存</button></div>
</div></form></div>

<div class="section"><div class="section-heading-row"><div><h2>変換プロファイル</h2><div class="section-help">○ を選んだプロファイルは、TvAIrから指定がない通常の自動変換で使用します。</div></div>{(editingProfile ? string.Empty : $"<form method=\"post\" action=\"{H(context.ActionEndpoint)}\">{formHead}<button class=\"button-primary\" type=\"submit\" name=\"action\" value=\"new-profile\">＋ プロファイルを追加</button></form>")}</div>
{savedProfiles}
{(creatingNewProfile ? editorMarkup : string.Empty)}
</div>

<div class="section"><h2>履歴</h2>{(historyRows.Length == 0 ? "<div class=\"empty\">履歴はありません。</div>" : historyRows)}<div class="history-clear-footer">{historyClearAction}</div></div>
</div>
""";
    }

    public async Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext context, CancellationToken cancellationToken)
    {
        var rt = _runtime;
        if (rt is null)
            return RuntimeUiActionResult.Fail("not_ready", "EncodeBridgeを開始できません。");

        try
        {
            rt.MarkUiInteraction(context.CurrentWindowId);
            var p = context.Payload;
            var action = context.ActionName;
            if (string.IsNullOrWhiteSpace(action) && p.TryGetValue("action", out var payloadAction)) action = payloadAction;
            action = action?.Trim() ?? string.Empty;

            switch (action)
            {
                case "toggle-auto":
                {
                    var enable = GetBool(p, "enabled", !rt.Store.Settings.AutoEncodeEnabled);
                    if (enable && !IsAutoConfigurationReady(rt.Store))
                        return RuntimeUiActionResult.Fail("configuration_required", "先に変換ツールと変換設定を登録してください。");
                    var previousAutoEncodeEnabled = rt.Store.Settings.AutoEncodeEnabled;
                    rt.Store.Settings.AutoEncodeEnabled = enable;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_AUTO_STATE result=CHANGED previous={previousAutoEncodeEnabled} current={enable} source=user_toggle");
#endif
                    rt.Save();
                    rt.PublishProfileSelections();
                    break;
                }
                case "save-basic":
                    rt.Store.Settings.MaxConcurrentEncodes = Math.Clamp(GetInt(p, "maxConcurrent", 1), 1, 4);
                    rt.Store.Settings.MinimumFreeSpaceGb = Math.Max(1, GetInt(p, "minFreeGb", 10));
                    rt.Store.Settings.HistoryRetentionDays = Math.Max(1, GetInt(p, "historyDays", 30));
                    rt.Store.Settings.PauseStartWhileRecording = GetBool(p, "pauseRecording", false);
                    rt.Store.Settings.PauseStartWhileEpg = GetBool(p, "pauseEpg", false);
                    rt.Save();
                    break;

                case "pick-tool-path":
                {
                    EncodeBridgeUiDraft.CaptureTool(context.CurrentWindowId, p);
                    EncodeBridgeUiDraft.CaptureProfile(context.CurrentWindowId, p);
                    var current = Draft(EncodeBridgeUiDraft.SnapshotTool(context.CurrentWindowId), "path");
                    var result = await rt.PickExecutableAsync(context.CurrentWindowId, cancellationToken).ConfigureAwait(false);
                    if (result.Cancelled) return RefreshResult();
                    if (!result.Accepted || string.IsNullOrWhiteSpace(result.SelectedPath))
                        return RuntimeUiActionResult.Fail(result.ErrorCode ?? "picker_failed", result.Message ?? "実行ファイルを選択できませんでした。");
                    EncodeBridgeUiDraft.SetToolPath(context.CurrentWindowId, result.SelectedPath);
                    EncodeBridgeUiDraft.SetProfileTool(context.CurrentWindowId, DraftToolId);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_TOOL_PICK result=OK window={context.CurrentWindowId} previousPresent={!string.IsNullOrWhiteSpace(current)} selectedPresent=True profileSelection=draft_tool");
#endif
                    return RefreshResult();
                }

                case "pick-output-dir":
                {
                    EncodeBridgeUiDraft.CaptureProfile(context.CurrentWindowId, p);
                    EncodeBridgeUiDraft.CaptureTool(context.CurrentWindowId, p);
                    var result = await rt.PickOutputFolderAsync(context.CurrentWindowId, cancellationToken).ConfigureAwait(false);
                    if (result.Cancelled) return RefreshResult();
                    if (!result.Accepted || string.IsNullOrWhiteSpace(result.SelectedPath))
                        return RuntimeUiActionResult.Fail(result.ErrorCode ?? "picker_failed", result.Message ?? "保存先を選択できませんでした。");
                    EncodeBridgeUiDraft.SetProfileOutput(context.CurrentWindowId, result.SelectedPath);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_OUTPUT_PICK result=OK window={context.CurrentWindowId} selectedPresent=True");
#endif
                    return RefreshResult();
                }


                case "pick-tmpg-template":
                {
                    var profileId = Get(p, "profileId");
                    var profile = rt.Store.Profiles.FirstOrDefault(x => x.ProfileId == profileId)
                                  ?? throw new InvalidOperationException("変換設定を確認してください。");
                    var tool = rt.Store.Tools.FirstOrDefault(x => x.ToolId == profile.ToolId)
                               ?? throw new InvalidOperationException("変換ツールを確認してください。");
                    if (tool.Kind != EncodeToolKind.TmpgEnc)
                        throw new InvalidOperationException("TMPGEncの変換設定ではありません。");

                    var result = await rt.PickTmpgEncEncodeTemplateAsync(context.CurrentWindowId, cancellationToken).ConfigureAwait(false);
                    if (result.Cancelled) return RefreshResult();
                    if (!result.Accepted || string.IsNullOrWhiteSpace(result.SelectedPath))
                        return RuntimeUiActionResult.Fail(result.ErrorCode ?? "picker_failed", result.Message ?? "TMPGEncの出力テンプレートを選択できませんでした。");

                    profile.AdapterSettings["encodeTemplate"] = result.SelectedPath.Trim();
                    rt.Save();
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_TMPGENC_TEMPLATE result=OK profileId={profile.ProfileId} selectedPresent=True");
#endif
                    return RefreshResult("TMPGEncの出力テンプレートを設定しました。");
                }

                case "add-tool":
                    EncodeBridgeUiDraft.CaptureTool(context.CurrentWindowId, p);
                    var addedTool = AddTool(rt, EncodeBridgeUiDraft.SnapshotTool(context.CurrentWindowId));
                    EncodeBridgeUiDraft.SetProfileTool(context.CurrentWindowId, addedTool.ToolId);
                    rt.Save();
                    EncodeBridgeUiDraft.ClearTool(context.CurrentWindowId);
                    break;
                case "select-tool":
                {
                    var toolId = Get(p, "toolId");
                    if (!rt.Store.Tools.Any(x => string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("変換ツールを確認してください。");
                    EncodeBridgeUiDraft.SetProfileTool(context.CurrentWindowId, toolId);
                    return RefreshResult();
                }
                case "remove-tool":
                {
                    var removedToolId = Get(p, "toolId");
                    RemoveTool(rt, p);
                    if (string.Equals(Draft(EncodeBridgeUiDraft.SnapshotProfile(context.CurrentWindowId), "toolId"), removedToolId, StringComparison.OrdinalIgnoreCase))
                        EncodeBridgeUiDraft.SetProfileTool(context.CurrentWindowId, rt.Store.Tools.FirstOrDefault()?.ToolId ?? string.Empty);
                    rt.Save();
                    break;
                }
                case "new-profile":
                    EncodeBridgeUiDraft.ClearValidationError(context.CurrentWindowId);
                    EncodeBridgeUiDraft.SetProfile(context.CurrentWindowId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["newProfile"] = "true",
                        ["toolId"] = DraftToolId,
                        ["video"] = "H265",
                        ["quality"] = "Standard",
                        ["container"] = "mkv",
                        ["outputDir"] = string.Empty,
                        ["postEncodeBatch"] = string.Empty,
                        ["postEncodeBatchEnabled"] = "false"
                    });
                    EncodeBridgeUiDraft.ClearTool(context.CurrentWindowId);
                    EncodeBridgeUiDraft.SetPostBatchConsentPending(context.CurrentWindowId, false);
                    return RefreshResult();
                case "use-registered-tool":
                {
                    EncodeBridgeUiDraft.CaptureProfile(context.CurrentWindowId, p);
                    EncodeBridgeUiDraft.CaptureTool(context.CurrentWindowId, p);
                    var toolId = Get(p, "existingToolId");
                    var savedTool = rt.Store.Tools.FirstOrDefault(t => string.Equals(t.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
                    if (savedTool is null)
                    {
                        EncodeBridgeUiDraft.SetValidationError(context.CurrentWindowId, "登録済みの実行ファイルを選択してください。");
                        return RefreshResult();
                    }
                    EncodeBridgeUiDraft.SetProfileTool(context.CurrentWindowId, savedTool.ToolId);
                    EncodeBridgeUiDraft.SetToolPath(context.CurrentWindowId, savedTool.ExecutablePath);
                    EncodeBridgeUiDraft.SetToolKind(context.CurrentWindowId, savedTool.Kind.ToString());
                    EncodeBridgeUiDraft.ClearValidationError(context.CurrentWindowId);
                    return RefreshResult();
                }
                case "add-profile":
                case "save-profile":
                {
                    EncodeBridgeUiDraft.CaptureProfile(context.CurrentWindowId, p);
                    EncodeBridgeUiDraft.CaptureTool(context.CurrentWindowId, p);
                    var profileValues = EncodeBridgeUiDraft.SnapshotProfile(context.CurrentWindowId);
                    var requestedPostBatchEnabled = GetBool(profileValues, "postEncodeBatchEnabled", false);
                    var existingProfileId = Get(profileValues, "profileId");
                    var existingProfile = string.IsNullOrWhiteSpace(existingProfileId)
                        ? null
                        : rt.Store.Profiles.FirstOrDefault(x => string.Equals(x.ProfileId, existingProfileId, StringComparison.OrdinalIgnoreCase));
                    var requiresPostBatchConsent = requestedPostBatchEnabled && (existingProfile is null || !existingProfile.PostEncodeBatchEnabled || !existingProfile.PostEncodeBatchRiskAccepted);
                    if (requiresPostBatchConsent)
                    {
                        EncodeBridgeUiDraft.SetPostBatchConsentPending(context.CurrentWindowId, true);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_POST_BATCH_CONSENT result=REQUIRED source=profile_save window={context.CurrentWindowId}");
#endif
                        return RefreshResult();
                    }

                    return CommitProfileOrShowError(rt, context.CurrentWindowId, profileValues, acceptPostBatchRisk: false);
                }
                case "confirm-post-batch-save":
                {
                    EncodeBridgeUiDraft.CaptureProfile(context.CurrentWindowId, p);
                    EncodeBridgeUiDraft.CaptureTool(context.CurrentWindowId, p);
                    var profileValues = EncodeBridgeUiDraft.SnapshotProfile(context.CurrentWindowId);
                    if (!GetBool(profileValues, "postEncodeBatchEnabled", false))
                    {
                        EncodeBridgeUiDraft.SetPostBatchConsentPending(context.CurrentWindowId, false);
                        return RefreshResult();
                    }
                    var saveResult = CommitProfileOrShowError(rt, context.CurrentWindowId, profileValues, acceptPostBatchRisk: true);
                    if (!string.IsNullOrWhiteSpace(EncodeBridgeUiDraft.GetValidationError(context.CurrentWindowId)))
                        return saveResult;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_POST_BATCH_CONSENT result=ACCEPTED source=profile_save window={context.CurrentWindowId}");
#endif
                    return RefreshResult("設定を保存しました。");
                }
                case "cancel-post-batch-save":
                    EncodeBridgeUiDraft.SetPostBatchConsentPending(context.CurrentWindowId, false);
                    EncodeBridgeUiDraft.SetPostBatchEnabled(context.CurrentWindowId, false);
                    return RefreshResult();
                case "edit-profile":
                    if (string.Equals(Get(EncodeBridgeUiDraft.SnapshotProfile(context.CurrentWindowId), "profileId"), Get(p, "profileId"), StringComparison.OrdinalIgnoreCase))
                    {
                        EncodeBridgeUiDraft.ClearProfile(context.CurrentWindowId);
                        EncodeBridgeUiDraft.ClearTool(context.CurrentWindowId);
                        return RefreshResult();
                    }
                    EncodeBridgeUiDraft.ClearValidationError(context.CurrentWindowId);
                    EncodeBridgeUiDraft.ClearTool(context.CurrentWindowId);
                    LoadProfileDraft(rt, context.CurrentWindowId, p);
                    EncodeBridgeUiDraft.SetPostBatchConsentPending(context.CurrentWindowId, false);
                    return RefreshResult();
                case "cancel-edit-profile":
                    EncodeBridgeUiDraft.ClearProfile(context.CurrentWindowId);
                    EncodeBridgeUiDraft.ClearTool(context.CurrentWindowId);
                    return RefreshResult();
                case "remove-profile":
                    RemoveProfile(rt, p);
                    rt.Save();
                    rt.PublishProfileSelections();
                    break;
                case "set-default-profile":
                {
                    var requestedId = Get(p, "profileId");
                    var before = EffectiveDefaultRule(rt.Store)?.ProfileId ?? string.Empty;
                    SetDefault(rt, p);
                    rt.Save();
                    var after = EffectiveDefaultRule(rt.Store)?.ProfileId ?? string.Empty;
                    if (!string.Equals(requestedId, after, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("既定プロファイルの保存結果が一致しません。");
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_DEFAULT_COMMIT result=OK previous={before} requested={requestedId} effective={after} profiles={rt.Store.Profiles.Count} rules={rt.Store.Rules.Count}");
#endif
                    // The Host may choose full navigation even for a successful patch. Make
                    // the persisted rule authoritative for both patches and HTML rendering.
                    var patches = new List<RuntimeUiPatch>();
                    foreach (var profile in rt.Store.Profiles)
                    {
                        var selected = string.Equals(profile.ProfileId, after, StringComparison.OrdinalIgnoreCase);
                        patches.Add(RuntimeUiPatch.Check("profile-default-radio-" + profile.ProfileId, selected));
                        patches.Add(RuntimeUiPatch.Visibility("profile-default-label-" + profile.ProfileId, selected));
                        patches.Add(RuntimeUiPatch.Classes("profile-line-" + profile.ProfileId,
                            add: selected ? new[] { "is-default" } : Array.Empty<string>(),
                            remove: selected ? Array.Empty<string>() : new[] { "is-default" }));
                    }
                    return new RuntimeUiActionResult { Succeeded = true,
                        Message = "既定のプロファイルを変更しました。",
                        UiPatches = patches, RefreshRequested = false };
                }
                case "remove-waiting": rt.Remove(Get(p, "id")); break;
                case "stop": rt.Stop(Get(p, "id")); break;
                case "retry": rt.Retry(Get(p, "id")); break;
                case "remove-history-item":
                {
                    var id = Get(p, "id");
                    var item = rt.Store.History.FirstOrDefault(h => string.Equals(h.QueueItemId, id, StringComparison.Ordinal));
                    if (item is null) return RuntimeUiActionResult.Fail("not_found", "対象の履歴がありません。");
                    rt.Store.History.Remove(item);
                    rt.Save();
                    return RefreshResult("履歴を削除しました。");
                }
                case "confirm-delete-history":
                    PendingHistoryClear[context.CurrentWindowId] = true;
                    return RefreshResult();
                case "cancel-delete-history":
                    PendingHistoryClear.TryRemove(context.CurrentWindowId, out _);
                    return RefreshResult();
                case "delete-history":
                    if (!PendingHistoryClear.TryRemove(context.CurrentWindowId, out _))
                        return RuntimeUiActionResult.Fail("confirmation_required", "削除には確認が必要です。");
                    rt.Store.History.Clear(); rt.Save();
                    return RefreshResult("履歴をすべて削除しました。");
                default:
                    return RuntimeUiActionResult.Fail("unsupported", "操作を実行できません。");
            }

            return RefreshResult("設定を保存しました。");
        }
        catch (Exception ex)
        {
            return RuntimeUiActionResult.Fail("action_failed", ex.Message);
        }
    }

    private static RuntimeUiActionResult CommitProfileOrShowError(EncodeBridgeRuntime rt, string windowId,
        Dictionary<string, string> profileValues, bool acceptPostBatchRisk)
    {
        try
        {
            SaveProfileDraft(rt, windowId, profileValues, EncodeBridgeUiDraft.SnapshotTool(windowId), acceptPostBatchRisk);
            EncodeBridgeUiDraft.ClearValidationError(windowId);
            return RefreshResult("設定を保存しました。");
        }
        catch (InvalidOperationException ex)
        {
            // The saved state is unchanged; retain the draft and render an actionable error.
            EncodeBridgeUiDraft.SetValidationError(windowId, ex.Message);
            EncodeBridgeUiDraft.SetPostBatchConsentPending(windowId, false);
            return RefreshResult();
        }
    }

    private static void SaveProfileDraft(EncodeBridgeRuntime rt, string windowId, Dictionary<string, string> profileValues, Dictionary<string, string> toolValues, bool acceptPostBatchRisk)
    {
        var originalToolId = Get(profileValues, "toolId");
        var toolCreated = ResolveDraftToolForProfile(rt, profileValues, toolValues);
        try
        {
            profileValues["postEncodeBatchRiskAccepted"] = acceptPostBatchRisk ? "true" : "false";
            if (string.IsNullOrWhiteSpace(Get(profileValues, "profileId")))
                AddProfile(rt, profileValues);
            else
                SaveProfile(rt, profileValues);

            rt.Save();
            rt.PublishProfileSelections();
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_PROFILE_COMMIT result=SAVED profiles={rt.Store.Profiles.Count} tools={rt.Store.Tools.Count} published=True");
#endif
            EncodeBridgeUiDraft.ClearProfile(windowId);
            if (toolCreated) EncodeBridgeUiDraft.ClearTool(windowId);
            EncodeBridgeUiDraft.SetPostBatchConsentPending(windowId, false);
        }
        catch
        {
            RollbackDraftTool(rt, profileValues, toolCreated);
            profileValues["toolId"] = originalToolId;
            throw;
        }
    }

    private static RuntimeUiActionResult RefreshResult(string? message = null) => new()
    {
        Succeeded = true,
        Message = message ?? string.Empty,
        RefreshRequested = true,
        RefreshTarget = "content",
        PreserveScroll = true
    };

    private static string FormHidden(RuntimeUiRenderContext context)
    {
        return Hidden("pluginId", context.PluginId)
            + Hidden("route", context.Route)
            + Hidden("routeSegment", context.Route)
            + Hidden("actionToken", context.ActionToken)
            + Hidden("windowId", context.CurrentWindowId)
            + Hidden("responseMode", "refreshWindow")
            + Hidden("refreshTarget", "content")
            + Hidden("preserveScroll", "true");
    }

    private static string Hidden(string name, string? value)
        => $"<input type=\"hidden\" name=\"{H(name)}\" value=\"{H(value)}\">";

    private static string Draft(IReadOnlyDictionary<string, string> draft, string key)
        => draft.TryGetValue(key, out var value) ? value : string.Empty;

    private static string Selected(string current, string value)
        => string.Equals(current, value, StringComparison.OrdinalIgnoreCase) ? " selected" : string.Empty;

    private static bool IsAutoConfigurationReady(EncodeBridgeStore store)
    {
        var rule = store.Rules.Where(r => r.Enabled && r.Kind == EncodeRuleKind.Default).OrderBy(r => r.Order).FirstOrDefault();
        if (rule is null) return false;
        var profile = store.Profiles.FirstOrDefault(p => p.Enabled && p.ProfileId == rule.ProfileId);
        if (profile is null) return false;
        var tool = store.Tools.FirstOrDefault(t => t.ToolId == profile.ToolId);
        return tool is not null && !string.IsNullOrWhiteSpace(tool.ExecutablePath) && File.Exists(tool.ExecutablePath);
    }

    private static EncodeTool AddTool(EncodeBridgeRuntime rt, IReadOnlyDictionary<string, string> values)
    {
        var kindText = FirstNonBlank(Get(values, "kind"), "Ffmpeg");
        if (!Enum.TryParse<EncodeToolKind>(kindText, true, out var kind))
            throw new InvalidOperationException("変換ツールの種類を確認してください。");

        var path = Get(values, "path").Trim();
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("実行ファイルを指定してください。");
        if (!File.Exists(path))
            throw new InvalidOperationException("指定した実行ファイルが見つかりません。");

        var tool = new EncodeTool
        {
            Kind = kind,
            DisplayName = ToolDisplayName(kind),
            ExecutablePath = path
        };
        rt.Store.Tools.Add(tool);

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_TOOL_SAVE result=OK toolId={tool.ToolId} kind={kind} pathPresent=True storeCount={rt.Store.Tools.Count}");
#endif
        return tool;
    }


    private static void RemoveTool(EncodeBridgeRuntime rt, IReadOnlyDictionary<string, string> p)
    {
        var toolId = Get(p, "toolId");
        var tool = rt.Store.Tools.FirstOrDefault(x => string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("変換ツールを確認してください。");

        if (rt.Store.Profiles.Any(x => string.Equals(x.ToolId, tool.ToolId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("この変換ツールは変換設定で使用中です。先にその変換設定を変更または削除してください。");

        rt.Store.Tools.Remove(tool);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_TOOL_REMOVE result=OK toolId={tool.ToolId} kind={tool.Kind} remaining={rt.Store.Tools.Count}");
#endif
    }

    private static string FirstNonBlank(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return string.Empty;
    }

    private static bool ResolveDraftToolForProfile(EncodeBridgeRuntime rt, Dictionary<string, string> profileValues, IReadOnlyDictionary<string, string> toolDraft)
    {
        var existingId = Get(profileValues, "toolId");
        var selectedTool = rt.Store.Tools.FirstOrDefault(t => string.Equals(t.ToolId, existingId, StringComparison.OrdinalIgnoreCase));
        var kindText = FirstNonBlank(Get(toolDraft, "kind"), selectedTool?.Kind.ToString() ?? "Ffmpeg");
        if (!Enum.TryParse<EncodeToolKind>(kindText, true, out var kind))
            throw new InvalidOperationException("変換ツールの種類を確認してください。");

        var path = FirstNonBlank(Get(toolDraft, "path").Trim(), selectedTool?.ExecutablePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("実行ファイルを指定してください。");
        if (!File.Exists(path))
            throw new InvalidOperationException("指定した実行ファイルが見つかりません。");

        var existing = rt.Store.Tools.FirstOrDefault(x => x.Kind == kind && string.Equals(x.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            profileValues["toolId"] = existing.ToolId;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_PROFILE_TOOL_BIND result=REUSED toolId={existing.ToolId} kind={kind} source=draft");
#endif
            return false;
        }

        var tool = new EncodeTool
        {
            Kind = kind,
            DisplayName = ToolDisplayName(kind),
            ExecutablePath = path
        };
        rt.Store.Tools.Add(tool);
        profileValues["toolId"] = tool.ToolId;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_PROFILE_TOOL_BIND result=CREATED toolId={tool.ToolId} kind={kind} source=draft storeCount={rt.Store.Tools.Count}");
#endif
        return true;
    }

    private static void RollbackDraftTool(EncodeBridgeRuntime rt, IReadOnlyDictionary<string, string> profileValues, bool toolCreated)
    {
        if (!toolCreated) return;
        var toolId = Get(profileValues, "toolId");
        var tool = rt.Store.Tools.FirstOrDefault(x => string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
        if (tool is not null && !rt.Store.Profiles.Any(x => string.Equals(x.ToolId, tool.ToolId, StringComparison.OrdinalIgnoreCase)))
            rt.Store.Tools.Remove(tool);
    }

    private static void AddProfile(EncodeBridgeRuntime rt, IReadOnlyDictionary<string, string> values)
    {
        var toolId = Get(values, "toolId");
        var tool = rt.Store.Tools.FirstOrDefault(x => x.ToolId == toolId)
                   ?? throw new InvalidOperationException("変換ツールを選択してください。");

        var videoText = FirstNonBlank(Get(values, "video"), "H265");
        if (!Enum.TryParse<VideoCodecMode>(videoText, true, out var video))
            throw new InvalidOperationException("映像設定を確認してください。");
        var qualityText = FirstNonBlank(Get(values, "quality"), "Standard");
        if (!Enum.TryParse<QualityPreset>(qualityText, true, out var quality))
            throw new InvalidOperationException("品質設定を確認してください。");

        var output = Get(values, "outputDir").Trim();
        if (!string.IsNullOrWhiteSpace(output) && !Directory.Exists(output))
            throw new InvalidOperationException("指定した保存先が見つかりません。");

        var postBatchEnabled = GetBool(values, "postEncodeBatchEnabled", false);
        var container = NormalizeContainer(Get(values, "container"));
        var profileName = ProfileDisplay.Name(tool.Kind, quality, video, container);


        var profile = new EncodeProfile
        {
            Name = profileName,
            ToolId = tool.ToolId,
            VideoCodec = video,
            Quality = quality,
            Container = container,
            OutputDirectoryMode = string.IsNullOrWhiteSpace(output) ? OutputDirectoryMode.SameAsSource : OutputDirectoryMode.Fixed,
            OutputDirectory = output,
            PostEncodeBatch = ValidatePostEncodeBatch(Get(values, "postEncodeBatch")),
            PostEncodeBatchEnabled = postBatchEnabled,
            PostEncodeBatchRiskAccepted = postBatchEnabled && GetBool(values, "postEncodeBatchRiskAccepted", false)
        };
        rt.Store.Profiles.Add(profile);
        if (!rt.Store.Rules.Any(x => x.Kind == EncodeRuleKind.Default))
            rt.Store.Rules.Add(new EncodeRule { Order = int.MaxValue, Kind = EncodeRuleKind.Default, ProfileId = profile.ProfileId });
    }


    private static void LoadProfileDraft(EncodeBridgeRuntime rt, string windowId, IReadOnlyDictionary<string, string> values)
    {
        var profileId = Get(values, "profileId");
        var profile = rt.Store.Profiles.FirstOrDefault(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("変換設定を確認してください。");

        EncodeBridgeUiDraft.SetProfile(windowId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["profileId"] = profile.ProfileId,
            ["toolId"] = profile.ToolId,
            ["video"] = profile.VideoCodec.ToString(),
            ["quality"] = profile.Quality.ToString(),
            ["container"] = profile.Container,
            ["outputDir"] = profile.OutputDirectoryMode == OutputDirectoryMode.Fixed ? profile.OutputDirectory : string.Empty,
            ["postEncodeBatch"] = profile.PostEncodeBatch,
            ["postEncodeBatchEnabled"] = profile.PostEncodeBatchEnabled.ToString().ToLowerInvariant(),
            ["postEncodeBatchRiskAccepted"] = profile.PostEncodeBatchRiskAccepted.ToString().ToLowerInvariant()
        });
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_PROFILE_EDIT result=LOADED profileId={profile.ProfileId} toolId={profile.ToolId}");
#endif
    }

    private static void SaveProfile(EncodeBridgeRuntime rt, IReadOnlyDictionary<string, string> values)
    {
        var profileId = Get(values, "profileId");
        var profile = rt.Store.Profiles.FirstOrDefault(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("変換設定を確認してください。");

        var toolId = Get(values, "toolId");
        var tool = rt.Store.Tools.FirstOrDefault(x => string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("変換ツールを選択してください。");

        var videoText = FirstNonBlank(Get(values, "video"), profile.VideoCodec.ToString());
        if (!Enum.TryParse<VideoCodecMode>(videoText, true, out var video))
            throw new InvalidOperationException("映像設定を確認してください。");
        var qualityText = FirstNonBlank(Get(values, "quality"), profile.Quality.ToString());
        if (!Enum.TryParse<QualityPreset>(qualityText, true, out var quality))
            throw new InvalidOperationException("品質設定を確認してください。");

        var output = Get(values, "outputDir").Trim();
        if (!string.IsNullOrWhiteSpace(output) && !Directory.Exists(output))
            throw new InvalidOperationException("指定した保存先が見つかりません。");

        // Validate all user input before mutating the persisted profile.
        var validatedBatch = ValidatePostEncodeBatch(Get(values, "postEncodeBatch"));
        var previousToolId = profile.ToolId;
        var wasPostBatchEnabled = profile.PostEncodeBatchEnabled;
        var wasPostBatchRiskAccepted = profile.PostEncodeBatchRiskAccepted;
        var container = NormalizeContainer(Get(values, "container"));
        var profileName = ProfileDisplay.Name(tool.Kind, quality, video, container);

        profile.Name = profileName;
        profile.ToolId = tool.ToolId;
        profile.VideoCodec = video;
        profile.Quality = quality;
        profile.Container = container;
        var postBatchEnabled = GetBool(values, "postEncodeBatchEnabled", false);

        profile.OutputDirectoryMode = string.IsNullOrWhiteSpace(output) ? OutputDirectoryMode.SameAsSource : OutputDirectoryMode.Fixed;
        profile.OutputDirectory = output;
        profile.PostEncodeBatch = validatedBatch;
        profile.PostEncodeBatchEnabled = postBatchEnabled;
        profile.PostEncodeBatchRiskAccepted = postBatchEnabled
            && (GetBool(values, "postEncodeBatchRiskAccepted", false) || (wasPostBatchEnabled && wasPostBatchRiskAccepted));
        if (!string.Equals(previousToolId, tool.ToolId, StringComparison.OrdinalIgnoreCase))
        {
            profile.AdapterSettings.Clear();
            PruneUnusedTools(rt);
        }

#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_PROFILE_SAVE result=OK profileId={profile.ProfileId} toolChanged={!string.Equals(previousToolId, tool.ToolId, StringComparison.OrdinalIgnoreCase)} toolId={profile.ToolId} postBatchEnabled={profile.PostEncodeBatchEnabled} postBatchTextBlank={string.IsNullOrWhiteSpace(profile.PostEncodeBatch)} postBatchRiskAccepted={profile.PostEncodeBatchRiskAccepted} postBatchEffective={profile.PostEncodeBatchEnabled && !string.IsNullOrWhiteSpace(profile.PostEncodeBatch) && profile.PostEncodeBatchRiskAccepted}");
#endif
    }


    private static void RemoveProfile(EncodeBridgeRuntime rt, IReadOnlyDictionary<string, string> p)
    {
        var profileId = Get(p, "profileId");
        var profile = rt.Store.Profiles.FirstOrDefault(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("変換設定を確認してください。");

        rt.Store.Profiles.Remove(profile);
        rt.Store.Rules.RemoveAll(x => string.Equals(x.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase));
        PruneUnusedTools(rt);
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_PROFILE_REMOVE result=OK profileId={profile.ProfileId} remaining={rt.Store.Profiles.Count}");
#endif
    }


    private static string NormalizeContainer(string value)
    {
        var container = string.IsNullOrWhiteSpace(value) ? "mkv" : value.Trim().ToLowerInvariant();
        return container is "mkv" or "mp4" ? container : throw new InvalidOperationException("出力形式を確認してください。");
    }

    private static string ValidatePostEncodeBatch(string value)
    {
        value ??= string.Empty;
        if (value.Length > EncodeBridgeDefaults.MaxPostEncodeBatchLength)
            throw new InvalidOperationException("変換後バッチの記述が長すぎます。");
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
    }

    private static void PruneUnusedTools(EncodeBridgeRuntime rt)
    {
        var used = rt.Store.Profiles.Select(x => x.ToolId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rt.Store.Tools.RemoveAll(x => !used.Contains(x.ToolId));
    }

    private static EncodeRule? EffectiveDefaultRule(EncodeBridgeStore store) =>
        store.Rules.Where(x => x.Enabled && x.Kind == EncodeRuleKind.Default)
            .OrderBy(x => x.Order).FirstOrDefault();

    private static void SetDefault(EncodeBridgeRuntime rt, IReadOnlyDictionary<string, string> p)
    {
        var id = Get(p, "profileId");
        if (!rt.Store.Profiles.Any(x => x.Enabled && string.Equals(x.ProfileId, id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("有効な変換プロファイルを選択してください。");
        var effective = EffectiveDefaultRule(rt.Store);
        if (effective is null)
        {
            // Re-enable the established default rule rather than creating a
            // second competing default owner when an inactive rule exists.
            effective = rt.Store.Rules.Where(x => x.Kind == EncodeRuleKind.Default)
                .OrderBy(x => x.Order).FirstOrDefault();
            if (effective is null)
            {
                effective = new EncodeRule { Order = int.MaxValue, Kind = EncodeRuleKind.Default };
                rt.Store.Rules.Add(effective);
            }
            effective.Enabled = true;
        }
        effective.ProfileId = id;
        // A single default owner is the authority for display and scheduling.
        foreach (var other in rt.Store.Rules.Where(x => x.Kind == EncodeRuleKind.Default && !ReferenceEquals(x, effective)))
            other.Enabled = false;
    }

    private static string RenderRegisteredTools(RuntimeUiRenderContext c, EncodeBridgeStore s, string selectedToolId)
    {
        if (s.Tools.Count == 0)
            return "<div class=\"empty compact-empty\">登録済みの外部アプリはありません。</div>";

        var items = string.Join("", s.Tools.Select(t =>
        {
            var selected = string.Equals(t.ToolId, selectedToolId, StringComparison.OrdinalIgnoreCase);
            var inUse = s.Profiles.Any(p => string.Equals(p.ToolId, t.ToolId, StringComparison.OrdinalIgnoreCase));
            var selectForm = $"<form class=\"app-select-form\" method=\"post\" action=\"{H(c.ActionEndpoint)}\">{FormHidden(c)}{Hidden("toolId", t.ToolId)}<button class=\"app-select {(selected ? "selected" : string.Empty)}\" type=\"submit\" name=\"action\" value=\"select-tool\" aria-label=\"{H(ToolDisplayName(t.Kind))}を使用\"><span class=\"radio-dot\"></span><span class=\"app-select-copy\"><span class=\"app-name-inline\">{H(ToolDisplayName(t.Kind))}</span><span class=\"app-path-inline mono\">{H(t.ExecutablePath)}</span></span></button></form>";
            var disabled = inUse ? " disabled title=\"変換設定で使用中です\"" : string.Empty;
            var deleteForm = $"<form class=\"app-delete-form\" method=\"post\" action=\"{H(c.ActionEndpoint)}\">{FormHidden(c)}{Hidden("toolId", t.ToolId)}<button class=\"button-danger small-action\" type=\"submit\" name=\"action\" value=\"remove-tool\"{disabled}>削除</button></form>";
            return $"<div class=\"app-line\"><div class=\"app-line-main\">{selectForm}</div><div class=\"app-line-action\">{deleteForm}</div></div>";
        }));
        return $"<div class=\"app-list\">{items}</div>";
    }

    private static string RenderRegisteredProfiles(RuntimeUiRenderContext c, EncodeBridgeStore s, string defaultProfileId, string editingProfileId, string editorMarkup)
    {
        if (s.Profiles.Count == 0)
            return "<div class=\"empty profile-empty\">プロファイルはまだありません。［＋ プロファイルを追加］から登録してください。</div>";

        var rows = string.Join("", s.Profiles.Select(p =>
        {
            var tool = s.Tools.FirstOrDefault(t => t.ToolId == p.ToolId);
            var displayName = ProfileDisplay.Label(s, p);
            var isDefault = string.Equals(p.ProfileId, defaultProfileId, StringComparison.OrdinalIgnoreCase);
            var batchState = p.PostEncodeBatchEnabled ? "バッチ ON" : "バッチ OFF";
            var outputState = p.OutputDirectoryMode == OutputDirectoryMode.Fixed ? "保存先 設定済み" : "保存先 元TSと同じ";
            var choose = $"<form method=\"post\" action=\"{H(c.ActionEndpoint)}\" class=\"profile-default-form\">{FormHidden(c)}{Hidden("profileId", p.ProfileId)}<label class=\"profile-default-label\" title=\"既定プロファイルに設定\"><input id=\"profile-default-radio-{H(p.ProfileId)}\" type=\"radio\" name=\"profile-choice-{H(defaultProfileId)}\" value=\"{H(p.ProfileId)}\" {(isDefault ? "checked" : string.Empty)} onchange=\"this.form.querySelector('button[type=submit]').click()\" aria-label=\"{H(displayName)}を既定のプロファイルに設定\"></label><button class=\"profile-default-submit\" type=\"submit\" name=\"action\" value=\"set-default-profile\" tabindex=\"-1\" aria-hidden=\"true\">適用</button></form>";
            var edit = $"<form method=\"post\" action=\"{H(c.ActionEndpoint)}\">{FormHidden(c)}{Hidden("profileId", p.ProfileId)}<button class=\"button-secondary small-action\" type=\"submit\" name=\"action\" value=\"edit-profile\">編集</button></form>";
            var remove = $"<form method=\"post\" action=\"{H(c.ActionEndpoint)}\">{FormHidden(c)}{Hidden("profileId", p.ProfileId)}<button class=\"button-danger small-action\" type=\"submit\" name=\"action\" value=\"remove-profile\">削除</button></form>";
            var expanded = string.Equals(p.ProfileId, editingProfileId, StringComparison.OrdinalIgnoreCase);
            return $"<div id=\"profile-line-{H(p.ProfileId)}\" class=\"profile-line {(isDefault ? "is-default" : string.Empty)}\"><div class=\"profile-title-row\">{choose}<b>{H(displayName)}</b><span id=\"profile-default-label-{H(p.ProfileId)}\" class=\"default-choice-label\"{(isDefault ? string.Empty : " hidden")}>既定</span><div class=\"profile-line-actions\">{edit}{remove}</div></div>{(expanded ? editorMarkup : string.Empty)}<div class=\"profile-extra\">{H(outputState)}　/　{H(batchState)}</div></div>";
        }));
        return $"<div class=\"profile-list\">{rows}</div>";
    }

    private static string RenderQueueRow(RuntimeUiRenderContext c, EncodeQueueItem q)
    {
        var command = q.State == EncodeQueueState.Running ? "stop" : "remove-waiting";
        var caption = q.State == EncodeQueueState.Running ? "停止" : "削除";
        var action = $"<form method=\"post\" action=\"{H(c.ActionEndpoint)}\">{FormHidden(c)}{Hidden("id", q.QueueItemId)}<button type=\"submit\" name=\"action\" value=\"{command}\">{caption}</button></form>";
        var label = q.State == EncodeQueueState.Running ? (q.PostEncodeBatchRunning ? "変換後処理中" : "処理中") : "待機";
        var reason = string.IsNullOrWhiteSpace(q.WaitingReason) ? string.Empty : $"<div class=\"reason\">{H(q.WaitingReason)}</div>";
        return $"<table class=\"item-row\"><tr><td><b>{label}</b><strong>{H(q.Recording.Title)}</strong><span>{H(q.Recording.ServiceName)}　{q.Recording.ScheduledStart:HH:mm}</span><span>{H(q.Profile.ProfileName)}</span>{reason}</td><td class=\"row-action\">{action}</td></tr></table>";
    }

    private static string RenderHistoryRow(RuntimeUiRenderContext c, EncodeHistoryItem h)
    {
        var label = h.Result switch
        {
            EncodeQueueState.HandedOff => "引き渡し済み",
            EncodeQueueState.Completed => "完了",
            EncodeQueueState.Failed => "失敗",
            EncodeQueueState.Stopped => "停止",
            _ => h.Result.ToString()
        };
        var retry = h.Result is EncodeQueueState.Failed or EncodeQueueState.Stopped
            ? $"<form method=\"post\" action=\"{H(c.ActionEndpoint)}\">{FormHidden(c)}{Hidden("id", h.QueueItemId)}<button type=\"submit\" name=\"action\" value=\"retry\">再実行</button></form>"
            : string.Empty;
        var failure = string.IsNullOrWhiteSpace(h.FailureMessage) ? string.Empty : $"<div class=\"reason\">{H(h.FailureMessage)}</div>";
        var remove = $"<form method=\"post\" action=\"{H(c.ActionEndpoint)}\">{FormHidden(c)}{Hidden("id", h.QueueItemId)}<button class=\"button-danger small-action\" type=\"submit\" name=\"action\" value=\"remove-history-item\">削除</button></form>";
        return $"<table class=\"item-row\"><tr><td><b>{label}</b><strong>{H(h.Title)}</strong><span>{H(h.ServiceName)}　{h.FinishedAt:MM/dd HH:mm}</span><span>{H(h.ProfileName)}</span>{failure}</td><td class=\"row-action\"><div class=\"history-row-actions\">{retry}{remove}</div></td></tr></table>";
    }

    private static string QualityPresetDisplayName(QualityPreset quality) => quality switch
    {
        QualityPreset.High => "高画質設定",
        QualityPreset.Compact => "少容量設定",
        _ => "標準設定"
    };

    private static string ToolDisplayName(EncodeToolKind kind) => kind switch
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

    private static string Get(IReadOnlyDictionary<string, string> p, string k) => p.TryGetValue(k, out var v) ? v : string.Empty;
    private static int GetInt(IReadOnlyDictionary<string, string> p, string k, int d) => int.TryParse(Get(p, k), out var n) ? n : d;
    private static bool GetBool(IReadOnlyDictionary<string, string> p, string k, bool d) => bool.TryParse(Get(p, k), out var b) ? b : d;
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    private static string BuildCss(RuntimeUiRenderContext c)
    {
        var dark = string.Equals(c.HostEffectiveTheme, "dark", StringComparison.OrdinalIgnoreCase);

        string Theme(string key, string fallback)
            => c.ThemeContract.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

        string ThemeAny(string fallback, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (c.ThemeContract.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return fallback;
        }

        // Host ThemeContract is the visual source of truth. Fallbacks are used only when
        // an older/partial Host does not provide the semantic role.
        var bg = ThemeAny(dark ? "#202124" : "#ffffff", "pageBackground", "background");
        var surface = ThemeAny(dark ? "#2b2d31" : "#f6f7f9", "surfaceBackground", "surface");
        var subtle = ThemeAny(surface, "subtleBackground", "subtle", "surfaceBackground", "surface");
        var input = ThemeAny(bg, "inputBackground", "input", "pageBackground", "background");
        var fg = ThemeAny(dark ? "#f1f3f4" : "#202124", "text", "foreground");
        var muted = ThemeAny(dark ? "#bdc1c6" : "#5f6368", "mutedText", "muted");
        var border = Theme("border", dark ? "#55585f" : "#c8ccd2");
        var accent = ThemeAny(dark ? "#4c8bf5" : "#1769d2", "accent", "primary");
        var accentText = ThemeAny("#ffffff", "accentText");
        var focus = ThemeAny(accent, "focus", "accent", "primary");
        var danger = ThemeAny(dark ? "#ff7b72" : "#b42318", "dangerActionText", "danger");

        var controlBg = ThemeAny(surface, "controlBackground", "secondaryActionBackground", "surfaceBackground", "surface");
        var controlHover = ThemeAny(subtle, "controlHover", "secondaryActionHover", "subtleBackground", "subtle");
        var controlText = ThemeAny(fg, "controlText", "secondaryActionText", "text", "foreground");
        var controlBorder = ThemeAny(border, "controlBorder", "border");

        var primaryBg = ThemeAny(accent, "primaryActionBackground", "accent", "primary");
        var primaryHover = ThemeAny(primaryBg, "primaryActionHover", "primaryActionBackground", "accent", "primary");
        var primaryText = ThemeAny(accentText, "primaryActionText", "accentText");
        var primaryBorder = ThemeAny(primaryBg, "primaryActionBorder", "primaryActionBackground", "accent", "primary");

        var secondaryBg = ThemeAny(controlBg, "secondaryActionBackground", "controlBackground", "surfaceBackground", "surface");
        var secondaryHover = ThemeAny(controlHover, "secondaryActionHover", "controlHover", "subtleBackground", "subtle");
        var secondaryText = ThemeAny(controlText, "secondaryActionText", "controlText", "text", "foreground");
        var secondaryBorder = ThemeAny(controlBorder, "secondaryActionBorder", "controlBorder", "border");

        var disabledBg = ThemeAny(subtle, "disabledBackground", "disabled", "subtleBackground", "subtle");
        var disabledText = ThemeAny(muted, "disabledText", "mutedText", "muted");
        var disabledBorder = ThemeAny(border, "disabledBorder", "border");
        var selected = ThemeAny(accent, "selected", "accent", "primary");
        var selectedHover = ThemeAny(selected, "selectedHover", "selected", "accent", "primary");
        var shadow = ThemeAny("none", "shadow", "controlShadow");

        return $@"
html,body{{margin:0;padding:0;background:{bg};color:{fg};font-family:'Segoe UI','Meiryo',sans-serif;font-size:14px;}}
.eb-root{{padding:18px 20px 28px 20px;background:{bg};color:{fg};min-height:100%;box-sizing:border-box;}}
.head-table{{width:100%;border-collapse:collapse;table-layout:fixed;}}
.head-table td{{vertical-align:top;padding:0;}}
.head-action{{width:250px;text-align:right;padding-top:2px!important;}}
.auto-toggle-form{{display:inline-block;margin:0;white-space:nowrap;}}
.auto-toggle-label{{display:inline-block;vertical-align:middle;margin-right:12px;font-weight:600;color:{fg};white-space:nowrap;}}
button.auto-switch{{display:inline-block;vertical-align:middle;width:50px;height:28px;min-height:28px;padding:0;background:transparent;border:0;box-shadow:none;cursor:pointer;}}
button.auto-switch:hover{{background:transparent;}}
.auto-toggle-track{{position:relative;display:inline-block;vertical-align:middle;width:46px;height:24px;border-radius:12px;background:{controlBg};border:1px solid {controlBorder};box-sizing:border-box;}}
.auto-toggle-thumb{{position:absolute;top:2px;left:2px;width:18px;height:18px;border-radius:50%;background:{controlText};box-shadow:{shadow};}}
button.auto-switch.is-on .auto-toggle-track{{background:{selected};border-color:{selected};}}
button.auto-switch.is-on .auto-toggle-thumb{{left:24px;background:{primaryText};}}
.auto-toggle-state{{display:inline-block;vertical-align:middle;margin-left:8px;min-width:24px;font-size:12px;font-weight:600;color:{controlText};}}
button.auto-switch:disabled{{cursor:default;opacity:.55;}}
button.auto-switch:disabled .auto-toggle-track{{background:{disabledBg};border-color:{disabledBorder};}}
button.auto-switch:disabled .auto-toggle-thumb{{background:{disabledText};}}
button.auto-switch:focus{{outline:1px solid {focus};outline-offset:2px;}}
h1{{margin:0 0 4px 0;font-size:25px;line-height:1.2;color:{fg};white-space:nowrap;}}
.app-version{{display:inline-block;margin-left:8px;font-size:14px;font-weight:600;color:{muted};vertical-align:baseline;white-space:nowrap;}}
h2{{margin:0 0 12px 0;font-size:17px;color:{fg};}}
.sub,.auto-hint,.item-row span{{color:{muted};}}
.auto-hint{{margin:8px 0 0 0;}}
.section{{margin-top:20px;}}
.summary{{width:100%;border-collapse:separate;border-spacing:7px;margin:14px -7px 0 -7px;table-layout:fixed;}}
.summary td{{background:{surface};border:1px solid {border};padding:12px 4px;text-align:center;vertical-align:middle;}}
.summary b{{display:block;font-size:21px;line-height:1.15;color:{fg};}}
.summary span{{display:block;margin-top:4px;color:{muted};white-space:nowrap;}}
button{{font-family:inherit;font-size:14px;font-weight:600;background:{secondaryBg};color:{secondaryText};border:1px solid {secondaryBorder};padding:6px 14px;min-height:32px;cursor:pointer;box-shadow:{shadow};}}
button:hover{{background:{secondaryHover};}}
button.commit.save,button.primary{{background:{primaryBg};color:{primaryText};border-color:{primaryBorder};}}
button.commit.save:hover,button.primary:hover{{background:{primaryHover};}}
button.commit.add,button.commit.set,button.browse{{background:{secondaryBg};color:{secondaryText};border-color:{secondaryBorder};}}
button.commit.add:hover,button.commit.set:hover,button.browse:hover{{background:{secondaryHover};}}
button:focus{{outline:2px solid {focus};outline-offset:1px;}}
button:active{{transform:translateY(1px);}}
// Disabled buttons must remain legible on the Host light theme; the Host disabledText role can be nearly white.
button:disabled{{color:{secondaryText};background:{disabledBg};border-color:{disabledBorder};cursor:default;opacity:1;box-shadow:none;}}
button.primary{{font-weight:600;min-width:128px;}}
.form-table{{width:100%;border-collapse:collapse;background:{surface};border:1px solid {border};table-layout:fixed;}}
.form-table th,.form-table td{{padding:8px 9px;border-bottom:1px solid {border};vertical-align:middle;text-align:left;}}
.form-table th{{width:145px;font-weight:600;color:{fg};white-space:nowrap;}}
.form-table tr:last-child th,.form-table tr:last-child td{{border-bottom:0;}}
.form-table input,.form-table select{{font-family:inherit;font-size:14px;background:{input};color:{fg};border:1px solid {border};padding:6px;box-sizing:border-box;min-height:30px;}}
.form-table input{{width:100%;}}
.form-table select{{min-width:150px;max-width:100%;}}
.form-table input.short{{width:90px;}}
.form-table input.path{{width:72%;margin-right:7px;}}
.form-table input.path-small{{width:64%;margin-right:7px;}}
.form-table input.path-readonly{{width:72%;margin-right:7px;font-family:Consolas,monospace;font-size:12px;}}
.form-table button.browse{{min-width:70px;}}
.path-state{{margin-top:6px;color:{muted};font-size:12px;word-break:break-all;}}
.path-display{{display:inline-block;vertical-align:middle;margin-left:10px;max-width:620px;}}
.check{{white-space:normal;}}
.check input{{width:auto!important;min-height:0!important;vertical-align:middle;}}
.empty,.notice{{border:1px solid {border};background:{surface};padding:12px;color:{muted};}}
.notice{{margin-top:12px;}}
.danger,.reason{{color:{danger};}}
.registered{{width:100%;border-collapse:collapse;margin-top:10px;table-layout:fixed;}}
.registered th,.registered td{{border:1px solid {border};padding:7px;text-align:left;vertical-align:top;word-wrap:break-word;}}
.registered th{{background:{surface};}}
.registered .mono,.mono{{font-family:Consolas,monospace;font-size:12px;}}
.registered th.index{{width:44px;text-align:center;}}
.choice-label{{margin:0 0 7px 0;font-weight:600;color:{fg};}}
.tool-choice-list{{border:1px solid {border};background:{surface};margin:0 0 10px 0;}}
.tool-choice{{display:block;padding:8px 10px;border-bottom:1px solid {border};cursor:pointer;white-space:normal;}}
.tool-choice:last-child{{border-bottom:0;}}
.tool-choice:hover{{background:{controlHover};}}
.tool-choice:focus-within{{outline:2px solid {focus};outline-offset:-2px;}}
.tool-choice input{{width:auto!important;min-height:0!important;margin:0 8px 0 0;vertical-align:middle;accent-color:{selected};}}
.tool-choice:hover input{{accent-color:{selectedHover};}}
.tool-choice-name{{display:inline-block;width:125px;font-weight:600;vertical-align:middle;}}
.tool-choice-path{{display:inline-block;width:calc(100% - 175px);word-break:break-all;color:{muted};vertical-align:middle;}}
.section-note{{margin:-4px 0 10px 0;color:{muted};line-height:1.55;}}
.settings-card{{border:1px solid {border};background:{surface};}}
.setting-row{{display:table;width:100%;table-layout:fixed;border-bottom:1px solid {border};}}
.setting-label,.setting-control{{display:table-cell;padding:10px 12px;vertical-align:middle;}}
.setting-label{{width:138px;font-weight:600;color:{fg};}}
.setting-control select,.setting-control input{{font-family:inherit;font-size:14px;background:{input};color:{fg};border:1px solid {border};padding:7px 8px;box-sizing:border-box;min-height:32px;}}
.setting-control select{{min-width:190px;}}
.path-control{{white-space:nowrap;}}
.path-control input.path-readonly{{width:calc(100% - 92px);margin-right:8px;font-family:Consolas,monospace;font-size:12px;}}
.settings-actions{{padding:10px 12px;text-align:right;}}
.button-primary{{background:{primaryBg};color:{primaryText};border-color:{primaryBorder};}}
.button-primary:hover{{background:{primaryHover};}}
.button-secondary{{background:{secondaryBg};color:{secondaryText};border-color:{secondaryBorder};}}
.button-secondary:hover{{background:{secondaryHover};}}
// Destructive actions use a secondary-action surface: pair it with the Host's secondary-action text.
// dangerActionText is intended for a contrasting danger-action surface and may be white on light themes.
.button-danger{{background:{secondaryBg};color:{secondaryText};border-color:{secondaryBorder};}}
.button-danger:hover{{background:{secondaryHover};color:{secondaryText};}}
button.button-danger:disabled,button.button-danger:disabled:hover{{color:{secondaryText};background:{disabledBg};border-color:{disabledBorder};cursor:default;}}
.small-action{{min-height:28px;padding:4px 11px;font-size:13px;}}
.registered-block{{margin-top:14px;}}
.registered-heading{{font-weight:600;margin:0 0 7px 0;color:{fg};}}
.app-list{{border:1px solid {border};background:{surface};}}
.app-item{{display:table;width:100%;table-layout:fixed;border-bottom:1px solid {border};}}
.app-item:last-child{{border-bottom:0;}}
.app-main,.app-action{{display:table-cell;padding:10px 12px;vertical-align:middle;}}
.app-main{{width:auto;}}
.app-action{{width:128px;text-align:right;}}
.app-name{{font-weight:600;color:{fg};margin-bottom:4px;}}
.app-path{{color:{muted};word-break:break-all;}}
.status-badge{{display:inline-block;padding:4px 8px;border:1px solid {border};background:{subtle};color:{muted};font-size:12px;white-space:nowrap;}}
.compact-empty{{margin-top:0;}}
.row-buttons{{display:flex;gap:6px;align-items:center;flex-wrap:wrap;}}
.row-buttons form{{margin:0;}}
.item-row{{width:100%;border-collapse:collapse;background:{surface};border:1px solid {border};margin-bottom:8px;table-layout:fixed;}}
.item-row td{{padding:10px;vertical-align:middle;}}
.item-row td:first-child{{width:auto;}}
.item-row td.row-action{{width:92px;text-align:right;}}
.item-row b{{display:inline-block;min-width:72px;margin-right:8px;color:{fg};}}
.item-row strong{{display:block;font-size:15px;margin:4px 0;color:{fg};}}
.item-row span{{display:block;margin-top:2px;}}
.basic-card{{border:1px solid {border};background:{surface};padding:10px 12px;}}
.basic-grid{{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px 16px;}}
.basic-field{{display:block;min-width:0;font-weight:600;}}
.field-label{{display:block;margin:0 0 4px 0;font-weight:600;color:{fg};line-height:1.35;}}
.field-control{{display:flex;align-items:center;width:100%;min-width:0;gap:8px;}}
.field-control input,.field-control select{{font-family:inherit;font-size:14px;background:{input};color:{fg};border:1px solid {border};padding:7px 8px;box-sizing:border-box;min-height:32px;}}
.field-control input,.field-control select{{width:100%;min-width:0;}}
.compact-control{{justify-content:flex-start;}}
.compact-control input{{width:84px;text-align:right;}}
.field-unit{{display:inline-block;min-width:28px;text-align:left;font-weight:600;white-space:nowrap;}}
.basic-field input{{font-family:inherit;font-size:14px;background:{input};color:{fg};border:1px solid {border};padding:6px;box-sizing:border-box;min-height:30px;}}
.basic-checks{{display:block;margin-top:10px;padding-top:8px;border-top:1px solid {border};}}
.basic-checks .check{{display:block;padding:4px 0;}}
.basic-actions{{text-align:right;margin-top:10px;}}
.app-register-row{{display:grid;grid-template-columns:150px minmax(0,1fr) auto auto;gap:8px;align-items:center;border:1px solid {border};background:{surface};padding:10px;}}
.app-register-row select,.app-register-row input{{font-family:inherit;font-size:14px;background:{input};color:{fg};border:1px solid {border};padding:7px 8px;box-sizing:border-box;min-height:32px;}}
.app-register-row .path-readonly{{width:100%;font-family:Consolas,monospace;font-size:12px;}}
.app-list{{border:1px solid {border};background:{surface};}}
.app-line{{display:grid;grid-template-columns:minmax(0,1fr) 82px;align-items:stretch;border-bottom:1px solid {border};}}
.app-line:last-child{{border-bottom:0;}}
.app-line-main{{min-width:0;}}
.app-select-form,.app-delete-form{{margin:0;height:100%;}}
button.app-select{{width:100%;height:100%;min-height:50px;display:grid;grid-template-columns:18px minmax(0,1fr);gap:9px;align-items:center;text-align:left;background:transparent;color:{fg};border:0;padding:7px 10px;font-weight:400;}}
.app-select-copy{{display:block;min-width:0;}}
.app-select-copy .app-name-inline{{display:block;line-height:1.35;}}
.app-select-copy .app-path-inline{{display:block;margin-top:2px;font-size:12px;line-height:1.3;}}
button.app-select:hover{{background:{controlHover};}}
.app-line-action{{display:flex;align-items:center;justify-content:flex-end;padding:6px 8px;}}
.radio-dot{{display:inline-block;width:12px;height:12px;border-radius:50%;border:2px solid {muted};box-sizing:border-box;position:relative;}}
.selected .radio-dot:after{{content:'';position:absolute;inset:2px;border-radius:50%;background:{selected};}}
.app-name-inline{{font-weight:600;color:{fg};white-space:nowrap;}}
.app-path-inline{{color:{muted};overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}}
.profile-card{{border:1px solid {border};background:{surface};padding:10px 12px;}}
.app-config-card{{margin-bottom:10px;}}
.profile-settings-card{{margin-top:0;}}
.unified-config-card{{margin-top:0;}}
.profile-fields{{display:block;}}
.profile-field{{display:block;min-width:0;margin:0;padding:0;}}
.profile-field+.profile-field{{padding-top:10px;}}
.profile-field>.field-label{{display:block;margin:0 0 4px 0;padding:0;}}
.profile-field>.field-control{{margin:0;}}
.profile-field .field-control select,.profile-field .field-control .path-readonly,.profile-field .field-control textarea{{width:100%;min-width:0;}}
.batch-field-control{{display:block;width:100%;min-width:0;}}
.batch-script{{display:block;width:100%;max-width:none;margin-top:8px;font-family:Consolas,monospace;font-size:12px;background:{input};color:{fg};border:1px solid {border};padding:8px;box-sizing:border-box;resize:vertical;min-height:92px;line-height:1.45;}}
.field-help{{display:block;margin-top:5px;font-size:12px;color:{muted};line-height:1.4;}}
.batch-enable{{display:block;margin:8px 0 0 0;font-weight:600;line-height:20px;}}
.batch-enable input[type=checkbox]{{width:auto;min-width:0;margin:0 6px 0 0;padding:0;vertical-align:middle;}}
.batch-risk-note{{display:block;margin:0 0 8px 0;font-size:12px;color:{muted};line-height:1.45;}}
.batch-consent{{display:block;margin:9px 0 0 0;padding:9px 10px;border:1px solid {border};background:{subtle};color:{fg};font-size:12px;line-height:1.5;}}
.batch-consent b{{display:block;margin-bottom:3px;font-size:13px;}}
.profile-field .path-readonly{{font-family:Consolas,monospace;font-size:12px;}}
.field-control-with-action{{display:grid;grid-template-columns:minmax(0,1fr) 72px;gap:8px;align-items:center;}}
.field-control-with-action button{{width:72px;min-width:72px;box-sizing:border-box;white-space:nowrap;word-break:keep-all;overflow-wrap:normal;}}
.profile-actions{{display:flex;justify-content:flex-end;gap:8px;margin-top:14px;}}
.section-heading-row{{display:flex;align-items:flex-end;justify-content:space-between;gap:16px;margin-bottom:8px;}}
.section-heading-row h2{{margin-bottom:2px;}}
.section-help{{font-size:12px;color:{muted};}}
.profile-editor-shell{{margin:10px 0;border:1px solid {border};background:{subtle};padding:12px;animation:eb-shutter .2s ease-out;transform-origin:top;}}
@keyframes eb-shutter{{from{{opacity:0;transform:scaleY(.94);}}to{{opacity:1;transform:scaleY(1);}}}}
.profile-editor-title{{font-weight:700;margin-bottom:8px;}}
.editor-group-title{{font-weight:700;margin:12px 0 8px;padding-top:10px;border-top:1px solid {border};}}
.profile-pair-grid{{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px;}}
.profile-pair-grid .profile-field+.profile-field{{padding-top:0;}}
.batch-section{{margin-top:4px;padding-top:12px!important;border-top:1px solid {border};}}
.profile-empty{{margin-top:4px;}}
.profile-extra{{font-size:12px;color:{muted};padding:2px 0 4px 29px;}}
.history-row-actions{{display:flex;gap:14px;justify-content:flex-end;align-items:center;}}
.history-clear-footer{{display:flex;justify-content:flex-end;margin-top:14px;}}
.history-clear-confirm{{display:flex;gap:12px;align-items:center;flex-wrap:wrap;justify-content:flex-end;}}
.saved-heading{{font-weight:600;margin:12px 0 7px;}}
.profile-list{{border:1px solid {border};background:{surface};}}
.profile-line{{display:block;border-bottom:1px solid {border};padding:7px 10px;min-height:52px;}}
.profile-line.is-default{{background:{controlHover};}}
.profile-line:last-child{{border-bottom:0;}}
.profile-default-form{{display:flex;align-items:center;}}
.profile-default-label{{display:inline-flex;align-items:center;justify-content:center;cursor:pointer;min-height:22px;min-width:24px;}}
.profile-default-label input[type=radio]{{appearance:auto;width:16px;min-width:16px;height:16px;margin:0;cursor:pointer;accent-color:{selected};}}
.profile-default-submit{{position:absolute!important;width:1px!important;height:1px!important;padding:0!important;margin:0!important;border:0!important;overflow:hidden!important;clip:rect(0,0,0,0)!important;}}
.default-choice-label{{flex:0 0 auto;font-size:11px;font-weight:600;color:{fg};border:1px solid {border};border-radius:3px;padding:1px 5px;}}
.default-choice-label[hidden]{{display:none!important;}}
.profile-summary{{min-width:0;padding:8px 4px;display:flex;flex-direction:column;gap:3px;align-items:flex-start;}}
.profile-title-row{{display:flex;align-items:center;gap:9px;min-width:0;}}
.profile-title-row form{{margin:0;display:flex;align-items:center;flex:0 0 auto;}}
.profile-title-row b{{min-width:0;overflow-wrap:anywhere;}}
.profile-summary span{{color:{muted};font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:100%;}}
.profile-extra{{font-size:12px;}}
.profile-line-actions{{display:flex;gap:14px;align-items:center;margin-left:auto;padding:2px 0 2px 12px;flex-shrink:0;}}
@media (max-width:720px){{.basic-grid{{grid-template-columns:1fr}}.profile-pair-grid{{grid-template-columns:1fr}}.section-heading-row{{align-items:flex-start;flex-direction:column}}}}

";
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

internal static class EncodeBridgeUiDraft
{
    private static readonly object Gate = new();
    private sealed class WindowDraft
    {
        public Dictionary<string, string> Tool { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Profile { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool PostBatchConsentPending { get; set; }
        public string ValidationError { get; set; } = string.Empty;
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

    public static bool ToolPathPresent(string windowId)
    {
        lock (Gate)
        {
            var tool = GetWindow(windowId, false).Tool;
            return tool.TryGetValue("path", out var value) && !string.IsNullOrWhiteSpace(value);
        }
    }

    public static bool ProfileOutputPresent(string windowId)
    {
        lock (Gate)
        {
            var profile = GetWindow(windowId, false).Profile;
            return profile.TryGetValue("outputDir", out var value) && !string.IsNullOrWhiteSpace(value);
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
        lock (Gate) GetWindow(windowId, true).Profile["outputDir"] = path?.Trim() ?? string.Empty;
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
            draft.PostBatchConsentPending = false;
            draft.ValidationError = string.Empty;
            Cleanup(windowId, draft);
        }
    }

    private static void Cleanup(string windowId, WindowDraft draft)
    {
        if (draft.Tool.Count == 0 && draft.Profile.Count == 0 && !draft.PostBatchConsentPending && string.IsNullOrEmpty(draft.ValidationError))
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
            || k.Equals("RouteSegment", StringComparison.OrdinalIgnoreCase)
            || k.Equals("responseMode", StringComparison.OrdinalIgnoreCase)
            || k.Equals("safeEvent", StringComparison.OrdinalIgnoreCase)
            || k.Equals("safeEventAction", StringComparison.OrdinalIgnoreCase)
            || k.Equals("safeEventInteractionId", StringComparison.OrdinalIgnoreCase)
            || k.Equals("safeEventSource", StringComparison.OrdinalIgnoreCase)
            || k.Equals("refresh-target", StringComparison.OrdinalIgnoreCase)
            || k.Equals("refreshTarget", StringComparison.OrdinalIgnoreCase)
            || k.Equals("preserve-scroll", StringComparison.OrdinalIgnoreCase)
            || k.Equals("preserveScroll", StringComparison.OrdinalIgnoreCase)
            || k.Equals("feedbackRequested", StringComparison.OrdinalIgnoreCase);
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
