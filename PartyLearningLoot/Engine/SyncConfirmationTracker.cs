using System;
using System.Collections.Generic;
using System.Linq;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>
/// Verifies that member clients really applied the reads Shared Reading sent them (apply buffs). The client is
/// authoritative for its player data, so later syncs from that client are compared with the expectation:
/// <list type="bullet">
/// <item>"stats": the progression in NetPackagePlayerStats (sent when the client's progression changed), levels;</item>
/// <item>"buffs": the cvars of the client's buff sync (NetPackageEntityStatsBuff, about every 5.5 s while alive, changed
/// cvars only, unreliable delivery) - it confirms recipes early but never counts a mismatch;</item>
/// <item>"playerdata": the player data file every client uploads about every 30 s (progression and cvars).</item>
/// </list>
/// Expected is at least one level above the server copy before the read (min(prediction, before + 1)): random parts
/// such as the nerd bonus are rolled again by the client, and members keep learning on their own. Every expected value
/// keeps its own mismatch streak, counted only by syncs that can see it. Syncs during the first
/// <see cref="GraceSeconds"/> seconds, or while the member is dead (the apply buff starts after the respawn), are not
/// counted: they may have been sent before the client processed the buff. After that a missing value is a warning; a
/// value missing in <see cref="MismatchesForError"/> counted syncs in a row, or a disconnect with a counted mismatch, is
/// an error. Expectations still open at session end are reported as unconfirmed.
/// </summary>
internal static class SyncConfirmationTracker
{
    public const int MismatchesForError = 3;
    public const double GraceSeconds = 5d;
    public const string ChannelStats = "stats";
    public const string ChannelBuffs = "buffs";
    public const string ChannelPlayerData = "playerdata";

    private const string LevelKey = "level:";
    private const string CVarKey = "cvar:";

    private sealed class Expectation
    {
        public int MemberId;
        public string MemberName;
        public long ReadSeq;
        public string Item;
        public DateTime CreatedUtc;
        public DateTime GraceUntilUtc;
        public readonly Dictionary<string, int> Levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, float> CVars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ConfirmedLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ConfirmedCVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Counted syncs in a row that still lacked the value, per open value ("level:x" / "cvar:x").</summary>
        public readonly Dictionary<string, int> Streaks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int Checks;

        public bool Complete => ConfirmedLevels.Count == Levels.Count && ConfirmedCVars.Count == CVars.Count;
        public int MismatchStreak => Streaks.Count == 0 ? 0 : Streaks.Values.Max();
    }

    private static readonly Dictionary<int, List<Expectation>> pending = new Dictionary<int, List<Expectation>>();

    public static int PendingCount => pending.Values.Sum(l => l.Count);

    public static bool HasPending(int _entityId) => pending.ContainsKey(_entityId);

    public static void Clear()
    {
        pending.Clear();
    }

    /// <summary>Registers what the member's client must report back after a shared read.</summary>
    public static void Expect(MemberReadResult _result, long _readSeq, string _item)
    {
        DateTime now = DateTime.UtcNow;
        var expectation = new Expectation
        {
            MemberId = _result.Member.entityId,
            MemberName = _result.Name,
            ReadSeq = _readSeq,
            Item = _item,
            CreatedUtc = now,
            GraceUntilUtc = now.AddSeconds(GraceSeconds)
        };
        foreach (KeyValuePair<string, int[]> level in _result.LevelChanges.Where(l => l.Value[1] > l.Value[0]))
        {
            expectation.Levels[level.Key] = ExpectedLevel(level.Value[0], level.Value[1]);
        }
        foreach (KeyValuePair<string, float[]> cvar in _result.CVarChanges.Where(c => _result.LearningCVars.Contains(c.Key) && c.Value[1] > c.Value[0]))
        {
            expectation.CVars[cvar.Key] = cvar.Value[1];
        }
        if (expectation.Levels.Count == 0 && expectation.CVars.Count == 0)
        {
            return;
        }
        if (!pending.TryGetValue(expectation.MemberId, out List<Expectation> list))
        {
            list = new List<Expectation>();
            pending.Add(expectation.MemberId, list);
        }
        list.Add(expectation);
    }

    /// <summary>At least one level above the value before the read, never more than predicted.</summary>
    public static int ExpectedLevel(int _before, int _predictedAfter)
    {
        return Math.Min(_predictedAfter, Math.Max(_before, 0) + 1);
    }

    /// <summary>Stats sync: the progression the member's client just sent (once per received package).</summary>
    public static void OnProgressionSync(EntityPlayer _member)
    {
        Progression progression = _member?.Progression;
        if (progression == null || !pending.ContainsKey(_member.entityId))
        {
            return;
        }
        Evaluate(_member.entityId, ChannelStats, name => progression.GetProgressionValue(name)?.Level, null, _member.IsDead(), true);
    }

    /// <summary>Buff sync: the member's cvars as just reported by the client (confirmation only, see the class remarks).</summary>
    public static void OnBuffSync(EntityPlayer _member)
    {
        EntityBuffs buffs = _member?.Buffs;
        if (buffs == null || !pending.ContainsKey(_member.entityId))
        {
            return;
        }
        Evaluate(_member.entityId, ChannelBuffs, null, name => buffs.GetCustomVar(name), _member.IsDead(), false);
    }

    /// <summary>Player data upload (every ~30 s per client): full progression and cvars of the client.</summary>
    public static void OnPlayerData(ClientInfo _client, PlayerDataFile _data)
    {
        if (_client == null || _data == null || !pending.ContainsKey(_client.entityId))
        {
            return;
        }
        Dictionary<string, int> levels = PlayerDataProbe.ReadLevels(_data, out string levelError);
        Dictionary<string, float> cvars = PlayerDataProbe.ReadCVars(_data, out string cvarError);
        if (levels == null && cvars == null)
        {
            ModLog.Warning($"Could not read the player data of {_client.playerName} for confirmation ({levelError ?? cvarError}).", "confirm_probe");
            return;
        }
        bool dead = GameManager.Instance?.World?.GetEntity(_client.entityId) is EntityPlayer player && player.IsDead();
        Evaluate(_client.entityId, ChannelPlayerData,
            levels == null ? (Func<string, int?>)null : name => levels.TryGetValue(name, out int level) ? level : (int?)null,
            cvars == null ? (Func<string, float?>)null : name => cvars.TryGetValue(name, out float value) ? value : 0f,
            dead, true);
    }

    /// <summary>A member left: open expectations with counted mismatches become errors, the others stay unconfirmed.</summary>
    public static void OnDisconnect(int _entityId)
    {
        if (!pending.TryGetValue(_entityId, out List<Expectation> list))
        {
            return;
        }
        foreach (Expectation expectation in list)
        {
            if (expectation.MismatchStreak > 0)
            {
                Finish(expectation, "disconnect", "failed", "member disconnected while the client still reported old values");
            }
            else
            {
                Finish(expectation, "disconnect", "unconfirmed", "member disconnected before a sync confirmed the values");
            }
        }
        pending.Remove(_entityId);
    }

    /// <summary>Session end: everything still open is reported as unconfirmed (warning).</summary>
    public static void OnSessionEnd()
    {
        foreach (List<Expectation> list in pending.Values)
        {
            foreach (Expectation expectation in list)
            {
                Finish(expectation, "session_end", "unconfirmed", "no sync confirmed the values before the session ended");
            }
        }
        pending.Clear();
    }

    private static void Evaluate(int _entityId, string _channel, Func<string, int?> _levels, Func<string, float?> _cvars, bool _memberDead, bool _countMismatches)
    {
        List<Expectation> list = pending[_entityId];
        DateTime now = DateTime.UtcNow;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            Expectation expectation = list[i];
            expectation.Checks++;
            if (_memberDead)
            {
                // A dead member's client starts the apply buff only after the respawn.
                expectation.GraceUntilUtc = now.AddSeconds(GraceSeconds);
            }
            bool counting = _countMismatches && !_memberDead && now >= expectation.GraceUntilUtc;
            var missing = new List<string>();
            bool counted = false;
            if (_levels != null)
            {
                foreach (KeyValuePair<string, int> level in expectation.Levels)
                {
                    if (expectation.ConfirmedLevels.Contains(level.Key))
                    {
                        continue;
                    }
                    int? actual = _levels(level.Key);
                    if (actual.HasValue && actual.Value >= level.Value)
                    {
                        expectation.ConfirmedLevels.Add(level.Key);
                        expectation.Streaks.Remove(LevelKey + level.Key);
                    }
                    else
                    {
                        missing.Add($"{level.Key} expected>={level.Value} actual={actual?.ToString() ?? "missing"}");
                        counted |= Count(expectation, LevelKey + level.Key, counting);
                    }
                }
            }
            if (_cvars != null)
            {
                foreach (KeyValuePair<string, float> cvar in expectation.CVars)
                {
                    if (expectation.ConfirmedCVars.Contains(cvar.Key))
                    {
                        continue;
                    }
                    float? actual = _cvars(cvar.Key);
                    if (actual.HasValue && actual.Value >= cvar.Value - 1e-4f)
                    {
                        expectation.ConfirmedCVars.Add(cvar.Key);
                        expectation.Streaks.Remove(CVarKey + cvar.Key);
                    }
                    else
                    {
                        missing.Add($"{cvar.Key} expected>={cvar.Value} actual={actual?.ToString() ?? "missing"}");
                        counted |= Count(expectation, CVarKey + cvar.Key, counting);
                    }
                }
            }

            if (expectation.Complete)
            {
                Finish(expectation, _channel, "confirmed", null);
                list.RemoveAt(i);
                continue;
            }
            if (!counted)
            {
                continue;   // grace, dead member, confirmation-only channel or no visible open value
            }
            if (expectation.MismatchStreak >= MismatchesForError)
            {
                Finish(expectation, _channel, "failed", string.Join("; ", missing));
                list.RemoveAt(i);
            }
            else
            {
                RuntimeStats.ConfirmMismatchWarnings++;
                Write(expectation, _channel, "pending_mismatch", string.Join("; ", missing));
            }
        }
        if (list.Count == 0)
        {
            pending.Remove(_entityId);
        }
    }

    private static bool Count(Expectation _expectation, string _key, bool _counting)
    {
        if (!_counting)
        {
            return false;
        }
        _expectation.Streaks[_key] = _expectation.Streaks.TryGetValue(_key, out int streak) ? streak + 1 : 1;
        return true;
    }

    private static void Finish(Expectation _expectation, string _channel, string _status, string _detail)
    {
        switch (_status)
        {
            case "confirmed":
                RuntimeStats.ConfirmConfirmed++;
                break;
            case "failed":
                RuntimeStats.ConfirmFailed++;
                ModLog.Error($"Shared Reading: {_expectation.MemberName}'s client did not apply '{_expectation.Item}' ({_detail}).", null, "confirm_failed");
                break;
            default:
                RuntimeStats.ConfirmUnconfirmed++;
                ModLog.Warning($"Shared Reading: '{_expectation.Item}' for {_expectation.MemberName} is unconfirmed ({_detail}).", "confirm_unconfirmed");
                break;
        }
        Write(_expectation, _channel, _status, _detail);
    }

    private static void Write(Expectation _expectation, string _channel, string _status, string _detail)
    {
        if (!AuditLog.IsOpen)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.SharedReadConfirm);
        record.BeginObject("member").Prop("id", _expectation.MemberId).Prop("name", _expectation.MemberName).EndObject()
            .Prop("read_seq", _expectation.ReadSeq)
            .Prop("item", _expectation.Item)
            .Prop("channel", _channel)
            .Prop("status", _status)
            .Prop("latency_ms", Math.Round((DateTime.UtcNow - _expectation.CreatedUtc).TotalMilliseconds))
            .Prop("checks", _expectation.Checks)
            .Prop("mismatch_streak", _expectation.MismatchStreak);
        record.BeginObject("expected");
        foreach (KeyValuePair<string, int> level in _expectation.Levels)
        {
            record.Prop(level.Key, level.Value);
        }
        foreach (KeyValuePair<string, float> cvar in _expectation.CVars)
        {
            record.Prop(cvar.Key, cvar.Value);
        }
        record.EndObject();
        if (_detail != null)
        {
            record.Prop("detail", _detail);
        }
        AuditLog.Commit(record, _status == "failed");
    }
}

/// <summary>Reads progression levels and cvars from an uploaded player data file without touching any entity.</summary>
internal static class PlayerDataProbe
{
    public static Dictionary<string, int> ReadLevels(PlayerDataFile _data, out string _error)
    {
        _error = null;
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_data.progressionData == null || _data.progressionData.Length == 0)
            {
                _error = "no progression data";
                return null;
            }
            StreamUtils.FromBlob(_data.progressionData, reader =>
            {
                reader.ReadByte();      // version
                reader.ReadUInt16();    // level
                reader.ReadInt32();     // exp to next level
                reader.ReadUInt16();    // skill points
                int count = reader.ReadInt32();
                var value = new ProgressionValue();
                for (int i = 0; i < count; i++)
                {
                    value.Read(reader);
                    levels[value.Name] = value.level;
                }
            });
            return levels;
        }
        catch (Exception e)
        {
            _error = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }

    public static Dictionary<string, float> ReadCVars(PlayerDataFile _data, out string _error)
    {
        _error = null;
        var cvars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_data.buffData == null || _data.buffData.Length == 0)
            {
                _error = "no buff data";
                return null;
            }
            bool supported = true;
            StreamUtils.FromBlob(_data.buffData, reader =>
            {
                int version = reader.ReadByte();
                int buffCount = reader.ReadUInt16();
                var buff = new BuffValue();
                for (int i = 0; i < buffCount; i++)
                {
                    buff.Read(reader, version);
                }
                if (version < 2)
                {
                    supported = false;
                    return;
                }
                int cvarCount = reader.ReadUInt16();
                for (int i = 0; i < cvarCount; i++)
                {
                    string name = reader.ReadString();
                    cvars[name] = reader.ReadSingle();
                }
            });
            if (!supported)
            {
                _error = "legacy buff data format";
                return null;
            }
            return cvars;
        }
        catch (Exception e)
        {
            _error = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }
}
