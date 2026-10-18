using System;
using System.Collections.Generic;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Activates the merge context for a LootManager spawn when the opener is in a party with at least one active member.
/// Everything else (no scope, no party, setting off, breaker open) leaves the spawn untouched.
/// </summary>
internal static class LootContainerSpawnPatch
{
    private static List<ItemStack> spawnedItems;

    public static void Prefix(LootContainer __instance, EntityPlayer player, out ActivationToken __state)
    {
        __state = default;
        if (!LootManagerPatches.Scopes.TryGetCurrent(out LootScope scope) || !ThreadManager.IsMainThread())
        {
            return;   // not a container/bag opened through LootManager: never ours
        }
        try
        {
            string reason = Decide(scope, player, out PartySnapshot snapshot);
            string container = scope.ContainerName ?? __instance?.Name;
            if (reason == null)
            {
                __state = MergeContext.Activate(player, snapshot, scope.Kind, container, PartyLearningLootMod.Settings.DryRun);
                if (!__state.Activated)
                {
                    reason = "activation_failed";
                }
            }
            if (reason != null)
            {
                RecordVanilla(scope, player, container, reason, snapshot);
            }
        }
        catch (Exception e)
        {
            MergeEngine.RegisterError("spawn_prefix", __instance?.Name, e);
            __state = default;
        }
    }

    public static void Postfix(List<ItemStack> __result, ActivationToken __state)
    {
        if (__state.Activated)
        {
            spawnedItems = __result;
        }
    }

    public static Exception Finalizer(Exception __exception, ActivationToken __state)
    {
        if (!__state.Activated)
        {
            return __exception;
        }
        SpawnTrace trace = MergeContext.Trace;
        try
        {
            trace.Stop();
            CountMergedKinds(spawnedItems, out int learningItems, out int otherLootItems);
            RuntimeStats.CountMerged(trace, learningItems, otherLootItems);
            if (AuditLog.Summary)
            {
                trace.WriteSpawnRecord(spawnedItems, __exception, AuditLog.Detailed);
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Writing the spawn audit record failed.", e, "spawn_record");
        }
        finally
        {
            MergeContext.Deactivate(__state);
            spawnedItems = null;
        }
        if (AuditLog.Detailed && !MergeContext.IsClean())
        {
            trace.Violation("context_clean", "merge context still holds state after the spawn");
            MergeContext.Reset();
        }
        return __exception;
    }

    /// <summary>Returns null when merging may start, otherwise the reason this spawn stays vanilla.</summary>
    private static string Decide(LootScope _scope, EntityPlayer _player, out PartySnapshot _snapshot)
    {
        _snapshot = null;
        ModSettings settings = PartyLearningLootMod.Settings;
        if (_player == null || _player.entityId != _scope.OpenerId)
        {
            return "scope_mismatch";
        }
        if (!settings.Enabled)
        {
            return "disabled";
        }
        if (_scope.Kind == LootOpenKind.Container && !settings.ApplyToContainers)
        {
            return "containers_disabled";
        }
        if (_scope.Kind == LootOpenKind.LootBag && !settings.ApplyToLootBags)
        {
            return "loot_bags_disabled";
        }
        GameCatalog catalog = GameCatalog.Current;
        if (catalog == null || catalog.IsEmpty)
        {
            return "catalog_unavailable";
        }
        if (MergeEngine.BreakerOpen)
        {
            return "breaker_open";
        }
        if (MergeContext.Active)
        {
            return "already_active";
        }
        _snapshot = PartySnapshot.Capture(_player, settings.MaxMemberDistance, settings.IncludeDeadMembers, settings.IncludeSpectators);
        return _snapshot.VanillaReason;
    }

    private static void RecordVanilla(LootScope _scope, EntityPlayer _player, string _container, string _reason, PartySnapshot _snapshot)
    {
        RuntimeStats.CountVanilla(_reason);
        if (!AuditLog.Detailed)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.SpawnVanilla);
        record.Prop("kind", LootScope.KindCode(_scope.Kind))
            .Prop("container", _container)
            .BeginObject("opener").Prop("id", _player?.entityId ?? _scope.OpenerId).Prop("name", PartySnapshot.DisplayName(_player)).EndObject()
            .Prop("reason", _reason);
        if (_snapshot != null && _snapshot.PartyId >= 0)
        {
            record.Prop("party_id", _snapshot.PartyId);
            _snapshot.WriteMembers(record, true);
        }
        AuditLog.Commit(record);
    }

    private static void CountMergedKinds(List<ItemStack> _items, out int _learning, out int _otherLoot)
    {
        _learning = 0;
        _otherLoot = 0;
        if (_items == null)
        {
            return;
        }
        GameCatalog catalog = GameCatalog.Current;
        foreach (ItemStack stack in _items)
        {
            ItemClass itemClass = stack?.itemValue?.ItemClass;
            if (GameCatalog.LearningCategory(itemClass) != null)
            {
                _learning += stack.count;
            }
            else if (catalog != null && catalog.IsOtherLoot(itemClass))
            {
                _otherLoot += stack.count;
            }
        }
    }
}
