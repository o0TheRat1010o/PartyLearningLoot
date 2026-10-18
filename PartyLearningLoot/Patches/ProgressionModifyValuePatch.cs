using System;
using System.Threading;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Frames the opener's progression evaluation for LootProb inside a merge activation: the prefix (first) records the
/// base before any perk (b₀) and opens the frame, the postfix (last) adds the party's accumulated extra once, scaled to
/// b₀, and the finalizer always closes the frame (also when vanilla throws).
/// </summary>
internal static class ProgressionModifyValuePatch
{
    public static void Prefix(Progression __instance, PassiveEffects _effect, ref float _baseVal, out bool __state)
    {
        __state = false;
        if (!MergeContext.Active)
        {
            return;
        }
        if (_effect != PassiveEffects.LootProb
            || MergeContext.InEval
            || MergeContext.SuppressMerge
            || MergeContext.ProgressionActive
            || !ReferenceEquals(__instance.parent, MergeContext.Opener)
            || Thread.CurrentThread.ManagedThreadId != MergeContext.OwnerThread)
        {
            return;
        }
        MergeContext.BeginProgression(_baseVal);
        __state = true;
    }

    public static void Postfix(ref float _baseVal, ref float _percVal, bool __state, bool __runOriginal)
    {
        if (!__state || !__runOriginal)
        {
            return;
        }
        try
        {
            MergeEngine.ApplyProgressionExtra(ref _percVal, _baseVal);
        }
        catch (Exception e)
        {
            MergeEngine.RegisterError("apply_extra", null, e);
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
