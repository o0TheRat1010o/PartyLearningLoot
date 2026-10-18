using System;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>
/// Hooks the per-perk evaluation inside the opener's progression evaluation. The prefix (first) captures the values
/// before the perk, the postfix (last) lets <see cref="MergeEngine.MergeClass"/> evaluate the members and record the
/// party's extra for this perk. The opener's own evaluation is the untouched original call and its values are never
/// written here; the extra is applied once at the end of the progression (<see cref="ProgressionModifyValuePatch"/>).
/// Outside a merge activation the prefix returns after one static read.
/// </summary>
internal static class ProgressionClassModifyValuePatch
{
    public struct Capture
    {
        public bool Armed;
        public float BaseIn;
        public float PercIn;
    }

    public static void Prefix(EntityAlive _ea, PassiveEffects _effect, ref float _base_value, ref float _perc_value, out Capture __state)
    {
        __state = default;
        if (!MergeContext.ProgressionActive)
        {
            return;
        }
        if (_effect != PassiveEffects.LootProb || MergeContext.InEval || !ReferenceEquals(_ea, MergeContext.Opener))
        {
            return;
        }
        __state.Armed = true;
        __state.BaseIn = _base_value;
        __state.PercIn = _perc_value;
    }

    public static void Postfix(ProgressionClass __instance, ref float _perc_value, FastTags<TagGroup.Global> _tags, Capture __state, bool __runOriginal)
    {
        if (!__state.Armed || !__runOriginal)
        {
            return;
        }
        try
        {
            MergeEngine.MergeClass(__instance, _perc_value, _tags, __state.BaseIn, __state.PercIn);
        }
        catch (Exception e)
        {
            MergeEngine.RegisterError("merge", __instance?.Name, e);
        }
    }
}
