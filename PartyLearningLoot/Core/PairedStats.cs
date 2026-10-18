using System;
using System.Collections.Generic;
using System.Linq;

namespace PartyLearningLoot.Core;

/// <summary>
/// Paired comparison of two runs that share the same random seed (vanilla vs party).
/// Uses Welford's algorithm on the per-pair difference, so the result is numerically stable for large run counts.
/// </summary>
public sealed class PairedStats
{
    private double meanVanilla;
    private double meanParty;
    private double meanDiff;
    private double m2Diff;

    public int Count { get; private set; }
    public int PartyHigher { get; private set; }
    public int VanillaHigher { get; private set; }
    public int Equal { get; private set; }

    public double MeanVanilla => meanVanilla;
    public double MeanParty => meanParty;
    public double MeanDifference => meanDiff;
    public double StdDevDifference => Count > 1 ? Math.Sqrt(m2Diff / (Count - 1)) : 0d;
    public double StdErrDifference => Count > 1 ? StdDevDifference / Math.Sqrt(Count) : 0d;
    public double Ci95Low => meanDiff - 1.96 * StdErrDifference;
    public double Ci95High => meanDiff + 1.96 * StdErrDifference;

    /// <summary>Relative change of the party mean against the vanilla mean (NaN when vanilla is zero).</summary>
    public double RelativeChange => meanVanilla == 0d ? double.NaN : (meanParty - meanVanilla) / meanVanilla;

    public void Add(double _vanilla, double _party)
    {
        Count++;
        double diff = _party - _vanilla;
        meanVanilla += (_vanilla - meanVanilla) / Count;
        meanParty += (_party - meanParty) / Count;
        double delta = diff - meanDiff;
        meanDiff += delta / Count;
        m2Diff += delta * (diff - meanDiff);
        if (diff > 0)
        {
            PartyHigher++;
        }
        else if (diff < 0)
        {
            VanillaHigher++;
        }
        else
        {
            Equal++;
        }
    }
}

/// <summary>Counts occurrences per key for two paired runs (e.g. learning items by name).</summary>
public sealed class PairedCounter
{
    private readonly Dictionary<string, int[]> counts = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

    public void Add(string _key, bool _party, int _amount = 1)
    {
        if (!counts.TryGetValue(_key, out int[] slot))
        {
            slot = new int[2];
            counts.Add(_key, slot);
        }
        slot[_party ? 1 : 0] += _amount;
    }

    public int Vanilla(string _key) => counts.TryGetValue(_key, out int[] slot) ? slot[0] : 0;
    public int Party(string _key) => counts.TryGetValue(_key, out int[] slot) ? slot[1] : 0;
    public int Keys => counts.Count;

    /// <summary>Keys ordered by the absolute change between the runs, largest first.</summary>
    public IEnumerable<KeyValuePair<string, int[]>> ByChange()
    {
        return counts.OrderByDescending(kv => Math.Abs(kv.Value[1] - kv.Value[0])).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);
    }
}
