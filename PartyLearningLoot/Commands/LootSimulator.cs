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
    public readonly PairedStats LearningItems = new PairedStats();
    public readonly PairedStats OtherLootItems = new PairedStats();
    public readonly PairedStats AllItems = new PairedStats();
    public bool IncludesOtherLoot;
    public readonly PairedCounter ByItem = new PairedCounter();
    public string Error;
}

/// <summary>
/// Paired loot simulation for one container: every run is spawned twice with the same seed, once vanilla and once with
/// the party merge, without placing anything in the world. Requirement rolls use a private random generator that is
/// seeded per run too, so the global game-event random state is restored afterwards and the pairs stay comparable.
/// Buffs that loot entries would grant go to a throwaway list.
/// </summary>
internal static class LootSimulator
{
    public const int DefaultRuns = 2000;
    public const int MaxRuns = 100000;
    public const double TimeBudgetSeconds = 5d;

    public static SimulationResult Run(string _containerName, int _runs, EntityPlayer _player, ModSettings _settings)
    {
        var result = new SimulationResult { Container = _containerName, Player = _player, RequestedRuns = _runs };
        LootContainer container = LootContainer.GetLootContainer(_containerName, false);
        if (container == null)
        {
            result.Error = $"unknown loot container '{_containerName}'";
            return result;
        }
        if (MergeContext.Active)
        {
            result.Error = "a merge is already running";
            return result;
        }
        result.Container = container.Name;
        GameCatalog catalog = GameCatalog.Current;
        result.IncludesOtherLoot = catalog != null && catalog.Data.IncludesOtherLoot;
        result.Snapshot = PartySnapshot.Capture(_player, _settings.MaxMemberDistance, _settings.IncludeDeadMembers, _settings.IncludeSpectators);
        result.LootStage = container.useUnmodifiedLootstage ? _player.unModifiedGameStage : _player.GetHighestPartyLootStage(0f, 0f);

        GameEventManager events = GameEventManager.Current;
        GameRandom originalEventRandom = events?.Random;
        int baseSeed = Environment.TickCount;
        var watch = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < _runs; i++)
            {
                if (watch.Elapsed.TotalSeconds > TimeBudgetSeconds)
                {
                    result.TimeLimited = true;
                    break;
                }
                int seed = unchecked(baseSeed + i * 7919);
                List<ItemStack> vanilla = SpawnOnce(container, _player, seed, result.LootStage, events, null);
                List<ItemStack> party = SpawnOnce(container, _player, seed, result.LootStage, events, result.Snapshot.VanillaReason == null ? result.Snapshot : null);
                result.LearningItems.Add(CountItems(vanilla, ItemKind.Learning, catalog, result.ByItem, false), CountItems(party, ItemKind.Learning, catalog, result.ByItem, true));
                if (result.IncludesOtherLoot)
                {
                    result.OtherLootItems.Add(CountItems(vanilla, ItemKind.OtherLoot, catalog, result.ByItem, false), CountItems(party, ItemKind.OtherLoot, catalog, result.ByItem, true));
                }
                result.AllItems.Add(CountItems(vanilla, ItemKind.All, catalog, null, false), CountItems(party, ItemKind.All, catalog, null, true));
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

    private static List<ItemStack> SpawnOnce(LootContainer _container, EntityPlayer _player, int _seed, float _lootStage, GameEventManager _events, PartySnapshot _party)
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
            }
            return SpawnWithoutSideEffects(_container, random, _player, _lootStage);
        }
        finally
        {
            MergeContext.Deactivate(token);
            GameRandomManager.Instance.FreeGameRandom(random);
            GameRandomManager.Instance.FreeGameRandom(eventRandom);
        }
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

    private static int CountItems(List<ItemStack> _items, ItemKind _kind, GameCatalog _catalog, PairedCounter _counter, bool _party)
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
            _counter?.Add(itemClass.Name, _party, stack.count);
        }
        return total;
    }

    public static IEnumerable<string> Format(SimulationResult _result)
    {
        if (_result.Error != null && _result.Runs == 0)
        {
            yield return "simulate: " + _result.Error;
            yield break;
        }
        PartySnapshot snapshot = _result.Snapshot;
        yield return $"Simulation of '{_result.Container}' for {PartySnapshot.DisplayName(_result.Player)}: {_result.Runs} paired runs" +
                     $"{(_result.TimeLimited ? $" (stopped after {TimeBudgetSeconds:0} s of {_result.RequestedRuns})" : string.Empty)}, loot stage {_result.LootStage:0}, {_result.Seconds:0.00} s";
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
        yield return $"Runs with more learning items in the party run: {_result.LearningItems.PartyHigher}, fewer: {_result.LearningItems.VanillaHigher}, equal: {_result.LearningItems.Equal}";
        if (_result.ByItem.Keys > 0)
        {
            var items = new TextTable(_result.IncludesOtherLoot ? "Learning / other perk loot item" : "Learning item", "Vanilla", "Party").AlignRight(1, 2);
            foreach (KeyValuePair<string, int[]> item in _result.ByItem.ByChange().Take(12))
            {
                items.Add(item.Key, item.Value[0], item.Value[1]);
            }
            foreach (string line in items.Lines())
            {
                yield return line;
            }
        }
        if (_result.Error != null)
        {
            yield return "Stopped early: " + _result.Error;
        }
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
