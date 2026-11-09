using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed class AssemblyOrder
    {
        public string Id;
        public OrderController.ServerOrderData Ticket;
        public ServerOrderControllerBase Controller;
        public ServerKitchenFlowControllerBase Flow;
        public OrderDefinitionNode Recipe;
        public AssembledDefinitionNode Goal;
        public bool Expires;
        public bool Live
        {
            get
            {
                if (Flow == null || Expires && Ticket.Remaining < 0 || !System.Object.ReferenceEquals(Ticket.RecipeListEntry.m_order, Recipe)) return false;
                return ActionObservations.Read<List<OrderController.ServerOrderData>>(Controller, "m_activeOrders").Contains(Ticket);
            }
        }
    }
    internal static class AssemblyOrders
    {
        public static List<AssemblyOrder> Observe(bool includeUnsupported = false)
        {
            var result = new List<AssemblyOrder>(); var seen = new HashSet<ServerOrderControllerBase>();
            foreach (var flow in UnityEngine.Object.FindObjectsOfType<ServerKitchenFlowControllerBase>())
                foreach (var station in UnityEngine.Object.FindObjectsOfType<ServerPlateStation>())
                {
                    var team = station.GetTeamID();
                    var monitor = flow.GetMonitorForTeam(team);
                    var controller = monitor == null ? null : monitor.OrdersController;
                    if (controller == null || !seen.Add(controller)) continue;
                    bool expires = ActionObservations.Read<bool>(controller, "m_enableOrderExpiration");
                    foreach (var ticket in ActionObservations.Read<List<OrderController.ServerOrderData>>(controller, "m_activeOrders"))
                    {
                        var recipe = ticket.RecipeListEntry == null ? null : ticket.RecipeListEntry.m_order;
                        if (recipe == null || expires && ticket.Remaining < 0) continue;
                        var goal = recipe.Simpilfy();
                        if (!RecipeProof.Supported(goal) || RecipeProof.Empty(goal)) goal = null;
                        if (goal == null && !includeUnsupported) continue;
                        result.Add(new AssemblyOrder { Id = flow.GetInstanceID() + ":" + team + ":" + ActionObservations.Read<uint>(ticket.ID, "m_id"),
                            Flow = flow, Controller = controller, Ticket = ticket, Recipe = recipe, Goal = goal, Expires = expires });
                    }
                }
            return result;
        }
        public static object Describe()
        {
            var result = new List<object>();
            foreach (var order in Observe()) result.Add(DescribeRecipe(order));
            return result;
        }
        public static Json.Record DescribeTicket(AssemblyOrder order)
        {
            return Json.Object("id", order.Id, "recipe", order.Recipe.name, "recipeId", order.Recipe.m_uID,
                "remainingSeconds", order.Ticket.Remaining, "expires", order.Expires);
        }
        public static Json.Record DescribeRecipe(AssemblyOrder order)
        {
            var ticket = DescribeTicket(order);
            ticket["composition"] = RecipeProof.Describe(order.Goal);
            ticket["platingStep"] = order.Recipe.m_platingStep == null ? null : order.Recipe.m_platingStep.name;
            return ticket;
        }
    }
}
