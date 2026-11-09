using System;
using System.Collections.Generic;
using System.Linq;
using SousChef.Core;
using SousChef.Plugin.Actions;
using UnityEngine;

internal static partial class Program
{
    private static IngredientAssembledNode Leaf(string name) { return new IngredientAssembledNode(name); }
    private static AssembledDefinitionNode Recipe(params AssembledDefinitionNode[] parts) { return new CompositeAssembledNode {m_composition=parts}; }
    private static GameObject AssemblyPlate(World w, GameObject counter, params AssembledDefinitionNode[] parts)
    {
        var plate=new GameObject("assembly plate");var contents=plate.Add(new ServerIngredientContainer());contents.Contents.AddRange(parts);
        plate.Add(new ServerPlate {m_ingredientContainer=contents});plate.Add(new PhysicalAttachment());plate.Add(new Pickup());
        counter.GetComponent<ServerAttachStation>().Item=plate;counter.Add(new Pickup());return plate;
    }
    private static GameObject AssemblyFood(World w, GameObject counter, string name)
    {
        var food=w.Ingredient(name);food.GetComponent<IngredientPropertiesComponent>().Node=Leaf(name);
        food.Add(new PhysicalAttachment());food.Add(new Pickup());counter.Add(new Pickup());counter.GetComponent<ServerAttachStation>().Item=food;return food;
    }
    private static TaskRequest AssemblyJob(World w, GameObject[] bases, GameObject[] foods, params AssembledDefinitionNode[] recipe)
    {
        ServingKitchen(w);
        var controller=UnityEngine.Object.FindObjectsOfType<ServerKitchenFlowControllerBase>()[0].Monitor.OrdersController;
        controller.m_activeOrders.Add(new OrderController.ServerOrderData {ID=new OrderController.OrderID {m_id=7},Remaining=100,
            RecipeListEntry=new RecipeList.Entry {m_order=new OrderDefinitionNode {name="assigned recipe",Node=Recipe(recipe)}}});
        for(int n=0;n<8;n++)w.Navigation.Available(w.Chef);
        var allocation=new AssemblyRequest {Order=AssemblyOrders.Observe()[0].Id};
        foreach(var plate in bases)allocation.Bases.Add(w.Navigation.RegisterItem(plate));
        foreach(var food in foods)allocation.Components.Add(w.Navigation.RegisterItem(food));
        return new TaskRequest {Action="assemble_dish",Assembly=allocation};
    }
    private static void StartAssembly(World w, TaskRequest request)
    {Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Id="assemble",Operation="task",Task=request})==null,"assigned assembly accepted");}
    private static int DriveAssembly(World w, int limit=180, Action<GameObject,string> beforeArrival=null, bool consume=true, Action onTick=null)
    {
        int additions=0;
        for(int n=0;n<limit && w.Tasks.Run.Running;n++)
        {
            onTick?.Invoke();w.Tick(.11f);if(!w.Skills.Run.Running)continue;
            var target=w.Navigation.Resolve(w.Skills.Run.Target);
            if(w.Skills.Run.Phase=="navigating") {beforeArrival?.Invoke(target,w.Skills.Run.Operation);w.ArriveAt(target);}
            if(w.Skills.Run.Phase!="pressing" || w.Inputs.Last()!=1)continue;
            if(w.Skills.Run.Operation=="pick_up" && w.Carrier.Item==null)
            {
                var cargo=ActionObservations.Receiver(target);w.Hold(cargo);
                var holder=target.GetComponent<ServerAttachStation>();if(holder!=null)holder.Item=null;
            }
            else if(w.Skills.Run.Operation=="place_at" && w.Carrier.Item!=null)
            {target.GetComponent<ServerAttachStation>().Item=w.Carrier.Item;w.Hold(null);}
            else if(w.Skills.Run.Operation=="transfer_contents" && w.Carrier.Item!=null)
            {
                var cargo=w.Carrier.Item;var contents=ActionObservations.Container(target);
                if(RecipeProof.Empty(RecipeProof.Composition(cargo)))continue;
                if(contents.GetContentsCount()!=0 && !consume)continue;
                var addition=RecipeProof.Composition(cargo);
                if(ActionObservations.Receiver(target).GetComponent<ServerPlate>()!=null && contents.Contents.Any(n=>n.GetType()==typeof(CompositeAssembledNode)))
                {var existing=contents.Contents.ToArray();contents.Contents.Clear();contents.Contents.Add(new CompositeAssembledNode {m_composition=existing.SelectMany(n=>n.GetType()==typeof(CompositeAssembledNode) ? ((CompositeAssembledNode)n).m_composition : new[]{n}).Concat(addition.GetType()==typeof(CompositeAssembledNode) ? ((CompositeAssembledNode)addition).m_composition : new[]{addition}).ToArray()});}
                else contents.Contents.Add(addition);
                if(consume){
                    var source=cargo.GetComponent<ServerIngredientContainer>();
                    if(source!=null && cargo.GetComponent<PreparationContainer>()==null)source.Contents.Clear();else {cargo.activeInHierarchy=false;w.Hold(null);}
                } additions++;
            }
        }
        return additions;
    }

    private sealed class ProcessorComposition : MonoBehaviour, IOrderDefinition
    {
        public CookingStepData Step;
        public AssembledDefinitionNode GetOrderComposition()
        {
            var contents=gameObject.GetComponent<ServerIngredientContainer>().GetContents();
            if(contents.Length==0)return AssembledDefinitionNode.NullNode;
            AssembledDefinitionNode result=Recipe(contents);
            var mixing=gameObject.GetComponent<ServerMixingHandler>();
            if(mixing!=null && mixing.m_serverData.m_mixingState==ProcessingState.Completed)
                result=new MixedCompositeAssembledNode {m_composition=contents,m_progress=MixedCompositeOrderNode.MixingProgress.Mixed};
            var cooking=gameObject.GetComponent<ServerCookingHandler>();
            if(cooking!=null && cooking.m_ServerData.m_cookingState==ProcessingState.Completed)
                result=new CookedCompositeAssembledNode {m_composition=result is MixedCompositeAssembledNode ? new[]{result}:contents,m_cookingStep=Step,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
            return result;
        }
    }
    private sealed class CookableBaseComposition : MonoBehaviour, IOrderDefinition
    {
        public CookingStepData Step;
        public AssembledDefinitionNode GetOrderComposition()
        {
            var parts=new[]{Leaf("dough")}.Concat(gameObject.GetComponent<ServerIngredientContainer>().GetContents()).ToArray();
            if(gameObject.GetComponent<ServerCookingHandler>().m_ServerData.m_cookingState==ProcessingState.Completed)
                return new CookedCompositeAssembledNode {m_composition=parts,m_cookingStep=Step,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
            return Recipe(parts);
        }
    }
    private static void AssemblyProcessing()
    {
        AssemblyRecoveryPolicy();
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var step=new CookingStepData {m_uID=31};
            var pans=new List<GameObject>();var goals=new List<AssembledDefinitionNode>();ServerCookingHandler pending=null;
            foreach(var name in new[]{"rice","meat"})
            {
                var holder=w.Delivery();holder.Add(new Pickup());if(name=="meat")holder.Add(new ServerCookingStation());
                var pan=new GameObject(name+" vessel");pan.Add(new PhysicalAttachment());pan.Add(new Pickup());pan.Add(new IngredientTransfer());
                pan.Add(new ServerIngredientContainer()).Contents.Add(Leaf(name));pan.Add(new CookingHandler {m_cookingType=step});pan.Add(new CookableContainer());
                var cooking=pan.Add(new ServerCookingHandler());pan.Add(new ProcessorComposition {Step=step});holder.GetComponent<ServerAttachStation>().Item=pan;
                if(name=="rice")cooking.m_ServerData.m_cookingState=ProcessingState.Completed;else pending=cooking;
                pans.Add(pan);goals.Add(new CookedCompositeAssembledNode {m_composition=new[]{Leaf(name)},m_cookingStep=step,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked});
            }
            var request=AssemblyJob(w,new[]{plate},new[]{pans[0]},goals.ToArray());request.Assembly.Equipment.Add(w.Navigation.RegisterItem(pans[1]));
            var burner=UnityEngine.Object.FindObjectsOfType<ServerAttachStation>().First(s=>s.Item==pans[1]).gameObject;request.Assembly.Equipment.Add(w.IdOf(burner));
            StartAssembly(w,request);bool storedEmpty=false;
            int additions=DriveAssembly(w,onTick:()=>{
                if(pans[0].GetComponent<ServerIngredientContainer>().GetContentsCount()==0 && w.Carrier.Item!=pans[0] &&
                    UnityEngine.Object.FindObjectsOfType<ServerAttachStation>().Any(s=>s.Item==pans[0]))
                { storedEmpty=true;pending.m_ServerData.m_cookingState=ProcessingState.Completed; }
            });
            Check(storedEmpty && additions==2 && w.Tasks.Run.State=="succeeded","empty allocated cookware is put aside while another recipe batch cooks, before its rescue needs free hands: "+Json.Encode(w.Tasks.Describe()));
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var counter=w.Delivery();counter.Add(new Pickup());
            var pan=new GameObject("assembled cold batch");pan.Add(new PhysicalAttachment());pan.Add(new Pickup());
            pan.Add(new ServerIngredientContainer()).Contents.Add(Leaf("rice"));var step=new CookingStepData {m_uID=31};
            pan.Add(new CookingHandler {m_cookingType=step});pan.Add(new CookableContainer());pan.Add(new ServerCookingHandler());pan.Add(new ProcessorComposition {Step=step});
            counter.GetComponent<ServerAttachStation>().Item=pan;var burner=w.Delivery();burner.Add(new ServerCookingStation());burner.Add(new Pickup());
            w.Delivery().transform.position=new Vector3(-3,0,0);
            var goal=new CookedCompositeAssembledNode {m_composition=new[]{Leaf("rice")},m_cookingStep=step,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
            var request=AssemblyJob(w,new[]{plate},new GameObject[0],goal);request.Assembly.Equipment.Add(w.Navigation.RegisterItem(pan));request.Assembly.Equipment.Add(w.IdOf(burner));StartAssembly(w,request);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var schedule=(AssemblySchedule)typeof(TaskActions).GetField("assembly",flags).GetValue(w.Tasks);schedule.Base=plate;schedule.Root.Vessel=plate;schedule.Root.Children[0].Vessel=pan;
            var inventory=(LogisticsInventory)typeof(TaskActions).GetField("inventory",flags).GetValue(w.Tasks);
            var catalog=(Json.Record)w.Navigation.Available(w.Chef);catalog["complete"]=false;catalog["destinations"]=new List<object>();
            inventory.BeginObservation();inventory.Refresh(w.Chef,catalog);w.Chef.GroundCollider=null;
            var select=typeof(TaskActions).GetMethod("SelectAssemblyWork",flags);var job=select.Invoke(w.Tasks,null);
            Check(job!=null && (GameObject)job.GetType().GetField("Source").GetValue(job)==pan,
                "complete assigned batch on a counter can start targeted processor planning while the broad map rebuilds");
            catalog["complete"]=true;
            Check(select.Invoke(w.Tasks,null)==null,"known unreachable processor is not treated as an unfinished discovery frontier");
            inventory.EndObservation();counter.GetComponent<ServerAttachStation>().Item=null;pan.GetComponent<ServerIngredientContainer>().Contents.Clear();w.Hold(pan);
            catalog["complete"]=false;inventory.BeginObservation();inventory.Refresh(w.Chef,catalog);
            var parkingJob=select.Invoke(w.Tasks,null);
            Check(parkingJob!=null && (string)parkingJob.GetType().GetField("Operation").GetValue(parkingJob)=="place_at","empty processing vessel seeks staging before its recipe additions");
            var rejected=(GameObject)parkingJob.GetType().GetField("Destination").GetValue(parkingJob);schedule.RetryAt[rejected]=Time.realtimeSinceStartup+1;
            parkingJob=select.Invoke(w.Tasks,null);
            Check(parkingJob!=null && (GameObject)parkingJob.GetType().GetField("Destination").GetValue(parkingJob)!=rejected,
                "processing-vessel staging tries another counter while the failed destination is deferred");
            inventory.EndObservation();w.Tasks.Cancel("test");
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var doughCounter=w.Delivery();
            var dough=new GameObject("assigned cookable dough base");dough.Add(new PreparationContainer());
            dough.Add(new PhysicalAttachment());dough.Add(new Pickup());dough.Add(new IngredientTransfer());dough.Add(new ServerIngredientContainer());
            var step=new CookingStepData {m_uID=71};dough.Add(new CookingHandler {m_cookingType=step});dough.Add(new CookableContainer());
            var handler=dough.Add(new ServerCookingHandler());dough.Add(new CookableBaseComposition {Step=step});
            doughCounter.GetComponent<ServerAttachStation>().Item=dough;doughCounter.Add(new Pickup());
            var tomato=AssemblyFood(w,w.Target,"tomato");var oven=w.Delivery();oven.Add(new ServerCookingStation());oven.Add(new Pickup());
            var goal=new CookedCompositeAssembledNode {m_composition=new[]{Leaf("dough"),Leaf("tomato")},m_cookingStep=step,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
            var request=AssemblyJob(w,new[]{plate},new[]{dough,tomato},goal);request.Assembly.Equipment.Add(w.IdOf(oven));
            StartAssembly(w,request);bool cooked=false;
            int additions=DriveAssembly(w,onTick:()=>{
                if(ActionObservations.Occupant(oven)!=dough)return;
                Check(dough.GetComponent<ServerIngredientContainer>().GetContentsCount()==1,"cookable base goes into oven only after its recipe inputs are assembled");
                cooked=true;handler.m_ServerData.m_cookingState=ProcessingState.Completed;
            });
            Check(cooked && additions==2 && w.Tasks.Run.State=="succeeded","assigned preparation base supplies the vessel for its cooking dependency without duplicate allocation");
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var raw=AssemblyFood(w,w.Target,"rice");
            var step=new CookingStepData {m_uID=31};
            var goal=new CookedCompositeAssembledNode {m_composition=new[]{Leaf("rice")},m_cookingStep=step,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
            var preparedCounter=w.Delivery();var prepared=AssemblyPlate(w,preparedCounter,goal);prepared.Add(new IngredientTransfer());
            var vesselCounter=w.Delivery();var vessel=new GameObject("empty assigned pot");
            vessel.Add(new PhysicalAttachment());vessel.Add(new Pickup());vessel.Add(new ServerIngredientContainer());
            vessel.Add(new CookingHandler {m_cookingType=step});vessel.Add(new CookableContainer());vessel.Add(new ServerCookingHandler());
            vessel.Add(new ProcessorComposition {Step=step});vesselCounter.GetComponent<ServerAttachStation>().Item=vessel;vesselCounter.Add(new Pickup());
            var human=new GameObject("human carrying assigned prepared food");human.Add(new PlayerControls());
            var carrier=human.Add(new ServerPlayerAttachmentCarrier {Item=prepared});
            var request=AssemblyJob(w,new[]{plate},new[]{raw,prepared},goal);request.Assembly.Equipment.Add(w.Navigation.RegisterItem(vessel));
            StartAssembly(w,request);DriveAssembly(w,25);
            Check(w.Tasks.Run.Running && !w.Inputs.Contains(1) && RecipeProof.Empty(RecipeProof.Composition(vessel)),
                "temporarily unavailable prepared component does not authorize cooking duplicate demand");
            carrier.Item=null;
            Check(DriveAssembly(w)==1 && w.Tasks.Run.State=="succeeded","uses exact already-cooked component when its holder releases it");
        }
        foreach(bool mixing in new[]{false,true})
        {
            var w=new World();var plateCounter=w.Delivery();var plate=AssemblyPlate(w,plateCounter);
            var first=AssemblyFood(w,w.Target,"flour");var otherCounter=w.Delivery();otherCounter.transform.position=new Vector3(-2,0,0);
            var second=AssemblyFood(w,otherCounter,"egg");var vesselCounter=w.Delivery();vesselCounter.transform.position=new Vector3(4,0,0);
            var vessel=new GameObject("assigned vessel");vessel.Add(new PhysicalAttachment());vessel.Add(new Pickup());vessel.Add(new IngredientTransfer());
            vessel.Add(new ServerIngredientContainer());var step=new CookingStepData {m_uID=31};
            vessel.Add(new CookingHandler {m_cookingType=step});vessel.Add(new CookableContainer());var cooking=vessel.Add(new ServerCookingHandler());
            vessel.Add(new ProcessorComposition {Step=step});vesselCounter.GetComponent<ServerAttachStation>().Item=vessel;vesselCounter.Add(new Pickup());
            var oven=w.Delivery();oven.name="oven";oven.Add(new ServerCookingStation());oven.Add(new Pickup());oven.transform.position=new Vector3(-4,0,0);
            GameObject mixer=null;ServerMixingHandler handler=null;
            AssembledDefinitionNode[] parts={Leaf("flour"),Leaf("egg")};
            if(mixing)
            {
                handler=vessel.Add(new ServerMixingHandler());vessel.Add(new MixingHandler {m_mixingType=new Definition {name="bowl"}});vessel.Add(new MixableContainer());
                mixer=w.Delivery();mixer.name="mixer";mixer.Add(new ServerMixingStation());mixer.Add(new Pickup());mixer.transform.position=new Vector3(0,0,3);
                vesselCounter.GetComponent<ServerAttachStation>().Item=null;mixer.GetComponent<ServerAttachStation>().Item=vessel;
                parts=new AssembledDefinitionNode[]{new MixedCompositeAssembledNode {m_composition=parts,m_progress=MixedCompositeOrderNode.MixingProgress.Mixed}};
            }
            var goal=new CookedCompositeAssembledNode {m_composition=parts,m_cookingStep=step,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
            var request=AssemblyJob(w,new[]{plate},new[]{first,second},goal);
            request.Assembly.Equipment.Add(w.Navigation.RegisterItem(vessel));request.Assembly.Equipment.Add(w.IdOf(oven));
            if(mixer!=null)request.Assembly.Equipment.Add(w.IdOf(mixer));
            if (mixing)
            {
                var contents = vessel.GetComponent<ServerIngredientContainer>().Contents;
                contents.Add(Leaf("flour"));
                var holder = w.Target.GetComponent<ServerAttachStation>();
                holder.Item = null;
                first.activeInHierarchy = false;
                var catalog = (Json.Record)new SemanticCatalog(w.Navigation).Capture(w.Chef, new object[0]);
                var order = (Json.Record)((List<object>)catalog["assemblyOrders"]).Single();
                var allocation = (Json.Record)order["allocation"];
                Check(new HashSet<string>((List<string>)allocation["equipment"]).SetEquals(request.Assembly.Equipment),
                    "allocation reserves the mixer and oven for the same partially filled vessel");
                contents.Clear();
                first.activeInHierarchy = true;
                holder.Item = first;
            }
            StartAssembly(w,request);bool sawMix=false,sawCook=false;
            var human=new GameObject("human temporarily holding allocated plate");human.Add(new PlayerControls());
            var humanCarrier=human.Add(new ServerPlayerAttachmentCarrier {Item=plate});plateCounter.GetComponent<ServerAttachStation>().Item=null;
            bool cookedBeforePlateAvailable=false, rescuedBeforePlateAvailable=false;
            int adds=DriveAssembly(w,450,onTick:()=>{
                if(mixer!=null && ActionObservations.Occupant(mixer)==vessel)
                {
                    if(vessel.GetComponent<ServerIngredientContainer>().GetContentsCount()!=2)return;
                    sawMix=true;
                    handler.m_serverData.m_mixingProgress+=1;
                    handler.m_serverData.m_mixingState=handler.m_serverData.m_mixingProgress>=4?ProcessingState.Completed:ProcessingState.Progressing;
                }
                if(ActionObservations.Occupant(oven)==vessel)
                {
                    cookedBeforePlateAvailable |= humanCarrier.Item==plate;
                    Check(vessel.GetComponent<ServerIngredientContainer>().GetContentsCount()==2 && (!mixing || sawMix && handler.m_serverData.m_mixingState==ProcessingState.Completed),"cooking starts only after full assembly and required mixing");sawCook=true;
                    cooking.m_ServerData.m_cookingProgress+=1;
                    cooking.m_ServerData.m_cookingState=cooking.m_ServerData.m_cookingProgress>=4?ProcessingState.Completed:ProcessingState.Progressing;
                }
                if(humanCarrier.Item==plate && cooking.m_ServerData.m_cookingState==ProcessingState.Completed && w.Carrier.Item==vessel)
                { rescuedBeforePlateAvailable=true;humanCarrier.Item=null;plateCounter.GetComponent<ServerAttachStation>().Item=plate; }
            });
            Check(cookedBeforePlateAvailable && rescuedBeforePlateAvailable,"independent recipe processing and heat rescue continue while the allocated plate is unavailable");
            Check(w.Tasks.Run.State=="succeeded" && adds==3 && sawCook && (!mixing || sawMix),"combined processing assembled, rescued and plated: "+Json.Encode(w.Tasks.Describe()));
            Check(RecipeProof.Equal(RecipeProof.Composition(plate),goal),"final dish retains exact cooking/mixing structure");
        }
    }

    private static void AssemblyRecoveryPolicy()
    {
        var w=new World();var plate=AssemblyPlate(w,w.Delivery());var fish=AssemblyFood(w,w.Target,"fish");
        var burner=w.Delivery();burner.Add(new Pickup());burner.Add(new ServerCookingStation());
        var pan=new GameObject("owned cooking rice");pan.Add(new PhysicalAttachment());pan.Add(new Pickup());pan.Add(new IngredientTransfer());
        pan.Add(new ServerIngredientContainer()).Contents.Add(Leaf("rice"));var cookingStep=new CookingStepData {m_uID=31};
        pan.Add(new CookingHandler {m_cookingType=cookingStep});pan.Add(new CookableContainer());var cooking=pan.Add(new ServerCookingHandler());
        pan.Add(new ProcessorComposition {Step=cookingStep});burner.GetComponent<ServerAttachStation>().Item=pan;
        w.Delivery().transform.position=new Vector3(-2,0,0);
        var goal=new CookedCompositeAssembledNode {m_composition=new[]{Leaf("rice")},m_cookingStep=cookingStep,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
        var request=AssemblyJob(w,new[]{plate},new[]{fish},goal,Leaf("fish"));request.Assembly.Equipment.Add(w.Navigation.RegisterItem(pan));request.Assembly.Equipment.Add(w.IdOf(burner));StartAssembly(w,request);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var schedule=(AssemblySchedule)typeof(TaskActions).GetField("assembly",flags).GetValue(w.Tasks);
        schedule.Base=plate;schedule.Root.Vessel=plate;schedule.Root.Children[0].Vessel=pan;
        var select=typeof(TaskActions).GetMethod("SelectAssemblyWork",flags);
        var inventory=(LogisticsInventory)typeof(TaskActions).GetField("inventory",flags).GetValue(w.Tasks);
        Action refresh=()=>{inventory.EndObservation();inventory.BeginObservation();inventory.Refresh(w.Chef);};refresh();
        cooking.m_ServerData.m_cookingState=ProcessingState.Completed;schedule.RetryAt[pan]=Time.realtimeSinceStartup+.2f;
        Check(select.Invoke(w.Tasks,null)==null && schedule.Waiting=="waiting_for_heat_access",
            "a ready batch's retry delay cannot dispatch the available cold ingredient");
        Time.realtimeSinceStartup+=.3f;var job=select.Invoke(w.Tasks,null);
        Check(job!=null && (bool)job.GetType().GetField("Rescue").GetValue(job),"ready batch retains rescue priority after the short recovery delay");
        cooking.m_ServerData.m_cookingState=ProcessingState.Progressing;cooking.m_ServerData.m_cookingProgress=cooking.AccessCookingTime-1;
        w.Target.GetComponent<ServerAttachStation>().Item=null;w.Hold(fish);refresh();job=select.Invoke(w.Tasks,null);
        Check(job!=null && (bool)job.GetType().GetField("Storage").GetValue(job) && (GameObject)job.GetType().GetField("Source").GetValue(job)==fish,
            "hands are cleared before another batch finishes rather than starting a distant addition");
        w.Hold(null);w.Target.GetComponent<ServerAttachStation>().Item=fish;refresh();job=select.Invoke(w.Tasks,null);
        Check(job!=null && (string)job.GetType().GetField("Operation").GetValue(job)=="approach_heat",
            "empty-handed chef approaches imminent food without removing it before cooking completes");
        cooking.m_ServerData.m_cookingProgress=2;refresh();job=select.Invoke(w.Tasks,null);
        Check(job!=null && (string)job.GetType().GetField("Operation").GetValue(job)=="pick_component",
            "useful nearby work still runs when its return trip fits before cooking completes");
        w.Target.transform.position=new Vector3(8,0,0);w.Chef.CurrentInteractionObjects.m_TheOriginalHandlePickup=burner;
        cooking.m_ServerData.m_cookingProgress=5;refresh();job=select.Invoke(w.Tasks,null);
        Check(job!=null && (string)job.GetType().GetField("Operation").GetValue(job)=="approach_heat",
            "a distant ingredient trip cannot consume the time needed to return and rescue cooking food");
        w.Target.transform.position=Vector3.zero;refresh();
        var defer=typeof(TaskActions).GetMethod("DeferAssemblyRoute",flags);var deferred=typeof(TaskActions).GetMethod("AssemblyDeferred",flags);
        defer.Invoke(w.Tasks,new object[]{w.Target,w.Target});Time.realtimeSinceStartup+=2.1f;
        defer.Invoke(w.Tasks,new object[]{w.Target,w.Target});Time.realtimeSinceStartup+=2.1f;
        Check((bool)deferred.Invoke(w.Tasks,new object[]{w.Target}),"the second failed route stays deferred while another two-second attempt runs");
        w.Target.GetComponent<ServerAttachStation>().Item=null;
        Check(!(bool)deferred.Invoke(w.Tasks,new object[]{w.Target}),"changed counter occupancy wakes a deferred route immediately");
        defer.Invoke(w.Tasks,new object[]{w.Target,w.Target});w.Chef.transform.position+=new Vector3(.5f,0,0);
        Check(!(bool)deferred.Invoke(w.Tasks,new object[]{w.Target}),"substantive movement wakes a deferred route instead of waiting for its timer");
        inventory.EndObservation();w.Tasks.Cancel("test");
    }

    private sealed class BaseComposition : MonoBehaviour, IOrderDefinition
    {
        public AssembledDefinitionNode GetOrderComposition() { return RecipeProof.Combine(Leaf("bun"), Recipe(gameObject.GetComponent<ServerIngredientContainer>().GetContents())); }
    }
    private static void AssemblyBasesAndRecovery()
    {
        var w=new World();var plate=AssemblyPlate(w,w.Delivery());var holder=w.Delivery();holder.Add(new Pickup());holder.transform.position=new Vector3(-2,0,0);
        var bun=new GameObject("partly assembled bun");bun.Add(new PhysicalAttachment());bun.Add(new Pickup());bun.Add(new IngredientTransfer());
        bun.Add(new PreparationContainer {m_ingredientOrderNode=new Definition {name="bun",m_uID=1}});bun.Add(new BaseComposition());
        bun.Add(new ServerIngredientContainer()).Contents.Add(Leaf("lettuce"));holder.GetComponent<ServerAttachStation>().Item=bun;
        var cheese=AssemblyFood(w,w.Target,"cheese");var request=AssemblyJob(w,new[]{plate,bun},new[]{cheese},Leaf("bun"),Leaf("lettuce"),Leaf("cheese"));
        bool platedCompleteBase=false;
        StartAssembly(w,request);int additions=DriveAssembly(w,beforeArrival:(target,op)=>{
            if(op=="transfer_contents" && ActionObservations.Receiver(target)==plate)
            { platedCompleteBase=true;Check(RecipeProof.Equal(RecipeProof.Composition(bun),Recipe(Leaf("bun"),Leaf("lettuce"),Leaf("cheese"))),"preparation base is complete before final plating"); }
        });
        Check(platedCompleteBase && w.Tasks.Run.State=="succeeded" && additions==2,"assigned partial assembly base is completed and then plated: "+Json.Encode(w.Tasks.Describe()));
        w=new World();plate=AssemblyPlate(w,w.Delivery());var food=AssemblyFood(w,w.Target,"fish");request=AssemblyJob(w,new[]{plate},new[]{food},Leaf("fish"));
        StartAssembly(w,request);
        // Reach the first transfer, suppress its input, then allow a retry only
        // after both native sides prove unchanged.
        for(int i=0;i<100 && w.Skills.Run.Operation!="transfer_contents";i++)
        {
            w.Tick(.11f);if(w.Skills.Run.Operation=="transfer_contents")break;if(!w.Skills.Run.Running)continue;
            var target=w.Navigation.Resolve(w.Skills.Run.Target);if(w.Skills.Run.Phase=="navigating")w.ArriveAt(target);
            if(w.Skills.Run.Operation=="pick_up" && w.Skills.Run.Phase=="pressing" && w.Inputs.Last()==1 && w.Carrier.Item==null)
            {w.Hold(food);w.Target.GetComponent<ServerAttachStation>().Item=null;}
        }
        w.ArriveAt(w.Navigation.Resolve(w.Skills.Run.Target));
        for(int i=0;i<25;i++)w.Tick(.11f);
        Check(w.Tasks.Run.Running && RecipeProof.Empty(RecipeProof.Composition(plate)),"unchanged suppressed transfer keeps its request and demand");
        Check(DriveAssembly(w)==1 && w.Tasks.Run.State=="succeeded","unchanged transfer retries once and verifies exactly one addition");

        w=new World();plate=AssemblyPlate(w,w.Delivery());food=AssemblyFood(w,w.Target,"fish");
        request=AssemblyJob(w,new[]{plate},new[]{food},Leaf("fish"));StartAssembly(w,request);
        for(int i=0;i<100 && w.Skills.Run.Operation!="transfer_contents";i++)
        {
            w.Tick(.11f);if(w.Skills.Run.Operation=="transfer_contents")break;if(!w.Skills.Run.Running)continue;
            var target=w.Navigation.Resolve(w.Skills.Run.Target);if(w.Skills.Run.Phase=="navigating")w.ArriveAt(target);
            if(w.Skills.Run.Operation=="pick_up" && w.Skills.Run.Phase=="pressing" && w.Inputs.Last()==1 && w.Carrier.Item==null)
            {w.Hold(food);w.Target.GetComponent<ServerAttachStation>().Item=null;}
        }
        // Reproduce native transport's temporary loss of held-item visibility.
        // The navigation suite separately exercises full portal geometry.
        var travelling=w.Chef.gameObject.Add(new ServerTeleportablePlayer {Travelling=true});
        typeof(SousChef.Plugin.Navigation.NavigationActions).GetField("activePortal",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
            .SetValue(w.Navigation,new SousChef.Plugin.Navigation.PortalLink());
        w.Hold(null);int edges=w.Inputs.Count(n=>n==1);w.Tick();
        Check(w.Tasks.Run.Running && w.Skills.Run.Running && w.Navigation.Action.State=="navigating" && w.Inputs.Count(n=>n==1)==edges,
            "native portal transport retains assembly and issues no new irreversible input with hidden cargo");
        travelling.Travelling=false;w.Hold(food);
        Check(DriveAssembly(w)==1 && w.Tasks.Run.State=="succeeded","assembly revalidates restored cargo and finishes after native transit");
    }

    private static void AssemblyResourceProposals()
    {
        var w = new World();
        var partial = AssemblyPlate(w, w.Delivery(), Leaf("fish"));
        var empty = AssemblyPlate(w, w.Delivery());
        var fish = AssemblyFood(w, w.Target, "fish");
        var raw = AssemblyFood(w, w.Delivery(), "fish");
        raw.Add(new WorkableItem());
        AssemblyFood(w, w.Delivery(), "prawn");
        var request = AssemblyJob(w, new[] { empty }, new[] { fish }, Leaf("fish"), Leaf("fish"));
        var controller = AssemblyOrders.Observe()[0].Controller;
        controller.m_activeOrders.Add(new OrderController.ServerOrderData {
            ID = new OrderController.OrderID { m_id = 8 }, Remaining = 20,
            RecipeListEntry = new RecipeList.Entry { m_order = new OrderDefinitionNode {
                name = "optional recipe", Node = new CompositeAssembledNode { m_composition = new[] { Leaf("fish"), Leaf("rice") }, m_optional = new[] { Leaf("prawn") } }
            }}
        });

        var observer = new SemanticCatalog(w.Navigation);
        var catalog = (Json.Record)observer.Capture(w.Chef, new object[0]);
        var tickets = ((List<object>)catalog["pendingOrders"]).Cast<Json.Record>().ToArray();
        var orders = ((List<object>)catalog["assemblyOrders"]).Cast<Json.Record>().ToArray();
        Check(tickets.Length == 2 && (string)tickets[1]["recipe"] == "optional recipe" && (bool)tickets[1]["expires"],
            "pending tickets include earlier orders whose recipes cannot be assembled");
        Check(orders.Length == 1 && (string)orders[0]["id"] == request.Assembly.Order,
            "explicit assembly still only exposes supported recipes");
        var allocation = (Json.Record)orders[0]["allocation"];
        var bases = (List<string>)allocation["bases"];
        var components = (List<string>)allocation["components"];
        Check(bases.SequenceEqual(new[] { w.Navigation.RegisterItem(partial) }) && components.SequenceEqual(new[] { w.Navigation.RegisterItem(fish) }),
            "allocation uses an exact partial base, respects quantities and excludes wrong or unchopped food");
        Check(((List<string>)allocation["equipment"]).Count == 0, "prepared-only assembly needs no processing equipment");
        request.Assembly.Bases.Clear();
        request.Assembly.Bases.AddRange(bases);
        request.Assembly.Components.Clear();
        request.Assembly.Components.AddRange(components);
        StartAssembly(w, request);
        Check(DriveAssembly(w) == 1 && w.Tasks.Run.State == "succeeded", "the proposed allocation executes through the existing exact assembly task");
    }

    private static void AssemblyProofs()
    {
        AssemblyResourceProposals();
        {
            var w=new World();var plateCounter=w.Delivery();var plate=AssemblyPlate(w,plateCounter);
            var fish=AssemblyFood(w,w.Target,"fish");var riceCounter=w.Delivery();var rice=AssemblyFood(w,riceCounter,"rice");
            var request=AssemblyJob(w,new[]{plate},new[]{fish,rice},Leaf("fish"),Leaf("rice"));StartAssembly(w,request);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var schedule=(AssemblySchedule)typeof(TaskActions).GetField("assembly",flags).GetValue(w.Tasks);
            schedule.Base=plate;schedule.Root.Vessel=plate;
            var inventory=(LogisticsInventory)typeof(TaskActions).GetField("inventory",flags).GetValue(w.Tasks);
            var catalog=(Json.Record)w.Navigation.Available(w.Chef);catalog["complete"]=false;
            catalog["destinations"]=((List<object>)catalog["destinations"]).Cast<Json.Record>()
                .Where(d=>(int)d["objectInstanceId"]==riceCounter.GetInstanceID() || (int)d["objectInstanceId"]==plateCounter.GetInstanceID()).Cast<object>().ToList();
            inventory.BeginObservation();inventory.Refresh(w.Chef,catalog);w.Chef.GroundCollider=null;
            var select=typeof(TaskActions).GetMethod("SelectAssemblyWork",flags);var job=select.Invoke(w.Tasks,null);
            Check(job!=null && (GameObject)job.GetType().GetField("Source").GetValue(job)==rice,
                "known executable recipe work precedes an earlier allocated source with an unknown route");
            var plateRoute=((List<object>)catalog["destinations"]).Cast<Json.Record>().Single(d=>(int)d["objectInstanceId"]==plateCounter.GetInstanceID());
            inventory.EndObservation();catalog["destinations"]=new List<object>();inventory.BeginObservation();inventory.Refresh(w.Chef,catalog);
            Check(select.Invoke(w.Tasks,null)==null,"an unknown receiving route does not trigger repeated collection of safely stored food");
            inventory.EndObservation();catalog["destinations"]=new List<object>{plateRoute};inventory.BeginObservation();inventory.Refresh(w.Chef,catalog);
            job=select.Invoke(w.Tasks,null);
            Check(job!=null && (string)job.GetType().GetField("Operation").GetValue(job)=="pick_component",
                "an incomplete catalogue with no certified source routes still probes an exact allocated addition");
            var step=((IEnumerable<TaskStep>)typeof(TaskActions).GetMethod("ExecuteAssembly",flags).Invoke(w.Tasks,new[]{job})).First();
            Check(w.Skills.Start(w.Chef,1,new ControlCommand {Id="route-probe",Operation="pick_up",Destination=w.Navigation.RegisterItem(w.Target)})==null,
                "allocated pickup route probe starts");
            w.Skills.Run.Advance("recovering_navigation");
            Check(!step.AbandonBeforePress(),"a transient route rebuild retains its bounded opportunity");
            Time.realtimeSinceStartup+=2.1f;
            Check(step.AbandonBeforePress() && !w.Inputs.Contains(1),"stalled planning yields to other assembly work before any irreversible input");
            w.Skills.Cancel("test");catalog["complete"]=true;
            Check(select.Invoke(w.Tasks,null)==null,"a complete catalogue proving no source route withholds speculative pickup");
            inventory.EndObservation();w.Tasks.Cancel("test");
        }
        {
            var w=new World();var counter=w.Delivery();var plate=AssemblyPlate(w,counter);
            var fish=AssemblyFood(w,w.Target,"fish");var rice=AssemblyFood(w,w.Delivery(),"rice");
            var request=AssemblyJob(w,new[]{plate},new[]{fish,rice},Leaf("fish"),Leaf("rice"));StartAssembly(w,request);
            w.Target.GetComponent<ServerAttachStation>().Item=null;w.Hold(fish);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var schedule=(AssemblySchedule)typeof(TaskActions).GetField("assembly",flags).GetValue(w.Tasks);
            schedule.Base=plate;schedule.Root.Vessel=plate;
            var inventory=(LogisticsInventory)typeof(TaskActions).GetField("inventory",flags).GetValue(w.Tasks);
            var catalog=(Json.Record)w.Navigation.Available(w.Chef);catalog["complete"]=false;catalog["destinations"]=new List<object>();
            inventory.BeginObservation();inventory.Refresh(w.Chef,catalog);w.Chef.GroundCollider=null;
            inventory.Items.Insert(0,new LogisticsItem {Item=null});
            var select=typeof(TaskActions).GetMethod("SelectAssemblyWork",flags);var job=select.Invoke(w.Tasks,null);
            Check(job!=null && (string)job.GetType().GetField("Operation").GetValue(job)=="transfer_contents" &&
                (GameObject)job.GetType().GetField("Source").GetValue(job)==fish,
                "incomplete broad map keeps useful held food for targeted route planning instead of swapping to another ingredient");
            Check(w.Tasks.Run.Running,"retired inventory entries between native consumption and refresh do not terminate the task");
            catalog["complete"]=true;
            Check(select.Invoke(w.Tasks,null)==null,"completed unreachable route evidence does not authorize speculative normal assembly work");
            inventory.EndObservation();
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var fish=AssemblyFood(w,w.Target,"fish");
            var rice=AssemblyFood(w,w.Delivery(),"rice");
            plate.Add(new ClientPlate {Accepts=item=>item==rice || !RecipeProof.Empty(RecipeProof.Composition(plate))});
            var request=AssemblyJob(w,new[]{plate},new[]{fish,rice},Leaf("fish"),Leaf("rice"));StartAssembly(w,request);
            GameObject firstPickup=null;
            int additions=DriveAssembly(w,beforeArrival:(target,op)=>{if(op=="pick_up" && firstPickup==null)firstPickup=ActionObservations.Receiver(target);});
            Check(firstPickup==rice && additions==2 && w.Tasks.Run.State=="succeeded",
                "native intermediate plating constraints select executable recipe work before collecting blocked food");
        }
        var a=Leaf("fish");var b=Leaf("rice");var goal=Recipe(a,b);
        Check(RecipeProof.Advances(Recipe(a),b,goal),"missing exact component advances recipe");
        Check(!RecipeProof.Advances(Recipe(a),a,goal),"native-compatible duplicate cannot advance recipe");
        Check(!RecipeProof.Advances(Recipe(a),Leaf("prawn"),goal),"wrong recipe component rejected");
        Check(RecipeProof.Subset(Recipe(a,a),Recipe(a,a,b)) && !RecipeProof.Subset(Recipe(a,a),goal),"multiplicity preserved");
        var cooked=new CookedCompositeAssembledNode {m_composition=new[]{b},m_cookingStep=new CookingStepData(),m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
        Check(!RecipeProof.Subset(b,Recipe(a,cooked)),"raw rice cannot satisfy cooked component");
        Check(!RecipeProof.Supported(new CompositeAssembledNode {m_composition=new[]{a},m_optional=new[]{b}}),"optional recipe semantics fail closed");
        foreach(bool partial in new[]{false,true})
        {
            var w=new World();var c1=w.Delivery();var c2=w.Delivery();c2.transform.position=new Vector3(-2,0,0);
            var unsuitable=AssemblyPlate(w,c1,Leaf("prawn"));var chosen=AssemblyPlate(w,c2,partial?new[]{Leaf("rice")}:new AssembledDefinitionNode[0]);
            var food=AssemblyFood(w,w.Target,"fish");var request=AssemblyJob(w,new[]{unsuitable,chosen},new[]{food},partial?new[]{a,b}:new[]{a});
            StartAssembly(w,request);int additions=DriveAssembly(w);
            Check(w.Tasks.Run.State=="succeeded" && additions==1,"chooses suitable alternative and resumes partial recipe: "+Json.Encode(w.Tasks.Describe()));
            Check(RecipeProof.Equal(RecipeProof.Composition(unsuitable),Leaf("prawn")),"other dish left unchanged");
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var wrong=AssemblyFood(w,w.Target,"prawn");
            var request=AssemblyJob(w,new[]{plate},new[]{wrong},a);StartAssembly(w,request);DriveAssembly(w,20);
            Check(w.Tasks.Run.Running && !w.Inputs.Contains(1),"assigned but native-compatible wrong ingredient never picked or added");w.Tasks.Cancel("test");
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var food=AssemblyFood(w,w.Target,"fish");
            var request=AssemblyJob(w,new[]{plate},new GameObject[0],a);StartAssembly(w,request);DriveAssembly(w,20);
            Check(w.Tasks.Run.Running && !w.Inputs.Contains(1),"unallocated matching ingredient never stolen");w.Tasks.Cancel("test");
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());
            var request=AssemblyJob(w,new[]{plate},new GameObject[0],a);
            AssemblyOrders.Observe()[0].Controller.m_activeOrders.Clear();
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Id="expired",Operation="task",Task=request})=="assembly_order_unavailable" && !w.Tasks.Run.Running,
                "expired exact ticket rejects before generic empty-order availability can turn it into indefinite waiting");
        }
        foreach(bool expire in new[]{false,true})
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var food=AssemblyFood(w,w.Target,"fish");
            var request=AssemblyJob(w,new[]{plate},new[]{food},a);StartAssembly(w,request);bool changed=false;
            int additions=DriveAssembly(w,60,(target,op)=>{
                if(changed || op!="transfer_contents")return;changed=true;
                if(expire)AssemblyOrders.Observe()[0].Controller.m_activeOrders.Clear();
                else plate.GetComponent<ServerIngredientContainer>().Contents.Add(Leaf("prawn"));
            });
            Check(changed && additions==0 && w.Carrier.Item==food,"ticket/base change before input edge prevents addition");
            Check(expire?w.Tasks.Run.Reason=="assembly_order_unavailable":w.Tasks.Run.Running,"expired ticket stops; contaminated base waits");w.Tasks.Cancel("test");
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var food=AssemblyFood(w,w.Target,"fish");
            var request=AssemblyJob(w,new[]{plate},new[]{food},a);StartAssembly(w,request);DriveAssembly(w,100,null,false);
            Check(w.Tasks.Run.State=="failed" && w.Carrier.Item==food,"destination changed without source consumption is ambiguous, never credited/replayed");
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var food=AssemblyFood(w,w.Target,"fish");
            var request=AssemblyJob(w,new[]{plate},new[]{food},a);StartAssembly(w,request);
            DriveAssembly(w,20,(target,op)=>{if(op=="pick_up")target.GetComponent<ServerAttachStation>().Item=null;});
            Check(!RecipeProof.Equal(RecipeProof.Composition(plate),a),"a removed assigned item is not silently replaced");w.Tasks.Cancel("test");
        }
    }
}
