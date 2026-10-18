using System;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Client confirmation channels for Shared Reading, hooked on the received packages (server only). Only packages a
/// client sent about its own player count.
/// <list type="bullet">
/// <item>NetPackagePlayerStats: the progression a client sent (only when its progression changed). Hooked on the
/// package and not on Progression.Read: the game applies every stats package twice (immediately and once more from
/// the entity's network queue in the next frame), the package itself is processed once.</item>
/// <item>NetPackageEntityStatsBuff: the client's buff and cvar sync (about every 5.5 s while alive), read into the
/// server copy by the original method; confirms recipes early.</item>
/// </list>
/// </summary>
internal static class ConfirmationSyncPatches
{
    public static void PlayerStatsPostfix(NetPackagePlayerStats __instance, World _world)
    {
        EntityAlive.EntityNetworkStats stats = __instance.entityNetworkStats;
        if (stats == null || !stats.hasProgression || !TryGetOwnPlayer(__instance, __instance.entityId, _world, out EntityPlayer player))
        {
            return;
        }
        try
        {
            SyncConfirmationTracker.OnProgressionSync(player);
        }
        catch (Exception e)
        {
            ModLog.Error("Shared Reading: checking a progression sync failed.", e, "confirm_stats");
        }
    }

    public static void StatsBuffPostfix(NetPackageEntityStatsBuff __instance, World _world)
    {
        if (!TryGetOwnPlayer(__instance, __instance.m_entityId, _world, out EntityPlayer player))
        {
            return;
        }
        try
        {
            SyncConfirmationTracker.OnBuffSync(player);
        }
        catch (Exception e)
        {
            ModLog.Error("Shared Reading: checking a buff sync failed.", e, "confirm_buffs");
        }
    }

    private static bool TryGetOwnPlayer(NetPackage _package, int _entityId, World _world, out EntityPlayer _player)
    {
        _player = null;
        if (_world == null || !SyncConfirmationTracker.HasPending(_entityId))
        {
            return false;
        }
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (connection == null || !connection.IsServer || _package.Sender == null || _package.Sender.entityId != _entityId)
        {
            return false;
        }
        _player = _world.GetEntity(_entityId) as EntityPlayer;
        return _player != null && _player.isEntityRemote;
    }
}
