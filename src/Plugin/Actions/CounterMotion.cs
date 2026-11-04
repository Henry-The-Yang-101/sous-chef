using System;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // A read-only forecast learned from actual counter travel in this round.
    // Unknown or one-way motion is not assumed to return to a previous stop.
    internal sealed class CounterMotion
    {
        private bool sampled, visited, away;
        private Vector3 position, velocity, stop;
        private float time, lastVisit, period;
        public void Observe(Vector3 point, float now, bool humanCanReach)
        {
            if (sampled && now > time + .01f) velocity = (point - position) / (now - time);
            position = point; time = now; sampled = true;
            if (!humanCanReach) { if (visited && Vector3.Distance(point, stop) > .6f) away = true; return; }
            if (!visited) { stop = point; lastVisit = now; visited = true; return; }
            if (away && Vector3.Distance(point, stop) < .75f)
            { if (now - lastVisit > 1) period = now - lastVisit; lastVisit = now; away = false; }
        }
        public float ArrivalSeconds(float now)
        {
            if (!visited || !away) return Single.MaxValue;
            if (velocity.sqrMagnitude > .01f)
            {
                float duration = Vector3.Dot(stop - position, velocity) / velocity.sqrMagnitude;
                if (duration > 0 && duration <= 30 && Vector3.Distance(position + velocity * duration, stop) < .75f) return duration;
            }
            if (period > 0)
            {
                float phase = (now - lastVisit) % period;
                return Math.Max(.1f, period - phase);
            }
            return Single.MaxValue;
        }
    }
}
