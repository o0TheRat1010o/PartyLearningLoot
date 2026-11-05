using System;
using System.Collections.Generic;
using System.Linq;

namespace PartyLearningLoot.Core;

/// <summary>Paired result of one item over all runs: how many were found per run in the vanilla and in the party run.</summary>
public sealed class ItemDelta
{
    public string Name;

    /// <summary>Total finds over all runs.</summary>
    public long VanillaTotal;
    public long PartyTotal;

    /// <summary>Mean of (party − vanilla) per run.</summary>
    public double Mean;
    public double StdErr;
    public double Ci95Low;
    public double Ci95High;

    /// <summary>Smallest (party − vanilla) of any run (zero or less; runs without the item count as 0).</summary>
    public int Min;

    /// <summary>Runs in which the party run found fewer of this item than the vanilla run.</summary>
    public int RunsBelow;

    public double VanillaMean(int _runs) => _runs == 0 ? 0d : (double)VanillaTotal / _runs;
    public double PartyMean(int _runs) => _runs == 0 ? 0d : (double)PartyTotal / _runs;
}

/// <summary>
/// Paired comparison of the item counts of two runs that share a random seed (vanilla and party). Every run adds one
/// count dictionary per side; the statistics are kept per item name (case-insensitive), sparse over the items that
/// appeared. The party run must contain the vanilla run, so <see cref="RunsWithItemBelowVanilla"/> has to stay zero.
/// </summary>
public sealed class ItemDeltaStats
{
    private sealed class Slot
    {
        public long Vanilla;
        public long Party;
        public double Sum;
        public double SumSquares;
        public int MinSeen = int.MaxValue;
        public int Appeared;
        public int RunsBelow;
    }

    private readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paired runs added so far.</summary>
    public int Runs { get; private set; }

    /// <summary>Runs in which at least one item was found less often in the party run.</summary>
    public int RunsWithItemBelowVanilla { get; private set; }

    /// <summary>Distinct items seen.</summary>
    public int ItemCount => slots.Count;

    /// <summary>
    /// 95 % upper bound of the true rate of runs below vanilla when none was observed ("rule of three": 3 / runs);
    /// NaN when a run was below vanilla or no run exists.
    /// </summary>
    public double RuleOfThreeUpperBound => Runs > 0 && RunsWithItemBelowVanilla == 0 ? 3d / Runs : double.NaN;

    /// <summary>Adds one paired run. Returns how many items the party run found less often than the vanilla run.</summary>
    public int AddRun(IReadOnlyDictionary<string, int> _vanilla, IReadOnlyDictionary<string, int> _party)
    {
        Runs++;
        int below = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_vanilla != null)
        {
            foreach (KeyValuePair<string, int> pair in _vanilla)
            {
                seen.Add(pair.Key);
            }
        }
        if (_party != null)
        {
            foreach (KeyValuePair<string, int> pair in _party)
            {
                seen.Add(pair.Key);
            }
        }
        foreach (string name in seen)
        {
            int vanilla = Lookup(_vanilla, name);
            int party = Lookup(_party, name);
            int difference = party - vanilla;
            if (!slots.TryGetValue(name, out Slot slot))
            {
                slot = new Slot();
                slots.Add(name, slot);
            }
            slot.Vanilla += vanilla;
            slot.Party += party;
            slot.Sum += difference;
            slot.SumSquares += (double)difference * difference;
            slot.MinSeen = Math.Min(slot.MinSeen, difference);
            slot.Appeared++;
            if (difference < 0)
            {
                slot.RunsBelow++;
                below++;
            }
        }
        if (below > 0)
        {
            RunsWithItemBelowVanilla++;
        }
        return below;
    }

    /// <summary>The statistics of one item (zeros for an item that never appeared).</summary>
    public ItemDelta Get(string _name)
    {
        return slots.TryGetValue(_name ?? string.Empty, out Slot slot) ? Build(_name, slot) : new ItemDelta { Name = _name };
    }

    /// <summary>All items, in no particular order.</summary>
    public IEnumerable<ItemDelta> Items()
    {
        return slots.Select(pair => Build(pair.Key, pair.Value));
    }

    /// <summary>The items with the lowest mean difference first (ties: lowest minimum, then name).</summary>
    public IEnumerable<ItemDelta> Weakest(int _count)
    {
        return Items().OrderBy(i => i.Mean).ThenBy(i => i.Min).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, _count));
    }

    private ItemDelta Build(string _name, Slot _slot)
    {
        double n = Runs;
        double mean = n > 0 ? _slot.Sum / n : 0d;
        double stdErr = 0d;
        if (Runs > 1)
        {
            // Runs without the item contribute a difference of zero.
            double m2 = Math.Max(0d, _slot.SumSquares - _slot.Sum * _slot.Sum / n);
            stdErr = Math.Sqrt(m2 / (n - 1d) / n);
        }
        int min = _slot.MinSeen;
        if (_slot.Appeared < Runs)
        {
            min = Math.Min(min, 0);
        }
        return new ItemDelta
        {
            Name = _name,
            VanillaTotal = _slot.Vanilla,
            PartyTotal = _slot.Party,
            Mean = mean,
            StdErr = stdErr,
            Ci95Low = mean - 1.96 * stdErr,
            Ci95High = mean + 1.96 * stdErr,
            Min = min,
            RunsBelow = _slot.RunsBelow
        };
    }

    private static int Lookup(IReadOnlyDictionary<string, int> _counts, string _name)
    {
        return _counts != null && _counts.TryGetValue(_name, out int value) ? value : 0;
    }
}
