using System;
using System.Threading;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Frames the opener's progression evaluation for LootProb while the bonus planner probes a loot entry: the prefix
/// (first) records the base before any perk (b₀) and opens the frame, the postfix (last) turns the extra the members'
/// perks would add into a weight and hands it to the planner, and the finalizer always closes the frame (also when
/// vanilla throws). The evaluation's own result is never changed.
/// </summary>
internal static class ProgressionModifyValuePatch
{
    public static void Prefix(Progression __instance, PassiveEffects _effect, ref float _baseVal, out bool __state)
    {
        __state = false;
        if (!MergeContext.Planning)
        {
            return;
        }
        if (_effect != PassiveEffects.LootProb
            || MergeContext.InEval
            || MergeContext.ProgressionActive
            || !ReferenceEquals(__instance.parent, MergeContext.Opener)
            || Thread.CurrentThread.ManagedThreadId != MergeContext.OwnerThread)
        {
            return;
        }
        MergeContext.BeginProgression(_baseVal);
        __state = true;
    }

    public static void Postfix(float _baseVal, bool __state, bool __runOriginal)
    {
        if (!__state || !__runOriginal)
        {
            return;
        }
        try
        {
            MergeEngine.RecordGain(_baseVal);
        }
        catch (Exception e)
        {
            MergeEngine.RegisterError("record_gain", null, e);
        }
    }

    public static Exception Finalizer(Exception __exception, bool __state)
    {
        if (__state)
        {
            MergeContext.EndProgression();
        }
        return __exception;
    }
}
