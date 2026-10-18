using System;
using System.Collections.Generic;

namespace PartyLearningLoot.Core;

/// <summary>Merge decision for one perk in one LootProb evaluation.</summary>
public readonly struct PerkMerge
{
    /// <summary>Index of the member whose version of the perk wins, or -1 when the opener's own value stands.</summary>
    public readonly int WinnerIndex;
    public readonly float OpenerDelta;
    public readonly float BestMemberDelta;

    /// <summary>
    /// Perc the party adds on top of vanilla for this perk, measured on the reference base: max(0, best member Δ − opener Δ).
    /// </summary>
    public readonly float Extra;
    public readonly int InvalidCandidates;

    public PerkMerge(int _winnerIndex, float _openerDelta, float _bestMemberDelta, float _extra, int _invalidCandidates)
    {
        WinnerIndex = _winnerIndex;
        OpenerDelta = _openerDelta;
        BestMemberDelta = _bestMemberDelta;
        Extra = _extra;
        InvalidCandidates = _invalidCandidates;
    }

    public bool MemberWon => WinnerIndex >= 0;
}

/// <summary>
/// The party merge rule.
///
/// Per perk (one progression class) the opener's vanilla call yields Δ_opener = perc after − perc before; every member's
/// call of the same vanilla code with the member's own level and requirements yields Δ_member. The party adds
/// <c>extra = max(0, max(Δ_member…) − Δ_opener)</c>: identical perks never stack, the strongest one counts.
///
/// After all perks of the opener's progression evaluation the extras are applied once:
/// <c>perc += Σextra · b₀ / b_final</c>, where b₀ is the base before the progression evaluation and b_final the base after
/// it. Without vanilla's "complete" quirk (base_set 1 once a crafting skill reaches its maximum) b₀ = b_final and the
/// rule is simply "perc + Σextra". With the quirk, member bonuses are counted at the entry's normal weight instead of
/// being multiplied by the quirk base, independent of the order in which the perks are evaluated.
///
/// Properties: never below vanilla (extra ≥ 0, bases &gt; 0), deterministic, ties keep vanilla, members without a
/// positive bonus never change anything, singleplayer/no party is exactly vanilla.
/// </summary>
public static class DeltaSelector
{
    public static PerkMerge SelectPerk(float _percIn, float _openerPercOut, IReadOnlyList<float> _memberPercOut)
    {
        float openerDelta = _openerPercOut - _percIn;
        if (!IsFinite(openerDelta))
        {
            // Never touch a broken vanilla value.
            return new PerkMerge(-1, openerDelta, float.NaN, 0f, 0);
        }

        int winner = -1;
        float best = float.NaN;
        int invalid = 0;
        if (_memberPercOut != null)
        {
            for (int i = 0; i < _memberPercOut.Count; i++)
            {
                float delta = _memberPercOut[i] - _percIn;
                if (!IsFinite(delta))
                {
                    invalid++;
                    continue;
                }
                if (float.IsNaN(best) || delta > best)
                {
                    best = delta;
                    if (delta > openerDelta)
                    {
                        winner = i;
                    }
                }
            }
        }
        float extra = winner >= 0 ? best - openerDelta : 0f;
        if (!(extra > 0f))
        {
            winner = -1;
            extra = 0f;
        }
        return new PerkMerge(winner, openerDelta, best, extra, invalid);
    }

    /// <summary>
    /// Applies the accumulated extra of one progression evaluation. Returns the new perc; unchanged when there is no
    /// extra or a base is not positive (a zero-weight entry stays zero, as it would for any single player).
    /// </summary>
    public static float ApplyExtra(float _perc, float _extra, float _baseRef, float _baseFinal)
    {
        if (!(_extra > 0f) || !IsFinite(_extra) || !IsFinite(_perc) || !IsFinite(_baseRef) || !IsFinite(_baseFinal) || !(_baseRef > 0f) || !(_baseFinal > 0f))
        {
            return _perc;
        }
        if (_baseRef == _baseFinal)
        {
            return _perc + _extra;
        }
        return _perc + _extra * (_baseRef / _baseFinal);
    }

    public static bool IsFinite(float _value)
    {
        return !float.IsNaN(_value) && !float.IsInfinity(_value);
    }

    /// <summary>Relative tolerance used when comparing float results (party ≥ vanilla checks).</summary>
    public static bool AlmostLessOrEqual(double _a, double _b)
    {
        return _a <= _b + Math.Max(1e-6, Math.Abs(_b) * 1e-5);
    }

    public static bool AlmostEqual(double _a, double _b)
    {
        return Math.Abs(_a - _b) <= Math.Max(1e-6, Math.Max(Math.Abs(_a), Math.Abs(_b)) * 1e-5);
    }
}
