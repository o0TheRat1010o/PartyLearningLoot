using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PartyLearningLoot.Engine;

/// <summary>A loot list (a container's list or a loot group) that has entries the party bonus applies to.</summary>
internal sealed class LootListInfo
{
    public const string ProblemRandomRoll = "random_roll";
    public const string ProblemUnknownRequirement = "unknown_requirement";

    public string Name;
    public List<LootContainer.LootEntry> List;

    /// <summary>The entries at the time the index was built; a list that changed since is left alone.</summary>
    public LootContainer.LootEntry[] Entries;

    /// <summary>Per entry: true when the party bonus applies to it.</summary>
    public bool[] Eligible;

    public int EligibleCount;

    /// <summary>At least one entry rolls independently (force_prob).</summary>
    public bool HasForceProb;

    /// <summary>
    /// Per entry: null, <see cref="ProblemRandomRoll"/> or <see cref="ProblemUnknownRequirement"/>. The requirements of an entry
    /// are evaluated whenever the bonus probes it or draws it; a random roll (or a class from a mod that might roll) makes
    /// its weight different on every evaluation, so such an entry is left alone.
    /// </summary>
    public string[] EntryProblem;

    /// <summary>
    /// A pick list is planned from the weights of all its entries: one entry with a problem leaves the whole list alone
    /// (null when none has one). Lists where every entry is rolled on its own only touch the entries they raise.
    /// </summary>
    public string PickProblem;

    /// <summary>The list belongs to a group declared with count="all" (informative; every visit decides on its own).</summary>
    public bool DeclaredAll;

    public bool Matches => List != null && List.Count == Entries.Length;
}

/// <summary>
/// All loot lists that contain at least one entry the party bonus applies to, found by identity of the list object.
/// Built when the catalog is built; looking up a list costs one dictionary access.
/// </summary>
internal sealed class LootListIndex
{
    private readonly Dictionary<List<LootContainer.LootEntry>, LootListInfo> byList =
        new Dictionary<List<LootContainer.LootEntry>, LootListInfo>(ReferenceComparer.Instance);

    public static readonly LootListIndex Empty = new LootListIndex();

    public int Count => byList.Count;

    public IEnumerable<LootListInfo> Lists => byList.Values;

    public int EligibleEntries
    {
        get
        {
            int total = 0;
            foreach (LootListInfo info in byList.Values)
            {
                total += info.EligibleCount;
            }
            return total;
        }
    }

    public bool TryGet(List<LootContainer.LootEntry> _list, out LootListInfo _info)
    {
        if (_list == null)
        {
            _info = null;
            return false;
        }
        return byList.TryGetValue(_list, out _info);
    }

    /// <summary>
    /// Indexes the given lists. A list is kept when <paramref name="_isEligible"/> accepts at least one of its entries.
    /// <paramref name="_declaredAll"/> (optional) tells which lists are declared with count="all".
    /// </summary>
    public static LootListIndex Build(IEnumerable<KeyValuePair<string, List<LootContainer.LootEntry>>> _lists, Func<LootContainer.LootEntry, bool> _isEligible,
        Func<string, bool> _declaredAll = null)
    {
        var index = new LootListIndex();
        foreach (KeyValuePair<string, List<LootContainer.LootEntry>> source in _lists)
        {
            List<LootContainer.LootEntry> list = source.Value;
            if (list == null || list.Count == 0 || index.byList.ContainsKey(list))
            {
                continue;
            }
            var info = new LootListInfo
            {
                Name = source.Key,
                List = list,
                Entries = list.ToArray(),
                Eligible = new bool[list.Count],
                EntryProblem = new string[list.Count],
                DeclaredAll = _declaredAll != null && _declaredAll(source.Key)
            };
            for (int i = 0; i < info.Entries.Length; i++)
            {
                LootContainer.LootEntry entry = info.Entries[i];
                if (entry == null)
                {
                    info.EntryProblem[i] = LootListInfo.ProblemUnknownRequirement;
                    info.PickProblem = Worse(info.PickProblem, info.EntryProblem[i]);
                    continue;
                }
                info.HasForceProb |= entry.forceProb;
                info.EntryProblem[i] = ClassifyRequirements(entry);
                info.PickProblem = Worse(info.PickProblem, info.EntryProblem[i]);
                if (_isEligible(entry))
                {
                    info.Eligible[i] = true;
                    info.EligibleCount++;
                }
            }
            if (info.EligibleCount > 0)
            {
                index.byList.Add(list, info);
            }
        }
        return index;
    }

    /// <summary>
    /// The requirements of an entry are evaluated by the game inside getProbability. The five requirement classes of the
    /// game are deterministic; a random roll gives an entry another weight on every evaluation, and an unknown class (from
    /// a mod) cannot be judged. Such entries are left alone (see <see cref="LootListInfo.EntryProblem"/>).
    /// </summary>
    public static string ClassifyRequirements(LootContainer.LootEntry _entry)
    {
        if (_entry.Requirements == null)
        {
            return null;
        }
        string problem = null;
        foreach (BaseLootEntryRequirement requirement in _entry.Requirements)
        {
            Type type = requirement?.GetType();
            if (type == typeof(LootEntryRequirementRandomRoll))
            {
                return LootListInfo.ProblemRandomRoll;
            }
            if (type != typeof(LootEntryRequirementBiome)
                && type != typeof(LootEntryRequirementCVar)
                && type != typeof(LootEntryRequirementProgression)
                && type != typeof(LootEntryRequirementQuestTags)
                && type != typeof(LootEntryRequirementSandboxOption))
            {
                problem = LootListInfo.ProblemUnknownRequirement;
            }
        }
        return problem;
    }

    private static string Worse(string _a, string _b)
    {
        if (_a == LootListInfo.ProblemRandomRoll || _b == LootListInfo.ProblemRandomRoll)
        {
            return LootListInfo.ProblemRandomRoll;
        }
        return _a ?? _b;
    }

    private sealed class ReferenceComparer : IEqualityComparer<List<LootContainer.LootEntry>>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public bool Equals(List<LootContainer.LootEntry> _a, List<LootContainer.LootEntry> _b) => ReferenceEquals(_a, _b);
        public int GetHashCode(List<LootContainer.LootEntry> _obj) => RuntimeHelpers.GetHashCode(_obj);
    }
}
