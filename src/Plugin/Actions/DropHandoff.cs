using System;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed class DropPlan
    {
        public GameObject Counter;
        public Vector3 Landing, InitialVelocity;
        public float FlightSeconds;
        public object Describe()
        {
            return Json.Object("handoffMode", Counter == null ? "ground_drop" : "nearby_counter", "landing", Values.Vector(Landing),
                "initialVelocity", Values.Vector(InitialVelocity), "flightSeconds", FlightSeconds,
                "counterInstanceId", Counter == null ? null : (object)Counter.GetInstanceID());
        }
    }
    internal sealed class DropHandoff
    {
        private readonly RaycastHit[] hits = new RaycastHit[128];
        private readonly WindField wind = new WindField();
        private int groundMask;
        private static float Foot(PlayerControls player)
        { var capsule = player.GetComponent<CapsuleCollider>(); return capsule == null ? player.transform.position.y : capsule.bounds.min.y; }
        public bool Plan(PlayerControls chef, GameObject item, PlayerControls player, out DropPlan plan)
        {
            plan = null;
            if (item == null || player == null || player == chef || !player.gameObject.activeInHierarchy || player.m_bRespawning) return false;
            var config = PlayerInputLookup.GetInputConfig();
            var entry = config == null || player.PlayerIDProvider == null ? null : config.GetInputConfigEntry(player.PlayerIDProvider.GetID());
            if (entry == null || !entry.IsLocal()) return false;
            Vector3 between = chef.transform.position - player.transform.position; between.y = 0;
            if (between.magnitude > 1.6f || Mathf.Abs(Foot(chef) - Foot(player)) > .65f) return false;
            var selected = chef.CurrentInteractionObjects.m_TheOriginalHandlePickup;
            if (chef.CurrentInteractionObjects.m_iHandlePlacement != null)
            {
                if (selected == null || selected.GetComponent<ServerAttachStation>() == null || ActionObservations.Occupant(selected) != null ||
                    selected.GetComponent<CookingStation>() != null || selected.GetComponent<ServerWashingStation>() != null || selected.GetComponent<ServerRubbishBin>() != null ||
                    Vector3.Distance(selected.transform.position, player.transform.position) > 1.6f || !ActionObservations.CanPlace(chef, selected)) return false;
                plan = new DropPlan { Counter = selected, Landing = selected.transform.position }; return true;
            }
            var physical = item.GetComponent<PhysicalAttachment>(); var shape = item.GetComponent<Collider>();
            var ground = chef.GetComponent<GroundCast>();
            if (physical == null || shape == null || ground == null) return false;
            groundMask = ActionObservations.Read<LayerMask>(ground, "m_landscapeMask").value;
            var parameters = physical.m_rigidBodyParams;
            if (parameters == null || !parameters.m_useGravity || parameters.m_drag < 0 || Time.fixedDeltaTime < .001f || Time.fixedDeltaTime > .1f) return false;
            if (physical.m_motion == null) return false;
            Vector3 launch = physical.m_motion.GetVelocity();
            if (launch.magnitude > .3f) return false;
            if (wind.Prepare(chef, item) != null) return false;
            float radius;
            if (!ItemBody.Radius(item, out radius)) return false;
            if (physical.m_container != null && (!physical.m_container.useGravity || ((int)physical.m_container.constraints & 14) != 0)) return false;
            var path = ThrowFlight.Simulate(item.transform.position, chef.transform.forward, launch, Physics.gravity,
                physical.m_container == null ? parameters.m_drag : physical.m_container.drag, 1.5f, Time.fixedDeltaTime, wind);
            for (int i = 1; i < path.Points.Count; i++)
            {
                Vector3 from = path.Points[i - 1], segment = path.Points[i] - from;
                int count = Physics.SphereCastNonAlloc(from, radius, segment.normalized, hits, segment.magnitude, ~0, QueryTriggerInteraction.Collide);
                if (count >= hits.Length) return false;
                float closest = Single.MaxValue; RaycastHit contact = new RaycastHit();
                for (int j = 0; j < count; j++)
                {
                    Collider c = hits[j].collider;
                    if (c == null || c.transform.IsChildOf(chef.transform) || c.transform.IsChildOf(item.transform)) continue;
                    if (c.GetComponentInParent<ServerFireHazard>() != null || c.GetComponentInParent<RespawnCollider>() != null || c.GetComponentInParent<Teleportal>() != null) return false;
                    if (c.isTrigger) continue;
                    if (hits[j].distance < closest) { closest = hits[j].distance; contact = hits[j]; }
                }
                if (closest == Single.MaxValue) continue;
                if ((groundMask & (1 << contact.collider.gameObject.layer)) == 0 || contact.normal.y < .6f || Mathf.Abs(contact.point.y - Foot(player)) > .35f) return false;
                Vector3 delta = contact.point - player.transform.position; delta.y = 0;
                if (delta.magnitude > 1.25f) return false;
                plan = new DropPlan { Landing = contact.point, InitialVelocity = launch, FlightSeconds = path.Times[i] }; return true;
            }
            return false;
        }
        public bool Received(GameObject item, PlayerControls player, DropPlan plan)
        {
            if (item == null || !item.activeInHierarchy) return false;
            if (System.Object.ReferenceEquals(ActionObservations.Held(player), item)) return true;
            if (plan.Counter != null) return Vector3.Distance(plan.Counter.transform.position, player.transform.position) <= 1.6f && System.Object.ReferenceEquals(ActionObservations.Occupant(plan.Counter), item);
            var physical = item.GetComponent<PhysicalAttachment>(); var attachment = GameObjectUtils.RequestInterface<IAttachment>(item);
            if (attachment == null || attachment.IsAttached() || physical == null || physical.m_motion == null || physical.m_motion.GetVelocity().magnitude > .3f) return false;
            Vector3 offset = item.transform.position - player.transform.position; offset.y = 0;
            if (offset.magnitude > 1.5f) return false;
            var shape = item.GetComponent<Collider>();
            if (shape == null || Mathf.Abs(shape.bounds.min.y - Foot(player)) > .35f) return false;
            int count = Physics.RaycastNonAlloc(shape.bounds.center + Vector3.up * .05f, Vector3.down, hits, 1, groundMask, QueryTriggerInteraction.Ignore);
            if (count >= hits.Length) return false;
            for (int i = 0; i < count; i++) if (hits[i].normal.y > .6f && Mathf.Abs(hits[i].point.y - Foot(player)) < .35f) return true;
            return false;
        }
    }
}
