using System;
using System.Collections.Generic;
using System.Threading;

namespace Barotrauma
{
    internal sealed class HullGapUpdateBatch
    {
        // Most gap updates only advance a counter. Look for substantial recursive flooding
        // before paying for a graph traversal and a synchronous thread-pool dispatch.
        private const int MinimumGaps = 1536;
        private const int MinimumFloodSources = 16;
        private const int MinimumFloodWorkPerGroup = 8192;
        private const int TopologyIdleLimit = 120;
        private const int MinimumHulls = 32;
        private const int MinimumHullWaveSamples = 8192;
        private const int MinimumWaveSamplesPerJob = 512;
        private const int MaximumHullJobs = 8;
        private const int HullRetryDelay = 8;

        private sealed class Group
        {
            public readonly List<(Gap Gap, bool AtDamageDepth, int Order)> Gaps = new List<(Gap, bool, int)>();
            public readonly HashSet<Hull> CheckedHulls = new HashSet<Hull>();
            public int HullCount;
            public int PendingFloods;
        }

        private readonly Dictionary<MapEntity, int> nodeIndices = new Dictionary<MapEntity, int>(ReferenceEqualityComparer.Instance);
        private readonly List<(int Node, int Next)> edges = new List<(int, int)>();
        private readonly Queue<int> pending = new Queue<int>();
        private readonly List<Group> groups = new List<Group>();
        private readonly List<(MapEntity Entity, int LinkCount)> topology = new List<(MapEntity, int)>();
        private readonly List<MapEntity> topologyLinks = new List<MapEntity>();
        private readonly List<int> pendingFloods = new List<int>();
        private readonly Dictionary<Submarine, bool> damageDepth = new Dictionary<Submarine, bool>(ReferenceEqualityComparer.Instance);
        private int[] firstEdges = Array.Empty<int>();
        private int[] component = Array.Empty<int>();
        private long[] orderKeys = Array.Empty<long>();
        private int cachedHullCount;
        private int groupCount;
        private int idleFrames;
        private int framesBeforeWorkloadCheck;
        private List<Hull>[] hullJobs;
        private int hullRetryFrames;
#if CLIENT
        private (Gap Gap, float DeltaTime, float WaterVolume)[] particleEmissions = Array.Empty<(Gap, float, float)>();
#endif

        internal void InvalidateTopology()
        {
            if (topology.Count != 0) { ClearTopology(); }
        }

        internal bool TryUpdateHulls(List<Hull> hulls, List<Gap> gaps, float deltaTime, Camera cam, bool checkWorkload = true)
        {
            if (hulls.Count < (checkWorkload ? MinimumHulls : 2) || GetSimulationScope() is not { } work)
            {
                return false;
            }

            if (checkWorkload)
            {
                if (hullRetryFrames > 0 && cachedHullCount == hulls.Count && topology.Count == hulls.Count + gaps.Count)
                {
                    hullRetryFrames--;
                    return false;
                }
                hullRetryFrames = 0;

                long totalSamples = 0;
                foreach (Hull hull in hulls)
                {
                    totalSamples += hull?.ActiveWaveSamples ?? 0;
                    if (totalSamples >= MinimumHullWaveSamples) { break; }
                }
                if (totalSamples < MinimumHullWaveSamples) { return false; }
            }

            foreach (Hull hull in hulls)
            {
                // Fire and flora can read or change other hulls between water updates.
                if (hull == null || !hull.CanBatchWaterUpdate) { return false; }
            }

            if (!MatchesTopology(hulls, gaps) && !RebuildTopology(hulls, gaps)) { return false; }
            idleFrames = 0;
            hullJobs ??= CreateHullJobs(hulls);
            int substantialJobs = 0;
            foreach (var job in hullJobs)
            {
                long samples = 0;
                foreach (Hull hull in job)
                {
                    if (checkWorkload) { samples += hull.ActiveWaveSamples; }
                }
                if ((checkWorkload ? samples >= MinimumWaveSamplesPerJob : job.Count > 0) && ++substantialJobs == 2)
                {
                    break;
                }
            }
            if (substantialJobs < 2)
            {
                // A connected layout usually persists for many ticks. Serial processing is
                // safe while waiting to see whether topology or wave workload has changed.
                if (checkWorkload) { hullRetryFrames = HullRetryDelay - 1; }
                return false;
            }

            // Networking, decals and AI bookkeeping keep their original order. With fire and
            // flora excluded above, these updates do not depend on a different hull's waves.
            foreach (Hull hull in hulls)
            {
                hull.UpdateBeforeWater(deltaTime, cam);
            }
            work.RunParallel("Hull water simulation", 0, hullJobs.Length, index =>
            {
                foreach (Hull hull in hullJobs[index])
                {
                    hull.UpdateWater(deltaTime);
                }
            });
            return true;
        }

        private List<Hull>[] CreateHullJobs(List<Hull> hulls)
        {
            int count = Math.Min(hulls.Count, Math.Min(Environment.ProcessorCount, MaximumHullJobs));
            var jobs = new List<Hull>[count];
            for (int i = 0; i < count; i++) { jobs[i] = new List<Hull>(); }

            int isolatedIndex = 0;
            for (int index = 0; index < hulls.Count; index++)
            {
                // A component always stays together. Isolated hulls can share any job.
                int group = component[index] >= 0 ? component[index] : isolatedIndex++;
                jobs[group % count].Add(hulls[index]);
            }
            return jobs;
        }

        private static ThreadManager.WorkScope GetSimulationScope()
        {
            return ThreadManager.Current is { } threads && Thread.CurrentThread == threads.MainThread &&
                !threads.Process.Stopping.IsCancellationRequested && Environment.ProcessorCount >= 2
                ? threads.Process
                : null;
        }

        internal bool TryUpdate(List<Hull> hulls, List<Gap> gaps, float deltaTime,
            IReadOnlyList<int> orderedIndices = null, bool checkWorkload = true)
        {
            int minimumCount = checkWorkload ? MinimumGaps : 2;
            if (topology.Count == 0 && gaps.Count < minimumCount) { return false; }

            if (GetSimulationScope() is not { } work)
            {
                return false;
            }

            if (topology.Count > 0 &&
                (cachedHullCount != hulls.Count || topology.Count != hulls.Count + gaps.Count || ++idleFrames >= TopologyIdleLimit))
            {
                ClearTopology();
            }
            if (gaps.Count < minimumCount) { return false; }
            if (checkWorkload && framesBeforeWorkloadCheck > 0)
            {
                framesBeforeWorkloadCheck--;
                return false;
            }

            try
            {
                if (checkWorkload)
                {
                    for (int index = 0; index < gaps.Count; index++)
                    {
                        if (gaps[index]?.HasPendingFloodPropagation == true)
                        {
                            pendingFloods.Add(index);
                        }
                    }
                    if (pendingFloods.Count < MinimumFloodSources) { return false; }
                }

                foreach (Gap gap in gaps)
                {
                    if (gap == null || !gap.CanUpdateInParallel) { return false; }
                }
                if (!MatchesTopology(hulls, gaps) && !RebuildTopology(hulls, gaps)) { return false; }

                idleFrames = 0;
                if (groupCount < 2 || (checkWorkload && !HasEnoughWork(hulls.Count))) { return false; }

                UpdateGroups(work, hulls.Count, gaps, orderedIndices, deltaTime);
                if (checkWorkload)
                {
                    // These leaks just ran. Recheck when the first one can run again.
                    // Any newly active leak is still updated by the ordinary serial path.
                    int framesToSkip = 7;
                    foreach (int gap in pendingFloods)
                    {
                        framesToSkip = Math.Min(framesToSkip, gaps[gap].UpdatesUntilExteriorFlood - 1);
                    }
                    framesBeforeWorkloadCheck = framesToSkip;
                }
                return true;
            }
            finally
            {
                pendingFloods.Clear();
            }
        }

        private void UpdateGroups(ThreadManager.WorkScope work, int hullCount, List<Gap> gaps,
            IReadOnlyList<int> orderedIndices, float deltaTime)
        {
#if CLIENT
            if (particleEmissions.Length < gaps.Count)
            {
                particleEmissions = new (Gap, float, float)[gaps.Count];
            }
#endif
            try
            {
                if (orderedIndices == null)
                {
                    if (orderKeys.Length < gaps.Count) { orderKeys = new long[gaps.Count]; }
                    // The index breaks ties in the same order as LINQ's stable OrderBy.
                    for (int index = 0; index < gaps.Count; index++)
                    {
                        orderKeys[index] = ((long)Rand.Int(int.MaxValue) << 32) | (uint)index;
                    }
                    Array.Sort(orderKeys, 0, gaps.Count);
                    for (int index = 0; index < gaps.Count; index++)
                    {
                        int gap = unchecked((int)orderKeys[index]);
                        AddGap(gaps[gap], component[hullCount + gap], index);
                    }
                }
                else
                {
                    for (int index = 0; index < orderedIndices.Count; index++)
                    {
                        int gap = orderedIndices[index];
                        AddGap(gaps[gap], component[hullCount + gap], index);
                    }
                }
                work.RunParallel("Gap simulation", 0, groupCount, index =>
                {
                    Group group = groups[index];
                    foreach (var (gap, atDamageDepth, order) in group.Gaps)
                    {
#if CLIENT
                        if (gap.UpdateFlow(deltaTime, group.CheckedHulls, atDamageDepth) is float emissionDelta &&
                            gap.FlowTargetHull is { } target)
                        {
                            particleEmissions[order] = (gap, emissionDelta, target.WaterVolume);
                        }
#else
                        gap.UpdateWaterAndAir(deltaTime, group.CheckedHulls, atDamageDepth);
#endif
                    }
                });
#if CLIENT
                // Other gaps can change the target hull's water after this gap runs.
                // Replay in the original order for the shared particle budget and RNG.
                for (int index = 0; index < gaps.Count; index++)
                {
                    var (gap, emissionDelta, waterVolume) = particleEmissions[index];
                    gap?.EmitParticles(emissionDelta, waterVolume);
                }
#endif
            }
            finally
            {
#if CLIENT
                Array.Clear(particleEmissions, 0, gaps.Count);
#endif
                damageDepth.Clear();
                for (int index = 0; index < groupCount; index++)
                {
                    groups[index].Gaps.Clear();
                }
            }
        }

        private void AddGap(Gap gap, int group, int order)
        {
            bool atDamageDepth = false;
            if (gap.Submarine is { } sub && gap.linkedTo.Count == 1 && gap.Open > 0.0f &&
                gap.UpdatesUntilExteriorFlood <= 1)
            {
                // AtDamageDepth can lazily initialize the submarine's crush-depth cache.
                if (!damageDepth.TryGetValue(sub, out atDamageDepth))
                {
                    atDamageDepth = sub.AtDamageDepth;
                    damageDepth.Add(sub, atDamageDepth);
                }
            }
            groups[group].Gaps.Add((gap, atDamageDepth, order));
        }

        private bool HasEnoughWork(int hullCount)
        {
            for (int i = 0; i < groupCount; i++)
            {
                groups[i].PendingFloods = 0;
            }
            foreach (int gap in pendingFloods)
            {
                groups[component[hullCount + gap]].PendingFloods++;
            }
            int substantialGroups = 0;
            for (int i = 0; i < groupCount; i++)
            {
                Group group = groups[i];
                if ((long)group.HullCount * group.PendingFloods >= MinimumFloodWorkPerGroup &&
                    ++substantialGroups >= 2)
                {
                    return true;
                }
            }
            return false;
        }

        private bool MatchesTopology(List<Hull> hulls, List<Gap> gaps)
        {
            if (topology.Count == 0 || cachedHullCount != hulls.Count || topology.Count != hulls.Count + gaps.Count)
            {
                return false;
            }

            int node = 0;
            int link = 0;
            foreach (Hull hull in hulls)
            {
                if (!MatchesLinks(hull, hull?.ConnectedGaps, node++, ref link)) { return false; }
            }
            foreach (Gap gap in gaps)
            {
                if (!MatchesLinks(gap, gap?.linkedTo, node++, ref link)) { return false; }
            }
            return true;
        }

        private bool MatchesLinks<T>(MapEntity entity, List<T> links, int node, ref int link) where T : MapEntity
        {
            var previous = topology[node];
            if (!ReferenceEquals(entity, previous.Entity) || links == null || links.Count != previous.LinkCount)
            {
                return false;
            }
            foreach (T neighbor in links)
            {
                if (!ReferenceEquals(neighbor, topologyLinks[link++])) { return false; }
            }
            return true;
        }

        private bool RebuildTopology(List<Hull> hulls, List<Gap> gaps)
        {
            ClearTopology();
            if (!TryBuildGraph(hulls, gaps))
            {
                ClearTopology();
                return false;
            }

            int unconnectedGroup = -1;
            int nodeCount = hulls.Count + gaps.Count;
            for (int index = hulls.Count; index < nodeCount; index++)
            {
                if (component[index] != -1) { continue; }
                if (firstEdges[index] == -1)
                {
                    if (unconnectedGroup == -1)
                    {
                        unconnectedGroup = groupCount++;
                        InitializeGroup(unconnectedGroup, 0);
                    }
                    component[index] = unconnectedGroup;
                }
                else
                {
                    int group = groupCount++;
                    InitializeGroup(group, AssignComponent(index, group, hulls.Count));
                }
            }
            nodeIndices.Clear();
            edges.Clear();
            cachedHullCount = hulls.Count;
            return true;
        }

        private void InitializeGroup(int index, int hullCount)
        {
            if (index == groups.Count) { groups.Add(new Group()); }
            groups[index].HullCount = hullCount;
        }

        private void ClearTopology()
        {
            groupCount = 0;
            hullJobs = null;
            hullRetryFrames = 0;
            idleFrames = 0;
            framesBeforeWorkloadCheck = 0;
            nodeIndices.Clear();
            topology.Clear();
            topologyLinks.Clear();
            edges.Clear();
            pending.Clear();
        }

        private bool TryBuildGraph(List<Hull> hulls, List<Gap> gaps)
        {
            int nodeCount = hulls.Count + gaps.Count;
            if (firstEdges.Length < nodeCount)
            {
                int capacity = Math.Max(nodeCount, firstEdges.Length * 2);
                firstEdges = new int[capacity];
                component = new int[capacity];
            }
            Array.Fill(firstEdges, -1, 0, nodeCount);
            Array.Fill(component, -1, 0, nodeCount);

            for (int index = 0; index < hulls.Count; index++)
            {
                Hull hull = hulls[index];
                if (hull == null || !nodeIndices.TryAdd(hull, index)) { return false; }
                topology.Add((hull, hull.ConnectedGaps.Count));
                topologyLinks.AddRange(hull.ConnectedGaps);
            }
            for (int index = 0; index < gaps.Count; index++)
            {
                Gap gap = gaps[index];
                int node = hulls.Count + index;
                if (gap == null || gap.GetType() != typeof(Gap) || gap.linkedTo.Count > 2 || !nodeIndices.TryAdd(gap, node))
                {
                    return false;
                }
                topology.Add((gap, gap.linkedTo.Count));
                topologyLinks.AddRange(gap.linkedTo);
                foreach (MapEntity endpoint in gap.linkedTo)
                {
                    if (endpoint is not Hull || !nodeIndices.TryGetValue(endpoint, out int hullNode)) { return false; }
                    Connect(node, hullNode);
                }
            }
            // Docking and relinking can expose a reference on only one side. Include both sources.
            for (int index = 0; index < hulls.Count; index++)
            {
                foreach (Gap gap in hulls[index].ConnectedGaps)
                {
                    if (gap == null || !nodeIndices.TryGetValue(gap, out int gapNode)) { return false; }
                    if (!gap.linkedTo.Contains(hulls[index]))
                    {
                        Connect(index, gapNode);
                    }
                }
            }
            return true;
        }

        private void Connect(int first, int second)
        {
            edges.Add((second, firstEdges[first]));
            firstEdges[first] = edges.Count - 1;
            edges.Add((first, firstEdges[second]));
            firstEdges[second] = edges.Count - 1;
        }

        private int AssignComponent(int root, int group, int hullCount)
        {
            int connectedHulls = 0;
            component[root] = group;
            pending.Enqueue(root);
            while (pending.TryDequeue(out int node))
            {
                if (node < hullCount)
                {
                    connectedHulls++;
                }
                for (int index = firstEdges[node]; index != -1; index = edges[index].Next)
                {
                    int neighbor = edges[index].Node;
                    if (component[neighbor] != -1) { continue; }
                    component[neighbor] = group;
                    pending.Enqueue(neighbor);
                }
            }
            return connectedHulls;
        }
    }
}
