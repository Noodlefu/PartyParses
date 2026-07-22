using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;

namespace PartyParses.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string clientId;
    private string clientSecret;

    public ConfigWindow(Plugin plugin)
        : base("Party Parses Settings##PartyParsesConfig", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        clientId = Plugin.Configuration.ClientId;
        clientSecret = Plugin.Configuration.ClientSecret;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextColored(ImGuiColors.DalamudViolet, "API Client");
        ImGui.Separator();

        ImGui.TextWrapped("Create a free API client (client credentials grant) at fflogs.com to use this plugin.");
        if (ImGui.Button("Open fflogs.com/api/clients/"))
        {
            Dalamud.Utility.Util.OpenLink("https://www.fflogs.com/api/clients/");
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(320);
        ImGui.InputText("Client ID", ref clientId, 128);

        ImGui.SetNextItemWidth(320);
        ImGui.InputText("Client Secret", ref clientSecret, 128, ImGuiInputTextFlags.Password);

        ImGui.Spacing();

        if (ImGui.Button("Save"))
        {
            Plugin.Configuration.ClientId = clientId.Trim();
            Plugin.Configuration.ClientSecret = clientSecret.Trim();
            Plugin.Configuration.Save();
            plugin.FFLogsClient.InvalidateToken();
        }

        ImGui.SameLine();
        DrawConnectionStatus();

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Display");
        ImGui.Separator();

        var autoRefresh = Plugin.Configuration.AutoRefresh;
        if (ImGui.Checkbox("Auto-refresh on entering a duty", ref autoRefresh))
        {
            Plugin.Configuration.AutoRefresh = autoRefresh;
            Plugin.Configuration.Save();
        }

        var compact = Plugin.Configuration.CompactRows;
        if (ImGui.Checkbox("Compact rows (24-man)", ref compact))
        {
            Plugin.Configuration.CompactRows = compact;
            Plugin.Configuration.Save();
        }
    }

    private void DrawConnectionStatus()
    {
        if (!FFLogsClient.IsConfigured)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Not configured yet.");
            return;
        }

        if (plugin.FFLogsClient.IsAuthenticated)
        {
            var minutes = (int)plugin.FFLogsClient.TokenTimeRemaining.TotalMinutes;
            ImGui.TextColored(ImGuiColors.HealerGreen, $"Connected \u00b7 token valid {minutes}m");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Saved \u00b7 will connect on next lookup");
        }
    }
}
