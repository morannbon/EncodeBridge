using System;
using TvAIrPlugin.Runtime;

namespace EncodeBridge;

// Shared window styling; values are projected from Host theme roles.
internal static class EncodeBridgeStyles
{
    public static string BuildCss(RuntimeUiRenderContext c)
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

        // Host theme roles are authoritative; fallback tokens apply only when a
        // semantic role is unavailable.
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
.head-table td.head-action{{width:250px;text-align:right;padding-top:2px;}}
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
button:disabled{{color:{secondaryText};background:{disabledBg};border-color:{disabledBorder};cursor:default;opacity:1;box-shadow:none;}}
button.primary{{font-weight:600;min-width:128px;}}
.check{{white-space:normal;}}
.check input{{width:auto;min-height:0;vertical-align:middle;}}
.empty,.notice{{border:1px solid {border};background:{surface};padding:12px;color:{muted};}}
.notice{{margin-top:12px;}}
.danger,.reason{{color:{danger};}}
.choice-label{{margin:0 0 7px 0;font-weight:600;color:{fg};}}
.button-primary{{background:{primaryBg};color:{primaryText};border-color:{primaryBorder};}}
.button-primary:hover{{background:{primaryHover};}}
.button-secondary{{background:{secondaryBg};color:{secondaryText};border-color:{secondaryBorder};}}
.button-secondary:hover{{background:{secondaryHover};}}
.button-danger{{background:{secondaryBg};color:{secondaryText};border-color:{secondaryBorder};}}
.button-danger:hover{{background:{secondaryHover};color:{secondaryText};}}
button.button-danger:disabled,button.button-danger:disabled:hover{{color:{secondaryText};background:{disabledBg};border-color:{disabledBorder};cursor:default;}}
.small-action{{min-height:28px;padding:4px 11px;font-size:13px;}}
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
.profile-card{{border:1px solid {border};background:{surface};padding:10px 12px;}}
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
.profile-field-error{{display:block;margin:5px 0 0;font-size:12px;font-weight:700;color:{danger};line-height:1.5;overflow-wrap:anywhere;}}
.output-mode-choices{{display:flex;flex-wrap:wrap;gap:6px 16px;margin:2px 0 9px;line-height:1.5;}}
.output-mode-choices label{{display:inline-flex;align-items:center;gap:5px;cursor:pointer;min-height:25px;}}
.output-mode-choices input[type=radio]{{width:16px;min-width:16px;height:16px;margin:0;accent-color:{selected};}}
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
.profile-field.batch-section{{margin-top:4px;padding-top:12px;border-top:1px solid {border};}}
.profile-empty{{margin-top:4px;}}
.profile-extra{{font-size:12px;color:{muted};padding:2px 0 4px 29px;}}
.profile-extra-warning{{color:{danger};font-weight:700;}}
.history-row-actions{{display:flex;gap:14px;justify-content:flex-end;align-items:center;}}
.history-clear-footer{{display:flex;justify-content:flex-end;margin-top:14px;}}
.history-clear-confirm{{display:flex;gap:12px;align-items:center;flex-wrap:wrap;justify-content:flex-end;}}
.profile-list{{border:1px solid {border};background:{surface};}}
.profile-line{{display:block;border-bottom:1px solid {border};padding:7px 10px;min-height:52px;}}
.profile-line.is-default{{background:{controlHover};}}
.profile-line.is-just-saved{{border-left:3px solid {selected};padding-left:7px;}}
.profile-saved-indicator{{display:inline-block;font-size:12px;font-weight:700;color:{selected};white-space:nowrap;}}
.profile-save-notice{{margin:10px 0 0;padding:10px 12px;background:{surface};border:1px solid {border};border-left:3px solid {selected};color:{fg};line-height:1.5;}}
.profile-line:last-child{{border-bottom:0;}}
.profile-default-form{{display:flex;align-items:center;}}
.profile-default-label{{display:inline-flex;align-items:center;justify-content:center;cursor:pointer;min-height:22px;min-width:24px;}}
.profile-default-label input[type=radio]{{appearance:auto;width:16px;min-width:16px;height:16px;margin:0;cursor:pointer;accent-color:{selected};}}
button.profile-default-submit{{position:absolute;width:1px;height:1px;min-width:0;min-height:0;padding:0;margin:0;border:0;overflow:hidden;clip:rect(0,0,0,0);}}
.default-choice-label{{flex:0 0 auto;font-size:11px;font-weight:600;color:{fg};border:1px solid {border};border-radius:3px;padding:1px 5px;}}
.default-choice-label[hidden]{{display:none;}}
.profile-title-row{{display:flex;align-items:center;gap:9px;min-width:0;}}
.profile-title-row form{{margin:0;display:flex;align-items:center;flex:0 0 auto;}}
.profile-title-row b{{min-width:0;overflow-wrap:anywhere;}}
.profile-line-actions{{display:flex;gap:14px;align-items:center;margin-left:auto;padding:2px 0 2px 12px;flex-shrink:0;}}
@media (max-width:720px){{.basic-grid{{grid-template-columns:1fr}}.profile-pair-grid{{grid-template-columns:1fr}}.section-heading-row{{align-items:flex-start;flex-direction:column}}}}

";
    }
}
