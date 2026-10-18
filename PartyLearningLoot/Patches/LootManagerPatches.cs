using System;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Opens a loot scope while LootManager spawns the contents of a world container or an entity loot bag.
/// Only <see cref="LootContainer.Spawn"/> calls inside such a scope, for the opener named by the scope, may merge;
/// bundles, quest rewards and trader stock never pass through here and therefore always stay vanilla.
/// </summary>
internal static class LootManagerPatches
{
    internal static readonly ScopeStack<LootScope> Scopes = new ScopeStack<LootScope>();

    public static void ContainerOpenedPrefix(TEFeatureStorage _tileEntity, int _entityIdThatOpenedIt, out ScopeStack<LootScope>.Token __state)
    {
        __state = default;
        if (!PartyLearningLootMod.Settings.Enabled || !ThreadManager.IsMainThread())
        {
            return;
        }
        __state = Scopes.Enter(new LootScope(LootOpenKind.Container, _entityIdThatOpenedIt, _tileEntity?.lootListName));
    }

    public static void BagOpenedPrefix(Entity _bagOwner, int _entityIdThatOpenedIt, out ScopeStack<LootScope>.Token __state)
    {
        __state = default;
        if (!PartyLearningLootMod.Settings.Enabled || !ThreadManager.IsMainThread())
        {
            return;
        }
        string lootList = null;
        try
        {
            lootList = _bagOwner?.GetLootList();
        }
        catch
        {
            // The name is informative only; vanilla validates the bag itself.
        }
        __state = Scopes.Enter(new LootScope(LootOpenKind.LootBag, _entityIdThatOpenedIt, lootList));
    }

    /// <summary>Always restores the previous scope, also when vanilla throws (the exception is passed on unchanged).</summary>
    public static Exception OpenedFinalizer(Exception __exception, ScopeStack<LootScope>.Token __state)
    {
        Scopes.Exit(__state);
        return __exception;
    }
}
