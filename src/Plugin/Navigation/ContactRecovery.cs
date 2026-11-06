using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class WalkPhysics
    {
        // A moving counter can leave the native body slightly inside its face.
        // Only a live, supported connector moving out of that existing contact
        // may consume the contact skin. Planned nodes/edges keep normal margins.
        private bool EscapingContact(Collider obstacle, Vector3 sample, Vector3 destination)
        {
            // Box faces are convex: the native separation plane proves that a
            // straight outward connector cannot cross through the same object.
            // Ground, ceilings, triggers and hazards retain their separate rules.
            if (!(obstacle is BoxCollider) || obstacle.isTrigger || Dangerous(obstacle) || chef.GroundCollider == null) return false;
            Vector3 direction; float depth;
            Queries++;
            if (!Physics.ComputePenetration(capsule, capsule.transform.position, capsule.transform.rotation,
                obstacle, obstacle.transform.position, obstacle.transform.rotation, out direction, out depth) ||
                depth <= 0 || depth > Skin || Mathf.Abs(direction.y) > .01f) return false;
            Vector3 travel = destination - Position;
            if (Vector3.Dot(travel, direction) <= depth + .01f || Mathf.Abs(travel.y) > .04f ||
                Vector3.Dot(sample - Position, direction) < -.0001f) return false;
            Vector3 sampleDirection; float sampleDepth;
            Queries++;
            bool penetrating = Physics.ComputePenetration(capsule, capsule.transform.position + sample - Position, capsule.transform.rotation,
                obstacle, obstacle.transform.position, obstacle.transform.rotation, out sampleDirection, out sampleDepth);
            return !penetrating || sampleDepth <= depth + .0001f && Vector3.Dot(sampleDirection, direction) > .99f;
        }
    }
}
