using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    // A portal is a directed native transport edge, never a walking chord.
    // Only the selected trigger is exempted during its checked entry/exit walk.
    internal sealed class PortalLink
    {
        public Teleportal Source;
        public ServerTeleportal Server;
        public TeleportalPlayerReceiver Receiver;
        public ServerTeleportalPlayerReceiver ServerReceiver;
        public Collider Trigger, ExitTrigger;
        public Vector3 Entry, Exit;
        public bool Valid;
        public int Id { get { return Source.GetInstanceID(); } }
        public bool Connected
        {
            get
            {
                return Source != null && Source.isActiveAndEnabled && Server != null && Server.isActiveAndEnabled &&
                    Trigger != null && Trigger.enabled && Trigger.isTrigger &&
                    Receiver != null && Receiver.isActiveAndEnabled && ServerReceiver != null && ServerReceiver.isActiveAndEnabled &&
                    Source.m_exitPortal == Receiver.gameObject && Receiver.m_attachPoint != null &&
                    ExitTrigger != null && ExitTrigger.enabled && ExitTrigger.gameObject.activeInHierarchy;
            }
        }
        public bool Ready(PlayerControls chef)
        {
            var traveller = chef.GetComponent<ServerTeleportablePlayer>();
            return Connected && traveller != null && traveller.CanTeleport(Server) &&
                Server.CanTeleport(traveller) && !Server.IsReceiving() && ServerReceiver.CanTeleportTo(traveller);
        }
        public float Timeout
        {
            get
            {
                var sender = Source == null ? null : Source.GetComponent<TeleportalPlayerSender>();
                return Mathf.Clamp(5f + (Source == null ? 0 : Source.m_receiveDelay + Source.m_cooldownTime) +
                    (sender == null || sender.m_SenderAnimation == null ? 0 : sender.m_SenderAnimation.m_CurveTime) +
                    (Receiver == null || Receiver.m_ReceiverAnimation == null ? 0 : Receiver.m_ReceiverAnimation.m_CurveTime), 5, 30);
            }
        }
        public static bool Travelling(PlayerControls chef)
        {
            if (chef == null) return false;
            var server = chef.GetComponent<ServerTeleportablePlayer>();
            var client = chef.GetComponent<ClientTeleportablePlayer>();
            return (server != null && server.IsTeleporting()) || (client != null && client.IsTeleporting());
        }
    }

    internal sealed partial class WalkPhysics
    {
        private readonly Dictionary<int, PortalLink> portals = new Dictionary<int, PortalLink>();
        private Collider permittedPortalTrigger, exitPortal;
        public PortalLink Portal(int id) { PortalLink link; return portals.TryGetValue(id, out link) ? link : null; }
        public void LeavePortal(PortalLink link) { exitPortal = link.ExitTrigger; }
        public object PortalDiagnostics()
        {
            var result = new List<object>();
            foreach (PortalLink link in portals.Values)
                result.Add(SousChef.Core.Json.Object("id", link.Id, "valid", link.Valid, "entry", Values.Vector(link.Entry), "exit", Values.Vector(link.Exit)));
            return result;
        }

        private void RefreshPortals()
        {
            var present = new HashSet<int>();
            for (int i = 0; i < nearbyCount; i++)
            {
                Collider trigger = nearby[i];
                if (trigger == null || !trigger.isTrigger) continue;
                var source = trigger.GetComponent<Teleportal>();
                if (source == null || source.m_exitPortal == null) continue;
                var sender = source.GetComponent<ServerTeleportalPlayerSender>();
                var receiver = source.m_exitPortal.GetComponent<TeleportalPlayerReceiver>();
                var serverReceiver = source.m_exitPortal.GetComponent<ServerTeleportalPlayerReceiver>();
                var server = source.GetComponent<ServerTeleportal>();
                if (sender == null || !sender.isActiveAndEnabled || receiver == null || serverReceiver == null || server == null) continue;
                int id = source.GetInstanceID(); present.Add(id);
                PortalLink link;
                if (!portals.TryGetValue(id, out link)) portals[id] = link = new PortalLink();
                link.Source = source; link.Server = server; link.Trigger = trigger;
                link.Receiver = receiver; link.ServerReceiver = serverReceiver;
                link.ExitTrigger = source.m_exitPortal.GetComponent<Collider>();
                link.Valid = PortalGeometry(link);
            }
            var removed = new List<int>();
            foreach (var pair in portals) if (!present.Contains(pair.Key)) { pair.Value.Valid = false; removed.Add(pair.Key); }
            foreach (int id in removed) portals.Remove(id);
        }

        public bool PortalGeometry(PortalLink link)
        {
            if (!link.Connected || link.Source.m_teleportArc <= 0 || link.Receiver.m_ReceiverAnimation == null ||
                !link.Receiver.m_groundPlayer) return false;
            // The native activation cone points along the portal's local +X.
            Vector3 facing = link.Source.transform.right; facing.y = 0;
            if (facing.sqrMagnitude < .99f) return false;
            Vector3 entry = link.Trigger.bounds.center + facing.normalized * .1f;
            entry.y = link.Source.transform.position.y;
            Collider ground; Vector3 point;
            if (!PortalStand(entry, link.Trigger, out point, out ground)) return false;
            link.Entry = point;
            // Match ClientTeleportalPlayerReceiver: grounded attach-point offset
            // plus the native animation's final position (not the portal centre).
            Transform attach = link.Receiver.m_attachPoint;
            int count = Physics.RaycastNonAlloc(attach.position, Vector3.down, hits, 2f, groundMask, QueryTriggerInteraction.Ignore); Queries++;
            if (count == hits.Length) { Overflow = true; return false; }
            float distance = Single.MaxValue; Vector3 offset = Vector3.zero;
            for (int i = 0; i < count; i++)
                if (hits[i].collider != null && hits[i].distance < distance)
                { distance = hits[i].distance; offset = hits[i].point - attach.position; }
            if (distance == Single.MaxValue) return false;
            Vector3 exit = attach.position + attach.rotation * link.Receiver.m_ReceiverAnimation.m_TargetPositionValue + offset;
            if ((exit - Position).sqrMagnitude > (Range - 2) * (Range - 2) ||
                !PortalStand(exit, link.ExitTrigger, out point, out ground) || Vector3.Distance(exit, point) > .1f) return false;
            link.Exit = point;
            return true;
        }
        private bool PortalStand(Vector3 guess, Collider trigger, out Vector3 point, out Collider ground)
        {
            Collider saved = permittedPortalTrigger; permittedPortalTrigger = trigger;
            try { return Stand(guess, out point, out ground); }
            finally { permittedPortalTrigger = saved; }
        }
        public bool PortalSegment(Vector3 from, Vector3 to, Collider trigger, int history = 0)
        {
            Collider saved = permittedPortalTrigger; permittedPortalTrigger = trigger;
            try { return Segment(from, to, Vector3.Distance(from, Position) < .02f ? ContactRadius : radius, true, null, null, history); }
            finally { permittedPortalTrigger = saved; }
        }
        public bool PortalEntry(Vector3 from, PortalLink link, int history = 0)
        {
            if (!link.Valid || !link.Connected || Vector3.Distance(from, link.Entry) > 1.6f) return false;
            Vector3 delta = from - link.Trigger.bounds.center; delta.y = 0;
            // Stay strictly inside the activation cone, including at first contact.
            if (Vector3.Dot(delta.normalized, link.Source.transform.right) <= Mathf.Cos(link.Source.m_teleportArc * Mathf.Deg2Rad) + .02f) return false;
            return PortalSegment(from, link.Entry, link.Trigger, history);
        }
        private IEnumerable<SousChef.Core.WalkEdge> WithPortals(SousChef.Core.WalkCell cell, IEnumerable<SousChef.Core.WalkEdge> walking)
        {
            foreach (var edge in walking) yield return edge;
            Vector3 from = Point(cell);
            foreach (PortalLink link in portals.Values)
            {
                if (!PortalEntry(from, link, cell.SurfaceHistory)) continue;
                // Teleport displacement adds no walking distance. Entry and exit
                // connectors retain their full distances and fresh physics checks.
                yield return new SousChef.Core.WalkEdge(new SousChef.Core.WalkCell(
                    Mathf.RoundToInt(link.Exit.x / Spacing), Mathf.RoundToInt(link.Exit.y / HeightBand),
                    Mathf.RoundToInt(link.Exit.z / Spacing), 0, link.Id), Vector3.Distance(from, link.Entry));
            }
        }
        private IEnumerable<SousChef.Core.WalkEdge> PortalDepartures(PortalLink link)
        {
            if (!link.Valid || !link.Connected) yield break;
            int x = Mathf.RoundToInt(link.Exit.x / Spacing), y = Mathf.RoundToInt(link.Exit.y / HeightBand), z = Mathf.RoundToInt(link.Exit.z / Spacing);
            for (int dx = -3; dx <= 3; dx++) for (int dz = -3; dz <= 3; dz++)
            {
                var key = new SousChef.Core.WalkCell(x + dx, y, z + dz); Probe probe = Sample(key);
                if (!probe.Valid || Vector3.Distance(probe.Point, link.Exit) > 1.6f) continue;
                int history = 0; Collider saved = permittedPortalTrigger; permittedPortalTrigger = link.ExitTrigger;
                bool clear;
                try { clear = Segment(link.Exit, probe.Point, radius, true, null, value => history = value); }
                finally { permittedPortalTrigger = saved; }
                if (clear)
                {
                    var resolved = Resolved(key, probe); resolved.SurfaceHistory = history;
                    yield return new SousChef.Core.WalkEdge(resolved, Vector3.Distance(link.Exit, probe.Point));
                }
            }
        }
        private bool CanExitPortal(Vector3 from, Vector3 to)
        {
            if (exitPortal == null || !exitPortal.enabled) return false;
            Vector3 centre = exitPortal.bounds.center; centre.y = from.y;
            if (Vector3.Distance(from, centre) > 2f) { exitPortal = null; return false; }
            return (to - centre).sqrMagnitude >= (from - centre).sqrMagnitude - .001f;
        }
        private bool StartSegment(Vector3 from, Vector3 to, Action<int> history)
        {
            Collider saved = permittedPortalTrigger;
            if (CanExitPortal(from, to)) permittedPortalTrigger = exitPortal;
            try { return Segment(from, to, ContactRadius, true, null, history); }
            finally { permittedPortalTrigger = saved; }
        }
    }
}
