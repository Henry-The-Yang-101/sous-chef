using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Environmental interruptions retain the original request indefinitely.
    internal sealed class ActionRecovery
    {
        private float since = -1;
        private string reason;
        public int Count { get; private set; }
        public bool Active { get { return since >= 0; } }
        public float Elapsed { get { return Active ? Time.realtimeSinceStartup - since : 0; } }
        public void Reset() { Clear(); Count = 0; }
        public void Clear() { since = -1; reason = null; }
        public void ContinueFrom(ActionRecovery previous)
        {
            if (!previous.Active || (Active && since <= previous.since)) return;
            since = previous.since;
            if (reason == null) reason = previous.reason;
        }
        public bool Wait(string failure)
        {
            if (!Transient(failure)) return false;
            if (!Active) { since = Time.realtimeSinceStartup; Count++; }
            reason = failure;
            return true;
        }
        public object Describe()
        {
            return Json.Object("reason", reason, "elapsedSeconds", Elapsed,
                "graceSeconds", null, "indefinite", true, "count", Count);
        }
        public static bool Transient(string reason)
        {
            switch (reason)
            {
                case "target_unavailable": case "destination_unavailable": case "destination_unreachable":
                case "planning_in_progress": case "planning_budget_exceeded": case "no_safe_start":
                case "not_grounded": case "route_blocked": case "next_step_unsafe":
                case "unsafe_alignment_stance": case "flow_prevents_walking":
                case "no_progress": case "alignment_failed": case "navigation_timeout": case "ground_contact_timeout":
                case "pickup_unavailable": case "placement_unavailable": case "interaction_unavailable":
                case "processing_not_ready": case "no_work_progress":
                    return true;
                default: return false;
            }
        }
    }
}
