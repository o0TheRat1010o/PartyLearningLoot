using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Commands;

internal sealed class SimulationResult
{
    public string Container;
    public EntityPlayer Player;
    public PartySnapshot Snapshot;
    public int RequestedRuns;
    public int Runs;
    public float LootStage;
    public double Seconds;
    public bool TimeLimited;
    public double TimeBudget;
    public readonly PairedStats LearningItems = new PairedStats();
    public readonly PairedStats OtherLootItems = new PairedStats();
    public readonly PairedStats AllItems = new PairedStats();
    public bool IncludesOtherLoot;

    /// <summary>Per item: finds in the vanilla run against the party run of the same seed.</summary>
    public readonly ItemDeltaStats Items = new ItemDeltaStats();

    /// <summary>Bonus draws made against the draws the plans expected.</summary>
    public readonly DrawRateCheck Draws = new DrawRateCheck();

    /// <summary>An item the caller asked to follow explicitly (any item, with or without a perk tag).</summary>
    public string FocusItem;

    public int BonusCalls;
    public int Violations;
    public readonly SortedDictionary<string, int> Skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
    public readonly List<string> ViolationRules = new List<string>();
    public string Error;

    public bool PartyActive => Snapshot != null && Snapshot.VanillaReason == null;
}

/// <summary>
/// Paired loot simulation for one container: every run is spawned twice with the same seed, once vanilla and once with
/// the party bonus, without placing anything in the world. Requirement rolls use a private random generator that is
/// seeded per run too, so the global game-event random state is restored afterwards and the pairs stay comparable.
/// Buffs that loot entries would grant go to a throwaway list. The party run must contain the vanilla run, so no item may
/// ever be found less often with the party: that is counted over all items, not only the learning ones.
/// </summary>
internal static class LootSimulator
{
    public const int DefaultRuns = 2000;
    public const int MaxRuns = 100000;

    /// <summary>The simulation stops after this long (the game does not tick meanwhile): enough for the default runs.</summary>
    public const double TimeBudgetSeconds = 5d;

    /// <summary>Upper limit for the time budget of large explicit requests.</summary>
    public const double MaxTimeBudgetSeconds = 60d;

    public const int WeakestItems = 5;

    /// <summary>Time budget for a request: the default, more for large run counts (about 2 ms per paired run), at most the limit.</summary>
    public static double BudgetFor(int _runs)
    {
        return Math.Min(MaxTimeBudgetSeconds, Math.Max(TimeBudgetSeconds, _runs * 0.002d));
    }

    public static SimulationResult Run(string _containerName, int _runs, EntityPlayer _player, ModSettings _settings, string _focusItem = null, double _timeBudgetSeconds = -1d)
    {
        double budget = _timeBudgetSeconds > 0d ? _timeBudgetSeconds : BudgetFor(_runs);
        var result = new SimulationResult { Container = _containerName, Player = _player, RequestedRuns = _runs, TimeBudget = budget };
        LootContainer container = LootContainer.GetLootContainer(_containerName, false);
        if (container == null)
        {
            result.Error = $"unknown loot container '{_containerName}'";
            return result;
        }
        if (MergeContext.Active || BonusEngine.Armed)
        {
            result.Error = "a merge is already running";
            return result;
        }
        if (!string.IsNullOrEmpty(_focusItem))
        {
            ItemValue focus = ItemClass.GetItem(_focusItem, true);
            if (focus == null || focus.IsEmpty())
            {
                result.Error = $"unknown item '{_focusItem}'";
                return result;
            }
            result.FocusItem = focus.ItemClass.Name;
        }
        result.Container = container.Name;
        GameCatalog catalog = GameCatalog.Current;
        result.IncludesOtherLoot = catalog != null && catalog.Data.IncludesOtherLoot;
        result.Snapshot = PartySnapshot.Capture(_player, _settings.MaxMemberDistance, _settings.IncludeDeadMembers, _settings.IncludeSpectators);
        result.LootStage = container.useUnmodifiedLootstage ? _player.unModifiedGameStage : _player.GetHighestPartyLootStage(0f, 0f);

        GameEventManager events = GameEventManager.HasInstance ? GameEventManager.Current : null;
        GameRandom originalEventRandom = events?.Random;
        int baseSeed = Environment.TickCount;
        var vanillaCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var partyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < _runs; i++)
            {
                if (watch.Elapsed.TotalSeconds > budget)
                {
                    result.TimeLimited = true;
                    break;
                }
                int seed = unchecked(baseSeed + i * 7919);
                List<ItemStack> vanilla = SpawnOnce(container, _player, seed, result.LootStage, events, null, result);
                List<ItemStack> party = SpawnOnce(container, _player, seed, result.LootStage, events, result.PartyActive ? result.Snapshot : null, result);
                result.LearningItems.Add(CountItems(vanilla, ItemKind.Learning, catalog, null), CountItems(party, ItemKind.Learning, catalog, null));
                if (result.IncludesOtherLoot)
                {
                    result.OtherLootItems.Add(CountItems(vanilla, ItemKind.OtherLoot, catalog, null), CountItems(party, ItemKind.OtherLoot, catalog, null));
                }
                vanillaCounts.Clear();
                partyCounts.Clear();
                result.AllItems.Add(CountItems(vanilla, ItemKind.All, catalog, vanillaCounts), CountItems(party, ItemKind.All, catalog, partyCounts));
                result.Items.AddRun(vanillaCounts, partyCounts);
                result.Runs++;
            }
        }
        catch (Exception e)
        {
            result.Error = e.GetType().Name + ": " + e.Message;
            ModLog.Error("Loot simulation failed.", e, "simulate");
        }
        finally
        {
            if (events != null)
            {
                events.Random = originalEventRandom;
            }
            if (!MergeContext.IsClean())
            {
                MergeContext.Reset();
            }
        }
        result.Seconds = watch.Elapsed.TotalSeconds;
        return result;
    }

    private static List<ItemStack> SpawnOnce(LootContainer _container, EntityPlayer _player, int _seed, float _lootStage, GameEventManager _events, PartySnapshot _party, SimulationResult _result)
    {
        GameRandom random = GameRandomManager.Instance.CreateGameRandom(_seed);
        GameRandom eventRandom = GameRandomManager.Instance.CreateGameRandom(unchecked(_seed ^ 0x5bd1e995));
        ActivationToken token = default;
        try
        {
            if (_events != null)
            {
                _events.Random = eventRandom;
            }
            if (_party != null)
            {
                token = MergeContext.Activate(_player, _party, LootOpenKind.Simulate, _container.Name, false);
                if (token.Activated)
                {
                    MergeContext.Trace.Bonus.Detailed = false;
                    MergeContext.ArmBonus(BonusMath.MixSeed(_seed, 0x51ed270b, _player.entityId));
                }
            }
            List<ItemStack> items = SpawnWithoutSideEffects(_container, random, _player, _lootStage);
            if (token.Activated)
            {
                Collect(MergeContext.Trace.Bonus, _result);
            }
            return items;
        }
        finally
        {
            MergeContext.Deactivate(token);
            GameRandomManager.Instance.FreeGameRandom(random);
            GameRandomManager.Instance.FreeGameRandom(eventRandom);
        }
    }

    /// <summary>Takes the bonus figures of one party run: draws against expectation, calls, skipped lists, invariant violations.</summary>
    private static void Collect(BonusTrace _bonus, SimulationResult _result)
    {
        _result.Draws.Add(_bonus.PlannedDraws, _bonus.ExpectedDraws, _bonus.RoundingVariance);
        _result.BonusCalls += _bonus.Calls;
        foreach (KeyValuePair<string, int> skipped in _bonus.Skipped)
        {
            _result.Skipped.TryGetValue(skipped.Key, out int count);
            _result.Skipped[skipped.Key] = count + skipped.Value;
        }
        foreach (KeyValuePair<string, string> violation in _bonus.Violations)
        {
            _result.Violations++;
            if (!_result.ViolationRules.Contains(violation.Key) && _result.ViolationRules.Count < 8)
            {
                _result.ViolationRules.Add(violation.Key);
            }
        }
        _bonus.Violations.Clear();
    }

    /// <summary>The body of LootContainer.Spawn minus the buff side effect (buffs are collected and discarded).</summary>
    private static List<ItemStack> SpawnWithoutSideEffects(LootContainer _container, GameRandom _random, EntityPlayer _player, float _lootStage)
    {
        int maxItems = Math.Max(1, _container.size.x * _container.size.y);
        float abundance = 1f;
        float abundanceCount = 1f;
        if (LootContainer.GlobalCountModifier <= 1f)
        {
            abundance = LootContainer.GlobalCountModifier;
        }
        else
        {
            abundanceCount = LootContainer.GlobalCountModifier;
        }
        int count = Math.Min(LootContainer.RandomSpawnCount(_random, _container.minCount, _container.maxCount, abundanceCount), maxItems);
        var items = new List<ItemStack>();
        var discardedBuffs = new List<string>();
        int slots = maxItems;
        LootContainer.SpawnLootItemsFromList(_random, _container.itemsToSpawn, count, abundance, items, ref slots, _lootStage, 0f,
            _container.lootQualityTemplate, _player, FastTags<TagGroup.Global>.none, _container.UniqueItems, _container.IgnoreLootProb,
            false, false, discardedBuffs, false);
        return items;
    }

    private enum ItemKind
    {
        Learning,
        OtherLoot,
        All
    }

    private static int CountItems(List<ItemStack> _items, ItemKind _kind, GameCatalog _catalog, Dictionary<string, int> _byName)
    {
        int total = 0;
        foreach (ItemStack stack in _items)
        {
            ItemClass itemClass = stack?.itemValue?.ItemClass;
            if (itemClass == null)
            {
                continue;
            }
            bool counts = _kind switch
            {
                ItemKind.Learning => GameCatalog.LearningCategory(itemClass) != null,
                ItemKind.OtherLoot => _catalog != null && _catalog.IsOtherLoot(itemClass),
                _ => true
            };
            if (!counts)
            {
                continue;
            }
            total += stack.count;
            if (_byName != null)
            {
                _byName.TryGetValue(itemClass.Name, out int count);
                _byName[itemClass.Name] = count + stack.count;
            }
        }
        return total;
    }

    // ----- output ------------------------------------------------------------------------------------------

    public static IEnumerable<string> Format(SimulationResult _result)
    {
        if (_result.Error != null && _result.Runs == 0)
        {
            yield return "simulate: " + _result.Error;
            yield break;
        }
        PartySnapshot snapshot = _result.Snapshot;
        yield return $"Simulation of '{_result.Container}' for {PartySnapshot.DisplayName(_result.Player)}: {_result.Runs} paired runs" +
                     $"{(_result.TimeLimited ? $" (stopped after {_result.TimeBudget:0} s of {_result.RequestedRuns})" : string.Empty)}, loot stage {_result.LootStage:0}, {_result.Seconds:0.00} s";
        yield return snapshot.VanillaReason != null
            ? $"Party: none ({snapshot.VanillaReason}) - both runs are vanilla"
            : $"Party: {string.Join(", ", snapshot.Members.Select(m => m.Candidate.Name))}";
        var table = new TextTable("Per container", "Vanilla", "Party", "Change", "95% CI of change").AlignRight(1, 2, 3, 4);
        AddRow(table, "learning items", _result.LearningItems);
        if (_result.IncludesOtherLoot)
        {
            AddRow(table, "other perk loot", _result.OtherLootItems);
        }
        AddRow(table, "all items", _result.AllItems);
        foreach (string line in table.Lines())
        {
            yield return line;
        }

        ItemDeltaStats items = _result.Items;
        if (items.RunsWithItemBelowVanilla == 0)
        {
            yield return $"Runs with an item below vanilla: 0 of {items.Runs}" + (items.Runs > 0 ? $" (95% upper bound for the rate of such runs: {items.RuleOfThreeUpperBound * 100d:0.###}%)" : string.Empty);
        }
        else
        {
            yield return $"Runs with an item below vanilla: {items.RunsWithItemBelowVanilla} of {items.Runs} - FAILED (the party run must contain the vanilla run)";
        }
        if (_result.PartyActive)
        {
            DrawRateCheck draws = _result.Draws;
            yield return $"Bonus draws: {draws.Observed:0} made, {draws.Expected:0.0} expected (+/- {draws.StandardDeviation:0.0}, z {draws.Z:+0.0;-0.0;0.0}): {(draws.Within() ? "OK" : "FAILED")}" +
                         $"; {_result.BonusCalls} calls of the game's draw code" +
                         (_result.Skipped.Count > 0 ? "; skipped lists: " + string.Join(", ", _result.Skipped.Select(s => $"{s.Key} {s.Value}")) : string.Empty);
        }
        if (_result.Violations > 0)
        {
            yield return $"Invariant violations during the simulation: {_result.Violations} ({string.Join(", ", _result.ViolationRules)}) - FAILED";
        }
        if (items.ItemCount > 0)
        {
            var weakest = new TextTable("Item with the lowest change", "Vanilla/run", "Party/run", "Change/run", "95% CI", "Worst run").AlignRight(1, 2, 3, 4, 5);
            foreach (ItemDelta item in items.Weakest(WeakestItems))
            {
                AddItemRow(weakest, item, items.Runs);
            }
            foreach (string line in weakest.Lines())
            {
                yield return line;
            }
        }
        if (_result.FocusItem != null)
        {
            ItemDelta focus = items.Get(_result.FocusItem);
            var focusTable = new TextTable("Item", "Vanilla/run", "Party/run", "Change/run", "95% CI", "Worst run").AlignRight(1, 2, 3, 4, 5);
            AddItemRow(focusTable, focus, items.Runs);
            foreach (string line in focusTable.Lines())
            {
                yield return line;
            }
            if (focus.VanillaTotal == 0 && focus.PartyTotal == 0)
            {
                yield return $"'{_result.FocusItem}' was not found in any of the {items.Runs} runs (it may not be part of this container).";
            }
        }
        if (_result.Error != null)
        {
            yield return "Stopped early: " + _result.Error;
        }
    }

    private static void AddItemRow(TextTable _table, ItemDelta _item, int _runs)
    {
        _table.Add(_item.Name, _item.VanillaMean(_runs).ToString("0.0000"), _item.PartyMean(_runs).ToString("0.0000"), _item.Mean.ToString("+0.0000;-0.0000;0.0000"),
            $"{_item.Ci95Low:+0.0000;-0.0000;0.0000} .. {_item.Ci95High:+0.0000;-0.0000;0.0000}", _item.Min.ToString("+0;-0;0"));
    }

    private static void AddRow(TextTable _table, string _label, PairedStats _stats)
    {
        _table.Add(_label, _stats.MeanVanilla.ToString("0.000"), _stats.MeanParty.ToString("0.000"), FormatRelative(_stats),
            $"{_stats.Ci95Low:+0.000;-0.000} .. {_stats.Ci95High:+0.000;-0.000}");
    }

    private static string FormatRelative(PairedStats _stats)
    {
        double relative = _stats.RelativeChange;
        return double.IsNaN(relative) ? (_stats.MeanParty > 0 ? "new" : "0%") : (relative * 100d).ToString("+0.0;-0.0;0.0") + "%";
    }
}
