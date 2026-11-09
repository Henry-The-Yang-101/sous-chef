using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class WalkPhysics
    {
        // Search node limits do not bound geometry retained across searches.
        // Moving kitchens visit new world-space cells throughout a round.
        private const int MaxGeometryCacheCells = 65536, GeometryBucketCells = 10;
        private const float GeometryBucketSize = Spacing * GeometryBucketCells;
        private struct ProbeCacheEntry { public WalkCell Cell; public Probe Probe; }
        private readonly Queue<ProbeCacheEntry> probeOrder = new Queue<ProbeCacheEntry>();
        private readonly Dictionary<long, HashSet<WalkCell>> probeBuckets = new Dictionary<long, HashSet<WalkCell>>();
        private readonly HashSet<WalkCell> dirtyProbes = new HashSet<WalkCell>();
        private bool geometryCacheEvicted;
        public int CachedProbeCount { get { return probes.Count; } }
        public int CachedEdgeCount { get { return edges.Count; } }
        public long GeometryCacheEvictions { get; private set; }
        private static long BucketKey(int x, int z) { return ((long)x << 32) ^ (uint)z; }
        private static int ProbeBucket(int coordinate) { return Mathf.FloorToInt((float)coordinate / GeometryBucketCells); }

        private void ClearGeometryCache()
        {
            probes.Clear(); edges.Clear(); probeBuckets.Clear(); dirtyProbes.Clear(); probeOrder.Clear();
        }

        private void CacheProbe(WalkCell cell, Probe probe)
        {
            Probe prior;
            if (probes.TryGetValue(cell, out prior) && System.Object.ReferenceEquals(prior, probe)) return;
            // Bound both retained cells and stale FIFO entries left by regional
            // invalidation. Evict incrementally, without a full-cache cold spike.
            if (probeOrder.Count >= MaxGeometryCacheCells)
            {
                ProbeCacheEntry oldest = probeOrder.Dequeue();
                Probe retained;
                if (probes.TryGetValue(oldest.Cell, out retained) && System.Object.ReferenceEquals(retained, oldest.Probe))
                {
                    RemoveCachedProbe(oldest.Cell); GeometryCacheEvictions++;
                    // Eviction discards range-decision evidence as well as
                    // acceleration data. Do not continue a saved search proof.
                    geometryCacheEvicted = true; searchRemembered = false;
                    Changed = true; geometryVersion++;
                }
            }
            if (!probes.ContainsKey(cell))
            {
                long key = BucketKey(ProbeBucket(cell.X), ProbeBucket(cell.Z));
                HashSet<WalkCell> bucket;
                if (!probeBuckets.TryGetValue(key, out bucket))
                { bucket = new HashSet<WalkCell>(); probeBuckets.Add(key, bucket); }
                bucket.Add(cell);
            }
            probes[cell] = probe;
            probeOrder.Enqueue(new ProbeCacheEntry { Cell = cell, Probe = probe });
        }

        private void RemoveCachedProbe(WalkCell cell)
        {
            probes.Remove(cell); edges.Remove(cell);
            long key = BucketKey(ProbeBucket(cell.X), ProbeBucket(cell.Z));
            HashSet<WalkCell> bucket = probeBuckets[key];
            bucket.Remove(cell);
            if (bucket.Count == 0) probeBuckets.Remove(key);
        }

        // Every attempted lattice stand (including misses) and every source of an
        // attempted edge has a cached probe. Use the full XZ footprint for the
        // search proof, including sweeps above and below the sampled height.
        private bool InvalidateGeometryCache()
        {
            bool touchesSearch = false;
            dirtyProbes.Clear();
            // Include a full diagonal edge and its furthest supporting bank.
            float padding = Mathf.Max(radius, 2 * roundedReach) + Spacing * 1.5f;
            foreach (Bounds bounds in changed)
            {
                if (dirtyProbes.Count == probes.Count) break;
                Vector3 min = bounds.min, max = bounds.max;
                min.x -= padding; min.z -= padding;
                max.x += padding; max.z += padding;
                // Retain the original asymmetric vertical probe envelope.
                min.y = min.y - height - StepHeight - nativeRadius - .2f;
                max.y = max.y + StepHeight + nativeRadius + .2f;
                // Include a spare bucket at the boundary to cover float rounding.
                int minX = Mathf.FloorToInt(min.x / GeometryBucketSize) - 1, maxX = Mathf.FloorToInt(max.x / GeometryBucketSize) + 1;
                int minZ = Mathf.FloorToInt(min.z / GeometryBucketSize) - 1, maxZ = Mathf.FloorToInt(max.z / GeometryBucketSize) + 1;
                double bucketCount = ((double)maxX - minX + 1) * ((double)maxZ - minZ + 1);
                if (bucketCount > probeBuckets.Count)
                {
                    // Broad kill planes must not enumerate an enormous empty grid.
                    foreach (HashSet<WalkCell> bucket in probeBuckets.Values) DirtyBucket(bucket, min, max, ref touchesSearch);
                }
                else
                    for (int x = minX; x <= maxX; x++) for (int z = minZ; z <= maxZ; z++)
                    {
                        HashSet<WalkCell> bucket;
                        if (probeBuckets.TryGetValue(BucketKey(x, z), out bucket)) DirtyBucket(bucket, min, max, ref touchesSearch);
                    }
            }
            foreach (WalkCell cell in dirtyProbes)
                RemoveCachedProbe(cell);
            if (dirtyProbes.Count > 0) Changed = true;
            dirtyProbes.Clear();
            return touchesSearch;
        }

        private void DirtyBucket(HashSet<WalkCell> bucket, Vector3 min, Vector3 max, ref bool touchesSearch)
        {
            foreach (WalkCell cell in bucket)
            {
                if (dirtyProbes.Contains(cell)) continue;
                Vector3 p = Approximate(cell);
                if (p.x < min.x || p.x > max.x || p.z < min.z || p.z > max.z) continue;
                touchesSearch = true;
                if (p.y >= min.y && p.y <= max.y)
                    dirtyProbes.Add(cell);
            }
        }
    }
}
