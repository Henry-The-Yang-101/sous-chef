using System;

namespace SousChef.Core
{
    // A priority is recomputed from live observations, never retained as a stale heap key.
    public sealed class WorkOffer
    {
        public bool Ready = true;
        public string Reason, Stage;
        public int Class = 3;
        public float Slack = Single.MaxValue, Remaining = 20, Cost, Age;
        public bool PreferTo(WorkOffer other)
        {
            if (!Ready) return false;
            if (other == null || !other.Ready) return true;
            if (Class != other.Class) return Class < other.Class;
            // Deadline differences smaller than the estimate's uncertainty do not
            // justify repeatedly putting down and recovering the same cargo.
            if (Math.Abs(Slack - other.Slack) > 2) return Slack < other.Slack;
            float a = Remaining + Cost - Math.Min(8, Age * .1f);
            float b = other.Remaining + other.Cost - Math.Min(8, other.Age * .1f);
            return a + 1 < b;
        }
        public object Describe(string state)
        {
            return Json.Object("state", state, "stage", Stage, "reason", Reason,
                "class", Class, "slackSeconds", Slack == Single.MaxValue ? null : (object)Slack,
                "remainingChefSeconds", Remaining, "switchCostSeconds", Cost);
        }
    }
}
