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
        food.Add(new PhysicalAttachment());food.Add(new Pickup());counter.GetComponent<ServerAttachStation>().Item=food;return food;
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
    private static int DriveAssembly(World w, int limit=180, Action<GameObject,string> beforeArrival=null, bool consume=true)
    {
        int additions=0;
        for(int n=0;n<limit && w.Tasks.Run.Running;n++)
        {
            w.Tick(.11f);if(!w.Skills.Run.Running)continue;
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
                if(contents.GetContentsCount()!=0 && !consume)continue;
                contents.Contents.Add(RecipeProof.Composition(cargo));
                if(consume){cargo.activeInHierarchy=false;w.Hold(null);} additions++;
            }
        }
        return additions;
    }
    private static void AssemblyProofs()
    {
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
