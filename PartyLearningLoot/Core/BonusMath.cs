using System;
using System.Collections.Generic;

namespace PartyLearningLoot.Core;

/// <summary>
/// How the party weights of one loot list differ from the opener's vanilla weights, as shares of the list.
/// Built by <see cref="BonusMath.Compare"/>; all arrays have one slot per list entry.
/// </summary>
public sealed class ListGain
{
    /// <summary>v: the weight the game computes for the opener (vanilla).</summary>
    public double[] Vanilla;

    /// <summary>p = v + e: the weight one person with all perks of the party would have.</summary>
    public double[] Party;

    /// <summary>sv = v / Σv.</summary>
    public double[] VanillaShare;

    /// <summary>sp = p / Σp.</summary>
    public double[] PartyShare;

    /// <summary>g = max(0, sp − sv): the share the party person gets on top of the opener's.</summary>
    public double[] Gain;

    public double SumVanilla;
    public double SumParty;

    /// <summary>G = Σg: the distance of both distributions (always below 1). A bonus draw is needed with probability G per vanilla draw.</summary>
    public double TotalGain;

    /// <summary>False when a weight was negative or not finite, or the vanilla list has no weight at all (the game draws nothing then).</summary>
    public bool Valid;

    /// <summary>The first problem found when <see cref="Valid"/> is false.</summary>
    public string Problem;

    public int Count => Vanilla.Length;
}

/// <summary>
/// Pure mathematics of the additive party bonus (no game references).
///
/// A pick list draws <c>n</c> times, entry i with the share v_i / Σv (vanilla, the opener's perks). One person holding all
/// perks of the party would draw with p_i / Σp. The bonus adds, per vanilla draw, a draw from the "gain list" with the
/// weights g_i = max(0, sp_i − sv_i) with probability G = Σg_i. The expected number of finds of entry i is then
/// <c>n · (sv_i + g_i) = n · max(sv_i, sp_i)</c>: never below vanilla, and at least what the party person would find.
/// </summary>
public static class BonusMath
{
    /// <summary>Largest number of bonus draws a single list may plan (guards against absurd inputs).</summary>
    public const int MaxDrawsPerList = 512;

    /// <summary>Quality gate for the statistical checks: a deviation of this many standard deviations is a failure.</summary>
    public const double FailSigma = 4d;

    public static bool IsFinite(double _value)
    {
        return !double.IsNaN(_value) && !double.IsInfinity(_value);
    }

    /// <summary>
    /// Compares vanilla weights v with the party weights p = v + e of one list. Entries without a gain keep e = 0.
    /// A negative or non-finite weight, a negative or non-finite extra, or an empty vanilla weight sum makes the result invalid
    /// (the caller then leaves the list alone).
    /// </summary>
    public static ListGain Compare(IReadOnlyList<double> _vanilla, IReadOnlyList<double> _extra)
    {
        int count = _vanilla?.Count ?? 0;
        var gain = new ListGain
        {
            Vanilla = new double[count],
            Party = new double[count],
            VanillaShare = new double[count],
            PartyShare = new double[count],
            Gain = new double[count],
            Valid = true
        };
        if (_extra != null && _extra.Count != count)
        {
            gain.Valid = false;
            gain.Problem = $"{_extra.Count} extra weights for {count} entries";
            return gain;
        }
        for (int i = 0; i < count; i++)
        {
            double v = _vanilla[i];
            double e = _extra == null ? 0d : _extra[i];
            if (!IsFinite(v) || v < 0d)
            {
                gain.Valid = false;
                gain.Problem ??= $"entry {i} has the vanilla weight {v}";
                continue;
            }
            if (!IsFinite(e) || e < 0d)
            {
                gain.Valid = false;
                gain.Problem ??= $"entry {i} has the extra weight {e}";
                continue;
            }
            gain.Vanilla[i] = v;
            gain.Party[i] = v + e;
            gain.SumVanilla += v;
            gain.SumParty += v + e;
        }
        if (!gain.Valid)
        {
            return gain;
        }
        if (!(gain.SumVanilla > 0d) || !IsFinite(gain.SumVanilla) || !IsFinite(gain.SumParty))
        {
            gain.Valid = false;
            gain.Problem = "the vanilla weights sum to " + gain.SumVanilla;
            return gain;
        }
        for (int i = 0; i < count; i++)
        {
            gain.VanillaShare[i] = gain.Vanilla[i] / gain.SumVanilla;
            gain.PartyShare[i] = gain.Party[i] / gain.SumParty;
            gain.Gain[i] = Math.Max(0d, gain.PartyShare[i] - gain.VanillaShare[i]);
            gain.TotalGain += gain.Gain[i];
        }
        return gain;
    }

    /// <summary>Expected number of finds of an entry over <paramref name="_draws"/> vanilla draws with the bonus: n · (sv + g).</summary>
    public static double ExpectedFinds(double _vanillaShare, double _gain, double _draws)
    {
        return _draws * (_vanillaShare + _gain);
    }

    /// <summary>
    /// Rounds <paramref name="_x"/> to an integer without bias: floor(x) plus one with probability frac(x), decided by the
    /// uniform number <paramref name="_u"/> in [0, 1). The expectation equals x, the variance is frac(x) · (1 − frac(x)).
    /// </summary>
    public static int RoundUnbiased(double _x, double _u)
    {
        if (!(_x > 0d) || !IsFinite(_x))
        {
            return 0;
        }
        double floor = Math.Floor(_x);
        if (floor >= MaxDrawsPerList)
        {
            return MaxDrawsPerList;
        }
        return (int)floor + (_u < _x - floor ? 1 : 0);
    }

    /// <summary>Variance of <see cref="RoundUnbiased"/> for the input x: f · (1 − f) with f = frac(x).</summary>
    public static double RoundingVariance(double _x)
    {
        if (!(_x > 0d) || !IsFinite(_x))
        {
            return 0d;
        }
        double f = _x - Math.Floor(_x);
        return f * (1d - f);
    }

    /// <summary>
    /// Extra chance of an entry in an "all" list that rolls independently with its weight (force_prob): the party person's
    /// chance min(p, 1) minus the opener's min(v, 1), never negative. The expected count then is max(min(v, 1), min(p, 1)).
    /// </summary>
    public static double ForceChance(double _vanilla, double _party)
    {
        if (!IsFinite(_vanilla) || !IsFinite(_party))
        {
            return 0d;
        }
        return Math.Max(0d, Math.Min(Math.Max(_party, 0d), 1d) - Math.Min(Math.Max(_vanilla, 0d), 1d));
    }

    /// <summary>
    /// Mixes counter, clock and opener into a seed for a private random stream (splitmix-style finalizer), so streams of
    /// neighbouring spawns do not correlate and never depend on the game's own streams.
    /// </summary>
    public static int MixSeed(long _a, long _b, long _c)
    {
        unchecked
        {
            ulong z = (ulong)_a * 0x9E3779B97F4A7C15UL;
            z ^= (ulong)_b + 0x7F4A7C15UL + (z << 6) + (z >> 2);
            z ^= (ulong)_c * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(z ^ (z >> 32));
        }
    }
}

/// <summary>
/// Observed against expected number of events of a random process with known variance (here: bonus draws, which are
/// rounded without bias). The z score says how many standard deviations the observation is off.
/// </summary>
public sealed class DrawRateCheck
{
    public double Observed { get; private set; }
    public double Expected { get; private set; }
    public double Variance { get; private set; }
    public int Samples { get; private set; }

    public void Add(double _observed, double _expected, double _variance)
    {
        Observed += _observed;
        Expected += _expected;
        Variance += _variance;
        Samples++;
    }

    public double StandardDeviation => Math.Sqrt(Math.Max(0d, Variance));

    /// <summary>
    /// (observed − expected) / √(σ² + |observed − expected| / 3); zero when both agree, infinite when they differ although
    /// the variance is zero. The draws are a sum of rounding decisions, which is far from a normal distribution while only
    /// a few round-ups are expected: a single round-up of an expectation of 0.05 is "4.4 σ" on the plain scale, yet happens
    /// in one run of twenty. The widened variance (Bernstein's inequality for sums of terms that stay within 1 of their mean)
    /// judges such small samples fairly and is the plain z score for large ones.
    /// </summary>
    public double Z
    {
        get
        {
            double difference = Observed - Expected;
            if (Variance > 1e-12)
            {
                return difference / Math.Sqrt(Variance + Math.Abs(difference) / 3d);
            }
            return Math.Abs(difference) <= 1e-9 ? 0d : (difference > 0d ? double.PositiveInfinity : double.NegativeInfinity);
        }
    }

    public bool Within(double _sigma = BonusMath.FailSigma)
    {
        return Math.Abs(Z) <= _sigma;
    }
}
