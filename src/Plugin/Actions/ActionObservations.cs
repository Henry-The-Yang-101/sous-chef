using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal static class ActionObservations
    {
        private static readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
        private static readonly MethodInfo endInteraction = typeof(ClientPlayerControlsImpl_Default).GetMethod("EndInteraction", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo interactionMessage = typeof(PlayerControls).Assembly.GetType("ClientMessenger").GetMethod("ChefEventMessage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(Team17.Online.Multiplayer.Messaging.ChefEventMessage.ChefEventType), typeof(GameObject), typeof(MonoBehaviour) }, null);
        public static void EndOwnedInteraction(PlayerControls chef, GameObject target)
        {
            var impl = chef.GetComponent<ClientPlayerControlsImpl_Default>();
            if (impl == null || impl.GetCurrentlyInteracting() == null || !Matches(impl.GetCurrentlyInteracting().gameObject, target)) return;
            if (endInteraction == null || interactionMessage == null) throw new InvalidOperationException("unsupported_interaction_cleanup");
            interactionMessage.Invoke(null, new object[] { Team17.Online.Multiplayer.Messaging.ChefEventMessage.ChefEventType.Interact, chef.gameObject, null });
            endInteraction.Invoke(impl, new object[0]);
        }
        public static T Read<T>(object instance, string name)
        {
            if (instance == null) throw new InvalidOperationException("observation_unavailable");
            Type type = instance.GetType(); string key = type.FullName + ":" + name; FieldInfo field;
            if (!fields.TryGetValue(key, out field))
            {
                for (Type t = type; t != null && field == null; t = t.BaseType)
                    field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                fields[key] = field;
            }
            if (field == null) throw new InvalidOperationException("observation_unavailable");
            return (T)field.GetValue(instance);
        }
        public static GameObject Held(PlayerControls chef)
        {
            var carrier = chef.GetComponent<ServerPlayerAttachmentCarrier>();
            return carrier == null ? null : carrier.InspectCarriedItem();
        }
        public static GameObject Occupant(GameObject target)
        {
            var station = target == null ? null : target.GetComponent<ServerAttachStation>();
            return station == null ? null : station.InspectItem();
        }
        public static GameObject Receiver(GameObject target)
        {
            GameObject occupant = Occupant(target);
            return occupant != null ? occupant : target;
        }
        public static ServerIngredientContainer Container(GameObject target)
        {
            GameObject receiver = Receiver(target);
            return receiver == null ? null : receiver.GetComponent<ServerIngredientContainer>();
        }
        public static AssembledDefinitionNode Composition(GameObject item)
        {
            var definition = item == null ? null : GameObjectUtils.RequestInterface<IOrderDefinition>(item);
            return definition == null ? null : definition.GetOrderComposition();
        }
        public static int MatchingCount(ServerIngredientContainer container, AssembledDefinitionNode ingredient)
        {
            int result = 0;
            if (container != null && ingredient != null)
                foreach (AssembledDefinitionNode node in container.GetContents())
                    if (AssembledDefinitionNode.Matching(node, ingredient)) result++;
            return result;
        }
        public static int StackCount(GameObject item)
        {
            if (item == null) return 0;
            var stack = item.GetComponent<ServerStack>();
            if (stack != null) return Read<ICollection>(stack, "m_stackItems").Count;
            return item.GetComponent<ServerPlate>() != null ? 1 : 0;
        }
        public static int DirtyCount(GameObject target)
        {
            var sink = target.GetComponent<ServerWashingStation>();
            return sink == null ? -1 : Read<int>(sink, "m_plateCount");
        }
        public static int CleanCount(GameObject target)
        {
            var sink = target.GetComponent<ServerWashingStation>();
            if (sink == null) return -1;
            var drying = Read<ServerAttachStation>(sink, "m_dryingAttachStation");
            return drying == null ? -1 : StackCount(drying.InspectItem());
        }
        public static bool? ToggleState(GameObject target)
        {
            var toggle = target.GetComponent<ClientTriggerToggleOnAnimator>();
            var cosmetics = target.GetComponent<ClientToggleSwitchCosmeticDecisions>();
            if (toggle == null && cosmetics != null) toggle = Read<ClientTriggerToggleOnAnimator>(cosmetics, "m_toggleOnAnimator");
            return toggle == null ? (bool?)null : toggle.CurrentState;
        }
        public static bool Selected(PlayerControls chef, GameObject target)
        {
            var observed = chef.CurrentInteractionObjects;
            return Matches(observed.m_TheOriginalHandlePickup, target) ||
                (observed.m_interactable != null && Matches(observed.m_interactable.gameObject, target));
        }
        public static bool Matches(GameObject a, GameObject b)
        {
            return a != null && b != null && (a == b || a.transform.IsChildOf(b.transform) || b.transform.IsChildOf(a.transform));
        }
        public static bool CanPlace(PlayerControls chef, GameObject target)
        {
            var carrier = chef.GetComponent<ClientPlayerAttachmentCarrier>(); var observed = chef.CurrentInteractionObjects;
            Vector3 f = chef.transform.forward;
            return carrier != null && observed.m_iHandlePlacement != null && Matches(observed.m_TheOriginalHandlePickup, target) &&
                observed.m_iHandlePlacement.CanHandlePlacement(carrier, new Vector2(f.x, f.z), new PlacementContext(PlacementContext.Source.Player));
        }
        public static bool CanPickUp(PlayerControls chef, GameObject target)
        {
            var carrier = chef.GetComponent<ClientPlayerAttachmentCarrier>(); var observed = chef.CurrentInteractionObjects;
            return carrier != null && observed.m_iHandlePickup != null && Matches(observed.m_TheOriginalHandlePickup, target) && observed.m_iHandlePickup.CanHandlePickup(carrier);
        }
        public static bool CanUse(PlayerControls chef, GameObject target)
        {
            var interact = chef.CurrentInteractionObjects.m_interactable;
            return interact != null && Matches(interact.gameObject, target) && interact.CanInteract(chef.gameObject);
        }
    }
}
