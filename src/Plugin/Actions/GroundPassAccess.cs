using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Shared by preview and execution for one handoff. Never command the
    // receiving chef. A support collider may span several walled-off islands.
    internal sealed class GroundPassAccess
    {
        public readonly Func<PlayerControls, Vector3, bool> CanLand;
        public readonly Func<PlayerControls, GameObject, bool> CanCollect;

        public GroundPassAccess(Func<PlayerControls, Vector3, bool> canLand, Func<PlayerControls, GameObject, bool> canCollect)
        { CanLand = canLand; CanCollect = canCollect; }
        private sealed class Rejection { public ThrowPlan Plan; public Vector3 Receiver; public Collider Floor; }
        private readonly List<Rejection> rejected = new List<Rejection>();
        public void Reset() { rejected.Clear(); }
        public void Reject(ThrowPlan plan, PlayerControls human)
        {
            if (plan == null || plan.StanceSupport == null || human == null || !Allows(plan.CurrentStance, human)) return;
            rejected.Add(new Rejection { Plan = plan, Floor = human.GroundCollider,
                Receiver = plan.StanceSupport.transform.InverseTransformPoint(human.transform.position) });
        }
        public bool Allows(Vector3 stance, PlayerControls human)
        {
            for (int i = rejected.Count - 1; i >= 0; i--)
            {
                var r = rejected[i]; var support = r.Plan.StanceSupport;
                if (support == null || r.Floor != human.GroundCollider ||
                    Vector3.Distance(support.transform.TransformPoint(r.Receiver), human.transform.position) > .4f)
                { rejected.RemoveAt(i); continue; }
                if (Vector3.Distance(r.Plan.CurrentStance, stance) < .4f) return false;
            }
            return true;
        }
    }
}
