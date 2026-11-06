using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // One ticket, finite assigned objects, and a persistent selected base. Unlike
    // preparation lanes, an addition satisfies part of a recipe, not a handoff.
    internal sealed class AssemblySchedule
    {
        public AssemblyOrder Order;
        public readonly List<GameObject> Bases = new List<GameObject>(), Components = new List<GameObject>(), Equipment = new List<GameObject>();
        public GameObject Base;
        public long Additions;
        public string Waiting;
        public bool Verified;
        public readonly Dictionary<GameObject, float> RetryAt = new Dictionary<GameObject, float>();
        public object Describe()
        {
            return Json.Object("orderId", Order.Id, "recipe", Order.Recipe.name, "baseInstanceId", Base == null ? null : (object)Base.GetInstanceID(),
                "verifiedAdditions", Additions, "compositionVerified", Verified, "waitingReason", Waiting);
        }
    }
}
