using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class WalkPhysics
    {
        // Continue over a continuously walkable corridor, with clearance for
        // the entire vertical landing envelope. GroundCast can still hold the
        // preceding slope normal: check that native basis as well as flat input.
        // This never authorizes crossing a hole or a disconnected lower deck.
        public bool TryLandingWalk(Vector3 goal, float seconds, out Vector3 input)
        {
            input = Vector3.zero;
            if (seconds <= 0 || seconds > .15f) return false;
            var surface = chef.PhysicsSurface == null ? null : chef.PhysicsSurface.Properties;
            if (surface != null && (surface.Slippiness > .05f || surface.Slidiness > .01f)) return false;
            Vector3 drift = chef.WindReceiver == null ? Vector3.zero : chef.WindReceiver.GetVelocity();
            if (chef.SurfaceMovable != null) drift += chef.SurfaceMovable.GetVelocity();
            if (drift.magnitude > .01f) return false;
            Vector3 position = Position, support; Collider ground; RaycastHit hit;
            if (!Stand(position, out support, out ground, ContactRadius, out hit, 1f + nativeRadius)) return false;
            float gap = position.y - support.y;
            if (gap < Skin || gap > 1f + nativeRadius) return false;
            if (!Segment(support, goal, ContactRadius, true)) return false;
            Vector3 direction = goal - position; direction.y = 0;
            if (direction.magnitude < .2f || direction.magnitude > 1.4f) return false;
            Vector3 desired = direction.normalized;
            Vector3 previous = chef.Motion.GetVelocityXZ();
            if (previous.magnitude > .2f && Vector3.Dot(previous.normalized, desired) < .866f) return false;
            float speed = Speed(ground);
            float horizon = seconds + Time.fixedDeltaTime;
            Vector3 nativeVelocity = desired * speed;
            if (chef.GroundCollider != null)
            {
                Vector3 normal = chef.GetComponent<GroundCast>().GetGroundNormal();
                if (normal.y < .52992f || Mathf.Abs(normal.magnitude - 1) > .01f) return false;
                // Match ProgressVelocityWrtFriction's orthonormal ground basis
                // and retained normal velocity; a world-space input is not the
                // resulting world-space velocity on a slope.
                Vector3 right = new Vector3(normal.y, -normal.x, 0).normalized;
                Vector3 forward = new Vector3(right.y * normal.z, -right.x * normal.z,
                    right.x * normal.y - right.y * normal.x).normalized;
                nativeVelocity = (right * desired.x + forward * desired.z) * speed +
                    normal * Vector3.Dot(chef.Motion.GetVelocity(), normal);
            }
            float rise = Mathf.Max(0, Mathf.Max(chef.Motion.GetVelocity().y, nativeVelocity.y)) * horizon;
            // Check both the preceding native motion and the new input. Gravity
            // may land anywhere down to the checked support; upward momentum
            // enlarges the volume instead of assuming an immediate flat pose.
            if (!AirborneCorridor(position, support, desired * speed * horizon, rise)) return false;
            nativeVelocity.y = 0;
            if (!AirborneCorridor(position, support, nativeVelocity * horizon, rise)) return false;
            if (previous.magnitude > .2f && !AirborneCorridor(position, support, previous * horizon, rise)) return false;
            input = desired; return !Overflow;
        }
        private bool AirborneCorridor(Vector3 body, Vector3 support, Vector3 offset, float rise)
        {
            Vector3 end; Collider ground; RaycastHit hit;
            Vector3 guess = support + offset;
            if (!Stand(guess, out end, out ground, ContactRadius, out hit)) return false;
            var samples = new List<GroundSample>();
            if (!Segment(support, end, ContactRadius, true, samples) || samples.Count == 0) return false;
            float lowest = support.y;
            foreach (GroundSample sample in samples) lowest = Mathf.Min(lowest, sample.Point.y);
            float extra = Mathf.Max(0, body.y - lowest) + rise;
            Vector3 previous = support;
            foreach (GroundSample sample in samples)
            {
                Vector3 low, high; Capsule(previous, out low, out high, ContactRadius); high.y += extra;
                int count = Physics.OverlapCapsuleNonAlloc(low, high, ContactRadius, overlaps, ~0, QueryTriggerInteraction.Collide); Queries++;
                if (count == overlaps.Length) { Overflow = true; return false; }
                for (int i = 0; i < count; i++) if (Blocks(overlaps[i])) return false;
                Vector3 delta = sample.Point - previous;
                if (delta.magnitude > .0001f)
                {
                    count = Physics.CapsuleCastNonAlloc(low, high, ContactRadius, delta.normalized, hits, delta.magnitude, ~0, QueryTriggerInteraction.Collide); Queries++;
                    if (count == hits.Length) { Overflow = true; return false; }
                    for (int i = 0; i < count; i++) if (Blocks(hits[i].collider)) return false;
                }
                previous = sample.Point;
            }
            return true;
        }
    }
}
