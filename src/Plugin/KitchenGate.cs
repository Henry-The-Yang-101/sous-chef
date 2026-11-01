using UnityEngine;

namespace SousChef.Plugin
{
    internal sealed class KitchenGate
    {
        public Component Flow;
        public string Role;
        public string Reason;
        public bool InRound;

        public static KitchenGate Inspect()
        {
            // InRound is toggled by the flow's actual round lifecycle, not a scene
            // name heuristic. Require a live scene object: exclude loaded prefabs.
            var result = new KitchenGate { Reason = "outside_active_round", Role = "unknown" };
            foreach (var flow in Resources.FindObjectsOfTypeAll<ServerFlowControllerBase>())
            {
                if (!Live(flow)) continue;
                result.Flow = flow; result.Role = "host";
                result.InRound = flow.InRound;
                if (result.InRound) { result.Reason = "active_round"; return result; }
            }
            // An existing server flow is authoritative even if its client has not
            // yet received the end-of-round transition.
            if (result.Flow != null) return result;
            foreach (var flow in Resources.FindObjectsOfTypeAll<ClientFlowControllerBase>())
            {
                if (!Live(flow)) continue;
                result.Flow = flow; result.Role = "client"; result.InRound = flow.InRound;
                if (result.InRound) { result.Reason = "active_round"; return result; }
            }
            return result;
        }

        private static bool Live(Component component)
        {
            return component != null && component.gameObject.scene.IsValid() &&
                component.gameObject.scene.isLoaded && component.gameObject.activeInHierarchy;
        }
    }
}
