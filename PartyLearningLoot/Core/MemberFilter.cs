using System;
using System.Collections.Generic;

namespace PartyLearningLoot.Core;

public enum ExclusionReason
{
    None,
    Opener,
    Duplicate,
    NotInWorld,
    NoProgression,
    NotSpawned,
    Unloading,
    Spectator,
    Dead,
    OutOfRange,
    UnknownPosition
}

/// <summary>Everything the filter needs to know about one party member, captured once per spawn or read.</summary>
public struct MemberCandidate
{
    public int EntityId;
    public string Name;
    public bool InWorld;
    public bool HasProgression;
    public bool Spawned;
    public bool Unloading;
    public bool Spectator;
    public bool Dead;

    /// <summary>Distance to the opener in metres, NaN when unknown.</summary>
    public float Distance;
}

public sealed class MemberFilterOptions
{
    /// <summary>Maximum distance in metres (exclusive, like vanilla shared kill XP); null = unlimited.</summary>
    public float? MaxDistance { get; set; }
    public bool IncludeDead { get; set; } = true;
    public bool IncludeSpectators { get; set; }
}

public sealed class MemberFilterResult
{
    public List<MemberCandidate> Included { get; } = new List<MemberCandidate>();
    public List<KeyValuePair<MemberCandidate, ExclusionReason>> Excluded { get; } = new List<KeyValuePair<MemberCandidate, ExclusionReason>>();
}

/// <summary>
/// Decides which party members contribute: connected, spawned, not unloading, with progression data;
/// spectators and dead members per settings; optional distance limit. The opener and duplicates never count twice.
/// </summary>
public static class MemberFilter
{
    public static MemberFilterResult Apply(int _openerId, IEnumerable<MemberCandidate> _candidates, MemberFilterOptions _options)
    {
        _options ??= new MemberFilterOptions();
        var result = new MemberFilterResult();
        var seen = new HashSet<int>();
        if (_candidates == null)
        {
            return result;
        }
        foreach (MemberCandidate candidate in _candidates)
        {
            ExclusionReason reason = Classify(_openerId, candidate, _options, seen);
            if (reason == ExclusionReason.None)
            {
                result.Included.Add(candidate);
            }
            else
            {
                result.Excluded.Add(new KeyValuePair<MemberCandidate, ExclusionReason>(candidate, reason));
            }
        }
        return result;
    }

    private static ExclusionReason Classify(int _openerId, MemberCandidate _c, MemberFilterOptions _options, HashSet<int> _seen)
    {
        if (_c.EntityId == _openerId)
        {
            return ExclusionReason.Opener;
        }
        if (!_seen.Add(_c.EntityId))
        {
            return ExclusionReason.Duplicate;
        }
        if (!_c.InWorld)
        {
            return ExclusionReason.NotInWorld;
        }
        if (!_c.HasProgression)
        {
            return ExclusionReason.NoProgression;
        }
        if (!_c.Spawned)
        {
            return ExclusionReason.NotSpawned;
        }
        if (_c.Unloading)
        {
            return ExclusionReason.Unloading;
        }
        if (_c.Spectator && !_options.IncludeSpectators)
        {
            return ExclusionReason.Spectator;
        }
        if (_c.Dead && !_options.IncludeDead)
        {
            return ExclusionReason.Dead;
        }
        if (_options.MaxDistance.HasValue)
        {
            if (float.IsNaN(_c.Distance) || float.IsInfinity(_c.Distance))
            {
                return ExclusionReason.UnknownPosition;
            }
            if (!(_c.Distance < _options.MaxDistance.Value))
            {
                return ExclusionReason.OutOfRange;
            }
        }
        return ExclusionReason.None;
    }

    /// <summary>Stable lower-case identifier used in the audit log.</summary>
    public static string ToCode(ExclusionReason _reason)
    {
        switch (_reason)
        {
            case ExclusionReason.Opener: return "opener";
            case ExclusionReason.Duplicate: return "duplicate";
            case ExclusionReason.NotInWorld: return "not_in_world";
            case ExclusionReason.NoProgression: return "no_progression";
            case ExclusionReason.NotSpawned: return "not_spawned";
            case ExclusionReason.Unloading: return "unloading";
            case ExclusionReason.Spectator: return "spectator";
            case ExclusionReason.Dead: return "dead";
            case ExclusionReason.OutOfRange: return "out_of_range";
            case ExclusionReason.UnknownPosition: return "unknown_position";
            default: return "none";
        }
    }

    public static float Distance(float _ax, float _ay, float _az, float _bx, float _by, float _bz)
    {
        float dx = _ax - _bx;
        float dy = _ay - _by;
        float dz = _az - _bz;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
