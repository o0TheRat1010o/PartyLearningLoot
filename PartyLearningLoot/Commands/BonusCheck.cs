using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;
using PartyLearningLoot.Patches;

namespace PartyLearningLoot.Commands;

/// <summary>
/// Self checks of the bonus: the mathematics (<see cref="CheckMath"/>) and the whole mechanism
/// (<see cref="CheckPipeline"/>), which pushes synthetic loot lists through the game's real, patched draw code with and
/// without the bonus and compares the paired results. Needs no party and no player: weights come from fixed tables.
/// Runs with fixed seeds, so a run either always passes or always fails for the same game data.
/// </summary>
internal static class BonusCheck
{
    private const int FlatRuns = 1500;
    private const int NestedRuns = 1000;
    private const int AllRuns = 800;
    private const int DryRuns = 60;
    private const int RuleRuns = 120;
    private const int Slots = 60;
    private const int PickDraws = 3;

    // ----- pure mathematics ---------------------------------------------------------------------------

    public static (string, string) CheckMath()
    {
        var failures = new List<string>();
        void Expect(string _case, double _actual, double _expected, double _tolerance = 1e-9)
        {
            if (!(Math.Abs(_actual - _expected) <= _tolerance))
            {
                failures.Add($"{_case}: {_actual} != {_expected}");
            }
        }

        ListGain gain = BonusMath.Compare(new double[] { 1, 1, 1, 1 }, new double[] { 0, 0, 0, 3 });
        Expect("shares of the unboosted entries sum to the lost share", gain.VanillaShare[0] + gain.VanillaShare[1] + gain.VanillaShare[2], 0.75);
        Expect("gain of the boosted entry", gain.Gain[3], 4d / 7d - 0.25);
        Expect("total gain", gain.TotalGain, 4d / 7d - 0.25);
        Expect("no gain for the others", gain.Gain[0] + gain.Gain[1] + gain.Gain[2], 0d);

        // n · (sv + g) = n · max(sv, sp): never below the opener, never below the party person.
        var random = new Random(4711);
        int cases = 0;
        for (int round = 0; round < 200; round++)
        {
            int count = random.Next(1, 10);
            double[] v = Enumerable.Range(0, count).Select(_ => random.NextDouble() * 3d + 0.05).ToArray();
            double[] e = Enumerable.Range(0, count).Select(_ => random.Next(3) == 0 ? random.NextDouble() * 15d : 0d).ToArray();
            ListGain list = BonusMath.Compare(v, e);
            Expect("shares sum to one", list.VanillaShare.Sum() + list.PartyShare.Sum(), 2d);
            for (int i = 0; i < count; i++)
            {
                double finds = BonusMath.ExpectedFinds(list.VanillaShare[i], list.Gain[i], 1d);
                Expect("expectation is max(vanilla share, party share)", finds, Math.Max(list.VanillaShare[i], list.PartyShare[i]));
            }
            cases++;
        }

        double sum = 0d;
        const int Steps = 4000;
        for (int i = 0; i < Steps; i++)
        {
            sum += BonusMath.RoundUnbiased(2.3, (i + 0.5) / Steps);
        }
        Expect("rounding keeps the expectation", sum / Steps, 2.3, 1e-3);
        Expect("force chance", BonusMath.ForceChance(0.3, 0.8), 0.5);
        Expect("force chance is capped", BonusMath.ForceChance(0.5, 3d), 0.5);
        Expect("force chance is never negative", BonusMath.ForceChance(0.9, 0.2), 0d);
        Expect("negative weight is rejected", BonusMath.Compare(new double[] { 1, -1 }, null).Valid ? 1d : 0d, 0d);

        return failures.Count == 0
            ? (SelfTest.Pass, $"shares, gain, n*max(sv,sp) over {cases} random lists, unbiased rounding, force chance, invalid weights rejected")
            : (SelfTest.Fail, string.Join("; ", failures.Take(4)));
    }

    // ----- the mechanism through the game's draw code ---------------------------------------------

    private sealed class TableWeights : IWeightSource
    {
        public readonly Dictionary<LootContainer.LootEntry, float> Extra = new Dictionary<LootContainer.LootEntry, float>();

        public ProbeResult Probe(LootContainer.LootEntry _entry, bool _eligible, SpawnArgs _args)
        {
            ProbeResult result = ProbeResult.Plain(LootContainer.getProbability(null, _entry, _args.LootStage, _args.IgnoreLootProb));
            if (_eligible && Extra.TryGetValue(_entry, out float extra))
            {
                result.Extra = extra;
                result.Frames = 1;
            }
            return result;
        }
    }

    /// <summary>The synthetic loot tables of the pipeline check: two flat lists and one tree with a pick group and an all group.</summary>
    private sealed class Tables
    {
        public readonly TableWeights Weights = new TableWeights();
        public readonly List<KeyValuePair<string, List<LootContainer.LootEntry>>> Lists = new List<KeyValuePair<string, List<LootContainer.LootEntry>>>();
        public List<LootContainer.LootEntry> FlatPick;
        public List<LootContainer.LootEntry> FlatAll;
        public List<LootContainer.LootEntry> Tree;
        public List<LootContainer.LootEntry> Zero;
        public LootContainer.LootEntry[] FlatPickEntries;
        public LootContainer.LootEntry[] FlatAllEntries;
        public string[] FlatPickItems;
        public string[] FlatAllItems;
        public LootListIndex Index;
    }

    public static (string, string) CheckPipeline()
    {
        PatchGroupStatus group = PatchInstaller.Find(PatchInstaller.LootMergeGroup);
        if (group == null || !group.Installed)
        {
            return (SelfTest.Skip, "the loot patches are not installed");
        }
        if (BonusEngine.Armed || MergeContext.Active)
        {
            return (SelfTest.Skip, "a spawn is running");
        }
        Tables tables = BuildTables(out string missing);
        if (tables == null)
        {
            return (SelfTest.Skip, "game item not found: " + missing);
        }

        var watch = Stopwatch.StartNew();
        GameEventManager events = GameEventManager.HasInstance ? GameEventManager.Current : null;
        GameRandom eventsBefore = events?.Random;
        int eventSampleBefore = eventsBefore?.PeekSample() ?? 0;
        var failures = new List<string>();
        var details = new List<string>();
        try
        {
            RunFlatPick(tables, failures, details);
            RunNested(tables, failures, details);
            RunFlatAll(tables, failures, details);
            RunDry(tables, failures, details);
            RunSpawnRules(tables, failures, details);
            CheckRollback(failures, details);
            CheckRequirementClasses(failures, details);
        }
        catch (Exception e)
        {
            failures.Add("threw " + e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            BonusEngine.End();
        }
        if (events != null && (!ReferenceEquals(events.Random, eventsBefore) || eventsBefore.PeekSample() != eventSampleBefore))
        {
            failures.Add("the game's event random stream was changed");
        }
        if (!BonusEngine.IsIdle)
        {
            failures.Add("the bonus engine kept state");
        }
        details.Add($"{watch.ElapsedMilliseconds} ms");
        return failures.Count == 0
            ? (SelfTest.Pass, string.Join(" | ", details))
            : (SelfTest.Fail, string.Join("; ", failures.Take(4)) + " | " + string.Join(" | ", details));
    }

    private static LootContainer.LootEntry ItemEntry(string _item, float _prob, bool _force, LootContainer.LootGroup _parent)
    {
        ItemValue value = ItemClass.GetItem(_item);
        if (value == null || value.IsEmpty())
        {
            return null;
        }
        return new LootContainer.LootEntry
        {
            prob = _prob,
            forceProb = _force,
            lootProbTemplate = string.Empty,
            minCount = 1,
            maxCount = 1,
            minQuality = -1,
            maxQuality = -1,
            item = new LootContainer.LootItem { itemValue = value },
            modsToInstall = new string[0],
            modChance = 1f,
            randomDurability = true,
            parentGroup = _parent
        };
    }

    private static LootContainer.LootEntry GroupEntry(LootContainer.LootGroup _group, float _prob, bool _force)
    {
        return new LootContainer.LootEntry
        {
            prob = _prob,
            forceProb = _force,
            lootProbTemplate = string.Empty,
            minCount = 1,
            maxCount = 1,
            minQuality = -1,
            maxQuality = -1,
            group = _group,
            modsToInstall = new string[0],
            modChance = 1f,
            randomDurability = true
        };
    }

    private static LootContainer.LootGroup NewGroup(string _name, int _min, int _max)
    {
        return new LootContainer.LootGroup
        {
            name = _name,
            minCount = _min,
            maxCount = _max,
            minQuality = -1,
            maxQuality = -1,
            minLevel = 0f,
            maxLevel = 10000f,
            modsToInstall = new string[0],
            modChance = 1f
        };
    }

    private static Tables BuildTables(out string _missing)
    {
        _missing = null;
        var tables = new Tables();
        string[] names = { "resourceWood", "resourceCloth", "resourcePaper", "resourceNail", "resourceDuctTape" };
        foreach (string name in names)
        {
            ItemValue value = ItemClass.GetItem(name);
            if (value == null || value.IsEmpty())
            {
                _missing = name;
                return null;
            }
        }

        // Flat pick list: five items, the third and fifth raised by the "party" (weights 3,1,1,1,2 plus extras 0,0,4,0,3).
        float[] pickWeights = { 3f, 1f, 1f, 1f, 2f };
        float[] pickExtras = { 0f, 0f, 4f, 0f, 3f };
        tables.FlatPickItems = names;
        tables.FlatPick = new List<LootContainer.LootEntry>();
        for (int i = 0; i < names.Length; i++)
        {
            LootContainer.LootEntry entry = ItemEntry(names[i], pickWeights[i], false, null);
            tables.FlatPick.Add(entry);
            if (pickExtras[i] > 0f)
            {
                tables.Weights.Extra[entry] = pickExtras[i];
            }
        }
        tables.FlatPickEntries = tables.FlatPick.ToArray();

        // Flat all list: independent entries (force_prob); two of them raised, one of them beyond a certain chance.
        float[] allProbs = { 0.30f, 0.20f, 0.50f, 0.90f, 0.10f };
        float[] allExtras = { 0.25f, 0f, 0f, 0.60f, 2.00f };
        tables.FlatAllItems = names;
        tables.FlatAll = new List<LootContainer.LootEntry>();
        for (int i = 0; i < names.Length; i++)
        {
            LootContainer.LootEntry entry = ItemEntry(names[i], allProbs[i], true, null);
            tables.FlatAll.Add(entry);
            if (allExtras[i] > 0f)
            {
                tables.Weights.Extra[entry] = allExtras[i];
            }
        }
        tables.FlatAllEntries = tables.FlatAll.ToArray();

        // A tree: the root picks among items and two groups; the pick group and the all group both hold raised entries.
        LootContainer.LootGroup pickGroup = NewGroup("pllCheckPick", 1, 2);
        pickGroup.items.Add(ItemEntry("resourceWood", 2f, false, pickGroup));
        pickGroup.items.Add(ItemEntry("resourceCloth", 1f, false, pickGroup));
        pickGroup.items.Add(ItemEntry("resourcePaper", 1f, false, pickGroup));
        tables.Weights.Extra[pickGroup.items[1]] = 3f;
        LootContainer.LootGroup allGroup = NewGroup("pllCheckAll", -1, -1);
        allGroup.items.Add(ItemEntry("resourceNail", 0.4f, true, allGroup));
        allGroup.items.Add(ItemEntry("resourceDuctTape", 0.7f, true, allGroup));
        tables.Weights.Extra[allGroup.items[0]] = 0.5f;
        tables.Tree = new List<LootContainer.LootEntry>
        {
            ItemEntry("resourceWood", 2f, false, null),
            ItemEntry("resourceNail", 1f, false, null),
            GroupEntry(pickGroup, 1f, false),
            GroupEntry(allGroup, 1f, false)
        };
        tables.Weights.Extra[tables.Tree[1]] = 2f;
        tables.Weights.Extra[tables.Tree[2]] = 2f;

        // A pick list without any vanilla weight (all probabilities are 0 at some loot stages): the game draws nothing from it.
        tables.Zero = new List<LootContainer.LootEntry>
        {
            ItemEntry("resourceWood", 0f, false, null),
            ItemEntry("resourceCloth", 0f, false, null)
        };
        tables.Weights.Extra[tables.Zero[0]] = 2f;

        tables.Lists.Add(new KeyValuePair<string, List<LootContainer.LootEntry>>("check:zero_pick", tables.Zero));
        tables.Lists.Add(new KeyValuePair<string, List<LootContainer.LootEntry>>("check:flat_pick", tables.FlatPick));
        tables.Lists.Add(new KeyValuePair<string, List<LootContainer.LootEntry>>("check:flat_all", tables.FlatAll));
        tables.Lists.Add(new KeyValuePair<string, List<LootContainer.LootEntry>>("check:tree", tables.Tree));
        tables.Lists.Add(new KeyValuePair<string, List<LootContainer.LootEntry>>("check:pick_group", pickGroup.items));
        tables.Lists.Add(new KeyValuePair<string, List<LootContainer.LootEntry>>("check:all_group", allGroup.items));
        tables.Index = LootListIndex.Build(tables.Lists, entry => tables.Weights.Extra.ContainsKey(entry));
        return tables;
    }

    private sealed class RunResult
    {
        public Dictionary<string, int> Items = new Dictionary<string, int>(StringComparer.Ordinal);
        public int Stacks;
        public int Slots;
        public int SampleAfter;
        public bool Result;
        public BonusTrace Trace;
    }

    /// <summary>One spawn of a synthetic root list, vanilla or with the bonus armed.</summary>
    private static RunResult Spawn(Tables _tables, List<LootContainer.LootEntry> _root, int _numToSpawn, int _seed, bool _bonus, int _bonusSeed, bool _dryRun = false,
        bool _uniqueItems = false, bool _ignoreLootProb = false, bool _detailed = false)
    {
        GameRandom random = GameRandomManager.Instance.CreateGameRandom(_seed);
        var items = new List<ItemStack>();
        var buffs = new List<string>();
        var trace = new BonusTrace();
        int slots = Slots;
        var run = new RunResult { Trace = trace };
        try
        {
            if (_bonus)
            {
                trace.Reset(_detailed, _dryRun);
                BonusEngine.Begin(_tables.Index, _tables.Weights, trace, _bonusSeed, _dryRun);
            }
            run.Result = LootContainer.SpawnLootItemsFromList(random, _root, _numToSpawn, 1f, items, ref slots, 0f, 0f, string.Empty, null,
                FastTags<TagGroup.Global>.none, _uniqueItems, _ignoreLootProb, false, false, buffs, false);
        }
        finally
        {
            BonusEngine.End();
        }
        run.SampleAfter = random.PeekSample();
        GameRandomManager.Instance.FreeGameRandom(random);
        run.Stacks = items.Count;
        run.Slots = slots;
        foreach (ItemStack stack in items)
        {
            string name = stack.itemValue.ItemClass.Name;
            run.Items.TryGetValue(name, out int count);
            run.Items[name] = count + stack.count;
        }
        return run;
    }

    /// <summary>Superset, slots and stream checks of one paired run. Returns null when everything holds.</summary>
    private static string ComparePair(RunResult _vanilla, RunResult _party)
    {
        foreach (KeyValuePair<string, int> pair in _vanilla.Items)
        {
            _party.Items.TryGetValue(pair.Key, out int count);
            if (count < pair.Value)
            {
                return $"{pair.Key}: {count} with the bonus, {pair.Value} without";
            }
        }
        if (_vanilla.SampleAfter != _party.SampleAfter)
        {
            return "the spawn's random stream moved";
        }
        if (_vanilla.Slots - _party.Slots != _party.Stacks - _vanilla.Stacks)
        {
            return $"slots left {_vanilla.Slots} -> {_party.Slots} do not match the stacks {_vanilla.Stacks} -> {_party.Stacks}";
        }
        if (_vanilla.Result && !_party.Result)
        {
            return "the result turned false";
        }
        if (_party.Trace.Violations.Count > 0)
        {
            return "invariant " + _party.Trace.Violations[0].Key + ": " + _party.Trace.Violations[0].Value;
        }
        return null;
    }

    private static double Mean(IEnumerable<double> _values) => _values.Any() ? _values.Average() : 0d;

    // flat pick list: the expected extra finds of every item are n · g

    private static void RunFlatPick(Tables _tables, List<string> _failures, List<string> _details)
    {
        double[] v = _tables.FlatPickEntries.Select(e => (double)e.prob).ToArray();
        double[] extra = _tables.FlatPickEntries.Select(e => _tables.Weights.Extra.TryGetValue(e, out float x) ? (double)x : 0d).ToArray();
        ListGain gain = BonusMath.Compare(v, extra);
        int count = v.Length;
        var sums = new double[count];
        var squares = new double[count];
        var draws = new DrawRateCheck();
        string problem = null;
        for (int run = 0; run < FlatRuns && problem == null; run++)
        {
            int seed = 1000 + run * 7919;
            RunResult vanilla = Spawn(_tables, _tables.FlatPick, PickDraws, seed, false, 0);
            RunResult party = Spawn(_tables, _tables.FlatPick, PickDraws, seed, true, 77 + run * 31);
            problem = ComparePair(vanilla, party);
            for (int i = 0; i < count; i++)
            {
                string name = _tables.FlatPickItems[i];
                vanilla.Items.TryGetValue(name, out int before);
                party.Items.TryGetValue(name, out int after);
                double difference = after - before;
                sums[i] += difference;
                squares[i] += difference * difference;
            }
            draws.Add(party.Trace.PlannedDraws, party.Trace.ExpectedDraws, party.Trace.RoundingVariance);
        }
        if (problem != null)
        {
            _failures.Add("flat pick list: " + problem);
            return;
        }
        double worst = 0d;
        for (int i = 0; i < count; i++)
        {
            double mean = sums[i] / FlatRuns;
            double variance = Math.Max(0d, squares[i] / FlatRuns - mean * mean) * FlatRuns / (FlatRuns - 1);
            double standardError = Math.Sqrt(variance / FlatRuns);
            double expected = PickDraws * gain.Gain[i];
            double z = standardError > 1e-12 ? (mean - expected) / standardError : (Math.Abs(mean - expected) < 1e-9 ? 0d : double.PositiveInfinity);
            worst = Math.Max(worst, Math.Abs(z));
            if (Math.Abs(z) > BonusMath.FailSigma)
            {
                _failures.Add($"flat pick list: item {_tables.FlatPickItems[i]} gained {mean:0.000} per run, expected {expected:0.000} (z {z:0.0})");
            }
        }
        if (!draws.Within())
        {
            _failures.Add($"flat pick list: {draws.Observed:0} bonus draws, expected {draws.Expected:0.0} (z {draws.Z:0.0})");
        }
        _details.Add($"pick {FlatRuns} runs (n={PickDraws}, G {gain.TotalGain:0.000}, worst item z {worst:0.0}, draws z {draws.Z:0.0})");
    }

    // a tree with a pick group and an all group below the root: groups get their own bonus, vanilla stays a subset

    private static void RunNested(Tables _tables, List<string> _failures, List<string> _details)
    {
        var draws = new DrawRateCheck();
        double extraItems = 0d;
        long calls = 0;
        string problem = null;
        for (int run = 0; run < NestedRuns && problem == null; run++)
        {
            int seed = 500000 + run * 6007;
            RunResult vanilla = Spawn(_tables, _tables.Tree, PickDraws, seed, false, 0);
            RunResult party = Spawn(_tables, _tables.Tree, PickDraws, seed, true, 9001 + run * 17);
            problem = ComparePair(vanilla, party);
            draws.Add(party.Trace.PlannedDraws, party.Trace.ExpectedDraws, party.Trace.RoundingVariance);
            extraItems += party.Items.Values.Sum() - vanilla.Items.Values.Sum();
            calls += party.Trace.Calls;
            if (party.Trace.Calls > BonusEngine.MaxCallsPerSpawn)
            {
                problem = $"{party.Trace.Calls} bonus calls exceed the limit";
            }
        }
        if (problem != null)
        {
            _failures.Add("nested lists: " + problem);
            return;
        }
        if (!draws.Within())
        {
            _failures.Add($"nested lists: {draws.Observed:0} bonus draws, expected {draws.Expected:0.0} (z {draws.Z:0.0})");
        }
        if (extraItems <= 0d || calls == 0)
        {
            _failures.Add("nested lists: the bonus never added anything");
        }
        _details.Add($"tree {NestedRuns} runs ({extraItems / NestedRuns:0.00} extra items per run, {calls / (double)NestedRuns:0.0} bonus calls, draws z {draws.Z:0.0})");
    }

    // flat all list: every raised independent entry gets an extra chance q = max(0, min(p,1) - min(v,1))

    private static void RunFlatAll(Tables _tables, List<string> _failures, List<string> _details)
    {
        int count = _tables.FlatAllEntries.Length;
        var sums = new double[count];
        string problem = null;
        for (int run = 0; run < AllRuns && problem == null; run++)
        {
            int seed = 910000 + run * 4001;
            RunResult vanilla = Spawn(_tables, _tables.FlatAll, -1, seed, false, 0);
            RunResult party = Spawn(_tables, _tables.FlatAll, -1, seed, true, 31337 + run * 13);
            problem = ComparePair(vanilla, party);
            for (int i = 0; i < count; i++)
            {
                string name = _tables.FlatAllItems[i];
                vanilla.Items.TryGetValue(name, out int before);
                party.Items.TryGetValue(name, out int after);
                sums[i] += after - before;
            }
        }
        if (problem != null)
        {
            _failures.Add("all list: " + problem);
            return;
        }
        double worst = 0d;
        for (int i = 0; i < count; i++)
        {
            LootContainer.LootEntry entry = _tables.FlatAllEntries[i];
            _tables.Weights.Extra.TryGetValue(entry, out float extra);
            double expected = BonusMath.ForceChance(entry.prob, (double)entry.prob + extra);
            double mean = sums[i] / AllRuns;
            double standardError = Math.Sqrt(Math.Max(expected * (1d - expected), 1e-9) / AllRuns);
            double z = (mean - expected) / standardError;
            worst = Math.Max(worst, Math.Abs(z));
            if (Math.Abs(z) > BonusMath.FailSigma || (expected == 0d && sums[i] != 0d))
            {
                _failures.Add($"all list: item {_tables.FlatAllItems[i]} gained {mean:0.000} per run, expected {expected:0.000} (z {z:0.0})");
            }
        }
        _details.Add($"all {AllRuns} runs (worst item z {worst:0.0})");
    }

    // a dry run plans but never draws: the result must be exactly vanilla

    private static void RunDry(Tables _tables, List<string> _failures, List<string> _details)
    {
        int planned = 0;
        for (int run = 0; run < DryRuns; run++)
        {
            int seed = 77000 + run * 101;
            RunResult vanilla = Spawn(_tables, _tables.Tree, PickDraws, seed, false, 0);
            RunResult dry = Spawn(_tables, _tables.Tree, PickDraws, seed, true, 5 + run, _dryRun: true);
            planned += dry.Trace.PlannedDraws;
            if (dry.Trace.Calls != 0 || dry.Items.Count != vanilla.Items.Count || dry.Items.Any(p => !vanilla.Items.TryGetValue(p.Key, out int c) || c != p.Value) || dry.Stacks != vanilla.Stacks)
            {
                _failures.Add("dry run: the result differs from vanilla");
                return;
            }
        }
        if (planned == 0)
        {
            _failures.Add("dry run: nothing was planned");
        }
        _details.Add($"dry {DryRuns} runs ({planned} draws planned, none made)");
    }

    // spawns that draw without replacement are left alone, a spawn that ignores the loot probability of the perks is not, and a
    // list without any vanilla weight is skipped without a trace of a plan

    private static void RunSpawnRules(Tables _tables, List<string> _failures, List<string> _details)
    {
        int uniqueSkipped = 0;
        int ignoredPlanned = 0;
        int ignoredCalls = 0;
        for (int run = 0; run < RuleRuns; run++)
        {
            int seed = 31000 + run * 211;
            RunResult vanillaUnique = Spawn(_tables, _tables.FlatPick, PickDraws, seed, false, 0, _uniqueItems: true);
            RunResult unique = Spawn(_tables, _tables.FlatPick, PickDraws, seed, true, 3 + run, _uniqueItems: true);
            string problem = ComparePair(vanillaUnique, unique);
            if (problem == null && (unique.Trace.Calls != 0 || unique.Trace.PlannedDraws != 0 || !unique.Trace.Skipped.ContainsKey("unique_items")
                                    || unique.Items.Count != vanillaUnique.Items.Count || unique.Items.Any(p => !vanillaUnique.Items.TryGetValue(p.Key, out int c) || c != p.Value)))
            {
                problem = "the bonus touched a spawn that draws without replacement";
            }
            if (problem != null)
            {
                _failures.Add("unique items: " + problem);
                return;
            }
            uniqueSkipped++;

            RunResult vanillaIgnoring = Spawn(_tables, _tables.FlatPick, PickDraws, seed, false, 0, _ignoreLootProb: true);
            RunResult ignoring = Spawn(_tables, _tables.FlatPick, PickDraws, seed, true, 9 + run, _ignoreLootProb: true);
            problem = ComparePair(vanillaIgnoring, ignoring);
            if (problem == null && ignoring.Trace.Skipped.ContainsKey("ignore_loot_prob"))
            {
                problem = "a spawn that ignores the loot probability was skipped as a whole";
            }
            if (problem != null)
            {
                _failures.Add("ignored loot probability: " + problem);
                return;
            }
            ignoredPlanned += ignoring.Trace.PlannedDraws;
            ignoredCalls += ignoring.Trace.Calls;

            RunResult empty = Spawn(_tables, _tables.Zero, PickDraws, seed, true, 17 + run, _detailed: true);
            if (empty.Trace.Calls != 0 || empty.Items.Count != 0 || empty.Trace.Lists.Count != 0 || empty.Trace.Plans != 1
                || !empty.Trace.Skipped.TryGetValue("invalid_weights", out int skipped) || skipped != 1 || empty.Trace.Violations.Count != 0)
            {
                _failures.Add("list without a vanilla weight: it was drawn from, traced as a plan or not skipped");
                return;
            }
        }
        if (ignoredPlanned == 0 || ignoredCalls == 0)
        {
            _failures.Add("ignored loot probability: the bonus never planned a draw");
        }
        _details.Add($"{uniqueSkipped} unique-item spawns left alone, {ignoredPlanned} draws planned when the loot probability is ignored, empty list skipped");
    }

    // entries whose requirements roll random numbers (or come from a mod) are recognized and left alone

    private sealed class UnknownRequirement : BaseLootEntryRequirement
    {
    }

    private static void CheckRequirementClasses(List<string> _failures, List<string> _details)
    {
        var roll = new LootEntryRequirementRandomRoll();
        roll.Init(System.Xml.Linq.XElement.Parse("<requirement class=\"RandomRoll\" seed_type=\"Random\" min_max=\"0,100\" operation=\"LTE\" value=\"50\"/>"));
        LootContainer.LootEntry Entry(params BaseLootEntryRequirement[] _requirements) =>
            new LootContainer.LootEntry { Requirements = _requirements.Length == 0 ? null : new List<BaseLootEntryRequirement>(_requirements) };

        string rolled = LootListIndex.ClassifyRequirements(Entry(new LootEntryRequirementBiome(), roll));
        string deterministic = LootListIndex.ClassifyRequirements(Entry(new LootEntryRequirementBiome(), new LootEntryRequirementCVar()));
        string none = LootListIndex.ClassifyRequirements(Entry());
        string unknown = LootListIndex.ClassifyRequirements(Entry(new UnknownRequirement()));
        if (rolled != LootListInfo.ProblemRandomRoll || deterministic != null || none != null || unknown != LootListInfo.ProblemUnknownRequirement)
        {
            _failures.Add($"requirements: roll {rolled}, deterministic {deterministic}, none {none}, unknown {unknown}");
            return;
        }
        _details.Add("random-roll and unknown requirements recognized");
    }

    // the rollback puts the vanilla result back exactly

    private static void CheckRollback(List<string> _failures, List<string> _details)
    {
        ItemValue value = ItemClass.GetItem("resourceWood");
        var items = new List<ItemStack> { new ItemStack(value, 4), new ItemStack(ItemClass.GetItem("resourceCloth"), 2) };
        var buffs = new List<string> { "a" };
        BonusEngine.ResultSnapshot snapshot = BonusEngine.ResultSnapshot.Take(items, buffs, 7);
        items[0].count = 9;
        items.Add(new ItemStack(ItemClass.GetItem("resourceNail"), 1));
        buffs.Add("b");
        bool detected = !snapshot.Verify(items, buffs, 8, out _);
        snapshot.Restore(items, buffs);
        bool restored = items.Count == 2 && items[0].count == 4 && items[1].count == 2 && buffs.Count == 1 && snapshot.Verify(items, buffs, 7, out string problem) && problem == null;
        if (!detected || !restored)
        {
            _failures.Add($"rollback: detected {detected}, restored {restored}");
            return;
        }
        _details.Add("rollback restores stacks, list and buffs");
    }
}
