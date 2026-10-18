using System;
using System.Collections.Generic;
using System.Linq;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>What one member received from a shared read.</summary>
internal sealed class MemberReadResult
{
    public EntityPlayer Member;
    public string Name;
    public float Distance;
    public bool IsHost;

    /// <summary>
    /// True for remote members: the changes are a side-effect-free prediction on the server copy, the member's client
    /// applies the read itself. False for the host's own player, whose changes are real.
    /// </summary>
    public bool Predicted;

    public readonly List<KeyValuePair<string, int[]>> LevelChanges = new List<KeyValuePair<string, int[]>>();
    public readonly List<KeyValuePair<string, float[]>> CVarChanges = new List<KeyValuePair<string, float[]>>();

    /// <summary>CVars that unlock a recipe (schematics). Helper cvars such as nerdPointChance are no learning.</summary>
    public readonly HashSet<string> LearningCVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public int Xp;

    /// <summary>Packets sent to a remote member: "ApplyBuff:&lt;buff&gt;", "AddExp:&lt;xp&gt;", "ToolbeltMessage".</summary>
    public readonly List<string> Packets = new List<string>();
    public readonly List<string> SkippedGroups = new List<string>();
    public readonly SortedSet<string> SkippedActions = new SortedSet<string>(StringComparer.Ordinal);
    public bool MessageSent;
    public string Error;

    /// <summary>True when a perk, crafting level or recipe (predicted to) change (XP and helper cvars do not count).</summary>
    public bool Learned => LevelChanges.Count > 0 || CVarChanges.Any(c => LearningCVars.Contains(c.Key) && c.Value[1] > c.Value[0]);
}

/// <summary>
/// Evaluates the primary-action-end effects of a read item for one party member, exactly as vanilla would for that
/// member: each effect group is checked with the member's own state (already read, series complete, sandbox points),
/// and only whitelisted actions run (Set/AddProgressionLevel, Modify/RemoveCVar, GiveExp).
/// <list type="bullet">
/// <item>Host member (listen server): the actions change the local player directly, XP included.</item>
/// <item>Remote member: the actions run on the server copy as a prediction (audit, message, confirmation) and every
/// touched value is restored afterwards, so the server copy always stays what the member's client reported. The
/// client then applies the read itself through the item's apply buff (<see cref="SendApply"/>), relative to its own
/// authoritative state. No absolute value is ever pushed, so nothing a client learned meanwhile can be overwritten.</item>
/// </list>
/// </summary>
internal static class SharedReadExecutor
{
    public static MemberReadResult Apply(EntityPlayer _member, float _distance, ItemClass _item, ModSettings _settings)
    {
        var result = new MemberReadResult
        {
            Member = _member,
            Name = PartySnapshot.DisplayName(_member),
            Distance = _distance,
            IsHost = !_member.isEntityRemote,
            Predicted = _member.isEntityRemote
        };
        MinEffectController effects = _item.Effects;
        if (effects?.EffectGroups == null || _member.Progression == null || _member.Buffs == null)
        {
            result.Error = _member.Progression == null ? "member has no progression" : null;
            return result;
        }

        // Every progression and cvar the item can touch, captured before and after.
        var progressionNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var cvarNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MinEffectGroup group in effects.EffectGroups)
        {
            foreach (MinEventActionBase action in group.GetTriggeredEffects(MinEventTypes.onSelfPrimaryActionEnd))
            {
                switch (action)
                {
                    case MinEventActionSetProgressionLevel set when set.progressionName != null:
                        progressionNames.Add(set.progressionName);
                        break;
                    case MinEventActionAddProgressionLevel add when add.progressionName != null:
                        progressionNames.Add(add.progressionName);
                        break;
                    case MinEventActionModifyCVar modify when modify.cvarName != null:
                        cvarNames.Add(modify.cvarName);
                        break;
                    case MinEventActionRemoveCVar remove when remove.cvarNames != null:
                        cvarNames.UnionWith(remove.cvarNames.Where(n => !string.IsNullOrEmpty(n)));
                        break;
                }
            }
        }
        Dictionary<string, int> levelsBefore = ReadLevels(_member, progressionNames);
        Dictionary<string, float> cvarsBefore = ReadCVars(_member, cvarNames);
        var cvarsPresent = new HashSet<string>(cvarNames.Where(_member.Buffs.HasCustomVar), StringComparer.OrdinalIgnoreCase);

        var parameters = new MinEventParams
        {
            Self = _member,
            ItemValue = new ItemValue(_item.Id),
            ParentType = MinEffectController.SourceParentType.ItemClass,
            Tags = _item.ItemTags,
            Position = _member.position,
            IsLocal = false
        };

        try
        {
            foreach (MinEffectGroup group in effects.EffectGroups)
            {
                IReadOnlyList<MinEventActionBase> actions = group.GetTriggeredEffects(MinEventTypes.onSelfPrimaryActionEnd);
                if (actions.Count == 0)
                {
                    continue;
                }
                if (!group.canRun(parameters))
                {
                    result.SkippedGroups.Add(DescribeGroup(actions));
                    continue;
                }
                for (int i = 0; i < actions.Count; i++)
                {
                    MinEventActionBase action = actions[i];
                    if (action is MinEventActionAddBuff)
                    {
                        continue;   // the Shared Reading marker itself (and any other buff) is never forwarded
                    }
                    if (!IsWhitelisted(action))
                    {
                        result.SkippedActions.Add(action.GetType().Name.Replace("MinEventAction", string.Empty));
                        continue;
                    }
                    if (!action.CanExecute(MinEventTypes.onSelfPrimaryActionEnd, parameters))
                    {
                        continue;
                    }
                    if (action is MinEventActionGiveExp giveExp)
                    {
                        if (!_settings.SharedReadingShareXp)
                        {
                            continue;
                        }
                        int amount = giveExp.cvarRef ? (int)_member.Buffs.GetCustomVar(giveExp.refCvarName) : giveExp.exp;
                        if (amount <= 0)
                        {
                            continue;
                        }
                        if (!result.Predicted)
                        {
                            action.Execute(parameters);
                        }
                        result.Xp += amount;
                        continue;
                    }
                    action.Execute(parameters);
                }
            }

            Dictionary<string, int> levelsAfter = ReadLevels(_member, progressionNames);
            Dictionary<string, float> cvarsAfter = ReadCVars(_member, cvarNames);
            foreach (string name in progressionNames)
            {
                if (levelsBefore[name] != levelsAfter[name])
                {
                    result.LevelChanges.Add(new KeyValuePair<string, int[]>(name, new[] { levelsBefore[name], levelsAfter[name] }));
                }
            }
            foreach (string name in cvarNames)
            {
                if (!ValuesEqual(cvarsBefore[name], cvarsAfter[name]))
                {
                    result.CVarChanges.Add(new KeyValuePair<string, float[]>(name, new[] { cvarsBefore[name], cvarsAfter[name] }));
                    if (IsRecipeCVar(name, _item))
                    {
                        result.LearningCVars.Add(name);
                    }
                }
            }
        }
        finally
        {
            if (result.Predicted)
            {
                Restore(_member, levelsBefore, cvarsBefore, cvarsPresent);
            }
        }
        return result;
    }

    /// <summary>
    /// Remote member: adds the item's apply buff on the member's own client (targeted, like the shared kill XP), which
    /// runs the item's learning effect groups with the member's own requirements, and books the XP like the vanilla
    /// GiveExp of the read (XP type Other, bonuses applied). The buff is sent whenever the item has one, even when the
    /// prediction expects no change: the client's state is authoritative and decides.
    /// </summary>
    public static void SendApply(MemberReadResult _result, string _applyBuff)
    {
        EntityPlayer member = _result.Member;
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        int sent = 0;
        if (_applyBuff != null)
        {
            connection.SendPackage(NetPackageManager.GetPackage<NetPackageAddRemoveBuff>().Setup(member.entityId, _applyBuff, -1f, true, member.entityId, Vector3i.zero), false, member.entityId);
            _result.Packets.Add("ApplyBuff:" + _applyBuff);
            sent++;
        }
        else if (_result.LevelChanges.Count > 0 || _result.CVarChanges.Count > 0)
        {
            _result.Error = "the item has learning effects but no apply buff";
        }
        if (_result.Xp > 0)
        {
            connection.SendPackage(NetPackageManager.GetPackage<NetPackageEntityAddExpClient>().Setup(member.entityId, _result.Xp, Progression.XPTypes.Other, null), false, member.entityId);
            _result.Packets.Add("AddExp:" + _result.Xp);
            sent++;
        }
        RuntimeStats.SharedPackets += sent;
    }

    /// <summary>Shows the toolbelt message (packet for remote members, tooltip for the host).</summary>
    public static void Notify(MemberReadResult _result, string _message)
    {
        GameManager.ShowTooltipMP(_result.Member, _message);
        _result.MessageSent = true;
        if (!_result.IsHost)
        {
            _result.Packets.Add("ToolbeltMessage");
            RuntimeStats.SharedPackets++;
        }
    }

    /// <summary>A cvar is a learning effect when it is the item's Unlocks target or the name of a recipe.</summary>
    public static bool IsRecipeCVar(string _cvar, ItemClass _item)
    {
        if (!string.IsNullOrEmpty(_item.Unlocks)
            && _item.Unlocks.Split(',').Any(u => string.Equals(u.Trim(), _cvar, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return CraftingManager.GetRecipe(_cvar) != null;
    }

    /// <summary>Puts every touched level and cvar of a remote member's server copy back to its value before the prediction.</summary>
    private static void Restore(EntityPlayer _member, Dictionary<string, int> _levels, Dictionary<string, float> _cvars, HashSet<string> _cvarsPresent)
    {
        foreach (KeyValuePair<string, int> level in _levels)
        {
            ProgressionValue value = _member.Progression?.GetProgressionValue(level.Key);
            if (value != null && level.Value >= 0 && value.Level != level.Value)
            {
                value.Level = level.Value;
            }
        }
        foreach (KeyValuePair<string, float> cvar in _cvars)
        {
            if (_cvarsPresent.Contains(cvar.Key))
            {
                _member.Buffs.SetCustomVar(cvar.Key, cvar.Value, _netSync: false);
            }
            else if (_member.Buffs.HasCustomVar(cvar.Key))
            {
                _member.Buffs.RemoveCustomVar(cvar.Key);
            }
        }
    }

    private static bool IsWhitelisted(MinEventActionBase _action)
    {
        return _action is MinEventActionSetProgressionLevel
               || _action is MinEventActionAddProgressionLevel
               || _action is MinEventActionModifyCVar
               || _action is MinEventActionRemoveCVar
               || _action is MinEventActionGiveExp;
    }

    private static Dictionary<string, int> ReadLevels(EntityPlayer _member, IEnumerable<string> _names)
    {
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in _names)
        {
            levels[name] = _member.Progression?.GetProgressionValue(name)?.Level ?? -1;
        }
        return levels;
    }

    private static Dictionary<string, float> ReadCVars(EntityPlayer _member, IEnumerable<string> _names)
    {
        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in _names)
        {
            values[name] = _member.Buffs.GetCustomVar(name);
        }
        return values;
    }

    private static bool ValuesEqual(float _a, float _b) => Math.Abs(_a - _b) < 1e-6f;

    private static string DescribeGroup(IReadOnlyList<MinEventActionBase> _actions)
    {
        return string.Join("+", _actions.Select(a => a.GetType().Name.Replace("MinEventAction", string.Empty)).Distinct());
    }
}
