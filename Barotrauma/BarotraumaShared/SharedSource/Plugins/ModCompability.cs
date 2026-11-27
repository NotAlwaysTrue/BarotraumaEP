#nullable enable

using System.Reflection;
using HarmonyLib;

namespace Barotrauma.Plugins;

internal static class ModCompability
{
    // These accessors can execute inside either water simulation batch.
    private static readonly MethodBase?[] sharedTargets =
    [
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.Volume)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.WaterVolume)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.LethalPressure)),
        AccessTools.PropertySetter(typeof(Hull), nameof(Hull.LethalPressure)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.Oxygen)),
        AccessTools.PropertySetter(typeof(Hull), nameof(Hull.Oxygen)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.IsRoomToRoom)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.IsHorizontal)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.Open)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.Rect)),
        AccessTools.PropertyGetter(typeof(MapEntity), nameof(MapEntity.Rect))
    ];

    // Batching also bypasses Update and moves the pre-water work ahead of the worker pass.
    private static readonly MethodBase?[] hullTargets =
    [
        AccessTools.DeclaredMethod(typeof(Hull), nameof(Hull.Update), [typeof(float), typeof(Camera)]),
        AccessTools.DeclaredMethod(typeof(Hull), nameof(Hull.UpdateBeforeWater)),
        AccessTools.DeclaredMethod(typeof(Hull), "UpdateProjSpecific"),
        AccessTools.DeclaredMethod(typeof(Hull), nameof(Hull.UpdateWater)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.CanBatchWaterUpdate)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.ActiveWaveSamples)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.WaterPercentage))
    ];

    private static readonly MethodBase?[] gapTargets =
    [
        AccessTools.DeclaredMethod(typeof(Gap), nameof(Gap.Update), [typeof(float), typeof(Camera)]),
        AccessTools.DeclaredMethod(typeof(Gap), nameof(Gap.UpdateWaterAndAir)),
        AccessTools.DeclaredMethod(typeof(Gap), nameof(Gap.UpdateFlow)),
        AccessTools.DeclaredMethod(typeof(Gap), "UpdateOxygen"),
        AccessTools.DeclaredMethod(typeof(Gap), "UpdateRoomToRoom"),
        AccessTools.DeclaredMethod(typeof(Gap), "UpdateRoomToOut"),
        AccessTools.DeclaredMethod(typeof(Gap), "GetWaterFlowFromOutside"),
        AccessTools.DeclaredMethod(typeof(Gap), "SimulateWaterFlowFromOutsideToConnectedHulls"),
        AccessTools.DeclaredMethod(typeof(Gap), "SimulateWaterFlowFromOutsideToConnectedHullsRecursive"),
        AccessTools.DeclaredMethod(typeof(Gap), "RefreshOverlappingGaps"),
        AccessTools.DeclaredMethod(typeof(Gap), nameof(Gap.RefreshOutsideCollider)),
        AccessTools.DeclaredMethod(typeof(Gap), nameof(Gap.GetOtherLinkedHull)),
        AccessTools.DeclaredMethod(typeof(Gap), nameof(Gap.ResetWaterFlowThisFrame)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.CanUpdateInParallel)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.UpdatesUntilExteriorFlood)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.HasPendingFloodPropagation)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.Size)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.PressureDistributionSpeed)),
        AccessTools.PropertyGetter(typeof(MapEntity), nameof(MapEntity.IsDisabled)),
        AccessTools.PropertyGetter(typeof(MapEntity), nameof(MapEntity.WorldRect)),
        AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.Submarine)),
        AccessTools.PropertyGetter(typeof(Submarine), nameof(Submarine.Position)),
        AccessTools.PropertyGetter(typeof(Submarine), nameof(Submarine.AtDamageDepth)),
        AccessTools.DeclaredMethod(typeof(Hull), nameof(Hull.GetWaveIndex), [typeof(float)]),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.Rect)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.Surface)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.WorldSurface)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.WaveY)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.WaveVel)),
        AccessTools.PropertySetter(typeof(Hull), nameof(Hull.WaterVolume)),
        AccessTools.PropertyGetter(typeof(Hull), nameof(Hull.Pressure)),
        AccessTools.PropertySetter(typeof(Hull), nameof(Hull.Pressure)),
#if CLIENT
        // The client batch defers particle emission until the worker pass has finished.
        AccessTools.DeclaredMethod(typeof(Gap), nameof(Gap.EmitParticles)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.FlowTargetHull)),
        AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.LerpedFlowForce)),
        AccessTools.PropertyGetter(typeof(MapEntity), nameof(MapEntity.Position)),
#endif
    ];

    internal static bool CanRunHullUpdatesInParallel() => !HasPatches(hullTargets) && !HasPatches(sharedTargets);

    internal static bool CanRunGapUpdatesInParallel() => !HasPatches(gapTargets) && !HasPatches(sharedTargets);

    private static bool HasPatches(MethodBase?[] targets)
    {
        // Resolve method identities once, but read patch state at dispatch time to support mod reloads.
        foreach (MethodBase? target in targets)
        {
            if (target == null || Harmony.GetPatchInfo(target)?.Owners.Count > 0) { return true; }
        }
        return false;
    }
}
