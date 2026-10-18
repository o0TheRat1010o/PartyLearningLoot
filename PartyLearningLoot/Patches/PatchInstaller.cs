using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PartyLearningLoot.Core;
using PartyLearningLoot.Engine;

namespace PartyLearningLoot.Patches;

/// <summary>One Harmony patch: a resolved game method plus our prefix/postfix/finalizer.</summary>
internal sealed class PatchSpec
{
    public string Label;
    public Func<MethodBase> Resolve;
    public MethodInfo Prefix;
    public MethodInfo Postfix;
    public MethodInfo Finalizer;
    public int PrefixPriority = Priority.Normal;
    public int PostfixPriority = Priority.Normal;

    public IEnumerable<MethodInfo> PatchMethods => new[] { Prefix, Postfix, Finalizer }.Where(m => m != null);
}

internal sealed class PatchGroupStatus
{
    public string Name;
    public string HarmonyId;
    public string State = "not_installed";
    public string Error;
    public readonly List<string> Targets = new List<string>();
    public readonly List<MethodBase> Methods = new List<MethodBase>();

    public bool Installed => State == "installed";
}

/// <summary>
/// Installs the patch groups. Each group is all-or-nothing: every target is resolved with its exact parameter types and
/// every parameter our patch methods bind by name must exist on the target; if anything fails (now or while patching),
/// the group's Harmony instance is unpatched completely and the game keeps its vanilla behaviour for that feature.
/// </summary>
internal static class PatchInstaller
{
    public const string LootMergeGroup = "LootMerge";
    public const string SharedReadingGroup = "SharedReading";
    public const string LootMergeHarmonyId = "studio.softdot.partylearningloot.lootmerge";
    public const string SharedReadingHarmonyId = "studio.softdot.partylearningloot.sharedreading";

    public static readonly List<PatchGroupStatus> Groups = new List<PatchGroupStatus>();

    public static PatchGroupStatus Find(string _name) => Groups.FirstOrDefault(g => g.Name == _name);

    public static void InstallAll(ModSettings _settings)
    {
        Groups.Clear();
        Groups.Add(InstallGroup(LootMergeGroup, LootMergeHarmonyId, LootMergeSpecs()));
        if (_settings.SharedReading)
        {
            Groups.Add(InstallGroup(SharedReadingGroup, SharedReadingHarmonyId, SharedReadingSpecs()));
        }
        else
        {
            Groups.Add(new PatchGroupStatus { Name = SharedReadingGroup, HarmonyId = SharedReadingHarmonyId, State = "disabled" });
        }
        SharedReadingService.Installed = Find(SharedReadingGroup)?.Installed == true;
    }

    // ----- group definitions ------------------------------------------------------------------

    private static List<PatchSpec> LootMergeSpecs()
    {
        Type fastTags = typeof(FastTags<TagGroup.Global>);
        Type floatRef = typeof(float).MakeByRefType();
        return new List<PatchSpec>
        {
            new PatchSpec
            {
                Label = "LootManager.LootContainerOpened",
                Resolve = () => AccessTools.DeclaredMethod(typeof(LootManager), nameof(LootManager.LootContainerOpened), new[] { typeof(TEFeatureStorage), typeof(int), fastTags }),
                Prefix = Method(typeof(LootManagerPatches), nameof(LootManagerPatches.ContainerOpenedPrefix)),
                Finalizer = Method(typeof(LootManagerPatches), nameof(LootManagerPatches.OpenedFinalizer))
            },
            new PatchSpec
            {
                Label = "LootManager.LootBagOpened",
                Resolve = () => AccessTools.DeclaredMethod(typeof(LootManager), nameof(LootManager.LootBagOpened), new[] { typeof(Bag), typeof(Entity), typeof(int) }),
                Prefix = Method(typeof(LootManagerPatches), nameof(LootManagerPatches.BagOpenedPrefix)),
                Finalizer = Method(typeof(LootManagerPatches), nameof(LootManagerPatches.OpenedFinalizer))
            },
            new PatchSpec
            {
                Label = "LootContainer.Spawn",
                Resolve = () => AccessTools.DeclaredMethod(typeof(LootContainer), nameof(LootContainer.Spawn), new[]
                {
                    typeof(GameRandom), typeof(int), typeof(float), typeof(float), typeof(EntityPlayer), fastTags, typeof(bool), typeof(bool), typeof(bool)
                }),
                Prefix = Method(typeof(LootContainerSpawnPatch), nameof(LootContainerSpawnPatch.Prefix)),
                Postfix = Method(typeof(LootContainerSpawnPatch), nameof(LootContainerSpawnPatch.Postfix)),
                Finalizer = Method(typeof(LootContainerSpawnPatch), nameof(LootContainerSpawnPatch.Finalizer))
            },
            new PatchSpec
            {
                Label = "Progression.ModifyValue",
                Resolve = () => AccessTools.DeclaredMethod(typeof(Progression), nameof(Progression.ModifyValue), new[]
                {
                    typeof(PassiveEffects), floatRef, floatRef, fastTags
                }),
                Prefix = Method(typeof(ProgressionModifyValuePatch), nameof(ProgressionModifyValuePatch.Prefix)),
                Postfix = Method(typeof(ProgressionModifyValuePatch), nameof(ProgressionModifyValuePatch.Postfix)),
                Finalizer = Method(typeof(ProgressionModifyValuePatch), nameof(ProgressionModifyValuePatch.Finalizer)),
                PrefixPriority = Priority.First,
                PostfixPriority = Priority.Last
            },
            new PatchSpec
            {
                Label = "ProgressionClass.ModifyValue",
                Resolve = () => AccessTools.DeclaredMethod(typeof(ProgressionClass), nameof(ProgressionClass.ModifyValue), new[]
                {
                    typeof(EntityAlive), typeof(ProgressionValue), typeof(PassiveEffects), floatRef, floatRef, fastTags
                }),
                Prefix = Method(typeof(ProgressionClassModifyValuePatch), nameof(ProgressionClassModifyValuePatch.Prefix)),
                Postfix = Method(typeof(ProgressionClassModifyValuePatch), nameof(ProgressionClassModifyValuePatch.Postfix)),
                PrefixPriority = Priority.First,
                PostfixPriority = Priority.Last
            },
            new PatchSpec
            {
                Label = "LootContainer.getProbability",
                Resolve = () => AccessTools.DeclaredMethod(typeof(LootContainer), "getProbability", new[]
                {
                    typeof(EntityPlayer), typeof(LootContainer.LootEntry), typeof(float), typeof(bool)
                }),
                Prefix = Method(typeof(GetProbabilityAuditPatch), nameof(GetProbabilityAuditPatch.Prefix)),
                Postfix = Method(typeof(GetProbabilityAuditPatch), nameof(GetProbabilityAuditPatch.Postfix)),
                PrefixPriority = Priority.First,
                PostfixPriority = Priority.Last
            }
        };
    }

    private static List<PatchSpec> SharedReadingSpecs()
    {
        return new List<PatchSpec>
        {
            new PatchSpec
            {
                Label = "WorldStaticData.CachePatchedXml",
                Resolve = () => typeof(WorldStaticData).GetMethods(AccessTools.all)
                    .SingleOrDefault(m => m.Name == "CachePatchedXml"
                                          && m.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(new[]
                                          {
                                              "XmlLoadInfo", "XmlFile", "MicroStopwatch", "MemoryStream", "DeflateOutputStream", "Action"
                                          })),
                Prefix = Method(typeof(XmlCachePatch), nameof(XmlCachePatch.Prefix))
            },
            new PatchSpec
            {
                Label = "NetPackageAddRemoveBuff.ProcessPackage",
                Resolve = () => AccessTools.DeclaredMethod(typeof(NetPackageAddRemoveBuff), nameof(NetPackageAddRemoveBuff.ProcessPackage), new[] { typeof(World), typeof(GameManager) }),
                Prefix = Method(typeof(SharedReadMarkerPatches), nameof(SharedReadMarkerPatches.AddRemoveBuffPrefix)),
                PrefixPriority = Priority.First
            },
            new PatchSpec
            {
                Label = "EntityBuffs.AddBuff",
                Resolve = () => AccessTools.DeclaredMethod(typeof(EntityBuffs), nameof(EntityBuffs.AddBuff), new[]
                {
                    typeof(string), typeof(Vector3i), typeof(int), typeof(bool), typeof(bool), typeof(float)
                }),
                Prefix = Method(typeof(SharedReadMarkerPatches), nameof(SharedReadMarkerPatches.AddBuffPrefix)),
                PrefixPriority = Priority.First
            },
            new PatchSpec
            {
                Label = "NetPackagePlayerStats.ProcessPackage",
                Resolve = () => AccessTools.DeclaredMethod(typeof(NetPackagePlayerStats), nameof(NetPackagePlayerStats.ProcessPackage), new[] { typeof(World), typeof(GameManager) }),
                Postfix = Method(typeof(ConfirmationSyncPatches), nameof(ConfirmationSyncPatches.PlayerStatsPostfix))
            },
            new PatchSpec
            {
                Label = "NetPackageEntityStatsBuff.ProcessPackage",
                Resolve = () => AccessTools.DeclaredMethod(typeof(NetPackageEntityStatsBuff), nameof(NetPackageEntityStatsBuff.ProcessPackage), new[] { typeof(World), typeof(GameManager) }),
                Postfix = Method(typeof(ConfirmationSyncPatches), nameof(ConfirmationSyncPatches.StatsBuffPostfix))
            }
        };
    }

    // ----- installation -------------------------------------------------------------------------

    private static PatchGroupStatus InstallGroup(string _name, string _harmonyId, List<PatchSpec> _specs)
    {
        var status = new PatchGroupStatus { Name = _name, HarmonyId = _harmonyId };
        var resolved = new List<KeyValuePair<PatchSpec, MethodBase>>();
        foreach (PatchSpec spec in _specs)
        {
            MethodBase target;
            try
            {
                target = spec.Resolve();
            }
            catch (Exception e)
            {
                return Fail(status, $"{spec.Label}: resolving the target threw {e.GetType().Name}: {e.Message}");
            }
            if (target == null)
            {
                return Fail(status, $"{spec.Label}: target method not found (game update?)");
            }
            foreach (MethodInfo patch in spec.PatchMethods)
            {
                string bindingError = ValidateBinding(target, patch);
                if (bindingError != null)
                {
                    return Fail(status, $"{spec.Label}: {bindingError}");
                }
            }
            resolved.Add(new KeyValuePair<PatchSpec, MethodBase>(spec, target));
            status.Targets.Add(spec.Label);
        }

        var harmony = new Harmony(_harmonyId);
        try
        {
            foreach (KeyValuePair<PatchSpec, MethodBase> pair in resolved)
            {
                PatchSpec spec = pair.Key;
                harmony.Patch(pair.Value,
                    prefix: spec.Prefix != null ? new HarmonyMethod(spec.Prefix, spec.PrefixPriority) : null,
                    postfix: spec.Postfix != null ? new HarmonyMethod(spec.Postfix, spec.PostfixPriority) : null,
                    finalizer: spec.Finalizer != null ? new HarmonyMethod(spec.Finalizer) : null);
                status.Methods.Add(pair.Value);
            }
        }
        catch (Exception e)
        {
            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception unpatchError)
            {
                ModLog.Error($"Unpatching group {_name} after a failure also failed.", unpatchError, "unpatch");
            }
            status.Methods.Clear();
            ModLog.Error($"Patch group {_name} could not be installed and was rolled back; this feature stays vanilla.", e, "patch_install");
            return Fail(status, e.GetType().Name + ": " + e.Message, _logError: false);
        }

        status.State = "installed";
        ModLog.Info($"Patch group {_name} installed ({status.Methods.Count} methods).");
        return status;
    }

    /// <summary>
    /// Every parameter of a patch method that is not a Harmony special (__instance, __state, ...) must exist on the
    /// target with a compatible type; this catches renamed or retyped game parameters before anything is patched.
    /// </summary>
    public static string ValidateBinding(MethodBase _target, MethodInfo _patch)
    {
        ParameterInfo[] targetParameters = _target.GetParameters();
        foreach (ParameterInfo parameter in _patch.GetParameters())
        {
            if (parameter.Name.StartsWith("__", StringComparison.Ordinal))
            {
                continue;
            }
            ParameterInfo match = targetParameters.FirstOrDefault(p => p.Name == parameter.Name);
            if (match == null)
            {
                return $"parameter '{parameter.Name}' of {_patch.DeclaringType?.Name}.{_patch.Name} does not exist on the target";
            }
            Type expected = match.ParameterType.IsByRef ? match.ParameterType.GetElementType() : match.ParameterType;
            Type actual = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
            if (expected != actual && !(actual != null && actual.IsAssignableFrom(expected)))
            {
                return $"parameter '{parameter.Name}' has type {expected?.Name} on the target, the patch expects {actual?.Name}";
            }
            if (parameter.ParameterType.IsByRef && !match.ParameterType.IsByRef)
            {
                return $"parameter '{parameter.Name}' is passed by reference in the patch but not on the target";
            }
        }
        return null;
    }

    /// <summary>True when Harmony reports our patches on every target of the group (used by the selftest).</summary>
    public static bool VerifyApplied(PatchGroupStatus _group, out string _problem)
    {
        _problem = null;
        foreach (MethodBase method in _group.Methods)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            bool ours = info != null && info.Owners.Contains(_group.HarmonyId);
            if (!ours)
            {
                _problem = $"{method.DeclaringType?.Name}.{method.Name} has no patch from {_group.HarmonyId}";
                return false;
            }
        }
        return true;
    }

    private static PatchGroupStatus Fail(PatchGroupStatus _status, string _error, bool _logError = true)
    {
        _status.State = "failed";
        _status.Error = _error;
        if (_logError)
        {
            ModLog.Error($"Patch group {_status.Name} not installed: {_error}. This feature stays vanilla.", null, "patch_install");
        }
        return _status;
    }

    private static MethodInfo Method(Type _type, string _name)
    {
        return AccessTools.DeclaredMethod(_type, _name) ?? throw new MissingMethodException(_type.Name, _name);
    }
}
