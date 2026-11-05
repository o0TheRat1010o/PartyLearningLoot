using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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

    /// <summary>The weight the opener's own perks give an entry of this tag with base weight 1 (what vanilla draws with).</summary>
    public float Vanilla;

    /// <summary>The extra weight the party's perks add (zero for probes and without a party).</summary>
    public float Gain;

    /// <summary>The weight one person with all perks of the party would have.</summary>
    public float Party => Vanilla + Gain;

    public string WinnerPerk;
    public int WinnerId = -1;
    public string WinnerName;
    public float WinnerGain;
}

/// <summary>One entry of an explained loot list: weights and shares of the opener and of the party person.</summary>
internal sealed class ExplainEntry
{
    public string Name;
    public string Tags;
    public double Vanilla;
    public double Party;
    public double VanillaShare;
    public double PartyShare;
    public double Gain;
}

/// <summary>The plan of one loot list of the explained container.</summary>
internal sealed class ExplainList
{
    public string Name;
    public bool IsAll;
    public double SumVanilla;
    public double SumParty;

    /// <summary>G for pick lists: bonus draws per vanilla draw; Σ chances for all lists.</summary>
    public double TotalGain;
    public int Raised;
    public string Problem;
    public readonly List<ExplainEntry> Entries = new List<ExplainEntry>();
}

internal sealed class ExplainResult
{
    public EntityPlayer Player;
    public PartySnapshot Snapshot;
    public string Container;
    public float LootStage;
    public readonly List<ExplainRow> Rows = new List<ExplainRow>();
    public readonly List<ExplainList> Lists = new List<ExplainList>();
    public readonly List<string> Problems = new List<string>();
    public int InvariantChecks;
    public string Error;

    public bool Ok => Error == null && Problems.Count == 0;
}

/// <summary>
/// Evaluates LootProb for every merged tag (learning material and, with IncludeOtherLoot, other loot) of a real player
/// and party through the same patched code path the bonus planner uses (but without spawning anything): the weight the
/// opener has and the weight the party's perks would add. Also probes unmerged tags, which must stay exactly vanilla,
/// and optionally plans the loot lists of a container. Used by "pll explain" and by the live part of the selftest.
/// </summary>
internal static class MergeExplainer
{
    public const int UnmergedProbes = 3;
    public const int MaxListsPerContainer = 400;

    public static ExplainResult Explain(EntityPlayer _player, ModSettings _settings, string _container = null)
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
        LootContainer container = null;
        if (!string.IsNullOrEmpty(_container))
        {
            container = LootContainer.GetLootContainer(_container, false);
            if (container == null)
            {
                result.Error = $"unknown loot container '{_container}'";
                return result;
            }
            result.Container = container.Name;
            result.LootStage = container.useUnmodifiedLootstage ? _player.unModifiedGameStage : _player.GetHighestPartyLootStage(0f, 0f);
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
            result.Rows.Add(new ExplainRow { Tag = tag.Key, Kind = tag.Value, Vanilla = Evaluate(_player, mask) });
        }

        if (result.Snapshot.VanillaReason != null)
        {
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
                MergeContext.BeginProbe();
                float observed;
                try
                {
                    observed = Evaluate(_player, mask);
                    row.Gain = MergeContext.ProbeGain;
                }
                finally
                {
                    MergeContext.EndProbe();
                }
                if (!DeltaSelector.AlmostEqual(observed, row.Vanilla))
                {
                    result.Problems.Add($"{row.Tag}: observing the party changed the opener's weight ({row.Vanilla} -> {observed})");
                }
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
            if (container != null)
            {
                ExplainLists(result, container, catalog, _player);
            }
        }
        finally
        {
            MergeContext.TraceAlways = false;
            MergeContext.Deactivate(token);
        }

        foreach (ExplainRow row in result.Rows)
        {
            result.InvariantChecks++;
            if (row.Merged && !(row.Gain >= 0f))
            {
                result.Problems.Add($"{row.Tag}: extra weight {row.Gain} is negative");
            }
            if (!row.Merged && row.Gain != 0f)
            {
                result.Problems.Add($"unmerged {row.Tag}: extra weight {row.Gain} instead of none");
            }
        }
        if (!MergeContext.IsClean())
        {
            result.Problems.Add("merge context not clean after explain");
            MergeContext.Reset();
        }
        return result;
    }

    /// <summary>LootProb of a base value of 1 for the given tags; the party is only observed while a probe is open.</summary>
    private static float Evaluate(EntityPlayer _player, FastTags<TagGroup.Global> _tags)
    {
        return EffectManager.GetValue(PassiveEffects.LootProb, null, 1f, _player, null, _tags);
    }

    // ----- the loot lists of a container ----------------------------------------------------------------

    private static void ExplainLists(ExplainResult _result, LootContainer _container, GameCatalog _catalog, EntityPlayer _player)
    {
        var args = new SpawnArgs
        {
            Player = _player,
            LootStage = _result.LootStage,
            Abundance = 1f,
            IgnoreLootProb = _container.IgnoreLootProb,
            UniqueItems = _container.UniqueItems,
            ContainerTags = FastTags<TagGroup.Global>.none
        };
        var trace = new BonusTrace();
        trace.Reset(false, false);
        var visited = new HashSet<List<LootContainer.LootEntry>>(ReferenceComparer.Instance);
        var pending = new Queue<KeyValuePair<string, List<LootContainer.LootEntry>>>();
        pending.Enqueue(new KeyValuePair<string, List<LootContainer.LootEntry>>(_container.Name, _container.itemsToSpawn));
        bool rootDeclaredAll = _container.minCount < 0;
        bool first = true;
        while (pending.Count > 0 && visited.Count < MaxListsPerContainer)
        {
            KeyValuePair<string, List<LootContainer.LootEntry>> next = pending.Dequeue();
            if (!visited.Add(next.Value))
            {
                continue;
            }
            foreach (LootContainer.LootEntry entry in next.Value)
            {
                if (entry?.group != null)
                {
                    pending.Enqueue(new KeyValuePair<string, List<LootContainer.LootEntry>>(GameCatalog.GroupListName(entry.group.name), entry.group.items));
                }
            }
            bool isAll = first ? rootDeclaredAll : (IsDeclaredAll(next.Key));
            first = false;
            if (!_catalog.ListIndex.TryGet(next.Value, out LootListInfo info))
            {
                continue;
            }
            ListPlan plan = PlanBuilder.Build(info, isAll, args, GameWeightSource.Instance, trace);
            _result.Lists.Add(Describe(plan));
        }
        _result.InvariantChecks += trace.InvariantChecks;
        foreach (KeyValuePair<string, string> violation in trace.Violations)
        {
            _result.Problems.Add($"{violation.Key}: {violation.Value}");
        }
        _result.Lists.Sort((a, b) => b.TotalGain.CompareTo(a.TotalGain));
    }

    private static bool IsDeclaredAll(string _listName)
    {
        const string prefix = "group:";
        return _listName.StartsWith(prefix, StringComparison.Ordinal)
               && LootContainer.lootGroups.TryGetValue(_listName.Substring(prefix.Length), out LootContainer.LootGroup group)
               && group.minCount < 0;
    }

    private static ExplainList Describe(ListPlan _plan)
    {
        var list = new ExplainList { Name = _plan.Info.Name, IsAll = _plan.IsAll, Problem = _plan.Valid ? null : _plan.Problem ?? "invalid" };
        if (!_plan.Valid)
        {
            return list;
        }
        list.TotalGain = _plan.TotalGain;
        if (_plan.Gain != null)
        {
            list.SumVanilla = _plan.Gain.SumVanilla;
            list.SumParty = _plan.Gain.SumParty;
        }
        for (int i = 0; i < _plan.Info.Entries.Length; i++)
        {
            if (!_plan.Probed[i] || !_plan.Info.Eligible[i])
            {
                continue;
            }
            LootContainer.LootEntry entry = _plan.Info.Entries[i];
            ProbeResult probe = _plan.Probes[i];
            var row = new ExplainEntry
            {
                Name = GameCatalog.EntryName(entry),
                Tags = string.Join(",", GameCatalog.EntryTags(entry).GetTagNames()),
                Vanilla = probe.Vanilla,
                Party = (double)probe.Vanilla + probe.Extra
            };
            if (_plan.IsAll)
            {
                row.Gain = _plan.Chance[i];
            }
            else if (_plan.Gain.Valid)
            {
                row.VanillaShare = _plan.Gain.VanillaShare[i];
                row.PartyShare = _plan.Gain.PartyShare[i];
                row.Gain = _plan.Gain.Gain[i];
            }
            if (row.Gain > 0d)
            {
                list.Raised++;
            }
            list.Entries.Add(row);
        }
        return list;
    }

    // ----- output ------------------------------------------------------------------------------------------

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
        yield return "Weight of an entry with base weight 1: the opener's own (vanilla) weight, the extra weight the party's perks add, and the weight one person with all of them would have.";
        var table = new TextTable("Tag", "Kind", "Vanilla", "Extra", "Party", "Change", "Best member perk").AlignRight(2, 3, 4, 5);
        foreach (ExplainRow row in _result.Rows)
        {
            string change = row.Vanilla > 0f ? ((row.Party / row.Vanilla - 1f) * 100f).ToString("+0;-0;0") + "%" : (row.Party > 0f ? "new" : "0%");
            string winner = row.WinnerPerk != null ? $"{row.WinnerPerk} by {row.WinnerName} (+{row.WinnerGain:0.##})" : "-";
            table.Add(row.Tag, row.Kind, row.Vanilla.ToString("0.###"), row.Gain.ToString("0.###"), row.Party.ToString("0.###"), change, winner);
        }
        foreach (string line in table.Lines())
        {
            yield return line;
        }
        if (_result.Container != null)
        {
            foreach (string line in FormatLists(_result))
            {
                yield return line;
            }
        }
        yield return _result.Problems.Count == 0
            ? "Checks: extra weight >= 0 for every merged tag, probes unchanged, weights untouched by the observation: OK"
            : "Checks FAILED: " + string.Join("; ", _result.Problems);
    }

    private static IEnumerable<string> FormatLists(ExplainResult _result)
    {
        if (_result.Lists.Count == 0)
        {
            yield return $"Container '{_result.Container}' (loot stage {_result.LootStage:0}): no list of it has an entry the party bonus applies to.";
            yield break;
        }
        yield return $"Bonus of '{_result.Container}' (loot stage {_result.LootStage:0}): per vanilla draw from a list the party adds G extra draws (pick lists), " +
                     "or an extra chance per entry (lists where every entry is rolled on its own).";
        var overview = new TextTable("List", "Kind", "G / chance", "Raised", "Note").AlignRight(2, 3);
        foreach (ExplainList list in _result.Lists.Take(25))
        {
            overview.Add(list.Name, list.IsAll ? "all" : "pick", list.TotalGain.ToString("0.0000"), list.Raised, list.Problem ?? string.Empty);
        }
        foreach (string line in overview.Lines())
        {
            yield return line;
        }
        if (_result.Lists.Count > 25)
        {
            yield return $"... and {_result.Lists.Count - 25} more lists";
        }
        ExplainList top = _result.Lists.FirstOrDefault(l => l.Problem == null && l.Raised > 0);
        if (top == null)
        {
            yield break;
        }
        yield return $"Entries of '{top.Name}' the party raises (share = chance of one draw to pick the entry):";
        var entries = new TextTable("Entry", "Tags", "Vanilla", "Party", "Share", "Party share", "Gain").AlignRight(2, 3, 4, 5, 6);
        foreach (ExplainEntry entry in top.Entries.Where(e => e.Gain > 0d).OrderByDescending(e => e.Gain).Take(15))
        {
            entries.Add(entry.Name, entry.Tags, entry.Vanilla.ToString("0.###"), entry.Party.ToString("0.###"),
                top.IsAll ? "-" : entry.VanillaShare.ToString("0.0000"), top.IsAll ? "-" : entry.PartyShare.ToString("0.0000"), entry.Gain.ToString("0.0000"));
        }
        foreach (string line in entries.Lines())
        {
            yield return line;
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<List<LootContainer.LootEntry>>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public bool Equals(List<LootContainer.LootEntry> _a, List<LootContainer.LootEntry> _b) => ReferenceEquals(_a, _b);
        public int GetHashCode(List<LootContainer.LootEntry> _obj) => RuntimeHelpers.GetHashCode(_obj);
    }
}
