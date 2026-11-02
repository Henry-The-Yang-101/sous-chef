using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class WalkPhysics
    {
        private const BindingFlags ConveyorFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo conveyorSpeed = typeof(Travelator).GetField("m_speed", ConveyorFields);
        private static readonly FieldInfo conveyorDirection = typeof(Travelator).GetField("m_directionXZ", ConveyorFields);
        private static readonly FieldInfo conveyorBorder = typeof(Travelator).GetField("m_border", ConveyorFields);
        private static readonly FieldInfo conveyorGrid = typeof(Travelator).GetField("m_gridManager", ConveyorFields);
        private static readonly FieldInfo conveyorIndex = typeof(Travelator).GetField("m_gridIndex", ConveyorFields);
        private static readonly MethodInfo adjacentConveyor = typeof(Travelator).GetMethod("GetAdjacentTravelator", ConveyorFields);
        private static readonly Vector3[] conveyorSides = { Vector3.right, -Vector3.right, Vector3.forward, -Vector3.forward };
        private sealed class ConveyorProof
        {
            public Travelator Belt;
            public object Speed, Direction, Border, Grid, Index;
            public Matrix4x4 Matrix;
            public bool Enabled, Active;
            public readonly Travelator[] Neighbours = new Travelator[4];
        }
        private readonly List<ConveyorProof> searchConveyors = new List<ConveyorProof>();
        private IMovingSurface searchPreviousSurface;

        private bool ConveyorSupport(Vector3 point, Vector3 normal, float clearance)
        {
            if (normal.y < .9f) return false;
            // Native velocity changes can leave the actual body slightly outside
            // the graph's padded lane. Checked contact connectors may recover to
            // that lane; do not demand the extra planning margin at their start.
            if (clearance < radius - .001f) return true;
            // Unlike a stair seam, a missing outer corner beside a fast belt
            // cannot safely support a turn. Keep the body footprint over the
            // union of belt tops and adjoining floors, including diagonal lips.
            // Adjacent tiles are accepted regardless of which collider owns them.
            float padding = Mathf.Max(.01f, nativeRadius - Skin);
            for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++)
            {
                if (x == 0 && z == 0) continue;
                Vector3 offset = new Vector3(x, 0, z).normalized * padding;
                int count = Physics.RaycastNonAlloc(point + offset + Vector3.up * .1f, Vector3.down,
                    hits, .2f, groundMask, QueryTriggerInteraction.Ignore); Queries++;
                if (count == hits.Length) { Overflow = true; return false; }
                bool supported = false;
                for (int i = 0; i < count; i++)
                    if (hits[i].collider != null && !Own(hits[i].collider) && !Dangerous(hits[i].collider) &&
                        hits[i].normal.y >= .9f && Mathf.Abs(hits[i].point.y - point.y) < .05f)
                    { supported = true; break; }
                if (!supported) return false;
            }
            return true;
        }

        // Travelator's native velocity depends only on these fields, its world
        // pose and adjacent grid occupants (including the previous belt's next
        // link at corners). Snapshot the connected neighbourhood, not just the
        // belt directly under the chef. Unknown IMovingSurface types still use
        // fresh searches; this proof is specific to the installed native class.
        private bool RememberConveyors()
        {
            searchConveyors.Clear();
            var queue = new Queue<Travelator>(); var seen = new HashSet<Travelator>();
            foreach (GroundProof proof in searchGrounds)
            {
                var belt = MovingSurface(proof.Ground) as Travelator;
                if (belt != null && seen.Add(belt)) queue.Enqueue(belt);
            }
            searchPreviousSurface = chef.SurfaceMovable == null || previousSurface == null ? null : previousSurface.GetValue(chef.SurfaceMovable) as IMovingSurface;
            var previous = searchPreviousSurface as Travelator;
            if (previous != null && seen.Add(previous)) queue.Enqueue(previous);
            if (queue.Count == 0) return true;
            if (conveyorSpeed == null || conveyorDirection == null || conveyorBorder == null ||
                conveyorGrid == null || conveyorIndex == null || adjacentConveyor == null) return false;
            while (queue.Count > 0)
            {
                if (seen.Count > 512) return false;
                Travelator belt = queue.Dequeue();
                if (belt == null || conveyorGrid.GetValue(belt) == null) return false;
                var proof = new ConveyorProof { Belt = belt, Speed = conveyorSpeed.GetValue(belt),
                    Direction = conveyorDirection.GetValue(belt), Border = conveyorBorder.GetValue(belt),
                    Grid = conveyorGrid.GetValue(belt), Index = conveyorIndex.GetValue(belt),
                    Matrix = belt.transform.localToWorldMatrix, Enabled = belt.enabled, Active = belt.gameObject.activeInHierarchy };
                for (int i = 0; i < conveyorSides.Length; i++)
                {
                    Travelator neighbour = (Travelator)adjacentConveyor.Invoke(belt, new object[] { conveyorSides[i] });
                    proof.Neighbours[i] = neighbour;
                    if (neighbour != null && seen.Add(neighbour)) queue.Enqueue(neighbour);
                }
                searchConveyors.Add(proof);
            }
            return true;
        }
        private bool ConveyorsUnchanged()
        {
            IMovingSurface previous = chef.SurfaceMovable == null || previousSurface == null ? null : previousSurface.GetValue(chef.SurfaceMovable) as IMovingSurface;
            if (!System.Object.ReferenceEquals(previous, searchPreviousSurface)) return false;
            foreach (ConveyorProof proof in searchConveyors)
            {
                Travelator belt = proof.Belt;
                if (belt == null || belt.enabled != proof.Enabled || belt.gameObject.activeInHierarchy != proof.Active ||
                    belt.transform.localToWorldMatrix != proof.Matrix || !Equals(conveyorSpeed.GetValue(belt), proof.Speed) ||
                    !Equals(conveyorDirection.GetValue(belt), proof.Direction) || !Equals(conveyorBorder.GetValue(belt), proof.Border) ||
                    !Equals(conveyorGrid.GetValue(belt), proof.Grid) || !Equals(conveyorIndex.GetValue(belt), proof.Index)) return false;
                for (int i = 0; i < conveyorSides.Length; i++)
                    if (!System.Object.ReferenceEquals(adjacentConveyor.Invoke(belt, new object[] { conveyorSides[i] }), proof.Neighbours[i])) return false;
            }
            return true;
        }
    }
}
