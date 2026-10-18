using System;
using System.Globalization;

namespace PartyLearningLoot.Core;

/// <summary>
/// Audit log schema (JSON lines). Every line carries the common header written by <see cref="AuditRecords.BeginRecord"/>:
/// <c>v</c> schema version, <c>seq</c> sequence number, <c>t</c> UTC timestamp, <c>session</c> id, <c>type</c>,
/// <c>day</c>/<c>time</c> game clock (null outside a world). The fields specific to a record type are written by the
/// component that creates it (session lifecycle, merge engine, Shared Reading, console commands).
/// </summary>
public static class AuditRecords
{
    public const int SchemaVersion = 1;

    public const string SessionStart = "session_start";
    public const string SessionEnd = "session_end";
    public const string SelfTest = "selftest";
    public const string PartyChange = "party_change";
    public const string Spawn = "spawn";
    public const string SpawnVanilla = "spawn_vanilla";
    public const string SharedRead = "shared_read";
    public const string SharedReadConfirm = "shared_read_confirm";
    public const string InvariantViolation = "invariant_violation";
    public const string Error = "error";
    public const string Warning = "warning";
    public const string CircuitBreaker = "circuit_breaker";
    public const string RateLimited = "rate_limited";
    public const string Stats = "stats";
    public const string Reload = "reload";
    public const string LogLevelChanged = "loglevel";

    /// <summary>
    /// Records that are written even when a session reached its size cap (from a small reserve): the session frame and
    /// the problems, so an analysis still sees how the session ended and how many records were dropped.
    /// </summary>
    public static bool IsEssential(string _type)
    {
        return _type == SessionStart || _type == SessionEnd || _type == Error || _type == CircuitBreaker;
    }

    /// <summary>Starts a record with the common header; the caller adds its fields and calls Finish().</summary>
    public static JsonLineWriter BeginRecord(string _type, long _seq, DateTime _utc, string _session, int? _gameDay, string _gameTime)
    {
        var writer = new JsonLineWriter { Sequence = _seq, RecordType = _type };
        writer.BeginObject()
            .Prop("v", SchemaVersion)
            .Prop("seq", _seq)
            .Prop("t", FormatUtc(_utc))
            .Prop("session", _session)
            .Prop("type", _type);
        if (_gameDay.HasValue)
        {
            writer.Prop("day", _gameDay.Value);
        }
        else
        {
            writer.PropNull("day");
        }
        writer.Prop("time", _gameTime);
        return writer;
    }

    public static string FormatUtc(DateTime _utc)
    {
        return _utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a game clock value as HH:MM.</summary>
    public static string FormatGameTime(int _hours, int _minutes)
    {
        return _hours.ToString("00", CultureInfo.InvariantCulture) + ":" + _minutes.ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>Spawn kinds as written to the audit log.</summary>
public enum LootOpenKind
{
    Container,
    LootBag,
    Explain,
    Simulate
}

/// <summary>The loot-open scope pushed by the LootManager patches.</summary>
public readonly struct LootScope
{
    public readonly LootOpenKind Kind;
    public readonly int OpenerId;
    public readonly string ContainerName;

    public LootScope(LootOpenKind _kind, int _openerId, string _containerName)
    {
        Kind = _kind;
        OpenerId = _openerId;
        ContainerName = _containerName;
    }

    public static string KindCode(LootOpenKind _kind)
    {
        switch (_kind)
        {
            case LootOpenKind.Container: return "container";
            case LootOpenKind.LootBag: return "bag";
            case LootOpenKind.Explain: return "explain";
            default: return "simulate";
        }
    }
}
