using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterFire() { Register("put_out_fire", FireAvailable, PutOutFire); }
        private LogisticsItem Extinguisher()
        {
            inventory.Refresh(chef);
            foreach (LogisticsItem item in inventory.Items)
                if (TaskNative.Component(item.Item, "ServerFireExtinguishSpray") != null && LogisticsChecks.Collect(chef, item, "move_item") == null) return item;
            return null;
        }
        private bool FireAvailable()
        {
            GameObject held = ActionObservations.Held(chef);
            if (held != null && TaskNative.Component(held, "ServerFireExtinguishSpray") == null) return false;
            LogisticsItem tool = Extinguisher(); if (tool == null) return false;
            foreach (ServerFlammable fire in ServerFlammable.GetAllOnFire()) if (fire != null && fire.OnFire()) return true;
            return false;
        }
        private bool FireStance(GameObject tool, ServerFlammable fire, out Vector3 stance)
        {
            stance = Vector3.zero; if (fire == null || !fire.OnFire()) return false;
            Component spray = TaskNative.Component(tool, "SprayingUtensil"); if (spray == null) return false;
            float range = ActionObservations.Read<float>(spray, "m_sprayDistance");
            float best = Single.MaxValue;
            foreach (float height in navigation.ReleaseHeights(fire.transform.position, range + 1))
            for (int ring = 0; ring < 3; ring++)
                for (int n = 0; n < 16; n++)
                {
                    float distance = Math.Max(1.25f, range * (.45f + ring * .2f));
                    float angle = n * Mathf.PI / 8; Vector3 position;
                    Vector3 seed = fire.transform.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * distance;
                    seed.y = height;
                    if (!navigation.TryStance(seed, out position)) continue;
                    if (Vector3.Distance(position, fire.transform.position) > range + .55f) continue;
                    float cost = navigation.RouteCost(position);
                    if (cost < best) { best = cost; stance = position; }
                }
            return best != Single.MaxValue;
        }
        private TaskStep SprayFire(GameObject tool, ServerFlammable fire)
        {
            float retry = 0;
            return new TaskStep { Phase = "extinguishing", Progress = delegate {
                if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), tool)) throw new InvalidOperationException("extinguisher_changed");
                if (fire == null || !fire.OnFire()) { input(0); return true; }
                if (navigation.Action.State == "navigating") return false;
                Component spray = TaskNative.Component(tool, "ServerFireExtinguishSpray");
                if (fire.gameObject.activeInHierarchy && (bool)TaskNative.Call(spray, "IsInSpray", fire.transform)) { input(2); return false; }
                input(0);
                if (Time.realtimeSinceStartup >= retry)
                {
                    retry = Time.realtimeSinceStartup + .2f; Vector3 position;
                    RefreshWorld();
                    if (FireStance(tool, fire, out position)) navigation.StartPosition(chef, Run.Owner, fire.gameObject, position, true);
                }
                return false;
            } };
        }
        private IEnumerable<TaskStep> PutOutFire()
        {
            LogisticsItem source = null;
            yield return Until("waiting_for_extinguisher", delegate { source = Extinguisher(); return source != null; });
            GameObject tool = source.Item;
            if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), tool)) yield return Do("pick_up", source.Source);
            while (true)
            {
                RefreshWorld();
                ServerFlammable selected = null; Vector3 stance = Vector3.zero;
                foreach (ServerFlammable fire in ServerFlammable.GetAllOnFire())
                    if (FireStance(tool, fire, out stance)) { selected = fire; break; }
                if (selected == null)
                {
                    if (!botSearchComplete)
                    { yield return Until("waiting_for_fire_routes", delegate { RefreshWorld(); return botSearchComplete; }); continue; }
                    break;
                }
                yield return WalkTo(selected.gameObject, stance);
                yield return SprayFire(tool, selected);
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
