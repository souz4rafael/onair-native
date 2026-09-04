using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OnAirNative.Services;
using Windows.ApplicationModel.DataTransfer;

namespace OnAirNative.Views.Dialogs;

/// <summary>
/// ContentDialog for the onAIr MCP server: usage instructions, a ready-to-copy client config
/// snippet, and a per-tool enable/disable checklist (persisted to
/// AppConfig.McpDisabledTools — read directly by the separate mcp-server process; see
/// mcp-server/ToolGate.cs).
/// </summary>
public sealed partial class McpToolsDialog : ContentDialog
{
    /// <summary>One entry per MCP tool exposed by mcp-server/OnAirTools.cs — KEEP IN SYNC with
    /// that file's [McpServerTool(Name = "...")] names whenever a tool is added/removed/renamed.
    /// Grouped for readability in the checklist; group order here is display order.</summary>
    private static readonly (string Group, string Name, string Title, string Description)[] Tools =
    [
        ("READ-ONLY",              "onair_is_running",               "Is running",           "Checks onAIr is reachable"),
        ("READ-ONLY",              "onair_get_state",                "Get state",            "Reads TP/lock/recording/font/scroll/Q&A status"),
        ("READ-ONLY",              "onair_get_last_qa_turn",         "Get last Q&A turn",    "Reads the most recent question/answer + turn counter, for monitoring"),
        ("READ-ONLY",              "onair_get_script_text",          "Get script text",      "Reads the loaded script's full text"),
        ("READ-ONLY",              "onair_list_fonts",               "List fonts",           "Lists fonts installed on this PC"),
        ("TELEPROMPTER CONTROL",   "onair_toggle_tp",                "Toggle TP",            "Opens/closes the teleprompter"),
        ("TELEPROMPTER CONTROL",   "onair_toggle_lock",              "Toggle lock",          "Locks/unlocks the TP window"),
        ("TELEPROMPTER CONTROL",   "onair_toggle_hide_tp",           "Toggle hide TP",       "Hides/shows the TP in screen share"),
        ("TELEPROMPTER CONTROL",   "onair_toggle_hide_controller",   "Toggle hide Controller","Hides/shows the Controller in screen share"),
        ("TELEPROMPTER CONTROL",   "onair_load_script",              "Load script",          "Loads a .txt script by path"),
        ("TELEPROMPTER CONTROL",   "onair_show_insight",             "Show insight",         "Shows a Copilot note in the TP's footer (Script + Q&A modes)"),
        ("TELEPROMPTER CONTROL",   "onair_clear_insight",            "Clear insight",        "Clears the TP's Copilot-insight footer"),
        ("SCROLL & APPEARANCE",    "onair_set_scroll_mode",          "Set scroll mode",      "Manual / Auto / Voice"),
        ("SCROLL & APPEARANCE",    "onair_set_scroll_speed",         "Set scroll speed",     "Auto mode's speed"),
        ("SCROLL & APPEARANCE",    "onair_set_voice_scroll_speed",   "Set voice scroll speed","Voice mode's speed"),
        ("SCROLL & APPEARANCE",    "onair_set_scroll_step",          "Set scroll step",      "Manual mode's step size"),
        ("SCROLL & APPEARANCE",    "onair_set_font_size",            "Set font size",        ""),
        ("SCROLL & APPEARANCE",    "onair_set_font_color",           "Set font color",       "Hex color code"),
        ("SCROLL & APPEARANCE",    "onair_set_font_family",          "Set font family",      ""),
        ("SCROLL & APPEARANCE",    "onair_set_opacity",              "Set opacity",          "TP window opacity"),
        ("SCROLL & APPEARANCE",    "onair_set_voice_threshold",      "Set voice threshold",  "Voice scroll sensitivity"),
        ("AI INSIGHTS",            "onair_toggle_insights",          "Toggle Insights",      "Opens/closes the AI Insights window"),
        ("AI INSIGHTS",            "onair_toggle_insights_lock",     "Toggle Insights lock", "Locks/unlocks the AI Insights window"),
        ("AI INSIGHTS",            "onair_toggle_insights_hide",     "Toggle hide Insights", "Hides/shows the AI Insights window in screen share"),
        ("AI INSIGHTS",            "onair_toggle_insights_show_questions",  "Toggle Questions section", "Shows/hides the QUESTIONS section"),
        ("AI INSIGHTS",            "onair_toggle_insights_show_external",   "Toggle External Insights section", "Shows/hides the EXTERNAL AI INSIGHTS section"),
        ("AI INSIGHTS",            "onair_toggle_insights_show_pacing",     "Toggle Pacing section", "Shows/hides the PACING section"),
        ("AI INSIGHTS",            "onair_toggle_insights_show_token_usage","Toggle Token Usage section", "Shows/hides the TOKEN USAGE section"),
        ("AI INSIGHTS",            "onair_set_insight_font_size",    "Set Insights font size",""),
        ("AI INSIGHTS",            "onair_set_insight_opacity",      "Set Insights opacity", "AI Insights window opacity"),
        ("AI INSIGHTS",            "onair_set_insight_font_color",   "Set Insights font color","Hex color code"),
        ("AI INSIGHTS",            "onair_set_insight_font_family",  "Set Insights font family",""),
        ("RECORDING & AI",         "onair_toggle_recording",         "Toggle recording",     "Starts/stops Q&A recording"),
        ("RECORDING & AI",         "onair_recheck_whisper_model",    "Recheck Whisper model","Refreshes local/cloud status"),
        ("APP STEALTH",            "onair_release_stealth_container","Release stealth container","Closes the App Stealth container"),
    ];

    private readonly ConfigService _config;
    private readonly Dictionary<string, CheckBox> _checkboxes = new();

    public McpToolsDialog(ConfigService config)
    {
        InitializeComponent();
        _config = config;

        PopulateServerPath();
        PopulateToolsList();
    }

    /// <summary>Checks the bundled MCP server DLL exists — always
    /// "&lt;app dir&gt;\Assets\mcp-server\OnAirMcp.dll" for both dev and installed builds, since
    /// it's copied there as a Content asset at build time (see OnAirNative.csproj). Disables the
    /// copy button (with an explanatory status message) instead of generating a broken path if
    /// somehow missing.</summary>
    private void PopulateServerPath()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "Assets", "mcp-server", "OnAirMcp.dll");
        if (File.Exists(dllPath))
        {
            CopyConfigButton.IsEnabled = true;
        }
        else
        {
            CopyStatusText.Text = "⚠ MCP server not found — rebuild onAIr so the bundled MCP server asset is included.";
            CopyConfigButton.IsEnabled = false;
        }
    }

    private void PopulateToolsList()
    {
        var disabled = new HashSet<string>(_config.Current.McpDisabledTools);
        string? currentGroup = null;

        foreach (var (group, name, title, description) in Tools)
        {
            if (group != currentGroup)
            {
                currentGroup = group;
                ToolsListPanel.Children.Add(new TextBlock
                {
                    Text = group,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 0),
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            }

            var checkbox = new CheckBox
            {
                IsChecked = !disabled.Contains(name),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = title, FontSize = 13 },
                        new TextBlock
                        {
                            Text = description,
                            FontSize = 11,
                            Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        },
                    },
                },
            };
            _checkboxes[name] = checkbox;
            ToolsListPanel.Children.Add(checkbox);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _checkboxes.Values) cb.IsChecked = true;
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _checkboxes.Values) cb.IsChecked = false;
    }

    /// <summary>Copies a ready-to-paste MCP client config snippet to the clipboard — Claude
    /// Desktop's "mcpServers" JSON shape, the most commonly referenced format across MCP docs.
    /// Other clients (e.g. VS Code's mcp.json uses "servers" instead) may need the outer key
    /// renamed — mentioned in the dialog's instructional text above.</summary>
    private void CopyConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "Assets", "mcp-server", "OnAirMcp.dll");
        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["onair"] = new { command = "dotnet", args = new[] { dllPath } },
            },
        };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        var package = new DataPackage();
        package.SetText(json);
        Clipboard.SetContent(package);

        CopyStatusText.Text = "✓ Copied to clipboard";
    }

    /// <summary>Persists the checklist's current state to AppConfig.McpDisabledTools —
    /// unchecked = disabled. Called from the Save button (PrimaryButtonClick).</summary>
    public void SaveToolsState()
    {
        _config.Current.McpDisabledTools = _checkboxes
            .Where(kv => kv.Value.IsChecked != true)
            .Select(kv => kv.Key)
            .ToList();
        _config.Save();
    }
}
