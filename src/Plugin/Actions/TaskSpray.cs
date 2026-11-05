using System;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private LogisticsItem SprayTool(string component)
        {
            inventory.Refresh(chef);
            LogisticsItem best = null;
            foreach (LogisticsItem item in inventory.Items)
                if (TaskNative.Component(item.Item, component) != null && LogisticsChecks.Collect(chef, item, "move_item", true) == null)
                {
                    if (System.Object.ReferenceEquals(item.Item, ActionObservations.Held(chef))) return item;
                    if (best == null) best = item;
                }
            return best;
        }
        private bool SprayStance(GameObject tool, GameObject target, out Vector3 stance)
        {
            stance = Vector3.zero; if (target == null) return false;
            Component spray = TaskNative.Component(tool, "SprayingUtensil"); if (spray == null) return false;
            float range = ActionObservations.Read<float>(spray, "m_sprayDistance");
            float best = Single.MaxValue;
            foreach (float height in navigation.ReleaseHeights(target.transform.position, range + 1))
            for (int ring = 0; ring < 3; ring++)
                for (int n = 0; n < 16; n++)
                {
                    float distance = Math.Max(1.25f, range * (.45f + ring * .2f));
                    float angle = n * Mathf.PI / 8; Vector3 position;
                    Vector3 seed = target.transform.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * distance;
                    seed.y = height;
                    if (!navigation.TryStance(seed, out position)) continue;
                    if (Vector3.Distance(position, target.transform.position) > range + .55f) continue;
                    float cost = navigation.RouteCost(position);
                    if (cost < best) { best = cost; stance = position; }
                }
            return best != Single.MaxValue;
        }
        private TaskStep SprayUntil(GameObject tool, GameObject target, string phase, Func<bool> complete)
        {
            float retry = 0;
            return new TaskStep { Phase = phase, Progress = delegate {
                if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), tool)) throw new InvalidOperationException("spray_tool_changed");
                if (complete()) { input(0); return true; }
                if (navigation.Action.State == "navigating") return false;
                Component spray = TaskNative.Component(tool, "ServerSprayingUtensil");
                if (target.activeInHierarchy && (bool)TaskNative.Call(spray, "IsInSpray", target.transform)) { input(2); return false; }
                input(0);
                if (Time.realtimeSinceStartup >= retry)
                {
                    retry = Time.realtimeSinceStartup + .2f; Vector3 position;
                    RefreshWorld();
                    if (SprayStance(tool, target, out position)) navigation.StartPosition(chef, Run.Owner, target, position, true);
                }
                return false;
            } };
        }
    }
}
