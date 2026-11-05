using System;
using System.Collections.Generic;
using System.Diagnostics;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>The arguments that stay the same for every list of one spawn, taken from the spawn's root call.</summary>
internal sealed class SpawnArgs
{
    /// <summary>The game's random stream of the spawn: the bonus never draws from it (it is only watched).</summary>
    public GameRandom Random;

    public float Abundance;
    public float LootStage;
    public float RareLootChance;
    public string LootQualityTemplate;
    public EntityPlayer Player;
    public FastTags<TagGroup.Global> ContainerTags;
    public bool UniqueItems;
    public bool IgnoreLootProb;
    public bool ForceStacking;
    public bool IgnoreLootAbundance;
    public List<ItemStack> Items;
    public List<string> Buffs;

    public void Clear()
    {
        Random = null;
        Abundance = 1f;
        LootStage = 0f;
        RareLootChance = 0f;
        LootQualityTemplate = null;
        Player = null;
        ContainerTags = FastTags<TagGroup.Global>.none;
        UniqueItems = IgnoreLootProb = ForceStacking = IgnoreLootAbundance = false;
        Items = null;
        Buffs = null;
    }
}

/// <summary>The weights of one list entry for the opener of a spawn.</summary>
internal struct ProbeResult
{
    /// <summary>v: the weight the game computes for the opener (what vanilla draws with).</summary>
    public float Vanilla;

    /// <summary>e: the extra weight of the party (Σ extra · b₀ · sandbox factor), observed while the game evaluated the entry.</summary>
    public float Extra;

    /// <summary>Progression evaluations the probe saw (an eligible entry whose requirements hold has exactly one).</summary>
    public int Frames;

    public float BaseRef;
    public float BaseFinal;

    /// <summary>Σ extra of the perks before it is scaled with b₀.</summary>
    public float ExtraSum;

    public static ProbeResult Plain(float _vanilla)
    {
        return new ProbeResult { Vanilla = _vanilla, BaseRef = float.NaN, BaseFinal = float.NaN, ExtraSum = float.NaN };
    }
}

/// <summary>Source of the entry weights a plan is made from: the game in production, fixed tables for the self check.</summary>
internal interface IWeightSource
{
    ProbeResult Probe(LootContainer.LootEntry _entry, bool _eligible, SpawnArgs _args);
}

/// <summary>
/// Reads the weights through the game's own getProbability. Entries the bonus applies to are evaluated while the merge
/// context observes the progression (<see cref="MergeContext.Planning"/>): the result is still the vanilla weight, the
/// party's extra is recorded on the side.
/// </summary>
internal sealed class GameWeightSource : IWeightSource
{
    public static readonly GameWeightSource Instance = new GameWeightSource();

    public ProbeResult Probe(LootContainer.LootEntry _entry, bool _eligible, SpawnArgs _args)
    {
        if (!_eligible)
        {
            return ProbeResult.Plain(LootContainer.getProbability(_args.Player, _entry, _args.LootStage, _args.IgnoreLootProb));
        }
        ProbeResult result = ProbeResult.Plain(0f);
        MergeContext.BeginProbe();
        try
        {
            result.Vanilla = LootContainer.getProbability(_args.Player, _entry, _args.LootStage, _args.IgnoreLootProb);
            result.Frames = MergeContext.ProbeFrames;
            result.BaseRef = MergeContext.ProbeBaseRef;
            result.BaseFinal = MergeContext.ProbeBaseFinal;
            result.ExtraSum = MergeContext.ProbeExtra;
            result.Extra = MergeContext.ProbeGain * MergeEngine.SandboxFactor(_entry);
        }
        finally
        {
            MergeContext.EndProbe();
        }
        return result;
    }
}

/// <summary>
/// The plan for one list: the weights of its entries, how the party's weights change the shares, and the synthetic list
/// plus weights the bonus draws are made from. Built once per spawn and list.
/// </summary>
internal sealed class ListPlan
{
    public LootListInfo Info;
    public bool IsAll;

    /// <summary>False when the list cannot receive a bonus; <see cref="Problem"/> names why (counted as skipped).</summary>
    public bool Valid;

    public string Problem;
    public ProbeResult[] Probes;
    public bool[] Probed;

    /// <summary>Pick lists: the shares and the gain.</summary>
    public ListGain Gain;

    /// <summary>All lists: the extra chance per entry.</summary>
    public double[] Chance;

    /// <summary>G for pick lists, Σ chances for all lists.</summary>
    public double TotalGain;

    /// <summary>The entries that can receive a bonus (gain / chance above zero), in list order.</summary>
    public List<LootContainer.LootEntry> BonusList = new List<LootContainer.LootEntry>();

    /// <summary>The weight the game's draw code uses for each entry of <see cref="BonusList"/>: g (pick) or the chance q (all).</summary>
    public Dictionary<LootContainer.LootEntry, float> Overrides = new Dictionary<LootContainer.LootEntry, float>();

    public BonusListTrace Trace;
    public double Milliseconds;
}

/// <summary>Builds <see cref="ListPlan"/>s from probes of the entries of a list.</summary>
internal static class PlanBuilder
{
    /// <summary>Smallest total gain / chance worth a bonus; below it float noise would produce draws.</summary>
    public const double MinimumGain = 1e-9;

    public static ListPlan Build(LootListInfo _info, bool _isAll, SpawnArgs _args, IWeightSource _source, BonusTrace _trace)
    {
        long started = Stopwatch.GetTimestamp();
        var plan = new ListPlan { Info = _info, IsAll = _isAll };
        int count = _info.Entries.Length;
        plan.Probes = new ProbeResult[count];
        plan.Probed = new bool[count];
        if (_trace.Detailed)
        {
            plan.Trace = new BonusListTrace { Name = _info.Name, IsAll = _isAll };
        }
        try
        {
            if (_isAll)
            {
                BuildAll(plan, _args, _source, _trace);
            }
            else
            {
                BuildPick(plan, _args, _source, _trace);
            }
        }
        finally
        {
            plan.Milliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
        return plan;
    }

    private static void BuildPick(ListPlan _plan, SpawnArgs _args, IWeightSource _source, BonusTrace _trace)
    {
        LootListInfo info = _plan.Info;
        if (info.PickProblem != null)
        {
            _plan.Problem = info.PickProblem;
            return;
        }
        int count = info.Entries.Length;
        var vanilla = new double[count];
        var extra = new double[count];
        for (int i = 0; i < count; i++)
        {
            ProbeResult probe = _source.Probe(info.Entries[i], info.Eligible[i], _args);
            _plan.Probes[i] = probe;
            _plan.Probed[i] = true;
            vanilla[i] = probe.Vanilla;
            extra[i] = probe.Extra;
            CheckProbe(_trace, info, i, probe);
        }
        ListGain gain = BonusMath.Compare(vanilla, extra);
        _plan.Gain = gain;
        if (!gain.Valid)
        {
            // A weight that is not a number, or no vanilla weight at all (the game draws nothing from such a list either).
            _plan.Problem = "invalid_weights";
            return;
        }
        _plan.TotalGain = gain.TotalGain;
        CheckShares(_trace, info, gain);
        if (gain.TotalGain > MinimumGain)
        {
            for (int i = 0; i < count; i++)
            {
                float weight = (float)gain.Gain[i];
                if (gain.Gain[i] > 0d && weight > 0f)
                {
                    _plan.BonusList.Add(info.Entries[i]);
                    _plan.Overrides[info.Entries[i]] = weight;
                }
            }
        }
        _plan.Valid = true;
        TraceEntries(_plan, _trace);
    }

    private static void BuildAll(ListPlan _plan, SpawnArgs _args, IWeightSource _source, BonusTrace _trace)
    {
        LootListInfo info = _plan.Info;
        int count = info.Entries.Length;
        _plan.Chance = new double[count];
        for (int i = 0; i < count; i++)
        {
            LootContainer.LootEntry entry = info.Entries[i];
            if (!info.Eligible[i] || !entry.forceProb)
            {
                continue;
            }
            if (info.EntryProblem[i] != null)
            {
                _trace.Skip(info.EntryProblem[i]);
                continue;
            }
            ProbeResult probe = _source.Probe(entry, true, _args);
            _plan.Probes[i] = probe;
            _plan.Probed[i] = true;
            CheckProbe(_trace, info, i, probe);
            double chance = BonusMath.ForceChance(probe.Vanilla, (double)probe.Vanilla + probe.Extra);
            _plan.Chance[i] = chance;
            float weight = (float)chance;
            if (chance > MinimumGain && weight > 0f)
            {
                _plan.BonusList.Add(entry);
                _plan.Overrides[entry] = weight;
                _plan.TotalGain += chance;
            }
        }
        _plan.Valid = true;
        TraceEntries(_plan, _trace);
    }

    // ----- checks of the plan -----------------------------------------------------------------------

    /// <summary>The observed extra weight must be exactly what the merge rule gives for the progression numbers of the probe.</summary>
    private static void CheckProbe(BonusTrace _trace, LootListInfo _info, int _index, ProbeResult _probe)
    {
        _trace.InvariantChecks++;
        if (!_info.Eligible[_index])
        {
            if (_probe.Extra != 0f)
            {
                _trace.Violation("plan_non_eligible_zero", $"{_info.Name}/{GameCatalog.EntryName(_info.Entries[_index])}: extra weight {_probe.Extra} on an entry the bonus does not apply to");
            }
            return;
        }
        if (_probe.Frames == 0)
        {
            // No progression evaluation (requirements failed or loot probability ignored): nothing can have been added.
            if (_probe.Extra != 0f)
            {
                _trace.Violation("plan_gain_matches_extra", $"{_info.Name}/{GameCatalog.EntryName(_info.Entries[_index])}: extra weight {_probe.Extra} without a progression evaluation");
            }
            return;
        }
        if (_probe.Frames == 1 && !float.IsNaN(_probe.ExtraSum))
        {
            float sandbox = MergeEngine.SandboxFactor(_info.Entries[_index]);
            double expected = (double)DeltaSelector.GainWeight(_probe.ExtraSum, _probe.BaseRef, _probe.BaseFinal) * sandbox;
            if (!DeltaSelector.AlmostEqual(_probe.Extra, expected))
            {
                _trace.Violation("plan_gain_matches_extra", $"{_info.Name}/{GameCatalog.EntryName(_info.Entries[_index])}: extra weight {_probe.Extra} but extra {_probe.ExtraSum} * b0 {_probe.BaseRef} * sandbox {sandbox} = {expected}");
            }
        }
    }

    private static void CheckShares(BonusTrace _trace, LootListInfo _info, ListGain _gain)
    {
        _trace.InvariantChecks += 2;
        double vanillaSum = 0d;
        double partySum = 0d;
        double gainSum = 0d;
        bool gainsMatch = true;
        for (int i = 0; i < _gain.Count; i++)
        {
            vanillaSum += _gain.VanillaShare[i];
            partySum += _gain.PartyShare[i];
            gainSum += _gain.Gain[i];
            gainsMatch &= DeltaSelector.AlmostEqual(_gain.Gain[i], Math.Max(0d, _gain.PartyShare[i] - _gain.VanillaShare[i]));
        }
        if (Math.Abs(vanillaSum - 1d) > 1e-9 || Math.Abs(partySum - 1d) > 1e-9)
        {
            _trace.Violation("plan_shares", $"{_info.Name}: shares sum to {vanillaSum} (vanilla) and {partySum} (party)");
        }
        if (!gainsMatch || !DeltaSelector.AlmostEqual(gainSum, _gain.TotalGain) || _gain.TotalGain >= 1d)
        {
            _trace.Violation("plan_gain", $"{_info.Name}: gain {gainSum} (G {_gain.TotalGain}) is not the positive part of party share minus vanilla share");
        }
    }

    private static void TraceEntries(ListPlan _plan, BonusTrace _trace)
    {
        if (_plan.Trace == null)
        {
            return;
        }
        LootListInfo info = _plan.Info;
        BonusListTrace list = _plan.Trace;
        if (_plan.Gain != null)
        {
            list.SumVanilla = _plan.Gain.SumVanilla;
            list.SumParty = _plan.Gain.SumParty;
        }
        list.TotalGain = _plan.TotalGain;
        list.EntryCount = info.Entries.Length;
        for (int i = 0; i < info.Entries.Length; i++)
        {
            // Only entries the bonus applies to are listed: the sums cover the whole list, and no other entry can gain a share.
            if (!_plan.Probed[i] || !info.Eligible[i])
            {
                continue;
            }
            ProbeResult probe = _plan.Probes[i];
            LootContainer.LootEntry entry = info.Entries[i];
            var trace = new BonusEntryTrace
            {
                Name = GameCatalog.EntryName(entry),
                Tags = string.Join(",", GameCatalog.EntryTags(entry).GetTagNames()),
                Eligible = info.Eligible[i],
                Probed = true,
                Vanilla = probe.Vanilla,
                Extra = probe.Extra,
                Frames = probe.Frames,
                BaseRef = probe.BaseRef,
                BaseFinal = probe.BaseFinal,
                ExtraSum = probe.ExtraSum,
                Sandbox = MergeEngine.SandboxFactor(entry)
            };
            if (_plan.Gain != null && _plan.Gain.Valid)
            {
                trace.VanillaShare = _plan.Gain.VanillaShare[i];
                trace.PartyShare = _plan.Gain.PartyShare[i];
                trace.Gain = _plan.Gain.Gain[i];
            }
            if (_plan.Chance != null)
            {
                trace.Chance = _plan.Chance[i];
            }
            list.Entries.Add(trace);
        }
    }
}
