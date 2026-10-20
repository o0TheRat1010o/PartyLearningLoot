using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;
using PartyLearningLoot.Patches;

namespace PartyLearningLoot.Commands;

internal sealed class SelfTestCheck
{
    public string Name;
    public string Status;
    public string Detail;
}

internal sealed class SelfTestResult
{
    public string Trigger;
    public readonly List<SelfTestCheck> Checks = new List<SelfTestCheck>();

    public string Overall => Checks.Any(c => c.Status == SelfTest.Fail) ? SelfTest.Fail : SelfTest.Pass;
    public int Count(string _status) => Checks.Count(c => c.Status == _status);
}

/// <summary>
/// Self-verification at game start and on "pll selftest": patches and parameter bindings, catalog against the b18
/// expectation, loot table scan, scope and merge-context hygiene, the delta rule under the game's Mono runtime,
/// CachedEventParam guard, a live explain of a real party (when one is online), Shared Reading injection including the
/// configs that are sent to clients, and the audit log. Warnings never fail the test; FAIL means a feature is broken.
/// </summary>
internal static class SelfTest
{
    public const string Pass = "PASS";
    public const string Warn = "WARN";
    public const string Fail = "FAIL";
    public const string Skip = "SKIP";

    public static SelfTestResult Run(string _trigger)
    {
        var result = new SelfTestResult { Trigger = _trigger };
        Check(result, "patches.LootMerge", () => CheckGroup(PatchInstaller.LootMergeGroup, false));
        Check(result, "patches.SharedReading", () => CheckGroup(PatchInstaller.SharedReadingGroup, true));
        Check(result, "settings", CheckSettings);
        Check(result, "version", CheckVersion);
        Check(result, "catalog", CheckCatalog);
        Check(result, "catalog.b18", CheckCatalogDrift);
        Check(result, "loot_tables", CheckLootTables);
        Check(result, "scope_stack", CheckScopes);
        Check(result, "delta_rule", CheckDeltaRule);
        Check(result, "merge_context", () => MergeContext.IsClean() ? (Pass, "clean") : (Fail, "context holds state outside a spawn"));
        Check(result, "event_param_guard", CheckEventParamGuard);
        Check(result, "live_party", CheckLiveParty);
        Check(result, "shared_reading", CheckSharedReading);
        Check(result, "audit_log", CheckAuditLog);
        Check(result, "circuit_breaker", () => MergeEngine.BreakerOpen ? (Warn, $"open after {MergeEngine.SessionErrors} errors (pll reload resets it)") : (Pass, "closed"));

        Report(result);
        return result;
    }

    public static IEnumerable<string> Format(SelfTestResult _result)
    {
        yield return $"Selftest ({_result.Trigger}): {_result.Overall} - {_result.Count(Pass)} pass, {_result.Count(Warn)} warn, {_result.Count(Fail)} fail, {_result.Count(Skip)} skip";
        var table = new TextTable("Check", "Result", "Detail");
        foreach (SelfTestCheck check in _result.Checks)
        {
            table.Add(check.Name, check.Status, check.Detail);
        }
        foreach (string line in table.Lines())
        {
            yield return line;
        }
    }

    // ----- checks -----------------------------------------------------------------------------

    private static (string, string) CheckGroup(string _name, bool _optional)
    {
        PatchGroupStatus group = PatchInstaller.Find(_name);
        if (group == null)
        {
            return (Fail, "not registered");
        }
        if (group.State == "disabled")
        {
            return _optional ? (Skip, "disabled in Settings.xml (restart required to enable)") : (Fail, "disabled");
        }
        if (!group.Installed)
        {
            return (Fail, group.Error ?? group.State);
        }
        return PatchInstaller.VerifyApplied(group, out string problem)
            ? (Pass, $"{group.Methods.Count} methods patched, parameter names verified")
            : (Fail, problem);
    }

    private static (string, string) CheckSettings()
    {
        List<string> warnings = PartyLearningLootMod.SettingsWarnings;
        if (warnings.Count == 0)
        {
            return (Pass, "no warnings");
        }
        return (Warn, string.Join(" | ", warnings.Take(3)) + (warnings.Count > 3 ? $" (+{warnings.Count - 3} more)" : string.Empty));
    }

    private static (string, string) CheckVersion()
    {
        string modInfo = PartyLearningLootMod.ModVersion;
        string assembly = PartyLearningLootMod.AssemblyVersion();
        return string.Equals(modInfo, assembly, StringComparison.Ordinal)
            ? (Pass, "ModInfo and DLL " + modInfo)
            : (Warn, $"ModInfo.xml says {modInfo}, the DLL was built as {assembly} (stale deploy?)");
    }

    private static (string, string) CheckCatalog()
    {
        GameCatalog catalog = GameCatalog.Current;
        if (catalog == null)
        {
            return (Fail, "not built");
        }
        if (catalog.IsEmpty)
        {
            return (Fail, "empty: no merged tags or mergeable perks");
        }
        return (Pass, $"{catalog.Describe()} ({catalog.Data.MagazineTags.Count} magazine, {catalog.Data.BookTags.Count} book)");
    }

    private static (string, string) CheckCatalogDrift()
    {
        GameCatalog catalog = GameCatalog.Current;
        if (catalog == null)
        {
            return (Skip, "no catalog");
        }
        ModSettings settings = PartyLearningLootMod.Settings;
        bool defaults = settings.IncludeSkillMagazines && settings.IncludePerkBooks && settings.ExtraLearningTags.Count == 0 && settings.ExcludedLearningTags.Count == 0;
        List<string> drift = catalog.DriftFromB18();
        if (drift.Count == 0)
        {
            return (Pass, "matches V 3.3 (b18)" + (catalog.Data.IncludesOtherLoot ? " with other loot" : string.Empty) + ": " + catalog.Describe());
        }
        return (Warn, (defaults ? "game data differs from b18 (update or XML mod): " : "differs from b18 because of the learning settings: ") + string.Join("; ", drift));
    }

    private static (string, string) CheckLootTables()
    {
        GameCatalog catalog = GameCatalog.Current;
        if (catalog == null)
        {
            return (Skip, "no catalog");
        }
        LootScanResult scan = catalog.ScanLootTables();
        if (scan.Eligible == 0)
        {
            return (Fail, $"no eligible entry in {scan.Groups} groups / {scan.Containers} containers");
        }
        string detail = $"{scan.Eligible} eligible entries ({scan.EligibleGroupRefs} group refs) in {scan.Groups} groups and {scan.Containers} containers";
        return scan.Mixed == 0
            ? (Pass, detail + ", no mixed entries")
            : (Warn, detail + $", {scan.Mixed} mixed merged/unmerged entries stay vanilla: " + string.Join(", ", scan.MixedExamples));
    }

    private static (string, string) CheckScopes()
    {
        var stack = new ScopeStack<LootScope>();
        ScopeStack<LootScope>.Token outer = stack.Enter(new LootScope(LootOpenKind.Container, 1, "a"));
        ScopeStack<LootScope>.Token inner = stack.Enter(new LootScope(LootOpenKind.LootBag, 2, "b"));
        bool innerOk = stack.TryGetCurrent(out LootScope current) && current.OpenerId == 2;
        stack.Exit(inner);
        bool outerOk = stack.TryGetCurrent(out current) && current.OpenerId == 1;
        stack.Exit(outer);
        bool emptyOk = stack.IsEmpty && stack.Anomalies == 0;
        bool liveOk = LootManagerPatches.Scopes.IsEmpty;
        if (innerOk && outerOk && emptyOk && liveOk)
        {
            return (Pass, "nested enter/exit restores the previous scope; no scope open");
        }
        return (Fail, $"inner={innerOk} outer={outerOk} empty={emptyOk} live_scope_empty={liveOk} anomalies={LootManagerPatches.Scopes.Anomalies}");
    }

    private static (string, string) CheckDeltaRule()
    {
        var failures = new List<string>();
        void Expect(string _case, float _actual, float _expected)
        {
            if (!DeltaSelector.AlmostEqual(_actual, _expected))
            {
                failures.Add($"{_case}: {_actual} != {_expected}");
            }
        }
        Expect("member wins", DeltaSelector.SelectPerk(1f, 3f, new[] { 11f }).Extra, 8f);
        Expect("opener wins", DeltaSelector.SelectPerk(1f, 11f, new[] { 3f }).Extra, 0f);
        Expect("tie keeps vanilla", DeltaSelector.SelectPerk(1f, 5f, new[] { 5f }).WinnerIndex, -1);
        Expect("invalid member ignored", DeltaSelector.SelectPerk(1f, 3f, new[] { float.NaN }).Extra, 0f);
        Expect("best of many", DeltaSelector.SelectPerk(2f, 2f, new[] { 4f, 9f, 6f }).Extra, 7f);
        Expect("apply without quirk", DeltaSelector.ApplyExtra(3f, 8f, 0.5f, 0.5f), 11f);

        // Vanilla quirk: crafting complete sets the base to 1. Food magazine (veryLow 0.05): opener complete (vanilla 1.0),
        // member Master Chef +10 (alone 0.55). The member's bonus is counted at the normal weight: 1 + 0.05 * 10 = 1.5.
        float extra = DeltaSelector.SelectPerk(1f, 1f, new[] { 11f }).Extra;
        Expect("complete quirk not amplified", 1f * DeltaSelector.ApplyExtra(1f, extra, 0.05f, 1f), 1.5f);

        // Two perks: perk 1 opener complete (base 1), member +20; perk 2 opener +10 -> 12, never below vanilla 11.
        float extra1 = DeltaSelector.SelectPerk(1f, 1f, new[] { 21f }).Extra;
        Expect("two perks never below vanilla", 1f * DeltaSelector.ApplyExtra(11f, extra1, 0.05f, 1f), 12f);
        return failures.Count == 0
            ? (Pass, "8 cases incl. the 'complete' quirk (1.5 instead of 11) and two perks never below vanilla (12 >= 11)")
            : (Fail, string.Join("; ", failures));
    }

    private static (string, string) CheckEventParamGuard()
    {
        var original = new MinEventParams();
        EventParamGuard.CopyAll(MinEventParams.CachedEventParam, original);
        var scratch = new MinEventParams();
        EventParamGuard.CopyAll(original, scratch);
        scratch.Seed = original.Seed + 1;
        bool detectsChange = !EventParamGuard.SameState(original, scratch);
        EventParamGuard.CopyAll(original, scratch);
        bool restores = EventParamGuard.SameState(original, scratch);
        bool untouched = EventParamGuard.SameState(original, MinEventParams.CachedEventParam);
        return detectsChange && restores && untouched ? (Pass, "snapshot, compare and restore work") : (Fail, $"detects={detectsChange} restores={restores} untouched={untouched}");
    }

    private static (string, string) CheckLiveParty()
    {
        World world = GameManager.Instance?.World;
        GameCatalog catalog = GameCatalog.Current;
        if (world == null || catalog == null)
        {
            return (Skip, "no world");
        }
        ModSettings settings = PartyLearningLootMod.Settings;
        foreach (EntityPlayer player in world.Players.list.Where(p => p != null && p.Party != null))
        {
            PartySnapshot snapshot = PartySnapshot.Capture(player, settings.MaxMemberDistance, settings.IncludeDeadMembers, settings.IncludeSpectators);
            if (snapshot.VanillaReason != null)
            {
                continue;
            }
            ExplainResult explain = MergeExplainer.Explain(player, settings);
            if (explain.Error != null)
            {
                return (Fail, explain.Error);
            }
            int raised = explain.Rows.Count(r => r.Merged && r.Party > r.Vanilla);
            return explain.Ok
                ? (Pass, $"{PartySnapshot.DisplayName(player)} + {snapshot.Members.Count} member(s): {explain.Rows.Count(r => r.Merged)} merged tags >= vanilla ({raised} raised), {explain.Rows.Count(r => !r.Merged)} probes unchanged, context clean")
                : (Fail, string.Join("; ", explain.Problems));
        }
        return (Skip, "no party with an active member online (run 'pll selftest' again during play)");
    }

    private static (string, string) CheckSharedReading()
    {
        if (!SharedReadingService.Installed)
        {
            return (Skip, PatchInstaller.Find(PatchInstaller.SharedReadingGroup)?.State == "disabled" ? "disabled in Settings.xml" : "patches not installed");
        }
        var problems = new List<string>();
        InjectionResult injection = SharedReadingService.Injection;
        if (SharedReadingService.InjectionError != null)
        {
            problems.Add("injection error: " + SharedReadingService.InjectionError);
        }
        if (injection == null || injection.Injected.Count == 0)
        {
            problems.Add("no items injected (was items.xml loaded after the mod?)");
        }
        if (BuffManager.GetBuff(SharedReadingService.MarkerBuff) == null)
        {
            problems.Add("marker buff not loaded");
        }

        int markedItems = 0;
        if (ItemClass.list != null)
        {
            foreach (ItemClass item in ItemClass.list)
            {
                if (item?.Effects?.EffectGroups == null)
                {
                    continue;
                }
                bool marked = item.Effects.EffectGroups.Any(g => g.GetTriggeredEffects(MinEventTypes.onSelfPrimaryActionEnd)
                    .OfType<MinEventActionAddBuff>().Any(a => a.buffNames != null && a.buffNames.Any(SharedReadingService.IsMarker)));
                if (marked)
                {
                    markedItems++;
                }
            }
        }
        if (injection != null && markedItems != injection.Injected.Count)
        {
            problems.Add($"{markedItems} loaded items carry the marker, the id table has {injection.Injected.Count}");
        }

        string cachedBuffs = null;
        foreach (string xmlName in new[] { "items", "buffs" })
        {
            WorldStaticData.XmlLoadInfo info = WorldStaticData.xmlsToLoad.FirstOrDefault(x => x != null && x.XmlName == xmlName);
            if (info == null)
            {
                problems.Add(xmlName + ".xml load info missing");
                continue;
            }
            if (!info.SendToClients)
            {
                problems.Add(xmlName + ".xml is not sent to clients");
            }
            string cached = Decompress(info.CompressedXmlData);
            if (cached == null)
            {
                problems.Add(xmlName + ".xml has no cached client copy");
            }
            else if (cached.IndexOf(SharedReadingService.MarkerBuff, StringComparison.OrdinalIgnoreCase) < 0)
            {
                problems.Add(xmlName + ".xml client copy has no marker");
            }
            if (xmlName == "buffs")
            {
                cachedBuffs = cached;
            }
        }

        int verified = 0;
        if (injection != null && cachedBuffs != null)
        {
            problems.AddRange(CheckApplyBuffs(injection, cachedBuffs, out verified));
        }
        if (problems.Count == 0 && !SharedReadingService.IsActive)
        {
            problems.Add("Shared Reading is not active");
        }

        if (problems.Count > 0)
        {
            return (Fail, string.Join("; ", problems));
        }
        return (Pass, $"{injection.Injected.Count} items marked ({injection.CountOf(ReadableCategory.Book)} books, {injection.CountOf(ReadableCategory.Magazine)} magazines, " +
                      $"{injection.CountOf(ReadableCategory.Schematic)} schematics), marker buff loaded, client configs carry the marker, " +
                      $"{verified} apply buffs verified (client copy + server placeholders), table {injection.TableHash}");
    }

    /// <summary>
    /// The client copy of buffs.xml must carry one apply buff per learning item. Each one is parsed with the game's own
    /// effect parser and must hold exactly the item's learning actions, once for the start and once for the stack trigger.
    /// </summary>
    private static List<string> CheckApplyBuffs(InjectionResult _injection, string _cachedBuffs, out int _verified)
    {
        const int MaxReported = 5;
        _verified = 0;
        var problems = new List<string>();
        XElement root = XDocument.Parse(_cachedBuffs).Root;
        string prefix = SharedReadingService.StartupOptions.ApplyBuffPrefix;
        var buffs = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement buff in root?.Elements("buff") ?? Enumerable.Empty<XElement>())
        {
            string name = (string)buff.Attribute("name") ?? string.Empty;
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                buffs[name] = buff;
            }
        }
        if (buffs.Count != _injection.ApplyBuffById.Count || buffs.Count != SharedReadingService.ApplyBuffsCached)
        {
            problems.Add($"client buffs.xml has {buffs.Count} apply buffs, the table {_injection.ApplyBuffById.Count}, the cache patch reported {SharedReadingService.ApplyBuffsCached}");
        }
        int failed = 0;
        foreach (KeyValuePair<int, string> entry in _injection.ApplyBuffById.OrderBy(e => e.Key))
        {
            string problem = null;
            if (!buffs.TryGetValue(entry.Value, out XElement buff))
            {
                problem = entry.Value + " missing in the client buffs.xml";
            }
            else
            {
                MinEffectController parsed = MinEffectController.ParseXml(buff, null, MinEffectController.SourceParentType.BuffClass, entry.Value);
                List<MinEventActionBase> start = parsed.EffectGroups.SelectMany(g => g.GetTriggeredEffects(MinEventTypes.onSelfBuffStart)).ToList();
                int stack = parsed.EffectGroups.Sum(g => g.GetTriggeredEffects(MinEventTypes.onSelfBuffStack).Count);
                ItemClass item = _injection.ById.TryGetValue(entry.Key, out ReadableItem readable) ? ItemClass.GetItemClass(readable.Name) : null;
                int expected = item?.Effects?.EffectGroups?.Sum(g => g.GetTriggeredEffects(MinEventTypes.onSelfPrimaryActionEnd).Count(IsLearningAction)) ?? -1;
                if (start.Count == 0 || start.Count != stack || start.Count != expected || !start.All(IsLearningAction))
                {
                    problem = $"{entry.Value}: {start.Count} start / {stack} stack actions, item {readable?.Name ?? "?"} has {expected} learning actions";
                }
            }
            if (problem == null)
            {
                _verified++;
            }
            else if (++failed <= MaxReported)
            {
                problems.Add(problem);
            }
        }
        if (failed > MaxReported)
        {
            problems.Add($"... {failed - MaxReported} more apply buff problems");
        }
        // The server reads apply buffs back from the clients' buff syncs: without a known name it would log errors.
        int unknownOnServer = _injection.ApplyBuffById.Values.Count(n => BuffManager.GetBuff(n) == null);
        if (unknownOnServer > 0)
        {
            problems.Add($"{unknownOnServer} apply buffs are unknown on the server (no placeholders registered)");
        }
        return problems;
    }

    private static bool IsLearningAction(MinEventActionBase _action)
    {
        return _action is MinEventActionSetProgressionLevel
               || _action is MinEventActionAddProgressionLevel
               || _action is MinEventActionModifyCVar
               || _action is MinEventActionRemoveCVar;
    }

    private static (string, string) CheckAuditLog()
    {
        AuditLevel level = PartyLearningLootMod.Settings.LogLevel;
        if (level == AuditLevel.Off)
        {
            return (Skip, "LogLevel Off");
        }
        if (!AuditLog.IsOpen)
        {
            return (Warn, "audit log could not be opened (see server log)");
        }
        return (Pass, $"{AuditLog.Level} -> {AuditLog.FilePath}");
    }

    // ----- helpers ----------------------------------------------------------------------------

    private static void Check(SelfTestResult _result, string _name, Func<(string, string)> _check)
    {
        string status;
        string detail;
        try
        {
            (status, detail) = _check();
        }
        catch (Exception e)
        {
            status = Fail;
            detail = "check threw " + e.GetType().Name + ": " + e.Message;
        }
        _result.Checks.Add(new SelfTestCheck { Name = _name, Status = status, Detail = detail });
    }

    private static string Decompress(byte[] _data)
    {
        if (_data == null || _data.Length == 0)
        {
            return null;
        }
        using (var input = new MemoryStream(_data))
        using (var inflater = new Noemax.GZip.DeflateInputStream(input))
        using (var reader = new StreamReader(inflater, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    private static void Report(SelfTestResult _result)
    {
        string summary = $"Selftest ({_result.Trigger}): {_result.Overall} ({_result.Count(Pass)} pass, {_result.Count(Warn)} warn, {_result.Count(Fail)} fail, {_result.Count(Skip)} skip)";
        foreach (SelfTestCheck check in _result.Checks.Where(c => c.Status == Fail || c.Status == Warn))
        {
            if (check.Status == Fail)
            {
                ModLog.Error($"Selftest FAIL {check.Name}: {check.Detail}", null, "selftest");
            }
            else
            {
                ModLog.Warning($"Selftest WARN {check.Name}: {check.Detail}", "selftest");
            }
        }
        ModLog.Info(summary);
        if (!AuditLog.IsOpen)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.SelfTest);
        record.Prop("trigger", _result.Trigger).Prop("result", _result.Overall);
        record.BeginArray("checks");
        foreach (SelfTestCheck check in _result.Checks)
        {
            record.BeginObject().Prop("name", check.Name).Prop("status", check.Status).Prop("detail", check.Detail).EndObject();
        }
        record.EndArray();
        AuditLog.Commit(record, _flushSoon: true);
    }
}
