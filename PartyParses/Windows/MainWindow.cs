using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace PartyParses.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private uint lastTerritoryId = uint.MaxValue;
    private bool isLoading;
    private bool isLoadingEncounterTree;
    private bool foundOnly;
    private string? statusMessage = "Pick a fight below, or enter a duty.";
    private List<ExpansionOption>? encounterTree;
    private EncounterOption? selectedEncounter;
    private List<MemberRanking>? rankings;

    private static readonly Vector4 TankColor = ImGuiColors.TankBlue;
    private static readonly Vector4 HealerColor = ImGuiColors.HealerGreen;
    private static readonly Vector4 DpsColor = ImGuiColors.DPSRed;
    private static readonly Vector4 SelfRowColor = new(ImGuiColors.TankBlue.X, ImGuiColors.TankBlue.Y, ImGuiColors.TankBlue.Z, 0.16f);
    private static readonly Vector4 FooterRowColor = new(1f, 1f, 1f, 0.04f);

    public MainWindow(Plugin plugin)
        : base("Party Parses##PartyParsesMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Filter,
            Click = _ => foundOnly = !foundOnly,
            ShowTooltip = () => ImGui.SetTooltip(foundOnly ? "Showing found only — click to show all" : "Show found only"),
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Sync,
            Click = mouseButton =>
            {
                if (!isLoading)
                {
                    _ = RefreshButtonAsync();
                }
            },
            ShowTooltip = () => ImGui.SetTooltip(isLoading ? "Refreshing..." : "Refresh"),
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            Click = _ => plugin.ToggleConfigUi(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });

        Plugin.DutyState.DutyStarted += OnDutyStarted;
        Plugin.DutyState.DutyRecommenced += OnDutyStarted;
    }

    public void Dispose()
    {
        Plugin.DutyState.DutyStarted -= OnDutyStarted;
        Plugin.DutyState.DutyRecommenced -= OnDutyStarted;
    }

    private void OnDutyStarted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
    {
        if (Plugin.Configuration.AutoRefresh && !isLoading && selectedEncounter != null)
        {
            _ = FetchRankingsAsync(selectedEncounter);
        }
    }

    public override void Draw()
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (territoryId != lastTerritoryId)
        {
            lastTerritoryId = territoryId;
            if (Plugin.Configuration.AutoRefresh && !isLoading)
            {
                var duty = GameState.GetCurrentDuty();
                if (duty != null)
                {
                    _ = AutoMatchAndRefreshAsync(duty.Value);
                }
            }
        }

        if (!FFLogsClient.IsConfigured)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudYellow, "FFLogs API client not configured.");
            if (ImGui.Button("Open Settings"))
            {
                plugin.ToggleConfigUi();
            }

            return;
        }

        if (encounterTree == null && !isLoadingEncounterTree)
        {
            _ = LoadEncounterTreeAsync();
        }

        DrawHeader();

        if (statusMessage != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, statusMessage);
        }

        if (isLoading)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Refreshing…");
        }

        if (rankings == null || rankings.Count == 0)
        {
            return;
        }

        var rows = foundOnly ? rankings.Where(r => r.Found).ToList() : rankings;
        if (rows.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No parses found for this filter.");
            return;
        }

        ImGui.Spacing();

        if (Plugin.Configuration.CompactRows && rows.Count > 8)
        {
            DrawCompactAlliances(rows);
        }
        else
        {
            DrawFullTable(rows);
        }
    }

    private void DrawHeader()
    {
        var style = ImGui.GetStyle();
        var switchWidth = MathF.Max(70, MathF.Max(ImGui.CalcTextSize("DPS").X, ImGui.CalcTextSize("HPS").X) + (style.FramePadding.X * 4));

        var pickerWidth = MathF.Max(80, ImGui.GetContentRegionAvail().X - switchWidth - style.ItemSpacing.X);
        DrawEncounterPicker(pickerWidth);

        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - switchWidth - style.WindowPadding.X));

        DrawMetricRocker(switchWidth);
    }

    private static readonly Vector4 DpsAccent = ImGuiColors.TankBlue;
    private static readonly Vector4 HpsAccent = ImGuiColors.HealerGreen;

    private void DrawMetricRocker(float width)
    {
        var isHps = Plugin.Configuration.Metric == "hps";
        var label = isHps ? "HPS" : "DPS";
        var accent = isHps ? HpsAccent : DpsAccent;
        var faded = new Vector4(accent.X, accent.Y, accent.Z, 0.12f);
        var size = new Vector2(width, ImGui.GetFrameHeight());

        var min = ImGui.GetCursorScreenPos();
        var max = min + size;

        ImGui.InvisibleButton("##metricRocker", size);
        var clicked = ImGui.IsItemClicked();

        var drawList = ImGui.GetWindowDrawList();
        var rounding = ImGui.GetStyle().FrameRounding;

        var leftColor = isHps ? faded : accent;
        var rightColor = isHps ? accent : faded;

        const int bands = 14;
        var bandWidth = size.X / bands;
        for (var i = 0; i < bands; i++)
        {
            var t = i / (float)(bands - 1);
            var col = ImGui.GetColorU32(Vector4.Lerp(leftColor, rightColor, t));
            var bandMin = new Vector2(min.X + (i * bandWidth), min.Y);
            var bandMax = new Vector2(i == bands - 1 ? max.X : min.X + ((i + 1) * bandWidth), max.Y);

            var flags = i == 0 ? ImDrawFlags.RoundCornersLeft
                : i == bands - 1 ? ImDrawFlags.RoundCornersRight
                : ImDrawFlags.RoundCornersNone;
            drawList.AddRectFilled(bandMin, bandMax, col, rounding, flags);
        }

        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(min + ((size - textSize) / 2), ImGui.GetColorU32(ImGuiCol.Text), label);

        if (clicked)
        {
            SetMetric(isHps ? "dps" : "hps");
        }
    }

    private void DrawEncounterPicker(float width)
    {
        if (encounterTree == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, isLoadingEncounterTree ? "Loading fight list…" : "Fight list unavailable.");
            return;
        }

        if (encounterTree.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No fights returned by FFLogs.");
            return;
        }

        if (ImGui.Button(selectedEncounter?.Name ?? "Select a fight...", new Vector2(width, 0)))
        {
            ImGui.OpenPopup("ppFightPicker");
        }

        if (ImGui.BeginPopup("ppFightPicker"))
        {
            foreach (var exp in encounterTree)
            {
                if (ImGui.BeginMenu(exp.Name))
                {
                    foreach (var zone in exp.Zones)
                    {
                        if (ImGui.BeginMenu(zone.Name))
                        {
                            if (zone.Difficulties.Count > 0)
                            {
                                foreach (var difficulty in zone.Difficulties)
                                {
                                    if (ImGui.BeginMenu(difficulty.Name))
                                    {
                                        DrawEncounterMenuItems(difficulty.Encounters);
                                        ImGui.EndMenu();
                                    }
                                }
                            }
                            else
                            {
                                DrawEncounterMenuItems(zone.Encounters);
                            }

                            ImGui.EndMenu();
                        }
                    }

                    ImGui.EndMenu();
                }
            }

            ImGui.EndPopup();
        }
    }

    private void DrawEncounterMenuItems(List<EncounterOption> encounters)
    {
        foreach (var enc in encounters)
        {
            if (ImGui.MenuItem(enc.Name, string.Empty, enc == selectedEncounter))
            {
                selectedEncounter = enc;
                ImGui.CloseCurrentPopup();
                _ = ManualSelectAsync();
            }
        }
    }

    private void DrawFullTable(List<MemberRanking> rows)
    {
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(8, 4));
        using var table = ImRaii.Table("PartyParsesTable", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("Role %", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Best %", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Best Job", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Kills", ImGuiTableColumnFlags.WidthFixed, 40);
        DrawTableHeaderRow();

        for (var i = 0; i < rows.Count; i++)
        {
            var ranking = rows[i];
            ImGui.TableNextRow();
            if (ranking.Member.IsSelf)
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(SelfRowColor));
            }

            ImGui.TableNextColumn();
            ImGui.Text($"{ranking.Member.FullName}{(ranking.Member.IsSelf ? " (you)" : string.Empty)}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(ranking.Member.WorldName);
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(RoleColor(ranking.Member.JobAbbreviation), ranking.Member.JobAbbreviation);

            ImGui.TableNextColumn();
            if (ranking.Found && ranking.RankPercent != null)
            {
                ImGui.TextColored(GetPercentColor(ranking.RankPercent), $"{ranking.RankPercent:0.#}");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, ranking.Error ?? "-");
            }

            ImGui.TableNextColumn();
            if (ranking.BestFound && ranking.BestRankPercent != null)
            {
                ImGui.TextColored(GetPercentColor(ranking.BestRankPercent), $"{ranking.BestRankPercent:0.#}");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "-");
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(ImGuiColors.DalamudGrey, ranking.BestJob ?? "-");

            ImGui.TableNextColumn();
            ImGui.Text(ranking.TotalKills?.ToString() ?? "-");
        }

        DrawAverageFooter(rows);
    }

    private static void DrawTableHeaderRow()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        var column = 0;
        DrawTableHeaderCell(column++, "Name");
        DrawTableHeaderCell(column++, "Job");
        DrawTableHeaderCell(column++, "Role %", "Percentile rank among other players of this character's role (tank/healer/DPS) for this fight.");
        DrawTableHeaderCell(column++, "Best %", "This character's single best logged pull of this fight, on any job.");
        DrawTableHeaderCell(column++, "Best Job", "Which job the Best % pull was played on.");
        DrawTableHeaderCell(column, "Kills", "Number of logged pulls this fight was killed on, in the character's current role.");
    }

    private static void DrawTableHeaderCell(int column, string label, string? tooltip = null)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.TableHeader(label);
        if (tooltip != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private void DrawAverageFooter(List<MemberRanking> rows)
    {
        var found = rows.Where(r => r.Found && r.RankPercent != null).ToList();
        if (found.Count == 0)
        {
            return;
        }

        var avgRole = found.Average(r => r.RankPercent!.Value);

        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(FooterRowColor));
        ImGui.TableNextColumn();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Party average");
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.Text, GetPercentColor(avgRole)))
        {
            ImGui.Text($"{avgRole:0.#}");
        }

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
    }

    private void DrawCompactAlliances(List<MemberRanking> rows)
    {
        var groups = new List<List<MemberRanking>>();
        for (var i = 0; i < rows.Count; i += 8)
        {
            groups.Add(rows.GetRange(i, Math.Min(8, rows.Count - i)));
        }

        using var outer = ImRaii.Table("PartyParsesAlliances", groups.Count, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV);
        if (!outer.Success)
        {
            return;
        }

        string[] labels = { "Alliance A", "Alliance B", "Alliance C" };
        Vector4[] labelColors = { DpsColor, TankColor, HealerColor };

        ImGui.TableNextRow();
        for (var g = 0; g < groups.Count; g++)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(g < labelColors.Length ? labelColors[g] : ImGuiColors.DalamudGrey, g < labels.Length ? labels[g] : $"Group {g + 1}");
            ImGui.Spacing();

            using var inner = ImRaii.Table($"ppAll{g}", 3, ImGuiTableFlags.SizingStretchProp);
            if (!inner.Success)
            {
                continue;
            }

            ImGui.TableSetupColumn("n", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("j", ImGuiTableColumnFlags.WidthFixed, 34);
            ImGui.TableSetupColumn("p", ImGuiTableColumnFlags.WidthFixed, 36);

            foreach (var r in groups[g])
            {
                ImGui.TableNextRow();
                if (r.Member.IsSelf)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(SelfRowColor));
                }

                ImGui.TableNextColumn();
                ImGui.Text(r.Member.FirstName);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{r.Member.FullName} · {r.Member.WorldName}");
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(RoleColor(r.Member.JobAbbreviation), r.Member.JobAbbreviation);

                ImGui.TableNextColumn();
                if (r.Found && r.RankPercent != null)
                {
                    ImGui.TextColored(GetPercentColor(r.RankPercent), $"{r.RankPercent:0.#}");
                }
                else
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "-");
                }
            }
        }
    }

    private static Vector4 RoleColor(string job)
    {
        if (JobRoles.IsTank(job))
        {
            return TankColor;
        }

        if (JobRoles.IsHealer(job))
        {
            return HealerColor;
        }

        return DpsColor;
    }

    private void SetMetric(string metric)
    {
        if (Plugin.Configuration.Metric == metric)
        {
            return;
        }

        Plugin.Configuration.Metric = metric;
        Plugin.Configuration.Save();

        SortRankings();
    }

    private async Task LoadEncounterTreeAsync()
    {
        isLoadingEncounterTree = true;
        try
        {
            encounterTree = await plugin.FFLogsClient.GetEncounterTreeAsync();
        }
        finally
        {
            isLoadingEncounterTree = false;
        }
    }

    private async Task RefreshButtonAsync()
    {
        var duty = GameState.GetCurrentDuty();
        if (duty != null)
        {
            await AutoMatchAndRefreshAsync(duty.Value);
        }
        else if (selectedEncounter != null)
        {
            await FetchRankingsAsync(selectedEncounter);
        }
        else
        {
            statusMessage = "Pick a fight below to refresh.";
        }
    }

    private async Task AutoMatchAndRefreshAsync(CurrentDuty duty)
    {
        if (!FFLogsClient.IsConfigured || isLoading)
        {
            return;
        }

        encounterTree ??= await plugin.FFLogsClient.GetEncounterTreeAsync();
        if (encounterTree == null)
        {
            statusMessage = "Couldn't reach FFLogs (check your API client in Settings).";
            return;
        }

        var match = FFLogsClient.FindEncounter(duty.Name, encounterTree);
        if (match == null)
        {
            statusMessage = $"No FFLogs encounter found matching \"{duty.Name}\".";
            return;
        }

        Plugin.Log.Debug($"Matched duty \"{duty.Name}\" to FFLogs encounter {match.Id} \"{match.Name}\".");
        selectedEncounter = match;
        await FetchRankingsAsync(match);
    }

    private async Task ManualSelectAsync()
    {
        if (isLoading || selectedEncounter == null)
        {
            return;
        }

        await FetchRankingsAsync(selectedEncounter);
    }

    private async Task FetchRankingsAsync(EncounterOption encounter)
    {
        isLoading = true;
        try
        {
            var members = GameState.GetPartyMembers();
            if (members.Count == 0)
            {
                statusMessage = "No party members found.";
                return;
            }

            var result = await plugin.FFLogsClient.GetPartyRankingsAsync(members, encounter.Id, encounter.DifficultyId);
            if (result == null)
            {
                statusMessage = "FFLogs request failed, see /xllog for details.";
                return;
            }

            rankings = result;
            SortRankings();
            statusMessage = null;
        }
        finally
        {
            isLoading = false;
        }
    }

    private void SortRankings()
    {
        if (rankings == null)
        {
            return;
        }

        rankings = rankings
            .OrderByDescending(r => r.Found)
            .ThenByDescending(r => r.RankPercent ?? -1)
            .ToList();
    }

    private static Vector4 GetPercentColor(double? percent) => percent switch
    {
        null => ImGuiColors.ParsedGrey,
        >= 100 => ImGuiColors.ParsedGold,
        >= 99 => ImGuiColors.ParsedPink,
        >= 95 => ImGuiColors.ParsedOrange,
        >= 75 => ImGuiColors.ParsedPurple,
        >= 50 => ImGuiColors.ParsedBlue,
        >= 25 => ImGuiColors.ParsedGreen,
        _ => ImGuiColors.ParsedGrey,
    };
}
