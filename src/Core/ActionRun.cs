using System;

namespace SousChef.Core
{
    // Unity-free lifecycle shared by all interaction executors. A receipt is not
    // success until the executor supplies observed outcome evidence.
    public sealed class ActionRun
    {
        public static readonly string[] Names = { "navigate_to", "pick_up", "place_at", "add_ingredient", "work_at", "activate", "discard", "wait_until", "throw_to", "throw_to_player", "stop" };
        public static readonly string[] WorkConditions = { "work_complete", "one_plate_clean", "sink_empty" };
        public static readonly string[] WaitConditions = { "work_complete", "one_plate_clean", "sink_empty", "occupied", "empty", "cooked", "mixed", "reachable", "ready" };
        public int Owner { get; private set; }
        public string Operation { get; private set; }
        public string RequestId { get; private set; }
        public string Target { get; private set; }
        public string State { get; private set; }
        public string Phase { get; private set; }
        public string Reason { get; private set; }
        public long Revision { get; private set; }
        public double Started { get; private set; }
        public double Deadline { get; private set; }
        public bool Running { get { return Owner != 0; } }
        public ActionRun() { State = Phase = "idle"; }
        public bool Start(int owner, ControlCommand command, double now)
        {
            if (Running || owner <= 0) return false;
            Owner = owner; Operation = command.Operation; RequestId = command.Id; Target = command.Destination;
            Started = now; Deadline = now + (command.Milliseconds > 0 ? command.Milliseconds / 1000.0 : 60);
            State = "running"; Phase = "starting"; Reason = null; Revision++; return true;
        }
        public void Advance(string phase) { if (Running && Phase != phase) { Phase = phase; Revision++; } }
        public bool Expired(double now) { return Running && now >= Deadline; }
        public void Finish(string state, string reason)
        {
            if (!Running) return;
            if (state != "succeeded" && state != "failed" && state != "blocked" && state != "cancelled") throw new ArgumentException("invalid_terminal_state");
            Owner = 0; State = state; Phase = "idle"; Reason = reason; Revision++;
        }
        public object Describe(object details)
        {
            return Json.Object("state", State, "operation", Operation, "requestId", RequestId, "targetId", Target, "phase", Phase,
                "reason", Reason, "revision", Revision, "started", Started, "deadline", Deadline, "details", details);
        }
    }
}
