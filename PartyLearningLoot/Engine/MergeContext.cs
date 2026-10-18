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
/// reads <see cref="Active"/> first and returns immediately when no merge is running, so outside of a party spawn
/// the mod costs one static field read per evaluation. Activations never nest; all fields are main-thread only.
/// </summary>
internal static class MergeContext
{
    public const int MaxMembers = 8;

    public static bool Active;
    public static bool InEval;
    public static bool SuppressMerge;
    public static bool ForceApply;
    public static bool InAudit;
    public static bool TraceAlways;
    public static EntityPlayer Opener;
    public static int OwnerThread = -1;
    public static bool DryRun;
    public static LootOpenKind Kind;
    public static string ContainerName;

    public static readonly MemberSlot[] Members = CreateSlots();
    public static int MemberCount;

    public static readonly SpawnTrace Trace = new SpawnTrace();

    // ----- the opener's current progression evaluation (Progression.ModifyValue for LootProb) -----

    /// <summary>True while the opener's Progression.ModifyValue(LootProb) runs; only then do perk evaluations merge.</summary>
    public static bool ProgressionActive;

    /// <summary>Base value before the progression evaluation (b₀): member bonuses are counted at this weight.</summary>
    public static float ProgressionBase;

    /// <summary>Sum of the per-perk extras of the current progression evaluation.</summary>
    public static float ProgressionExtra;

    // Numbers of the last finished progression evaluation, read by the getProbability audit.
    public static bool LastProgressionValid;
    public static float LastBaseRef;
    public static float LastBaseFinal;
    public static float LastExtra;
    public static float LastApplied;

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

    public static void RecordProgression(float _baseRef, float _baseFinal, float _extra, float _applied)
    {
        LastProgressionValid = true;
        LastBaseRef = _baseRef;
        LastBaseFinal = _baseFinal;
        LastExtra = _extra;
        LastApplied = _applied;
    }

    public static void ResetLastProgression()
    {
        LastProgressionValid = false;
        LastBaseRef = 0f;
        LastBaseFinal = 0f;
        LastExtra = 0f;
        LastApplied = 0f;
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
        SuppressMerge = false;
        ForceApply = false;
        InAudit = false;
        EndProgression();
        ResetLastProgression();
        Trace.Begin(_opener, _snapshot, _kind, _containerName, _dryRun);
        Active = true;
        return new ActivationToken(true);
    }

    public static void Deactivate(ActivationToken _token)
    {
        if (!_token.Activated)
        {
            return;
        }
        Active = false;
        InEval = false;
        SuppressMerge = false;
        ForceApply = false;
        InAudit = false;
        TraceAlways = false;
        EndProgression();
        ResetLastProgression();
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
        return !Active && !InEval && !SuppressMerge && !ForceApply && !InAudit && !TraceAlways && !ProgressionActive && Opener == null && MemberCount == 0;
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
