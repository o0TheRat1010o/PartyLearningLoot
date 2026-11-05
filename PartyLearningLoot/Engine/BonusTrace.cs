using System;
using System.Collections.Generic;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>Weights of one entry of a planned list (Detailed audit): vanilla v, party extra e, shares and gain.</summary>
internal sealed class BonusEntryTrace
{
    public string Name;
    public string Tags;
    public bool Eligible;
    public bool Probed;
    public double Vanilla;
    public double Extra;
    public double VanillaShare;
    public double PartyShare;
    public double Gain;

    /// <summary>All lists: the extra chance of an independent entry.</summary>
    public double Chance;

    // The progression numbers of the probe that produced Extra (eligible entries only).
    public int Frames;
    public float BaseRef = float.NaN;
    public float BaseFinal = float.NaN;
    public float ExtraSum = float.NaN;

    /// <summary>The factor getProbability multiplies the entry's weight with (1 except for treasure maps).</summary>
    public float Sandbox = 1f;
}

/// <summary>One processed visit group of a list: the vanilla draws it stands for and the bonus draws planned for them.</summary>
internal sealed class BonusRunTrace
{
    /// <summary>Pick lists: vanilla draws; all lists: visits.</summary>
    public int Draws;
    public bool Sandbox;

    /// <summary>Expected bonus draws n · G (pick lists) or the number of bonus calls (all lists).</summary>
    public double Expected;

    /// <summary>Planned bonus draws m after the unbiased rounding (pick lists) or bonus calls (all lists).</summary>
    public int Planned;
}

/// <summary>A plan of one list with every processing of it.</summary>
internal sealed class BonusListTrace
{
    public string Name;
    public bool IsAll;

    /// <summary>Entries of the whole list (the sums below cover all of them; only eligible entries are listed).</summary>
    public int EntryCount;
    public double SumVanilla;
    public double SumParty;
    public double TotalGain;
    public readonly List<BonusEntryTrace> Entries = new List<BonusEntryTrace>();
    public readonly List<BonusRunTrace> Runs = new List<BonusRunTrace>();
}

/// <summary>An item type the bonus added to the result.</summary>
internal sealed class BonusAddedItem
{
    public string Name;
    public int Count;
    public string Learn;
    public bool OtherLoot;
}

/// <summary>
/// What the bonus did in one spawn: counters, skipped lists, items added and, at Detailed level, every plan. Reused
/// between spawns; written as the "bonus" object of the spawn record.
/// </summary>
internal sealed class BonusTrace
{
    public bool Detailed;
    public bool DryRun;

    /// <summary>Visits noted (lists with entries the bonus applies to).</summary>
    public int Visits;

    /// <summary>Distinct plans built.</summary>
    public int Plans;

    /// <summary>Calls of the game's draw code made for the bonus.</summary>
    public int Calls;

    /// <summary>Bonus draws planned (the rounded n · G of all pick lists).</summary>
    public int PlannedDraws;

    /// <summary>Σ n · G over all pick-list runs: what the planned draws should average to.</summary>
    public double ExpectedDraws;

    /// <summary>Σ f(1 − f) of the roundings: the variance of <see cref="PlannedDraws"/>.</summary>
    public double RoundingVariance;

    public double MsPlan;
    public double MsBonus;
    public int SlotsLeft = -1;

    /// <summary>Checks of the bonus invariants made (plans, streams, result).</summary>
    public int InvariantChecks;

    public readonly SortedDictionary<string, int> Skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
    public readonly List<BonusAddedItem> Added = new List<BonusAddedItem>();
    public readonly List<BonusListTrace> Lists = new List<BonusListTrace>();

    /// <summary>Invariant violations (rule, detail) found while planning and drawing; the caller reports them.</summary>
    public readonly List<KeyValuePair<string, string>> Violations = new List<KeyValuePair<string, string>>();

    /// <summary>Distinct lists that were planned or skipped.</summary>
    public int DistinctLists;

    public int AddedCount
    {
        get
        {
            int total = 0;
            foreach (BonusAddedItem item in Added)
            {
                total += item.Count;
            }
            return total;
        }
    }

    public bool HasActivity => Visits > 0 || Skipped.Count > 0;

    public void Reset(bool _detailed, bool _dryRun)
    {
        Detailed = _detailed;
        DryRun = _dryRun;
        Visits = 0;
        Plans = 0;
        Calls = 0;
        PlannedDraws = 0;
        ExpectedDraws = 0d;
        RoundingVariance = 0d;
        MsPlan = 0d;
        MsBonus = 0d;
        SlotsLeft = -1;
        DistinctLists = 0;
        InvariantChecks = 0;
        Skipped.Clear();
        Added.Clear();
        Lists.Clear();
        Violations.Clear();
    }

    public void Skip(string _reason, int _count = 1)
    {
        Skipped.TryGetValue(_reason, out int count);
        Skipped[_reason] = count + _count;
    }

    /// <summary>Records an invariant violation; returns false so a check can read "ok || Violation(...)".</summary>
    public bool Violation(string _rule, string _detail)
    {
        Violations.Add(new KeyValuePair<string, string>(_rule, _detail));
        return false;
    }

    /// <summary>Writes the "bonus" object into a spawn record.</summary>
    public void Write(JsonLineWriter _record)
    {
        _record.BeginObject("bonus")
            .Prop("lists", DistinctLists)
            .Prop("visits", Visits)
            .Prop("plans", Plans)
            .Prop("calls", Calls)
            .Prop("planned", PlannedDraws)
            .Prop("expected", Math.Round(ExpectedDraws, 6))
            .Prop("variance", Math.Round(RoundingVariance, 6))
            .Prop("drawn", !DryRun)
            .Prop("ms_plan", Math.Round(MsPlan, 3))
            .Prop("ms_bonus", Math.Round(MsBonus, 3))
            .Prop("slots_left", SlotsLeft);
        _record.BeginArray("items");
        foreach (BonusAddedItem item in Added)
        {
            _record.BeginObject().Prop("name", item.Name).Prop("count", item.Count);
            if (item.Learn != null)
            {
                _record.Prop("learn", item.Learn);
            }
            else if (item.OtherLoot)
            {
                _record.Prop("other_loot", true);
            }
            _record.EndObject();
        }
        _record.EndArray();
        _record.BeginObject("skipped");
        foreach (KeyValuePair<string, int> skipped in Skipped)
        {
            _record.Prop(skipped.Key, skipped.Value);
        }
        _record.EndObject();
        if (Detailed)
        {
            WriteLists(_record);
        }
        _record.EndObject();
    }

    private void WriteLists(JsonLineWriter _record)
    {
        _record.BeginArray("detail");
        foreach (BonusListTrace list in Lists)
        {
            _record.BeginObject()
                .Prop("list", list.Name)
                .Prop("kind", list.IsAll ? "all" : "pick")
                .Prop("entries_total", list.EntryCount)
                .Prop("v_sum", list.SumVanilla)
                .Prop("p_sum", list.SumParty)
                .Prop("g", list.TotalGain);
            _record.BeginArray("entries");
            foreach (BonusEntryTrace entry in list.Entries)
            {
                _record.BeginObject().Prop("entry", entry.Name).Prop("tags", entry.Tags).Prop("elig", entry.Eligible)
                    .Prop("v", entry.Vanilla).Prop("e", entry.Extra);
                if (list.IsAll)
                {
                    _record.Prop("q", entry.Chance);
                }
                else
                {
                    _record.Prop("sv", entry.VanillaShare).Prop("sp", entry.PartyShare).Prop("g", entry.Gain);
                }
                if (entry.Eligible)
                {
                    _record.Prop("frames", entry.Frames);
                    if (!float.IsNaN(entry.ExtraSum))
                    {
                        _record.Prop("b0", entry.BaseRef).Prop("b_final", entry.BaseFinal).Prop("x", entry.ExtraSum);
                    }
                    if (entry.Sandbox != 1f)
                    {
                        _record.Prop("s", entry.Sandbox);
                    }
                }
                _record.EndObject();
            }
            _record.EndArray();
            _record.BeginArray("runs");
            foreach (BonusRunTrace run in list.Runs)
            {
                _record.BeginObject().Prop("n", run.Draws).Prop("sandbox", run.Sandbox).Prop("x", Math.Round(run.Expected, 6)).Prop("m", run.Planned).EndObject();
            }
            _record.EndArray();
            _record.EndObject();
        }
        _record.EndArray();
    }
}
