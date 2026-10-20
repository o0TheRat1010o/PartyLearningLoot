using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;
using PartyLearningLoot.Patches;

namespace PartyLearningLoot.Commands;

/// <summary>Admin console command "pll" (alias "partylearningloot").</summary>
public sealed class ConsoleCmdPartyLearningLoot : ConsoleCmdAbstract
{
    public override int DefaultPermissionLevel => 0;

    public override string[] getCommands() => new[] { "partylearningloot", "pll" };

    public override string getDescription() => "Party Learning Loot: status, reload, loglevel, selftest, explain, simulate";

    public override string getHelp()
    {
        return "Usage:\n" +
               "  pll status                              - version, settings, patches, catalog, statistics, audit log file\n" +
               "  pll reload                              - re-read Settings.xml (Shared Reading switches need a restart)\n" +
               "  pll loglevel off|summary|detailed       - change the audit log level for this session\n" +
               "  pll selftest                            - run all self checks (PASS/WARN/FAIL)\n" +
               "  pll explain [player]                    - vanilla vs party weight per merged tag for a player's party\n" +
               $"  pll simulate <lootContainer> [runs] [player] - paired vanilla/party loot simulation (default {LootSimulator.DefaultRuns} runs)\n" +
               "Player: entity id, name or platform id; defaults to yourself (or the only player online).";
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        try
        {
            string sub = _params.Count == 0 ? "status" : _params[0].ToLowerInvariant();
            switch (sub)
            {
                case "status":
                    Output(Status());
                    break;
                case "reload":
                    Output(Reload());
                    break;
                case "loglevel":
                    Output(LogLevel(_params.Count > 1 ? _params[1] : null));
                    break;
                case "selftest":
                    Output(SelfTest.Format(SelfTest.Run("command")));
                    break;
                case "explain":
                {
                    EntityPlayer player = ResolvePlayer(_params.Count > 1 ? _params[1] : null, _senderInfo, out string error);
                    Output(player == null ? new[] { "explain: " + error } : MergeExplainer.Format(MergeExplainer.Explain(player, PartyLearningLootMod.Settings)));
                    break;
                }
                case "simulate":
                    Output(Simulate(_params, _senderInfo));
                    break;
                default:
                    Output(new[] { $"Unknown sub command '{_params[0]}'.", GetHelp() });
                    break;
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Console command failed.", e, "command");
            Output(new[] { "pll failed: " + e.GetType().Name + ": " + e.Message });
        }
    }

    // ----- sub commands -------------------------------------------------------------------------

    private static IEnumerable<string> Status()
    {
        ModSettings settings = PartyLearningLootMod.Settings;
        GameCatalog catalog = GameCatalog.Current;
        var table = new TextTable("Item", "Value");
        table.Add("Version", $"{PartyLearningLootMod.ModVersion} (DLL {PartyLearningLootMod.AssemblyVersion()}), game {Constants.cVersionInformation.LongString}");
        table.Add("Session", PartyLearningLootMod.SessionActive ? $"active since {RuntimeStats.SessionStartUtc:HH:mm:ss} UTC" : "no world hosted");
        table.Add("Merging", !settings.Enabled ? "disabled" : MergeEngine.BreakerOpen ? "BREAKER OPEN (vanilla until pll reload)" : settings.DryRun ? "dry run (computed, loot stays vanilla)" : "enabled");
        table.Add("Applies to", $"containers={settings.ApplyToContainers}, loot bags={settings.ApplyToLootBags}, magazines={settings.IncludeSkillMagazines}, books={settings.IncludePerkBooks}, other loot={settings.IncludeOtherLoot}");
        table.Add("Members", $"distance={DescribeDistance(settings.MaxMemberDistance)}, dead={settings.IncludeDeadMembers}, spectators={settings.IncludeSpectators}");
        foreach (PatchGroupStatus group in PatchInstaller.Groups)
        {
            table.Add("Patches " + group.Name, group.State + (group.Installed ? $" ({group.Methods.Count} methods)" : string.Empty) + (group.Error != null ? ": " + group.Error : string.Empty));
        }
        table.Add("Catalog", catalog == null
            ? "not built"
            : $"{catalog.Describe()} ({catalog.Data.MagazineTags.Count} magazine, {catalog.Data.BookTags.Count} book), b18 drift: {(catalog.DriftFromB18().Count == 0 ? "none" : "yes (see selftest)")}");
        InjectionResult injection = SharedReadingService.Injection;
        table.Add("Shared Reading", !SharedReadingService.Installed
            ? "off"
            : SharedReadingService.IsActive
                ? $"active: {injection.Injected.Count} items ({injection.CountOf(ReadableCategory.Book)} books, {injection.CountOf(ReadableCategory.Magazine)} magazines, {injection.CountOf(ReadableCategory.Schematic)} schematics), {SharedReadingService.ApplyBuffsCached} apply buffs, xp={settings.SharedReadingShareXp}, distance={DescribeDistance(settings.SharedReadingMaxDistance)}"
                : "INACTIVE: " + (SharedReadingService.InjectionError
                                  ?? (injection == null || injection.Injected.Count == 0 ? "no items injected" : "no apply buffs in the client buffs.xml")));
        string reasons = string.Join(", ", RuntimeStats.VanillaReasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}"));
        table.Add("Spawns", $"merged {RuntimeStats.SpawnsMerged} (avg {RuntimeStats.MergedMsAverage:0.000} ms, max {RuntimeStats.MergedMsMax:0.000} ms, {RuntimeStats.MemberWins} member wins), vanilla {RuntimeStats.SpawnsVanilla}{(reasons.Length > 0 ? " (" + reasons + ")" : string.Empty)}");
        table.Add("Shared reads", $"{RuntimeStats.SharedReads} (learned {RuntimeStats.SharedMemberLearnings}, xp grants {RuntimeStats.SharedXpGrants}, rejected {RuntimeStats.SharedReadsRejected}, rate limited {RuntimeStats.SharedReadsRateLimited})");
        table.Add("Confirmations", $"confirmed {RuntimeStats.ConfirmConfirmed}, pending {SyncConfirmationTracker.PendingCount}, mismatch warnings {RuntimeStats.ConfirmMismatchWarnings}, failed {RuntimeStats.ConfirmFailed}, unconfirmed {RuntimeStats.ConfirmUnconfirmed}");
        table.Add("Health", $"errors {ModLog.ErrorCount}, warnings {ModLog.WarningCount}, invariant violations {RuntimeStats.InvariantViolations}, merge errors {RuntimeStats.MergeErrors}");
        table.Add("Audit log", AuditLog.IsOpen ? $"{AuditLog.Level}: {AuditLog.FilePath} ({AuditLog.Written} records, {AuditLog.Dropped} dropped)" : $"off ({settings.LogLevel})");
        return table.Lines();
    }

    private static IEnumerable<string> Reload()
    {
        var lines = new List<string>();
        var warnings = new List<string>();
        ModSettings previous = PartyLearningLootMod.Settings;
        ModSettings next = PartyLearningLootMod.LoadSettings(warnings);
        PartyLearningLootMod.Settings = next;
        PartyLearningLootMod.SettingsWarnings.Clear();
        PartyLearningLootMod.SettingsWarnings.AddRange(warnings);
        foreach (string warning in warnings)
        {
            ModLog.Warning(warning, "settings");
            lines.Add("Warning: " + warning);
        }
        IReadOnlyList<string> restart = next.ChangedRestartSettings(PartyLearningLootMod.StartupSettings);
        foreach (string name in restart)
        {
            lines.Add($"Note: {name} changed but only takes effect after a server restart.");
        }

        GameCatalog catalog = null;
        var catalogWarnings = new List<string>();
        if (PartyLearningLootMod.SessionActive)
        {
            AuditLog.SetLevel(next.LogLevel, PartyLearningLootMod.ModPath);
            catalog = GameCatalog.Rebuild(next, catalogWarnings);
            foreach (string warning in catalogWarnings)
            {
                ModLog.Warning(warning, "catalog");
            }
            lines.Add($"Catalog rebuilt: {catalog.Describe()}.");
        }
        MergeEngine.ResetBreaker("reload");

        List<string> changed = next.ToPairs().Zip(previous.ToPairs(), (a, b) => a.Value != b.Value ? $"{a.Key}={a.Value}" : null).Where(c => c != null).ToList();
        if (AuditLog.IsOpen)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.Reload);
            record.PropStrings("changed", changed).PropStrings("warnings", warnings).PropStrings("restart_required", restart);
            // The settings now in effect: restart-only switches keep their server-start values until the restart.
            record.BeginObject("settings");
            foreach (KeyValuePair<string, string> pair in next.WithRestartValuesFrom(PartyLearningLootMod.StartupSettings).ToPairs())
            {
                record.Prop(pair.Key, pair.Value);
            }
            record.EndObject();
            if (catalog != null)
            {
                PartyLearningLootMod.WriteCatalog(record, catalog, catalogWarnings);
            }
            AuditLog.Commit(record);
        }
        lines.Insert(0, changed.Count == 0 ? "Settings reloaded, nothing changed." : "Settings reloaded, changed: " + string.Join(", ", changed));
        ModLog.Info(lines[0]);
        return lines;
    }

    private static IEnumerable<string> LogLevel(string _value)
    {
        if (_value == null || !ModSettings.TryParseLevel(_value, out AuditLevel level))
        {
            return new[] { $"Current audit level: {PartyLearningLootMod.Settings.LogLevel}. Usage: pll loglevel off|summary|detailed" };
        }
        AuditLevel previous = PartyLearningLootMod.Settings.LogLevel;
        PartyLearningLootMod.Settings = PartyLearningLootMod.Settings.WithLogLevel(level);
        if (PartyLearningLootMod.SessionActive)
        {
            if (AuditLog.IsOpen && level == AuditLevel.Off)
            {
                AuditLog.Commit(AuditLog.Begin(AuditRecords.LogLevelChanged).Prop("from", previous.ToString()).Prop("to", level.ToString()));
            }
            AuditLog.SetLevel(level, PartyLearningLootMod.ModPath);
            if (AuditLog.IsOpen)
            {
                // With the settings in effect: a reload while the log was off left no record of them.
                JsonLineWriter record = AuditLog.Begin(AuditRecords.LogLevelChanged).Prop("from", previous.ToString()).Prop("to", level.ToString());
                record.BeginObject("settings");
                foreach (KeyValuePair<string, string> pair in PartyLearningLootMod.Settings.WithRestartValuesFrom(PartyLearningLootMod.StartupSettings).ToPairs())
                {
                    record.Prop(pair.Key, pair.Value);
                }
                record.EndObject();
                AuditLog.Commit(record);
            }
        }
        string message = $"Audit level {previous} -> {level}" + (AuditLog.IsOpen ? $" ({AuditLog.FilePath})" : string.Empty) + ". Settings.xml is unchanged.";
        ModLog.Info(message);
        return new[] { message };
    }

    private static IEnumerable<string> Simulate(List<string> _params, CommandSenderInfo _sender)
    {
        if (_params.Count < 2)
        {
            return new[] { "Usage: pll simulate <lootContainer> [runs] [player]" };
        }
        int runs = LootSimulator.DefaultRuns;
        string playerArg = null;
        if (_params.Count > 2)
        {
            if (int.TryParse(_params[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                runs = Math.Max(1, Math.Min(LootSimulator.MaxRuns, parsed));
                playerArg = _params.Count > 3 ? _params[3] : null;
            }
            else
            {
                playerArg = _params[2];
            }
        }
        EntityPlayer player = ResolvePlayer(playerArg, _sender, out string error);
        if (player == null)
        {
            return new[] { "simulate: " + error };
        }
        return LootSimulator.Format(LootSimulator.Run(_params[1], runs, player, PartyLearningLootMod.Settings));
    }

    // ----- helpers ------------------------------------------------------------------------------

    private static EntityPlayer ResolvePlayer(string _argument, CommandSenderInfo _sender, out string _error)
    {
        _error = null;
        World world = GameManager.Instance?.World;
        if (world == null)
        {
            _error = "no world loaded";
            return null;
        }
        if (!string.IsNullOrEmpty(_argument))
        {
            ClientInfo client = ConsoleHelper.ParseParamIdOrName(_argument);
            if (client != null && world.GetEntity(client.entityId) is EntityPlayer remote)
            {
                return remote;
            }
            if (int.TryParse(_argument, out int id) && world.GetEntity(id) is EntityPlayer byId)
            {
                return byId;
            }
            EntityPlayer byName = world.Players.list.FirstOrDefault(p => p != null && string.Equals(PartySnapshot.DisplayName(p), _argument, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                return byName;
            }
            _error = $"player '{_argument}' not found";
            return null;
        }
        if (_sender.RemoteClientInfo != null && world.GetEntity(_sender.RemoteClientInfo.entityId) is EntityPlayer self)
        {
            return self;
        }
        EntityPlayerLocal local = world.GetPrimaryPlayer();
        if (local != null)
        {
            return local;
        }
        List<EntityPlayer> online = world.Players.list.Where(p => p != null).ToList();
        if (online.Count == 1)
        {
            return online[0];
        }
        _error = online.Count == 0 ? "no player online" : "several players online, please name one";
        return null;
    }

    private static string DescribeDistance(float _setting)
    {
        if (_setting == 0f)
        {
            return "unlimited";
        }
        return _setting < 0f ? $"kill XP range ({GameStats.GetInt(EnumGameStats.PartySharedKillRange)} m)" : _setting.ToString("0.#", CultureInfo.InvariantCulture) + " m";
    }

    private static void Output(IEnumerable<string> _lines)
    {
        foreach (string line in _lines)
        {
            SingletonMonoBehaviour<SdtdConsole>.Instance.Output(line);
        }
    }
}
