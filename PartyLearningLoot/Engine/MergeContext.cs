using System.Threading;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>One active party member during a merge activation (pre-allocated and reused).</summary>
internal sealed class MemberSlot
{
    public EntityPlayer Entity;
    public int EntityId;
    public string Name;
    public float Distance;
    public readonly MinEventParams ContextSnapshot = new MinEventParams();
    public int Evaluations;
    public int Errors;

    public void Clear()
    {
        Entity = null;
        EntityId = -1;
        Name = null;
        Distance = 0f;
        Evaluations = 0;
        Errors = 0;
        EventParamGuard.CopyAll(Empty, ContextSnapshot);
    }

    private static readonly MinEventParams Empty = new MinEventParams();
}

/// <summary>Restores the previous activation state; returned by <see cref="MergeContext.Activate"/>.</summary>
internal readonly struct ActivationToken
{
    public readonly bool Activated;

    public ActivationToken(bool _activated)
    {
        Activated = _activated;
    }
}

/// <summary>
/// Static state of the current merge activation. The hot path (every ProgressionClass.ModifyValue call in the game)
/// reads <see cref="Planning"/> first and returns immediately when no bonus plan is being made, so outside of a party
/// spawn the mod costs one static field read per evaluation. Activations never nest; all fields are main-thread only.
///
/// The game's own loot weights are never changed. While the bonus planner probes an entry (<see cref="Planning"/>) the
/// perk evaluations of the opener are observed: for each perk the members' versions are evaluated and the extra weight a
/// party member's perk would add is recorded in <see cref="ProbeGain"/>.
/// </summary>
internal static class MergeContext
{
    public const int MaxMembers = 8;

    public static bool Active;
    public static bool InEval;
    public static bool InAudit;
    public static bool TraceAlways;

    /// <summary>True while the planner probes one entry: only then are the opener's perk evaluations observed.</summary>
    public static bool Planning;
    public static EntityPlayer Opener;
    public static int OwnerThread = -1;
    public static bool DryRun;
    public static LootOpenKind Kind;
    public static string ContainerName;

    public static readonly MemberSlot[] Members = CreateSlots();
    public static int MemberCount;

    public static readonly SpawnTrace Trace = new SpawnTrace();

    // ----- the opener's current progression evaluation (Progression.ModifyValue for LootProb) -----

    /// <summary>True while the opener's Progression.ModifyValue(LootProb) runs inside a probe; only then do perk evaluations merge.</summary>
    public static bool ProgressionActive;

    /// <summary>Base value before the progression evaluation (b₀): member bonuses are counted at this weight.</summary>
    public static float ProgressionBase;

    /// <summary>Sum of the per-perk extras of the current progression evaluation.</summary>
    public static float ProgressionExtra;

    // ----- the probe in progress ---------------------------------------------------------------

    /// <summary>Progression evaluations finished during the probe.</summary>
    public static int ProbeFrames;

    /// <summary>Extra weight of the probe: Σ extra · b₀ over its progression evaluations (before the sandbox factor).</summary>
    public static float ProbeGain;

    /// <summary>Σ extra of the probe.</summary>
    public static float ProbeExtra;

    public static float ProbeBaseRef = float.NaN;
    public static float ProbeBaseFinal = float.NaN;

    public static void BeginProbe()
    {
        ProbeFrames = 0;
        ProbeGain = 0f;
        ProbeExtra = 0f;
        ProbeBaseRef = float.NaN;
        ProbeBaseFinal = float.NaN;
        EndProgression();
        Planning = true;
    }

    public static void EndProbe()
    {
        Planning = false;
        EndProgression();
    }

    public static void BeginProgression(float _baseRef)
    {
        ProgressionActive = true;
        ProgressionBase = _baseRef;
        ProgressionExtra = 0f;
    }

    public static void EndProgression()
    {
        ProgressionActive = false;
        ProgressionExtra = 0f;
    }

    /// <summary>Closes one progression evaluation of the probe: adds its extra weight and remembers the numbers.</summary>
    public static void RecordProbe(float _baseRef, float _baseFinal, float _extra, float _gain)
    {
        ProbeFrames++;
        ProbeGain += _gain;
        ProbeExtra += _extra;
        ProbeBaseRef = _baseRef;
        ProbeBaseFinal = _baseFinal;
    }

    /// <summary>Activates merging for the opener with the snapshot's members. Fails when already active or empty.</summary>
    public static ActivationToken Activate(EntityPlayer _opener, PartySnapshot _snapshot, LootOpenKind _kind, string _containerName, bool _dryRun)
    {
        if (Active || _opener == null || _snapshot == null || _snapshot.Members.Count == 0)
        {
            return new ActivationToken(false);
        }
        MemberCount = 0;
        foreach (PartyMember member in _snapshot.Members)
        {
            if (MemberCount >= MaxMembers)
            {
                break;
            }
            MemberSlot slot = Members[MemberCount++];
            slot.Clear();
            slot.Entity = member.Entity;
            slot.EntityId = member.Candidate.EntityId;
            slot.Name = member.Candidate.Name;
            slot.Distance = member.Candidate.Distance;
        }
        Opener = _opener;
        OwnerThread = Thread.CurrentThread.ManagedThreadId;
        Kind = _kind;
        ContainerName = _containerName;
        DryRun = _dryRun;
        InEval = false;
        InAudit = false;
        Planning = false;
        EndProgression();
        Trace.Begin(_opener, _snapshot, _kind, _containerName, _dryRun);
        Active = true;
        return new ActivationToken(true);
    }

    /// <summary>
    /// Lets the bonus engine watch the spawn that follows (the activation must be on). The seed of the engine's private
    /// random streams: production derives it from a counter, the clock and the opener, never from the game's streams.
    /// </summary>
    public static void ArmBonus(int _seed)
    {
        GameCatalog catalog = GameCatalog.Current;
        BonusEngine.Begin(catalog?.ListIndex, GameWeightSource.Instance, Trace.Bonus, _seed, DryRun);
    }

    public static void Deactivate(ActivationToken _token)
    {
        if (!_token.Activated)
        {
            return;
        }
        BonusEngine.End();
        Active = false;
        InEval = false;
        InAudit = false;
        TraceAlways = false;
        Planning = false;
        EndProgression();
        Opener = null;
        OwnerThread = -1;
        for (int i = 0; i < MemberCount; i++)
        {
            Members[i].Clear();
        }
        MemberCount = 0;
        MergeEngine.ClearActivationCache();
    }

    /// <summary>True when the context holds no state (checked after every spawn as an invariant).</summary>
    public static bool IsClean()
    {
        return !Active && !InEval && !InAudit && !TraceAlways && !Planning && !ProgressionActive && Opener == null && MemberCount == 0 && BonusEngine.IsIdle;
    }

    /// <summary>Emergency reset (world shutdown, reload).</summary>
    public static void Reset()
    {
        Deactivate(new ActivationToken(true));
    }

    private static MemberSlot[] CreateSlots()
    {
        var slots = new MemberSlot[MaxMembers];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = new MemberSlot();
        }
        return slots;
    }
}
