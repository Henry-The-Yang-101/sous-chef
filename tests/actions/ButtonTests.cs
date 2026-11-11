using System;
using System.Linq;
using SousChef.Core;
using UnityEngine;

internal static partial class Program
{
    private static GameObject Button(World w, bool placement = false)
    {
        var button = w.Delivery(); button.name = "button";
        button.Add(new ClientInteractable { UsePlacementButton = placement }); button.Add(new ServerInteractable());
        return button;
    }
    private static string Press(World w, GameObject target, string desired = "trigger")
    {
        return w.Tasks.Start(w.Chef, 1, new ControlCommand { Id = "button", Operation = "task",
            Task = new TaskRequest { Action = "press_button", Target = w.IdOf(target), DesiredState = desired } });
    }
    private static void ButtonTasks()
    {
        {
            var w = new World(); var terminal = Button(w); terminal.Add(new ClientSessionInteractable());
            var tool = Button(w); tool.Add(new PhysicalAttachment());
            foreach (var target in new[] { terminal, tool, w.Target })
            {
                var identity = Json.Object(); SousChef.Plugin.Actions.HarnessOptions.Target(identity, target);
                Check(!((string[])identity["possibleOperations"]).Contains("press_button"), "sessions, portable tools and workstations are not one-shot buttons");
                Check(Press(w, target) == "button_target_unavailable", "native button binding enforces the same discovery predicate");
            }
            var legacy = Json.Object(); SousChef.Plugin.Actions.HarnessOptions.Target(legacy, terminal);
            Check(((string[])legacy["possibleOperations"]).Contains("activate"), "session exclusion preserves the explicit native activate contract");
        }
        foreach (bool placement in new[] { false, true })
        {
            var w = new World(); var button = Button(w, placement); var other = Button(w);
            other.transform.position = new Vector3(-2, 0, 0);
            Check(Press(w, button) == null, "exact button task starts with a checked route");
            w.Tick(); Check(w.Skills.Run.Operation == "activate" && w.Navigation.Resolve(w.Skills.Run.Target) == button,
                "task delegates approach and activation to the existing primitive for the exact selected button");
            Check(w.Inputs.All(i => i == 0), "no press before approach finishes");
            w.ArriveAt(button); w.Verify();
            Check(w.Inputs.Contains(placement ? 1 : 2), "native control chooses its own pickup/use binding");
            button.GetComponent<ServerInteractable>().Triggered(new GameObject("human"), new Vector2()); w.Tick();
            Check(w.Tasks.Run.Running, "another chef's trigger does not finish the button task");
            button.GetComponent<ServerInteractable>().Triggered(w.Chef.gameObject, new Vector2()); w.Tick(); w.Tick();
            Check(w.Tasks.Run.State == "succeeded" && w.Releases == 1 && w.Inputs.Last() == 0,
                "P2-specific trigger completes the parent exactly once with neutral input");
            Check(button.GetComponent<ServerInteractable>().Triggered == null, "completion removes the native trigger listener");
        }
        {
            var w = new World(); var button = Button(w); var cargo = w.Ingredient("tomato"); w.Hold(cargo);
            var counter = w.Delivery(); counter.transform.position = new Vector3(-2, 0, 0);
            Check(Press(w, button) == null, "held cargo does not hide a reachable button task");
            Check(TaskUntil(w, () => w.Skills.Run.Operation == "place_at" && w.Skills.Run.Running), "button task reuses safe hand clearing");
            var storage = w.Navigation.Resolve(w.Skills.Run.Target);
            w.ArriveAt(storage); storage.GetComponent<ServerAttachStation>().Item = cargo; w.Hold(null); w.Verify();
            Check(TaskUntil(w, () => w.Skills.Run.Operation == "activate" && w.Skills.Run.Running), "the same task resumes activation after parking cargo");
            Check(w.Navigation.Resolve(w.Skills.Run.Target) == button, "hand clearing preserves the exact selected button");
            w.Tasks.Cancel("stopped"); Check(w.Releases == 1 && w.Inputs.Last() == 0, "Stop cancels navigation and releases the parent once");
        }
        {
            var w = new World(); var button = Button(w); button.Add(new ClientTriggerToggleOnAnimator { CurrentState = true });
            Check(Press(w, button, "on") == null, "observed toggle accepts explicit on state");
            w.Tick(); w.ArriveAt(button); w.Tick();
            Check(w.Tasks.Run.State == "succeeded" && w.Inputs.All(i => i == 0), "already-on switch completes without toggling it off");
        }
        {
            var w = new World(); var button = Button(w);
            Check(Press(w, button, "on") == "unsupported_activation_state", "unknown state cannot accept on/off");
            Check(Press(w, w.Target) == "button_target_unavailable", "a workstation cannot be bound as a button");
            Check(Press(w, button) == null, "trigger remains supported"); w.Tick();
            button.activeInHierarchy = false; w.Tick();
            Check(w.Tasks.Run.Reason == "button_target_unavailable" && !w.Tasks.Run.Running && w.Inputs.All(i => i == 0),
                "vanished target fails instead of substituting another control");
        }
        {
            var w = new World(); var button = Button(w); Check(Press(w, button) == null, "unacknowledged press fixture starts");
            w.Tick(); w.ArriveAt(button); w.Verify(); w.Tick(3); w.Tick(3);
            Check(w.Tasks.Run.State == "failed" && w.Tasks.Run.Reason == "outcome_not_observed", "missing trigger acknowledgement fails without replaying the press");
            int presses = w.Inputs.Count(i => i != 0); w.Tick(10);
            Check(w.Inputs.Count(i => i != 0) == presses, "no new input after an unverified activation");
        }
    }
}
