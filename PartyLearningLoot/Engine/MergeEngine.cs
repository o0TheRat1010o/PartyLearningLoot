using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>
/// Party-wide LootProb merge.
///
/// <see cref="MergeClass"/> runs in the ProgressionClass.ModifyValue postfix for the opener's own evaluation of each
/// perk: it evaluates the same (patched) vanilla method for every member with the member's own level and requirements
/// and records <c>extra = max(0, best member Δ − opener Δ)</c> (see <see cref="DeltaSelector"/>). The opener's values are
/// never touched there. <see cref="ApplyProgressionExtra"/> runs in the Progression.ModifyValue postfix and adds the
/// accumulated extra once, scaled to the base before the progression evaluation, so vanilla's "complete" base_set quirk
/// cannot multiply member bonuses. Member results are cached per (perk, tag content, input) within one activation;
/// CachedEventParam and the member contexts are restored after every member evaluation.
/// </summary>
internal static class MergeEngine
{
    public const int BreakerThreshold = 20;

    private static readonly Dictionary<DeltaKey, CacheEntry> cache = new Dictionary<DeltaKey, CacheEntry>();
    private static readonly MinEventParams cachedEventSnapshot = new MinEventParams();
    private static readonly MinEventParams openerSnapshot = new MinEventParams();
    private static readonly MinEventParams replicaCachedSnapshot = new MinEventParams();

    public static bool BreakerOpen { get; private set; }
    public static int SessionErrors { get; private set; }

    private sealed class CacheEntry
    {
        public readonly float[] MemberPerc;
        public PerkTrace Trace;
        public bool WinCounted;

        public CacheEntry(int _members)
        {
            MemberPerc = new float[_members];
        }
    }

    // ----- merge per perk (hot path after the gates in the patch) --------------------------------

    public static void MergeClass(ProgressionClass _class, float _openerPercOut, FastTags<TagGroup.Global> _tags, float _baseIn, float _percIn)
    {
        GameCatalog catalog = GameCatalog.Current;
        if (catalog == null || BreakerOpen)
        {
            return;
        }
        SpawnTrace trace = MergeContext.Trace;
        bool counting = !MergeContext.InAudit;
        if (!catalog.IsEligible(_tags))
        {
            if (counting)
            {
                trace.NonEligibleClassEvaluations++;
            }
            return;
        }
        if (!catalog.TryGetMergeable(_class, out MergeableClass info) || !_tags.Test_AnySet(info.MergedTagUnion))
        {
            return;
        }

        var key = new DeltaKey(_class, _tags, _baseIn, _percIn);
        if (!cache.TryGetValue(key, out CacheEntry entry))
        {
            entry = EvaluateMembers(info, _tags, _baseIn, _percIn, trace);
            cache.Add(key, entry);
        }
        else if (counting)
        {
            trace.CacheHits++;
        }

        PerkMerge merge = DeltaSelector.SelectPerk(_percIn, _openerPercOut, entry.MemberPerc);
        if (counting)
        {
            trace.MergeEvaluations++;
            if (merge.MemberWon)
            {
                trace.MemberWins++;
                trace.MaxExtra = Math.Max(trace.MaxExtra, merge.Extra);
                if (!entry.WinCounted)
                {
                    entry.WinCounted = true;
                    trace.PerksRaised++;
                }
            }
            if (AuditLog.Detailed)
            {
                CheckPerk(trace, info, merge, entry, _percIn);
            }
            if (AuditLog.Detailed || MergeContext.TraceAlways)
            {
                RecordPerk(trace, info, entry, merge, _tags, _baseIn, _percIn);
            }
        }
        if (merge.Extra > 0f)
        {
            MergeContext.ProgressionExtra += merge.Extra;
        }
    }

    /// <summary>
    /// End of the opener's progression evaluation: adds the accumulated extra (not in dry run) and remembers the
    /// numbers for the getProbability audit.
    /// </summary>
    public static void ApplyProgressionExtra(ref float _perc, float _baseFinal)
    {
        float extra = MergeContext.ProgressionExtra;
        float baseRef = MergeContext.ProgressionBase;
        float applied = 0f;
        if (extra > 0f && (!MergeContext.DryRun || MergeContext.ForceApply))
        {
            float merged = DeltaSelector.ApplyExtra(_perc, extra, baseRef, _baseFinal);
            applied = merged - _perc;
            _perc = merged;
        }
        MergeContext.RecordProgression(baseRef, _baseFinal, extra, applied);
        if (!MergeContext.InAudit && extra > 0f)
        {
            MergeContext.Trace.ExtraApplications++;
        }
    }

    private static CacheEntry EvaluateMembers(MergeableClass _info, FastTags<TagGroup.Global> _tags, float _baseIn, float _percIn, SpawnTrace _trace)
    {
        int count = MergeContext.MemberCount;
        var entry = new CacheEntry(count);
        bool detailed = AuditLog.Detailed;
        EventParamGuard.CopyAll(MinEventParams.CachedEventParam, cachedEventSnapshot);
        for (int i = 0; i < count; i++)
        {
            MemberSlot slot = MergeContext.Members[i];
            EntityPlayer member = slot.Entity;
            Progression progression = member != null ? member.Progression : null;
            if (member == null || progression == null || member.MinEventContext == null)
            {
                entry.MemberPerc[i] = float.NaN;
                continue;
            }
            ProgressionValue pv = progression.GetProgressionValue(_info.ProgressionId);
            if (pv == null)
            {
                entry.MemberPerc[i] = _percIn;
                continue;
            }

            float baseValue = _baseIn;
            float percValue = _percIn;
            EventParamGuard.CopyAll(member.MinEventContext, slot.ContextSnapshot);
            try
            {
                MergeContext.InEval = true;
                _info.Class.ModifyValue(member, pv, PassiveEffects.LootProb, ref baseValue, ref percValue, _tags);
                entry.MemberPerc[i] = percValue;
                slot.Evaluations++;
                _trace.MemberEvaluations++;
            }
            catch (Exception e)
            {
                entry.MemberPerc[i] = float.NaN;
                slot.Errors++;
                _trace.Errors++;
                RegisterError("member_evaluation", $"perk {_info.Name}, member {slot.Name} ({slot.EntityId})", e);
            }
            finally
            {
                MergeContext.InEval = false;
                EventParamGuard.CopyAll(slot.ContextSnapshot, member.MinEventContext);
                EventParamGuard.CopyAll(cachedEventSnapshot, MinEventParams.CachedEventParam);
            }

            if (detailed)
            {
                _trace.InvariantChecks++;
                if (!EventParamGuard.SameState(member.MinEventContext, slot.ContextSnapshot))
                {
                    _trace.Violation("member_context_restored", $"member {slot.EntityId} context differs after evaluating {_info.Name}");
                }
                if (!EventParamGuard.SameState(MinEventParams.CachedEventParam, cachedEventSnapshot))
                {
                    _trace.Violation("cached_event_param_restored", $"CachedEventParam differs after evaluating {_info.Name} for member {slot.EntityId}");
                }
            }
        }
        return entry;
    }

    private static void CheckPerk(SpawnTrace _trace, MergeableClass _info, PerkMerge _merge, CacheEntry _entry, float _percIn)
    {
        _trace.InvariantChecks++;
        if (!DeltaSelector.IsFinite(_merge.OpenerDelta))
        {
            return;
        }
        float best = float.NegativeInfinity;
        foreach (float perc in _entry.MemberPerc)
        {
            float delta = perc - _percIn;
            if (DeltaSelector.IsFinite(delta) && delta > best)
            {
                best = delta;
            }
        }
        float expected = Math.Max(0f, best - _merge.OpenerDelta);
        if (!DeltaSelector.AlmostEqual(expected, _merge.Extra))
        {
            _trace.Violation("extra_is_best_margin", $"{_info.Name}: extra {_merge.Extra} but best member margin {expected}");
        }
        if (_merge.Extra < 0f)
        {
            _trace.Violation("extra_non_negative", $"{_info.Name}: extra {_merge.Extra} < 0");
        }
    }

    private static void RecordPerk(SpawnTrace _trace, MergeableClass _info, CacheEntry _entry, PerkMerge _merge, FastTags<TagGroup.Global> _tags, float _baseIn, float _percIn)
    {
        PerkTrace perk = _entry.Trace;
        if (perk == null)
        {
            perk = new PerkTrace
            {
                Perk = _info.Name,
                Tags = string.Join(",", _tags.GetTagNames()),
                BaseIn = _baseIn,
                PercIn = _percIn,
                OpenerDelta = _merge.OpenerDelta,
                MemberDeltas = new float[_entry.MemberPerc.Length],
                WinnerSlot = _merge.WinnerIndex,
                Extra = _merge.Extra
            };
            for (int i = 0; i < _entry.MemberPerc.Length; i++)
            {
                perk.MemberDeltas[i] = _entry.MemberPerc[i] - _percIn;
            }
            _entry.Trace = perk;
            _trace.Perks.Add(perk);
        }
        else if (!DeltaSelector.AlmostEqual(perk.OpenerDelta, _merge.OpenerDelta))
        {
            perk.OpenerDeltaVaries = true;
        }
        perk.Calls++;
    }

    public static void ClearActivationCache()
    {
        cache.Clear();
    }

    // ----- getProbability audit -----------------------------------------------------------------

    private enum ReplicaMode
    {
        Vanilla,
        Party,
        PartyForced
    }

    /// <summary>
    /// Audits one getProbability result of the opener inside a merge activation. Counts eligible calls (Summary) and,
    /// at Detailed level, recomputes the vanilla value without side effects and checks the invariants:
    /// party ≥ vanilla and party − vanilla = extra · b₀ for eligible entries, party = vanilla for all others,
    /// dry run keeps vanilla.
    /// </summary>
    public static void AuditProbability(EntityPlayer _player, LootContainer.LootEntry _entry, float _lootstage, bool _ignoreLootProb, float _result)
    {
        GameCatalog catalog = GameCatalog.Current;
        if (catalog == null || _entry == null)
        {
            return;
        }
        if (!UsesLootProb(_entry, _ignoreLootProb))
        {
            return;
        }
        SpawnTrace trace = MergeContext.Trace;
        FastTags<TagGroup.Global> tags = GameCatalog.EntryTags(_entry);
        bool eligible = catalog.IsEligible(tags);
        if (eligible)
        {
            trace.EligibleEntryCalls++;
        }
        else
        {
            trace.NonEligibleEntryCalls++;
        }
        if (!AuditLog.Detailed)
        {
            return;
        }

        // Numbers of the progression evaluation that produced _result (read before the replications overwrite them).
        bool hasProgression = MergeContext.LastProgressionValid;
        float baseRef = MergeContext.LastBaseRef;
        float baseFinal = MergeContext.LastBaseFinal;
        float extra = MergeContext.LastExtra;

        EntryTrace entry = trace.GetOrAddEntry(_entry, tags, eligible);
        entry.Calls++;
        if (!entry.Evaluated)
        {
            entry.Evaluated = true;
            entry.FirstResult = _result;
            if (hasProgression)
            {
                entry.BaseRef = baseRef;
                entry.BaseFinal = baseFinal;
                entry.Extra = extra;
            }
            entry.Vanilla = Replicate(_player, _entry, _lootstage, _ignoreLootProb, ReplicaMode.Vanilla);
            if (MergeContext.DryRun)
            {
                entry.Party = Replicate(_player, _entry, _lootstage, _ignoreLootProb, ReplicaMode.PartyForced);
                trace.InvariantChecks++;
                if (!DeltaSelector.AlmostEqual(_result, entry.Vanilla) && !(_result == 0f && entry.Vanilla > 0f))
                {
                    trace.Violation("dry_run_keeps_vanilla", $"{entry.Name}: result {_result} != vanilla {entry.Vanilla}");
                }
            }
            else
            {
                float replicated = Replicate(_player, _entry, _lootstage, _ignoreLootProb, ReplicaMode.Party);
                entry.Party = _result;
                if (!DeltaSelector.AlmostEqual(replicated, _result))
                {
                    if (_result == 0f && replicated > 0f)
                    {
                        entry.RequirementsFailed = true;
                        entry.Party = replicated;
                    }
                    else
                    {
                        entry.ReplicationMismatch = true;
                        ModLog.Warning($"Audit replication of '{entry.Name}' differs from the game ({replicated} vs {_result}); getProbability may have changed.", "audit_replication");
                    }
                }
            }
            CheckEntry(trace, entry);
            return;
        }

        // Later calls of the same entry must agree (requirement rolls such as Bookworm may legitimately return 0).
        if (entry.FirstResult == 0f && _result > 0f)
        {
            entry.FirstResult = _result;
            return;
        }
        if (_result > 0f && entry.FirstResult > 0f && !DeltaSelector.AlmostEqual(_result, entry.FirstResult) && !entry.Unstable)
        {
            entry.Unstable = true;
            trace.Violation("deterministic_probability", $"{entry.Name}: {entry.FirstResult} then {_result} within one spawn");
        }
    }

    private static void CheckEntry(SpawnTrace _trace, EntryTrace _entry)
    {
        if (_entry.ReplicationMismatch || _entry.Checked)
        {
            return;
        }
        _entry.Checked = true;
        _trace.InvariantChecks++;
        if (!_entry.Eligible)
        {
            if (!DeltaSelector.AlmostEqual(_entry.Party, _entry.Vanilla))
            {
                _trace.Violation("non_learning_unchanged", $"{_entry.Name} [{_entry.Tags}]: party {_entry.Party} != vanilla {_entry.Vanilla}");
            }
            return;
        }
        if (!DeltaSelector.AlmostLessOrEqual(_entry.Vanilla, _entry.Party))
        {
            _trace.Violation("party_ge_vanilla", $"{_entry.Name}: party {_entry.Party} < vanilla {_entry.Vanilla}");
        }
        // party − vanilla must be exactly the extra counted at the entry's base before the progression evaluation.
        if (!float.IsNaN(_entry.Extra) && _entry.BaseRef > 0f && _entry.BaseFinal > 0f)
        {
            double expectedGain = (double)_entry.Extra * _entry.BaseRef;
            double gain = (double)_entry.Party - _entry.Vanilla;
            if (Math.Abs(gain - expectedGain) > Math.Max(1e-5, Math.Abs(_entry.Party) * 1e-4))
            {
                _trace.Violation("gain_matches_extra", $"{_entry.Name}: party - vanilla = {gain} but extra * b0 = {expectedGain}");
            }
        }
    }

    /// <summary>True when vanilla getProbability runs the LootProb effect evaluation for this entry.</summary>
    public static bool UsesLootProb(LootContainer.LootEntry _entry, bool _ignoreLootProb)
    {
        ItemClass itemClass = _entry.item?.itemValue?.ItemClass;
        if (_entry.item != null && itemClass != null && !itemClass.ItemTags.IsEmpty)
        {
            return !_ignoreLootProb;
        }
        return !_entry.tags.IsEmpty;
    }

    /// <summary>
    /// Re-evaluates the vanilla probability formula of LootContainer.getProbability without its requirement check
    /// (requirements roll random numbers, which must not be consumed twice). Restores CachedEventParam and the
    /// opener's context, so the audit has no effect on the spawn.
    /// </summary>
    private static float Replicate(EntityPlayer _player, LootContainer.LootEntry _entry, float _lootstage, bool _ignoreLootProb, ReplicaMode _mode)
    {
        bool suppress = MergeContext.SuppressMerge;
        bool force = MergeContext.ForceApply;
        bool inAudit = MergeContext.InAudit;
        EventParamGuard.CopyAll(MinEventParams.CachedEventParam, replicaCachedSnapshot);
        EventParamGuard.CopyAll(_player.MinEventContext, openerSnapshot);
        try
        {
            MergeContext.SuppressMerge = _mode == ReplicaMode.Vanilla;
            MergeContext.ForceApply = _mode == ReplicaMode.PartyForced;
            MergeContext.InAudit = true;
            return ProbabilityWithoutRequirements(_player, _entry, _lootstage, _ignoreLootProb);
        }
        catch (Exception e)
        {
            ModLog.Error($"Audit replication failed for '{GameCatalog.EntryName(_entry)}'.", e, "audit_replication");
            return float.NaN;
        }
        finally
        {
            MergeContext.SuppressMerge = suppress;
            MergeContext.ForceApply = force;
            MergeContext.InAudit = inAudit;
            EventParamGuard.CopyAll(openerSnapshot, _player.MinEventContext);
            EventParamGuard.CopyAll(replicaCachedSnapshot, MinEventParams.CachedEventParam);
        }
    }

    /// <summary>Vanilla getProbability (V 3.3 b18) minus the leading HasRequirements check.</summary>
    public static float ProbabilityWithoutRequirements(EntityPlayer _player, LootContainer.LootEntry _entry, float _lootstage, bool _ignoreLootProb)
    {
        if (!string.IsNullOrEmpty(_entry.lootProbTemplate)
            && LootContainer.lootProbTemplates.TryGetValue(_entry.lootProbTemplate, out LootContainer.LootProbabilityTemplate template))
        {
            for (int i = 0; i < template.templates.Count; i++)
            {
                LootContainer.LootEntry level = template.templates[i];
                if (!(level.minLevel <= _lootstage) || !(level.maxLevel >= _lootstage))
                {
                    continue;
                }
                return EvaluateProbability(_player, _entry, level.prob, _ignoreLootProb);
            }
        }
        return EvaluateProbability(_player, _entry, _entry.prob, _ignoreLootProb);
    }

    private static float EvaluateProbability(EntityPlayer _player, LootContainer.LootEntry _entry, float _prob, bool _ignoreLootProb)
    {
        ItemClass itemClass = _entry.item?.itemValue?.ItemClass;
        if (_entry.item != null && itemClass != null && !itemClass.ItemTags.IsEmpty)
        {
            float sandbox = LootContainer.GetSandboxProb(itemClass);
            if (_ignoreLootProb)
            {
                return _prob * sandbox;
            }
            return EffectManager.GetValue(PassiveEffects.LootProb, null, _prob, _player, null, itemClass.ItemTags) * sandbox;
        }
        if (_entry.tags.IsEmpty)
        {
            return _prob;
        }
        return EffectManager.GetValue(PassiveEffects.LootProb, null, _prob, _player, null, _entry.tags);
    }

    // ----- errors and circuit breaker --------------------------------------------------------

    public static void RegisterError(string _context, string _detail, Exception _exception)
    {
        SessionErrors++;
        RuntimeStats.MergeErrors++;
        if (SessionErrors <= 5 || SessionErrors % 10 == 0)
        {
            ModLog.Error($"Merge error in {_context}: {_detail} (error {SessionErrors} of {BreakerThreshold} before the circuit breaker opens).", _exception, _context);
        }
        if (!BreakerOpen && SessionErrors >= BreakerThreshold)
        {
            BreakerOpen = true;
            ModLog.Error($"Circuit breaker opened after {SessionErrors} errors: party merging is disabled (loot stays vanilla) until 'pll reload'.", null, "circuit_breaker");
            if (AuditLog.IsOpen)
            {
                JsonLineWriter record = AuditLog.Begin(AuditRecords.CircuitBreaker);
                record.Prop("state", "open").Prop("errors", SessionErrors).Prop("threshold", BreakerThreshold).Prop("last_context", _context);
                AuditLog.Commit(record, _flushSoon: true);
            }
        }
    }

    public static void ResetBreaker(string _reason)
    {
        bool wasOpen = BreakerOpen;
        BreakerOpen = false;
        SessionErrors = 0;
        if (wasOpen && AuditLog.IsOpen)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.CircuitBreaker);
            record.Prop("state", "reset").Prop("reason", _reason);
            AuditLog.Commit(record);
        }
    }

    // ----- cache key ----------------------------------------------------------------------------

    /// <summary>Identifies one member evaluation: same perk, same tag content, same input values.</summary>
    private readonly struct DeltaKey : IEquatable<DeltaKey>
    {
        private readonly ProgressionClass cls;
        private readonly int singleBit;
        private readonly ulong[] bits;
        private readonly int hash;
        private readonly float baseIn;
        private readonly float percIn;

        public DeltaKey(ProgressionClass _cls, FastTags<TagGroup.Global> _tags, float _baseIn, float _percIn)
        {
            cls = _cls;
            singleBit = _tags.singleBit;
            bits = _tags.bits;
            baseIn = _baseIn;
            percIn = _percIn;
            int h = RuntimeHelpers.GetHashCode(_cls);
            h = h * 31 + singleBit;
            if (bits != null)
            {
                int last = LastNonZero(bits);
                for (int i = 0; i <= last; i++)
                {
                    h = h * 31 + bits[i].GetHashCode();
                }
            }
            h = h * 31 + _baseIn.GetHashCode();
            h = h * 31 + _percIn.GetHashCode();
            hash = h;
        }

        public bool Equals(DeltaKey _other)
        {
            return ReferenceEquals(cls, _other.cls)
                   && singleBit == _other.singleBit
                   && baseIn.Equals(_other.baseIn)
                   && percIn.Equals(_other.percIn)
                   && SameBits(bits, _other.bits);
        }

        public override bool Equals(object _obj) => _obj is DeltaKey other && Equals(other);

        public override int GetHashCode() => hash;

        private static int LastNonZero(ulong[] _bits)
        {
            for (int i = _bits.Length - 1; i >= 0; i--)
            {
                if (_bits[i] != 0UL)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool SameBits(ulong[] _a, ulong[] _b)
        {
            if (ReferenceEquals(_a, _b))
            {
                return true;
            }
            int lengthA = _a == null ? 0 : _a.Length;
            int lengthB = _b == null ? 0 : _b.Length;
            int max = Math.Max(lengthA, lengthB);
            for (int i = 0; i < max; i++)
            {
                ulong a = i < lengthA ? _a[i] : 0UL;
                ulong b = i < lengthB ? _b[i] : 0UL;
                if (a != b)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
