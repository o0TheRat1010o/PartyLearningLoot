using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PartyLearningLoot.Commands;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;
using PartyLearningLoot.Patches;
using UnityEngine;

namespace PartyLearningLoot;

/// <summary>
/// Mod entry point. Loads the settings, installs the patch groups, and drives the session lifecycle:
/// at game start (server only) the catalog is built, the audit log opened, "session_start" written and the
/// selftest run; at world shutdown open confirmations are closed and "session_end" written.
/// </summary>
public sealed class PartyLearningLootMod : IModApi
{
    public const string SettingsFileName = "Settings.xml";
    public const float PartyPollSeconds = 2f;
    public const float StatsIntervalSeconds = 600f;

    internal static Mod ModInstance { get; private set; }
    internal static string ModPath { get; private set; } = string.Empty;
    internal static string ModVersion { get; private set; } = "?";

    /// <summary>Current settings (replaced by "pll reload" and "pll loglevel").</summary>
    internal static ModSettings Settings { get; set; } = ModSettings.Default;

    /// <summary>Settings at server start; restart-only values are taken from here.</summary>
    internal static ModSettings StartupSettings { get; private set; } = ModSettings.Default;

    internal static readonly List<string> SettingsWarnings = new List<string>();
    internal static bool SessionActive { get; private set; }

    private static float nextPartyPoll;
    private static float nextStats;

    public void InitMod(Mod _modInstance)
    {
        ModInstance = _modInstance;
        ModPath = NormalizePath(_modInstance?.Path);
        ModVersion = _modInstance?.VersionString ?? AssemblyVersion();
        ModLog.Info($"Loading v{ModVersion} (game {Constants.cVersionInformation.LongString}).");

        Settings = LoadSettings(SettingsWarnings);
        StartupSettings = Settings;
        foreach (string warning in SettingsWarnings)
        {
            ModLog.Warning(warning, "settings");
        }
        SharedReadingService.StartupOptions = new InjectionOptions
        {
            Books = Settings.SharedReadingBooks,
            Magazines = Settings.SharedReadingMagazines,
            Schematics = Settings.SharedReadingSchematics
        };

        PatchInstaller.InstallAll(Settings);

        ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
        ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
        ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
        ModEvents.GameShutdown.RegisterHandler(OnGameShutdown);
        if (SharedReadingService.Installed)
        {
            ModEvents.PlayerDisconnected.RegisterHandler(OnPlayerDisconnected);
            ModEvents.SavePlayerData.RegisterHandler(OnSavePlayerData);
        }

        string groups = string.Join(", ", PatchInstaller.Groups.Select(g => g.Name + "=" + g.State));
        ModLog.Info($"Loaded ({groups}); party merging starts when a world is hosted. Settings: {Path.Combine(ModPath, SettingsFileName)}");
    }

    // ----- settings ---------------------------------------------------------------------------

    internal static string SettingsPath => Path.Combine(ModPath, SettingsFileName);

    internal static ModSettings LoadSettings(ICollection<string> _warnings)
    {
        string path = SettingsPath;
        try
        {
            if (!File.Exists(path))
            {
                _warnings.Add($"{SettingsFileName} not found at {path}; using defaults.");
                return ModSettings.Default;
            }
            return ModSettings.Parse(File.ReadAllText(path), _warnings);
        }
        catch (Exception e)
        {
            _warnings.Add($"{SettingsFileName} could not be read ({e.GetType().Name}: {e.Message}); using defaults.");
            return ModSettings.Default;
        }
    }

    // ----- session lifecycle ------------------------------------------------------------------

    private static bool IsServer => SingletonMonoBehaviour<ConnectionManager>.Instance != null && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;

    private static void OnGameStartDone(ref ModEvents.SGameStartDoneData _data)
    {
        if (!IsServer)
        {
            ModLog.Info("Connected as a client: the mod is inactive here (it only works on the server or host).");
            return;
        }
        try
        {
            StartSession();
        }
        catch (Exception e)
        {
            ModLog.Error("Starting the session failed; loot stays vanilla.", e, "session_start");
        }
    }

    private static void StartSession()
    {
        if (SessionActive)
        {
            EndSession("restart");
        }
        RuntimeStats.Reset();
        ModLog.ResetCounters();
        MergeContext.Reset();
        LootManagerPatches.Scopes.Reset();
        MergeEngine.ResetBreaker("session_start");
        SyncConfirmationTracker.Clear();
        SharedReadingService.ResetSession();
        PartyMonitor.Reset();

        string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        AuditLog.Open(ModPath, Settings.LogLevel, sessionId);
        SessionActive = true;

        var catalogWarnings = new List<string>();
        GameCatalog catalog = GameCatalog.Rebuild(Settings, catalogWarnings);
        foreach (string warning in catalogWarnings)
        {
            ModLog.Warning(warning, "catalog");
        }
        WriteSessionStart(catalog, catalogWarnings);

        SelfTestResult selftest = SelfTest.Run("auto");
        float now = Time.realtimeSinceStartup;
        nextPartyPoll = now;
        nextStats = now + StatsIntervalSeconds;

        ModLog.Info($"ready: v{ModVersion}, {(Settings.Enabled ? "merging enabled" : "merging disabled")}{(Settings.DryRun ? " (dry run)" : string.Empty)}, " +
                    $"catalog {catalog.Describe()}, " +
                    $"shared reading {(SharedReadingService.IsActive ? "active" : SharedReadingService.Installed ? "INACTIVE" : "off")}, " +
                    $"selftest {selftest.Overall}, audit {(AuditLog.IsOpen ? AuditLog.FilePath : "off")}");
    }

    internal static void EndSession(string _reason)
    {
        if (!SessionActive)
        {
            return;
        }
        try
        {
            SyncConfirmationTracker.OnSessionEnd();
            if (AuditLog.IsOpen)
            {
                JsonLineWriter record = AuditLog.Begin(AuditRecords.SessionEnd);
                record.Prop("reason", _reason);
                RuntimeStats.Write(record);
                AuditLog.Commit(record);
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Writing session_end failed.", e, "session_end");
        }
        finally
        {
            AuditLog.Close();
            MergeContext.Reset();
            LootManagerPatches.Scopes.Reset();
            GameCatalog.Clear();
            SessionActive = false;
        }
    }

    private static void OnGameUpdate(ref ModEvents.SGameUpdateData _data)
    {
        if (!SessionActive)
        {
            return;
        }
        float now = Time.realtimeSinceStartup;
        if (now >= nextPartyPoll)
        {
            nextPartyPoll = now + PartyPollSeconds;
            try
            {
                PartyMonitor.Poll();
            }
            catch (Exception e)
            {
                ModLog.Error("Party monitor failed.", e, "party_monitor");
            }
        }
        if (now >= nextStats)
        {
            nextStats = now + StatsIntervalSeconds;
            WriteStats();
        }
    }

    private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData _data)
    {
        EndSession("world_shutdown");
        SharedReadingService.OnWorldUnloaded();
    }

    private static void OnGameShutdown(ref ModEvents.SGameShutdownData _data)
    {
        EndSession("game_shutdown");
        AuditLog.Close();
    }

    private static void OnPlayerDisconnected(ref ModEvents.SPlayerDisconnectedData _data)
    {
        if (SessionActive && _data.ClientInfo != null)
        {
            SyncConfirmationTracker.OnDisconnect(_data.ClientInfo.entityId);
        }
    }

    private static void OnSavePlayerData(ref ModEvents.SSavePlayerDataData _data)
    {
        if (!SessionActive)
        {
            return;
        }
        try
        {
            SyncConfirmationTracker.OnPlayerData(_data.ClientInfo, _data.PlayerDataFile);
        }
        catch (Exception e)
        {
            ModLog.Error("Shared Reading: checking uploaded player data failed.", e, "confirm_playerdata");
        }
    }

    internal static void WriteStats()
    {
        if (!AuditLog.Summary)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.Stats);
        RuntimeStats.Write(record);
        AuditLog.Commit(record);
    }

    // ----- records ------------------------------------------------------------------------------

    private static void WriteSessionStart(GameCatalog _catalog, List<string> _catalogWarnings)
    {
        if (!AuditLog.IsOpen)
        {
            return;
        }
        World world = GameManager.Instance?.World;
        JsonLineWriter record = AuditLog.Begin(AuditRecords.SessionStart);
        record.BeginObject("mod")
            .Prop("name", ModInstance?.Name ?? "PartyLearningLoot")
            .Prop("version", ModVersion)
            .Prop("assembly", AssemblyVersion())
            .EndObject();
        record.BeginObject("game")
            .Prop("version", Constants.cVersionInformation.LongString)
            .Prop("dedicated", GameManager.IsDedicatedServer)
            .Prop("world", GamePrefs.GetString(EnumGamePrefs.GameWorld))
            .Prop("game_name", GamePrefs.GetString(EnumGamePrefs.GameName))
            .Prop("party_shared_kill_range", GameStats.GetInt(EnumGameStats.PartySharedKillRange))
            .Prop("players_online", world?.Players?.list?.Count ?? 0)
            .EndObject();
        // Restart-only switches are logged as they are in effect (server start), changes waiting for a restart separately.
        record.BeginObject("settings");
        foreach (KeyValuePair<string, string> pair in Settings.WithRestartValuesFrom(StartupSettings).ToPairs())
        {
            record.Prop(pair.Key, pair.Value);
        }
        record.EndObject();
        record.PropStrings("pending_restart", Settings.ChangedRestartSettings(StartupSettings));
        record.PropStrings("settings_warnings", SettingsWarnings);
        record.BeginArray("patches");
        foreach (PatchGroupStatus group in PatchInstaller.Groups)
        {
            record.BeginObject().Prop("group", group.Name).Prop("state", group.State).PropStrings("targets", group.Targets);
            if (group.Error != null)
            {
                record.Prop("error", group.Error);
            }
            record.EndObject();
        }
        record.EndArray();
        WriteCatalog(record, _catalog, _catalogWarnings);
        SharedReadingService.WriteInjection(record);
        record.BeginObject("log").Prop("level", AuditLog.Level.ToString()).Prop("file", AuditLog.FilePath).EndObject();
        AuditLog.Commit(record, _flushSoon: true);
    }

    /// <summary>
    /// The catalog in effect (session_start and reload): which tags are merged (learning material, other loot) and
    /// which never are, so an analysis can check every merged entry against it.
    /// </summary>
    internal static void WriteCatalog(JsonLineWriter _record, GameCatalog _catalog, IEnumerable<string> _warnings)
    {
        List<string> drift = _catalog.DriftFromB18();
        _record.BeginObject("catalog")
            .Prop("include_other_loot", _catalog.Data.IncludesOtherLoot)
            .Prop("learning_tags", _catalog.Data.LearningTags.Count)
            .Prop("magazine_tags", _catalog.Data.MagazineTags.Count)
            .Prop("book_tags", _catalog.Data.BookTags.Count)
            .Prop("other_loot_tags", _catalog.Data.OtherLootTags.Count)
            .Prop("merged_tags", _catalog.Data.MergedTags.Count)
            .Prop("unmerged_tags", _catalog.Data.UnmergedTags.Count)
            .Prop("mergeable_perks", _catalog.Mergeable.Count)
            .Prop("matches_b18", drift.Count == 0)
            .PropStrings("drift", drift)
            .PropStrings("learning", _catalog.Data.LearningTags)
            .PropStrings("other_loot", _catalog.Data.OtherLootTags)
            .PropStrings("unmerged", _catalog.Data.UnmergedTags)
            .PropStrings("perks", _catalog.Data.MergeablePerks.Select(p => p.Name))
            .PropStrings("warnings", _warnings ?? Enumerable.Empty<string>())
            .EndObject();
    }

    /// <summary>The loader reports paths like ".../7DaysToDie_Data/../Mods\Name"; resolve them for readable logs.</summary>
    private static string NormalizePath(string _path)
    {
        if (string.IsNullOrEmpty(_path))
        {
            return string.Empty;
        }
        try
        {
            return Path.GetFullPath(_path);
        }
        catch (Exception)
        {
            return _path;
        }
    }

    internal static string AssemblyVersion()
    {
        Assembly assembly = typeof(PartyLearningLootMod).Assembly;
        var informational = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
        return informational?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "?";
    }
}
