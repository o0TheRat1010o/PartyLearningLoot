using System.Collections.Generic;
using System.Linq;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>
/// Polls the parties every two seconds and writes a "party_change" record whenever a party forms, changes or disbands.
/// Polling keeps the game's party code unpatched; the audit only needs the membership timeline.
/// </summary>
internal static class PartyMonitor
{
    private static Dictionary<int, int[]> lastParties = new Dictionary<int, int[]>();
    private static readonly Dictionary<int, string> names = new Dictionary<int, string>();

    public static void Reset()
    {
        lastParties = new Dictionary<int, int[]>();
        names.Clear();
    }

    public static void Poll()
    {
        World world = GameManager.Instance?.World;
        if (world?.Players?.list == null)
        {
            return;
        }
        var current = new Dictionary<int, int[]>();
        foreach (EntityPlayer player in world.Players.list)
        {
            Party party = player?.Party;
            if (party?.MemberList == null || current.ContainsKey(party.PartyID))
            {
                continue;
            }
            current[party.PartyID] = party.MemberList.Where(m => m != null).Select(m => m.entityId).OrderBy(id => id).ToArray();
            foreach (EntityPlayer member in party.MemberList.Where(m => m != null))
            {
                names[member.entityId] = PartySnapshot.DisplayName(member);
            }
        }

        foreach (KeyValuePair<int, int[]> party in current)
        {
            if (!lastParties.TryGetValue(party.Key, out int[] before))
            {
                Write(party.Key, "formed", party.Value, party.Value, new int[0]);
            }
            else if (!before.SequenceEqual(party.Value))
            {
                Write(party.Key, "changed", party.Value, party.Value.Except(before).ToArray(), before.Except(party.Value).ToArray());
            }
        }
        foreach (KeyValuePair<int, int[]> party in lastParties)
        {
            if (!current.ContainsKey(party.Key))
            {
                Write(party.Key, "disbanded", new int[0], new int[0], party.Value);
            }
        }
        lastParties = current;
    }

    private static void Write(int _partyId, string _event, int[] _members, int[] _joined, int[] _left)
    {
        if (!AuditLog.Summary)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.PartyChange);
        record.Prop("party_id", _partyId).Prop("event", _event);
        WritePlayers(record, "members", _members);
        WritePlayers(record, "joined", _joined);
        WritePlayers(record, "left", _left);
        AuditLog.Commit(record);
    }

    private static void WritePlayers(JsonLineWriter _record, string _name, IEnumerable<int> _ids)
    {
        _record.BeginArray(_name);
        foreach (int id in _ids)
        {
            _record.BeginObject().Prop("id", id).Prop("name", names.TryGetValue(id, out string name) ? name : null).EndObject();
        }
        _record.EndArray();
    }
}
