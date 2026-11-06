using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class NavigationActions
    {
        private const float StallSeconds = .5f, ProgressTolerance = .03f;
        private Transform walkingSupport;
        private Vector3 walkingLocalPosition, walkingLocalOrigin, walkingDisplacement, walkingHeading;
        private float walkingSampleTime, walkingCommandUntil, stationarySeconds;
        private int stallReroutes;

        private void ResetWalkingProgress()
        {
            walkingSupport = null; walkingDisplacement = Vector3.zero; stationarySeconds = 0;
        }

        private void RecordWalking(Vector3 position, Vector3 heading, int milliseconds)
        {
            Collider ground = chef.GroundCollider;
            if (ground == null) { ResetWalkingProgress(); return; }
            walkingSupport = ground.transform;
            walkingLocalPosition = walkingSupport.InverseTransformPoint(position);
            // Carry the accumulated progress origin into each new support frame.
            // Summing world-space steps would cancel real walking if the ground
            // rotated between samples, and resetting on contact changes would
            // let jitter between neighbouring floor colliders hide a stall.
            walkingLocalOrigin = walkingSupport.InverseTransformPoint(position - walkingDisplacement);
            // Store a point as well as the pose so rotating ground transports the
            // failed heading into the same frame as the next observation.
            walkingHeading = walkingSupport.InverseTransformPoint(position + heading.normalized);
            walkingSampleTime = Time.realtimeSinceStartup;
            walkingCommandUntil = walkingSampleTime + milliseconds / 1000f;
        }

        private bool RecoverIfStalled(Vector3 position)
        {
            if (walkingSupport == null || chef.GroundCollider == null) return true;
            Vector3 previous = walkingSupport.TransformPoint(walkingLocalPosition);
            walkingDisplacement = position - walkingSupport.TransformPoint(walkingLocalOrigin);
            walkingDisplacement.y = 0;
            float now = Time.realtimeSinceStartup;
            // Count only the time covered by a nonzero, checked walking command.
            // Neutral phases and resume reset this clock; native input expiry
            // cannot turn a long gap without commands into a false stall.
            stationarySeconds += Mathf.Max(0, Mathf.Min(now, walkingCommandUntil) - walkingSampleTime);
            if (walkingDisplacement.magnitude >= ProgressTolerance)
            { walkingDisplacement = Vector3.zero; stationarySeconds = 0; }
            if (stationarySeconds + .00001f < StallSeconds) return true;

            Vector3 heading = walkingSupport.TransformPoint(walkingHeading) - previous;
            physics.AvoidStalledHeading(position, heading, chef.GroundCollider);
            stallReroutes++;
            Trace("stall_reroute", position + heading, Vector3.zero);
            ResetWalkingProgress();
            neutral();
            // The usual periodic refresh could choose precisely the same edge.
            // Rebuild with the failed local heading excluded, including cached
            // edges, start/goal connectors and execution shortcuts.
            Refresh(chef, true, true, false, RecoveryBudgetMilliseconds);
            return ApplyPlan();
        }
    }

    internal sealed partial class WalkPhysics
    {
        private sealed class StalledHeading
        {
            public Transform Support;
            public Vector3 Origin, Ahead;
        }
        private readonly List<StalledHeading> stalledHeadings = new List<StalledHeading>();

        public void ClearStallAvoidance()
        {
            if (stalledHeadings.Count == 0) return;
            stalledHeadings.Clear(); ForgetSearch();
        }

        public void AvoidStalledHeading(Vector3 position, Vector3 heading, Collider ground)
        {
            heading.y = 0;
            if (ground == null || heading.sqrMagnitude < .0001f) return;
            Transform support = ground.transform;
            if (stalledHeadings.Count == 8) stalledHeadings.RemoveAt(0);
            stalledHeadings.Add(new StalledHeading { Support = support,
                Origin = support.InverseTransformPoint(position),
                Ahead = support.InverseTransformPoint(position + heading.normalized) });
            ForgetSearch();
        }

        private bool AvoidsStalledHeadings(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from; delta.y = 0;
            if (delta.sqrMagnitude < .000001f) return true;
            float clearance = Mathf.Max(Spacing, radius * 1.5f);
            foreach (StalledHeading stalled in stalledHeadings)
            {
                if (stalled.Support == null) continue;
                Vector3 origin = stalled.Support.TransformPoint(stalled.Origin);
                Vector3 heading = stalled.Support.TransformPoint(stalled.Ahead) - origin; heading.y = 0;
                // Sideways and backward departures remain eligible. All of them
                // still undergo ordinary support, collision and flow checks.
                if (Vector3.Dot(delta.normalized, heading.normalized) <= .5f) continue;
                Vector3 offset = origin - from; offset.y = 0;
                float fraction = Mathf.Clamp01(Vector3.Dot(offset, delta) / delta.sqrMagnitude);
                Vector3 closest = Vector3.Lerp(from, to, fraction) - origin;
                if (Mathf.Abs(closest.y) > StepHeight + .1f) continue;
                closest.y = 0;
                if (closest.sqrMagnitude < clearance * clearance) return Fail("stalled_heading", origin);
            }
            return true;
        }
    }
}
