using System;
using System.Collections.Generic;
using System.Linq;
using SousChef.Core;
using SousChef.Plugin.Actions;
using SousChef.Plugin.Navigation;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
    private sealed class Placement : MonoBehaviour, IPlacement
    { public bool CanHandlePlacement(ICarrier carrier, Vector2 direction, PlacementContext context) { return false; } }
    private static void Main()
    {
        var actor = new GameObject("chef"); var chef = actor.Add(new PlayerControls());
        actor.Add(new ServerPlayerAttachmentCarrier()); actor.Add(new ClientPlayerAttachmentCarrier());
        var nav = new NavigationActions((direction, ms) => { throw new Exception("display moved chef"); }, () => {});
        var child = new InteractionActions(nav, buttons => { throw new Exception("display pressed buttons"); }, () => {});
        var jobs = new LogisticsActions(nav, child, buttons => {}, () => {});
        var raw = new GameObject("Tomato_Raw");
        raw.Add(new IngredientPropertiesComponent { m_ingredientOrderNode = new Definition { name = "Tomato", m_uID = 123 } });
        var supply = new GameObject("supply"); supply.Add(new PickupItemSpawner { m_itemPrefab = raw }); nav.RegisterItem(supply);
        var counter = new GameObject("occupied counter"); counter.Add(new ServerAttachStation()); counter.Add(new Placement()); nav.RegisterItem(counter);
        var pot = new GameObject("pot"); pot.Add(new PhysicalAttachment());
        pot.Add(new ItemPropertiesComponent { m_itemDefinition = new Definition { name = "CookingPot" } });
        var contents = pot.Add(new ServerIngredientContainer()); contents.Contents.Add(new AssembledDefinitionNode("Rice"));
        counter.GetComponent<ServerAttachStation>().Item = pot;
        // An unrelated prefab in memory must not become a level option.
        var unrelated = new GameObject("Chocolate"); unrelated.Add(new IngredientPropertiesComponent { m_ingredientOrderNode = new Definition { name = "Chocolate", m_uID = 999 } });
        var catalog = Json.Object("destinations", new List<object>(), "actions", new[] { Json.Object("name", "stop") });
        jobs.Decorate(catalog, chef, true);
        var display = (Json.Record)catalog["displayOptions"];
        var ingredients = ((List<object>)display["ingredients"]).Cast<Json.Record>().ToList();
        Check(ingredients.Count == 1 && (string)ingredients[0]["label"] == "Tomato", "busy/unreachable supply remains visible, unrelated prefab excluded");
        Check(((List<string>)display["utensilTypes"]).Contains("pot"), "filled utensil remains a visible type");
        Check(((List<object>)display["items"]).Cast<Json.Record>().Any(item => (int)item["objectInstanceId"] == pot.GetInstanceID()), "occupied station item remains selectable");
        Check(((List<object>)display["deliveryDestinations"]).Count == 1, "occupied/unusable surface remains selectable");
        Check(((List<object>)((Json.Record)catalog["jobOptions"])["fetch_ingredient"]).Count == 0, "display choices never authorize a blocked job");
        var prep = ((List<object>)display["preparationOptions"]).Cast<Json.Record>().ToList();
        Check(prep.Count == 2 && prep.Count(item => (bool)item["utensil"]) == 1, "preparation lists only level roots and utensils");
        var human = new GameObject("human"); human.Add(new PlayerControls());
        var humanCarrier = human.Add(new ServerPlayerAttachmentCarrier());
        var pan = new GameObject("human's pan"); pan.Add(new ItemPropertiesComponent { m_itemDefinition = new Definition { name = "Pan" } });
        humanCarrier.Item = pan;
        jobs.Decorate(catalog, chef, true); display = (Json.Record)catalog["displayOptions"];
        Check(((List<string>)display["utensilTypes"]).Contains("pan"), "a utensil held by a human remains a level type");
        Check(((List<object>)display["items"]).Cast<Json.Record>().Any(item => (int)item["objectInstanceId"] == pan.GetInstanceID() && (bool)item["inUse"]), "human-held item stays visible with its exact identity");
        pan.activeInHierarchy = false;
        var dirty = new GameObject("dirty plate stack"); dirty.Add(new ServerDirtyPlateStack());
        humanCarrier.Item = dirty;
        jobs.Decorate(catalog, chef, true); display = (Json.Record)catalog["displayOptions"];
        Check(((List<string>)display["utensilTypes"]).Contains("plate"), "plates remain a level option even when only dirty dishes are observed");
        var board = new GameObject("empty board"); board.Add(new ServerWorkstation()); board.Add(new ServerAttachStation()); board.Add(new Placement());
        var identity = Json.Object(); HarnessOptions.Target(identity, board);
        Check(((string[])identity["possibleOperations"]).Contains("work_at"), "empty board retains work action");
        Check(((string[])identity["possibleWorkConditions"]).Contains("work_complete"), "empty board retains its completion condition");
        Check(!InteractionActions.Offered(chef, board).Contains("work_at"), "visible work action remains unavailable on an empty board");
        supply.activeInHierarchy = counter.activeInHierarchy = pot.activeInHierarchy = false;
        humanCarrier.Item = null; dirty.activeInHierarchy = false;
        catalog = Json.Object("destinations", new List<object>(), "actions", new List<object>());
        jobs.Decorate(catalog, chef, true);
        display = (Json.Record)catalog["displayOptions"];
        Check(((List<object>)display["preparationOptions"]).Count == 0, "removed level objects disappear even while busy");
        Check(((List<object>)display["items"]).Count == 0, "removed exact items are not retained");
        Console.WriteLine("PASS: " + checks + " harness display checks (production catalog, mocked native state)");
    }
}
