using Dalamud.Configuration;
using System;

namespace PartyParses;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public string Metric { get; set; } = "dps";
    public bool AutoRefresh { get; set; } = true;
    public bool CompactRows { get; set; } = false;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
