using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FarseerPhysics;
using HarmonyLib;
using Lidgren.Network;
using Microsoft.Xna.Framework;

namespace Barotrauma.Networking;

// Shared world data is captured once per send tick. Packet writing starts after every worker has joined.
internal sealed class PositionUpdateBatch
{
    internal sealed class Selection
    {
        internal readonly List<(Entity Entity, bool ResetTimestamp)> Due = new();
        internal bool Ready;

        internal void Clear()
        {
            Due.Clear();
            Ready = false;
        }
    }

    private readonly record struct Viewer(Client Client, Character Character, Vector2 Position, Vector2? ViewTarget, bool Spectating)
    {
        internal static Viewer Capture(Client client)
        {
            var character = client.Character;
            var spectate = client.SpectatePos;
            return new(client, character, spectate ?? character.WorldPosition, character?.ViewTarget?.WorldPosition, spectate.HasValue);
        }
    }

    private readonly record struct CharacterTarget(Character Entity, Vector2 Position, Vector2? ViewTarget, float RangeSquared, bool SlowCorpse);
    private readonly record struct ItemTarget(Item Entity, Vector2 Position, float Interval);

    private readonly List<Viewer> viewers = new();
    private readonly List<CharacterTarget> characters = new();
    private readonly List<Submarine> submarines = new();
    private readonly List<ItemTarget> items = new();
    private readonly HashSet<Entity> alreadyQueued = new();
    private double now;
    private bool prepared;

    private static readonly MethodInfo characterInterval = AccessTools.Method(typeof(Character), nameof(Character.GetPositionUpdateInterval));
    private static readonly MethodInfo itemInterval = AccessTools.Method(typeof(Item), nameof(Item.GetPositionUpdateInterval));

    internal void Prepare(IReadOnlyList<Client> clients)
    {
        now = NetTime.Now;
        foreach (var client in clients)
        {
            if (client.InGame && !client.NeedsMidRoundSync) { viewers.Add(Viewer.Capture(client)); }
        }
        if (viewers.Count == 0) { return; }

        foreach (var c in Character.CharacterList)
        {
            if (!c.Enabled) { continue; }
            characters.Add(new(c, c.WorldPosition, c.ViewTarget != c ? c.ViewTarget?.WorldPosition : null,
                MathUtils.Pow2(c.Params.DisableDistance), c.IsDead && !c.AnimController.IsDraggedWithRope));
        }
        foreach (var sub in Submarine.Loaded)
        {
            if (sub.Info.IsOutpost || sub.PhysicsBody == null || sub.PhysicsBody.BodyType == BodyType.Static) { continue; }
            if (!sub.DockedTo.Any(other => other.ID < sub.ID)) { submarines.Add(sub); }
        }
        foreach (var item in Item.ItemList)
        {
            float interval = item.PositionUpdateInterval;
            if (interval == float.PositiveInfinity || item.DelayPositionUpdatesUntil > now) { continue; }
            if (item.body == null || item.ParentInventory != null) { interval = float.PositiveInfinity; }
            items.Add(new(item, interval == float.PositiveInfinity ? default : item.WorldPosition, interval));
        }

        // Small batches and modded interval hooks use the public interval methods on the server thread.
        const int minimumParallelChecks = 256;
        if (viewers.Count < 2 || (long)viewers.Count * (characters.Count + items.Count) < minimumParallelChecks ||
            Harmony.GetPatchInfo(characterInterval)?.Owners.Count > 0 || Harmony.GetPatchInfo(itemInterval)?.Owners.Count > 0)
        {
            prepared = true;
            return;
        }
        ThreadManager.Instance.Process.RunParallel("Select client position updates", 0, viewers.Count, index =>
        {
            var viewer = viewers[index];
            Select(viewer, useSnapshotIntervals: true);
            viewer.Client.PositionSelection.Ready = true;
        });
        prepared = true;
    }

    private void Select(Viewer viewer, bool useSnapshotIntervals)
    {
        var client = viewer.Client;
        var due = client.PositionSelection.Due;
        foreach (var target in characters)
        {
            float distanceSquared = Character.GetPositionUpdateDistanceSquared(viewer.Position, viewer.ViewTarget, target.Position, target.ViewTarget);
            // Two remote cameras can bring an entity into range. Only the three original distances set its send rate.
            float visibilityDistance = distanceSquared;
            if (viewer.Spectating)
            {
                visibilityDistance = Character.GetPositionUpdateDistanceSquared(viewer.Position, null, target.Position, target.ViewTarget);
            }
            else if (viewer.ViewTarget is { } from && target.ViewTarget is { } to)
            {
                visibilityDistance = Math.Min(visibilityDistance, Vector2.DistanceSquared(from, to));
            }
            if (!(viewer.Spectating && viewer.Character == target.Entity) && visibilityDistance >= target.RangeSquared) { continue; }
            float interval = useSnapshotIntervals
                ? Character.CalculatePositionUpdateInterval(MathF.Sqrt(distanceSquared), target.SlowCorpse)
                : target.Entity.GetPositionUpdateInterval(client);
            AddIfDue(target.Entity, interval);
        }
        foreach (var sub in submarines) { due.Add((sub, false)); }
        foreach (var item in items)
        {
            float interval = useSnapshotIntervals
                ? Item.CalculatePositionUpdateInterval(item.Interval, viewer.Spectating ? null : Vector2.DistanceSquared(viewer.Position, item.Position))
                : item.Entity.GetPositionUpdateInterval(client);
            AddIfDue(item.Entity, interval);
        }

        void AddIfDue(Entity entity, float interval)
        {
            client.PositionUpdateLastSent.TryGetValue(entity, out float lastSent);
            bool reset = lastSent > now;
            if (!reset && lastSent > now - interval) { return; }
            due.Add((entity, reset));
        }
    }

    internal void EnqueueFor(Client client)
    {
        if (!prepared) { return; }
        var selection = client.PositionSelection;
        try
        {
            if (!selection.Ready) { Select(Viewer.Capture(client), useSnapshotIntervals: false); }
            if (selection.Due.Count == 0) { return; }

            // Preserve backlog and insertion order without repeatedly scanning the pending queue.
            alreadyQueued.Clear();
            alreadyQueued.UnionWith(client.PendingPositionUpdates);
            foreach (var (entity, resetTimestamp) in selection.Due)
            {
                if (resetTimestamp) { client.PositionUpdateLastSent.Remove(entity); }
                if (alreadyQueued.Add(entity)) { client.PendingPositionUpdates.Enqueue(entity); }
            }
        }
        finally
        {
            selection.Clear();
        }
    }

    internal void Clear()
    {
        prepared = false;
        foreach (var viewer in viewers) { viewer.Client.PositionSelection.Clear(); }
        viewers.Clear();
        characters.Clear();
        submarines.Clear();
        items.Clear();
        alreadyQueued.Clear();
    }
}
