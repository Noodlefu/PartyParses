using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace PartyParses;

public static class JobRoles
{
    private static readonly HashSet<string> Tanks = new(StringComparer.OrdinalIgnoreCase)
        { "PLD", "WAR", "DRK", "GNB", "GLA", "MRD" };
    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
        { "WHM", "SCH", "AST", "SGE", "CNJ" };

    public static bool IsTank(string jobAbbreviation) => Tanks.Contains(jobAbbreviation);

    public static bool IsHealer(string jobAbbreviation) => Healers.Contains(jobAbbreviation);
}

public class PartyMemberInfo
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public string RegionCode { get; init; } = string.Empty;
    public string JobAbbreviation { get; init; } = string.Empty;
    public string JobName { get; init; } = string.Empty;
    public bool IsSelf { get; init; }

    public string FullName => LastName == string.Empty ? FirstName : $"{FirstName} {LastName}";
}

public readonly record struct CurrentDuty(string Name, string ContentType);

public static class GameState
{
    public static string GetRegionCode(World world)
    {
        return world.DataCenter.ValueNullable?.Region.RowId switch
        {
            1 => "jp",
            2 => "na",
            3 => "eu",
            4 => "oc",
            5 => "cn",
            _ => string.Empty,
        };
    }

    public static CurrentDuty? GetCurrentDuty()
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (!Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territoryRow))
        {
            return null;
        }

        var cfc = territoryRow.ContentFinderCondition.ValueNullable;
        if (cfc == null || cfc.Value.RowId == 0)
        {
            return null;
        }

        var name = cfc.Value.Name.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var contentType = cfc.Value.ContentType.ValueNullable?.Name.ToString() ?? string.Empty;
        return new CurrentDuty(name, contentType);
    }

    public static List<PartyMemberInfo> GetPartyMembers()
    {
        var members = new List<PartyMemberInfo>();

        void Add(string fullName, RowRef<World> worldRef, RowRef<ClassJob> jobRef, bool isSelf)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return;
            }

            var world = worldRef.ValueNullable;
            if (world == null)
            {
                return;
            }

            var nameParts = fullName.Split(' ', 2);
            members.Add(new PartyMemberInfo
            {
                FirstName = nameParts[0],
                LastName = nameParts.Length > 1 ? nameParts[1] : string.Empty,
                WorldName = world.Value.Name.ToString(),
                RegionCode = GetRegionCode(world.Value),
                JobAbbreviation = jobRef.ValueNullable?.Abbreviation.ToString() ?? "???",
                JobName = jobRef.ValueNullable?.Name.ToString() ?? string.Empty,
                IsSelf = isSelf,
            });
        }

        var partyList = Plugin.PartyList;
        foreach (var member in partyList)
        {
            Add(member.Name.TextValue, member.World, member.ClassJob, false);
        }

        if (partyList.IsAlliance)
        {
            for (var i = 0; i < 24; i++)
            {
                var address = partyList.GetAllianceMemberAddress(i);
                if (address == nint.Zero)
                {
                    continue;
                }

                var allianceMember = partyList.CreateAllianceMemberReference(address);
                if (allianceMember == null)
                {
                    continue;
                }

                Add(allianceMember.Name.TextValue, allianceMember.World, allianceMember.ClassJob, false);
            }
        }

        members = members
            .GroupBy(m => (m.FirstName, m.LastName, m.WorldName))
            .Select(g => g.First())
            .ToList();

        var playerState = Plugin.PlayerState;
        if (playerState.IsLoaded
            && playerState.HomeWorld.ValueNullable != null
            && !members.Any(m => m.FullName == playerState.CharacterName))
        {
            Add(playerState.CharacterName, playerState.HomeWorld, playerState.ClassJob, true);
        }

        return members;
    }
}
