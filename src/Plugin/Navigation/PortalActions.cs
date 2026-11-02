using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class NavigationActions
    {
        private readonly Dictionary<int, PortalLink> portalSteps = new Dictionary<int, PortalLink>();
        private PortalLink activePortal;
        private float portalStarted;
        private bool sawTeleport;
        private int teleportsCompleted;
        private string portalPhase;
        public bool NativeTeleportInProgress(PlayerControls player)
        { return player == chef && activePortal != null && Action.State == "navigating" && PortalLink.Travelling(player); }
        private object DescribePortal()
        {
            return activePortal == null ? null : Json.Object("entranceId", activePortal.Source == null ? 0 : activePortal.Id,
                "entry", Values.Vector(activePortal.Entry), "exit", Values.Vector(activePortal.Exit));
        }
        private bool RouteSegment(Vector3 from, int index)
        {
            PortalLink portal;
            return portalSteps.TryGetValue(index, out portal) ? physics.PortalEntry(from, portal) : physics.Segment(from, route[index]);
        }
        private void TickPortal(int commandMilliseconds)
        {
            if (Time.realtimeSinceStartup - portalStarted > activePortal.Timeout) { Stop("teleport_timeout"); return; }
            if (!activePortal.Connected) { Stop("teleport_unavailable"); return; }
            if (PortalLink.Travelling(chef))
            {
                if (!sawTeleport) Trace("teleport_started", activePortal.Exit, Vector3.zero);
                sawTeleport = true; portalPhase = "teleporting";
                move(Vector3.zero, commandMilliseconds); return;
            }
            if (sawTeleport)
            {
                // Native animations restore the collider and controls themselves.
                // Accept only the linked exit, then build a fresh walking tree.
                string error = physics.Refresh(chef);
                if (error != null) { Stop(error); return; }
                if (!physics.PortalGeometry(activePortal) || Vector3.Distance(physics.Position, activePortal.Exit) > .6f)
                { Stop("teleport_exit_mismatch"); return; }
                physics.LeavePortal(activePortal); teleportsCompleted++;
                Trace("teleport_complete", activePortal.Exit, Vector3.zero);
                activePortal = null; lastPlan = -1; settlingSince = -1;
                progressPosition = physics.Position; lastProgress = Time.realtimeSinceStartup;
                haveMotion = motionSampleValid = false; arrival.Reset(); facingSince = -1;
                move(Vector3.zero, commandMilliseconds); return;
            }
            string refreshError = physics.Refresh(chef);
            if (refreshError != null) { Stop(refreshError); return; }
            if (!physics.PortalGeometry(activePortal) || !physics.PortalEntry(physics.Position, activePortal))
            { Stop("teleport_entry_blocked"); return; }
            if (!activePortal.Ready(chef))
            {
                portalPhase = "waiting_portal";
                Trace(portalPhase, activePortal.Entry, Vector3.zero);
                move(Vector3.zero, commandMilliseconds); return;
            }
            portalPhase = "entering_portal";
            Vector3 position = physics.Position, direction = activePortal.Entry - position; direction.y = 0;
            if (direction.magnitude < .06f)
            { move(Vector3.zero, commandMilliseconds); return; }
            Vector3 drift = chef.WindReceiver == null ? Vector3.zero : chef.WindReceiver.GetVelocity();
            if (chef.SurfaceMovable != null) drift += chef.SurfaceMovable.GetVelocity();
            float speed = physics.Speed(chef.GroundCollider), x, z;
            if (!WalkingSteering.Direction(direction.x, direction.z, drift.x, drift.z, speed, out x, out z))
            { Stop("flow_prevents_walking"); return; }
            Vector3 input = new Vector3(x, 0, z);
            Vector3 step = position + (input * speed + drift) * Mathf.Max(commandMilliseconds / 1000f, Time.deltaTime);
            if (!physics.PortalSegment(position, step, activePortal.Trigger)) { Stop("teleport_entry_blocked"); return; }
            Trace(portalPhase, activePortal.Entry, input);
            move(input, commandMilliseconds);
        }
    }
}
