using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterFire() { Register("put_out_fire", FireAvailable, PutOutFire); }
        private bool FireAvailable()
        {
            LogisticsItem tool = SprayTool<ServerFireExtinguishSpray>(); if (tool == null) return false;
            foreach (ServerFlammable fire in ServerFlammable.GetAllOnFire()) if (fire != null && fire.OnFire()) return true;
            return false;
        }
        private IEnumerable<TaskStep> PutOutFire()
        {
            while (MoreRequested)
            {
                yield return Until("waiting_for_fire", FireAvailable);
                foreach (TaskStep step in ExtinguishReachableFires()) yield return step;
                completed++;
            }
        }
        private IEnumerable<TaskStep> ExtinguishReachableFires()
        {
            LogisticsItem source = null;
            yield return Until("waiting_for_extinguisher", delegate { source = SprayTool<ServerFireExtinguishSpray>(); return source != null; });
            if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), source.Item))
            {
                TaskStep pickup = Do("pick_up", source.Source);
                pickup.UseHeld = held => TaskNative.Component<ServerFireExtinguishSpray>(held) != null;
                yield return pickup;
            }
            GameObject tool = ActionObservations.Held(chef);
            while (true)
            {
                RefreshWorld();
                ServerFlammable selected = null; Vector3 stance = Vector3.zero;
                foreach (ServerFlammable fire in ServerFlammable.GetAllOnFire())
                    if (fire != null && fire.OnFire() && SprayStance(tool, fire.gameObject, out stance)) { selected = fire; break; }
                if (selected == null)
                {
                    if (!botSearchComplete)
                    { yield return Until("waiting_for_fire_routes", delegate { RefreshWorld(); return botSearchComplete; }); continue; }
                    break;
                }
                yield return WalkTo(selected.gameObject, stance);
                yield return SprayUntil(tool, selected.gameObject, "extinguishing", () => selected == null || !selected.OnFire());
                // Re-scan after each extinguish: native fire can spread while
                // walking/spraying. Completion includes newly reachable fires.
            }
            GameObject home = null;
            yield return Until("returning_extinguisher", delegate {
                home = Parking(tool, inventory.Home(tool)); return home != null || botSearchComplete;
            });
            if (home != null) yield return Do("place_at", home);
        }
    }
}
