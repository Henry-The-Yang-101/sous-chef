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
        var fish = new GameObject("Fish");
        fish.Add(new IngredientPropertiesComponent { m_ingredientOrderNode = new Definition { name = "Fish", m_uID = 456 } });
        var rawFish = new GameObject("Fish"); rawFish.Add(new WorkableItem { Next = fish });
        var rice = new GameObject("Rice");
        rice.Add(new IngredientPropertiesComponent { m_ingredientOrderNode = new Definition { name = "Rice", m_uID = 789 } });
        var automatic = new GameObject("non-interactive belt source");
        automatic.Add(new TriggerAttachedSpawn { m_attachmentPrefabs = new[] {
            new TriggerAttachedSpawn.WeightedPrefab { AttachmentPrefab = rawFish },
            new TriggerAttachedSpawn.WeightedPrefab { AttachmentPrefab = rice },
            new TriggerAttachedSpawn.WeightedPrefab { AttachmentPrefab = raw },
            new TriggerAttachedSpawn.WeightedPrefab { AttachmentPrefab = pot }, null,
            new TriggerAttachedSpawn.WeightedPrefab() } });
        Time.realtimeSinceStartup += 1.1f;
        jobs.Decorate(catalog, chef, true); display = (Json.Record)catalog["displayOptions"];
        prep = ((List<object>)display["preparationOptions"]).Cast<Json.Record>().Where(item => !(bool)item["utensil"]).ToList();
        Check(prep.Count == 3 && prep.Any(item => (string)item["label"] == "Fish") && prep.Any(item => (string)item["label"] == "Rice"),
            "all automatic variants appear before any cargo exists, without a navigable spawner; duplicates, tools and nulls excluded");
        Check(((List<object>)display["ingredients"]).Cast<Json.Record>().Any(item => (string)item["label"] == "Rice"), "automatic types also appear in the ingredient chooser");
        Check(!jobs.Inventory.Items.Any(item => item.Item == rawFish || item.Item == rice), "automatic prefabs never become obtainable inventory");
        Check(((List<object>)((Json.Record)catalog["jobOptions"])["fetch_ingredient"]).Count == 0, "unspawned types do not authorize fetch jobs");
        var observer = new SemanticCatalog(nav);
        var tasks = new TaskActions(nav, child, buttons => { throw new Exception("semantic observer pressed input"); },
            () => { throw new Exception("semantic observer released task"); });
        tasks.Run.Start(1, new ControlCommand { Id = "active", Operation = "task", Indefinite = true }, Time.realtimeSinceStartup);
        long revision = tasks.Run.Revision;
        string before = Json.Encode(tasks.Describe());
        var flow = new GameObject("flow").Add(new ServerKitchenFlowControllerBase());
        new GameObject("serving").Add(new ServerPlateStation());
        var ticket = new OrderController.ServerOrderData { ID = new OrderController.OrderID { m_id = 7 }, Remaining = 100,
            RecipeListEntry = new RecipeList.Entry { m_order = new OrderDefinitionNode { name = "Fish", Node = new IngredientAssembledNode("Fish") } } };
        flow.Monitor.OrdersController.m_activeOrders.Add(ticket);
        var semantic = (Json.Record)observer.Capture(chef, new List<object>());
        tasks.Decorate(catalog, chef);
        Check(((List<object>)catalog["taskOptions"]).Count == 0, "busy task is never advertised as startable");
        Check(((Json.Record)catalog["taskApplicability"]).Length / 2 == TaskRequest.Names.Length, "all implemented families retain applicability records while busy");
        Check(((List<object>)semantic["assemblyOrders"]).Count == 1, "exact live assembly tickets remain observable while busy");
        Check(tasks.Run.Running && tasks.Run.Revision == revision && Json.Encode(tasks.Describe()) == before, "semantic observation preserves active task receipt and state");
        var semanticPrep = ((List<object>)((Json.Record)semantic["displayOptions"])["preparationOptions"]).Cast<Json.Record>().ToList();
        var fishOption = semanticPrep.First(item => (string)item["label"] == "Fish");
        Check((string)((Json.Record)fishOption["preparations"])["chopped"] == "applicable", "native work chain supports chopped fish");
        Check((string)((Json.Record)fishOption["preparations"])["cooked"] == "unknown", "work chain never fabricates cooking compatibility");
        Check((string)((Json.Record)semanticPrep.First(item => (string)item["label"] == "Rice")["preparations"])["chopped"] == "not_applicable", "non-workable rice does not advertise chopping");
        flow.Monitor.OrdersController.m_activeOrders.Clear();
        semantic = (Json.Record)observer.Capture(chef, new List<object>());
        Check(((List<object>)semantic["assemblyOrders"]).Count == 0, "expired tickets disappear during busy observation");
        rawFish.GetComponent<WorkableItem>().Next = rawFish;
        Check((string)((Json.Record)((Json.Record)SemanticCatalog.Preparation(new PreparationIngredient { Id = "cycle", Name = "cycle", Prefab = rawFish }))["preparations"])["chopped"] == "unknown", "cyclic work chain is bounded and unknown");
        rawFish.GetComponent<WorkableItem>().Next = fish;
        // Native crates may change their output later in the round.
        supply.Add(new PickupItemSwitcher { m_itemPrefabs = new[] { raw, rice, null } });
        automatic.activeInHierarchy = false;
        jobs.Decorate(catalog, chef, true); display = (Json.Record)catalog["displayOptions"];
        prep = ((List<object>)display["preparationOptions"]).Cast<Json.Record>().Where(item => !(bool)item["utensil"]).ToList();
        Check(prep.Count == 2 && prep.Any(item => (string)item["label"] == "Rice"), "switching dispenser includes its future outputs and removed belt source disappears immediately");
        supply.activeInHierarchy = counter.activeInHierarchy = pot.activeInHierarchy = false;
        humanCarrier.Item = null; dirty.activeInHierarchy = false;
        catalog = Json.Object("destinations", new List<object>(), "actions", new List<object>());
        jobs.Decorate(catalog, chef, true);
        display = (Json.Record)catalog["displayOptions"];
        Check(((List<object>)display["preparationOptions"]).Count == 0, "removed level objects disappear even while busy");
        Check(((List<object>)display["items"]).Count == 0, "removed exact items are not retained");
        // A new level must not wait for the old scan deadline or retain its food.
        var newSource = new GameObject("new level source");
        newSource.Add(new TriggerAttachedSpawn { m_attachmentPrefabs = new[] { new TriggerAttachedSpawn.WeightedPrefab { AttachmentPrefab = unrelated } } });
        jobs.Reset("round_changed"); jobs.Decorate(catalog, chef, true); display = (Json.Record)catalog["displayOptions"];
        prep = ((List<object>)display["preparationOptions"]).Cast<Json.Record>().ToList();
        Check(prep.Count == 1 && (string)prep[0]["label"] == "Chocolate", "reset discovers the new kitchen immediately without leaking old ingredient types");
        Console.WriteLine("PASS: " + checks + " harness display checks (production catalog, mocked native state)");
    }
}
