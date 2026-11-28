using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Barotrauma.Lights;

internal sealed class LosGeometryBatch
{
    private const int SegmentsPerJob = 32;
    private readonly List<ConvexHull.LosSegment> segments = new();
    private VertexPositionColor[] shadows = Array.Empty<VertexPositionColor>();
    private VertexPositionTexture[] penumbras = Array.Empty<VertexPositionTexture>();

    internal VertexPositionColor[] Shadows => shadows;
    internal VertexPositionTexture[] Penumbras => penumbras;
    internal int ShadowCount { get; private set; }
    internal int PenumbraCount { get; private set; }

    private static readonly MethodInfo shadowMethod = AccessTools.Method(typeof(ConvexHull), nameof(ConvexHull.CalculateLosVertices));
    private static readonly MethodInfo penumbraMethod = AccessTools.Method(typeof(ConvexHull), "CalculateLosPenumbraVertices");

    internal void Build(List<ConvexHull> hulls, Rectangle view, Vector2 viewpoint)
    {
        ShadowCount = PenumbraCount = 0;
        // Mods can replace the geometry or touch game state in either original method.
        bool patched = Harmony.GetPatchInfo(shadowMethod)?.Owners.Count > 0 ||
                       Harmony.GetPatchInfo(penumbraMethod)?.Owners.Count > 0;
        try
        {
            foreach (var hull in hulls)
            {
                if (!hull.Intersects(view)) { continue; }
                Vector2 offset = hull.ParentEntity?.Submarine?.DrawPosition ?? Vector2.Zero;
                Vector2 localView = viewpoint - offset;
                if (patched)
                {
                    hull.CalculateLosVertices(localView);
                    Append(hull);
                }
                else
                {
                    segments.Add(hull.CaptureLosSegment(localView, offset));
                }
            }

            if (segments.Count >= 2 * SegmentsPerJob)
            {
                int jobs = (segments.Count + SegmentsPerJob - 1) / SegmentsPerJob;
                ThreadManager.Instance.Process.RunParallel("LOS shadow geometry", 0, jobs, CalculateJob);
            }
            else
            {
                foreach (var segment in segments) { segment.CalculateVertices(); }
            }

            // RunParallel joins before returning. Each occluder owns its output arrays.
            foreach (var segment in segments) { Append(segment.Hull); }
        }
        finally
        {
            segments.Clear();
        }
    }

    private void CalculateJob(int index)
    {
        int start = index * SegmentsPerJob;
        int end = Math.Min(start + SegmentsPerJob, segments.Count);
        for (int i = start; i < end; i++) { segments[i].CalculateVertices(); }
    }

    private void Append(ConvexHull hull)
    {
        ShadowCount = AppendVertices(ref shadows, ShadowCount, hull.ShadowVertices, hull.ShadowVertexCount);
        PenumbraCount = AppendVertices(ref penumbras, PenumbraCount, hull.PenumbraVertices, hull.PenumbraVertexCount);
    }

    private static int AppendVertices<T>(ref T[] destination, int offset, T[] source, int count)
    {
        int end = offset + count;
        if (end > destination.Length) { Array.Resize(ref destination, Math.Max(end, destination.Length * 2)); }
        Array.Copy(source, 0, destination, offset, count);
        return end;
    }
}
