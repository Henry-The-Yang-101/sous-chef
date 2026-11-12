using System;
using System.Collections.Generic;
using System.Reflection;
using SousChef.Core;
using SousChef.Plugin.Actions;
using UnityEngine;

internal static partial class Program
{
    private static void SchedulingTests()
    {
        var w = new World();
        var request = new TaskRequest { Action = "prepare_near_player" };
        request.Items.Add(new PreparationRequest { Selector = "fish", Preparation = "chopped", Quantity = 1 });
        w.Tasks.ConfigureScheduling(w.Chef, request);
        var owner = new TaskActions(w.Navigation, new InteractionActions(w.Navigation, _ => {}, () => {}), _ => {}, () => {});
        var food = w.Ingredient("fish");
        var board = w.Target; board.Add(new ServerWorkstation());
        board.GetComponent<ServerAttachStation>().Item = food;
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        ((HashSet<GameObject>)typeof(TaskActions).GetField("ownedFood", fields).GetValue(owner)).Add(food);
        ((HashSet<GameObject>)typeof(TaskActions).GetField("ownedBoards", fields).GetValue(owner)).Add(board);
        w.Tasks.ReservedByOther = (task, item) => owner.Owns(item);
        Check(owner.Owns(board) && owner.Owns(food), "a scheduled job owns both worked food and its occupied board");
        Check(w.Tasks.WaitingForOwner(owner, "waiting_for_chopping_board"), "blocked board work identifies the actual resource owner for inheritance");
        var successor = w.Ingredient("chopped fish");
        typeof(TaskActions).GetField("step", fields).SetValue(owner, new TaskStep { Phase = "work_at", Target = board });
        typeof(TaskActions).GetField("childStarted", fields).SetValue(owner, true);
        board.GetComponent<ServerAttachStation>().Item = successor;
        Check(owner.Owns(board) && owner.Owns(successor), "the board protects a native chopping successor before its owner observes completion");
        typeof(TaskActions).GetField("childStarted", fields).SetValue(owner, false);
        board.GetComponent<ServerAttachStation>().Item = null;
        Check(!owner.Owns(board), "a human clearing the board releases its reservation");
        board.GetComponent<ServerAttachStation>().Item = w.Ingredient("onion");
        Check(!owner.Owns(board), "a later unrelated occupant never inherits the previous food reservation");
        var deliveries = new DeliveryHistory(); deliveries.Add(food);
        w.Tasks.ShareDeliveries(deliveries);
        board.GetComponent<ServerAttachStation>().Item = food;
        w.Hold(food);
        Check(!w.Tasks.Suspend(), "scheduled work finishes clearing foreign cargo before retaining a checkpoint");
        w.Hold(null);
        var other = new TaskActions(w.Navigation, new InteractionActions(w.Navigation, _ => {}, () => {}), _ => {}, () => {});
        other.ConfigureScheduling(w.Chef, request);
        other.ReservedByOther = (task, item) => owner.Owns(item);
        var pendingPickup = new TaskStep { Phase = "pick_up", EmptyHands = true, UseHeld = held => true };
        typeof(TaskActions).GetField("step", fields).SetValue(other, pendingPickup);
        w.Hold(food);
        typeof(TaskActions).GetMethod("ReconcileHands", fields).Invoke(other, new object[0]);
        Check(!pendingPickup.Succeeded && typeof(TaskActions).GetField("handsPlan", fields).GetValue(other) != null,
            "switching portions parks another job's held food instead of adopting it through the pickup shortcut");
        var adopter = new TaskActions(w.Navigation, new InteractionActions(w.Navigation, _ => {}, () => {}), _ => {}, () => {});
        adopter.ConfigureScheduling(w.Chef, request); adopter.ReservedByOther = (task, item) => false;
        var adoptedPickup = new TaskStep { Phase = "pick_up", Target = board, EmptyHands = true, UseHeld = held => true };
        typeof(TaskActions).GetField("step", fields).SetValue(adopter, adoptedPickup);
        typeof(TaskActions).GetMethod("ReconcileHands", fields).Invoke(adopter, new object[0]);
        Check(adoptedPickup.Succeeded && adopter.Owns(food), "adopting unowned held food establishes ownership before another scheduling decision");
        w.Hold(null);
        var localDeliveries = (HashSet<GameObject>)typeof(TaskActions).GetField("deliveredItems", fields).GetValue(w.Tasks);
        Check(!localDeliveries.Contains(food), "a shared handoff exclusion is not this job's completion evidence");
        w.Tasks.Reset("test_reset");
        Check(deliveries.Contains(food), "resetting one job cannot erase the kitchen's delivery history");
        var pan = new GameObject("reusable pan"); var contents = pan.Add(new ServerIngredientContainer());
        contents.Contents.Add(new AssembledDefinitionNode("rice")); deliveries.Add(pan);
        Check(deliveries.Contains(pan), "delivered food remains excluded while its vessel is full");
        contents.Contents.Clear(); deliveries.Observe();
        Check(!deliveries.Contains(pan), "emptying a delivered vessel makes its capacity reusable");
        deliveries.Add(pan); deliveries.Observe();
        Check(deliveries.Contains(pan), "an explicit empty utensil handoff is still counted only once");
        contents.Contents.Add(new AssembledDefinitionNode("rice")); deliveries.Observe();
        contents.Contents.Clear(); deliveries.Observe();
        Check(!deliveries.Contains(pan), "a handed-off empty utensil becomes reusable after a later fill and empty cycle");
        w.Tasks.ReservedByOther = (task, item) => false;
        var authorize = typeof(TaskActions).GetMethod("AuthorizeScheduledStep", fields);
        Check(!(bool)authorize.Invoke(w.Tasks, new object[] { new TaskStep { Phase = "pick_up", Target = board } }),
            "a preselected pickup cannot reuse an item delivered by another portion before resumption");
        var demandType = typeof(TaskActions).GetNestedType("Demand", BindingFlags.NonPublic);
        var demandListType = typeof(List<>).MakeGenericType(demandType);
        var missing = typeof(TaskActions).GetMethod("MissingDemand", BindingFlags.Static | BindingFlags.NonPublic);
        var fish = Leaf("fish");
        var stock = new List<AssembledDefinitionNode> { fish };
        var needs = (System.Collections.IList)Activator.CreateInstance(demandListType);
        missing.Invoke(null, new object[] { Recipe(fish, fish), null, 20f, stock, needs });
        Check(needs.Count == 1 && stock.Count == 0, "one existing portion covers only one unit of repeated recipe demand");
        var cooked = new CookedCompositeAssembledNode { m_composition = new AssembledDefinitionNode[] { fish }, m_cookingStep = new CookingStepData { name = "pan", m_uID = 1 } };
        stock.Add(fish); needs.Clear();
        missing.Invoke(null, new object[] { cooked, null, 20f, stock, needs });
        Check(needs.Count == 1 && stock.Count == 1, "raw stock does not satisfy a cooked processing boundary");
        stock.Clear(); stock.Add(cooked); needs.Clear();
        missing.Invoke(null, new object[] { cooked, null, 20f, stock, needs });
        Check(needs.Count == 0 && stock.Count == 0, "an existing cooked output covers its exact processing demand");
        var duty = new TaskRequest { Action = "deliver_completed_dishes" };
        var offer = w.Tasks.SchedulingOffer(w.Chef, duty, false);
        Check(!offer.Ready, "a serving subscription without a valid dish cannot monopolize the chef");
        var dish = new WorkOffer { Class = 2, Slack = 8, Remaining = 3 };
        var hopeless = new WorkOffer { Class = 2, Slack = -20, Remaining = 40, Feasible = false };
        Check(dish.PreferTo(hopeless), "estimated impossible work does not starve an achievable order");
    }
}
