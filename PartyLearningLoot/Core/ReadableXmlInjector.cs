using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace PartyLearningLoot.Core;

public enum ReadableCategory
{
    Magazine,
    Book,
    Schematic
}

/// <summary>A readable learning item (Eat action + csm/books tag + own primary-action-end effects).</summary>
public sealed class ReadableItem
{
    public int Id { get; internal set; }
    public string Name { get; }
    public ReadableCategory Category { get; }
    public IReadOnlyList<string> Tags { get; }

    public ReadableItem(string _name, ReadableCategory _category, IEnumerable<string> _tags, int _id = 0)
    {
        Name = _name;
        Category = _category;
        Tags = (_tags ?? Enumerable.Empty<string>()).ToList();
        Id = _id;
    }

    public static string CategoryCode(ReadableCategory _category)
    {
        switch (_category)
        {
            case ReadableCategory.Magazine: return "magazine";
            case ReadableCategory.Book: return "book";
            default: return "schematic";
        }
    }
}

public sealed class InjectionOptions
{
    public bool Books { get; set; } = true;
    public bool Magazines { get; set; } = true;
    public bool Schematics { get; set; } = true;
    public string MarkerBuffName { get; set; } = ReadableXmlInjector.DefaultMarkerBuff;
    public string ApplyBuffPrefix { get; set; } = ReadableXmlInjector.DefaultApplyBuffPrefix;

    public bool Allows(ReadableCategory _category)
    {
        switch (_category)
        {
            case ReadableCategory.Magazine: return Magazines;
            case ReadableCategory.Book: return Books;
            default: return Schematics;
        }
    }
}

public sealed class InjectionResult
{
    public IReadOnlyList<ReadableItem> Injected { get; internal set; } = Array.Empty<ReadableItem>();
    public IReadOnlyDictionary<int, ReadableItem> ById { get; internal set; } = new Dictionary<int, ReadableItem>();
    public int ReadableFound { get; internal set; }
    public int SkippedByCategory { get; internal set; }
    public int SkippedWithoutEffects { get; internal set; }
    public int ReplacedExistingMarkers { get; internal set; }
    public string TableHash { get; internal set; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; internal set; } = Array.Empty<string>();

    /// <summary>Apply buffs (for buffs.xml) of the injected items that have learning effects.</summary>
    public IReadOnlyList<XElement> ApplyBuffs { get; internal set; } = Array.Empty<XElement>();

    /// <summary>Item id -> apply buff name (items without learning effects, e.g. XP only, have none).</summary>
    public IReadOnlyDictionary<int, string> ApplyBuffById { get; internal set; } = new Dictionary<int, string>();

    public int CountOf(ReadableCategory _category) => Injected.Count(i => i.Category == _category);
}

/// <summary>
/// Shared Reading XML: adds a tiny marker effect group to every readable learning item, defines the hidden marker buff
/// and builds one hidden "apply" buff per item.
///
/// When a client reads an item, its own (vanilla) effect evaluation adds the marker buff and reports it to the server
/// through the regular buff network package. The buff duration encodes the item id (id / 1000 seconds), so the server
/// learns exactly which item was read without any client-side code.
///
/// The apply buff carries copies of the item's learning effect groups (requirements included, triggers moved to
/// onSelfBuffStart and onSelfBuffStack, only Set/AddProgressionLevel and Modify/RemoveCVar). The server adds it to a
/// member's own client, which then applies the read relative to its own authoritative state - exactly like reading it.
/// All operations are idempotent.
/// </summary>
public static class ReadableXmlInjector
{
    public const string DefaultMarkerBuff = "pllSharedReadMarker";
    public const string DefaultApplyBuffPrefix = "pllShareApply_";
    public const string PrimaryActionEnd = "onSelfPrimaryActionEnd";
    public const string BuffStart = "onSelfBuffStart";
    public const string BuffStack = "onSelfBuffStack";
    public const string ApplyBuffDuration = "1";
    public const int MaxItems = 60000;

    /// <summary>Actions that carry a learning effect; everything else (XP, sounds, buffs, ...) stays out of apply buffs.</summary>
    public static readonly string[] ApplyActions = { "SetProgressionLevel", "AddProgressionLevel", "ModifyCVar", "RemoveCVar" };

    private const string ExtendsProperty = "Extends";
    private const string TagsProperty = "Tags";
    private const string CreativeModeProperty = "CreativeMode";
    private const string PrimaryActionClass = "Action0";

    // ----- analysis -------------------------------------------------------------------------

    /// <summary>
    /// Finds all readable learning items in document order (before category filtering).
    /// Effective properties follow the vanilla rules: Extends copies the parent's properties except CreativeMode and
    /// the names listed in param1 ("Action0.Class" style entries exclude nested class properties); effect groups are
    /// never inherited.
    /// </summary>
    public static List<ReadableItem> FindReadableItems(XDocument _items, string _markerBuffName, out int _skippedWithoutEffects, ICollection<string> _warnings)
    {
        _skippedWithoutEffects = 0;
        var result = new List<ReadableItem>();
        XElement root = _items?.Root;
        if (root == null)
        {
            _warnings?.Add("items.xml has no root element.");
            return result;
        }

        var raw = new Dictionary<string, RawItem>(StringComparer.Ordinal);
        var order = new List<RawItem>();
        foreach (XElement element in root.Elements("item"))
        {
            string name = (string)element.Attribute("name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            if (raw.ContainsKey(name))
            {
                _warnings?.Add($"items.xml defines '{name}' more than once; the first definition is used for Shared Reading.");
                continue;
            }
            var item = RawItem.FromElement(name, element);
            raw.Add(name, item);
            order.Add(item);
        }

        var resolver = new Resolver(raw, _warnings);
        foreach (RawItem item in order)
        {
            EffectiveProps props = resolver.Resolve(item.Name);
            if (props == null || !props.IsEatAction)
            {
                continue;
            }
            List<string> tags = props.Tags;
            if (!TryCategorize(tags, out ReadableCategory category))
            {
                continue;
            }
            if (!HasOwnPrimaryEndEffects(item.Element, _markerBuffName))
            {
                _skippedWithoutEffects++;
                continue;
            }
            result.Add(new ReadableItem(item.Name, category, tags));
        }
        return result;
    }

    /// <summary>csm = magazine; books plus any further tag (series) = book; books alone = schematic.</summary>
    public static bool TryCategorize(IReadOnlyCollection<string> _tags, out ReadableCategory _category)
    {
        _category = ReadableCategory.Schematic;
        if (_tags == null || _tags.Count == 0)
        {
            return false;
        }
        StringComparer cmp = StringComparer.OrdinalIgnoreCase;
        if (_tags.Contains(LearningCatalogBuilder.MagazineCategoryTag, cmp))
        {
            _category = ReadableCategory.Magazine;
            return true;
        }
        if (_tags.Contains(LearningCatalogBuilder.BookCategoryTag, cmp))
        {
            bool hasSeriesTag = _tags.Any(t => !cmp.Equals(t, LearningCatalogBuilder.BookCategoryTag));
            _category = hasSeriesTag ? ReadableCategory.Book : ReadableCategory.Schematic;
            return true;
        }
        return false;
    }

    // ----- injection ------------------------------------------------------------------------

    /// <summary>Injects the marker effect group into every readable item of an enabled category. Ids start at 1.</summary>
    public static InjectionResult InjectItems(XDocument _items, InjectionOptions _options)
    {
        _options ??= new InjectionOptions();
        var warnings = new List<string>();
        List<ReadableItem> readable = FindReadableItems(_items, _options.MarkerBuffName, out int skippedNoEffects, warnings);
        var injected = new List<ReadableItem>();
        var byId = new Dictionary<int, ReadableItem>();
        int skippedCategory = 0;
        int replaced = 0;

        Dictionary<string, XElement> elements = _items.Root.Elements("item")
            .Where(e => !string.IsNullOrEmpty((string)e.Attribute("name")))
            .GroupBy(e => (string)e.Attribute("name"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var applyBuffs = new List<XElement>();
        var applyById = new Dictionary<int, string>();
        var applyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ReadableItem item in readable)
        {
            XElement element = elements[item.Name];
            replaced += RemoveMarkerGroups(element, _options.MarkerBuffName);
            if (!_options.Allows(item.Category))
            {
                skippedCategory++;
                continue;
            }
            if (injected.Count >= MaxItems)
            {
                warnings.Add($"More than {MaxItems} readable items; the rest are not shared.");
                break;
            }
            item.Id = injected.Count + 1;
            string applyName = ApplyBuffName(_options.ApplyBuffPrefix, item);
            if (!applyNames.Add(applyName))
            {
                // Buff names are case-insensitive; items that differ only in case fall back to their id.
                applyName = _options.ApplyBuffPrefix + IdSuffix(item.Id);
                applyNames.Add(applyName);
            }
            XElement applyBuff = CreateApplyBuff(element, applyName);
            element.Add(CreateMarkerGroup(_options.MarkerBuffName, item.Id));
            injected.Add(item);
            byId.Add(item.Id, item);
            if (applyBuff != null)
            {
                applyBuffs.Add(applyBuff);
                applyById.Add(item.Id, (string)applyBuff.Attribute("name"));
            }
        }

        return new InjectionResult
        {
            Injected = injected,
            ById = byId,
            ReadableFound = readable.Count,
            SkippedByCategory = skippedCategory,
            SkippedWithoutEffects = skippedNoEffects,
            ReplacedExistingMarkers = replaced,
            TableHash = ComputeTableHash(injected),
            Warnings = warnings,
            ApplyBuffs = applyBuffs,
            ApplyBuffById = applyById
        };
    }

    /// <summary>
    /// Apply buff name of an item: prefix + item name. Stable across item table changes, so an apply buff that a player
    /// file saved (e.g. received while dead) can never map to another item after a restart. Names with characters
    /// outside [A-Za-z0-9_.-] fall back to the item id.
    /// </summary>
    public static string ApplyBuffName(string _prefix, ReadableItem _item)
    {
        string name = _item.Name ?? string.Empty;
        bool safe = name.Length > 0 && name.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-');
        return (_prefix ?? DefaultApplyBuffPrefix) + (safe ? name : IdSuffix(_item.Id));
    }

    private static string IdSuffix(int _id) => "id" + _id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the apply buff for one item from its own primary-action-end effect groups (the marker group excluded).
    /// Returns null when the item has no learning action (e.g. only XP).
    /// </summary>
    public static XElement CreateApplyBuff(XElement _item, string _buffName)
    {
        var groups = new List<XElement>();
        foreach (XElement group in _item.Elements("effect_group"))
        {
            List<XElement> actions = group.Elements("triggered_effect")
                .Where(t => string.Equals((string)t.Attribute("trigger"), PrimaryActionEnd, StringComparison.OrdinalIgnoreCase)
                            && ApplyActions.Contains((string)t.Attribute("action"), StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (actions.Count == 0)
            {
                continue;
            }
            var copy = new XElement("effect_group", group.Attributes());
            foreach (XElement requirement in group.Elements().Where(e => e.Name == "requirement" || e.Name == "requirement_group"))
            {
                copy.Add(new XElement(requirement));
            }
            // Both triggers: a second apply of the same item while the buff is still active fires onSelfBuffStack.
            foreach (string trigger in new[] { BuffStart, BuffStack })
            {
                foreach (XElement action in actions)
                {
                    var effect = new XElement(action);
                    effect.SetAttributeValue("trigger", trigger);
                    copy.Add(effect);
                }
            }
            groups.Add(copy);
        }
        if (groups.Count == 0)
        {
            return null;
        }
        return new XElement("buff",
            new XAttribute("name", _buffName),
            new XAttribute("hidden", "true"),
            new XAttribute("showonhud", "false"),
            new XAttribute("remove_on_death", "false"),
            new XElement("stack_type", new XAttribute("value", "replace")),
            new XElement("duration", new XAttribute("value", ApplyBuffDuration)),
            groups);
    }

    /// <summary>
    /// The server-side stand-in for an apply buff: same name and buff settings, no effect groups. The server never applies
    /// a read itself, but it reads apply buffs back from the clients' buff syncs and player data.
    /// </summary>
    public static XElement CreatePlaceholderBuff(XElement _applyBuff)
    {
        return new XElement(_applyBuff.Name, _applyBuff.Attributes(), _applyBuff.Elements().Where(e => e.Name != "effect_group").Select(e => new XElement(e)));
    }

    /// <summary>Replaces all apply buffs (name prefix) in buffs.xml with the given ones. Returns the number added.</summary>
    public static int InjectApplyBuffs(XDocument _buffs, IEnumerable<XElement> _applyBuffs, string _prefix = DefaultApplyBuffPrefix)
    {
        XElement root = _buffs?.Root ?? throw new ArgumentException("buffs.xml has no root element.");
        foreach (XElement old in root.Elements("buff").Where(b => ((string)b.Attribute("name") ?? string.Empty).StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            old.Remove();
        }
        int added = 0;
        foreach (XElement buff in _applyBuffs ?? Enumerable.Empty<XElement>())
        {
            root.Add(new XElement(buff));
            added++;
        }
        return added;
    }

    /// <summary>Adds the hidden marker buff definition; returns false when it already exists.</summary>
    public static bool InjectMarkerBuff(XDocument _buffs, string _markerBuffName = DefaultMarkerBuff)
    {
        XElement root = _buffs?.Root ?? throw new ArgumentException("buffs.xml has no root element.");
        bool exists = root.Elements("buff").Any(b => string.Equals((string)b.Attribute("name"), _markerBuffName, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
            return false;
        }
        root.Add(new XElement("buff",
            new XAttribute("name", _markerBuffName),
            new XAttribute("hidden", "true"),
            new XAttribute("showonhud", "false"),
            new XAttribute("remove_on_death", "true"),
            new XElement("stack_type", new XAttribute("value", "ignore")),
            new XElement("duration", new XAttribute("value", "0.001"))));
        return true;
    }

    public static XElement CreateMarkerGroup(string _markerBuffName, int _id)
    {
        return new XElement("effect_group",
            new XAttribute("tiered", "false"),
            new XElement("triggered_effect",
                new XAttribute("trigger", PrimaryActionEnd),
                new XAttribute("action", "AddBuff"),
                new XAttribute("buff", _markerBuffName),
                new XAttribute("duration", FormatDuration(_id))));
    }

    /// <summary>Removes the marker groups of all items (rollback when Shared Reading cannot be completed).</summary>
    public static int RemoveAllMarkerGroups(XDocument _items, string _markerBuffName = DefaultMarkerBuff)
    {
        return _items?.Root?.Elements("item").Sum(item => RemoveMarkerGroups(item, _markerBuffName)) ?? 0;
    }

    /// <summary>Removes effect groups that only exist to carry our marker. Returns the number removed.</summary>
    public static int RemoveMarkerGroups(XElement _item, string _markerBuffName)
    {
        List<XElement> markers = _item.Elements("effect_group")
            .Where(g => g.Elements("triggered_effect").Any(t => IsMarkerEffect(t, _markerBuffName)))
            .ToList();
        foreach (XElement marker in markers)
        {
            marker.Remove();
        }
        return markers.Count;
    }

    // ----- id encoding ----------------------------------------------------------------------

    public static string FormatDuration(int _id)
    {
        if (_id < 1 || _id > MaxItems)
        {
            throw new ArgumentOutOfRangeException(nameof(_id));
        }
        return (_id / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static bool TryDecodeDuration(float _duration, out int _id)
    {
        _id = 0;
        if (float.IsNaN(_duration) || float.IsInfinity(_duration) || _duration <= 0f)
        {
            return false;
        }
        double scaled = _duration * 1000d;
        double rounded = Math.Round(scaled);
        if (Math.Abs(scaled - rounded) > 0.01 || rounded < 1 || rounded > MaxItems)
        {
            return false;
        }
        _id = (int)rounded;
        return true;
    }

    /// <summary>FNV-1a 64 over "id:name\n" lines; identical tables yield identical hashes.</summary>
    public static string ComputeTableHash(IEnumerable<ReadableItem> _items)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (ReadableItem item in _items ?? Enumerable.Empty<ReadableItem>())
        {
            foreach (byte b in Encoding.UTF8.GetBytes(item.Id.ToString(CultureInfo.InvariantCulture) + ":" + item.Name + "\n"))
            {
                hash ^= b;
                hash *= prime;
            }
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    // ----- helpers --------------------------------------------------------------------------

    private static bool HasOwnPrimaryEndEffects(XElement _item, string _markerBuffName)
    {
        return _item.Elements("effect_group")
            .SelectMany(g => g.Elements("triggered_effect"))
            .Any(t => string.Equals((string)t.Attribute("trigger"), PrimaryActionEnd, StringComparison.OrdinalIgnoreCase)
                      && !IsMarkerEffect(t, _markerBuffName));
    }

    private static bool IsMarkerEffect(XElement _triggeredEffect, string _markerBuffName)
    {
        return string.Equals((string)_triggeredEffect.Attribute("action"), "AddBuff", StringComparison.OrdinalIgnoreCase)
               && string.Equals((string)_triggeredEffect.Attribute("buff"), _markerBuffName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RawItem
    {
        public string Name;
        public XElement Element;
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Params1 = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, string>> Classes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        public static RawItem FromElement(string _name, XElement _element)
        {
            var item = new RawItem { Name = _name, Element = _element };
            foreach (XElement property in _element.Elements("property"))
            {
                string className = (string)property.Attribute("class");
                if (!string.IsNullOrEmpty(className))
                {
                    if (!item.Classes.TryGetValue(className, out Dictionary<string, string> values))
                    {
                        values = new Dictionary<string, string>(StringComparer.Ordinal);
                        item.Classes.Add(className, values);
                    }
                    foreach (XElement nested in property.Elements("property"))
                    {
                        string nestedName = (string)nested.Attribute("name");
                        if (!string.IsNullOrEmpty(nestedName))
                        {
                            values[nestedName] = (string)nested.Attribute("value") ?? string.Empty;
                        }
                    }
                    continue;
                }
                string name = (string)property.Attribute("name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                item.Values[name] = (string)property.Attribute("value") ?? string.Empty;
                string param1 = (string)property.Attribute("param1");
                if (param1 != null)
                {
                    item.Params1[name] = param1;
                }
            }
            return item;
        }
    }

    private sealed class EffectiveProps
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, string>> Classes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        public bool IsEatAction =>
            Classes.TryGetValue(PrimaryActionClass, out Dictionary<string, string> action)
            && action.TryGetValue("Class", out string cls)
            && string.Equals(cls?.Trim(), "Eat", StringComparison.OrdinalIgnoreCase);

        public List<string> Tags =>
            Values.TryGetValue(TagsProperty, out string tags)
                ? tags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
                : new List<string>();
    }

    private sealed class Resolver
    {
        private readonly Dictionary<string, RawItem> raw;
        private readonly Dictionary<string, EffectiveProps> resolved = new Dictionary<string, EffectiveProps>(StringComparer.Ordinal);
        private readonly HashSet<string> inProgress = new HashSet<string>(StringComparer.Ordinal);
        private readonly ICollection<string> warnings;

        public Resolver(Dictionary<string, RawItem> _raw, ICollection<string> _warnings)
        {
            raw = _raw;
            warnings = _warnings;
        }

        public EffectiveProps Resolve(string _name)
        {
            if (resolved.TryGetValue(_name, out EffectiveProps cached))
            {
                return cached;
            }
            if (!raw.TryGetValue(_name, out RawItem item))
            {
                return null;
            }
            if (!inProgress.Add(_name))
            {
                warnings?.Add($"items.xml: Extends cycle detected at '{_name}'.");
                return null;
            }

            var props = new EffectiveProps();
            if (item.Values.TryGetValue(ExtendsProperty, out string parentName) && !string.IsNullOrEmpty(parentName))
            {
                EffectiveProps parent = Resolve(parentName);
                if (parent == null)
                {
                    warnings?.Add($"items.xml: '{_name}' extends '{parentName}', which could not be resolved.");
                }
                else
                {
                    var exclude = new HashSet<string>(StringComparer.Ordinal) { CreativeModeProperty };
                    if (item.Params1.TryGetValue(ExtendsProperty, out string excluded))
                    {
                        foreach (string entry in excluded.Split(','))
                        {
                            exclude.Add(entry.Trim());
                        }
                    }
                    foreach (KeyValuePair<string, string> value in parent.Values)
                    {
                        if (!exclude.Contains(value.Key))
                        {
                            props.Values[value.Key] = value.Value;
                        }
                    }
                    foreach (KeyValuePair<string, Dictionary<string, string>> cls in parent.Classes)
                    {
                        if (exclude.Contains(cls.Key))
                        {
                            continue;
                        }
                        string prefix = cls.Key + ".";
                        var nestedExclude = new HashSet<string>(exclude.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).Select(x => x.Substring(prefix.Length)), StringComparer.Ordinal);
                        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, string> nested in cls.Value)
                        {
                            if (!nestedExclude.Contains(nested.Key))
                            {
                                copy[nested.Key] = nested.Value;
                            }
                        }
                        props.Classes[cls.Key] = copy;
                    }
                }
            }

            foreach (KeyValuePair<string, string> value in item.Values)
            {
                props.Values[value.Key] = value.Value;
            }
            foreach (KeyValuePair<string, Dictionary<string, string>> cls in item.Classes)
            {
                if (!props.Classes.TryGetValue(cls.Key, out Dictionary<string, string> target))
                {
                    target = new Dictionary<string, string>(StringComparer.Ordinal);
                    props.Classes.Add(cls.Key, target);
                }
                foreach (KeyValuePair<string, string> nested in cls.Value)
                {
                    target[nested.Key] = nested.Value;
                }
            }

            inProgress.Remove(_name);
            resolved[_name] = props;
            return props;
        }
    }
}
