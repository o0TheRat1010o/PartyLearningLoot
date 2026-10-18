using System;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Detects reads through the injected marker buff and swallows the marker: it is never applied on the server nor
/// forwarded to other clients. Remote readers report it with NetPackageAddRemoveBuff; the host's own player (listen
/// server) adds it locally through EntityBuffs.AddBuff.
/// </summary>
internal static class SharedReadMarkerPatches
{
    public static bool AddRemoveBuffPrefix(NetPackageAddRemoveBuff __instance)
    {
        if (!SharedReadingService.IsMarker(__instance.buffName))
        {
            return true;
        }
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (connection == null || !connection.IsServer)
        {
            return true;
        }
        try
        {
            if (__instance.adding)
            {
                SharedReadingService.OnRemoteMarker(__instance.Sender, __instance.entityId, __instance.duration);
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Shared Reading: handling a remote read failed.", e, "shared_read_remote");
        }
        return false;
    }

    public static bool AddBuffPrefix(EntityBuffs __instance, string _name, float _buffDuration, ref EntityBuffs.BuffStatus __result)
    {
        if (!SharedReadingService.IsMarker(_name))
        {
            return true;
        }
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (connection == null || !connection.IsServer)
        {
            return true;   // a client reports the marker to its server as usual
        }
        try
        {
            if (__instance.parent is EntityPlayer player && !player.isEntityRemote)
            {
                SharedReadingService.OnHostMarker(player, _buffDuration);
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Shared Reading: handling a host read failed.", e, "shared_read_host");
        }
        __result = EntityBuffs.BuffStatus.Added;
        return false;
    }
}
