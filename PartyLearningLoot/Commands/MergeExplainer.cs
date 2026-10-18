using System;
using System.Collections.Generic;
using System.Linq;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Commands;

internal sealed class ExplainRow
{
    public const string KindLearning = "learning";
    public const string KindOtherLoot = "loot";
    public const string KindProbe = "probe";

    public string Tag;

    /// <summary>learning (magazine/book tag), loot (other loot, merged with IncludeOtherLoot) or probe (never merged).</summary>
    public string Kind;
    public bool Merged => Kind != KindProbe;
    public float Vanilla;
    public float Party;
    public string WinnerPerk;
    public int WinnerId = -1;
    public string WinnerName;
    public float WinnerGain;
}

internal sealed class ExplainResult
{
    public EntityPlayer Player;
    public PartySnapshot Snapshot;
    public readonly List<ExplainRow> Rows = new List<ExplainRow>();
    public readonly List<string> Problems = new List<string>();
    public string Error;

    public bool Ok => Error == null && Problems.Count == 0;
}

/// <summary>
/// Evaluates LootProb for every merged tag (learning material and, with IncludeOtherLoot, other loot) of a real player
/// and party, vanilla versus merged, through the same patched code path a spawn uses (but without spawning anything).
/// Also probes unmerged tags, which must stay exactly vanilla. Used by "pll explain" and by the live part of the selftest.
/// </summary>
internal static class MergeExplainer
{
    public const int UnmergedProbes = 3;

    public static ExplainResult Explain(EntityPlayer _player, ModSettings _settings)
    {
        var result = new ExplainResult { Player = _player };
        GameCatalog catalog = GameCatalog.Current;
        if (catalog == null || catalog.IsEmpty)
        {
            result.Error = "the learning catalog is not available";
            return result;
        }
        if (MergeContext.Active)
        {
            result.Error = "a merge is already running";
            return result;
        }
        result.Snapshot = PartySnapshot.Capture(_player, _settings.MaxMemberDistance, _settings.IncludeDeadMembers, _settings.IncludeSpectators);

        var tags = catalog.Data.LearningTags.Select(t => new KeyValuePair<string, string>(t, ExplainRow.KindLearning))
            .Concat(catalog.Data.OtherLootTags.Select(t => new KeyValuePair<string, string>(t, ExplainRow.KindOtherLoot)))
            .Concat(catalog.Data.UnmergedTags.Take(UnmergedProbes).Select(t => new KeyValuePair<string, string>(t, ExplainRow.KindProbe)))
            .Where(t => GameCatalog.IsKnownTag(t.Key))
            .ToList();

        foreach (KeyValuePair<string, string> tag in tags)
        {
            FastTags<TagGroup.Global> mask = FastTags<TagGroup.Global>.Parse(tag.Key);
            result.Rows.Add(new ExplainRow { Tag = tag.Key, Kind = tag.Value, Vanilla = Evaluate(_player, mask), Party = float.NaN });
        }

        if (result.Snapshot.VanillaReason != null)
        {
            foreach (ExplainRow row in result.Rows)
            {
                row.Party = row.Vanilla;
            }
            return result;
        }

        ActivationToken token = MergeContext.Activate(_player, result.Snapshot, LootOpenKind.Explain, "explain", false);
        if (!token.Activated)
        {
            result.Error = "the merge context could not be activated";
            return result;
        }
        try
        {
            MergeContext.TraceAlways = true;
            foreach (ExplainRow row in result.Rows)
            {
                int firstPerk = MergeContext.Trace.Perks.Count;
                FastTags<TagGroup.Global> mask = FastTags<TagGroup.Global>.Parse(row.Tag);
                row.Party = Evaluate(_player, mask);
                PerkTrace best = null;
                for (int i = firstPerk; i < MergeContext.Trace.Perks.Count; i++)
                {
                    PerkTrace perk = MergeContext.Trace.Perks[i];
                    if (perk.WinnerSlot >= 0 && (best == null || perk.Extra > best.Extra))
                    {
                        best = perk;
                    }
                }
                if (best != null)
                {
                    MemberSlot slot = MergeContext.Members[best.WinnerSlot];
                    row.WinnerPerk = best.Perk;
                    row.WinnerId = slot.EntityId;
                    row.WinnerName = slot.Name;
                    row.WinnerGain = best.Extra;
                }
            }
        }
        finally
        {
            MergeContext.TraceAlways = false;
            MergeContext.Deactivate(token);
        }

        foreach (ExplainRow row in result.Rows)
        {
            if (row.Merged && !DeltaSelector.AlmostLessOrEqual(row.Vanilla, row.Party))
            {
                result.Problems.Add($"{row.Tag}: party {row.Party} < vanilla {row.Vanilla}");
            }
            if (!row.Merged && !DeltaSelector.AlmostEqual(row.Vanilla, row.Party))
            {
                result.Problems.Add($"unmerged {row.Tag}: party {row.Party} != vanilla {row.Vanilla}");
            }
        }
        if (!MergeContext.IsClean())
        {
            result.Problems.Add("merge context not clean after explain");
            MergeContext.Reset();
        }
        return result;
    }

    /// <summary>LootProb of a base value of 1 for the given tags; merges only while a context is active.</summary>
    private static float Evaluate(EntityPlayer _player, FastTags<TagGroup.Global> _tags)
    {
        return EffectManager.GetValue(PassiveEffects.LootProb, null, 1f, _player, null, _tags);
    }

    public static IEnumerable<string> Format(ExplainResult _result)
    {
        if (_result.Error != null)
        {
            yield return "explain: " + _result.Error;
            yield break;
        }
        PartySnapshot snapshot = _result.Snapshot;
        yield return $"Explain for {PartySnapshot.DisplayName(_result.Player)} (entity {_result.Player.entityId}), " +
                     (snapshot.VanillaReason != null
                         ? $"vanilla: {snapshot.VanillaReason}"
                         : $"party {snapshot.PartyId}, members: {string.Join(", ", snapshot.Members.Select(m => $"{m.Candidate.Name} ({m.Candidate.Distance:0} m)"))}");
        foreach (KeyValuePair<MemberCandidate, ExclusionReason> excluded in snapshot.Excluded)
        {
            yield return $"  excluded: {excluded.Key.Name} ({MemberFilter.ToCode(excluded.Value)})";
        }
        var table = new TextTable("Tag", "Kind", "Vanilla", "Party", "Change", "Best member perk").AlignRight(2, 3, 4);
        foreach (ExplainRow row in _result.Rows)
        {
            string change = row.Vanilla > 0f ? ((row.Party / row.Vanilla - 1f) * 100f).ToString("+0;-0;0") + "%" : (row.Party > 0f ? "new" : "0%");
            string winner = row.WinnerPerk != null ? $"{row.WinnerPerk} by {row.WinnerName} (+{row.WinnerGain:0.##})" : "-";
            table.Add(row.Tag, row.Kind, row.Vanilla.ToString("0.###"), row.Party.ToString("0.###"), change, winner);
        }
        foreach (string line in table.Lines())
        {
            yield return line;
        }
        yield return _result.Problems.Count == 0
            ? "Checks: party >= vanilla for every merged tag, probes unchanged: OK"
            : "Checks FAILED: " + string.Join("; ", _result.Problems);
    }
}
