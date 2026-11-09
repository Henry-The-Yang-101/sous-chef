using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class NavigationActions
    {
        private const float PlayerLookahead = .4f;
        private const float PassingMargin = .08f, ReleaseMargin = .06f;
        private const float SideCommitmentSeconds = .16f;

        private struct TeammateMotion
        {
            public Vector3 Offset, Velocity;
            public float Clearance;
            public bool Moving { get { return Velocity.sqrMagnitude > .04f; } }

            public bool Conflicts(Vector3 movement, float horizon = PlayerLookahead)
            {
                Vector3 relative = Velocity - movement; relative.y = 0;
                float closing = -Vector3.Dot(Offset, relative);
                // Already close but separating (or moving together). Allow the
                // chef to leave contact without requiring an impossible start gap.
                if (closing <= 0) return false;
                float closest = Mathf.Min(horizon, closing / relative.sqrMagnitude);
                return (Offset + relative * closest).sqrMagnitude < Clearance * Clearance;
            }
        }

        private readonly List<PlayerControls> localTeammates;
        private readonly List<TeammateMotion> nearbyTeammates = new List<TeammateMotion>(3);
        private bool yieldingPlayer;
        private Vector3 yieldSide;
        private float yieldSideUntil;

        private void ResetPlayerYield()
        {
            yieldingPlayer = false;
            yieldSide = Vector3.zero;
            yieldSideUntil = 0;
        }

        private bool YieldToTeammates(Vector3 position, Vector3 planned,
            Vector3 velocity, Vector3 drift, float speed, float retainedMotion, int milliseconds)
        {
            if (localTeammates == null || localTeammates.Count == 0) return false;
            Bounds body = physics.BodyBounds;
            float maxSpeed = Mathf.Max(speed, velocity.magnitude) + drift.magnitude;
            Vector3 remaining = target.Approach - position; remaining.y = 0;
            float plannedWindow = .22f;
            // The arrival controller brakes at the stance. Do not predict
            // walking through a stationary chef on the far side of it.
            if (waypoint == route.Count - 1 && planned.sqrMagnitude > .001f)
                plannedWindow = Mathf.Clamp(Vector3.Dot(remaining, planned) / planned.sqrMagnitude, 0, plannedWindow);
            nearbyTeammates.Clear();
            TeammateMotion blocker = default(TeammateMotion);
            float blockerDistance = float.PositiveInfinity;
            foreach (PlayerControls teammate in localTeammates)
            {
                if (teammate == null || !teammate.isActiveAndEnabled || teammate.m_bRespawning) continue;
                var shape = teammate.GetComponent<CapsuleCollider>();
                if (shape == null || !shape.enabled || shape.isTrigger) continue;
                Bounds other = shape.bounds;
                if (body.max.y <= other.min.y || other.max.y <= body.min.y) continue;
                Vector3 offset = other.center - body.center; offset.y = 0;
                Vector3 motion = teammate.Motion.GetVelocityXZ();
                if (teammate.WindReceiver != null) motion += teammate.WindReceiver.GetVelocity();
                if (teammate.SurfaceMovable != null) motion += teammate.SurfaceMovable.GetVelocity();
                motion.y = 0;
                float clearance = physics.BodyRadius + Mathf.Max(other.extents.x, other.extents.z);
                // A stationary chef may be working right next to our stance.
                // Reserve a comfort margin for passing bodies in motion.
                clearance += motion.sqrMagnitude > .04f ? PassingMargin : .01f;
                if (yieldingPlayer) clearance += ReleaseMargin;
                float reach = clearance + (maxSpeed + motion.magnitude) * PlayerLookahead;
                float distance = offset.sqrMagnitude;
                if (distance >= reach * reach) continue;
                var sample = new TeammateMotion { Offset = offset, Velocity = motion, Clearance = clearance };
                nearbyTeammates.Add(sample);
                if (distance < blockerDistance && sample.Conflicts(planned, sample.Moving ? PlayerLookahead : plannedWindow))
                {
                    blocker = sample;
                    blockerDistance = distance;
                }
            }
            if (nearbyTeammates.Count == 0) return false;

            float now = Time.realtimeSinceStartup;
            float window = Mathf.Max(milliseconds / 1000f, Time.deltaTime) + Time.fixedDeltaTime;
            Vector3 holding = velocity * retainedMotion + drift;
            bool committed = now < yieldSideUntil;
            if (committed && TryYieldStep(position, yieldSide, drift, speed, window, milliseconds))
                return true;
            if (float.IsPositiveInfinity(blockerDistance)) return false;
            bool occupiedStance = !blocker.Moving && (blocker.Offset - remaining).sqrMagnitude < blocker.Clearance * blocker.Clearance;
            bool canHold = CanHoldPosition(position, holding, window);
            if (canHold && (blocker.Moving || occupiedStance) && ClearOfTeammates(holding))
                return SendYield(position, Vector3.zero, milliseconds, "yield_wait");

            if (!committed)
            {
                // Step out of the teammate's lane. Prefer the side we are
                // already on, with a consistent tie break for a head-on meeting.
                Vector3 lane = (blocker.Moving ? blocker.Velocity : blocker.Offset).normalized;
                Vector3 side = new Vector3(lane.z, 0, -lane.x);
                if (Vector3.Dot(side, blocker.Moving ? -blocker.Offset : planned) < 0) side = -side;
                Vector3 forward = Vector3.zero;
                if (!blocker.Moving)
                {
                    // Follow a tangent to a stationary body, so successive
                    // steps make progress around it instead of shuffling sideways.
                    float sine = Mathf.Clamp01((blocker.Clearance + .05f) / Mathf.Sqrt(blockerDistance));
                    forward = lane * Mathf.Sqrt(1 - sine * sine);
                    side *= sine;
                }
                for (int i = 0; i < 2; i++, side = -side)
                {
                    Vector3 direction = forward + side;
                    if (!blocker.Moving && Vector3.Dot(direction, planned) <= 0) continue;
                    if (!TryYieldStep(position, direction, drift, speed, window, milliseconds)) continue;
                    if (blocker.Moving)
                    {
                        yieldSide = direction;
                        yieldSideUntil = now + SideCommitmentSeconds;
                    }
                    return true;
                }
            }
            Vector3 away = -blocker.Offset.normalized;
            if (blocker.Moving && TryYieldStep(position, away, drift, speed, window, milliseconds, "yield_retreat")) return true;
            // If nowhere can clear their trajectory, stop pushing. On moving
            // ground keep the checked route command if neutral would lose footing.
            return canHold && SendYield(position, Vector3.zero, milliseconds, "yield_wait");
        }

        private bool ClearOfTeammates(Vector3 movement)
        {
            foreach (var teammate in nearbyTeammates)
                if (teammate.Conflicts(movement)) return false;
            return true;
        }

        private bool CanHoldPosition(Vector3 position, Vector3 holding, float window)
        {
            return holding.sqrMagnitude < .0001f || physics.Segment(position, position + holding * window);
        }

        private bool TryYieldStep(Vector3 position, Vector3 direction, Vector3 drift, float speed,
            float window, int milliseconds, string phase = "yield_step")
        {
            float x, z;
            if (!WalkingSteering.Direction(direction.x, direction.z, drift.x, drift.z, speed, out x, out z)) return false;
            Vector3 input = new Vector3(x, 0, z);
            Vector3 movement = input * speed + drift;
            if (!ClearOfTeammates(movement) || !physics.Segment(position, position + movement * window)) return false;
            return SendYield(position + movement * window, input, milliseconds, phase);
        }

        private bool SendYield(Vector3 goal, Vector3 input, int milliseconds, string phase)
        {
            yieldingPlayer = true;
            ResetWalkingProgress();
            Trace(phase, goal, input);
            move(input, milliseconds);
            return true;
        }
    }
}
