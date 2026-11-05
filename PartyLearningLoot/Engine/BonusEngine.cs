using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>
/// The additive party bonus of one loot spawn.
///
/// While the game draws normally (with the opener's own perks, untouched), the engine only watches: every call of
/// LootContainer.SpawnLootItemsFromList for a list with entries the party rule applies to is noted (list, draws).
/// When the root list is finished, the notes are processed: each list is planned (<see cref="PlanBuilder"/>) and gets
/// <c>m = round(Σn · G)</c> extra draws from a synthetic list that holds only the entries whose share the party raises,
/// made by the game's own draw code with a private random stream and a private event stream. Vanilla therefore keeps
/// its result bit for bit and the party only adds to it. Lists reached by a bonus draw are noted like any other and get
/// their own bonus afterwards (at most <see cref="MaxCallsPerSpawn"/> bonus calls per spawn).
/// Everything runs on the thread that started the spawn; other threads pass through untouched.
/// </summary>
internal static class BonusEngine
{
    public const int MaxCallsPerSpawn = 64;

    /// <summary>What the Harmony prefix remembers for the postfix and finalizer of the same call.</summary>
    public struct CallState
    {
        public bool Counted;
        public bool Root;
    }

    private enum Phase
    {
        Idle,
        Collecting,
        Flushing,
        Done
    }

    private readonly struct VisitKey : IEquatable<VisitKey>
    {
        private readonly LootListInfo info;
        private readonly bool isAll;
        private readonly bool sandbox;

        public VisitKey(LootListInfo _info, bool _isAll, bool _sandbox)
        {
            info = _info;
            isAll = _isAll;
            sandbox = _sandbox;
        }

        public bool Equals(VisitKey _other) => ReferenceEquals(info, _other.info) && isAll == _other.isAll && sandbox == _other.sandbox;

        public override bool Equals(object _obj) => _obj is VisitKey other && Equals(other);

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(info) * 4 + (isAll ? 2 : 0) + (sandbox ? 1 : 0);
    }

    private sealed class Visit
    {
        public VisitKey Key;
        public LootListInfo Info;
        public bool IsAll;
        public bool Sandbox;

        /// <summary>Pick lists: the vanilla draws summed over all visits.</summary>
        public int Draws;

        /// <summary>Number of visits (all lists get one bonus call per visit).</summary>
        public int Visits;
    }

    /// <summary>True from the activation of a spawn until its end; the hot path of every patched call reads only this.</summary>
    public static bool Armed;

    /// <summary>Nesting of SpawnLootItemsFromList calls inside the armed spawn.</summary>
    public static int Depth;

    private static Phase phase;
    private static int ownerThread = -1;
    private static LootListIndex index = LootListIndex.Empty;
    private static IWeightSource weights;
    private static BonusTrace trace;
    private static bool dryRun;
    private static int bonusDepth;
    private static Dictionary<LootContainer.LootEntry, float> overrides;
    private static int callsMade;

    private static readonly SpawnArgs args = new SpawnArgs();
    private static readonly GameRandom bonusRandom = new GameRandom();
    private static readonly GameRandom eventRandom = new GameRandom();
    private static readonly Dictionary<VisitKey, Visit> pending = new Dictionary<VisitKey, Visit>();
    private static readonly Queue<Visit> queue = new Queue<Visit>();
    private static readonly Dictionary<VisitKey, ListPlan> plans = new Dictionary<VisitKey, ListPlan>();
    private static readonly HashSet<LootListInfo> seenLists = new HashSet<LootListInfo>();
    private static readonly MinEventParams cachedSnapshot = new MinEventParams();
    private static readonly MinEventParams openerSnapshot = new MinEventParams();

    /// <summary>True while the engine plans or draws a bonus (the game's vanilla evaluations are not the opener's own draws then).</summary>
    public static bool InFlush => phase == Phase.Flushing;

    public static bool IsIdle => !Armed && phase == Phase.Idle && Depth == 0 && bonusDepth == 0 && overrides == null && queue.Count == 0 && pending.Count == 0;

    /// <summary>True when a finished spawn left no depth, queue or override behind (checked before the engine is ended).</summary>
    public static bool IsBalanced => Depth == 0 && bonusDepth == 0 && overrides == null && queue.Count == 0 && pending.Count == 0;

    // ----- lifecycle ------------------------------------------------------------------------------

    private static long seedCounter;

    /// <summary>
    /// A seed for the private streams of one spawn from a counter, the high-resolution clock and the opener: it never
    /// depends on a random stream of the game, so the game's streams stay exactly as vanilla leaves them.
    /// </summary>
    public static int NextSeed(int _openerId)
    {
        return BonusMath.MixSeed(Interlocked.Increment(ref seedCounter), Stopwatch.GetTimestamp(), _openerId);
    }

    /// <summary>
    /// Arms the engine for one spawn on the current thread. <paramref name="_seed"/> seeds the private streams; production
    /// passes a seed that never depends on the game's own streams.
    /// </summary>
    public static void Begin(LootListIndex _index, IWeightSource _weights, BonusTrace _trace, int _seed, bool _dryRun)
    {
        End();
        index = _index ?? LootListIndex.Empty;
        weights = _weights;
        trace = _trace;
        dryRun = _dryRun;
        bonusRandom.SetSeed(_seed);
        eventRandom.SetSeed(unchecked(_seed ^ 0x5bd1e995));
        ownerThread = Thread.CurrentThread.ManagedThreadId;
        phase = Phase.Collecting;
        Armed = true;
    }

    /// <summary>Disarms the engine and drops all state.</summary>
    public static void End()
    {
        Armed = false;
        phase = Phase.Idle;
        Depth = 0;
        bonusDepth = 0;
        overrides = null;
        callsMade = 0;
        pending.Clear();
        queue.Clear();
        plans.Clear();
        seenLists.Clear();
        args.Clear();
        index = LootListIndex.Empty;
        weights = null;
        trace = null;
        dryRun = false;
        ownerThread = -1;
    }

    // ----- the hooks of SpawnLootItemsFromList ----------------------------------------------------

    public static CallState Enter(GameRandom _random, List<LootContainer.LootEntry> _itemSet, int _numToSpawn, float _abundance, List<ItemStack> _spawnedItems,
        float _lootStage, float _rareLootChance, string _lootQualityTemplate, EntityPlayer _player, FastTags<TagGroup.Global> _containerTags, bool _uniqueItems,
        bool _ignoreLootProb, bool _forceStacking, bool _ignoreLootAbundance, List<string> _buffsToAdd, bool _sandboxModifierUsed)
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread)
        {
            return default;
        }
        int depth = Depth + 1;
        var state = new CallState { Counted = true };
        if (phase == Phase.Collecting)
        {
            if (depth == 1)
            {
                state.Root = true;
                args.Random = _random;
                args.Abundance = _abundance;
                args.LootStage = _lootStage;
                args.RareLootChance = _rareLootChance;
                args.LootQualityTemplate = _lootQualityTemplate;
                args.Player = _player;
                args.ContainerTags = _containerTags;
                args.UniqueItems = _uniqueItems;
                args.IgnoreLootProb = _ignoreLootProb;
                args.ForceStacking = _forceStacking;
                args.IgnoreLootAbundance = _ignoreLootAbundance;
                args.Items = _spawnedItems;
                args.Buffs = _buffsToAdd;
            }
            NoteVisit(_itemSet, _numToSpawn, _sandboxModifierUsed);
        }
        else if (phase == Phase.Flushing && depth != bonusDepth)
        {
            // A list reached from inside a bonus draw: it gets its own bonus later. The bonus call itself is not noted.
            NoteVisit(_itemSet, _numToSpawn, _sandboxModifierUsed);
        }
        Depth = depth;
        return state;
    }

    /// <summary>After the root call: the vanilla result is final, the bonus is made now.</summary>
    public static void Leave(CallState _state, ref bool _result, ref int _slotsLeft)
    {
        if (_state.Counted && _state.Root && phase == Phase.Collecting)
        {
            Flush(ref _result, ref _slotsLeft);
        }
    }

    public static void Exit(CallState _state, Exception _exception)
    {
        if (!_state.Counted)
        {
            return;
        }
        Depth--;
        if (_state.Root)
        {
            if (_exception != null)
            {
                // The game's own draw failed: the spawn is lost anyway, nothing is added on top.
                pending.Clear();
                queue.Clear();
            }
            phase = Phase.Done;
        }
    }

    /// <summary>Weight the game's draw code uses for an entry of the synthetic list of the running bonus call, or false for every other call.</summary>
    public static bool TryOverride(LootContainer.LootEntry _entry, out float _weight)
    {
        _weight = 0f;
        return bonusDepth != 0 && Depth == bonusDepth && overrides != null && _entry != null && Thread.CurrentThread.ManagedThreadId == ownerThread
               && overrides.TryGetValue(_entry, out _weight);
    }

    // ----- visits -----------------------------------------------------------------------------------

    private static void NoteVisit(List<LootContainer.LootEntry> _list, int _numToSpawn, bool _sandbox)
    {
        if (_numToSpawn == 0 || _numToSpawn < -1 || !index.TryGet(_list, out LootListInfo info))
        {
            return;
        }
        bool isAll = _numToSpawn == -1;
        bool sandbox = !isAll && _sandbox;
        var key = new VisitKey(info, isAll, sandbox);
        if (!pending.TryGetValue(key, out Visit visit))
        {
            visit = new Visit { Key = key, Info = info, IsAll = isAll, Sandbox = sandbox };
            pending.Add(key, visit);
            queue.Enqueue(visit);
        }
        if (!isAll)
        {
            visit.Draws += _numToSpawn;
        }
        visit.Visits++;
        trace.Visits++;
        if (seenLists.Add(info))
        {
            trace.DistinctLists++;
        }
    }

    // ----- flush ----------------------------------------------------------------------------------

    /// <summary>The vanilla result of a spawn at the moment the bonus starts, with the means to verify and restore it.</summary>
    public sealed class ResultSnapshot
    {
        private ItemStack[] stacks;
        private int[] counts;
        private int[] types;
        private int buffs;

        /// <summary>Free slots when the snapshot was taken.</summary>
        public int Slots { get; private set; }

        public static ResultSnapshot Take(List<ItemStack> _items, List<string> _buffs, int _slotsLeft)
        {
            var snapshot = new ResultSnapshot
            {
                stacks = _items.ToArray(),
                counts = new int[_items.Count],
                types = new int[_items.Count],
                Slots = _slotsLeft,
                buffs = _buffs?.Count ?? 0
            };
            for (int i = 0; i < snapshot.stacks.Length; i++)
            {
                snapshot.counts[i] = snapshot.stacks[i]?.count ?? 0;
                snapshot.types[i] = snapshot.stacks[i]?.itemValue?.type ?? 0;
            }
            return snapshot;
        }

        /// <summary>The bonus only adds: every stack is still there, no smaller, and no slot came back. False with the problem otherwise.</summary>
        public bool Verify(List<ItemStack> _items, List<string> _buffs, int _slotsAfter, out string _problem)
        {
            _problem = null;
            if (_items.Count < stacks.Length)
            {
                _problem = $"{_items.Count} stacks after the bonus, {stacks.Length} before";
                return false;
            }
            for (int i = 0; i < stacks.Length; i++)
            {
                if (!ReferenceEquals(_items[i], stacks[i]))
                {
                    _problem = $"stack {i} was replaced";
                    return false;
                }
                if ((_items[i]?.count ?? 0) < counts[i] || (_items[i]?.itemValue?.type ?? 0) != types[i])
                {
                    _problem = $"stack {i} shrank or changed type ({counts[i]} -> {_items[i]?.count})";
                    return false;
                }
            }
            if (_slotsAfter > Slots)
            {
                _problem = $"slots left grew from {Slots} to {_slotsAfter}";
                return false;
            }
            if ((_buffs?.Count ?? 0) < buffs)
            {
                _problem = "buffs were removed";
                return false;
            }
            return true;
        }

        /// <summary>Puts the vanilla result back: the list, the stack sizes and the buffs.</summary>
        public void Restore(List<ItemStack> _items, List<string> _buffs)
        {
            if (_items.Count > stacks.Length)
            {
                _items.RemoveRange(stacks.Length, _items.Count - stacks.Length);
            }
            for (int i = 0; i < stacks.Length && i < _items.Count; i++)
            {
                _items[i] = stacks[i];
                if (stacks[i] != null)
                {
                    stacks[i].count = counts[i];
                }
            }
            if (_buffs != null && _buffs.Count > buffs)
            {
                _buffs.RemoveRange(buffs, _buffs.Count - buffs);
            }
        }

        /// <summary>Total count per item type before the bonus.</summary>
        public Dictionary<int, int> TotalsByType()
        {
            var totals = new Dictionary<int, int>();
            for (int i = 0; i < types.Length; i++)
            {
                totals.TryGetValue(types[i], out int count);
                totals[types[i]] = count + counts[i];
            }
            return totals;
        }
    }

    private static void Flush(ref bool _result, ref int _slotsLeft)
    {
        phase = Phase.Flushing;
        if (queue.Count == 0)
        {
            phase = Phase.Done;
            return;
        }
        trace.SlotsLeft = _slotsLeft;
        if (args.Items == null)
        {
            SkipAll("no_result_list");
            return;
        }
        if (args.RareLootChance != 0f)
        {
            SkipAll("rare_loot_chance");
            return;
        }
        if (args.UniqueItems)
        {
            // Such a spawn draws without replacement: a bonus call of its own could pick an entry a second time.
            SkipAll("unique_items");
            return;
        }

        ResultSnapshot snapshot = ResultSnapshot.Take(args.Items, args.Buffs, _slotsLeft);
        GameEventManager events = GameEventManager.HasInstance ? GameEventManager.Current : null;
        GameRandom savedEvents = events?.Random;
        int mainBefore = args.Random != null ? args.Random.PeekSample() : 0;
        int eventsBefore = savedEvents != null ? savedEvents.PeekSample() : 0;
        EventParamGuard.CopyAll(MinEventParams.CachedEventParam, cachedSnapshot);
        MinEventParams openerContext = args.Player?.MinEventContext;
        if (openerContext != null)
        {
            EventParamGuard.CopyAll(openerContext, openerSnapshot);
        }

        bool spawned = false;
        int slots = _slotsLeft;
        bool failed = false;
        try
        {
            if (events != null)
            {
                events.Random = eventRandom;
            }
            while (queue.Count > 0)
            {
                if (callsMade >= MaxCallsPerSpawn)
                {
                    trace.Skip("call_limit", queue.Count);
                    break;
                }
                Visit visit = queue.Dequeue();
                pending.Remove(visit.Key);
                Process(visit, ref slots, ref spawned);
            }
            trace.InvariantChecks++;
            if (!snapshot.Verify(args.Items, args.Buffs, slots, out string problem))
            {
                failed = true;
                trace.Violation("bonus_only_adds", problem);
            }
        }
        catch (Exception e)
        {
            failed = true;
            MergeEngine.RegisterError("bonus", args.Player != null ? PartySnapshot.DisplayName(args.Player) : null, e);
        }
        finally
        {
            bonusDepth = 0;
            overrides = null;
            if (events != null)
            {
                events.Random = savedEvents;
            }
            EventParamGuard.CopyAll(cachedSnapshot, MinEventParams.CachedEventParam);
            if (openerContext != null)
            {
                EventParamGuard.CopyAll(openerSnapshot, openerContext);
            }
            queue.Clear();
            pending.Clear();
            phase = Phase.Done;
        }

        trace.InvariantChecks += 2;
        if (args.Random != null && args.Random.PeekSample() != mainBefore)
        {
            trace.Violation("main_stream_untouched", "the spawn's random stream moved while the bonus was made");
        }
        if (savedEvents != null && savedEvents.PeekSample() != eventsBefore)
        {
            trace.Violation("event_stream_untouched", "the game's event random stream moved while the bonus was made");
        }

        if (failed)
        {
            snapshot.Restore(args.Items, args.Buffs);
            trace.Added.Clear();
            _slotsLeft = snapshot.Slots;
            return;
        }
        _slotsLeft = slots;
        if (spawned)
        {
            _result = true;
        }
        if (trace.Calls > 0)
        {
            RecordAdded(snapshot);
        }
    }

    private static void SkipAll(string _reason)
    {
        trace.Skip(_reason, queue.Count);
        queue.Clear();
        pending.Clear();
        phase = Phase.Done;
    }

    private static void Process(Visit _visit, ref int _slots, ref bool _spawned)
    {
        LootListInfo info = _visit.Info;
        if (!info.Matches)
        {
            trace.Skip("list_changed");
            return;
        }
        if (!_visit.IsAll && info.PickProblem != null)
        {
            trace.Skip(info.PickProblem);
            return;
        }
        if (!_visit.IsAll && info.HasForceProb)
        {
            trace.Skip("force_prob");
            return;
        }
        if (_slots < 1)
        {
            trace.Skip("no_slots");
            return;
        }
        if (!plans.TryGetValue(_visit.Key, out ListPlan plan))
        {
            plan = PlanBuilder.Build(info, _visit.IsAll, args, weights, trace);
            plans[_visit.Key] = plan;
            trace.Plans++;
            trace.MsPlan += plan.Milliseconds;
            if (plan.Trace != null && plan.Valid)
            {
                trace.Lists.Add(plan.Trace);
            }
        }
        if (!plan.Valid)
        {
            trace.Skip(plan.Problem ?? "invalid_plan");
            return;
        }

        var run = new BonusRunTrace { Draws = _visit.IsAll ? _visit.Visits : _visit.Draws, Sandbox = _visit.Sandbox };
        plan.Trace?.Runs.Add(run);
        if (_visit.IsAll)
        {
            ProcessAll(_visit, plan, run, ref _slots, ref _spawned);
        }
        else
        {
            ProcessPick(_visit, plan, run, ref _slots, ref _spawned);
        }
    }

    private static void ProcessPick(Visit _visit, ListPlan _plan, BonusRunTrace _run, ref int _slots, ref bool _spawned)
    {
        double expected = _visit.Draws * _plan.TotalGain;
        if (!(expected > 0d) || _plan.BonusList.Count == 0)
        {
            return;
        }
        _run.Expected = expected;
        int draws = BonusMath.RoundUnbiased(expected, bonusRandom.RandomDouble);
        _run.Planned = draws;
        trace.ExpectedDraws += expected;
        trace.RoundingVariance += BonusMath.RoundingVariance(expected);
        trace.PlannedDraws += draws;
        if (draws > 0 && !dryRun)
        {
            _spawned |= Draw(_plan, draws, _visit.Sandbox, ref _slots);
        }
    }

    private static void ProcessAll(Visit _visit, ListPlan _plan, BonusRunTrace _run, ref int _slots, ref bool _spawned)
    {
        _run.Expected = _visit.Visits;
        if (_plan.BonusList.Count == 0)
        {
            return;
        }
        for (int visit = 0; visit < _visit.Visits; visit++)
        {
            if (callsMade >= MaxCallsPerSpawn)
            {
                trace.Skip("call_limit", _visit.Visits - visit);
                return;
            }
            _run.Planned++;
            if (!dryRun)
            {
                _spawned |= Draw(_plan, -1, false, ref _slots);
            }
        }
    }

    /// <summary>One call of the game's draw code over the synthetic list of the plan, with the plan's weights.</summary>
    private static bool Draw(ListPlan _plan, int _numToSpawn, bool _sandbox, ref int _slots)
    {
        bonusDepth = Depth + 1;
        overrides = _plan.Overrides;
        long started = Stopwatch.GetTimestamp();
        try
        {
            return LootContainer.SpawnLootItemsFromList(bonusRandom, _plan.BonusList, _numToSpawn, args.Abundance, args.Items, ref _slots, args.LootStage,
                args.RareLootChance, args.LootQualityTemplate, args.Player, args.ContainerTags, args.UniqueItems, args.IgnoreLootProb, args.ForceStacking,
                args.IgnoreLootAbundance, args.Buffs, _sandbox);
        }
        finally
        {
            bonusDepth = 0;
            overrides = null;
            callsMade++;
            trace.Calls++;
            trace.MsBonus += (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
    }

    // ----- result bookkeeping ---------------------------------------------------------------------

    private static void RecordAdded(ResultSnapshot _snapshot)
    {
        Dictionary<int, int> before = _snapshot.TotalsByType();
        var after = new Dictionary<int, int>();
        foreach (ItemStack stack in args.Items)
        {
            int type = stack?.itemValue?.type ?? 0;
            after.TryGetValue(type, out int count);
            after[type] = count + (stack?.count ?? 0);
        }
        GameCatalog catalog = GameCatalog.Current;
        foreach (KeyValuePair<int, int> pair in after)
        {
            before.TryGetValue(pair.Key, out int previous);
            int added = pair.Value - previous;
            if (added <= 0)
            {
                continue;
            }
            ItemClass itemClass = ItemClass.GetForId(pair.Key);
            string learn = GameCatalog.LearningCategory(itemClass);
            trace.Added.Add(new BonusAddedItem
            {
                Name = itemClass?.Name ?? ("#" + pair.Key),
                Count = added,
                Learn = learn,
                OtherLoot = learn == null && catalog != null && catalog.IsOtherLoot(itemClass)
            });
        }
        trace.Added.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }
}
