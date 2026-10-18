using System;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Audit-only patch on LootContainer.getProbability: inside a merge activation the prefix clears the numbers of the
/// previous progression evaluation, the postfix reports the final probability of every entry the opener evaluates
/// (and, at Detailed level, the exact vanilla value). It never changes the result.
/// </summary>
internal static class GetProbabilityAuditPatch
{
    public static void Prefix(EntityPlayer _player)
    {
        if (MergeContext.Active && !MergeContext.InAudit && !MergeContext.InEval && ReferenceEquals(_player, MergeContext.Opener))
        {
            MergeContext.ResetLastProgression();
        }
    }

    public static void Postfix(EntityPlayer _player, LootContainer.LootEntry _item, float _lootstage, bool _ignoreLootProb, float __result)
    {
        if (!MergeContext.Active
            || MergeContext.InEval
            || MergeContext.SuppressMerge
            || MergeContext.InAudit
            || MergeContext.Kind == LootOpenKind.Simulate
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
