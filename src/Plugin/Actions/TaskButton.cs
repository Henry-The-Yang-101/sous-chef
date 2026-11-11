using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private GameObject buttonTarget;
        partial void RegisterButton() { RegisterForRequest("press_button", ButtonAvailable, PressButton); }

        private string BindButton()
        {
            if (request.Repetitions != 1 || !ControlCommand.ActivationState(request.DesiredState)) return "invalid_button_request";
            buttonTarget = navigation.Resolve(request.Target);
            if (!ActionObservations.ButtonTarget(buttonTarget)) return "button_target_unavailable";
            if (request.DesiredState != "trigger" && !ActionObservations.ToggleState(buttonTarget).HasValue)
                return "unsupported_activation_state";
            return null;
        }
        private bool ButtonAvailable(TaskRequest selected)
        {
            foreach (GameObject target in navigation.Targets())
                if ((selected == null || target == buttonTarget) && ActionObservations.ButtonTarget(target) && Reachable(target)) return true;
            return false;
        }
        private IEnumerable<TaskStep> PressButton()
        {
            // The shared task step parks held cargo, walks to this exact object,
            // and uses the ordinary native activation edge and outcome proof.
            // Never rebind a vanished control or replay an unverified press.
            TaskStep activation = Do("activate", buttonTarget, desiredState: request.DesiredState);
            activation.BeforePress = () => ActionObservations.ButtonTarget(buttonTarget);
            yield return activation;
            completed++;
        }
    }
}
