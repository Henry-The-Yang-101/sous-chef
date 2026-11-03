using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // One continuous interruption gets one grace window, even if its reason
    // changes. Waiting never extends the action's overall deadline.
    internal sealed class ActionRecovery
    {
        public const float GraceSeconds = .5f;
        private float since = -1;
        private string reason;
        public int Count { get; private set; }
        public bool Active { get { return since >= 0; } }
        public void Reset() { Clear(); Count = 0; }
        public void Clear() { since = -1; reason = null; }
        public bool Wait(string failure)
        {
            if (!Transient(failure)) return false;
            if (!Active) { since = Time.realtimeSinceStartup; Count++; }
            reason = failure;
            return Time.realtimeSinceStartup - since < GraceSeconds;
        }
        public object Describe()
        {
            return Json.Object("reason", reason, "elapsedSeconds", Active ? Time.realtimeSinceStartup - since : 0,
                "graceSeconds", GraceSeconds, "count", Count);
        }
        public static bool Transient(string reason)
        {
            switch (reason)
            {
                case "target_unavailable": case "destination_unavailable": case "destination_unreachable":
                case "planning_in_progress": case "planning_budget_exceeded": case "no_safe_start":
                case "not_grounded": case "route_blocked": case "next_step_unsafe":
                case "unsafe_alignment_stance": case "flow_prevents_walking":
                case "pickup_unavailable": case "placement_unavailable": case "interaction_unavailable":
                    return true;
                default: return false;
            }
        }
    }
}
