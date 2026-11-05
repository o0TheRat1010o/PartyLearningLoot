using System;
using System.Collections.Generic;
using System.Linq;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>Session counters for "pll status", the periodic "stats" record and "session_end". Main thread only.</summary>
internal static class RuntimeStats
{
    public static DateTime SessionStartUtc = DateTime.UtcNow;

    public static long SpawnsMerged;
    public static long SpawnsMergedDryRun;
    public static long SpawnsVanilla;
    public static readonly Dictionary<string, long> VanillaReasons = new Dictionary<string, long>(StringComparer.Ordinal);
    public static double MergedMsTotal;
    public static double MergedMsMax;
    public static long MemberEvaluations;
    public static long CacheHits;
    public static long MemberWins;
    public static long LearningItemsInMergedSpawns;
    public static long OtherLootItemsInMergedSpawns;
    public static long InvariantViolations;
    public static long MergeErrors;

    // The bonus (merged spawns of real openers only; dry runs plan but draw nothing).
    public static long BonusVisits;
    public static long BonusPlans;
    public static long BonusCalls;
    public static long BonusPlannedDraws;
    public static double BonusExpectedDraws;
    public static double BonusRoundingVariance;
    public static long BonusItemsAdded;
    public static long BonusLearningItemsAdded;
    public static double BonusMsPlanTotal;
    public static double BonusMsDrawTotal;
    public static readonly Dictionary<string, long> BonusSkipped = new Dictionary<string, long>(StringComparer.Ordinal);

    public static long SharedReads;
    public static long SharedReadsRejected;
    public static long SharedReadsRateLimited;
    public static long SharedMemberLearnings;
    public static long SharedXpGrants;
    public static long SharedPackets;

    public static long ConfirmConfirmed;
    public static long ConfirmMismatchWarnings;
    public static long ConfirmFailed;
    public static long ConfirmUnconfirmed;

    public static void Reset()
    {
        SessionStartUtc = DateTime.UtcNow;
        SpawnsMerged = SpawnsMergedDryRun = SpawnsVanilla = 0;
        VanillaReasons.Clear();
        MergedMsTotal = MergedMsMax = 0;
        MemberEvaluations = CacheHits = MemberWins = LearningItemsInMergedSpawns = OtherLootItemsInMergedSpawns = InvariantViolations = MergeErrors = 0;
        BonusVisits = BonusPlans = BonusCalls = BonusPlannedDraws = BonusItemsAdded = BonusLearningItemsAdded = 0;
        BonusExpectedDraws = BonusRoundingVariance = BonusMsPlanTotal = BonusMsDrawTotal = 0d;
        BonusSkipped.Clear();
        SharedReads = SharedReadsRejected = SharedReadsRateLimited = SharedMemberLearnings = SharedXpGrants = SharedPackets = 0;
        ConfirmConfirmed = ConfirmMismatchWarnings = ConfirmFailed = ConfirmUnconfirmed = 0;
    }

    public static void CountVanilla(string _reason)
    {
        SpawnsVanilla++;
        VanillaReasons.TryGetValue(_reason, out long count);
        VanillaReasons[_reason] = count + 1;
    }

    public static void CountMerged(SpawnTrace _trace, int _learningItems, int _otherLootItems)
    {
        SpawnsMerged++;
        if (_trace.DryRun)
        {
            SpawnsMergedDryRun++;
        }
        double ms = _trace.ElapsedMs;
        MergedMsTotal += ms;
        MergedMsMax = Math.Max(MergedMsMax, ms);
        MemberEvaluations += _trace.MemberEvaluations;
        CacheHits += _trace.CacheHits;
        MemberWins += _trace.MemberWins;
        LearningItemsInMergedSpawns += _learningItems;
        OtherLootItemsInMergedSpawns += _otherLootItems;

        BonusTrace bonus = _trace.Bonus;
        BonusVisits += bonus.Visits;
        BonusPlans += bonus.Plans;
        BonusCalls += bonus.Calls;
        BonusPlannedDraws += bonus.PlannedDraws;
        BonusExpectedDraws += bonus.ExpectedDraws;
        BonusRoundingVariance += bonus.RoundingVariance;
        BonusMsPlanTotal += bonus.MsPlan;
        BonusMsDrawTotal += bonus.MsBonus;
        foreach (BonusAddedItem item in bonus.Added)
        {
            BonusItemsAdded += item.Count;
            if (item.Learn != null)
            {
                BonusLearningItemsAdded += item.Count;
            }
        }
        foreach (KeyValuePair<string, int> skipped in bonus.Skipped)
        {
            BonusSkipped.TryGetValue(skipped.Key, out long count);
            BonusSkipped[skipped.Key] = count + skipped.Value;
        }
    }

    public static double MergedMsAverage => SpawnsMerged == 0 ? 0d : MergedMsTotal / SpawnsMerged;

    /// <summary>Writes all counters as nested objects (stats and session_end records).</summary>
    public static void Write(JsonLineWriter _record)
    {
        _record.Prop("uptime_s", Math.Round((DateTime.UtcNow - SessionStartUtc).TotalSeconds, 1));
        _record.BeginObject("spawns")
            .Prop("merged", SpawnsMerged)
            .Prop("merged_dry_run", SpawnsMergedDryRun)
            .Prop("vanilla", SpawnsVanilla)
            .Prop("avg_ms", Math.Round(MergedMsAverage, 3))
            .Prop("max_ms", Math.Round(MergedMsMax, 3))
            .Prop("member_evals", MemberEvaluations)
            .Prop("cache_hits", CacheHits)
            .Prop("member_wins", MemberWins)
            .Prop("learning_items", LearningItemsInMergedSpawns)
            .Prop("other_loot_items", OtherLootItemsInMergedSpawns);
        _record.BeginObject("vanilla_reasons");
        foreach (KeyValuePair<string, long> reason in VanillaReasons.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            _record.Prop(reason.Key, reason.Value);
        }
        _record.EndObject().EndObject();
        _record.BeginObject("bonus")
            .Prop("visits", BonusVisits)
            .Prop("plans", BonusPlans)
            .Prop("calls", BonusCalls)
            .Prop("planned", BonusPlannedDraws)
            .Prop("expected", Math.Round(BonusExpectedDraws, 6))
            .Prop("variance", Math.Round(BonusRoundingVariance, 6))
            .Prop("items", BonusItemsAdded)
            .Prop("learning_items", BonusLearningItemsAdded)
            .Prop("ms_plan", Math.Round(BonusMsPlanTotal, 3))
            .Prop("ms_draw", Math.Round(BonusMsDrawTotal, 3));
        _record.BeginObject("skipped");
        foreach (KeyValuePair<string, long> skipped in BonusSkipped.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            _record.Prop(skipped.Key, skipped.Value);
        }
        _record.EndObject().EndObject();
        _record.BeginObject("shared_reading")
            .Prop("reads", SharedReads)
            .Prop("rejected", SharedReadsRejected)
            .Prop("rate_limited", SharedReadsRateLimited)
            .Prop("member_learnings", SharedMemberLearnings)
            .Prop("xp_grants", SharedXpGrants)
            .Prop("packets", SharedPackets)
            .EndObject();
        _record.BeginObject("confirmations")
            .Prop("confirmed", ConfirmConfirmed)
            .Prop("mismatch_warnings", ConfirmMismatchWarnings)
            .Prop("failed", ConfirmFailed)
            .Prop("unconfirmed", ConfirmUnconfirmed)
            .Prop("pending", SyncConfirmationTracker.PendingCount)
            .EndObject();
        _record.BeginObject("health")
            .Prop("invariant_violations", InvariantViolations)
            .Prop("merge_errors", MergeErrors)
            .Prop("breaker_open", MergeEngine.BreakerOpen)
            .Prop("warnings", ModLog.WarningCount)
            .Prop("errors", ModLog.ErrorCount)
            .Prop("audit_written", AuditLog.Written)
            .Prop("audit_dropped", AuditLog.Dropped)
            .Prop("audit_write_errors", AuditLog.WriteErrors)
            .EndObject();
    }
}
