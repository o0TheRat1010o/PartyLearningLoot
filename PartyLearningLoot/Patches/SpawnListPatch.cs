using System;
using System.Collections.Generic;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Patch on LootContainer.SpawnLootItemsFromList, the game's draw code for a loot list. While a party spawn is armed the
/// prefix notes every call for a list the bonus applies to, the postfix of the outermost call (after the game's own draws
/// are complete) lets the <see cref="BonusEngine"/> make the bonus and may set the result to true when it added items,
/// and the finalizer keeps the nesting depth right. The game's own draws are never touched. Outside an armed spawn
/// every call costs one static field read.
/// </summary>
internal static class SpawnListPatch
{
    public static void Prefix(GameRandom random, List<LootContainer.LootEntry> itemSet, int numToSpawn, float abundance, List<ItemStack> spawnedItems,
        float lootStage, float rareLootChance, string lootQualityTemplate, EntityPlayer player, FastTags<TagGroup.Global> containerTags, bool uniqueItems,
        bool ignoreLootProb, bool _forceStacking, bool _ignoreLootAbundance, List<string> _buffsToAdd, bool _sandboxModifierUsed,
        out BonusEngine.CallState __state)
    {
        __state = default;
        if (!BonusEngine.Armed)
        {
            return;
        }
        try
        {
            __state = BonusEngine.Enter(random, itemSet, numToSpawn, abundance, spawnedItems, lootStage, rareLootChance, lootQualityTemplate, player,
                containerTags, uniqueItems, ignoreLootProb, _forceStacking, _ignoreLootAbundance, _buffsToAdd, _sandboxModifierUsed);
        }
        catch (Exception e)
        {
            MergeEngine.RegisterError("spawn_list_enter", null, e);
        }
    }

    public static void Postfix(ref bool __result, ref int slotsLeft, BonusEngine.CallState __state)
    {
        if (!__state.Counted)
        {
            return;
        }
        try
        {
            BonusEngine.Leave(__state, ref __result, ref slotsLeft);
        }
        catch (Exception e)
        {
            MergeEngine.RegisterError("spawn_list_leave", null, e);
        }
    }

    public static Exception Finalizer(Exception __exception, BonusEngine.CallState __state)
    {
        if (__state.Counted)
        {
            try
            {
                BonusEngine.Exit(__state, __exception);
            }
            catch (Exception e)
            {
                MergeEngine.RegisterError("spawn_list_exit", null, e);
            }
        }
        return __exception;
    }
}
