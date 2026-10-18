using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PartyLearningLoot.Core;

/// <summary>Verbosity of the JSON-lines audit log.</summary>
public enum AuditLevel
{
    Off = 0,
    Summary = 1,
    Detailed = 2
}

/// <summary>
/// Immutable mod configuration. Every setting is described once in <see cref="Descriptors"/>;
/// parsing, defaults, validation, logging and restart detection are all driven by that table.
/// </summary>
public sealed class ModSettings
{
    public const int MaxMessageLength = 200;

    private static readonly Regex TagNamePattern = new Regex("^[A-Za-z0-9_\\-\\.]+$", RegexOptions.Compiled);

    public bool Enabled { get; private set; } = true;
    public bool DryRun { get; private set; }
    public bool ApplyToContainers { get; private set; } = true;
    public bool ApplyToLootBags { get; private set; } = true;
    public bool IncludeSkillMagazines { get; private set; } = true;
    public bool IncludePerkBooks { get; private set; } = true;

    /// <summary>Also merge the perk bonuses for all other loot (ammo, weapons, tools, armor, medical, food, seeds, ...).</summary>
    public bool IncludeOtherLoot { get; private set; }
    public float MaxMemberDistance { get; private set; }
    public bool IncludeDeadMembers { get; private set; } = true;
    public bool IncludeSpectators { get; private set; }
    public IReadOnlyList<string> ExtraLearningTags { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedLearningTags { get; private set; } = Array.Empty<string>();
    public bool SharedReading { get; private set; }
    public bool SharedReadingBooks { get; private set; } = true;
    public bool SharedReadingMagazines { get; private set; } = true;
    public bool SharedReadingSchematics { get; private set; } = true;
    public bool SharedReadingShareXp { get; private set; } = true;
    public float SharedReadingMaxDistance { get; private set; }
    public bool SharedReadingNotify { get; private set; } = true;
    public string SharedReadingMessage { get; private set; } = "Learned with {reader}: {item}";
    public AuditLevel LogLevel { get; private set; } = AuditLevel.Summary;

    public static ModSettings Default { get; } = new ModSettings();

    private sealed class Descriptor
    {
        public readonly string Name;
        public readonly bool RequiresRestart;
        public readonly Func<ModSettings, string, string> Apply;   // returns an error text or null
        public readonly Func<ModSettings, string> Format;

        public Descriptor(string _name, bool _requiresRestart, Func<ModSettings, string, string> _apply, Func<ModSettings, string> _format)
        {
            Name = _name;
            RequiresRestart = _requiresRestart;
            Apply = _apply;
            Format = _format;
        }
    }

    private static readonly Descriptor[] Descriptors =
    {
        Bool("Enabled", false, (s, v) => s.Enabled = v, s => s.Enabled),
        Bool("DryRun", false, (s, v) => s.DryRun = v, s => s.DryRun),
        Bool("ApplyToContainers", false, (s, v) => s.ApplyToContainers = v, s => s.ApplyToContainers),
        Bool("ApplyToLootBags", false, (s, v) => s.ApplyToLootBags = v, s => s.ApplyToLootBags),
        Bool("IncludeSkillMagazines", false, (s, v) => s.IncludeSkillMagazines = v, s => s.IncludeSkillMagazines),
        Bool("IncludePerkBooks", false, (s, v) => s.IncludePerkBooks = v, s => s.IncludePerkBooks),
        Bool("IncludeOtherLoot", false, (s, v) => s.IncludeOtherLoot = v, s => s.IncludeOtherLoot),
        Distance("MaxMemberDistance", false, (s, v) => s.MaxMemberDistance = v, s => s.MaxMemberDistance),
        Bool("IncludeDeadMembers", false, (s, v) => s.IncludeDeadMembers = v, s => s.IncludeDeadMembers),
        Bool("IncludeSpectators", false, (s, v) => s.IncludeSpectators = v, s => s.IncludeSpectators),
        TagList("ExtraLearningTags", (s, v) => s.ExtraLearningTags = v, s => s.ExtraLearningTags),
        TagList("ExcludedLearningTags", (s, v) => s.ExcludedLearningTags = v, s => s.ExcludedLearningTags),
        Bool("SharedReading", true, (s, v) => s.SharedReading = v, s => s.SharedReading),
        Bool("SharedReadingBooks", true, (s, v) => s.SharedReadingBooks = v, s => s.SharedReadingBooks),
        Bool("SharedReadingMagazines", true, (s, v) => s.SharedReadingMagazines = v, s => s.SharedReadingMagazines),
        Bool("SharedReadingSchematics", true, (s, v) => s.SharedReadingSchematics = v, s => s.SharedReadingSchematics),
        Bool("SharedReadingShareXp", false, (s, v) => s.SharedReadingShareXp = v, s => s.SharedReadingShareXp),
        Distance("SharedReadingMaxDistance", false, (s, v) => s.SharedReadingMaxDistance = v, s => s.SharedReadingMaxDistance),
        Bool("SharedReadingNotify", false, (s, v) => s.SharedReadingNotify = v, s => s.SharedReadingNotify),
        new Descriptor("SharedReadingMessage", false, ApplyMessage, s => s.SharedReadingMessage),
        new Descriptor("LogLevel", false, ApplyLogLevel, s => s.LogLevel.ToString())
    };

    /// <summary>Names of all known settings in declaration order.</summary>
    public static IEnumerable<string> SettingNames => Descriptors.Select(d => d.Name);

    /// <summary>
    /// Parses a settings document. Unknown names, duplicates and invalid values produce a warning and keep the default.
    /// A missing or malformed document yields the defaults.
    /// </summary>
    public static ModSettings Parse(string _xml, ICollection<string> _warnings)
    {
        var settings = new ModSettings();
        if (string.IsNullOrWhiteSpace(_xml))
        {
            _warnings?.Add("Settings document is empty, using defaults.");
            return settings;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(_xml);
        }
        catch (XmlException e)
        {
            _warnings?.Add($"Settings document is not valid XML ({e.Message}), using defaults.");
            return settings;
        }

        var byName = Descriptors.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportedDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement property in doc.Descendants("property"))
        {
            string name = ((string)property.Attribute("name"))?.Trim();
            string value = (string)property.Attribute("value");
            if (string.IsNullOrEmpty(name))
            {
                _warnings?.Add("Settings: <property> without name attribute ignored.");
                continue;
            }
            if (!byName.TryGetValue(name, out Descriptor descriptor))
            {
                _warnings?.Add($"Settings: unknown setting '{name}' ignored.");
                continue;
            }
            if (!seen.Add(descriptor.Name) && reportedDuplicates.Add(descriptor.Name))
            {
                _warnings?.Add($"Settings: '{descriptor.Name}' is defined more than once, the last value wins.");
            }
            if (value == null)
            {
                _warnings?.Add($"Settings: '{descriptor.Name}' has no value attribute, keeping '{descriptor.Format(settings)}'.");
                continue;
            }
            string error = descriptor.Apply(settings, value);
            if (error != null)
            {
                string fallback = descriptor.Format(Default);
                descriptor.Apply(settings, fallback);
                _warnings?.Add($"Settings: '{descriptor.Name}' value '{value}' is invalid ({error}), using default '{fallback}'.");
            }
        }
        return settings;
    }

    /// <summary>All settings as name/value pairs (stable order), for logs and the status command.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToPairs()
    {
        return Descriptors.Select(d => new KeyValuePair<string, string>(d.Name, d.Format(this))).ToList();
    }

    /// <summary>Names of restart-only settings whose value differs between the two instances.</summary>
    public IReadOnlyList<string> ChangedRestartSettings(ModSettings _other)
    {
        return Descriptors.Where(d => d.RequiresRestart && d.Format(this) != d.Format(_other)).Select(d => d.Name).ToList();
    }

    /// <summary>
    /// The settings in effect: restart-only values come from the instance loaded at server start (they decided which
    /// patches were installed and what was injected), everything else from this instance.
    /// </summary>
    public ModSettings WithRestartValuesFrom(ModSettings _startup)
    {
        var copy = (ModSettings)MemberwiseClone();
        if (_startup != null)
        {
            foreach (Descriptor descriptor in Descriptors.Where(d => d.RequiresRestart))
            {
                descriptor.Apply(copy, descriptor.Format(_startup));
            }
        }
        return copy;
    }

    /// <summary>True when the named setting can only be applied by restarting the server.</summary>
    public static bool RequiresRestart(string _name)
    {
        return Descriptors.Any(d => d.RequiresRestart && string.Equals(d.Name, _name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns a copy with a different audit level (used by "pll loglevel").</summary>
    public ModSettings WithLogLevel(AuditLevel _level)
    {
        var copy = (ModSettings)MemberwiseClone();
        copy.LogLevel = _level;
        return copy;
    }

    /// <summary>
    /// Resolves a distance setting: 0 = unlimited (null), -1 = the server's PartySharedKillRange, otherwise metres.
    /// </summary>
    public static float? ResolveDistance(float _setting, float _partySharedKillRange)
    {
        if (_setting == 0f)
        {
            return null;
        }
        if (_setting < 0f)
        {
            return Math.Max(0f, _partySharedKillRange);
        }
        return _setting;
    }

    /// <summary>Formats the shared reading message, placeholders {reader} and {item} are replaced.</summary>
    public string FormatSharedReadingMessage(string _reader, string _item)
    {
        return SharedReadingMessage
            .Replace("{reader}", _reader ?? "?")
            .Replace("{item}", _item ?? "?");
    }

    // ----- descriptor factories -------------------------------------------------------------

    private static Descriptor Bool(string _name, bool _restart, Action<ModSettings, bool> _set, Func<ModSettings, bool> _get)
    {
        return new Descriptor(_name, _restart, (s, raw) =>
        {
            if (!TryParseBool(raw, out bool value))
            {
                return "expected true or false";
            }
            _set(s, value);
            return null;
        }, s => _get(s) ? "true" : "false");
    }

    private static Descriptor Distance(string _name, bool _restart, Action<ModSettings, float> _set, Func<ModSettings, float> _get)
    {
        return new Descriptor(_name, _restart, (s, raw) =>
        {
            if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
            {
                return "expected a number";
            }
            if (value < 0f && value != -1f)
            {
                return "expected 0 (unlimited), -1 (PartySharedKillRange) or a positive distance in metres";
            }
            _set(s, value);
            return null;
        }, s => _get(s).ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static Descriptor TagList(string _name, Action<ModSettings, IReadOnlyList<string>> _set, Func<ModSettings, IReadOnlyList<string>> _get)
    {
        return new Descriptor(_name, false, (s, raw) =>
        {
            var tags = new List<string>();
            foreach (string token in raw.Split(','))
            {
                string tag = token.Trim();
                if (tag.Length == 0)
                {
                    continue;
                }
                if (!TagNamePattern.IsMatch(tag))
                {
                    return $"'{tag}' is not a valid tag name";
                }
                if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(tag);
                }
            }
            _set(s, tags);
            return null;
        }, s => string.Join(",", _get(s)));
    }

    private static string ApplyMessage(ModSettings _settings, string _raw)
    {
        string message = new string(_raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (message.Length == 0)
        {
            return "message must not be empty";
        }
        if (message.Length > MaxMessageLength)
        {
            return $"message is longer than {MaxMessageLength} characters";
        }
        _settings.SharedReadingMessage = message;
        return null;
    }

    private static string ApplyLogLevel(ModSettings _settings, string _raw)
    {
        if (!TryParseLevel(_raw, out AuditLevel level))
        {
            return "expected Off, Summary or Detailed";
        }
        _settings.LogLevel = level;
        return null;
    }

    public static bool TryParseLevel(string _raw, out AuditLevel _level)
    {
        _level = AuditLevel.Summary;
        if (string.IsNullOrWhiteSpace(_raw))
        {
            return false;
        }
        string text = _raw.Trim();
        if (int.TryParse(text, out _))
        {
            return false;   // numeric enum values are not part of the documented format
        }
        return Enum.TryParse(text, true, out _level) && Enum.IsDefined(typeof(AuditLevel), _level);
    }

    public static bool TryParseBool(string _raw, out bool _value)
    {
        switch ((_raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "on":
                _value = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "off":
                _value = false;
                return true;
            default:
                _value = false;
                return false;
        }
    }
}
