using System.Collections.Generic;
using PartyLearningLoot.Core;
using UnityEngine;

namespace PartyLearningLoot.Engine;

/// <summary>One party member that passed the filter, captured for a single spawn or read.</summary>
internal sealed class PartyMember
{
    public EntityPlayer Entity;
    public MemberCandidate Candidate;
}

/// <summary>
/// The active members of a player's party at one moment, filtered by <see cref="MemberFilter"/>.
/// Built once per spawn/read and never cached beyond it, because progression data changes with every stats sync.
/// </summary>
internal sealed class PartySnapshot
{
    public const string ReasonNoParty = "no_party";
    public const string ReasonNoMembers = "no_members";

    public EntityPlayer Player { get; private set; }
    public int PartyId { get; private set; } = -1;
    public float? MaxDistance { get; private set; }
    public List<PartyMember> Members { get; } = new List<PartyMember>();
    public List<KeyValuePair<MemberCandidate, ExclusionReason>> Excluded { get; } = new List<KeyValuePair<MemberCandidate, ExclusionReason>>();

    /// <summary>Null when at least one member is active, otherwise why the action stays vanilla.</summary>
    public string VanillaReason => PartyId < 0 ? ReasonNoParty : Members.Count == 0 ? ReasonNoMembers : null;

    public static PartySnapshot Capture(EntityPlayer _player, float _distanceSetting, bool _includeDead, bool _includeSpectators)
    {
        var snapshot = new PartySnapshot { Player = _player };
        Party party = _player?.Party;
        if (party?.MemberList == null)
        {
            return snapshot;
        }
        snapshot.PartyId = party.PartyID;
        snapshot.MaxDistance = ModSettings.ResolveDistance(_distanceSetting, GameStats.GetInt(EnumGameStats.PartySharedKillRange));

        World world = GameManager.Instance?.World;
        Dictionary<int, EntityPlayer> players = world?.Players?.dict;
        var candidates = new List<MemberCandidate>(party.MemberList.Count);
        var entities = new List<EntityPlayer>(party.MemberList.Count);
        foreach (EntityPlayer member in party.MemberList)
        {
            if (member == null)
            {
                continue;
            }
            bool inWorld = players != null && players.TryGetValue(member.entityId, out EntityPlayer registered) && ReferenceEquals(registered, member);
            candidates.Add(new MemberCandidate
            {
                EntityId = member.entityId,
                Name = DisplayName(member),
                InWorld = inWorld,
                HasProgression = member.Progression != null,
                Spawned = member.IsSpawned(),
                // The raw flag, not IsMarkedForUnload(): that method also reports long-dead players, which still count.
                Unloading = member.markedForUnload,
                Spectator = member.IsSpectator,
                Dead = member.IsDead(),
                Distance = Vector3.Distance(_player.position, member.position)
            });
            entities.Add(member);
        }

        MemberFilterResult filtered = MemberFilter.Apply(_player.entityId, candidates, new MemberFilterOptions
        {
            MaxDistance = snapshot.MaxDistance,
            IncludeDead = _includeDead,
            IncludeSpectators = _includeSpectators
        });
        foreach (MemberCandidate included in filtered.Included)
        {
            int index = candidates.FindIndex(c => c.EntityId == included.EntityId);
            snapshot.Members.Add(new PartyMember { Entity = entities[index], Candidate = included });
        }
        foreach (KeyValuePair<MemberCandidate, ExclusionReason> excluded in filtered.Excluded)
        {
            if (excluded.Value != ExclusionReason.Opener)
            {
                snapshot.Excluded.Add(excluded);
            }
        }
        return snapshot;
    }

    /// <summary>Writes "members" and "excluded" arrays (id, name, distance, reason) into an audit record.</summary>
    public void WriteMembers(JsonLineWriter _record, bool _detailedExclusions)
    {
        _record.BeginArray("members");
        foreach (PartyMember member in Members)
        {
            _record.BeginObject()
                .Prop("id", member.Candidate.EntityId)
                .Prop("name", member.Candidate.Name)
                .Prop("dist", Round(member.Candidate.Distance))
                .Prop("dead", member.Candidate.Dead)
                .EndObject();
        }
        _record.EndArray();
        if (_detailedExclusions)
        {
            _record.BeginArray("excluded");
            foreach (KeyValuePair<MemberCandidate, ExclusionReason> excluded in Excluded)
            {
                _record.BeginObject()
                    .Prop("id", excluded.Key.EntityId)
                    .Prop("name", excluded.Key.Name)
                    .Prop("dist", Round(excluded.Key.Distance))
                    .Prop("reason", MemberFilter.ToCode(excluded.Value))
                    .EndObject();
            }
            _record.EndArray();
        }
        else
        {
            _record.Prop("excluded_count", Excluded.Count);
        }
        if (MaxDistance.HasValue)
        {
            _record.Prop("max_dist", MaxDistance.Value);
        }
        else
        {
            _record.PropNull("max_dist");
        }
    }

    public static string DisplayName(EntityPlayer _player)
    {
        if (_player == null)
        {
            return "?";
        }
        string name = null;
        try
        {
            name = _player.PlayerDisplayName;
        }
        catch
        {
            // PersistentPlayerData can be missing for a moment while a client connects.
        }
        return string.IsNullOrEmpty(name) ? _player.EntityName ?? ("entity " + _player.entityId) : name;
    }

    public static float Round(float _value)
    {
        return float.IsNaN(_value) || float.IsInfinity(_value) ? _value : Mathf.Round(_value * 10f) / 10f;
    }
}
