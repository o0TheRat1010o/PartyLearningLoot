using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>Merge decision for one (perk, tags, input) combination, deduplicated over all calls of a spawn.</summary>
internal sealed class PerkTrace
{
    public string Perk;
    public string Tags;
    public float BaseIn;
    public float PercIn;
    public float OpenerDelta;
    public bool OpenerDeltaVaries;
    public float[] MemberDeltas;
    public int WinnerSlot = -1;

    /// <summary>max(0, best member Δ − opener Δ), counted at the reference base.</summary>
    public float Extra;
    public int Calls;
}

/// <summary>Vanilla versus party probability of one loot entry within a spawn (Detailed level).</summary>
internal sealed class EntryTrace
{
    public string Name;
    public bool IsGroup;
    public string Tags;
    public bool Eligible;
    public int Calls;
    public float FirstResult = float.NaN;
    public float Party = float.NaN;
    public float Vanilla = float.NaN;
    public bool Evaluated;

    /// <summary>Progression numbers of the first call: base before/after the progression and the summed extra.</summary>
    public float BaseRef = float.NaN;
    public float BaseFinal = float.NaN;
    public float Extra = float.NaN;
    public bool RequirementsFailed;
    public bool Unstable;
    public bool ReplicationMismatch;
    public bool Checked;
}

/// <summary>
/// Collects everything about the current merge activation that the audit log reports: inputs, per-perk decisions,
/// per-entry vanilla/party values, invariants, spawned items and timing. Reused between activations.
/// </summary>
internal sealed class SpawnTrace
{
    private readonly Stopwatch watch = new Stopwatch();
    private readonly Dictionary<LootContainer.LootEntry, EntryTrace> entries = new Dictionary<LootContainer.LootEntry, EntryTrace>(EntryComparer.Instance);

    public readonly List<PerkTrace> Perks = new List<PerkTrace>();
    public readonly List<string> ViolationRules = new List<string>();

    public int OpenerId;
    public string OpenerName;
    public LootOpenKind Kind;
    public string ContainerName;
    public bool DryRun;
    public PartySnapshot Snapshot;

    public int MergeEvaluations;
    public int NonEligibleClassEvaluations;
    public int MemberEvaluations;
    public int CacheHits;
    public int MemberWins;
    public int PerksRaised;
    public int ExtraApplications;
    public int Errors;
    public float MaxExtra;
    public int EligibleEntryCalls;
    public int NonEligibleEntryCalls;
    public int InvariantChecks;
    public int InvariantViolations;

    public IReadOnlyDictionary<LootContainer.LootEntry, EntryTrace> Entries => entries;

    public double ElapsedMs => watch.Elapsed.TotalMilliseconds;

    public void Begin(EntityPlayer _opener, PartySnapshot _snapshot, LootOpenKind _kind, string _containerName, bool _dryRun)
    {
        watch.Reset();
        watch.Start();
        entries.Clear();
        Perks.Clear();
        ViolationRules.Clear();
        OpenerId = _opener.entityId;
        OpenerName = PartySnapshot.DisplayName(_opener);
        Kind = _kind;
        ContainerName = _containerName;
        DryRun = _dryRun;
        Snapshot = _snapshot;
        MergeEvaluations = 0;
        NonEligibleClassEvaluations = 0;
        MemberEvaluations = 0;
        CacheHits = 0;
        MemberWins = 0;
        PerksRaised = 0;
        ExtraApplications = 0;
        Errors = 0;
        MaxExtra = 0f;
        EligibleEntryCalls = 0;
        NonEligibleEntryCalls = 0;
        InvariantChecks = 0;
        InvariantViolations = 0;
    }

    public void Stop()
    {
        watch.Stop();
    }

    public EntryTrace GetOrAddEntry(LootContainer.LootEntry _entry, FastTags<TagGroup.Global> _tags, bool _eligible)
    {
        if (!entries.TryGetValue(_entry, out EntryTrace trace))
        {
            trace = new EntryTrace
            {
                Name = GameCatalog.EntryName(_entry),
                IsGroup = _entry.group != null,
                Tags = string.Join(",", _tags.GetTagNames()),
                Eligible = _eligible
            };
            entries.Add(_entry, trace);
        }
        return trace;
    }

    /// <summary>Records an invariant violation (Detailed) and returns false so callers can write "check || Violation()".</summary>
    public bool Violation(string _rule, string _detail)
    {
        InvariantViolations++;
        if (ViolationRules.Count < 32 && !ViolationRules.Contains(_rule))
        {
            ViolationRules.Add(_rule);
        }
        RuntimeStats.InvariantViolations++;
        if (AuditLog.IsOpen)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.InvariantViolation);
            record.Prop("rule", _rule)
                .Prop("detail", _detail)
                .Prop("container", ContainerName)
                .Prop("kind", LootScope.KindCode(Kind))
                .Prop("opener", OpenerId);
            AuditLog.Commit(record, _flushSoon: true);
        }
        if (RuntimeStats.InvariantViolations <= 10)
        {
            ModLog.Warning($"Invariant '{_rule}' violated: {_detail}", "invariant");
        }
        return false;
    }

    /// <summary>Writes the "spawn" record for a merged activation.</summary>
    public void WriteSpawnRecord(IList<ItemStack> _items, Exception _exception, bool _detailed)
    {
        JsonLineWriter record = AuditLog.Begin(AuditRecords.Spawn);
        record.Prop("kind", LootScope.KindCode(Kind))
            .Prop("container", ContainerName)
            .Prop("dry_run", DryRun)
            .BeginObject("opener").Prop("id", OpenerId).Prop("name", OpenerName).EndObject();
        Snapshot?.WriteMembers(record, _detailed);
        record.Prop("ms", Math.Round(ElapsedMs, 3))
            .Prop("merge_evals", MergeEvaluations)
            .Prop("member_evals", MemberEvaluations)
            .Prop("cache_hits", CacheHits)
            .Prop("member_wins", MemberWins)
            .Prop("perks_raised", PerksRaised)
            .Prop("extra_applications", ExtraApplications)
            .Prop("max_extra", MaxExtra)
            .Prop("eligible_calls", EligibleEntryCalls)
            .Prop("errors", Errors);
        if (_exception != null)
        {
            record.Prop("exception", _exception.GetType().Name + ": " + _exception.Message);
        }

        WriteItems(record, _items, _detailed);

        if (_detailed)
        {
            WriteInputs(record);
            WritePerks(record);
            WriteEntries(record);
            record.BeginObject("invariants")
                .Prop("checked", InvariantChecks)
                .Prop("violations", InvariantViolations)
                .PropStrings("rules", ViolationRules)
                .EndObject();
        }
        AuditLog.Commit(record);
    }

    /// <summary>Summary lists the merged kinds only (learning items, other perk loot when enabled); Detailed lists everything.</summary>
    private void WriteItems(JsonLineWriter _record, IList<ItemStack> _items, bool _detailed)
    {
        GameCatalog catalog = GameCatalog.Current;
        _record.BeginArray("items");
        if (_items != null)
        {
            foreach (ItemStack stack in _items)
            {
                ItemClass itemClass = stack?.itemValue?.ItemClass;
                if (itemClass == null)
                {
                    continue;
                }
                string learn = GameCatalog.LearningCategory(itemClass);
                bool otherLoot = learn == null && catalog != null && catalog.IsOtherLoot(itemClass);
                if (!_detailed && learn == null && !otherLoot)
                {
                    continue;
                }
                _record.BeginObject().Prop("name", itemClass.Name).Prop("count", stack.count);
                if (learn != null)
                {
                    _record.Prop("learn", learn);
                }
                else if (otherLoot)
                {
                    _record.Prop("other_loot", true);
                }
                _record.EndObject();
            }
        }
        _record.EndArray();
    }

    private void WriteInputs(JsonLineWriter _record)
    {
        GameCatalog catalog = GameCatalog.Current;
        _record.BeginArray("inputs");
        if (catalog != null)
        {
            WritePlayerInputs(_record, MergeContext.Opener, OpenerId, true, catalog);
            for (int i = 0; i < MergeContext.MemberCount; i++)
            {
                MemberSlot slot = MergeContext.Members[i];
                WritePlayerInputs(_record, slot.Entity, slot.EntityId, false, catalog);
            }
        }
        _record.EndArray();
    }

    private static void WritePlayerInputs(JsonLineWriter _record, EntityPlayer _player, int _id, bool _isOpener, GameCatalog _catalog)
    {
        _record.BeginObject().Prop("id", _id).Prop("opener", _isOpener);
        Progression progression = _player?.Progression;
        _record.BeginObject("perks");
        var requirements = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (progression != null)
        {
            foreach (MergeableClass cls in _catalog.Mergeable.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                int level = progression.GetProgressionValue(cls.ProgressionId)?.Level ?? 0;
                if (level <= 0)
                {
                    continue;
                }
                _record.Prop(cls.Name, level);
                requirements.UnionWith(cls.RequirementProgressions);
            }
        }
        _record.EndObject();
        _record.BeginObject("req");
        if (progression != null)
        {
            foreach (string name in requirements)
            {
                _record.Prop(name, progression.GetProgressionValue(name)?.Level ?? -1);
            }
        }
        _record.EndObject().EndObject();
    }

    private void WritePerks(JsonLineWriter _record)
    {
        _record.BeginArray("perks");
        foreach (PerkTrace perk in Perks)
        {
            _record.BeginObject()
                .Prop("perk", perk.Perk)
                .Prop("tags", perk.Tags)
                .Prop("b_in", perk.BaseIn)
                .Prop("p_in", perk.PercIn)
                .Prop("calls", perk.Calls)
                .Prop("opener_delta", perk.OpenerDelta);
            if (perk.OpenerDeltaVaries)
            {
                _record.Prop("opener_delta_varies", true);
            }
            _record.BeginArray("members");
            for (int i = 0; i < perk.MemberDeltas.Length && i < MergeContext.MemberCount; i++)
            {
                _record.BeginObject().Prop("id", MergeContext.Members[i].EntityId).Prop("delta", perk.MemberDeltas[i]).EndObject();
            }
            _record.EndArray();
            if (perk.WinnerSlot >= 0 && perk.WinnerSlot < MergeContext.MemberCount)
            {
                _record.Prop("winner", MergeContext.Members[perk.WinnerSlot].EntityId);
            }
            else
            {
                _record.Prop("winner", "opener");
            }
            _record.Prop("extra", perk.Extra).EndObject();
        }
        _record.EndArray();
    }

    private void WriteEntries(JsonLineWriter _record)
    {
        _record.BeginArray("entries");
        int nonEligible = 0;
        int nonEligibleChanged = 0;
        foreach (EntryTrace entry in entries.Values)
        {
            if (!entry.Eligible)
            {
                nonEligible++;
                if (entry.Evaluated && !DeltaSelector.AlmostEqual(entry.Party, entry.Vanilla))
                {
                    nonEligibleChanged++;
                }
                continue;
            }
            _record.BeginObject()
                .Prop("entry", entry.Name)
                .Prop("group", entry.IsGroup)
                .Prop("tags", entry.Tags)
                .Prop("calls", entry.Calls)
                .Prop("vanilla", entry.Vanilla)
                .Prop("party", entry.Party);
            if (!float.IsNaN(entry.Extra))
            {
                _record.Prop("b0", entry.BaseRef).Prop("b_final", entry.BaseFinal).Prop("extra", entry.Extra);
            }
            if (entry.RequirementsFailed)
            {
                _record.Prop("req_failed", true);
            }
            if (entry.Unstable)
            {
                _record.Prop("unstable", true);
            }
            if (entry.ReplicationMismatch)
            {
                _record.Prop("replication_mismatch", true);
            }
            _record.EndObject();
        }
        _record.EndArray();
        _record.BeginObject("non_eligible")
            .Prop("entries", nonEligible)
            .Prop("calls", NonEligibleEntryCalls)
            .Prop("changed", nonEligibleChanged)
            .EndObject();
    }

    private sealed class EntryComparer : IEqualityComparer<LootContainer.LootEntry>
    {
        public static readonly EntryComparer Instance = new EntryComparer();
        public bool Equals(LootContainer.LootEntry _a, LootContainer.LootEntry _b) => ReferenceEquals(_a, _b);
        public int GetHashCode(LootContainer.LootEntry _obj) => RuntimeHelpers.GetHashCode(_obj);
    }
}
