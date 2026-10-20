using System;
using System.Collections.Generic;
using System.Linq;

namespace PartyLearningLoot.Core;

/// <summary>One LootProb passive effect of a progression class, reduced to what the catalog needs.</summary>
public sealed class LootProbEffectInfo
{
    public IReadOnlyList<string> Tags { get; }
    public bool Inverted { get; }
    public bool MatchAll { get; }
    public bool UsesCVar { get; }
    public string Operation { get; }
    public IReadOnlyList<string> RequirementTypes { get; }

    public LootProbEffectInfo(IEnumerable<string> _tags, bool _inverted = false, bool _matchAll = false, bool _usesCVar = false,
        string _operation = "perc_add", IEnumerable<string> _requirementTypes = null)
    {
        Tags = (_tags ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        Inverted = _inverted;
        MatchAll = _matchAll;
        UsesCVar = _usesCVar;
        Operation = _operation ?? string.Empty;
        RequirementTypes = (_requirementTypes ?? Enumerable.Empty<string>()).ToList();
    }

    /// <summary>A concrete effect only applies to entries that carry one of its tags.</summary>
    public bool IsConcrete => Tags.Count > 0 && !Inverted && !UsesCVar;
}

/// <summary>All LootProb effects of one progression class (perk, skill, book, ...).</summary>
public sealed class ProgressionLootInfo
{
    public string Name { get; }
    public IReadOnlyList<LootProbEffectInfo> Effects { get; }

    public ProgressionLootInfo(string _name, IEnumerable<LootProbEffectInfo> _effects)
    {
        Name = _name;
        Effects = (_effects ?? Enumerable.Empty<LootProbEffectInfo>()).ToList();
    }
}

/// <summary>Effective tags of an item that carries a learning category tag (csm or books).</summary>
public sealed class LearningItemInfo
{
    public string Name { get; }
    public IReadOnlyList<string> Tags { get; }

    public LearningItemInfo(string _name, IEnumerable<string> _tags)
    {
        Name = _name;
        Tags = (_tags ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
    }
}

public sealed class CatalogOptions
{
    public bool IncludeMagazines { get; set; } = true;
    public bool IncludeBooks { get; set; } = true;

    /// <summary>
    /// Also merge all other loot a perk weights (ammo, weapons, tools, armor, medical supplies, food, seeds, ...): every
    /// LootProb tag that is not learning material becomes an "other loot" tag.
    /// </summary>
    public bool IncludeOtherLoot { get; set; }
    public IReadOnlyList<string> ExtraTags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedTags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Checks whether a tag name already exists in the game. Unknown names are never parsed into tags,
    /// because parsing registers new tag bits globally. Null accepts every name (when no game is loaded).
    /// </summary>
    public Func<string, bool> IsKnownTag { get; set; }
}

public sealed class MergeablePerk
{
    public string Name { get; }

    /// <summary>The merged tags this perk can raise (learning material, other loot if enabled); a cheap pre-filter.</summary>
    public IReadOnlyCollection<string> MergedTags { get; }

    /// <summary>Requirement types other than ProgressionLevel found on its LootProb effects.</summary>
    public IReadOnlyCollection<string> UnusualRequirementTypes { get; }

    public MergeablePerk(string _name, IEnumerable<string> _mergedTags, IEnumerable<string> _unusualRequirements)
    {
        Name = _name;
        MergedTags = new SortedSet<string>(_mergedTags, StringComparer.OrdinalIgnoreCase);
        UnusualRequirementTypes = new SortedSet<string>(_unusualRequirements, StringComparer.Ordinal);
    }
}

/// <summary>Result of <see cref="LearningCatalogBuilder.Build"/>; all tag sets are case-insensitive.</summary>
public sealed class LearningCatalog
{
    /// <summary>Every tag a positive LootProb effect of a progression class targets.</summary>
    public IReadOnlyCollection<string> AllLootProbTags { get; internal set; }

    /// <summary>Learning material: the tags of skill magazines and perk book series (plus extra, minus excluded tags).</summary>
    public IReadOnlyCollection<string> LearningTags { get; internal set; }

    /// <summary>All other LootProb tags, merged only with <see cref="CatalogOptions.IncludeOtherLoot"/> (empty otherwise).</summary>
    public IReadOnlyCollection<string> OtherLootTags { get; internal set; }

    /// <summary>Every merged tag: learning material plus other loot.</summary>
    public IReadOnlyCollection<string> MergedTags { get; internal set; }

    /// <summary>LootProb tags that are never merged; an entry that carries one stays vanilla.</summary>
    public IReadOnlyCollection<string> UnmergedTags { get; internal set; }

    public IReadOnlyCollection<string> MagazineTags { get; internal set; }
    public IReadOnlyCollection<string> BookTags { get; internal set; }
    public bool IncludesOtherLoot { get; internal set; }
    public IReadOnlyList<MergeablePerk> MergeablePerks { get; internal set; }
    public IReadOnlyList<KeyValuePair<string, string>> RejectedPerks { get; internal set; }
    public IReadOnlyList<string> Warnings { get; internal set; }

    /// <summary>
    /// An entry is eligible when its tags hit at least one merged tag and no unmerged LootProb tag.
    /// Empty merged sets never match (guards the "empty mask matches empty tags" quirk of FastTags).
    /// </summary>
    public bool IsEligible(IEnumerable<string> _entryTags)
    {
        if (_entryTags == null || MergedTags.Count == 0)
        {
            return false;
        }
        bool hitsMerged = false;
        foreach (string tag in _entryTags)
        {
            if (UnmergedTags.Contains(tag))
            {
                return false;
            }
            hitsMerged |= MergedTags.Contains(tag);
        }
        return hitsMerged;
    }

    public bool IsMergeable(string _perkName)
    {
        return MergeablePerks.Any(p => string.Equals(p.Name, _perkName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Derives the merge catalog from game data:
/// <list type="bullet">
/// <item>learning tags = (tags targeted by LootProb effects) ∩ (tags of csm/books items) + extra;</item>
/// <item>other loot tags = every remaining LootProb tag, only with <see cref="CatalogOptions.IncludeOtherLoot"/>;</item>
/// <item>excluded tags are removed from both; whatever is left of the LootProb tags is never merged.</item>
/// </list>
/// A perk is mergeable when it raises at least one merged tag and every LootProb effect it has is concrete
/// (specific tags, not inverted, no @cvar value).
/// </summary>
public static class LearningCatalogBuilder
{
    public const string MagazineCategoryTag = "csm";
    public const string BookCategoryTag = "books";
    public const string AllowedRequirementType = "ProgressionLevel";

    public static LearningCatalog Build(IEnumerable<ProgressionLootInfo> _classes, IEnumerable<LearningItemInfo> _items, CatalogOptions _options)
    {
        _options ??= new CatalogOptions();
        var classes = (_classes ?? Enumerable.Empty<ProgressionLootInfo>()).Where(c => c != null && c.Effects.Count > 0).ToList();
        var items = (_items ?? Enumerable.Empty<LearningItemInfo>()).Where(i => i != null).ToList();
        var warnings = new List<string>();
        StringComparer cmp = StringComparer.OrdinalIgnoreCase;

        // 1. Every tag that a positive (non-inverted) LootProb effect targets.
        var allLootProb = new SortedSet<string>(cmp);
        foreach (ProgressionLootInfo cls in classes)
        {
            foreach (LootProbEffectInfo effect in cls.Effects.Where(e => !e.Inverted))
            {
                allLootProb.UnionWith(effect.Tags);
            }
        }

        // 2. Tags carried by learning items (category tags removed), per category.
        var magazineItemTags = new HashSet<string>(cmp);
        var bookItemTags = new HashSet<string>(cmp);
        foreach (LearningItemInfo item in items)
        {
            if (item.Tags.Contains(MagazineCategoryTag, cmp))
            {
                magazineItemTags.UnionWith(item.Tags);
            }
            if (item.Tags.Contains(BookCategoryTag, cmp))
            {
                bookItemTags.UnionWith(item.Tags);
            }
        }
        foreach (HashSet<string> set in new[] { magazineItemTags, bookItemTags })
        {
            set.Remove(MagazineCategoryTag);
            set.Remove(BookCategoryTag);
        }

        // 3. Learning tags of the enabled categories. All learning material is remembered as well: a disabled category
        //    must never come back through "other loot".
        var allMagazineTags = new SortedSet<string>(allLootProb.Where(magazineItemTags.Contains), cmp);
        var allBookTags = new SortedSet<string>(allLootProb.Where(bookItemTags.Contains), cmp);
        var magazineTags = new SortedSet<string>(_options.IncludeMagazines ? allMagazineTags : Enumerable.Empty<string>(), cmp);
        var bookTags = new SortedSet<string>(_options.IncludeBooks ? allBookTags : Enumerable.Empty<string>(), cmp);
        var learning = new SortedSet<string>(magazineTags.Concat(bookTags), cmp);
        var learningMaterial = new HashSet<string>(allMagazineTags.Concat(allBookTags), cmp);

        foreach (string extra in _options.ExtraTags ?? Array.Empty<string>())
        {
            if (_options.IsKnownTag != null && !_options.IsKnownTag(extra))
            {
                warnings.Add($"ExtraLearningTags: '{extra}' is not a known tag and was ignored.");
                continue;
            }
            if (!allLootProb.Contains(extra))
            {
                warnings.Add($"ExtraLearningTags: '{extra}' is not targeted by any LootProb effect, so it changes nothing.");
            }
            learning.Add(extra);
        }

        // 4. Other loot: with the option every LootProb tag that is no learning material (ammo, weapons, tools, armor,
        //    medical supplies, food, seeds, ...).
        var otherLoot = new SortedSet<string>(cmp);
        if (_options.IncludeOtherLoot)
        {
            otherLoot.UnionWith(allLootProb.Where(t => !learning.Contains(t) && !learningMaterial.Contains(t)));
        }

        // 5. Excluded tags leave every merged set.
        foreach (string excluded in _options.ExcludedTags ?? Array.Empty<string>())
        {
            bool removedLearning = learning.Remove(excluded);
            bool removedOther = otherLoot.Remove(excluded);
            if (!removedLearning && !removedOther)
            {
                warnings.Add($"ExcludedLearningTags: '{excluded}' is not a merged tag and was ignored.");
                continue;
            }
            magazineTags.Remove(excluded);
            bookTags.Remove(excluded);
        }

        // 6. Every LootProb tag that is not merged stays vanilla; entries carrying one are never merged.
        var merged = new SortedSet<string>(learning.Concat(otherLoot), cmp);
        var unmerged = new SortedSet<string>(allLootProb.Where(t => !merged.Contains(t)), cmp);

        // 7. Mergeable perks.
        var mergeable = new List<MergeablePerk>();
        var rejected = new List<KeyValuePair<string, string>>();
        foreach (ProgressionLootInfo cls in classes)
        {
            var mergedHits = new SortedSet<string>(cls.Effects.Where(e => !e.Inverted).SelectMany(e => e.Tags).Where(merged.Contains), cmp);
            if (mergedHits.Count == 0)
            {
                continue;
            }
            string reason = RejectReason(cls);
            if (reason != null)
            {
                rejected.Add(new KeyValuePair<string, string>(cls.Name, reason));
                warnings.Add($"Perk '{cls.Name}' raises merged tags but is not mergeable: {reason}.");
                continue;
            }
            var unusual = cls.Effects.SelectMany(e => e.RequirementTypes).Where(r => !string.Equals(r, AllowedRequirementType, StringComparison.Ordinal)).Distinct().ToList();
            if (unusual.Count > 0)
            {
                warnings.Add($"Perk '{cls.Name}' uses requirement types {string.Join(", ", unusual)} on LootProb; members are evaluated in their own context.");
            }
            mergeable.Add(new MergeablePerk(cls.Name, mergedHits, unusual));
        }

        if (merged.Count == 0)
        {
            warnings.Add("The catalog is empty: no LootProb effect targets a learning item tag (and other loot is off).");
        }

        return new LearningCatalog
        {
            AllLootProbTags = allLootProb,
            LearningTags = learning,
            OtherLootTags = otherLoot,
            MergedTags = merged,
            UnmergedTags = unmerged,
            MagazineTags = magazineTags,
            BookTags = bookTags,
            IncludesOtherLoot = _options.IncludeOtherLoot,
            MergeablePerks = mergeable,
            RejectedPerks = rejected,
            Warnings = warnings
        };
    }

    private static string RejectReason(ProgressionLootInfo _cls)
    {
        foreach (LootProbEffectInfo effect in _cls.Effects)
        {
            if (effect.Tags.Count == 0)
            {
                return "a LootProb effect without tags applies to every entry";
            }
            if (effect.Inverted)
            {
                return "a LootProb effect uses invert_tag_check";
            }
            if (effect.UsesCVar)
            {
                return "a LootProb effect reads an @cvar value";
            }
        }
        return null;
    }
}
