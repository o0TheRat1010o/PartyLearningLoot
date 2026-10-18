using System;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Injects the Shared Reading marker into items.xml and the marker buff into buffs.xml on the server, after all mod
/// XML patches and conditionals, right before the configs are compressed for the clients and parsed by the server.
/// Clients therefore receive the marker through the vanilla config sync and need no mod.
/// </summary>
internal static class XmlCachePatch
{
    public static void Prefix(WorldStaticData.XmlLoadInfo _loadInfo, XmlFile _xmlFile)
    {
        if (!SharedReadingService.Installed || _loadInfo == null || _xmlFile?.XmlDoc == null)
        {
            return;
        }
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (connection == null || !connection.IsServer)
        {
            return;
        }
        string name = _loadInfo.XmlName;
        try
        {
            if (string.Equals(name, "buffs", StringComparison.OrdinalIgnoreCase))
            {
                SharedReadingService.OnBuffsXml(_xmlFile.XmlDoc);
            }
            else if (string.Equals(name, "items", StringComparison.OrdinalIgnoreCase))
            {
                SharedReadingService.OnItemsXml(_xmlFile.XmlDoc);
            }
        }
        catch (Exception e)
        {
            SharedReadingService.OnInjectionFailed(e);
            ModLog.Error($"Shared Reading: injecting into {name}.xml failed; Shared Reading stays inactive.", e, "xml_injection");
        }
    }
}
