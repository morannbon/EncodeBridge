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
        Version = "1.0.2",
        SdkContractVersion = "1.1.16",
        RequiredPermissions = BuildRequiredPermissions(),
        Windows = new[]
        {
            new PluginWindowDefinition
            {
                WindowDefinitionId = WindowId,
                Title = "EncodeBridge v1.0.2",
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
                    "toggle-auto", "save-basic", "pick-tool-path", "pick-output-dir", "pick-tmpg-template",
                    "add-tool", "select-tool", "remove-tool", "new-profile", "use-registered-tool",
                    "add-profile", "edit-profile", "save-profile", "confirm-post-batch-save", "cancel-post-batch-save", "cancel-edit-profile", "remove-profile", "set-default-profile",
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
        var configured = EncodeProfilePolicy.IsAutoConfigurationReady(s);

        var toolDraft = EncodeBridgeUiDraft.SnapshotTool(context.CurrentWindowId);
        var profileDraft = EncodeBridgeUiDraft.SnapshotProfile(context.CurrentWindowId);
        var toolPath = Draft(toolDraft, "path");
        var outputDir = Draft(profileDraft, "outputDir");
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_RENDER_DRAFT window={context.CurrentWindowId} toolPathPresent={!string.IsNullOrWhiteSpace(toolPath)} profileOutputPresent={!string.IsNullOrWhiteSpace(outputDir)}");
#endif

        var rows = string.Join("", s.Queue.OrderBy(x => x.AddedAt).Select(q => RenderQueueRow(context, q)));
        var historyRows = string.Join("", s.History.OrderByDescending(x => x.FinishedAt).Take(30).Select(h => RenderHistoryRow(context, h)));
        var defaultRule = EncodeProfilePolicy.EffectiveDefaultRule(s);
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
        var activeTool = EncodeProfilePolicy.ResolveTool(s, activeProfile?.ToolId);
        var selectedDraftTool = EncodeProfilePolicy.ResolveTool(s, Draft(profileDraft, "toolId"));
        var toolKindValue = FirstNonBlank(Draft(toolDraft, "kind"), selectedDraftTool?.Kind.ToString() ?? activeTool?.Kind.ToString() ?? EncodeBridgeDefaults.DefaultToolKind.ToString());
        var toolPathValue = FirstNonBlank(toolPath, selectedDraftTool?.ExecutablePath ?? activeTool?.ExecutablePath ?? string.Empty);
        var profileVideoValue = FirstNonBlank(Draft(profileDraft, "video"), activeProfile?.VideoCodec.ToString() ?? EncodeBridgeDefaults.DefaultVideoCodec.ToString());
        var profileQualityValue = FirstNonBlank(Draft(profileDraft, "quality"), activeProfile?.Quality.ToString() ?? EncodeBridgeDefaults.DefaultQuality.ToString());
        var profileContainerValue = FirstNonBlank(Draft(profileDraft, "container"), activeProfile?.Container ?? EncodeBridgeDefaults.DefaultContainer);
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
        
        var availableToolOptions = string.Join("", s.Tools.Where(t => File.Exists(t.ExecutablePath)).Select(t => $"<option value=\"{H(t.ToolId)}\"{Selected(Draft(profileDraft, "toolId"), t.ToolId)}>{H(ProfileDisplay.ToolName(t.Kind))}</option>"));
        var existingToolChooser = s.Tools.Count == 0 ? string.Empty : $"<div class=\"profile-field\"><label class=\"field-label\" for=\"existing-tool\">登録済みの実行ファイルを使用</label><div class=\"field-control field-control-with-action\"><select id=\"existing-tool\" name=\"existingToolId\"><option value=\"\">選択してください</option>{availableToolOptions}</select><button class=\"button-secondary\" type=\"submit\" name=\"action\" value=\"use-registered-tool\">選択</button></div></div>";
        var draftError = EncodeBridgeUiDraft.GetValidationError(context.CurrentWindowId);
        var error = !rt.StorageHealthy
            ? $"<div class=\"notice danger\">{H(rt.StorageError)}</div>"
            : string.IsNullOrWhiteSpace(rt.FileJournalError) ? string.Empty : $"<div class=\"notice danger\">{H(rt.FileJournalError)}</div>";
        var profileError = editingProfile && !string.IsNullOrWhiteSpace(draftError)
            ? $"<div class=\"notice danger\" role=\"alert\">{H(draftError)}</div>"
            : string.Empty;
        var css = EncodeBridgeStyles.BuildCss(context);
        var formHead = FormHidden(context);
        var editorMarkup = editingProfile ? $"""
<div class="profile-editor-shell">
{profileError}
<div class="profile-editor-title">{(creatingNewProfile ? "新しいプロファイル" : $"プロファイルを編集　{H(ProfileDisplay.Name(activeTool?.Kind ?? EncodeBridgeDefaults.DefaultToolKind, activeProfile?.Quality ?? EncodeBridgeDefaults.DefaultQuality, activeProfile?.VideoCodec ?? EncodeBridgeDefaults.DefaultVideoCodec, activeProfile?.Container ?? EncodeBridgeDefaults.DefaultContainer))}")}</div>
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
<td><h1>EncodeBridge <span class="app-version">v1.0.2</span></h1><div class="sub">録画終了後の変換を外部ツールへ引き渡します。</div></td>
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
<label class="basic-field"><span class="field-label">同時に変換</span><span class="field-control compact-control"><input name="maxConcurrent" type="number" min="{EncodeBridgeDefaults.MinConcurrentEncodes}" max="{EncodeBridgeDefaults.MaxConcurrentEncodesLimit}" value="{s.Settings.MaxConcurrentEncodes}"><span class="field-unit">件</span></span></label>
<label class="basic-field"><span class="field-label">最低空き容量</span><span class="field-control compact-control"><input name="minFreeGb" type="number" min="{EncodeBridgeDefaults.MinFreeSpaceGb}" value="{s.Settings.MinimumFreeSpaceGb}"><span class="field-unit">GB</span></span></label>
<label class="basic-field"><span class="field-label">履歴保持</span><span class="field-control compact-control"><input name="historyDays" type="number" min="{EncodeBridgeDefaults.MinHistoryRetentionDays}" max="{EncodeBridgeDefaults.MaxHistoryRetentionDays}" value="{s.Settings.HistoryRetentionDays}"><span class="field-unit">日</span></span></label>
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
                    if (enable && !EncodeProfilePolicy.IsAutoConfigurationReady(rt.Store))
                        return RuntimeUiActionResult.Fail("configuration_required", "先に変換ツールと変換設定を登録してください。");
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    var previousAutoEncodeEnabled = rt.Store.Settings.AutoEncodeEnabled;
#endif
                    rt.Store.Settings.AutoEncodeEnabled = enable;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
                    rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_AUTO_STATE result=CHANGED previous={previousAutoEncodeEnabled} current={enable} source=user_toggle");
#endif
                    rt.Save();
                    rt.PublishProfileSelections();
                    break;
                }
                case "save-basic":
                    rt.Store.Settings.MaxConcurrentEncodes = Math.Clamp(GetInt(p, "maxConcurrent", EncodeBridgeDefaults.MaxConcurrentEncodes), EncodeBridgeDefaults.MinConcurrentEncodes, EncodeBridgeDefaults.MaxConcurrentEncodesLimit);
                    rt.Store.Settings.MinimumFreeSpaceGb = Math.Max(EncodeBridgeDefaults.MinFreeSpaceGb, GetInt(p, "minFreeGb", EncodeBridgeDefaults.MinimumFreeSpaceGb));
                    rt.Store.Settings.HistoryRetentionDays = Math.Clamp(GetInt(p, "historyDays", EncodeBridgeDefaults.HistoryRetentionDays), EncodeBridgeDefaults.MinHistoryRetentionDays, EncodeBridgeDefaults.MaxHistoryRetentionDays);
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
                        ["video"] = EncodeBridgeDefaults.DefaultVideoCodec.ToString(),
                        ["quality"] = EncodeBridgeDefaults.DefaultQuality.ToString(),
                        ["container"] = EncodeBridgeDefaults.DefaultContainer,
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
                    var before = EncodeProfilePolicy.EffectiveDefaultRule(rt.Store)?.ProfileId ?? string.Empty;
                    EncodeProfilePolicy.SelectDefault(rt.Store, requestedId);
                    rt.Save();
                    var after = EncodeProfilePolicy.EffectiveDefaultRule(rt.Store)?.ProfileId ?? string.Empty;
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
                case "remove-waiting": return QueueCommandUiResult(rt.Remove(Get(p, "id")));
                case "stop": return QueueCommandUiResult(rt.Stop(Get(p, "id")));
                case "retry": return QueueCommandUiResult(rt.Retry(Get(p, "id")));
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

    // The queue command is not successful merely because the handler ran.
    // Refresh even on rejection so the queue/history always reflects the true state.
    private static RuntimeUiActionResult QueueCommandUiResult(EncodeQueueCommandResult command) => new()
    {
        Succeeded = command.Succeeded,
        ErrorCode = command.Succeeded ? string.Empty : "queue_action_not_completed",
        Message = command.Message,
        RefreshRequested = true,
        RefreshTarget = "content",
        PreserveScroll = true
    };

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

    private static EncodeToolKind ParseToolKind(string value)
    {
        if (!Enum.TryParse<EncodeToolKind>(value, true, out var kind) || !Enum.IsDefined(kind))
            throw new InvalidOperationException("変換ツールの種類を確認してください。");
        return kind;
    }

    private static string ValidateExecutablePath(string path)
    {
        path = path.Trim();
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("実行ファイルを指定してください。");
        if (!File.Exists(path))
            throw new InvalidOperationException("指定した実行ファイルが見つかりません。");
        return path;
    }

    private static EncodeTool CreateTool(EncodeToolKind kind, string path) => new()
    {
        Kind = kind,
        DisplayName = ProfileDisplay.ToolName(kind),
        ExecutablePath = path
    };

    private static EncodeTool AddTool(EncodeBridgeRuntime rt, IReadOnlyDictionary<string, string> values)
    {
        var kind = ParseToolKind(FirstNonBlank(Get(values, "kind"), EncodeBridgeDefaults.DefaultToolKind.ToString()));
        var path = ValidateExecutablePath(Get(values, "path"));
        var tool = CreateTool(kind, path);
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
        var selectedTool = EncodeProfilePolicy.ResolveTool(rt.Store, existingId);
        var kind = ParseToolKind(FirstNonBlank(Get(toolDraft, "kind"), selectedTool?.Kind.ToString() ?? EncodeBridgeDefaults.DefaultToolKind.ToString()));
        var path = ValidateExecutablePath(FirstNonBlank(Get(toolDraft, "path"), selectedTool?.ExecutablePath ?? string.Empty));

        var existing = rt.Store.Tools.FirstOrDefault(x => x.Kind == kind && string.Equals(x.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            profileValues["toolId"] = existing.ToolId;
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
            rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_PROFILE_TOOL_BIND result=REUSED toolId={existing.ToolId} kind={kind} source=draft");
#endif
            return false;
        }

        var tool = CreateTool(kind, path);
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

    private readonly record struct ValidatedProfileEdit(
        EncodeTool Tool, VideoCodecMode Video, QualityPreset Quality, string Container,
        string OutputDirectory, string PostEncodeBatch, bool PostEncodeBatchEnabled, bool PostEncodeBatchRiskAccepted);

    // Validate the complete edit before changing the stored profile. New and existing
    // profiles use identical parsing, path checks and output/batch normalization.
    private static ValidatedProfileEdit ValidateProfileEdit(EncodeBridgeRuntime rt,
        IReadOnlyDictionary<string, string> values, VideoCodecMode fallbackVideo, QualityPreset fallbackQuality)
    {
        var tool = EncodeProfilePolicy.ResolveTool(rt.Store, Get(values, "toolId"))
            ?? throw new InvalidOperationException("変換ツールを選択してください。");
        var videoText = FirstNonBlank(Get(values, "video"), fallbackVideo.ToString());
        if (!Enum.TryParse<VideoCodecMode>(videoText, true, out var video) || !Enum.IsDefined(video))
            throw new InvalidOperationException("映像設定を確認してください。");
        var qualityText = FirstNonBlank(Get(values, "quality"), fallbackQuality.ToString());
        if (!Enum.TryParse<QualityPreset>(qualityText, true, out var quality) || !Enum.IsDefined(quality))
            throw new InvalidOperationException("品質設定を確認してください。");

        var output = Get(values, "outputDir").Trim();
        if (!string.IsNullOrWhiteSpace(output) && !Directory.Exists(output))
            throw new InvalidOperationException("指定した保存先が見つかりません。");
        return new ValidatedProfileEdit(
            tool, video, quality, NormalizeContainer(Get(values, "container")), output,
            ValidatePostEncodeBatch(Get(values, "postEncodeBatch")),
            GetBool(values, "postEncodeBatchEnabled", false),
            GetBool(values, "postEncodeBatchRiskAccepted", false));
    }

    private static void ApplyProfileEdit(EncodeProfile profile, ValidatedProfileEdit edit, bool retainAcceptedRisk)
    {
        var previouslyAccepted = retainAcceptedRisk && profile.PostEncodeBatchEnabled && profile.PostEncodeBatchRiskAccepted;
        profile.Name = ProfileDisplay.Name(edit.Tool.Kind, edit.Quality, edit.Video, edit.Container);
        profile.ToolId = edit.Tool.ToolId;
        profile.VideoCodec = edit.Video;
        profile.Quality = edit.Quality;
        profile.Container = edit.Container;
        profile.OutputDirectoryMode = string.IsNullOrWhiteSpace(edit.OutputDirectory)
            ? OutputDirectoryMode.SameAsSource : OutputDirectoryMode.Fixed;
        profile.OutputDirectory = edit.OutputDirectory;
        profile.PostEncodeBatch = edit.PostEncodeBatch;
        profile.PostEncodeBatchEnabled = edit.PostEncodeBatchEnabled;
        profile.PostEncodeBatchRiskAccepted = edit.PostEncodeBatchEnabled &&
            (edit.PostEncodeBatchRiskAccepted || previouslyAccepted);
    }

    private static void AddProfile(EncodeBridgeRuntime rt, IReadOnlyDictionary<string, string> values)
    {
        var edit = ValidateProfileEdit(rt, values, EncodeBridgeDefaults.DefaultVideoCodec, EncodeBridgeDefaults.DefaultQuality);
        var profile = new EncodeProfile();
        ApplyProfileEdit(profile, edit, retainAcceptedRisk: false);
        rt.Store.Profiles.Add(profile);
        EncodeProfilePolicy.EnsureInitialDefault(rt.Store, profile);
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
        var profile = rt.Store.Profiles.FirstOrDefault(x =>
            string.Equals(x.ProfileId, Get(values, "profileId"), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("変換設定を確認してください。");
        var edit = ValidateProfileEdit(rt, values, profile.VideoCodec, profile.Quality);
        var previousToolId = profile.ToolId;
        ApplyProfileEdit(profile, edit, retainAcceptedRisk: true);
        if (!string.Equals(previousToolId, profile.ToolId, StringComparison.OrdinalIgnoreCase))
        {
            profile.AdapterSettings.Clear();
            PruneUnusedTools(rt);
        }
#if ENCODEBRIDGE_DEVELOPER_DIAGNOSTICS
        rt.WriteDeveloperDiagnostic($"ENCODEBRIDGE_PROFILE_SAVE result=OK profileId={profile.ProfileId} toolChanged={!string.Equals(previousToolId, profile.ToolId, StringComparison.OrdinalIgnoreCase)} toolId={profile.ToolId} postBatchEnabled={profile.PostEncodeBatchEnabled} postBatchTextBlank={string.IsNullOrWhiteSpace(profile.PostEncodeBatch)} postBatchRiskAccepted={profile.PostEncodeBatchRiskAccepted} postBatchEffective={profile.PostEncodeBatchEnabled && !string.IsNullOrWhiteSpace(profile.PostEncodeBatch) && profile.PostEncodeBatchRiskAccepted}");
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
        var container = string.IsNullOrWhiteSpace(value) ? EncodeBridgeDefaults.DefaultContainer : value.Trim().ToLowerInvariant();
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

    private static string RenderRegisteredProfiles(RuntimeUiRenderContext c, EncodeBridgeStore s, string defaultProfileId, string editingProfileId, string editorMarkup)
    {
        if (s.Profiles.Count == 0)
            return "<div class=\"empty profile-empty\">プロファイルはまだありません。［＋ プロファイルを追加］から登録してください。</div>";

        var rows = string.Join("", s.Profiles.Select(p =>
        {
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
            EncodeQueueState.Unverified => "結果未確認",
            _ => h.Result.ToString()
        };
        var retry = h.Result is EncodeQueueState.Failed or EncodeQueueState.Stopped or EncodeQueueState.Unverified
            ? $"<form method=\"post\" action=\"{H(c.ActionEndpoint)}\">{FormHidden(c)}{Hidden("id", h.QueueItemId)}<button type=\"submit\" name=\"action\" value=\"retry\">再実行</button></form>"
            : string.Empty;
        var failure = string.IsNullOrWhiteSpace(h.FailureMessage) ? string.Empty : $"<div class=\"reason\">{H(h.FailureMessage)}</div>";
        var remove = $"<form method=\"post\" action=\"{H(c.ActionEndpoint)}\">{FormHidden(c)}{Hidden("id", h.QueueItemId)}<button class=\"button-danger small-action\" type=\"submit\" name=\"action\" value=\"remove-history-item\">削除</button></form>";
        return $"<table class=\"item-row\"><tr><td><b>{label}</b><strong>{H(h.Title)}</strong><span>{H(h.ServiceName)}　{h.FinishedAt:MM/dd HH:mm}</span><span>{H(h.ProfileName)}</span>{failure}</td><td class=\"row-action\"><div class=\"history-row-actions\">{retry}{remove}</div></td></tr></table>";
    }

    private static string Get(IReadOnlyDictionary<string, string> p, string k) => p.TryGetValue(k, out var v) ? v : string.Empty;
    private static int GetInt(IReadOnlyDictionary<string, string> p, string k, int d) => int.TryParse(Get(p, k), out var n) ? n : d;
    private static bool GetBool(IReadOnlyDictionary<string, string> p, string k, bool d) => bool.TryParse(Get(p, k), out var b) ? b : d;
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

}
