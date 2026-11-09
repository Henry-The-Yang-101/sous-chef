using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class NavigationActions
    {
        // The catalogue visits indexed cells around each station or point. A focused walk
        // feeds the same matcher cells in Dijkstra settlement order instead.
        private sealed class DestinationMatch
        {
            private struct Region
            {
                public Collider Collider;
                public int Left, Right, Bottom, Top;
            }

            private struct Choice
            {
                public float Cost;
                public Vector3 Stance;
                public WalkCell Cell;
            }

            private readonly NavigationActions nav;
            private readonly Destination destination;
            private readonly List<Region> regions = new List<Region>();
            private readonly Vector3 retained;
            private readonly bool preferRetained;
            private Choice best = new Choice { Cost = float.PositiveInfinity };
            private Choice preferred = new Choice { Cost = float.PositiveInfinity };

            public DestinationMatch(NavigationActions nav, Destination destination)
            {
                this.nav = nav;
                this.destination = destination;
                bool walkingToStation = !destination.IsPoint && nav.target != null && destination.Id == nav.target.Id;
                retained = destination.IsPoint ? destination.Approach :
                    destination.Object.transform.TransformPoint((walkingToStation ? nav.target : destination).LocalApproach);
                if (destination.IsPoint)
                {
                    float reach=WalkPhysics.Spacing*2;
                    regions.Add(new Region { Left=Mathf.FloorToInt((retained.x-reach)/WalkPhysics.Spacing),
                        Right=Mathf.CeilToInt((retained.x+reach)/WalkPhysics.Spacing),
                        Bottom=Mathf.FloorToInt((retained.z-reach)/WalkPhysics.Spacing),
                        Top=Mathf.CeilToInt((retained.z+reach)/WalkPhysics.Spacing) });
                    return;
                }
                foreach (Collider collider in destination.Colliders)
                {
                    Bounds bounds = collider.bounds;
                    var area = new Region {
                        Collider = collider,
                        Left = Mathf.FloorToInt((bounds.min.x - 1) / WalkPhysics.Spacing),
                        Right = Mathf.CeilToInt((bounds.max.x + 1) / WalkPhysics.Spacing),
                        Bottom = Mathf.FloorToInt((bounds.min.z - 1) / WalkPhysics.Spacing),
                        Top = Mathf.CeilToInt((bounds.max.z + 1) / WalkPhysics.Spacing)
                    };
                    if ((area.Right - area.Left) * (area.Top - area.Bottom) <= 900) regions.Add(area);
                    Vector3 stance;
                    if (walkingToStation && !preferRetained && Vector3.Distance(nav.physics.Position, retained) < 1f &&
                        nav.physics.Approach(retained, collider, out stance) && Vector3.Distance(stance, retained) < .04f)
                        preferRetained = true;
                }
            }

            public float CertificationCost { get { return preferRetained ? preferred.Cost : best.Cost; } }

            // Distance to the union of candidate cell rectangles is a consistent
            // lower bound on ordinary walking distance to any usable stance.
            // Portal graphs use Dijkstra because a portal can bypass this bound.
            public float LowerBound(WalkCell cell)
            {
                float best = float.PositiveInfinity;
                foreach (Region r in regions)
                {
                    float x = Mathf.Max(r.Left - cell.X, Mathf.Max(0, cell.X - r.Right));
                    float z = Mathf.Max(r.Bottom - cell.Z, Mathf.Max(0, cell.Z - r.Top));
                    best = Mathf.Min(best, (float)System.Math.Sqrt(x * x + z * z) * WalkPhysics.Spacing);
                }
                return regions.Count == 0 ? 0 : best;
            }
            public bool SameRegion(DestinationMatch other)
            {
                if (other == null || destination != other.destination || regions.Count != other.regions.Count ||
                    (destination.IsPoint && Vector3.Distance(retained,other.retained)>.001f)) return false;
                for (int i = 0; i < regions.Count; i++)
                {
                    var a = regions[i]; var b = other.regions[i];
                    if (a.Left != b.Left || a.Right != b.Right || a.Bottom != b.Bottom || a.Top != b.Top) return false;
                }
                return true;
            }

            public void Visit(WalkCell cell, float distance)
            {
                if (cell.Portal != 0) return;
                foreach (Region r in regions)
                    if (cell.X >= r.Left && cell.X <= r.Right && cell.Z >= r.Bottom && cell.Z <= r.Top)
                        Consider(cell, distance, r.Collider);
            }

            public void VisitIndexed()
            {
                foreach (Region r in regions)
                    for (int x = r.Left; x <= r.Right; x++)
                        for (int z = r.Bottom; z <= r.Top; z++)
                        {
                            List<WalkCell> cells;
                            if (nav.reachableByXZ.TryGetValue(((long)x << 32) ^ (uint)z, out cells))
                                foreach (WalkCell cell in cells) Consider(cell, nav.search.Distances[cell], r.Collider);
                        }
            }

            private void Consider(WalkCell cell, float distance, Collider collider)
            {
                if (distance >= CertificationCost) return;
                if (destination.IsPoint)
                {
                    Vector3 point=nav.physics.Point(cell); float connector=Vector3.Distance(point,retained);
                    int history=cell.SurfaceHistory;
                    if (connector<=WalkPhysics.Spacing*2 && distance+connector<best.Cost && nav.physics.Segment(point,retained,ref history))
                        best=new Choice { Cost=distance+connector, Stance=retained, Cell=cell };
                    return;
                }
                Vector3 stance;
                if (!nav.physics.CellApproach(cell, collider, out stance)) return;
                if (destination.Pack != null)
                {
                    // A backpack only dispenses from its rear cone.
                    Vector3 offset = stance - destination.Object.transform.position;
                    offset.y = 0;
                    if (Vector3.Angle(-destination.Object.transform.forward, offset) >= destination.Pack.m_pickupAngle * .5f) return;
                }
                var choice = new Choice { Cost = distance + Vector3.Distance(nav.physics.Point(cell), stance), Stance = stance, Cell = cell };
                if (choice.Cost < best.Cost) best = choice;
                if (preferRetained && choice.Cost < preferred.Cost && Vector3.Distance(stance, retained) < .04f)
                    preferred = choice;
            }

            public void Publish()
            {
                Choice chosen = best;
                if (preferRetained && preferred.Cost < float.PositiveInfinity && preferred.Cost <= nav.search.MinimumPendingDistance)
                    chosen = preferred;
                else if (preferRetained && !nav.search.Complete && nav.RemainingRouteClear()) return;

                if (chosen.Cost == float.PositiveInfinity || chosen.Cost > nav.search.MinimumPendingDistance) return;
                if (destination.IsPoint)
                {
                    destination.Goal=chosen.Cell; destination.Distance=chosen.Cost; destination.DirectRoute=false;
                    nav.available[destination.Id]=destination; return;
                }
                Vector3 local = destination.Object.transform.InverseTransformPoint(chosen.Stance);
                destination.Goal = chosen.Cell;
                destination.Approach = chosen.Stance;
                destination.LocalApproach = local;
                destination.Distance = chosen.Cost;
                nav.available[destination.Id] = destination;
            }
        }
    }
}
