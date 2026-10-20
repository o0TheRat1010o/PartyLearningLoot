using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>A progression class whose LootProb bonus may be merged across the party.</summary>
internal sealed class MergeableClass
{
    public ProgressionClass Class;
    public string Name;
    public int ProgressionId;
    public FastTags<TagGroup.Global> MergedTagUnion;

    /// <summary>Progression names referenced by ProgressionLevel requirements on its LootProb effects (e.g. craftingRifles).</summary>
    public string[] RequirementProgressions;
}

/// <summary>
/// Game-facing merge catalog: converts the runtime progression and item data into the pure
/// <see cref="LearningCatalogBuilder"/> input and back into FastTags masks for the hot path.
/// Rebuilt at game start and on "pll reload"; the instance is immutable and swapped atomically.
/// </summary>
internal sealed class GameCatalog
{
    // Expected catalog of V 3.3 (b18); used by the selftest to detect drift after game updates or XML mods.
    public static readonly string[] ExpectedB18LearningTags =
    {
        "armorCSM", "artOfMiningProb", "autoWeaponsProb", "backpackCSM", "barBrawlingProb", "batterUpProb", "bladesCSM",
        "bowsCSM", "clubsCSM", "electricianCSM", "enforcerProb", "explosivesCSM", "foodCSM", "greatHeistProb", "handgunsCSM",
        "harvestingToolsCSM", "huntingProb", "knucklesCSM", "luckyProb", "machineGunsCSM", "medicalCSM", "nightStalkerProb",
        "pistolPeteProb", "rangersProb", "repairToolsCSM", "riflesCSM", "roboticsCSM", "salvageToolsCSM", "seedCSM",
        "shotgunMessiahProb", "shotgunsCSM", "sledgeSagaProb", "sledgehammersCSM", "sniperProb", "spearHunterProb",
        "spearsCSM", "techJunkieProb", "trapsCSM", "urbanProb", "vehiclesCSM", "wastelandProb", "workstationCSM"
    };

    /// <summary>The other LootProb tags of b18: never merged by default, all merged with IncludeOtherLoot.</summary>
    public static readonly string[] ExpectedB18OtherLootTags =
    {
        "WasteTreasuresCompleteHarvest", "ammo44Mag", "ammo762mm", "ammo9mm", "ammoShells", "armorHeavySkill", "armorLightSkill",
        "armorMediumSkill", "bladeSkill", "bowSkill", "clubSkill", "electricianSkill", "explosivesSkill", "foodSkill", "handgunSkill",
        "harvestingSkill", "knuckleSkill", "machinegunSkill", "medicalSkill", "perkMasterChef2", "perkMasterChefSeeds", "repairingSkill",
        "rifleSkill", "roboticsSkill", "salvagingSkill", "seedSkill", "shotgunSkill", "sledgeSkill", "spearSkill", "trapsSkill",
        "vehiclesSkill", "workstationSkill"
    };

    /// <summary>Perks that only weight other loot; mergeable in b18 only with IncludeOtherLoot.</summary>
    public static readonly string[] ExpectedB18OtherLootPerks = { "perkWasteTreasuresComplete" };

    public static readonly string[] ExpectedB18MergeablePerks =
    {
        "perkDeadEye", "perkDemolitionsExpert", "perkJavelinMaster", "perkAnimalTracker", "perkSalvageOperations",
        "perkBoomstick", "perkPummelPete", "perkSkullCrusher", "perkPackMule", "perkMiner69r", "perkBrawler",
        "perkMachineGunner", "perkArchery", "perkGunslinger", "perkDeepCuts", "perkHiddenStrike", "perkFromTheShadows",
        "perkElectrocutioner", "perkTurrets", "perkPhysician", "perkAdvancedEngineering", "perkGreaseMonkey",
        "perkLightArmor", "perkMediumArmor", "perkHeavyArmor", "perkMasterChef", "perkLivingOffTheLand",
        "perkLockPicking", "perkLuckyLooter"
    };

    private static volatile GameCatalog current;

    private readonly Dictionary<ProgressionClass, MergeableClass> mergeable;

    public static GameCatalog Current => current;

    public LearningCatalog Data { get; }

    /// <summary>All merged tags (learning material plus other loot).</summary>
    public FastTags<TagGroup.Global> MergedMask { get; }

    /// <summary>LootProb tags that are never merged.</summary>
    public FastTags<TagGroup.Global> UnmergedMask { get; }

    /// <summary>The other loot tags (empty unless IncludeOtherLoot).</summary>
    public FastTags<TagGroup.Global> OtherLootMask { get; }
    public FastTags<TagGroup.Global> MagazineCategoryMask { get; }
    public FastTags<TagGroup.Global> BookCategoryMask { get; }
    public IReadOnlyCollection<MergeableClass> Mergeable => mergeable.Values;
    public DateTime BuiltUtc { get; }
    public bool IsEmpty => Data.MergedTags.Count == 0 || mergeable.Count == 0;

    private GameCatalog(LearningCatalog _data, Dictionary<ProgressionClass, MergeableClass> _mergeable)
    {
        Data = _data;
        mergeable = _mergeable;
        MergedMask = ParseKnown(_data.MergedTags);
        UnmergedMask = ParseKnown(_data.UnmergedTags);
        OtherLootMask = ParseKnown(_data.OtherLootTags);
        MagazineCategoryMask = ParseKnown(new[] { LearningCatalogBuilder.MagazineCategoryTag });
        BookCategoryMask = ParseKnown(new[] { LearningCatalogBuilder.BookCategoryTag });
        BuiltUtc = DateTime.UtcNow;
    }

    /// <summary>Builds a new catalog from the loaded game data and makes it current.</summary>
    public static GameCatalog Rebuild(ModSettings _settings, List<string> _warnings)
    {
        var classes = new List<ProgressionLootInfo>();
        var requirementNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (Progression.ProgressionClasses != null)
        {
            foreach (ProgressionClass cls in Progression.ProgressionClasses.Values)
            {
                if (cls?.Effects?.EffectGroups == null)
                {
                    continue;
                }
                var effects = new List<LootProbEffectInfo>();
                var progressions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (MinEffectGroup group in cls.Effects.EffectGroups)
                {
                    if (group?.PassiveEffects == null)
                    {
                        continue;
                    }
                    var groupRequirements = new List<string>();
                    CollectRequirements(group.Requirements, groupRequirements, progressions);
                    foreach (PassiveEffect effect in group.PassiveEffects)
                    {
                        if (effect == null || effect.Type != PassiveEffects.LootProb)
                        {
                            continue;
                        }
                        var requirements = new List<string>(groupRequirements);
                        CollectRequirements(effect.Requirements, requirements, progressions);
                        bool usesCVar = effect.CVarValues != null && effect.CVarValues.Any(v => v != null);
                        effects.Add(new LootProbEffectInfo(effect.Tags.GetTagNames(), effect.InvertTagCheck, !effect.MatchAnyTags,
                            usesCVar, effect.Modifier.ToString(), requirements));
                    }
                }
                if (effects.Count > 0)
                {
                    classes.Add(new ProgressionLootInfo(cls.Name, effects));
                    requirementNames[cls.Name] = progressions.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }
        }

        var items = new List<LearningItemInfo>();
        if (ItemClass.list != null)
        {
            foreach (ItemClass item in ItemClass.list)
            {
                if (item == null || item.ItemTags.IsEmpty)
                {
                    continue;
                }
                List<string> tags = item.ItemTags.GetTagNames();
                if (tags.Contains(LearningCatalogBuilder.MagazineCategoryTag, StringComparer.OrdinalIgnoreCase)
                    || tags.Contains(LearningCatalogBuilder.BookCategoryTag, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new LearningItemInfo(item.Name, tags));
                }
            }
        }

        LearningCatalog data = LearningCatalogBuilder.Build(classes, items, new CatalogOptions
        {
            IncludeMagazines = _settings.IncludeSkillMagazines,
            IncludeBooks = _settings.IncludePerkBooks,
            IncludeOtherLoot = _settings.IncludeOtherLoot,
            ExtraTags = _settings.ExtraLearningTags,
            ExcludedTags = _settings.ExcludedLearningTags,
            IsKnownTag = IsKnownTag
        });
        _warnings?.AddRange(data.Warnings);

        var mergeable = new Dictionary<ProgressionClass, MergeableClass>(ReferenceComparer.Instance);
        foreach (MergeablePerk perk in data.MergeablePerks)
        {
            if (!Progression.ProgressionClasses.TryGetValue(perk.Name, out ProgressionClass cls) || cls == null)
            {
                continue;
            }
            mergeable[cls] = new MergeableClass
            {
                Class = cls,
                Name = cls.Name,
                ProgressionId = Progression.CalcId(cls.Name),
                MergedTagUnion = ParseKnown(perk.MergedTags),
                RequirementProgressions = requirementNames.TryGetValue(cls.Name, out string[] names) ? names : Array.Empty<string>()
            };
        }

        var catalog = new GameCatalog(data, mergeable);
        current = catalog;
        return catalog;
    }

    public static void Clear()
    {
        current = null;
    }

    /// <summary>Entry is eligible when it hits a merged tag and no unmerged LootProb tag (empty masks never match).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEligible(FastTags<TagGroup.Global> _tags)
    {
        if (_tags.IsEmpty || MergedMask.IsEmpty || !_tags.Test_AnySet(MergedMask))
        {
            return false;
        }
        return UnmergedMask.IsEmpty || !_tags.Test_AnySet(UnmergedMask);
    }

    /// <summary>True for an item that is other perk-weighted loot (not learning material) while IncludeOtherLoot is on.</summary>
    public bool IsOtherLoot(ItemClass _item)
    {
        return _item != null && !OtherLootMask.IsEmpty && !_item.ItemTags.IsEmpty && _item.ItemTags.Test_AnySet(OtherLootMask)
               && LearningCategory(_item) == null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMergeable(ProgressionClass _class, out MergeableClass _info)
    {
        return mergeable.TryGetValue(_class, out _info);
    }

    /// <summary>Learning category of an item: magazine, book (books + series tag), schematic, or null.</summary>
    public static string LearningCategory(ItemClass _item)
    {
        if (_item == null || _item.ItemTags.IsEmpty)
        {
            return null;
        }
        List<string> tags = _item.ItemTags.GetTagNames();
        return ReadableXmlInjector.TryCategorize(tags, out ReadableCategory category) ? ReadableItem.CategoryCode(category) : null;
    }

    /// <summary>
    /// Compares the catalog with the V 3.3 (b18) expectation for the active mode (with or without other loot).
    /// Returns human-readable drift lines.
    /// </summary>
    public List<string> DriftFromB18()
    {
        var drift = new List<string>();
        CompareTags(drift, "learning tags", ExpectedB18LearningTags, Data.LearningTags);
        CompareTags(drift, "other loot tags", Data.IncludesOtherLoot ? ExpectedB18OtherLootTags : Array.Empty<string>(), Data.OtherLootTags);
        CompareTags(drift, "unmerged tags", Data.IncludesOtherLoot ? Array.Empty<string>() : ExpectedB18OtherLootTags, Data.UnmergedTags);
        IEnumerable<string> perks = Data.IncludesOtherLoot ? ExpectedB18MergeablePerks.Concat(ExpectedB18OtherLootPerks) : ExpectedB18MergeablePerks;
        var expectedPerks = new HashSet<string>(perks, StringComparer.OrdinalIgnoreCase);
        var actualPerks = new HashSet<string>(Data.MergeablePerks.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var missingPerks = expectedPerks.Where(p => !actualPerks.Contains(p)).OrderBy(p => p).ToList();
        var extraPerks = actualPerks.Where(p => !expectedPerks.Contains(p)).OrderBy(p => p).ToList();
        if (missingPerks.Count > 0)
        {
            drift.Add("mergeable perks missing: " + string.Join(",", missingPerks));
        }
        if (extraPerks.Count > 0)
        {
            drift.Add("mergeable perks added: " + string.Join(",", extraPerks));
        }
        return drift;
    }

    /// <summary>
    /// Short summary for logs and the status command, e.g. "42 learning tags, 32 unmerged, 29 perks" in V 3.3, or
    /// "42 learning + 32 other loot tags, 0 unmerged, 30 perks" with IncludeOtherLoot.
    /// </summary>
    public string Describe()
    {
        return $"{Data.LearningTags.Count} learning" + (Data.IncludesOtherLoot ? $" + {Data.OtherLootTags.Count} other loot" : string.Empty) +
               $" tags, {Data.UnmergedTags.Count} unmerged, {Mergeable.Count} perks";
    }

    private static void CompareTags(List<string> _drift, string _label, IEnumerable<string> _expected, IEnumerable<string> _actual)
    {
        var expected = new HashSet<string>(_expected, StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(_actual, StringComparer.OrdinalIgnoreCase);
        List<string> missing = expected.Where(t => !actual.Contains(t)).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        List<string> added = actual.Where(t => !expected.Contains(t)).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            _drift.Add($"{_label} missing: " + string.Join(",", missing));
        }
        if (added.Count > 0)
        {
            _drift.Add($"{_label} added: " + string.Join(",", added));
        }
    }

    /// <summary>Scans every loot group and container for eligible and mixed (merged + unmerged tags) entries.</summary>
    public LootScanResult ScanLootTables()
    {
        var result = new LootScanResult();
        void Visit(LootContainer.LootEntry _entry, string _owner)
        {
            if (_entry == null)
            {
                return;
            }
            result.Entries++;
            FastTags<TagGroup.Global> tags = EntryTags(_entry);
            if (tags.IsEmpty)
            {
                return;
            }
            bool merged = !MergedMask.IsEmpty && tags.Test_AnySet(MergedMask);
            bool unmerged = !UnmergedMask.IsEmpty && tags.Test_AnySet(UnmergedMask);
            if (merged && unmerged)
            {
                result.Mixed++;
                if (result.MixedExamples.Count < 10)
                {
                    result.MixedExamples.Add(_owner + "/" + EntryName(_entry));
                }
            }
            else if (merged)
            {
                result.Eligible++;
                if (_entry.group != null)
                {
                    result.EligibleGroupRefs++;
                }
            }
        }

        foreach (KeyValuePair<string, LootContainer.LootGroup> group in LootContainer.lootGroups)
        {
            result.Groups++;
            foreach (LootContainer.LootEntry entry in group.Value.items)
            {
                Visit(entry, group.Key);
            }
        }
        foreach (KeyValuePair<string, LootContainer> container in LootContainer.lootContainers)
        {
            result.Containers++;
            foreach (LootContainer.LootEntry entry in container.Value.itemsToSpawn)
            {
                Visit(entry, container.Key);
            }
        }
        return result;
    }

    /// <summary>The tags vanilla getProbability evaluates: the item's tags for item entries, else the entry tags.</summary>
    public static FastTags<TagGroup.Global> EntryTags(LootContainer.LootEntry _entry)
    {
        ItemClass itemClass = _entry.item?.itemValue?.ItemClass;
        if (itemClass != null && !itemClass.ItemTags.IsEmpty)
        {
            return itemClass.ItemTags;
        }
        return _entry.tags;
    }

    public static string EntryName(LootContainer.LootEntry _entry)
    {
        if (_entry.group != null)
        {
            return "group:" + _entry.group.name;
        }
        return _entry.item?.itemValue?.ItemClass?.Name ?? "?";
    }

    /// <summary>True when the tag name is already registered; never registers new names.</summary>
    public static bool IsKnownTag(string _name)
    {
        return !string.IsNullOrWhiteSpace(_name) && FastTags<TagGroup.Global>.tags.ContainsKey(_name.Trim());
    }

    private static FastTags<TagGroup.Global> ParseKnown(IEnumerable<string> _names)
    {
        List<string> known = _names.Where(IsKnownTag).Select(n => n.Trim()).ToList();
        return known.Count == 0 ? FastTags<TagGroup.Global>.none : FastTags<TagGroup.Global>.Parse(string.Join(",", known));
    }

    private static void CollectRequirements(RequirementGroup _group, List<string> _types, HashSet<string> _progressions)
    {
        if (_group == null)
        {
            return;
        }
        if (_group.reqs != null)
        {
            foreach (IRequirement requirement in _group.reqs)
            {
                if (requirement == null)
                {
                    continue;
                }
                _types.Add(requirement.GetType().Name);
                if (requirement is ProgressionLevel level && !string.IsNullOrEmpty(level.progressionName))
                {
                    _progressions.Add(level.progressionName);
                }
            }
        }
        if (_group.groups != null)
        {
            foreach (RequirementGroup nested in _group.groups)
            {
                CollectRequirements(nested, _types, _progressions);
            }
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<ProgressionClass>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public bool Equals(ProgressionClass _a, ProgressionClass _b) => ReferenceEquals(_a, _b);
        public int GetHashCode(ProgressionClass _obj) => RuntimeHelpers.GetHashCode(_obj);
    }
}

internal sealed class LootScanResult
{
    public int Groups;
    public int Containers;
    public int Entries;
    public int Eligible;
    public int EligibleGroupRefs;
    public int Mixed;
    public readonly List<string> MixedExamples = new List<string>();
}
