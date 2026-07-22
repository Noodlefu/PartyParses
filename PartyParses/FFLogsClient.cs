using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PartyParses;

public record EncounterOption(int Id, string Name, int? DifficultyId = null);
public record DifficultyOption(string Name, List<EncounterOption> Encounters);
public record ZoneOption(string Name, List<EncounterOption> Encounters, List<DifficultyOption> Difficulties);
public record ExpansionOption(string Name, List<ZoneOption> Zones);

public class MetricRanking
{
    public bool Found { get; set; }
    public double? RankPercent { get; set; }
    public int? TotalKills { get; set; }
    public string? Job { get; set; }
    public string? Error { get; set; }
}

public class MemberRanking
{
    public required PartyMemberInfo Member { get; init; }
    public bool Hidden { get; set; }
    public string? CharacterError { get; set; }
    public MetricRanking RoleDps { get; } = new();
    public MetricRanking RoleHps { get; } = new();
    public MetricRanking BestDps { get; } = new();
    public MetricRanking BestHps { get; } = new();

    private MetricRanking Role => Plugin.Configuration.Metric == "hps" ? RoleHps : RoleDps;
    private MetricRanking Best => Plugin.Configuration.Metric == "hps" ? BestHps : BestDps;

    public bool Found => CharacterError == null && Role.Found;
    public double? RankPercent => Role.RankPercent;
    public int? TotalKills => Role.TotalKills;
    public string? Error => CharacterError ?? Role.Error;

    public bool BestFound => CharacterError == null && Best.Found;
    public double? BestRankPercent => Best.RankPercent;
    public string? BestJob => Best.Job;
}

public class FFLogsClient
{
    private const string TokenEndpoint = "https://www.fflogs.com/oauth/token";
    private const string GraphQlEndpoint = "https://www.fflogs.com/api/v2/client";

    private readonly HttpClient http = new();
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private readonly SemaphoreSlim gameDataLock = new(1, 1);

    private string? token;
    private DateTime tokenExpiryUtc = DateTime.MinValue;
    private List<ExpansionOption>? encounterTreeCache;

    public static bool IsConfigured =>
        !string.IsNullOrEmpty(Plugin.Configuration.ClientId) && !string.IsNullOrEmpty(Plugin.Configuration.ClientSecret);

    public bool IsAuthenticated => token != null && DateTime.UtcNow < tokenExpiryUtc;

    public TimeSpan TokenTimeRemaining => IsAuthenticated ? tokenExpiryUtc - DateTime.UtcNow : TimeSpan.Zero;

    public void InvalidateToken()
    {
        token = null;
        tokenExpiryUtc = DateTime.MinValue;
    }

    public async Task<List<ExpansionOption>?> GetEncounterTreeAsync(bool forceRefresh = false)
    {
        if (encounterTreeCache != null && !forceRefresh)
        {
            return encounterTreeCache;
        }

        await gameDataLock.WaitAsync();
        try
        {
            if (encounterTreeCache != null && !forceRefresh)
            {
                return encounterTreeCache;
            }

            if (!await EnsureTokenAsync())
            {
                return null;
            }

            const string query = """{"query":"{worldData {expansions {name zones {name difficulties { id name } encounters {id name}}}}}"}""";
            var response = await http.PostAsync(GraphQlEndpoint, new StringContent(query, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Error($"FFLogs game data request failed: {response.StatusCode}");
                return null;
            }

            var root = JObject.Parse(await response.Content.ReadAsStringAsync());
            var tree = new List<ExpansionOption>();
            var expansions = root["data"]?["worldData"]?["expansions"];
            if (expansions != null)
            {
                foreach (var expansion in expansions)
                {
                    var expansionName = expansion["name"]?.ToString() ?? "Unknown";
                    var zones = new List<ZoneOption>();
                    foreach (var zone in expansion["zones"] ?? Enumerable.Empty<JToken>())
                    {
                        var zoneName = zone["name"]?.ToString() ?? "Unknown";

                        var difficulties = new List<(int Id, string Name)>();
                        foreach (var difficulty in zone["difficulties"] ?? Enumerable.Empty<JToken>())
                        {
                            var difficultyId = difficulty["id"]?.ToObject<int?>();
                            var difficultyName = difficulty["name"]?.ToString();
                            if (difficultyId != null && !string.IsNullOrEmpty(difficultyName))
                            {
                                difficulties.Add((difficultyId.Value, difficultyName));
                            }
                        }

                        var rawEncounters = new List<(int Id, string Name)>();
                        foreach (var encounter in zone["encounters"] ?? Enumerable.Empty<JToken>())
                        {
                            var id = encounter["id"]?.ToObject<int?>();
                            var name = encounter["name"]?.ToString();
                            if (id != null && !string.IsNullOrEmpty(name))
                            {
                                rawEncounters.Add((id.Value, name));
                            }
                        }

                        if (rawEncounters.Count == 0)
                        {
                            continue;
                        }

                        if (difficulties.Count > 1)
                        {
                            var difficultyOptions = difficulties
                                .Select(d => new DifficultyOption(d.Name, rawEncounters.Select(e => new EncounterOption(e.Id, e.Name, d.Id)).ToList()))
                                .ToList();
                            zones.Add(new ZoneOption(zoneName, [], difficultyOptions));
                        }
                        else
                        {
                            var flatEncounters = rawEncounters.Select(e => new EncounterOption(e.Id, e.Name)).ToList();
                            zones.Add(new ZoneOption(zoneName, flatEncounters, []));
                        }
                    }

                    if (zones.Count > 0)
                    {
                        tree.Add(new ExpansionOption(expansionName, zones));
                    }
                }
            }

            encounterTreeCache = tree;
            return tree;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error fetching FFLogs game data.");
            return null;
        }
        finally
        {
            gameDataLock.Release();
        }
    }

    private static readonly Dictionary<string, string> NameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hell on Rails (Extreme)"] = "Doomtrain",
    };

    public static EncounterOption? FindEncounter(string dutyName, IReadOnlyCollection<ExpansionOption> encounterTree)
    {
        if (string.IsNullOrWhiteSpace(dutyName))
        {
            return null;
        }

        var zones = encounterTree.SelectMany(e => e.Zones).ToList();
        var candidates = zones
            .SelectMany(z => (z.Difficulties.Count > 0 ? z.Difficulties[0].Encounters : z.Encounters)
                .Select(e => (Zone: z, Encounter: e)))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        (ZoneOption Zone, EncounterOption Encounter)? match = null;

        if (NameAliases.TryGetValue(dutyName, out var alias))
        {
            match = FirstOrNull(candidates, c => string.Equals(c.Encounter.Name, alias, StringComparison.OrdinalIgnoreCase));
        }

        match ??= FirstOrNull(candidates, c => string.Equals(c.Encounter.Name, dutyName, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            var normalizedDuty = Normalize(dutyName);
            match = FirstOrNull(candidates, c => Normalize(c.Encounter.Name) == normalizedDuty);
        }

        if (match == null)
        {
            var dutyWords = Words(dutyName);
            var bestScore = 0;
            foreach (var candidate in candidates)
            {
                var score = Words(candidate.Encounter.Name).Count(dutyWords.Contains);
                if (score > bestScore)
                {
                    bestScore = score;
                    match = candidate;
                }
            }

            if (bestScore < Math.Max(1, dutyWords.Count / 2))
            {
                match = null;
            }
        }

        return match == null ? null : SelectDifficulty(match.Value.Zone, match.Value.Encounter.Id, dutyName);
    }

    private static (ZoneOption Zone, EncounterOption Encounter)? FirstOrNull(
        List<(ZoneOption Zone, EncounterOption Encounter)> candidates, Func<(ZoneOption Zone, EncounterOption Encounter), bool> predicate)
    {
        foreach (var candidate in candidates)
        {
            if (predicate(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static EncounterOption SelectDifficulty(ZoneOption zone, int encounterId, string dutyName)
    {
        if (zone.Difficulties.Count == 0)
        {
            return zone.Encounters.First(e => e.Id == encounterId);
        }

        var difficulty = zone.Difficulties.FirstOrDefault(d => dutyName.Contains(d.Name, StringComparison.OrdinalIgnoreCase))
            ?? zone.Difficulties.FirstOrDefault(d => string.Equals(d.Name, "Normal", StringComparison.OrdinalIgnoreCase))
            ?? zone.Difficulties[0];

        return difficulty.Encounters.First(e => e.Id == encounterId);
    }

    public async Task<List<MemberRanking>?> GetPartyRankingsAsync(IReadOnlyList<PartyMemberInfo> members, int encounterId, int? difficultyId = null)
    {
        if (members.Count == 0)
        {
            return [];
        }

        if (!await EnsureTokenAsync())
        {
            return null;
        }

        var query = BuildRankingsQuery(members, encounterId, difficultyId);

        try
        {
            var response = await http.PostAsync(GraphQlEndpoint, new StringContent(query, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Error($"FFLogs rankings request failed: {response.StatusCode}");
                return null;
            }

            var root = JObject.Parse(await response.Content.ReadAsStringAsync());
            if (root["errors"] != null)
            {
                Plugin.Log.Error($"FFLogs rankings query had errors: {root["errors"]}");
                return null;
            }

            var characterData = root["data"]?["characterData"];
            var results = new List<MemberRanking>(members.Count);
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                var result = new MemberRanking { Member = member };
                var character = characterData?[$"p{i}"];

                if (character == null || character.Type == JTokenType.Null)
                {
                    result.CharacterError = "Not found on FFLogs";
                }
                else if (character["hidden"]?.ToObject<bool?>() == true)
                {
                    result.Hidden = true;
                    result.CharacterError = "Logs hidden";
                }
                else
                {
                    ParseBestMetric(character["roleRdps"], character["roleDps"], result.RoleDps);
                    ParseBestMetric(character["roleHps"], null, result.RoleHps);
                    ParseBestMetric(character["bestRdps"], character["bestDps"], result.BestDps);
                    ParseBestMetric(character["bestHps"], null, result.BestHps);
                }

                Plugin.Log.Debug(result.Found
                    ? $"FFLogs: {member.FullName} ({member.JobAbbreviation}) - {result.RankPercent:0.#}% best, {result.TotalKills} kills"
                    : $"FFLogs: {member.FullName} ({member.JobAbbreviation}) - {result.Error}");

                results.Add(result);
            }

            return results;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error fetching FFLogs rankings.");
            return null;
        }
    }

    private static void ParseBestMetric(JToken? a, JToken? b, MetricRanking target)
    {
        var aKills = a?["totalKills"]?.ToObject<int?>() ?? 0;
        var bKills = b?["totalKills"]?.ToObject<int?>() ?? 0;

        JToken? chosen;
        if (aKills == 0 && bKills == 0)
        {
            target.Error = "No kills";
            return;
        }

        if (aKills == 0)
        {
            chosen = b;
        }
        else if (bKills == 0)
        {
            chosen = a;
        }
        else
        {
            var aBest = a!["ranks"]?.FirstOrDefault()?["rankPercent"]?.ToObject<double?>() ?? -1;
            var bBest = b!["ranks"]?.FirstOrDefault()?["rankPercent"]?.ToObject<double?>() ?? -1;
            chosen = bBest > aBest ? b : a;
        }

        target.Found = true;
        var bestRank = chosen!["ranks"]?.FirstOrDefault();
        target.RankPercent = bestRank?["rankPercent"]?.ToObject<double?>();
        target.TotalKills = chosen["totalKills"]?.ToObject<int?>();
        target.Job = bestRank?["bestSpec"]?.ToString() ?? bestRank?["spec"]?.ToString();
    }

    private static string BuildRankingsQuery(IReadOnlyList<PartyMemberInfo> members, int encounterId, int? difficultyId)
    {
        var difficultyArg = difficultyId != null ? $", difficulty: {difficultyId}" : string.Empty;

        var sb = new StringBuilder();
        sb.Append("{\"query\":\"query {characterData {");
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var name = EscapeGraphQlString(member.FullName);
            var world = EscapeGraphQlString(member.WorldName);
            var region = EscapeGraphQlString(member.RegionCode);
            var role = JobRoles.IsTank(member.JobAbbreviation) ? "Tank" : JobRoles.IsHealer(member.JobAbbreviation) ? "Healer" : "DPS";

            sb.Append($"p{i}: character(name: \\\"{name}\\\", serverSlug: \\\"{world}\\\", serverRegion: \\\"{region}\\\") {{ hidden ");
            sb.Append($"roleRdps: encounterRankings(encounterID: {encounterId}, role: {role}, metric: rdps, timeframe: Historical{difficultyArg}) ");
            sb.Append($"roleDps: encounterRankings(encounterID: {encounterId}, role: {role}, metric: dps, timeframe: Historical{difficultyArg}) ");
            sb.Append($"roleHps: encounterRankings(encounterID: {encounterId}, role: {role}, metric: hps, timeframe: Historical{difficultyArg}) ");
            sb.Append($"bestRdps: encounterRankings(encounterID: {encounterId}, metric: rdps, timeframe: Historical{difficultyArg}) ");
            sb.Append($"bestDps: encounterRankings(encounterID: {encounterId}, metric: dps, timeframe: Historical{difficultyArg}) ");
            sb.Append($"bestHps: encounterRankings(encounterID: {encounterId}, metric: hps, timeframe: Historical{difficultyArg}) }}");
        }

        sb.Append("}}\"}");
        return sb.ToString();
    }

    private static string EscapeGraphQlString(string value) => value.Replace("\\", string.Empty).Replace("\"", string.Empty);

    private static string Normalize(string s) => Regex.Replace(s, "[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();

    private static HashSet<string> Words(string s) =>
        s.ToLowerInvariant().Split([' ', '(', ')', ':', '\''], StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private async Task<bool> EnsureTokenAsync()
    {
        if (!IsConfigured)
        {
            return false;
        }

        if (token != null && DateTime.UtcNow < tokenExpiryUtc)
        {
            return true;
        }

        await tokenLock.WaitAsync();
        try
        {
            if (token != null && DateTime.UtcNow < tokenExpiryUtc)
            {
                return true;
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = Plugin.Configuration.ClientId,
                ["client_secret"] = Plugin.Configuration.ClientSecret,
            };

            var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form));
            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Error($"FFLogs token request failed: {response.StatusCode}");
                return false;
            }

            var obj = JObject.Parse(await response.Content.ReadAsStringAsync());
            var accessToken = obj["access_token"]?.ToString();
            var expiresIn = obj["expires_in"]?.ToObject<int?>() ?? 0;
            if (string.IsNullOrEmpty(accessToken))
            {
                Plugin.Log.Error($"FFLogs token response missing access_token: {obj}");
                return false;
            }

            token = accessToken;
            tokenExpiryUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, expiresIn - 60));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Plugin.Log.Debug($"FFLogs token acquired, expires {tokenExpiryUtc:HH:mm:ss} UTC.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error fetching FFLogs token.");
            return false;
        }
        finally
        {
            tokenLock.Release();
        }
    }
}
