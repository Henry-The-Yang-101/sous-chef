using System;
using System.Linq;
using SousChef.Core;
using SousChef.Plugin.Actions;
using UnityEngine;

internal static partial class Program
{
    private static GameObject SpawnAssemblyIngredient(World w, GameObject prefab)
    {
        var item=prefab.GetComponent<IngredientPropertiesComponent>()==null ? new GameObject(prefab.name) : w.Ingredient(prefab.name);
        if(item.GetComponent<IngredientPropertiesComponent>()!=null)item.GetComponent<IngredientPropertiesComponent>().Node=ActionObservations.Composition(prefab);
        item.Add(new PhysicalAttachment());item.Add(new Pickup());
        var work=prefab.GetComponent<WorkableItem>();
        if(work!=null) { item.Add(new WorkableItem {Next=work.Next});item.Add(new ServerWorkableItem()); }
        return item;
    }
    private static GameObject AssemblyRawPrefab(World w, string name, int stages=1)
    {
        var output=w.Ingredient(name);output.GetComponent<IngredientPropertiesComponent>().Node=Leaf(name);
        for(int i=0;i<stages;i++)
        { var raw=new GameObject(name+" raw "+i);raw.Add(new WorkableItem {Next=output});raw.Add(new ServerWorkableItem());output=raw; }
        return output;
    }
    private static void AssemblyFromScratch()
    {
        foreach(bool automatic in new[]{false,true})
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());
            var prefab=AssemblyRawPrefab(w,"tomato",2);
            var supply=w.Delivery();supply.Add(new Pickup());supply.Add(new PickupItemSpawner {m_itemPrefab=prefab});
            var board=w.Target;
            var request=AssemblyJob(w,new[]{plate},new GameObject[0],Leaf("tomato"),Leaf("tomato"));
            if(automatic)request.Assembly.Bases.Clear();else request.Assembly.Components.Add(w.IdOf(supply));
            StartAssembly(w,request);int pickups=0;
            int additions=DriveAssembly(w,500,beforeArrival:(target,op)=>{if(target==supply && op=="pick_up")pickups++;});
            Check(w.Tasks.Run.State=="succeeded" && additions==2 && pickups==2,
                "fetches exact recipe quantities and follows multiple native work successors under one request: "+Json.Encode(w.Tasks.Describe()));
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var board=w.Target;board.Add(new ServerWorkstation());
            var raw=SpawnAssemblyIngredient(w,AssemblyRawPrefab(w,"fish"));board.GetComponent<ServerAttachStation>().Item=raw;
            var request=AssemblyJob(w,new[]{plate},new[]{raw},Leaf("fish"));StartAssembly(w,request);
            Check(DriveAssembly(w)==1 && w.Tasks.Run.State=="succeeded","explicit raw stock is chopped in place and its exact successor is plated");
        }
        {
            var w=new World();var first=AssemblyPlate(w,w.Delivery(),Leaf("fish"));
            var alternative=AssemblyPlate(w,w.Delivery(),Leaf("rice"));
            var rice=AssemblyRawPrefab(w,"rice",0);w.Target.Add(new PickupItemSpawner {m_itemPrefab=rice});
            var request=AssemblyJob(w,new[]{first,alternative},new GameObject[0],Leaf("fish"),Leaf("rice"));
            request.Assembly.Components.Add(w.IdOf(w.Target));StartAssembly(w,request);
            Check(DriveAssembly(w)==1 && w.Tasks.Run.State=="succeeded" && RecipeProof.Equal(RecipeProof.Composition(alternative),Leaf("rice")),
                "an unselected partial plate cannot hide missing ingredient demand on the selected plate");
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());
            var wrong=AssemblyRawPrefab(w,"prawn");w.Target.Add(new PickupItemSpawner {m_itemPrefab=wrong});
            var board=w.Delivery();board.Add(new ServerWorkstation());
            var request=AssemblyJob(w,new[]{plate},new GameObject[0],Leaf("fish"));request.Assembly.Bases.Clear();StartAssembly(w,request);
            DriveAssembly(w,40);Check(w.Tasks.Run.Running && !w.Inputs.Contains(1),"automatic sourcing never substitutes a wrong ingredient");w.Tasks.Cancel("test");
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var prefab=AssemblyRawPrefab(w,"fish");
            var supply=w.Delivery();supply.Add(new Pickup());supply.Add(new PickupItemSpawner {m_itemPrefab=prefab});
            w.Target.GetComponent<Placement>().Accepts=item=>false;
            var request=AssemblyJob(w,new[]{plate},new GameObject[0],Leaf("fish"));request.Assembly.Bases.Clear();StartAssembly(w,request);
            DriveAssembly(w,30);Check(w.Tasks.Run.Running && !w.Inputs.Contains(1),"missing chopping board waits without dispensing unusable stock");
            w.Target.GetComponent<Placement>().Accepts=item=>true;
            Check(DriveAssembly(w)==1 && w.Tasks.Run.State=="succeeded","newly available board resumes the same automatic order");
        }
        foreach(bool inSink in new[]{true,false})
        {
            var w=new World();var rack=w.Delivery();rack.Add(new Pickup());
            var sink=w.Target;sink.Add(new WashingStation());
            var washing=sink.Add(new ServerWashingStation {m_plateCount=inSink ? 2 : 0,m_dryingAttachStation=rack.GetComponent<ServerAttachStation>()});
            if(!inSink)
            {
                var dirty=new GameObject("dirty plates");dirty.Add(new ServerDirtyPlateStack());dirty.Add(new ServerStack()).m_stackItems.AddRange(new[]{new GameObject("dirty plate 1"),new GameObject("dirty plate 2")});
                dirty.Add(new PhysicalAttachment());dirty.Add(new Pickup());
                var counter=w.Delivery();counter.Add(new Pickup());counter.GetComponent<ServerAttachStation>().Item=dirty;
            }
            var fish=AssemblyFood(w,w.Delivery(),"fish");
            var request=AssemblyJob(w,new GameObject[0],new GameObject[0],Leaf("fish"));StartAssembly(w,request);
            bool washed=false;
            int additions=DriveAssembly(w,onTick:()=>{
                if(!washed && w.Skills.Run.Operation=="work_at" && w.Skills.Run.Phase=="working")
                { washed=true;washing.m_plateCount--;AssemblyPlate(w,rack); }
            });
            Check(washed && washing.m_plateCount==1 && additions==1 && w.Tasks.Run.State=="succeeded",
                "automatic order washes only the needed plate and then uses the native drying-rack output: "+Json.Encode(w.Tasks.Describe()));
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var obstruction=AssemblyFood(w,w.Target,"prawn");
            var supply=w.Delivery();supply.Add(new Pickup());supply.Add(new PickupItemSpawner {m_itemPrefab=AssemblyRawPrefab(w,"fish")});
            var staging=w.Delivery();staging.Add(new Pickup());staging.transform.position=new Vector3(-2,0,0);
            var request=AssemblyJob(w,new[]{plate},new GameObject[0],Leaf("fish"));request.Assembly.Bases.Clear();StartAssembly(w,request);
            Check(DriveAssembly(w,400)==1 && w.Tasks.Run.State=="succeeded" && obstruction.activeInHierarchy && RecipeProof.Equal(RecipeProof.Composition(obstruction),Leaf("prawn")),
                "an occupied chopping board is cleared safely before fetching the recipe's raw stock: "+Json.Encode(w.Tasks.Describe()));
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var board=w.Target;
            var raw=SpawnAssemblyIngredient(w,AssemblyRawPrefab(w,"fish"));board.GetComponent<ServerAttachStation>().Item=raw;
            var burner=w.Delivery();burner.Add(new Pickup());burner.Add(new ServerCookingStation());
            var pan=new GameObject("cooking rice");pan.Add(new PhysicalAttachment());pan.Add(new Pickup());pan.Add(new IngredientTransfer());
            pan.Add(new ServerIngredientContainer()).Contents.Add(Leaf("rice"));var step=new CookingStepData {m_uID=31};
            pan.Add(new CookingHandler {m_cookingType=step});pan.Add(new CookableContainer());var cooking=pan.Add(new ServerCookingHandler());pan.Add(new ProcessorComposition {Step=step});
            burner.GetComponent<ServerAttachStation>().Item=pan;
            var staging=w.Delivery();staging.Add(new Pickup());staging.transform.position=new Vector3(-2,0,0);
            var cooked=new CookedCompositeAssembledNode {m_composition=new[]{Leaf("rice")},m_cookingStep=step,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
            var request=AssemblyJob(w,new[]{plate},new[]{raw},Leaf("fish"),cooked);
            request.Assembly.Equipment.Add(w.Navigation.RegisterItem(pan));request.Assembly.Equipment.Add(w.IdOf(burner));StartAssembly(w,request);
            for(int i=0;i<50 && w.Skills.Run.Phase!="working";i++)
            { w.Tick(.11f);if(w.Skills.Run.Phase=="navigating")w.ArriveAt(w.Navigation.Resolve(w.Skills.Run.Target)); }
            Check(w.Skills.Run.Phase=="working","chopping starts while another dependency cooks");
            raw.GetComponent<ServerWorkableItem>().Progress=.5f;cooking.m_ServerData.m_cookingState=ProcessingState.Completed;w.Tick();
            Check(w.Skills.Run.Reason=="task_precondition_changed" && ActionObservations.Occupant(board)==raw,
                "continuous chopping yields immediately to ready heat and preserves partial work");
            GameObject firstPickup=null;
            int additions=DriveAssembly(w,300,beforeArrival:(target,op)=>{if(op=="pick_up" && firstPickup==null)firstPickup=ActionObservations.Receiver(target);});
            Check(firstPickup==pan && additions==2 && w.Tasks.Run.State=="succeeded","rescues cooking before returning to chop and finish the exact dish: "+Json.Encode(w.Tasks.Describe()));
        }
        {
            var w=new World();var plate=AssemblyPlate(w,w.Delivery());var supply=w.Delivery();supply.Add(new Pickup());
            supply.Add(new PickupItemSpawner {m_itemPrefab=AssemblyRawPrefab(w,"potato")});
            var burner=w.Delivery();burner.Add(new Pickup());burner.Add(new ServerCookingStation());
            var pan=new GameObject("empty potato pan");pan.Add(new PhysicalAttachment());pan.Add(new Pickup());pan.Add(new IngredientTransfer());
            pan.Add(new ServerIngredientContainer());var step=new CookingStepData {m_uID=31};
            pan.Add(new CookingHandler {m_cookingType=step});pan.Add(new CookableContainer());var cooking=pan.Add(new ServerCookingHandler());pan.Add(new ProcessorComposition {Step=step});
            burner.GetComponent<ServerAttachStation>().Item=pan;
            var staging=w.Delivery();staging.Add(new Pickup());staging.transform.position=new Vector3(-2,0,0);
            var goal=new CookedCompositeAssembledNode {m_composition=new[]{Leaf("potato")},m_cookingStep=step,m_progress=CookedCompositeOrderNode.CookingProgress.Cooked};
            var request=AssemblyJob(w,new[]{plate},new GameObject[0],goal);request.Assembly.Bases.Clear();StartAssembly(w,request);
            GameObject firstPickup=null;bool cooked=false;
            int additions=DriveAssembly(w,500,beforeArrival:(target,op)=>{if(op=="pick_up" && firstPickup==null)firstPickup=target;},onTick:()=>{
                if(ActionObservations.Occupant(burner)==pan && pan.GetComponent<ServerIngredientContainer>().GetContentsCount()>0)
                {cooked=true;cooking.m_ServerData.m_cookingState=ProcessingState.Completed;}
            });
            Check(firstPickup==supply && cooked && additions==2 && w.Tasks.Run.State=="succeeded",
                "automatic cooked order fetches and chops before staging empty cookware, then cooks and plates: "+Json.Encode(w.Tasks.Describe()));
        }
    }
}
