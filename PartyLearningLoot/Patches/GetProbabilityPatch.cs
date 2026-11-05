using System;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Patch on LootContainer.getProbability. The prefix gives the game's draw code the bonus weights while the bonus
/// engine draws from its synthetic list (and only then; every other call runs the original untouched). The postfix is
/// an audit of the opener's own draws inside a merge activation: it counts them and, at Detailed level, checks the
/// result against the vanilla formula. It never changes a result.
/// </summary>
internal static class GetProbabilityPatch
{
    public static bool Prefix(LootContainer.LootEntry _item, ref float __result)
    {
        if (!BonusEngine.Armed)
        {
            return true;
        }
        if (BonusEngine.TryOverride(_item, out float weight))
        {
            __result = weight;
            return false;
        }
        return true;
    }

    public static void Postfix(EntityPlayer _player, LootContainer.LootEntry _item, float _lootstage, bool _ignoreLootProb, float __result)
    {
        if (!MergeContext.Active
            || BonusEngine.InFlush
            || MergeContext.InAudit
            || MergeContext.Kind == LootOpenKind.Simulate
            || MergeContext.Kind == LootOpenKind.Explain
            || !ReferenceEquals(_player, MergeContext.Opener)
            || !AuditLog.Summary)
        {
            return;
        }
        try
        {
            MergeEngine.AuditProbability(_player, _item, _lootstage, _ignoreLootProb, __result);
        }
        catch (Exception e)
        {
            ModLog.Error("Loot probability audit failed.", e, "audit_probability");
        }
    }
}
