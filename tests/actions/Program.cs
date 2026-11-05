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
    private static void Check(bool value,string reason) { if (!value) throw new Exception(reason); checks++; }
    private sealed class Pickup : MonoBehaviour, IPickup { public bool Allowed=true; public bool CanHandlePickup(ICarrier c) { return Allowed; } }
    private sealed class Placement : MonoBehaviour, IPlacement { public Func<GameObject,bool> Accepts = item=>true; public bool CanHandlePlacement(ICarrier c,Vector2 d,PlacementContext x) { return Accepts(c.InspectCarriedItem()); } }
    private sealed class Throwable : MonoBehaviour, IClientThrowable, ICatchable { public bool CanHandleThrow(ClientAttachmentThrower t,Vector2 d) { return true; } }
    private sealed class Catcher : MonoBehaviour, IHandleCatch { public bool Allowed=true; public bool CanHandleCatch(ICatchable i,Vector2 d) { return Allowed; } }
    private sealed class World
    {
        public readonly PlayerControls Chef;
        public readonly GameObject Target;
        public readonly NavigationActions Navigation;
        public readonly InteractionActions Skills;
        public readonly LogisticsActions Logistics;
        public readonly TaskActions Tasks;
        public readonly ServerPlayerAttachmentCarrier Carrier;
        public readonly List<int> Inputs = new List<int>();
        public readonly List<Collider> Colliders = new List<Collider>();
        public string Id;
        public int Releases;
        public World(Action<GameObject> configure = null)
        {
            UnityEngine.Object.All.Clear(); Time.realtimeSinceStartup=10;Time.frameCount=1;
            TimeManager.Scale=1;
            var actor=new GameObject("chef"); Chef=actor.Add(new PlayerControls());
            actor.Add(new CapsuleCollider());actor.Add(new GroundCast());actor.Add(new PlayerIDProvider());
            actor.Add(new ClientPlayerAttachmentCarrier());Carrier=actor.Add(new ServerPlayerAttachmentCarrier());
            actor.Add(new ClientPlayerControlsImpl_Default()); Chef.transform.position=new Vector3(0,0,-1);
            var floor=new GameObject("floor").Add(new BoxCollider {size=new Vector3(20,.1f,20),center=new Vector3(0,-.05f,0)});
            floor.gameObject.layer=9; Chef.GroundCollider=floor;
            Target=new GameObject("station");Target.Add(new ServerWorkstation());Target.Add(new ServerAttachStation());
            var box=Target.Add(new BoxCollider {size=new Vector3(.8f,1,.8f),center=new Vector3(0,.5f,0)});
            var use=Target.Add(new ClientInteractable());Target.Add(new ServerInteractable());Target.Add(new Pickup());Target.Add(new Placement());
            if (configure != null) configure(Target);
            Colliders.Add(floor);Colliders.Add(box);
            Physics.Nearby=(p,r)=>Colliders.ToArray();
            Physics.Ray=(p,d,length,mask)=>d.y<-.9f && p.y>=0 && p.y<=length ? new[] {new RaycastHit {collider=floor,point=new Vector3(p.x,0,p.z),normal=Vector3.up,distance=p.y}} : new RaycastHit[0];
            Physics.GroundSphere=(p,r,d,length)=>Physics.Ray(p-Vector3.up*r,d,length,1);
            Physics.Overlap=(a,b,r)=>new Collider[0];Physics.Cast=(a,b,r,d,l)=>new RaycastHit[0];
            Physics.Penetration=(a,p,q,b,r,s)=>false;Physics.gravity=new Vector3(0,-9.81f,0);
            Chef.CurrentInteractionObjects.m_TheOriginalHandlePickup=Target;
            Chef.CurrentInteractionObjects.m_iHandlePickup=Target.GetComponent<IPickup>();
            Chef.CurrentInteractionObjects.m_iHandlePlacement=Target.GetComponent<IPlacement>();
            Chef.CurrentInteractionObjects.m_interactable=use;
            Navigation=new NavigationActions((d,ms)=>{},()=>{});
            Skills=new InteractionActions(Navigation,b=>Inputs.Add(b),()=>{if((Logistics!=null && Logistics.Run.Running) || (Tasks!=null && Tasks.Run.Running)) Inputs.Add(0); else Releases++;});
            Logistics=new LogisticsActions(Navigation,Skills,b=>Inputs.Add(b),()=>Releases++);
            Tasks=new TaskActions(Navigation,Skills,b=>Inputs.Add(b),()=>Releases++);
            for(int i=0;i<10 && Id==null;i++)
            {
                var catalog=(Json.Record)Navigation.Available(Chef);
                foreach(Json.Record d in (List<object>)catalog["destinations"]) if((int)d["objectInstanceId"]==Target.GetInstanceID()) Id=(string)d["id"];
            }
            Check(Id!=null,"fixture has a real navigable target");
        }
        public GameObject Ingredient(string label)
        {
            var go=new GameObject(label);go.Add(new IngredientPropertiesComponent {Node=new AssembledDefinitionNode(label),m_ingredientOrderNode=new Definition {name=label,m_uID=label.GetHashCode()}});go.Add(new IngredientTransfer());return go;
        }
        public void Hold(GameObject item) { Carrier.Item=item;Chef.GetComponent<ClientPlayerAttachmentCarrier>().Item=item; }
        public string Start(string op,string condition=null,string state=null,int ms=10000)
        { return Skills.Start(Chef,1,new ControlCommand {Operation=op,Destination=Id,Condition=condition,DesiredState=state,Milliseconds=ms}); }
        public void Arrive() { Navigation.Stop("arrived");Tick(); }
        public void Tick(float delta=.05f) { Time.realtimeSinceStartup+=delta;Time.frameCount++;Skills.Tick();Logistics.Tick();Tasks.Tick(); }
        public void Verify() { Tick();Tick();Tick(); }
        public GameObject Delivery()
        {
            var go=new GameObject("handoff counter");go.transform.position=new Vector3(2,0,0);go.Add(new AttachStation());go.Add(new ServerAttachStation());go.Add(new Placement());
            Colliders.Add(go.Add(new BoxCollider {center=new Vector3(0,.5f,0),size=new Vector3(.8f,1,.8f)}));
            Navigation.Available(Chef);return go;
        }
        public string IdOf(GameObject go) { return (string)((Json.Record)Navigation.TargetIdentity(go))["id"]; }
        public void ArriveAt(GameObject go)
        {
            Chef.CurrentInteractionObjects.m_TheOriginalHandlePickup=go;Chef.CurrentInteractionObjects.m_iHandlePickup=go.GetComponent<IPickup>();
            Chef.CurrentInteractionObjects.m_iHandlePlacement=go.GetComponent<IPlacement>();Chef.CurrentInteractionObjects.m_interactable=go.GetComponent<ClientInteractable>();Arrive();
        }
    }
    private static void CounterForecasts()
    {
        var motion=new CounterMotion();motion.Observe(new Vector3(0,0,0),0,true);
        motion.Observe(new Vector3(4,0,0),2,false);
        Check(motion.ArrivalSeconds(2)==Single.MaxValue,"one-way motion away from a human is not promised to return");
        motion.Observe(new Vector3(3,0,0),3,false);
        Check(Math.Abs(motion.ArrivalSeconds(3)-3)<.01f,"motion toward an observed human stop predicts arrival");
        motion.Observe(new Vector3(0,0,0),6,true);motion.Observe(new Vector3(4,0,0),8,false);
        motion.Observe(new Vector3(4,0,0),9,false);
        Check(Math.Abs(motion.ArrivalSeconds(9)-3)<.01f,"observed full cycles predict return from a stopped loading counter");
        motion=new CounterMotion();motion.Observe(new Vector3(4,0,0),0,false);motion.Observe(new Vector3(3,0,0),1,false);
        Check(motion.ArrivalSeconds(1)==Single.MaxValue,"unknown motion cannot authorize a transport handoff");
    }
    private static void PersistentTasks()
    {
        var w=new World(); var destination=w.Delivery(); destination.Add(new Pickup());
        var human=new GameObject("human");human.Add(new PlayerIDProvider {Id=0});var p=human.Add(new PlayerControls {GroundCollider=w.Chef.GroundCollider});
        human.Add(new CapsuleCollider());human.Add(new GroundCast());human.Add(new ClientPlayerAttachmentCarrier());human.Add(new ServerPlayerAttachmentCarrier());human.transform.position=new Vector3(3,0,-1);
        var item=w.Ingredient("Tomato_Raw");w.Target.Add(new PickupItemSpawner {m_itemPrefab=item});
        var request=new TaskRequest {Action="prepare_near_player",Repetitions=2};request.Items.Add(new PreparationRequest {Selector="tomato",Preparation="raw",Quantity=1});
        Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Id="two-batches",Operation="task",Task=request})==null,"finite preparation task accepted");
        var shipment=new GameObject("ingredient still in transit");
        typeof(TaskActions).GetMethod("DispatchDelivery",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(w.Tasks,new object[] {shipment,1});
        int pickups=0;
        for(int n=0;n<150 && w.Tasks.Run.Running;n++)
        {
            w.Tick(.25f);
            if(w.Skills.Run.Running && w.Skills.Run.Phase=="navigating")
            {
                var target=w.Navigation.Resolve(w.Skills.Run.Target);w.ArriveAt(target);
            }
            if(w.Skills.Run.Running && w.Skills.Run.Phase=="pressing" && w.Inputs.Last()==1)
            {
                if(w.Skills.Run.Operation=="pick_up" && w.Carrier.Item==null) { Check(w.Navigation.Resolve(w.Skills.Run.Target)==w.Target,"repeat preparation fetches a fresh dispenser item instead of its delivered ingredient"); destination.GetComponent<ServerAttachStation>().Item=null; w.Hold(w.Ingredient("Tomato_Raw"));pickups++;}
                if(w.Skills.Run.Operation=="place_at" && w.Carrier.Item!=null) {destination.GetComponent<ServerAttachStation>().Item=w.Carrier.Item;w.Hold(null);}
            }
        }
        Check(w.Tasks.Run.Running && pickups==2 && w.Releases==0,"pending transport does not block preparing subsequent repetitions, but finite completion still waits for delivery");
        human.GetComponent<ServerPlayerAttachmentCarrier>().Item=shipment;
        for(int n=0;n<8 && w.Tasks.Run.Running;n++)w.Tick(.25f);
        Check(w.Tasks.Run.State=="succeeded" && pickups==2 && w.Releases==1,"two repetitions perform two observed deliveries with one parent release");
        request.Repetitions=0;Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Id="loop",Operation="task",Task=request})==null,"infinite task accepted");
        w.Target.GetComponent<BoxCollider>().enabled=false;w.Tick(1000);w.Tick(1000);
        Check(w.Tasks.Run.Running && w.Releases==1,"infinite task survives long environmental wait");
        w.Tasks.Cancel("stopped");Check(!w.Tasks.Run.Running && !w.Skills.Run.Running && w.Releases==2,"stop cancels complete task and releases once");
        var busy=(Json.Record)w.Navigation.Available(w.Chef);busy["actions"]=new[] {Json.Object("name","stop")};
        w.Logistics.Decorate(busy,w.Chef,true);w.Tasks.Decorate(busy,w.Chef,true);
        Check(((List<object>)busy["taskOptions"]).Count==0,"catalogue polling during a parent wait withholds jobs without casting the busy action array");
    }
    private static void HumanSinkEligibility()
    {
        var w=new World();
        var dirty=new GameObject("dirty");dirty.Add(new ServerDirtyPlateStack());dirty.Add(new ServerStack());dirty.Add(new PhysicalAttachment());
        w.Target.GetComponent<ServerAttachStation>().Item=dirty;
        var otherFloor=new GameObject("other floor").Add(new BoxCollider {size=new Vector3(16,.1f,16),center=new Vector3(30,-.05f,0)});otherFloor.gameObject.layer=9;w.Colliders.Add(otherFloor);
        var sink=new GameObject("human sink");sink.transform.position=new Vector3(30,0,0);sink.Add(new WashingStation());sink.Add(new ServerWashingStation());sink.Add(new ServerAttachStation());sink.Add(new ClientInteractable());sink.Add(new Placement());w.Colliders.Add(sink.Add(new BoxCollider {center=new Vector3(0,.5f,0),size=new Vector3(.8f,1,.8f)}));
        var actor=new GameObject("human");actor.Add(new PlayerIDProvider {Id=0});actor.Add(new PlayerControls {GroundCollider=otherFloor});actor.Add(new CapsuleCollider());actor.Add(new GroundCast());actor.Add(new ClientPlayerAttachmentCarrier());actor.Add(new ServerPlayerAttachmentCarrier());actor.transform.position=new Vector3(30,0,-2);
        var firstFloor=w.Chef.GroundCollider;
        Physics.Ray=(point,direction,length,mask)=> {
            if(direction.y>-.9f || point.y<0 || point.y>length)return new RaycastHit[0];
            Collider support=point.x>=22 && point.x<=38 ? otherFloor : point.x>=-10 && point.x<=10 ? firstFloor : null;
            return support==null ? new RaycastHit[0] : new[] {new RaycastHit {collider=support,point=new Vector3(point.x,0,point.z),normal=Vector3.up,distance=point.y}};
        };
        bool offered=false;
        for(int i=0;i<500 && !offered;i++)
        {
            var catalog=(Json.Record)w.Navigation.Available(w.Chef);w.Tasks.Decorate(catalog,w.Chef);
            var names=((List<object>)catalog["taskOptions"]).Cast<Json.Record>().Select(row=>(string)row["name"]).ToList();
            Check(!names.Contains("wash_dishes"),"washing is withheld without a bot route to the human's sink");offered=names.Contains("deliver_dirty_dishes");
            Time.realtimeSinceStartup+=.1f;Time.frameCount++;
        }
        Check(offered,"human-only sink delivery uses the same navigator with an independent human origin");
    }
    private static void NativeUtensilDefinitions()
    {
        var w=new World();var pot=new GameObject("arbitrary native prefab name");pot.Add(new ServerIngredientContainer());pot.Add(new CookableContainer());pot.Add(new CookingHandler {m_cookingType=new Definition {name="Pot"}});
        Check(LogisticsInventory.UtensilType(pot)=="pot","native cooking step identifies a pot without item properties or object-name guesses");
        pot.GetComponent<CookingHandler>().m_cookingType.name="FryingPan";
        Check(LogisticsInventory.UtensilType(pot)=="pan","native frying step identifies pan capability");
        pot.Add(new MixableContainer());pot.Add(new MixingHandler {m_mixingType=new Definition {name="MixingBowl"}});
        Check(LogisticsInventory.UtensilType(pot)=="bowl","mixing bowl uses its mixing definition even when it also supports cooking");
        pot.Add(new PreparationContainer());Check(LogisticsInventory.UtensilType(pot)==null,"an assembly base cannot be classified as an empty utensil");
    }
    private static void PreparationEquipmentDiscovery()
    {
        var w=new World();var item=w.Ingredient("potato");w.Hold(item);
        var catalog=(Json.Record)w.Navigation.Available(w.Chef);w.Tasks.Decorate(catalog,w.Chef);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var inventory=(LogisticsInventory)typeof(TaskActions).GetField("inventory",flags).GetValue(w.Tasks);
        var board=typeof(TaskActions).GetMethod("Board",flags);
        catalog["complete"]=false;catalog["destinations"]=new List<object>();
        inventory.BeginObservation();inventory.Refresh(w.Chef,catalog);
        var args=new object[] {item,null,false};
        Check((GameObject)board.Invoke(w.Tasks,args)==w.Target,"incomplete discovery may propose a compatible board on the current support for active route validation");
        w.Target.GetComponent<Placement>().Accepts=cargo=>false;
        Check(board.Invoke(w.Tasks,args)==null && !(bool)args[1],"provisional preparation still respects native placement vetoes");
        w.Target.GetComponent<Placement>().Accepts=cargo=>true;w.Target.GetComponent<ServerAttachStation>().Item=w.Ingredient("other food");
        Check(board.Invoke(w.Tasks,args)==null && (bool)args[1],"a reachable board holding other food is reported occupied, not unreachable");
        w.Target.GetComponent<ServerAttachStation>().Item=null;catalog["complete"]=true;
        Check(board.Invoke(w.Tasks,args)==null && !(bool)args[1],"complete discovery cannot propose a board without a certified route");
        catalog["complete"]=false;w.Target.transform.position=new Vector3(0,3,0);
        Check(board.Invoke(w.Tasks,args)==null,"a board on another elevation is not a provisional local station");
        inventory.EndObservation();
    }
    private static void PreparedIngredientSources()
    {
        var w=new World();var chopped=w.Ingredient("arbitrary chopped identity");
        var root=new GameObject("arbitrary raw prefab");root.Add(new WorkableItem {Next=chopped});
        w.Target.Add(new PickupItemSpawner {m_itemPrefab=root});
        var counter=w.Delivery();counter.Add(new Pickup());counter.GetComponent<ServerAttachStation>().Item=chopped;
        var catalog=(Json.Record)w.Navigation.Available(w.Chef);w.Tasks.Decorate(catalog,w.Chef);
        string rootId,label;LogisticsInventory.IngredientIdentity(root,out rootId,out label);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var source=typeof(TaskActions).GetMethod("PreparationSource",flags);
        foreach(string state in new[] {"chopped","cooked","mixed"})
        {
            var selected=(LogisticsItem)source.Invoke(w.Tasks,new object[] {new PreparationRequest {Selector=rootId,Preparation=state}});
            Check(selected!=null && selected.Item==chopped,"processed preparation recognizes and prefers native work output for "+state);
        }
        var raw=(LogisticsItem)source.Invoke(w.Tasks,new object[] {new PreparationRequest {Selector=rootId,Preparation="raw"}});
        Check(raw!=null && raw.Supply,"raw preparation retains the exact dispenser identity instead of substituting chopped food");
        var delivered=(HashSet<GameObject>)typeof(TaskActions).GetField("deliveredItems",flags).GetValue(w.Tasks);delivered.Add(chopped);
        var fresh=(LogisticsItem)source.Invoke(w.Tasks,new object[] {new PreparationRequest {Selector=rootId,Preparation="cooked"}});
        Check(fresh!=null && fresh.Supply,"later-stage matching preserves the human's delivered ingredients");
        delivered.Clear();counter.GetComponent<ServerAttachStation>().Item=w.Ingredient("unrelated processed food");
        var different=(LogisticsItem)source.Invoke(w.Tasks,new object[] {new PreparationRequest {Selector=rootId,Preparation="chopped"}});
        Check(different!=null && different.Supply,"unrelated processed food never matches a preparation root");
        var intermediate=new GameObject("intermediate prefab");intermediate.Add(new WorkableItem {Next=chopped});root.GetComponent<WorkableItem>().Next=intermediate;
        var work=typeof(TaskActions).GetMethod("PreparationWork",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        Check((int)work.Invoke(null,new object[] {root})==2,"preparation follows every native work stage");
    }
    private static void AvailablePreparationWork()
    {
        foreach(string preparation in new[] {"raw","chopped","cooked"})
        {
            var w=new World();var human=new GameObject("human");human.Add(new PlayerIDProvider {Id=0});
            human.Add(new PlayerControls {GroundCollider=w.Chef.GroundCollider});human.Add(new CapsuleCollider());human.Add(new GroundCast());
            human.Add(new ServerPlayerAttachmentCarrier());human.Add(new ClientPlayerAttachmentCarrier());
            var potato=w.Ingredient("potato");w.Target.Add(new PickupItemSpawner {m_itemPrefab=potato});
            var request=new TaskRequest {Action="prepare_near_player"};
            request.Items.Add(new PreparationRequest {Selector="missing chicken",Preparation="cooked",Quantity=1});
            request.Items.Add(new PreparationRequest {Selector="potato",Preparation=preparation,Quantity=1});
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Id="available-work",Operation="task",Task=request})==null,"mixed preparation request accepted");
            for(int i=0;i<10 && !w.Skills.Run.Running;i++)w.Tick(.25f);
            Check(w.Skills.Run.Running && w.Skills.Run.Operation=="pick_up" && w.Navigation.Resolve(w.Skills.Run.Target)==w.Target,
                "missing earlier chicken cannot block available potato requested "+preparation);
            w.Tasks.Cancel("test_end");
        }
    }
    private static void OccupiedBoards()
    {
        // Raft kitchens: the human throws raw food onto P2's floor, counters
        // and boards. P2 must never hold cargo while it alone could free a board.
        Func<World,string,GameObject> raw=(w,label)=>{
            var food=w.Ingredient(label+" raw");food.Add(new ServerWorkableItem());food.Add(new WorkableItem {Next=w.Ingredient(label+" chopped")});return food;
        };
        Func<World> kitchen=()=>{
            var w=new World();var human=new GameObject("human");human.Add(new PlayerIDProvider {Id=0});human.transform.position=new Vector3(3,0,-1);
            human.Add(new PlayerControls {GroundCollider=w.Chef.GroundCollider});human.Add(new CapsuleCollider());human.Add(new GroundCast());
            human.Add(new ServerPlayerAttachmentCarrier());human.Add(new ClientPlayerAttachmentCarrier());return w;
        };
        Func<World,string[],bool> start=(w,foods)=>{
            var request=new TaskRequest {Action="prepare_near_player"};
            foreach(string food in foods)request.Items.Add(new PreparationRequest {Selector=food,Preparation="chopped",Quantity=1});
            return w.Tasks.Start(w.Chef,1,new ControlCommand {Id="boards",Operation="task",Task=request})==null;
        };
        Func<World,string,bool> until=(w,operation)=>{
            for(int i=0;i<16 && !(w.Skills.Run.Running && w.Skills.Run.Operation==operation);i++)w.Tick(.25f);
            return w.Skills.Run.Running && w.Skills.Run.Operation==operation;
        };
        Action<World,GameObject,GameObject> collect=(w,counter,food)=>{
            w.ArriveAt(counter);counter.GetComponent<ServerAttachStation>().Item=null;w.Hold(food);w.Verify();
        };

        var a=kitchen();var shelf=a.Delivery();shelf.Add(new Pickup());
        shelf.GetComponent<ServerAttachStation>().Item=raw(a,"potato");a.Target.GetComponent<ServerAttachStation>().Item=raw(a,"chicken");
        Check(start(a,new[] {"potato","chicken"}),"occupied-board preparation accepted");
        Check(until(a,"work_at") && a.Navigation.Resolve(a.Skills.Run.Target)==a.Target && a.Carrier.Item==null && !a.Inputs.Contains(1),
            "food the human left on the only board is chopped in place before other food is collected");
        a.Tasks.Cancel("test_end");

        var b=kitchen();shelf=b.Delivery();shelf.Add(new Pickup());var spare=b.Delivery();var potato=raw(b,"potato");var onion=raw(b,"onion");
        shelf.GetComponent<ServerAttachStation>().Item=potato;b.Target.GetComponent<ServerAttachStation>().Item=onion;
        Check(start(b,new[] {"potato"}),"blocked-board preparation accepted");
        Check(until(b,"pick_up") && b.Navigation.Resolve(b.Skills.Run.Target)==b.Target && b.Carrier.Item==null,
            "with every board holding food no remaining unit can use, P2 lifts it off a board instead of collecting food that needs one");
        collect(b,b.Target,onion);
        Check(until(b,"place_at") && b.Navigation.Resolve(b.Skills.Run.Target)==spare && b.Carrier.Item==onion,"the lifted food is parked on a free counter");
        b.ArriveAt(spare);b.Hold(null);spare.GetComponent<ServerAttachStation>().Item=onion;b.Verify();
        Check(until(b,"pick_up") && b.Navigation.Resolve(b.Skills.Run.Target)==shelf,"collection resumes once a board is free");
        b.Tasks.Cancel("test_end");

        foreach(bool duringPlacement in new[] {false,true})
        {
            var c=kitchen();shelf=c.Delivery();shelf.Add(new Pickup());potato=raw(c,"potato");shelf.GetComponent<ServerAttachStation>().Item=potato;
            Check(start(c,new[] {"potato"}) && until(c,"pick_up"),"potato collection starts while the board is free");
            if(!duringPlacement)c.Target.GetComponent<ServerAttachStation>().Item=raw(c,"onion");
            collect(c,shelf,potato);
            if(duringPlacement)
            {
                Check(until(c,"place_at") && c.Navigation.Resolve(c.Skills.Run.Target)==c.Target,"collected food heads for the free board");
                c.Target.GetComponent<ServerAttachStation>().Item=raw(c,"onion");c.ArriveAt(c.Target);
            }
            for(int i=0;i<16 && !(c.Skills.Run.Running && c.Skills.Run.Operation=="place_at" && c.Navigation.Resolve(c.Skills.Run.Target)==shelf);i++)c.Tick(.25f);
            Check(c.Skills.Run.Running && c.Skills.Run.Operation=="place_at" && c.Navigation.Resolve(c.Skills.Run.Target)==shelf && !c.Inputs.Contains(2),
                "cargo is set down when a throw fills the board "+(duringPlacement ? "during placement" : "after collection")+", never held indefinitely");
            c.Tasks.Cancel("test_end");
        }

        var d=kitchen();shelf=d.Delivery();shelf.Add(new Pickup());
        var loose=raw(d,"potato");var placed=raw(d,"potato");shelf.GetComponent<ServerAttachStation>().Item=loose;d.Target.GetComponent<ServerAttachStation>().Item=placed;
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var catalog=(Json.Record)d.Navigation.Available(d.Chef);d.Tasks.Decorate(catalog,d.Chef);
        foreach(Json.Record row in (List<object>)catalog["destinations"]) row["routeDistance"]=(int)row["objectInstanceId"]==shelf.GetInstanceID() ? .5f : 4f;
        var inventory=(LogisticsInventory)typeof(TaskActions).GetField("inventory",flags).GetValue(d.Tasks);
        inventory.BeginObservation();inventory.Refresh(d.Chef,catalog);
        var chosen=(LogisticsItem)typeof(TaskActions).GetMethod("PreparationSource",flags).Invoke(d.Tasks,new object[] {new PreparationRequest {Selector="potato",Preparation="chopped"}});
        inventory.EndObservation();
        Check(chosen!=null && chosen.Item==placed,"equal-work food already on a board is preferred to a closer copy that would need that board");

        // A throw that lands in front of the board takes the native selection
        // from it. Waiting there cannot change that; P2 moves the loose food.
        var e=kitchen();shelf=e.Delivery();shelf.Add(new Pickup());potato=raw(e,"potato");shelf.GetComponent<ServerAttachStation>().Item=potato;
        var stray=raw(e,"chicken");stray.Add(new PhysicalAttachment());stray.Add(new ServerPhysicalAttachment {Attached=false});stray.Add(new Pickup());
        stray.transform.position=new Vector3(.5f,0,-.6f);
        Check(start(e,new[] {"potato"}) && until(e,"pick_up"),"obstruction fixture collects the potato first");
        collect(e,shelf,potato);
        Check(until(e,"place_at") && e.Navigation.Resolve(e.Skills.Run.Target)==e.Target,"collected potato heads for the board");
        var selection=e.Chef.CurrentInteractionObjects;
        selection.m_TheOriginalHandlePickup=stray;selection.m_iHandlePickup=stray.GetComponent<IPickup>();selection.m_iHandlePlacement=null;selection.m_interactable=null;
        e.Navigation.Stop("positioned_no_action");
        for(int i=0;i<16 && !(e.Skills.Run.Running && e.Skills.Run.Operation=="place_at" && e.Navigation.Resolve(e.Skills.Run.Target)==shelf);i++)e.Tick(.25f);
        Check(e.Skills.Run.Running && e.Navigation.Resolve(e.Skills.Run.Target)==shelf && e.Carrier.Item==potato && !e.Inputs.Contains(2),
            "a positioned chef whose native selection is loose food sets its cargo down instead of waiting at the board");
        e.ArriveAt(shelf);e.Hold(null);shelf.GetComponent<ServerAttachStation>().Item=potato;e.Verify();
        Check(until(e,"pick_up") && e.Navigation.Resolve(e.Skills.Run.Target)==stray,"the loose food blocking the board is collected next");
        e.Tasks.Cancel("test_end");
    }
    private static void HeldItemTasks()
    {
        Func<World> kitchen=()=>{
            var w=new World();var human=new GameObject("human");human.Add(new PlayerIDProvider {Id=0});human.transform.position=new Vector3(3,0,-1);
            human.Add(new PlayerControls {GroundCollider=w.Chef.GroundCollider});human.Add(new CapsuleCollider());human.Add(new GroundCast());
            human.Add(new ServerPlayerAttachmentCarrier());human.Add(new ClientPlayerAttachmentCarrier());return w;
        };
        Func<World,string,GameObject> raw=(w,label)=>{
            var food=w.Ingredient(label+" raw");food.Add(new ServerWorkableItem());food.Add(new WorkableItem {Next=w.Ingredient(label+" chopped")});return food;
        };
        Action<World,GameObject,GameObject> place=(w,target,item)=>{
            w.ArriveAt(target);target.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        };
        Func<World,string,bool> until=(w,operation)=>{
            for(int i=0;i<30 && !(w.Skills.Run.Running && w.Skills.Run.Operation==operation);i++)w.Tick(.25f);
            return w.Skills.Run.Running && w.Skills.Run.Operation==operation;
        };
        Action<World,string[]> start=(w,foods)=>{
            var request=new TaskRequest {Action="prepare_near_player"};
            foreach(string food in foods)request.Items.Add(new PreparationRequest {Selector=food,Preparation="chopped",Quantity=1});
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Id="held-food",Operation="task",Task=request})==null,"preparation accepts occupied hands");
        };
        foreach(string timing in new[] {"idle","walking","blocked"})
        {
            var w=kitchen();var shelf=w.Delivery();shelf.Add(new Pickup());shelf.Add(new PickupItemSpawner {m_itemPrefab=raw(w,"potato")});
            var caught=raw(w,"potato");
            if(timing=="idle")w.Hold(caught);
            start(w,new[] {"potato"});
            if(timing!="idle")
            {
                Check(until(w,"pick_up"),"preparation begins walking to an ingredient source");w.Hold(caught);
                if(timing=="blocked")w.ArriveAt(shelf);
            }
            Check(until(w,"place_at") && w.Navigation.Resolve(w.Skills.Run.Target)==w.Target && w.Carrier.Item==caught && !w.Inputs.Contains(1),
                "a matching "+timing+" catch goes directly to the board without another pickup or drop");
            place(w,w.Target,caught);
            Check(until(w,"work_at") && w.Tasks.Run.RequestId=="held-food" && w.Releases==0,"the same request chops the caught potato without releasing ownership");
            w.Tasks.Cancel("test_end");
        }
        foreach(bool alreadyChopped in new[] {false,true})
        {
            var w=kitchen();var shelf=w.Delivery();shelf.Add(new Pickup());shelf.Add(new PickupItemSpawner {m_itemPrefab=raw(w,"potato")});
            w.Delivery();
            var cooked=raw(w,"potato");var caught=alreadyChopped ? cooked.GetComponent<WorkableItem>().Next : cooked;
            w.Target.GetComponent<ServerAttachStation>().Item=raw(w,"potato");w.Hold(caught);
            // Use an open board here for raw cargo; a processed held item needs no board.
            if(!alreadyChopped)w.Target.GetComponent<ServerAttachStation>().Item=null;
            start(w,new[] {"potato"});
            if(alreadyChopped)
            {
                for(int i=0;i<15;i++)w.Tick(.25f);
                var details=(Json.Record)((Json.Record)w.Tasks.Describe())["details"];
                Check(details["handoff"]!=null && (int)((Json.Record)details["handoff"])["itemInstanceId"]==caught.GetInstanceID() && !w.Inputs.Contains(1) && !w.Inputs.Contains(2),
                    "held chopped food goes straight to handoff without chopping or fetching a substitute");
            }
            else Check(until(w,"place_at") && w.Navigation.Resolve(w.Skills.Run.Target)==w.Target,"held raw food is selected without fetching a substitute");
            w.Tasks.Cancel("test_end");
        }
        foreach(bool caughtDuringTravel in new[] {false,true})
        {
            var w=kitchen();var shelf=w.Delivery();shelf.Add(new Pickup());shelf.Add(new PickupItemSpawner {m_itemPrefab=raw(w,"potato")});
            var parking=w.Delivery();parking.transform.position=new Vector3(-2,0,0);
            var stray=w.Ingredient("onion");if(!caughtDuringTravel)w.Hold(stray);
            start(w,new[] {"potato"});
            if(caughtDuringTravel) {Check(until(w,"pick_up"),"unrelated catch fixture begins source travel");w.Hold(stray);}
            Check(until(w,"place_at") && w.Carrier.Item==stray && w.Navigation.Resolve(w.Skills.Run.Target)==parking,"unrelated cargo is parked before ingredient collection");
            place(w,parking,stray);
            Check(until(w,"pick_up") && w.Navigation.Resolve(w.Skills.Run.Target)==shelf && w.Tasks.Run.RequestId=="held-food" && w.Releases==0,
                "parking resumes the exact suspended pickup under the original request");
            Check(parking.GetComponent<ServerAttachStation>().Item==stray,"unrelated cargo survives on a counter");w.Tasks.Cancel("test_end");
        }
        {
            var w=kitchen();var chopped=w.Ingredient("SushiFish");var rawFish=new GameObject("SushiFish");rawFish.Add(new WorkableItem {Next=chopped});
            var source=w.Delivery();source.Add(new Pickup());source.Add(new PickupItemSpawner {m_itemPrefab=rawFish});
            var parking=w.Delivery();parking.transform.position=new Vector3(-2,0,0);w.Hold(chopped);
            var request=new TaskRequest {Action="prepare_near_player"};request.Items.Add(new PreparationRequest {Selector="SushiFish",Preparation="raw",Quantity=1});
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Operation="task",Task=request})==null && until(w,"place_at") && w.Navigation.Resolve(w.Skills.Run.Target)==parking,
                "a chopped order node sharing the dispenser's display name is parked for a raw request");
            place(w,parking,chopped);
            Check(until(w,"pick_up") && w.Navigation.Resolve(w.Skills.Run.Target)==source,"raw requests collect the dispenser root after parking a same-name chopped item");w.Tasks.Cancel("test_end");
        }
        {
            var w=kitchen();var shelf=w.Delivery();shelf.Add(new Pickup());shelf.Add(new PickupItemSpawner {m_itemPrefab=raw(w,"potato")});
            start(w,new[] {"potato","chicken"});Check(until(w,"pick_up"),"multi-item request starts collecting potato");
            var caught=raw(w,"chicken");w.Hold(caught);
            Check(until(w,"place_at") && w.Carrier.Item==caught && w.Navigation.Resolve(w.Skills.Run.Target)==w.Target,
                "a catch for another pending unit reschedules directly to that ingredient");w.Tasks.Cancel("test_end");
        }
        {
            var w=kitchen();var shelf=w.Delivery();shelf.Add(new Pickup());var potato=raw(w,"potato");w.Target.GetComponent<ServerAttachStation>().Item=potato;
            start(w,new[] {"potato"});Check(until(w,"work_at"),"in-place preparation starts working");w.ArriveAt(w.Target);
            var stray=w.Ingredient("onion");w.Hold(stray);
            Check(until(w,"place_at") && w.Navigation.Resolve(w.Skills.Run.Target)==shelf && w.Carrier.Item==stray,"a catch during active chopping is parked");
            place(w,shelf,stray);
            Check(until(w,"work_at") && w.Navigation.Resolve(w.Skills.Run.Target)==w.Target && w.Target.GetComponent<ServerAttachStation>().Item==potato,
                "chopping resumes the original partially worked food after clearing hands");w.Tasks.Cancel("test_end");
        }
        {
            var w=kitchen();w.Target.Add(new CookingStation());var pan=new GameObject("pan");pan.Add(new PhysicalAttachment());
            pan.Add(new ServerIngredientContainer()).Contents.Add(new AssembledDefinitionNode("rice"));pan.Add(new IngredientTransfer());
            pan.Add(new ServerCookingHandler()).m_ServerData.m_cookingState=ProcessingState.OverDoing;w.Target.GetComponent<ServerAttachStation>().Item=pan;
            var plate=new GameObject("plate");var contents=plate.Add(new ServerIngredientContainer());plate.Add(new ServerPlate {m_ingredientContainer=contents});w.Hold(plate);
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Operation="task",Task=new TaskRequest {Action="take_ready"}})==null,"ready-food task accepts a held serving plate");
            Check(until(w,"collect_contents") && w.Carrier.Item==plate && !w.Inputs.Contains(1),"an already-held compatible plate collects urgent ready food directly: "+Json.Encode(w.Tasks.Describe())+" navigation "+Json.Encode(w.Navigation.Describe()));w.Tasks.Cancel("test_end");
        }
        foreach(bool duringParking in new[] {false,true})
        {
            var w=kitchen();w.Delivery();var potato=raw(w,"potato");w.Target.GetComponent<ServerAttachStation>().Item=potato;
            start(w,new[] {"potato"});Check(until(w,"work_at"),"release-after-catch fixture begins chopping");w.ArriveAt(w.Target);
            w.Hold(w.Ingredient("onion"));w.Tick();
            if(duringParking)Check(until(w,"place_at"),"clearing hands begins navigating before the delayed native release");
            // Native use was down when the catch arrived. Releasing it can
            // launch the caught item one frame after the work interruption.
            w.Hold(null);
            Check(until(w,"work_at") && w.Target.GetComponent<ServerAttachStation>().Item==potato && w.Releases==0,
                "a caught item leaving during hands recovery resumes original work instead of parking with empty hands");w.Tasks.Cancel("test_end");
        }
        {
            var w=kitchen();var shelf=w.Delivery();var potato=raw(w,"potato");w.Target.GetComponent<ServerAttachStation>().Item=potato;
            start(w,new[] {"potato"});Check(until(w,"work_at"),"changed-board fixture starts working the original potato");w.ArriveAt(w.Target);
            var stray=w.Ingredient("onion");w.Hold(stray);Check(until(w,"place_at"),"changed-board fixture begins parking a catch");
            w.Target.GetComponent<ServerAttachStation>().Item=raw(w,"chicken");place(w,shelf,stray);
            for(int i=0;i<20;i++)w.Tick(.25f);
            Check(w.Tasks.Run.Running && !w.Skills.Run.Running,"resuming after hands recovery revalidates the board instead of chopping replacement food");w.Tasks.Cancel("test_end");
        }
        {
            var w=kitchen();var shelf=w.Delivery();shelf.Add(new Pickup());shelf.Add(new PickupItemSpawner {m_itemPrefab=raw(w,"potato")});w.Hold(w.Ingredient("onion"));
            shelf.GetComponent<ServerAttachStation>().Item=w.Ingredient("counter full");start(w,new[] {"potato"});
            for(int i=0;i<30;i++)w.Tick(.25f);
            Check(w.Tasks.Run.Running && !w.Skills.Run.Running && (string)((Json.Record)((Json.Record)w.Tasks.Describe())["details"])["waitingReason"]=="waiting_for_safe_counter",
                "without storage, hands recovery waits with cargo retained: "+Json.Encode(w.Tasks.Describe())+" navigation "+Json.Encode(w.Navigation.Describe()));
            w.Tasks.Cancel("stopped");Check(w.Releases==1 && w.Carrier.Item!=null && !w.Tasks.Run.Running,"Stop cancels suspended hands recovery once, retaining cargo");
        }
        {
            var w=kitchen();var source=w.Delivery();source.Add(new Pickup());var dirty=new GameObject("dirty plates");dirty.Add(new ServerDirtyPlateStack());dirty.Add(new ServerStack());dirty.Add(new PhysicalAttachment());source.GetComponent<ServerAttachStation>().Item=dirty;
            w.Target.Add(new ServerWashingStation());var parking=w.Delivery();parking.transform.position=new Vector3(-2,0,0);w.Hold(w.Ingredient("potato"));
            var catalog=(Json.Record)w.Navigation.Available(w.Chef);w.Tasks.Decorate(catalog,w.Chef);
            Check(((List<object>)catalog["taskOptions"]).Cast<Json.Record>().Any(row=>(string)row["name"]=="wash_dishes"),"washing stays visible with unrelated held food");
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Operation="task",Task=new TaskRequest {Action="wash_dishes"}})==null,"washing accepts unrelated held food");
            Check(until(w,"place_at") && w.Navigation.Resolve(w.Skills.Run.Target)==parking,"washing parks unrelated cargo instead of placing it into a dirty stack or sink");w.Tasks.Cancel("test_end");
        }
        {
            var w=kitchen();var sink=w.Target.Add(new ServerWashingStation {m_plateCount=1});
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Operation="task",Task=new TaskRequest {Action="wash_dishes"}})==null && until(w,"work_at"),"washing starts on dishes already in the sink");
            w.ArriveAt(w.Target);
            var dirty=new GameObject("caught dirty plates");dirty.Add(new ServerDirtyPlateStack());dirty.Add(new ServerStack());w.Hold(dirty);
            Check(until(w,"place_at") && w.Navigation.Resolve(w.Skills.Run.Target)==w.Target && w.Carrier.Item==dirty,"dirty dishes caught while washing join the current sink instead of being parked");
            w.ArriveAt(w.Target);w.Hold(null);sink.m_plateCount++;w.Verify();
            Check(until(w,"work_at") && w.Tasks.Run.Running && w.Releases==0,"washing resumes after adding caught dishes under the same lease");w.Tasks.Cancel("test_end");
        }
        {
            var w=kitchen();w.Target.Add(new ServerWashingStation());var source=w.Delivery();source.Add(new Pickup());
            var dirty=new GameObject("dirty stack");dirty.Add(new PhysicalAttachment());dirty.Add(new ServerDirtyPlateStack());dirty.Add(new ServerStack());source.GetComponent<ServerAttachStation>().Item=dirty;
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Operation="task",Task=new TaskRequest {Action="wash_dishes"}})==null && until(w,"pick_up"),"washing starts collecting a stack");
            var caught=new GameObject("caught dirty stack");caught.Add(new ServerDirtyPlateStack());caught.Add(new ServerStack());w.Hold(caught);
            Check(until(w,"place_at") && w.Navigation.Resolve(w.Skills.Run.Target)==source && w.Carrier.Item==caught,"a dirty catch during pickup is merged with the original batch");w.Tasks.Cancel("test_end");
        }
        {
            var w=kitchen();var source=w.Delivery();source.Add(new Pickup());var tool=new GameObject("extinguisher");tool.Add(new PhysicalAttachment());tool.Add(new ServerFireExtinguishSpray());source.GetComponent<ServerAttachStation>().Item=tool;
            var parking=w.Delivery();parking.transform.position=new Vector3(-2,0,0);w.Target.Add(new ServerFlammable {Burning=true});w.Hold(w.Ingredient("potato"));
            var catalog=(Json.Record)w.Navigation.Available(w.Chef);w.Tasks.Decorate(catalog,w.Chef);
            Check(((List<object>)catalog["taskOptions"]).Cast<Json.Record>().Any(row=>(string)row["name"]=="put_out_fire"),"firefighting stays available with unrelated held food");
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Operation="task",Task=new TaskRequest {Action="put_out_fire"}})==null && until(w,"place_at") && w.Navigation.Resolve(w.Skills.Run.Target)==parking,
                "firefighting parks unrelated food before fetching the extinguisher");w.Tasks.Cancel("test_end");
            var held=new GameObject("held extinguisher");held.Add(new ServerFireExtinguishSpray());w.Hold(held);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var selected=(LogisticsItem)typeof(TaskActions).GetMethod("SprayTool",flags).Invoke(w.Tasks,new object[] {"ServerFireExtinguishSpray"});
            Check(selected!=null && selected.Item==held,"a useful held extinguisher takes priority over another available tool");
        }
        {
            var w=kitchen();w.Target.Add(new CookingStation());var source=w.Delivery();source.Add(new Pickup());source.Add(new PickupItemSpawner {m_itemPrefab=raw(w,"potato")});
            var pan=new GameObject("pan");pan.Add(new PhysicalAttachment());pan.Add(new ServerIngredientContainer()).Contents.Add(new AssembledDefinitionNode("burnt rice"));pan.Add(new ServerCookingHandler()).m_ServerData.m_cookingState=ProcessingState.Ruined;w.Target.GetComponent<ServerAttachStation>().Item=pan;
            w.Hold(w.Ingredient("unrelated"));var catalog=(Json.Record)w.Navigation.Available(w.Chef);w.Tasks.Decorate(catalog,w.Chef);
            var names=((List<object>)catalog["taskOptions"]).Cast<Json.Record>().Select(row=>(string)row["name"]).ToList();
            Check(names.Contains("prepare_near_player") && names.Contains("monitor_equipment") && names.Contains("toss_burnt_food"),"idle task discovery ignores cargo that tasks can park");
            Check(w.Carrier.Item.name=="unrelated" && !w.Inputs.Contains(1),"task discovery is read-only");
        }
    }
    private static void ReconciledHandoffs()
    {
        // A pressed pass that the primitive could not verify: it reached the
        // human anyway, stayed in P2's hands, or was lost (e.g. into a river).
        foreach(string outcome in new[] {"delivered","held","lost"})
        {
            var w=new World();var destination=w.Delivery();destination.Add(new Pickup());
            var human=new GameObject("human");human.Add(new PlayerIDProvider {Id=0});human.transform.position=new Vector3(3,0,-1);
            human.Add(new PlayerControls {GroundCollider=w.Chef.GroundCollider});human.Add(new CapsuleCollider());human.Add(new GroundCast());
            human.Add(new ServerPlayerAttachmentCarrier());human.Add(new ClientPlayerAttachmentCarrier());
            w.Target.Add(new PickupItemSpawner {m_itemPrefab=w.Ingredient("tomato")});
            var request=new TaskRequest {Action="prepare_near_player"};request.Items.Add(new PreparationRequest {Selector="tomato",Preparation="raw",Quantity=1});
            Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Id="reconcile",Operation="task",Task=request})==null,"handoff reconciliation task accepted");
            int injected=0,pickups=0;bool offeredAgain=false;
            for(int i=0;i<100 && w.Tasks.Run.Running && injected<2 && !offeredAgain;i++)
            {
                w.Tick(.25f);
                offeredAgain=outcome=="held" && injected==1 && (w.Tasks.Run.Phase=="approaching" || w.Tasks.Run.Phase=="giving_to_player");
                if(w.Skills.Run.Running && w.Skills.Run.Operation=="place_at")
                {
                    if(outcome=="delivered") w.Navigation.Resolve(w.Skills.Run.Target).GetComponent<ServerAttachStation>().Item=w.Carrier.Item;
                    if(outcome!="held") w.Hold(null);
                    typeof(InteractionActions).GetField("pressed",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(w.Skills,true);
                    w.Navigation.Stop("arrived");w.Skills.Run.Finish("failed","throw_missed");injected++;
                }
                else if(w.Skills.Run.Running && w.Skills.Run.Phase=="navigating")w.ArriveAt(w.Navigation.Resolve(w.Skills.Run.Target));
                if(w.Skills.Run.Running && w.Skills.Run.Operation=="pick_up" && w.Skills.Run.Phase=="pressing" && w.Inputs.Last()==1 && w.Carrier.Item==null)
                {w.Hold(w.Ingredient("tomato"));pickups++;}
            }
            if(outcome=="delivered") Check(injected==1 && w.Tasks.Run.State=="succeeded","a failed primitive reconciles an exact item actually obtainable by the human");
            else if(outcome=="held") Check(offeredAgain && w.Tasks.Run.Running && pickups==1 && w.Carrier.Item!=null,
                "an unverified pass is never counted as delivered: cargo still in hand is offered again");
            else Check(injected==2 && w.Tasks.Run.Running && pickups==2,"an unverified pass is never counted as delivered: lost cargo is prepared again");
            w.Tasks.Cancel("test_end");
        }
    }
    private static void MixerReadiness()
    {
        var w=new World(t=>t.Add(new MixingStation()));
        var bowl=new GameObject("bowl");bowl.Add(new ServerIngredientContainer()).Contents.Add(new AssembledDefinitionNode("batter"));
        var cooking=bowl.Add(new ServerCookingHandler());cooking.m_ServerData.m_cookingState=ProcessingState.Progressing;
        var mixing=bowl.Add(new ServerMixingHandler());mixing.m_serverData.m_mixingState=ProcessingState.Completed;
        w.Target.GetComponent<ServerAttachStation>().Item=bowl;
        Check(TaskNative.ReadyAt(w.Target) && TaskNative.ReadyAt(bowl),"a mixer and its item target use mixing completion despite the unused cooking handler");
        var cat=(Json.Record)w.Navigation.Available(w.Chef);w.Tasks.Decorate(cat,w.Chef);
        Check(!((List<object>)cat["taskOptions"]).Cast<Json.Record>().Any(row=>(string)row["name"]=="take_ready"),"heat-only task excludes a ready mixer bowl");
        mixing.m_serverData.m_mixingState=ProcessingState.Ruined;
        Check(!TaskNative.ReadyAt(w.Target),"ruined mixed contents are not ready");
    }
    private static void ReadyFoodTasks()
    {
        var w=new World(t=>t.Add(new CookingStation()));w.Delivery();
        var pan=new GameObject("pan");pan.Add(new ItemPropertiesComponent {m_itemDefinition=new Definition {name="pan"}});pan.Add(new PhysicalAttachment());
        var food=pan.Add(new ServerIngredientContainer());food.Contents.Add(new AssembledDefinitionNode("rice"));pan.Add(new IngredientTransfer());var cooking=pan.Add(new ServerCookingHandler());
        w.Target.GetComponent<ServerAttachStation>().Item=pan;
        Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Id="heat",Operation="task",Task=new TaskRequest {Action="take_ready"}})==null,"take-ready is offered for accessible food before it completes");
        for(int i=0;i<5;i++)w.Tick(.25f);w.Tick(1000);
        Check(w.Tasks.Run.Running && !w.Skills.Run.Running && !w.Inputs.Contains(1),"heat task waits indefinitely without picking up uncooked food");
        w.Tasks.Cancel("stopped");
        var occupied=w.Delivery();var recipient=new GameObject("empty plate");recipient.Add(new ServerIngredientContainer());recipient.Add(new ServerPlate());occupied.GetComponent<ServerAttachStation>().Item=recipient;
        var parking=typeof(TaskActions).GetMethod("Parking",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
        var selected=(GameObject)parking.Invoke(w.Tasks,new object[] {pan,null,false,null,Single.MaxValue});
        Check(selected!=null && selected!=occupied,"parking excludes an empty plate on a counter: native placement there would transfer food rather than park the held object");
        cooking.m_ServerData.m_cookingState=ProcessingState.Completed;
        var plate=new GameObject("plate");var contents=plate.Add(new ServerIngredientContainer());plate.Add(new ServerPlate {m_ingredientContainer=contents});w.Hold(plate);
        Check(w.Start("collect_contents")==null,"compatible held plate can collect cooked pot contents");w.Arrive();
        contents.Contents.Add(new AssembledDefinitionNode("rice"));food.Contents.Clear();w.Verify();
        Check(w.Skills.Run.State=="succeeded" && w.Carrier.Item==plate && w.Target.GetComponent<ServerAttachStation>().Item==pan,"collection requires observed transfer, retains plate and original pot");
        var mixing=pan.Add(new ServerMixingHandler());mixing.m_serverData.m_mixingState=ProcessingState.Completed;
        cooking.m_ServerData.m_cookingState=ProcessingState.Progressing;
        Check(!TaskNative.Ready(pan) && !TaskNative.Ready(pan,false) && TaskNative.Ready(pan,true),"completed mixing cannot prematurely finish an active cooking stage");
        cooking.m_ServerData.m_cookingState=ProcessingState.Ruined;food.Contents.Add(new AssembledDefinitionNode("burnt"));
        Check(!TaskNative.Ready(pan),"ruined cooking overrides an earlier completed mixing stage");
        Check(w.Start("collect_contents")=="processing_not_ready","burnt contents are never treated as ready food");
    }
    private static bool TaskUntil(World w, Func<bool> done, int ticks = 120)
    {
        for (int n=0;n<ticks && !done();n++) w.Tick(.11f);
        return done();
    }
    private static GameObject Dish(World w, string recipe = "fish")
    {
        var dish=new GameObject("plated "+recipe);
        var contents=dish.Add(new ServerIngredientContainer());contents.Contents.Add(new AssembledDefinitionNode(recipe));
        dish.Add(new ServerPlate {m_ingredientContainer=contents,Composition=new AssembledDefinitionNode(recipe)});
        dish.Add(new PhysicalAttachment());dish.Add(new Pickup());return dish;
    }
    private static GameObject ServingKitchen(World w)
    {
        var flow=new GameObject("kitchen flow").Add(new ServerKitchenFlowControllerBase());
        flow.Monitor.OrdersController.Recipes.Add(new AssembledDefinitionNode("fish"));
        var window=w.Delivery();window.name="serving window";window.Add(new ServerPlateStation());
        return window;
    }
    private static void StartDishTask(World w, int repetitions = 1)
    {
        Check(w.Tasks.Start(w.Chef,1,new ControlCommand {Id="deliver",Operation="task",Task=new TaskRequest {Action="deliver_completed_dishes",Repetitions=repetitions}})==null,
            "dish delivery accepts a reachable serving window even before a dish is ready");
    }
    private static void FinishDishTask(World w, GameObject window, int expectedServes)
    {
        int serves=0;
        for (int n=0;n<200 && w.Tasks.Run.Running;n++)
        {
            w.Tick(.11f);
            if (!w.Skills.Run.Running) continue;
            var target=w.Navigation.Resolve(w.Skills.Run.Target);
            if (w.Skills.Run.Phase=="navigating") w.ArriveAt(target);
            if (w.Skills.Run.Phase!="pressing" || w.Inputs.Last()!=1) continue;
            if (w.Skills.Run.Operation=="pick_up" && w.Carrier.Item==null)
            { w.Hold(ActionObservations.Receiver(target));target.GetComponent<ServerAttachStation>().Item=null; }
            else if (w.Skills.Run.Operation=="place_at" && w.Carrier.Item!=null)
            {
                var item=w.Carrier.Item;w.Hold(null);
                if (target==window)
                {
                    var plate=item.GetComponent<ServerPlate>();Check(plate!=null,"serving always carries a plate");plate.Reserved=true;
                    window.GetComponent<ServerPlateStation>().m_data=new PlateStationMessage {m_delivered=item,m_success=true};serves++;
                }
                else target.GetComponent<ServerAttachStation>().Item=item;
            }
        }
        Check(w.Tasks.Run.State=="succeeded" && serves==expectedServes,"dish repetitions finish only after their exact native plate deliveries");
        Check(w.Releases==1,"dish delivery retains one parent ownership lease through all child actions");
    }
    private static void DishDeliveryTasks()
    {
        foreach (bool tied in new[] {false, true})
        {
            var w=new World();ServingKitchen(w);
            var orders=UnityEngine.Object.FindObjectsOfType<ServerKitchenFlowControllerBase>()[0].Monitor.OrdersController;
            orders.Recipes.Add(new AssembledDefinitionNode("prawn"));
            orders.Remaining["fish"]=20;orders.Lifetimes["fish"]=200;
            orders.Remaining["prawn"]=tied ? 20 : 10;orders.Lifetimes["prawn"]=25;
            w.Target.GetComponent<ServerAttachStation>().Item=Dish(w);
            var far=w.Delivery();far.transform.position=new Vector3(-4,0,0);far.Add(new Pickup());far.GetComponent<ServerAttachStation>().Item=Dish(w,"prawn");
            for(int n=0;n<30;n++) {w.Tick(.11f);w.Navigation.Available(w.Chef);}
            StartDishTask(w);
            Check(TaskUntil(w,()=>w.Skills.Run.Running) && w.Skills.Run.Operation=="pick_up" && w.Navigation.Resolve(w.Skills.Run.Target)==(tied ? w.Target : far),
                tied ? "equal ticket expiry uses walking distance to break the tie" : "an earlier-expiring distant ticket outranks a closer dish and a lower lifetime fraction");
            w.Tasks.Cancel("stopped");
        }
        {
            var w=new World();var window=ServingKitchen(w);
            var orders=UnityEngine.Object.FindObjectsOfType<ServerKitchenFlowControllerBase>()[0].Monitor.OrdersController;
            orders.Recipes.Add(new AssembledDefinitionNode("prawn"));orders.Remaining["fish"]=50;orders.Remaining["prawn"]=5;
            w.Hold(Dish(w));w.Target.GetComponent<ServerAttachStation>().Item=Dish(w,"prawn");StartDishTask(w);
            Check(TaskUntil(w,()=>w.Skills.Run.Running) && w.Skills.Run.Operation=="place_at" && w.Navigation.Resolve(w.Skills.Run.Target)==window,
                "an already-held ready dish retains direct-serving priority over more urgent map dishes");
            w.Tasks.Cancel("stopped");
        }
        {
            var w=new World();ServingKitchen(w);
            var orders=UnityEngine.Object.FindObjectsOfType<ServerKitchenFlowControllerBase>()[0].Monitor.OrdersController;
            orders.Recipes.Add(new AssembledDefinitionNode("prawn"));orders.Remaining["fish"]=50;orders.Remaining["prawn"]=5;
            w.Target.GetComponent<ServerAttachStation>().Item=Dish(w);
            var far=w.Delivery();far.transform.position=new Vector3(-4,0,0);far.Add(new Pickup());
            for(int n=0;n<30;n++) {w.Tick(.11f);w.Navigation.Available(w.Chef);}
            StartDishTask(w);Check(TaskUntil(w,()=>w.Skills.Run.Running),"initial dish pickup starts before a more urgent meal appears");
            far.GetComponent<ServerAttachStation>().Item=Dish(w,"prawn");
            Check(TaskUntil(w,()=>w.Skills.Run.Running && w.Navigation.Resolve(w.Skills.Run.Target)==far),
                "a newly ready earlier-expiring dish can replace an unpressed pickup without another task request");
            w.Tasks.Cancel("stopped");
        }
        {
            var w=new World();w.Hold(w.Ingredient("onion"));
            Check(w.Skills.Start(w.Chef,1,new ControlCommand {Operation="place_at",GroundDrop=true})==null,"floor placement starts under the normal interaction executor");
            Check(w.Navigation.StartPosition(w.Chef,1,w.Target,new Vector3(-2,0,-1),true)==null,"floor positioning owns a checked walking child");
            w.Skills.Run.Advance("seeking_ground_drop");int inputCount=w.Inputs.Count;w.Skills.Tick();
            Check(w.Inputs.Count==inputCount && w.Navigation.Action.State=="navigating","floor positioning does not neutralize its walking child's movement input");
            w.Skills.Cancel("stopped");Check(w.Navigation.Action.State=="idle" && w.Inputs.Last()==0,"Stop neutralizes a floor positioning walk");
        }
        {
            var w=new World();var item=w.Ingredient("onion");w.Hold(item);
            var catalog=(Json.Record)w.Navigation.Available(w.Chef);
            var target=((List<object>)catalog["destinations"]).Cast<Json.Record>().First(d=>(string)d["id"]==w.Id);
            float[] approach=(float[])target["approachPosition"];
            Check(w.Start("place_at")==null,"placement starts before its native alignment opportunity");
            w.Chef.transform.position=new Vector3(approach[0],approach[1],approach[2]);w.Navigation.Tick(w.Chef);
            w.Tick();Check(w.Skills.Run.Phase=="pressing" && w.Navigation.Action.State=="idle" && w.Inputs.Last()==1,
                "a stopped exact native placement opportunity can finish conservative navigation alignment");
            w.Target.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
            Check(w.Skills.Run.State=="succeeded","immediate native placement still verifies the original cargo");
        }
        {
            var w=new World();var window=ServingKitchen(w);StartDishTask(w,0);
            w.Tick(1000);w.Verify();
            Check(w.Tasks.Run.Running && w.Tasks.Run.Phase=="waiting_for_plated_dish" && !w.Skills.Run.Running && !w.Inputs.Contains(1),
                "idle dish monitor waits indefinitely without picking up an empty plate");
            var dish=Dish(w,"partial");w.Target.GetComponent<ServerAttachStation>().Item=dish;
            w.Verify();Check(!w.Skills.Run.Running,"nonempty partial dishes do not qualify for serving");
            var plate=dish.GetComponent<ServerPlate>();plate.Composition=new AssembledDefinitionNode("fish");plate.PlatingStep=new PlatingStepData();
            w.Verify();Check(!w.Skills.Run.Running,"a completed recipe on the wrong native plate type is excluded");
            plate.PlatingStep=null;plate.Reserved=true;w.Verify();Check(!w.Skills.Run.Running,"a plate already reserved for delivery is excluded");
            plate.Reserved=false;dish.GetComponent<ServerIngredientContainer>().Contents.Clear();w.Verify();Check(!w.Skills.Run.Running,"empty plates do not qualify even with a stale matching composition");
            w.Tasks.Cancel("stopped");Check(w.Releases==1 && w.Inputs.Last()==0,"Stop cancels an idle dish monitor with neutral input");
        }
        {
            var w=new World();var window=ServingKitchen(w);var held=Dish(w);var other=Dish(w);w.Hold(held);w.Target.GetComponent<ServerAttachStation>().Item=other;
            StartDishTask(w);
            Check(TaskUntil(w,()=>w.Skills.Run.Running) && w.Skills.Run.Operation=="place_at" && w.Navigation.Resolve(w.Skills.Run.Target)==window,
                "a ready dish already in P2's hands goes straight to serving ahead of map dishes");
            w.ArriveAt(window);w.Tick();w.Hold(null);
            window.GetComponent<ServerPlateStation>().m_data=new PlateStationMessage {m_delivered=other,m_success=true};w.Verify();
            Check(w.Skills.Run.Running && w.Tasks.Run.Running,"another chef's delivery and empty hands cannot verify P2's served plate");
            window.GetComponent<ServerPlateStation>().m_data.m_delivered=held;w.Verify();
            Check(w.Tasks.Run.State=="succeeded" && !w.Inputs.Contains(2) && w.Target.GetComponent<ServerAttachStation>().Item==other,
                "immediately removed plates verify through exact native serving evidence without another pickup");
        }
        {
            var w=new World();var window=ServingKitchen(w);var shelf=w.Delivery();shelf.transform.position=new Vector3(-2,0,0);
            var meal=Dish(w);w.Target.GetComponent<ServerAttachStation>().Item=meal;
            var cargo=w.Ingredient("onion");w.Hold(cargo);StartDishTask(w);
            Check(TaskUntil(w,()=>w.Skills.Run.Running) && w.Skills.Run.Operation=="place_at" && w.Navigation.Resolve(w.Skills.Run.Target)==shelf,
                "unrelated held cargo goes to the nearest compatible counter before dish pickup");
            FinishDishTask(w,window,1);
            Check(shelf.GetComponent<ServerAttachStation>().Item==cargo,"parked cargo remains on its counter after serving");
        }
        {
            var w=new World();var window=ServingKitchen(w);var meal=Dish(w);w.Target.GetComponent<ServerAttachStation>().Item=meal;StartDishTask(w);
            Check(TaskUntil(w,()=>w.Skills.Run.Running) && w.Skills.Run.Operation=="pick_up","a map dish starts an exact pickup");
            var caught=Dish(w);w.Hold(caught);
            Check(TaskUntil(w,()=>w.Skills.Run.Running && w.Skills.Run.Operation=="place_at") && w.Navigation.Resolve(w.Skills.Run.Target)==window,
                "catching a different ready plate while approaching a dish replans directly to serving");
            FinishDishTask(w,window,1);
            Check(w.Target.GetComponent<ServerAttachStation>().Item==meal,"catch recovery leaves the uncollected map dish alone");
        }
        {
            var w=new World();var window=ServingKitchen(w);var meal=Dish(w);w.Target.GetComponent<ServerAttachStation>().Item=meal;StartDishTask(w);
            Check(TaskUntil(w,()=>w.Skills.Run.Running),"dish pickup begins before source-change fixture");
            w.Target.GetComponent<ServerAttachStation>().Item=null;meal.activeInHierarchy=false;
            Check(TaskUntil(w,()=>!w.Skills.Run.Running && w.Tasks.Run.Phase=="waiting_for_plated_dish"),"a vanished selected dish returns to monitoring without a false repetition");
            w.Target.GetComponent<ServerAttachStation>().Item=Dish(w);FinishDishTask(w,window,1);
        }
        {
            var w=new World();var window=ServingKitchen(w);var meal=Dish(w);w.Target.GetComponent<ServerAttachStation>().Item=meal;StartDishTask(w);
            Check(TaskUntil(w,()=>w.Skills.Run.Running),"dish pickup begins before an external pickup");
            var human=new GameObject("human");human.Add(new PlayerControls());human.Add(new ServerPlayerAttachmentCarrier {Item=meal});
            w.Target.GetComponent<ServerAttachStation>().Item=null;
            Check(TaskUntil(w,()=>!w.Skills.Run.Running && w.Tasks.Run.Phase=="waiting_for_plated_dish") && !w.Inputs.Contains(1),
                "a ready dish picked up by another chef is left alone and monitoring resumes");
            w.Tasks.Cancel("stopped");
        }
        {
            var w=new World();var window=ServingKitchen(w);var meal=Dish(w);w.Hold(meal);StartDishTask(w);
            Check(TaskUntil(w,()=>w.Skills.Run.Running),"held meal starts its serving route");
            var orders=UnityEngine.Object.FindObjectsOfType<ServerKitchenFlowControllerBase>()[0].Monitor.OrdersController;
            orders.Recipes.Clear();
            Check(TaskUntil(w,()=>!w.Skills.Run.Running && w.Tasks.Run.Phase=="waiting_for_plated_dish") && w.Carrier.Item==meal && !w.Inputs.Contains(1),
                "an order expiring before the native press stops the serve and retains its plate");
            orders.Recipes.Add(new AssembledDefinitionNode("fish"));FinishDishTask(w,window,1);
        }
        {
            var w=new World();var window=ServingKitchen(w);w.Hold(Dish(w));w.Target.GetComponent<ServerAttachStation>().Item=Dish(w);StartDishTask(w,2);
            FinishDishTask(w,window,2);
            var details=(Json.Record)((Json.Record)w.Tasks.Describe())["details"];
            Check((long)details["completedRepetitions"]==2,"each dish, including the initially held one, counts as one repetition");
        }
        {
            var w=new World();var window=ServingKitchen(w);var meal=Dish(w);w.Target.GetComponent<ServerAttachStation>().Item=meal;
            var belt=w.Delivery();belt.Add(new ServerConveyorStation());var far=w.Delivery();far.transform.position=new Vector3(8,0,0);
            var cargo=w.Ingredient("onion");cargo.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});
            var attachment=cargo.Add(new ServerPhysicalAttachment {Attached=true});cargo.Add(new BoxCollider {size=Vector3.one*.2f});
            cargo.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(cargo);
            var floor=w.Colliders[0];
            Physics.Sphere=(p,r,d,length)=>d.y<-.001f && p.y-r>=0 && p.y+d.y*length-r<=0 ? new[] {new RaycastHit {collider=floor,point=new Vector3(p.x,0,p.z),normal=Vector3.up,distance=(p.y-r)/-d.y}} : new RaycastHit[0];
            StartDishTask(w);
            Check(TaskUntil(w,()=>w.Skills.Run.Running) && w.Skills.Run.Phase=="positioning_ground_drop",
                "missing nearby storage uses floor placement; conveyors, boards, windows and distant counters are excluded");
            double dropStarted=w.Skills.Run.Started;
            w.Verify();Check(w.Skills.Run.Running && w.Skills.Run.Started==dropStarted && !w.Inputs.Contains(1),
                "a temporarily intercepted floor placement retains its original child action while waiting");
            w.Chef.CurrentInteractionObjects.m_iHandlePlacement=null;w.Chef.CurrentInteractionObjects.m_TheOriginalHandlePickup=null;
            for(int n=0;n<60 && w.Skills.Run.Phase!="pressing";n++)
            { cargo.transform.position=w.Chef.transform.position+Vector3.up+w.Chef.transform.forward*.4f;w.Tick(.05f); }
            Check(w.Skills.Run.Phase=="pressing" && w.Inputs.Last()==1,"a supported floor drop uses the ordinary pickup/placement button");
            Check(Vector3.Dot(w.Chef.transform.forward,w.Target.transform.position-w.Chef.transform.position)<=0,
                "floor placement faces away from the next dish so its cargo does not block that pickup route");
            w.Hold(null);attachment.Attached=false;cargo.transform.position=w.Chef.transform.position+new Vector3(0,.1f,.4f);
            Check(TaskUntil(w,()=>w.Skills.Run.Operation=="pick_up" && w.Skills.Run.Running),"the verified floor drop resumes the suspended exact dish pickup");
            FinishDishTask(w,window,1);
            Check(!w.Inputs.Contains(2) && ActionObservations.Loose(cargo),"fallback drops cargo on the ground without charging or throwing it");
            Physics.Sphere=(p,r,d,length)=>new RaycastHit[0];
        }
    }
    private static void PauseTimers()
    {
        var w=new World();var item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Arrive();w.Verify();
        Check(w.Skills.Run.Phase=="verifying","pause fixture reaches an issued placement's verification phase");
        Time.realtimeSinceStartup+=1000;Time.frameCount+=60000;
        w.Navigation.ResumeAfterPause(1000);w.Skills.ResumeAfterPause(1000);w.Tick();
        Check(w.Skills.Run.Running,"a long pause does not consume the finite action or two-second outcome watchdog");
        w.Hold(null);w.Target.GetComponent<ServerAttachStation>().Item=item;w.Verify();
        Check(w.Skills.Run.State=="succeeded","the same issued placement can verify after resuming without another press");
    }
    private static void MovingPickups()
    {
        var w=new World();var item=w.Ingredient("tomato");w.Target.GetComponent<ServerAttachStation>().Item=item;
        Check(w.Start("pick_up")==null,"moving counter pickup starts");w.Target.transform.position+=new Vector3(.1f,0,0);w.Tick();
        Check(w.Inputs.Contains(1) && w.Navigation.Action.State=="idle","a native moving pickup opportunity does not require relative settling");
        w.Hold(item);w.Target.GetComponent<ServerAttachStation>().Item=null;w.Verify();Check(w.Skills.Run.State=="succeeded","moving counter pickup verifies the exact carried object");
        w=new World();item=w.Ingredient("tomato");w.Target.GetComponent<ServerAttachStation>().Item=item;w.Start("pick_up");w.Target.transform.yaw+=.1f;w.Tick();
        Check(w.Inputs.Contains(1),"a rotating platform pickup works even when its pivot position stays fixed");w.Skills.Cancel("stopped");
        w=new World();var prefab=w.Ingredient("tomato");w.Target.Add(new PickupItemSpawner {m_itemPrefab=prefab});
        Check(w.Start("pick_up")==null,"moving dispenser pickup starts");w.Target.transform.position+=new Vector3(.1f,0,0);w.Tick();
        Check(w.Inputs.Contains(1),"native selection allows taking from a moving dispenser");w.Hold(w.Ingredient("tomato"));w.Verify();Check(w.Skills.Run.State=="succeeded","moving dispenser verifies the originally requested prefab");
        w=new World();prefab=w.Ingredient("tomato");w.Target.Add(new PickupItemSpawner {m_itemPrefab=prefab});w.Start("pick_up");w.Target.transform.yaw+=.1f;w.Tick();
        Check(w.Inputs.Contains(1),"a rotating dispenser can be collected without a stationary approach");w.Hold(w.Ingredient("tomato"));w.Verify();Check(w.Skills.Run.State=="succeeded","rotating dispenser verifies its original ingredient");
        w=new World();prefab=w.Ingredient("tomato");var supply=w.Target.Add(new PickupItemSpawner {m_itemPrefab=prefab});w.Start("pick_up");
        supply.m_itemPrefab=w.Ingredient("onion");w.Target.transform.position+=new Vector3(.1f,0,0);w.Tick();
        Check(w.Skills.Run.State=="blocked" && !w.Inputs.Contains(1),"changing a moving dispenser's prefab cannot pick a substitute");
        w=new World();item=w.Ingredient("tomato");item.Add(new PhysicalAttachment());item.transform.parent=w.Target.transform;w.Target.Add(new ServerConveyorStation());w.Target.GetComponent<ServerAttachStation>().Item=item;
        Check(w.Start("pick_up")==null && w.Navigation.Action.Destination==w.Id,"attached conveyor cargo without a live collider uses its holder's walking stance");
        var nextHolder=w.Delivery();nextHolder.Add(new Pickup());nextHolder.GetComponent<ServerAttachStation>().Item=item;w.Target.GetComponent<ServerAttachStation>().Item=null;item.transform.parent=nextHolder.transform;w.Tick();
        Check(w.Navigation.Action.Destination==w.IdOf(nextHolder) && w.Navigation.Resolve(w.Skills.Run.Target)==item,"walking follows the current holder while the pickup request keeps exact cargo identity");w.Skills.Cancel("stopped");
        w=new World();item=w.Ingredient("tomato");item.Add(new PhysicalAttachment());item.Add(new Pickup());
        w.Colliders.Add(item.Add(new BoxCollider {center=new Vector3(0,.7f,0),size=new Vector3(.2f,.2f,.2f)}));
        w.Target.Add(new ServerConveyorStation());w.Target.GetComponent<ServerAttachStation>().Item=item;
        Check(w.Start("pick_up")==null,"conveyor pickup pins the original cargo");
        Check(w.Navigation.Resolve(w.Skills.Run.Target)==item,"conveyor pickup follows item identity instead of its first belt station");
        var receiver=w.Delivery();receiver.Add(new Pickup());receiver.GetComponent<ServerAttachStation>().Item=item;w.Target.GetComponent<ServerAttachStation>().Item=null;
        item.transform.position+=new Vector3(.1f,0,0);w.Chef.CurrentInteractionObjects.m_TheOriginalHandlePickup=receiver;w.Chef.CurrentInteractionObjects.m_iHandlePickup=receiver.GetComponent<IPickup>();w.Tick();
        receiver.GetComponent<ServerAttachStation>().Item=null;w.Chef.CurrentInteractionObjects.m_iHandlePickup=item.GetComponent<IPickup>();
        Check(ActionObservations.CanPickUpExact(w.Chef,item,item),"native incoming-cargo referral proves identity even between attach-station ownership edges");
        Check(!ActionObservations.CanPickUpExact(w.Chef,item,w.Ingredient("onion")),"incoming-cargo proof cannot authorize a different object");
        Check(w.Inputs.Contains(1),"pickup remains valid after the exact item crosses to another native holder");
        w.Hold(item);receiver.GetComponent<ServerAttachStation>().Item=null;w.Verify();Check(w.Skills.Run.State=="succeeded","conveyor handover cannot hide the exact pickup outcome");
    }
    private static void Transactions()
    {
        var w=new World();var item=w.Ingredient("tomato");w.Target.GetComponent<ServerAttachStation>().Item=item;
        Check(w.Start("pick_up")==null,"pickup starts");w.Arrive();
        Check(w.Inputs.Contains(1) && w.Skills.Run.State=="running","accepted input is not pickup success");
        w.Target.GetComponent<ServerAttachStation>().Item=null;w.Hold(item);w.Verify();
        Check(w.Skills.Run.State=="succeeded" && w.Releases==1,"exact picked-up object confirms success");
        Check(w.Inputs.Count(i=>i==1)<=2,"one pickup press is held briefly, never blindly retried");

        w=new World();item=w.Ingredient("tomato");w.Target.GetComponent<ServerAttachStation>().Item=item;
        w.Start("pick_up");w.Target.GetComponent<ServerAttachStation>().Item=w.Ingredient("onion");w.Arrive();
        Check(w.Skills.Run.State=="blocked" && !w.Inputs.Contains(1),"changed source cannot pick up a substitute");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Arrive();w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="running","empty hands alone cannot prove placement");
        w.Target.GetComponent<ServerAttachStation>().Item=item;w.Tick();
        Check(w.Skills.Run.State=="succeeded","placement confirms destination holder");

        w=new World();w.Hold(w.Ingredient("tomato"));Check(w.Start("pick_up")=="hands_full","pickup never silently parks held food");
        w=new World();w.Hold(w.Ingredient("tomato"));w.Start("place_at");w.Arrive();w.Verify();w.Tick(3);
        Check(w.Skills.Run.Reason=="outcome_not_observed","no native effect times out without another placement tap");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);
        Check(w.Skills.Start(w.Chef,1,new ControlCommand {Operation="place_at",Destination=w.Id,Indefinite=true})==null,"task placement starts with persistent recovery");
        w.Arrive();w.Verify();w.Tick(3);
        Check(w.Skills.Run.State=="blocked" && w.Skills.Run.Reason=="placement_not_applied" && w.Skills.CanRetry,"unchanged authoritative cargo and destination permit safe task replanning after a suppressed placement");
        Check(w.Inputs.Count(i=>i==1)<=2,"proven no-effect placement returns control without silently pressing twice");
        w=new World();item=w.Ingredient("tomato");var bowl=item.Add(new ServerIngredientContainer());bowl.Contents.Add(new AssembledDefinitionNode("rice"));w.Hold(item);
        w.Skills.Start(w.Chef,1,new ControlCommand {Operation="place_at",Destination=w.Id,Indefinite=true});w.Arrive();w.Verify();bowl.Contents.Clear();w.Tick(3);
        Check(w.Skills.Run.Reason=="outcome_not_observed" && !w.Skills.CanRetry,"a changed held container cannot authorize replay of a possibly applied transfer");

        w=new World(t=>{t.Add(new PlateStackBase());t.Add(new ServerStack());});
        var plate=new GameObject("last clean plate");plate.Add(new ServerPlate());w.Target.GetComponent<ServerStack>().m_stackItems.Add(plate);
        Check(w.Start("pick_up")==null,"direct clean plate stack is a valid pickup source");w.Arrive();
        w.Hold(plate);w.Target.GetComponent<ServerStack>().m_stackItems.Clear();w.Verify();
        Check(w.Skills.Run.State=="succeeded","exact top plate confirms pickup from direct stack");

        w=new World(t=>{t.Add(new PlateStackBase());t.Add(new ServerStack());});plate=new GameObject("last plate");plate.Add(new ServerPlate());
        w.Target.GetComponent<ServerStack>().m_stackItems.Add(plate);w.Start("pick_up");w.Arrive();w.Hold(plate);w.Target.Destroyed=true;w.Verify();
        Check(w.Skills.Run.State=="succeeded","destroyed direct pickup source does not hide the verified last plate in P2's hands");

        w=new World();var stackObject=new GameObject("clean plate stack");stackObject.Add(new PhysicalAttachment());var stack=stackObject.Add(new ServerStack());
        plate=new GameObject("last clean plate");plate.Add(new ServerPlate());stack.m_stackItems.Add(plate);w.Target.GetComponent<ServerAttachStation>().Item=stackObject;
        w.Start("pick_up");w.Arrive();w.Hold(plate);stackObject.Destroyed=true;w.Target.GetComponent<ServerAttachStation>().Item=null;w.Verify();
        Check(w.Skills.Run.State=="succeeded","last plate pickup survives native auto-destruction of its empty stack");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Arrive();item.Destroyed=true;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="running","destroyed item and empty target are not a successful placement despite Unity fake-null equality");
    }
    private static void IngredientsAndDisposal()
    {
        var w=new World();var pan=new GameObject("pan");var contents=pan.Add(new ServerIngredientContainer());
        w.Target.GetComponent<ServerAttachStation>().Item=pan;var item=w.Ingredient("meat");w.Hold(item);
        Check(w.Start("add_ingredient")==null,"ingredient addition accepts compatible container");w.Arrive();
        contents.Contents.Add(new AssembledDefinitionNode("onion"));w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="running","an unrelated ingredient added by human does not count");
        contents.Contents.Add(new AssembledDefinitionNode("meat"));w.Tick();
        Check(w.Skills.Run.State=="succeeded","matching contents and consumed source confirm addition");

        w=new World();pan=new GameObject("pan");contents=pan.Add(new ServerIngredientContainer());w.Target.GetComponent<ServerAttachStation>().Item=pan;
        item=w.Ingredient("meat");w.Hold(item);w.Start("add_ingredient");w.Arrive();
        contents.Contents.Add(new AssembledDefinitionNode("meat"));item.Destroyed=true;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="succeeded","native destruction of consumed ingredient still confirms addition");

        w=new World(t=>{t.Add(new WashingStation());t.Add(new ServerWashingStation());});item=new GameObject("dirty stack");w.Hold(item);
        w.Start("place_at");w.Arrive();w.Target.GetComponent<ServerWashingStation>().m_plateCount=3;item.Destroyed=true;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="succeeded","sink loading verifies count even when the dirty stack is destroyed");

        w=new World();w.Hold(new GameObject("pot"));
        Check(w.Start("add_ingredient")=="held_item_is_not_single_ingredient","container transfer stays outside first layer");
        w=new World();w.Hold(w.Ingredient("bad food"));Check(w.Start("discard")=="target_is_not_disposal","discard refuses ordinary counters");
        w=new World(t=>t.Add(new ServerRubbishBin()));item=w.Ingredient("bad food");w.Hold(item);
        Check(w.Start("place_at")=="use_discard","placement cannot accidentally dispose");
        Check(w.Start("discard")==null,"explicit disposal starts");w.Arrive();item.activeInHierarchy=false;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="succeeded","native item removal confirms disposal");
    }
    private static void WorkAndWait()
    {
        var w=new World(t=>{t.Add(new WashingStation());t.Add(new ServerWashingStation {m_plateCount=2});});
        Check(w.Start("work_at","one_plate_clean")==null,"washing starts");w.Arrive();
        Check(w.Inputs.Last()==2,"work holds use");w.Target.GetComponent<ServerWashingStation>().m_plateCount=1;w.Tick();
        Check(w.Skills.Run.State=="succeeded","one-plate scope finishes with more dishes remaining");
        w=new World(t=>{t.Add(new WashingStation());t.Add(new ServerWashingStation {m_plateCount=2});});
        w.Start("wait_until","sink_empty");w.Tick();
        Check(w.Inputs.All(i=>i==0),"wait never presses a button or starts navigation");
        w.Target.GetComponent<ServerWashingStation>().m_plateCount=0;w.Tick();
        Check(w.Skills.Run.State=="succeeded","typed wait observes current state");

        w=new World();var raw=w.Ingredient("raw");var cooked=w.Ingredient("chopped");
        var workable=raw.Add(new ServerWorkableItem());raw.Add(new WorkableItem {Next=cooked});w.Target.GetComponent<ServerAttachStation>().Item=raw;
        w.Start("work_at","work_complete");w.Arrive();
        w.Chef.GetComponent<ClientPlayerControlsImpl_Default>().Current=w.Target.GetComponent<ClientInteractable>();
        int ends=ClientMessenger.Ends;w.Skills.Cancel("stopped");
        Check(w.Skills.Run.State=="cancelled" && ClientMessenger.Ends==ends+1 && w.Chef.GetCurrentlyInteractingForTest()==null,"stop ends only the owned sticky native interaction");

        w=new World();raw=w.Ingredient("raw");cooked=w.Ingredient("chopped");raw.Add(new ServerWorkableItem());raw.Add(new WorkableItem {Next=cooked});
        w.Target.GetComponent<ServerAttachStation>().Item=raw;w.Start("work_at","work_complete");w.Arrive();
        w.Target.GetComponent<ServerAttachStation>().Item=cooked;w.Tick();
        Check(w.Skills.Run.State=="succeeded","chopping accepts verified replacement prefab composition");

        w=new World();raw=w.Ingredient("raw");cooked=w.Ingredient("chopped");raw.Add(new ServerWorkableItem());raw.Add(new WorkableItem {Next=cooked});
        w.Target.GetComponent<ServerAttachStation>().Item=raw;w.Start("work_at","work_complete");w.Arrive();
        w.Target.GetComponent<ServerAttachStation>().Item=w.Ingredient("someone else's ingredient");w.Tick();
        Check(w.Skills.Run.Reason=="work_item_changed","human replacing the work item blocks further work on unrelated food");

        w=new World();var pan=new GameObject("pan");pan.Add(new ClientCookingHandler());w.Target.GetComponent<ServerAttachStation>().Item=pan;
        w.Start("wait_until","cooked");w.Target.GetComponent<ServerAttachStation>().Item=new GameObject("replacement pot");w.Tick();
        Check(w.Skills.Run.Reason=="station_contents_changed","processing wait stays bound to its original container");

        w=new World();pan=new GameObject("pan");var process=pan.Add(new ClientCookingHandler());w.Target.GetComponent<ServerAttachStation>().Item=pan;
        w.Start("wait_until","cooked");process.m_cachedCookingState="Ruined";w.Tick();Check(w.Skills.Run.State=="running","burnt food cannot satisfy cooked wait");
        process.m_cachedCookingState="Completed";w.Tick();Check(w.Skills.Run.State=="succeeded","native Completed processing state satisfies cooked wait");

        w=new World();w.Start("wait_until","occupied",ms:100);w.Tick(.11f);
        Check(w.Skills.Run.Reason=="action_timeout" && w.Releases==1,"wait has bounded lifetime and releases ownership");
    }
    private static void ImmediateNativeWork()
    {
        Func<World> board=()=>{
            var w=new World();var raw=w.Ingredient("raw");var chopped=w.Ingredient("chopped");
            raw.Add(new ServerWorkableItem());raw.Add(new WorkableItem {Next=chopped});
            w.Target.GetComponent<ServerAttachStation>().Item=raw;return w;
        };
        var ready=board();
        Check(ready.Start("work_at","work_complete")==null && ready.Skills.Run.Phase=="working" &&
            ready.Navigation.Action.State=="idle" && ready.Inputs.Last()==2,
            "a stopped chef at the natively selected board starts work immediately without another navigation action");
        foreach(string unsafeState in new[] {"moving","distant","unselected","airborne","respawning"})
        {
            var w=board();
            if(unsafeState=="moving") w.Chef.Motion.Velocity=Vector3.right;
            if(unsafeState=="distant") w.Chef.transform.position=new Vector3(0,0,-4);
            if(unsafeState=="unselected") w.Chef.CurrentInteractionObjects.m_interactable=null;
            if(unsafeState=="airborne") w.Chef.GroundCollider=null;
            if(unsafeState=="respawning") w.Chef.m_bRespawning=true;
            w.Start("work_at","work_complete");
            Check(!w.Inputs.Contains(2),"immediate work does not bypass native arrival checks when "+unsafeState);
        }
        var refreshed=board();refreshed.Chef.CurrentInteractionObjects.m_interactable=null;
        refreshed.Start("work_at","work_complete");
        refreshed.Chef.CurrentInteractionObjects.m_interactable=refreshed.Target.GetComponent<ClientInteractable>();refreshed.Tick();
        Check(refreshed.Skills.Run.Phase=="working" && refreshed.Navigation.Action.State=="idle" && refreshed.Inputs.Last()==2,
            "selection refreshing after placement starts work on that update without waiting for redundant arrival settling");
    }
    private static void Activation()
    {
        var w=new World(t=>t.Add(new ClientTriggerToggleOnAnimator {CurrentState=true}));
        w.Start("activate",state:"on");w.Arrive();
        Check(w.Skills.Run.Reason=="already_in_desired_state" && !w.Inputs.Contains(2),"desired-state activation is idempotent");
        w=new World();Check(w.Start("activate",state:"on")=="unsupported_activation_state","unknown toggle state cannot be guessed");
        w.Start("activate",state:"trigger");w.Arrive();
        w.Target.GetComponent<ServerInteractable>().Triggered(new GameObject("human"),new Vector2());w.Verify();
        Check(w.Skills.Run.State=="running","another chef's trigger is not our success");
        w.Target.GetComponent<ServerInteractable>().Triggered(w.Chef.gameObject,new Vector2());w.Tick();
        Check(w.Skills.Run.State=="succeeded" && w.Target.GetComponent<ServerInteractable>().Triggered==null,"native trigger acknowledgement verifies activation and removes listener");
    }
    private static void BallisticsAndLifecycle()
    {
        float t;
        Check(ThrowTrajectory.FlightTime(0,2,-10,0,.02f,out t) && Math.Abs(t-.38f)<.0001f,"descending equal-height arc solved");
        Check(!ThrowTrajectory.FlightTime(1,2,-10,0,.02f,out t),"target above apex is rejected");
        Check(!ThrowTrajectory.FlightTime(0,2,0,0,.02f,out t),"unsupported zero gravity fails closed");
        Check(ThrowTrajectory.Intercept(4,0,1,0,5,0,.02f,out t) && Math.Abs(t-1)<.0001f,"moving-player horizontal intercept");
        Check(!ThrowTrajectory.Intercept(4,0,10,0,5,0,.02f,out t),"unreachable moving receiver is rejected");
        Check(Math.Abs(ThrowTrajectory.Displacement(2,-10,0,.4f,.02f)+.04f)<.0001f,"arc accounts for fixed-step gravity integration");
        foreach(float invalid in new[] {float.NaN,float.PositiveInfinity}) Check(!ThrowTrajectory.Intercept(invalid,0,0,0,5,0,.02f,out t),"invalid physics cannot create a plan");
        // Compare the closed solver to independent repeated native fixed-step
        // integration, including damping and non-integer flight durations.
        foreach(float drag in new[] {0f,.2f,2f,60f})
        {
            float velocity=3,position=0;
            for(int i=0;i<30;i++) { velocity=(velocity-9.81f*.02f)*Math.Max(0,1-drag*.02f);position+=velocity*.02f; }
            Check(Math.Abs(ThrowTrajectory.Displacement(3,-9.81f,drag,.6f,.02f)-position)<.0001f,"damped arc agrees with independent native fixed-step integration");
            float next=(velocity-9.81f*.02f)*Math.Max(0,1-drag*.02f);
            Check(Math.Abs(ThrowTrajectory.Displacement(3,-9.81f,drag,.61f,.02f)-(position+next*.01f))<.0001f,"fractional collision sample follows actual fixed-step segment");
        }
        Check(ThrowTrajectory.FlightTime(0,2,-10,.2f,.02f,out t) && Math.Abs(ThrowTrajectory.Displacement(2,-10,.2f,t,.02f))<.0001f,"inverse damped arc reaches selected height");
        Check(ThrowTrajectory.Intercept(4,0,1,0,8,.2f,.02f,out t) && Math.Abs(ThrowTrajectory.Displacement(8,0,.2f,t,.02f)-(4+t))<.0001f,"damped moving-receiver intercept reaches moving target");
        var run=new ActionRun();var command=new ControlCommand {Operation="pick_up",Destination="d1"};
        Check(run.Start(1,command,0) && !run.Start(2,command,0),"action lifecycle excludes concurrent owner");
        run.Finish("cancelled","disconnected");Check(!run.Running && run.Target=="d1","cancel retains exact attempted target receipt");
        foreach(string line in new[] {"work_at r d1 work_complete 10000","wait_until r d1 cooked 5000","activate r d1 off","throw_to r d2","throw_to_player r 1","discard r d3"})
            Check(ControlCommand.Parse(line).Id=="r","typed action command accepted");
        foreach(string line in new[] {"work_at r d1 forever 10000","wait_until r d1 cooked 0","wait_until r d1 cooked 120001","activate r d1 toggle","throw_to_player r 2","pick_up r d1 extra"})
        { bool failed=false;try {ControlCommand.Parse(line);}catch(ArgumentException){failed=true;}Check(failed,"malformed action cannot reach game"); }
    }
    private static void NativeItemGeometry()
    {
        var w=new World();var item=w.Ingredient("Cheese_Raw");
        var shape=item.Add(new BoxCollider {size=new Vector3(.73f,.3793645f,.6994182f),center=new Vector3(0,.19f,0)});
        item.transform.position=new Vector3(0,.55f,-1);
        Vector3 spread=ThrowPlanner.PoseUncertainty(Vector3.forward,1,.3f);
        Check(Math.Abs(spread.z-.3f)<.0001f && spread.x==0,"fixed-force radial range uncertainty persists at landing while lateral aim error can converge");
        spread=ThrowPlanner.PoseUncertainty(Vector3.forward,0,.3f);
        Check(spread.x>=.3f && spread.z>=.3f,"release clearance covers both horizontal directions before steering correction");
        var body=new ItemBody();
        Check(body.Prepare(w.Chef,item),"native cheese collider is supported despite its old pivot radius exceeding 0.6");
        Bounds bounds=body.BoundsAt(item.transform.position,Vector3.forward);
        Check(Math.Abs(bounds.min.y-(.55f+.19f-.3793645f*.5f))<.0001f,"clearance uses the actual collider center rather than a sphere below the ingredient pivot");
        body.Cast(item.transform.position,Vector3.forward,Vector3.forward*.1f,new RaycastHit[8],~0,.01f);
        Check(Math.Abs(Physics.LastBoxCenter.y-.74f)<.0001f && Math.Abs(Physics.LastBoxHalf.y-(.3793645f*.5f+.01f))<.0001f,"native box sweep preserves vertical thickness and offset");
        var receiver=new Bounds(new Vector3(0,.5f,0),new Vector3(1.2f,1,1.2f));
        Bounds receiving=body.Receiving(receiver,Vector3.forward);
        Check(Math.Abs(receiving.center.y-(1-.19f+.3793645f*.5f))<.0001f && receiving.size.x>.46f,"counter arrival uses the ingredient's bottom and actual width");
        item.transform.lossyScale=new Vector3(-1,2,1);body.Prepare(w.Chef,item);
        Check(Math.Abs(body.BoundsAt(Vector3.zero,Vector3.forward).size.y-.758729f)<.0001f,"mirrored and nonuniform native scales preserve positive dimensions");
        shape.size=new Vector3(float.NaN,1,1);Check(!body.Prepare(w.Chef,item),"nonfinite native collider dimensions fail closed");
        shape.size=new Vector3(0,1,1);Check(!body.Prepare(w.Chef,item),"degenerate native collider dimensions fail closed");

        item=w.Ingredient("Lettuce");item.Add(new SphereCollider {radius=.38f,center=new Vector3(0,.38f,0)});
        Check(body.Prepare(w.Chef,item) && Math.Abs(body.BoundsAt(Vector3.zero,Vector3.forward).min.y)<.0001f,"native lettuce sphere has its base at the item pivot, not 0.76 units below it");
        item=w.Ingredient("capsule");item.Add(new CapsuleCollider {radius=.2f,height=1.4f,direction=0});item.transform.lossyScale=new Vector3(2,1,3);
        Check(body.Prepare(w.Chef,item) && Math.Abs(body.BoundsAt(Vector3.zero,Vector3.forward).size.x-2.8f)<.0001f && Math.Abs(body.BoundsAt(Vector3.zero,Vector3.forward).size.z-1.2f)<.0001f,"capsule height and radius use their respective native scale axes");
        item.layer=15;item.Add(new PlacementLayerSwapper {m_defaultLayerId=14});
        Check(ItemBody.DetachedLayer(item)==14,"flight collision/wind filters use the native detached layer rather than the held layer");
        item=w.Ingredient("unsupported mesh");item.Add(new MeshCollider());Check(!body.Prepare(w.Chef,item),"unsupported collision meshes do not silently become point projectiles");
    }
    private static string FindPlan(ThrowPlanner planner,NavigationActions navigation,out ThrowPlan plan)
    {
        string error;int slices=0;
        do {error=planner.Find(navigation,out plan);} while(error==ThrowPlanner.Pending && ++slices<200);
        return error;
    }
    private static void Throws()
    {
        var w=new World(t=>t.Add(new Catcher()));var item=w.Ingredient("tomato");item.Add(new Throwable());
        var body=item.Add(new PhysicalAttachment());body.m_rigidBodyParams.m_drag=.2f;item.Add(new BoxCollider {size=Vector3.one*.2f});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        float flight;ThrowTrajectory.FlightTime(.1f,8*Mathf.Tan(15*Mathf.Deg2Rad),-9.81f,.2f,.02f,out flight);
        float distance=ThrowTrajectory.Displacement(8,0,.2f,flight,.02f);
        w.Chef.transform.position=new Vector3(0,0,-distance-.4f);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        var colliders=UnityEngine.Object.All.OfType<Collider>().ToArray();
        Physics.Nearby=(p,r)=>colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        var block=new GameObject("player-only Block");block.layer=26;block.transform.parent=w.Target.transform;
        block.Add(new BoxCollider {center=new Vector3(0,3,0),size=new Vector3(1,6,1)});
        Physics.IgnoreLayers=(a,b)=>b==26;
        var planner=new ThrowPlanner();ThrowPlan plan;
        Check(planner.Prepare(w.Chef,item,w.Target)==null && planner.From(w.Chef.transform.position,false,out plan),"production planner validates native receiving surface without including the taller player-only Block collider");
        Physics.IgnoreLayers=(a,b)=>false;block.activeInHierarchy=false;
        Check(FindPlan(planner,w.Navigation,out plan)==null && plan.WalkDistance<.1f,"inverse search prefers an already reachable valid release stance");
        var nearWall=new GameObject("nearby wall").Add(new BoxCollider());
        var nearbyBefore=Physics.Nearby;
        Physics.Nearby=(p,r)=>r>.3f ? new Collider[] {nearWall} : nearbyBefore(p,r);
        Check(!planner.From(w.Chef.transform.position,false,out plan,.22f),"a planned walking stance reserves clearance for native arrival error instead of requiring an exact point");
        Physics.Nearby=nearbyBefore;
        var catchTrigger=w.Target.Add(new BoxCollider {isTrigger=true,size=Vector3.one*2});
        Physics.Sphere=(p,r,d,length)=>new[] {new RaycastHit {collider=catchTrigger}};
        Check(planner.From(w.Chef.transform.position,false,out plan),"native receiver catch trigger is not an earlier physical obstruction on the landing arc");
        Physics.Sphere=(p,r,d,length)=>new[] {new RaycastHit {collider=colliders.First(c=>c.gameObject.name=="floor")}};
        Check(!planner.From(w.Chef.transform.position,false,out plan),"flight obstruction rejects a previously valid arc");Physics.Sphere=(p,r,d,l)=>new RaycastHit[0];
        w.Target.GetComponent<Catcher>().Allowed=false;
        Check(!planner.From(w.Chef.transform.position,false,out plan),"native catcher veto rejects incompatible receipt");w.Target.GetComponent<Catcher>().Allowed=true;
        w.Chef.CurrentInteractionObjects.m_interactable=null;
        Check(w.Start("throw_to")==null,"targeted throw starts with a planned release stance");w.Arrive();
        for(int i=0;i<10 && w.Skills.Run.Phase=="aiming";i++)w.Tick();
        Check(w.Skills.Run.Phase=="throw_hold","targeted throw first holds the native use button");
        Check(!w.Skills.CanRetry,"arming the native use button commits a throw before its eventual release");
        w.Chef.GetComponent<ServerAttachmentThrower>().Launched(new GameObject("unrelated item"));
        w.Tick(.001f);Check(w.Skills.Run.Phase=="throw_hold","another object's launch cannot release this throw");
        body.m_motion=new RigidbodyMotion {Velocity=new Vector3(1,2,3)};w.Chef.GetComponent<ServerAttachmentThrower>().Launched(item);
        var observed=(float[])((Json.Record)((Json.Record)w.Skills.Describe())["details"])["observedLaunchVelocity"];
        Check(observed[0]==1 && observed[1]==2 && observed[2]==3,"native throw callback records actual launch velocity rather than reusing a prediction");
        w.Hold(null);w.Tick();
        Check(w.Skills.Run.Phase=="flight" && !w.Skills.CanRetry,"an early native launch enters verification instead of retrying cargo that already left the chef");
        w.Target.GetComponent<ServerAttachStation>().Item=item;w.Tick();
        Check(w.Skills.Run.State=="succeeded" && w.Skills.Run.Reason=="throw_received","native holder edge confirms throw receipt");
        Check(w.Chef.GetComponent<ServerAttachmentThrower>().Launched==null,"throw observation callback is removed after completion");

        w.Target.GetComponent<ServerAttachStation>().Item=null;w.Hold(item);
        Check(w.Start("throw_to")==null,"late revalidation fixture starts a checked throw");w.Arrive();
        for(int i=0;i<10 && w.Skills.Run.Phase=="aiming";i++)w.Tick();
        Check(w.Skills.Run.Phase=="throw_hold","late revalidation fixture arms the native button");
        w.Target.GetComponent<Catcher>().Allowed=false;
        for(int i=0;i<10 && w.Skills.Run.Phase=="throw_hold";i++)w.Tick();
        Check(w.Skills.Run.Phase=="flight" && !w.Skills.CanRetry,"failed release revalidation verifies the committed throw instead of treating cleanup as a safe retry");
        w.Hold(null);w.Target.GetComponent<ServerAttachStation>().Item=item;w.Tick();
        Check(w.Skills.Run.State=="succeeded","exact observed receipt resolves a committed throw after its release proof changed");
        w.Target.GetComponent<Catcher>().Allowed=true;

        var human=new GameObject("human");var player=human.Add(new PlayerControls());human.Add(new PlayerIDProvider {Id=0});
        human.Add(new AttachmentCatcher());human.Add(new ServerPlayerAttachmentCarrier());human.transform.yaw=Mathf.PI;
        w.Target.GetComponent<ServerAttachStation>().Item=null;w.Hold(item);
        distance=ThrowTrajectory.Displacement(8,0,.2f,.6f,.02f);w.Chef.transform.position=new Vector3(0,0,-distance-.4f);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);
        player.Motion.Velocity=new Vector3(1,0,0);
        Check(planner.Prepare(w.Chef,item,human)==null && planner.From(w.Chef.transform.position,false,out plan) && plan.Direction.x>0,"player pass leads a moving human who faces the incoming throw");
        var playerBody=human.Add(new CapsuleCollider());
        Physics.Sphere=(p,r,d,l)=>Vector3.Distance(p,player.transform.position)<.65f ? new[] {new RaycastHit {collider=playerBody}} : new RaycastHit[0];
        Check(planner.From(w.Chef.transform.position,false,out plan),"receiver body inside native catching range is the receiving contact, not an intervening obstruction");
        Physics.Sphere=(p,r,d,l)=>new RaycastHit[0];
        human.transform.yaw=Mathf.PI*.5f;human.GetComponent<AttachmentCatcher>().m_catchAngleMax=140;
        Check(!planner.From(w.Chef.transform.position,false,out plan),"native forward catch scan rejects a side pass even when the attachment catch-angle setting permits it");
        human.GetComponent<AttachmentCatcher>().m_catchAngleMax=60;
        human.transform.yaw=0;Check(!planner.From(w.Chef.transform.position,false,out plan),"player facing away is outside native catch cone");
        human.GetComponent<ServerPlayerAttachmentCarrier>().Item=new GameObject("occupied hands");
        Check(planner.Prepare(w.Chef,item,human)=="player_cannot_catch","player pass respects occupied hands");
        human.GetComponent<ServerPlayerAttachmentCarrier>().Item=null;human.transform.yaw=Mathf.PI;player.Motion.Velocity=Vector3.zero;
        Check(w.Skills.Start(w.Chef,1,new ControlCommand {Operation="throw_to_player",Destination="1",Milliseconds=10000})==null,"player pass resolves actual logical player identity");
        w.Arrive();w.Tick();w.Tick();w.Tick();Check(w.Skills.Run.Phase=="flight","player pass uses ordinary use hold/release");
        item.Destroyed=true;w.Hold(null);w.Tick();
        Check(w.Skills.Run.State=="running","destroyed projectile and empty receiver hands cannot falsely confirm a catch");
        w.Tick(3);Check(w.Skills.Run.Reason=="throw_missed","missed player pass terminates with an explicit outcome");
    }
    private static void IncrementalThrowPlanning()
    {
        var w=new World(t=>t.Add(new Catcher()));var item=w.Ingredient("tomato");item.Add(new Throwable());
        item.Add(new PhysicalAttachment());item.Add(new BoxCollider {size=Vector3.one*.2f});
        item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());
        var server=w.Chef.gameObject.Add(new ServerAttachmentThrower());
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        var planner=new ThrowPlanner();ThrowPlan plan;Check(planner.Prepare(w.Chef,item,w.Target)==null,"incremental throw fixture prepares native geometry");
        bool delayed=false;
        server.Override=d=>{if(!delayed){delayed=true;System.Threading.Thread.Sleep(35);}return new Vector3(d.x*8,8*Mathf.Tan(15*Mathf.Deg2Rad),d.y*8);};
        string result=planner.Find(w.Navigation,out plan);
        Check(result==ThrowPlanner.Pending && plan==null,"a cold planning slice remains pending instead of falsely proving no reachable release position");
        for(int i=0;i<20 && result==ThrowPlanner.Pending;i++)result=planner.Find(w.Navigation,out plan);
        Check(result==null && plan!=null && plan.WalkDistance>0,"later bounded planning slices retain candidates and find a reachable release stance");
        planner.CancelPlanning();
        int invocation=0;
        server.Override=d=>{if(++invocation==2)System.Threading.Thread.Sleep(35);return new Vector3(d.x*8,8*Mathf.Tan(15*Mathf.Deg2Rad),d.y*8);};
        Check(w.Start("throw_to")==null && w.Skills.Run.Phase=="planning_throw","executor retains its lease and exposes pending planning as an active action");
        int calls=server.Calls;w.Skills.Tick();
        Check(server.Calls==calls,"a pending request cannot spend a second planning slice in the same Unity frame");
        w.Skills.Cancel("stopped");
        Check(w.Skills.Run.Reason=="stopped" && w.Carrier.Item==item && !w.Inputs.Contains(2),"stop during planning releases without launching or losing cargo");
    }
    private static void ClosePlayerPassPositions()
    {
        var w=new World();w.Chef.transform.position=new Vector3(3,0,-3);
        var item=w.Ingredient("tomato");item.Add(new Throwable());item.Add(new PhysicalAttachment());item.Add(new BoxCollider {size=Vector3.one*.2f});
        item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        var human=new GameObject("human");human.Add(new PlayerControls());human.Add(new PlayerIDProvider {Id=0});human.Add(new ServerPlayerAttachmentCarrier());
        human.Add(new AttachmentCatcher {m_catchDistance=1.8f});human.transform.yaw=Mathf.PI;
        var floor=w.Colliders[0];
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        Physics.Ray=(p,d,length,mask)=>d.y<-.9f && p.y>=0 && p.y<=length && p.z>=-3.2f ? new[] {new RaycastHit {collider=floor,point=new Vector3(p.x,0,p.z),normal=Vector3.up,distance=p.y}} : new RaycastHit[0];
        var planner=new ThrowPlanner();ThrowPlan plan;
        Check(planner.Prepare(w.Chef,item,human)==null && FindPlan(planner,w.Navigation,out plan)==null && plan.Stance.z>=-3.2f,
            "player stance search includes earlier catch-volume rings when the descending foot-height ring lies outside the kitchen");
    }
    private static void GeneratedStances()
    {
        var w=new World();
        Check(w.Navigation.StartPosition(w.Chef,1,w.Target,w.Chef.transform.position)==null,"generated release stance uses production route planner");
        var catalog=(Json.Record)w.Navigation.Available(w.Chef);
        Check(((List<object>)catalog["destinations"]).All(d=>(string)((Json.Record)d)["id"]!="throw_stance"),"internal stance never leaks into wire destination options");
        w.Navigation.Stop("stopped");
        Check(w.Navigation.Action.State=="idle","generated stance navigation is interruptible");
        Vector3 goal=w.Chef.transform.position;
        Check(w.Navigation.StartPosition(w.Chef,1,w.Target,goal,true)==null,"throws can request strict generated-point arrival without changing ordinary navigation");
        w.Chef.transform.position=goal+Vector3.forward*.19f;Time.realtimeSinceStartup+=.02f;Time.frameCount++;w.Navigation.Tick(w.Chef);
        w.Chef.transform.position=goal+Vector3.forward*.23f;Time.realtimeSinceStartup+=.4f;Time.frameCount++;w.Navigation.Tick(w.Chef);
        Check(w.Navigation.Action.State=="navigating","strict release stance cannot succeed inside the wider station-arrival hysteresis");
        w.Navigation.Stop("stopped");
    }
    private static void LogisticsJobs()
    {
        var w=new World();var destination=w.Delivery();var item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        string initialError=w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="move_item",Source=w.Id,Destination=w.IdOf(destination)});
        Check(initialError==null,"move_item resolves exact source and empty destination with empty hands: "+initialError);
        w.Arrive();w.Hold(item);w.Target.GetComponent<ServerAttachStation>().Item=null;w.Tick();
        Check(w.Logistics.Run.Phase=="delivering" && w.Skills.Run.Owner==w.Logistics.Run.Owner && w.Releases==0,"composed pickup retains the same owner into delivery without releasing bindings");
        w.ArriveAt(destination);destination.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        Check(w.Logistics.Run.State=="succeeded" && w.Releases==1,"move_item completes only after verified placement and releases once");

        w=new World();destination=w.Delivery();item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="fetch_ingredient",Selector="tomato",Destination=w.IdOf(destination)})==null,"ingredient selector resolves native raw definition");
        w.Arrive();w.Hold(item);w.Target.GetComponent<ServerAttachStation>().Item=null;w.Tick();
        destination.GetComponent<ServerAttachStation>().Item=w.Ingredient("someone else's food");w.ArriveAt(destination);w.Verify();
        Check(w.Logistics.Run.State=="blocked" && w.Carrier.Item==item,"occupied destination blocks delivery while preserving carried ingredient");

        w=new World();destination=w.Delivery();item=w.Ingredient("Tomato_Chopped");w.Target.GetComponent<ServerAttachStation>().Item=item;
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="fetch_ingredient",Selector="tomato",Destination=w.IdOf(destination)})=="no_reachable_ingredient","raw ingredient request cannot silently substitute chopped food");
        w.Hold(w.Ingredient("unrelated"));
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="move_item",Source=w.Id,Destination=w.IdOf(destination)})=="hands_full" && w.Carrier.Item.name=="unrelated","logistics never parks or drops unrelated cargo");

        w=new World();destination=w.Delivery();item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="stage_item",Source=w.Id,Destination=w.IdOf(destination)});w.Logistics.Cancel("stopped");
        Check(!w.Logistics.Run.Running && !w.Skills.Run.Running && w.Navigation.Action.State=="idle" && w.Releases==1,"stop cancels parent, child and navigation under a single release");

        w=new World();destination=w.Delivery();
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="clear_station",Source=w.Id,Destination=w.IdOf(destination)})==null && w.Logistics.Run.Reason=="station_already_empty","clearing an empty station is idempotent");
        item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="clear_station",Source=w.Id,Destination=w.IdOf(destination)});w.Arrive();w.Hold(item);w.Target.GetComponent<ServerAttachStation>().Item=null;w.Tick();
        w.ArriveAt(destination);w.Hold(null);destination.GetComponent<ServerAttachStation>().Item=item;w.Verify();
        Check(w.Logistics.Run.Reason=="station_cleared","clear_station verifies the source is freed after delivery");

        w=new World();destination=w.Delivery();var cleanStack=new GameObject("clean stack");cleanStack.Add(new CleanPlateStack());var plateStack=cleanStack.Add(new ServerStack());
        var bottom=new GameObject("original bottom plate");bottom.Add(new ServerPlate {m_ingredientContainer=bottom.Add(new ServerIngredientContainer())});
        var top=new GameObject("original top plate");top.Add(new ServerPlate {m_ingredientContainer=top.Add(new ServerIngredientContainer())});plateStack.m_stackItems.Add(bottom);plateStack.m_stackItems.Add(top);
        w.Target.GetComponent<ServerAttachStation>().Item=cleanStack;w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="clear_station",Source=w.Id,Destination=w.IdOf(destination)});
        w.Arrive();plateStack.m_stackItems.Remove(top);w.Hold(top);w.Tick();w.ArriveAt(destination);
        var arrival=new GameObject("newly arrived plate");arrival.Add(new ServerPlate());plateStack.m_stackItems.Add(arrival);
        destination.GetComponent<ServerAttachStation>().Item=top;w.Hold(null);w.Verify();
        Check(w.Logistics.Run.Reason=="station_contents_changed" && w.Skills.Run.State=="succeeded","clear_station binds its original plate batch and does not silently collect new arrivals");

        w=new World();destination=w.Delivery();var plate=new GameObject("clean plate");var contents=plate.Add(new ServerIngredientContainer());plate.Add(new ServerPlate {m_ingredientContainer=contents});w.Target.GetComponent<ServerAttachStation>().Item=plate;
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="get_plate",Destination=w.IdOf(destination)})==null,"get_plate selects observed clean empty plate");w.Logistics.Cancel("test_end");
        contents.Contents.Add(new AssembledDefinitionNode("finished dinner"));
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="get_plate",Destination=w.IdOf(destination)})=="no_reachable_clean_plate","get_plate cannot steal a completed meal");

        w=new World();destination=w.Delivery();var pan=new GameObject("utensil");pan.Add(new ItemPropertiesComponent {m_itemDefinition=new Definition {name="FryingPan"}});pan.Add(new ServerIngredientContainer());pan.Add(new PhysicalAttachment());w.Target.GetComponent<ServerAttachStation>().Item=pan;
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="get_utensil",Selector="pan",Destination=w.IdOf(destination)})==null,"utensil type comes from native item definition with deterministic aliases");w.Logistics.Cancel("test_end");
        w.Target.GetComponent<ServerAttachStation>().Item=null;w.Hold(pan);string panId=w.Navigation.RegisterItem(pan);
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="return_utensil",Source=panId})==null && w.Logistics.Run.Target==w.Id,"return_utensil remembers its observed home rather than its last delivery counter");w.Logistics.Cancel("test_end");
        pan.GetComponent<ServerIngredientContainer>().Contents.Add(new AssembledDefinitionNode("rice"));
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="return_utensil",Source=panId})=="utensil_has_contents","return_utensil does not dispose of food to empty a utensil");
        Check(ControlCommand.Parse("move_item r d1 d2").Source=="d1" && ControlCommand.Parse("get_plate r d2").Destination=="d2","logistics grammar carries source and destination separately");
        Check(ControlCommand.Parse("fetch_ingredient r sdG9tYXRv d2").Selector=="tomato","selector encoding safely supports human-readable names");
        foreach(string command in new[] {"clear_station r d1 d2 extra","move_item r d1 nope","get_utensil r s%%% d2","fetch_ingredient r sCg d2","return_utensil r d1 d2"})
        {bool failed=false;try{ControlCommand.Parse(command);}catch(ArgumentException){failed=true;}Check(failed,"malformed logistics command is rejected");}
    }
    private static void JobRecovery()
    {
        var w=new World();var destination=w.Delivery();var item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Id="fetch-once",Operation="fetch_ingredient",Selector="tomato",Destination=w.IdOf(destination)})==null,"recovery fetch accepted once");
        w.Arrive();destination.activeInHierarchy=false;w.Hold(item);w.Target.GetComponent<ServerAttachStation>().Item=null;w.Tick();
        Check(w.Logistics.Run.Running && w.Logistics.Run.Phase=="delivering" && w.Releases==0,"brief destination loss between pickup and delivery retains parent lease");
        w.Tick(.25f);destination.activeInHierarchy=true;w.Tick();
        Check(w.Skills.Run.Running && w.Skills.Run.Target==w.IdOf(destination) && w.Logistics.Run.RequestId=="fetch-once","same job resumes delivery to the original destination");
        w.ArriveAt(destination);destination.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        Check(w.Logistics.Run.State=="succeeded" && w.Releases==1,"destination restored inside grace completes exact delivery");

        w=new World();destination=w.Delivery();item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="fetch_ingredient",Selector="tomato",Destination=w.IdOf(destination)});w.Arrive();
        destination.GetComponent<BoxCollider>().enabled=false;w.Hold(item);w.Target.GetComponent<ServerAttachStation>().Item=null;w.Tick();
        Check(w.Logistics.Run.Running && w.Skills.Run.Phase=="recovering_navigation" && w.Releases==0,"delivery navigation startup failure is accepted as recoverable under the existing job");
        w.Tick(.2f);destination.GetComponent<BoxCollider>().enabled=true;w.Tick(.1f);w.ArriveAt(destination);
        destination.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        Check(w.Logistics.Run.State=="succeeded" && w.Releases==1,"route returns after pickup and delivers without another fetch request");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");
        double started=w.Skills.Run.Started,deadline=w.Skills.Run.Deadline;
        w.Navigation.Stop("destination_unreachable");w.Tick();
        Check(w.Skills.Run.Phase=="recovering_navigation" && w.Inputs.Last()==0 && w.Releases==0,"route dropout neutralizes input and waits before cancelling");
        w.Tick(.1f);
        Check(w.Skills.Run.Phase=="navigating" && w.Skills.Run.Started==started && w.Skills.Run.Deadline>=deadline,"replan retains original action deadline and snapshots");
        w.Arrive();w.Target.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="succeeded","first-layer placement also recovers transient route loss");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Target.GetComponent<BoxCollider>().enabled=false;
        w.Navigation.Stop("destination_unreachable");w.Tick();w.Tick(.45f);w.Target.GetComponent<BoxCollider>().enabled=true;w.Tick(.11f);
        Check(w.Skills.Run.Running && w.Skills.Run.Phase=="navigating","expiry performs a fresh route check even when the last periodic poll missed its restoration");
        w.Arrive();w.Target.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="succeeded","last-chance revalidation completes the original delivery without extending the overall deadline");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Target.activeInHierarchy=false;w.Tick();w.Tick(.45f);
        w.Target.activeInHierarchy=true;w.Tick(.06f);
        Check(w.Skills.Run.Running && w.Skills.Run.Phase=="navigating","a target first observed restored during indefinite recovery gets the same final route check");
        w.Skills.Cancel("test_end");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Navigation.Stop("destination_unreachable");w.Tick();w.Hold(w.Ingredient("replacement"));w.Tick();
        Check(w.Skills.Run.Reason=="held_item_changed" && w.Navigation.Action.State=="idle","navigation recovery validates the original cargo before moving again");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Target.activeInHierarchy=false;w.Navigation.Stop("destination_unavailable");w.Tick();w.Tick(.49f);
        Check(w.Skills.Run.Running && w.Releases==0,"a 490 ms interruption keeps the job alive");w.Tick(.02f);
        w.Tick(180); Check(w.Skills.Run.Running && w.Releases==0 && w.Carrier.Item==item,"persistent target loss waits beyond the old deadline with cargo retained");
        w.Target.activeInHierarchy=true;w.Tick();w.Arrive();w.Target.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();Check(w.Skills.Run.State=="succeeded","restored target completes after minutes without a new request");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Target.GetComponent<Placement>().Accepts=_=>false;w.Arrive();w.Tick(.3f);
        Check(w.Skills.Run.Running && !w.Inputs.Contains(1),"temporary native placement unavailability waits without pressing");
        w.Target.GetComponent<Placement>().Accepts=_=>true;w.Tick();w.Target.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="succeeded","native placement recovery presses once after revalidation");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Arrive();w.Tick(.16f);
        int presses=w.Inputs.Count(i=>i==1);w.Target.activeInHierarchy=false;w.Tick();w.Tick(.2f);w.Target.activeInHierarchy=true;
        w.Target.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="succeeded" && w.Inputs.Count(i=>i==1)==presses,"target loss during verification never repeats an already issued placement press");

        w=new World();destination=w.Delivery();item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="fetch_ingredient",Selector="tomato",Destination=w.IdOf(destination)});w.Arrive();
        destination.activeInHierarchy=false;w.Hold(item);w.Target.GetComponent<ServerAttachStation>().Item=null;w.Tick();w.Tick(.3f);
        destination.activeInHierarchy=true;destination.GetComponent<BoxCollider>().enabled=false;w.Tick();w.Tick(.2f);
        Check(w.Logistics.Run.Running && w.Carrier.Item==item,"changing environmental blocks retains the same job indefinitely");

        w=new World(t=>{t.Add(new WashingStation());t.Add(new ServerWashingStation {m_plateCount=2});});
        w.Start("work_at","one_plate_clean");w.Arrive();var nativeUse=w.Chef.CurrentInteractionObjects.m_interactable;
        w.Chef.CurrentInteractionObjects.m_interactable=null;w.Tick();w.Tick(.25f);
        Check(w.Skills.Run.Running && w.Inputs.Last()==0,"transient work readiness loss pauses active work with neutral input");
        w.Chef.CurrentInteractionObjects.m_interactable=nativeUse;w.Tick();
        Check(w.Inputs.Last()==2,"the same work job resumes normal use after readiness returns");
        w.Target.GetComponent<ServerWashingStation>().m_plateCount=1;w.Tick();
        Check(w.Skills.Run.State=="succeeded","resumed work still requires the original completion condition");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at");w.Target.activeInHierarchy=false;w.Navigation.Stop("destination_unavailable");w.Tick();w.Skills.Cancel("stopped");
        Check(w.Skills.Run.State=="cancelled" && w.Skills.Run.Reason=="stopped" && w.Releases==1,"explicit stop bypasses recovery grace");

        w=new World();destination=w.Delivery();item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="fetch_ingredient",Selector="tomato",Destination=w.IdOf(destination)});w.Arrive();destination.activeInHierarchy=false;w.Hold(item);w.Target.GetComponent<ServerAttachStation>().Item=null;w.Tick();w.Hold(w.Ingredient("someone else's cargo"));w.Tick();
        Check(w.Logistics.Run.Reason=="held_item_changed" && w.Releases==1,"parent recovery never resumes with replacement cargo");

        w=new World();item=w.Ingredient("tomato");w.Hold(item);w.Start("place_at",ms:100);w.Target.activeInHierarchy=false;w.Navigation.Stop("destination_unavailable");w.Tick();w.Tick(.1f);
        Check(w.Skills.Run.Running,"environmental waiting suspends the finite action watchdog");

        var grace=new ActionRecovery();Check(grace.Wait("destination_unreachable"),"route interruption starts grace");
        Time.realtimeSinceStartup+=.3f;Check(grace.Wait("planning_in_progress"),"reason changes remain in the same interruption");
        Time.realtimeSinceStartup+=.21f;Check(grace.Wait("no_safe_start") && grace.Count==1,"changing failure reasons retains an indefinite interruption");
        Check(!grace.Wait("control_lost") && !grace.Wait("station_contents_changed"),"ownership, identity and watchdog failures are terminal");
    }
    private static void PreparationSupplies()
    {
        var w=new World(); var destination=w.Delivery();
        var definition=new Definition {name="Bun",m_uID=16088};
        var prefab=new GameObject("ChoppedBun"); prefab.Add(new PreparationContainer {m_ingredientOrderNode=definition});
        w.Target.Add(new PickupItemSpawner {m_itemPrefab=prefab});
        string id,name; Check(LogisticsInventory.IngredientIdentity(prefab,out id,out name) && id=="i16088" && name=="Bun","preparation supply uses its native ingredient definition");
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="fetch_ingredient",Selector=id,Destination=w.IdOf(destination)})==null,"bun fetch starts from preparation supply");
        w.Arrive(); var bun=new GameObject("ChoppedBun(Clone)"); bun.Add(new PreparationContainer {m_ingredientOrderNode=definition});
        var contents=bun.Add(new ServerIngredientContainer()); bun.Add(new IngredientPropertiesForTest()); w.Hold(bun); w.Verify();
        Check(w.Logistics.Run.Phase=="delivering","prefab without runtime composition matches spawned preparation base with composition");
        w.ArriveAt(destination); w.Hold(null); destination.GetComponent<ServerAttachStation>().Item=bun; w.Verify();
        Check(w.Logistics.Run.State=="succeeded","bun fetch verifies delivery");
        contents.Contents.Add(new AssembledDefinitionNode("Meat"));
        Check(!LogisticsInventory.IngredientIdentity(bun,out id,out name),"filled bun cannot substitute for an empty bun");
        var plate=new GameObject("Plate");plate.Add(new ServerPlate());
        Check(!LogisticsInventory.IngredientIdentity(plate,out id,out name),"ordinary containers are not inferred to be ingredients");
    }
    private sealed class IngredientPropertiesForTest : MonoBehaviour, IOrderDefinition
    { public AssembledDefinitionNode GetOrderComposition() { return new AssembledDefinitionNode("Bun composite"); } }
    private static Json.Record JobCatalog(World w, params string[] omitted)
    {
        var catalog=(Json.Record)w.Navigation.Available(w.Chef);
        catalog["targets"]=w.Navigation.Targets().Select(w.Navigation.TargetIdentity).ToList();
        catalog["destinations"]=((List<object>)catalog["destinations"]).Where(d=>!omitted.Contains((string)((Json.Record)d)["id"])).ToList();
        w.Logistics.Decorate(catalog,w.Chef);return catalog;
    }
    private static List<Json.Record> JobRows(Json.Record catalog,string operation)
    { var options=(Json.Record)catalog["jobOptions"]; return options[operation] != null ? ((List<object>)options[operation]).Cast<Json.Record>().ToList() : new List<Json.Record>(); }
    private static void ValidJobOptions()
    {
        var w=new World();var item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        var counter=w.Delivery();var restricted=w.Delivery();restricted.GetComponent<Placement>().Accepts=cargo=>false;
        string counterId=w.IdOf(counter), restrictedId=w.IdOf(restricted);
        var catalog=JobCatalog(w);var fetch=JobRows(catalog,"fetch_ingredient");
        Check(fetch.Count==1 && ((List<string>)fetch[0]["destinationIds"]).Contains(counterId),"fetch offers a reachable empty counter before chef picks up cargo");
        Check(!((List<string>)fetch[0]["destinationIds"]).Contains(restrictedId) && !((List<string>)fetch[0]["destinationIds"]).Contains(w.Id),"native placement veto and occupied source are excluded from delivery pairs");
        Check(JobRows(catalog,"clear_station").Count==1 && (string)JobRows(catalog,"clear_station")[0]["sourceId"]==w.Id,"clear station offers occupied sources, never empty delivery counters");
        catalog=JobCatalog(w,w.Id);
        Check(JobRows(catalog,"fetch_ingredient").Count==0 && ((List<object>)catalog["ingredients"]).Count==0 && JobRows(catalog,"move_item").Count==0,"observed but uncertified source is withheld from every pickup job");
        catalog=JobCatalog(w,counterId);
        Check(JobRows(catalog,"fetch_ingredient").Count==0,"no fetch job is offered when its only compatible delivery is unreachable");
        w.Target.GetComponent<Pickup>().Allowed=false;catalog=JobCatalog(w);
        Check(JobRows(catalog,"fetch_ingredient").Count==0,"native pickup refusal hides otherwise reachable source");
        w.Target.GetComponent<Pickup>().Allowed=true;w.Target.GetComponent<ClientInteractable>().Users=1;catalog=JobCatalog(w);
        Check(JobRows(catalog,"fetch_ingredient").Count==0,"items in use are withheld");w.Target.GetComponent<ClientInteractable>().Users=0;
        var unrelated=w.Ingredient("Onion");w.Hold(unrelated);catalog=JobCatalog(w);
        Check(((List<object>)catalog["ingredients"]).Cast<Json.Record>().All(i=>(string)i["label"]!="Tomato_Raw"),"full hands cannot offer retrieval of another ingredient");
        Check(w.Carrier.Item==unrelated && w.Chef.GetComponent<ClientPlayerAttachmentCarrier>().Item==unrelated,"catalogue previews preserve both real carrier objects");
        w.Hold(null);w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="move_item",Source=w.Id,Destination=counterId});catalog=JobCatalog(w);
        Check(JobRows(catalog,"fetch_ingredient").Count==0 && ((List<object>)catalog["items"]).Count==0,"busy job publishes no stale selectable inventory");w.Logistics.Cancel("test_end");

        w=new World();var otherSource=w.Delivery();otherSource.Add(new Pickup());
        item=w.Ingredient("Tomato_Raw");var second=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;otherSource.GetComponent<ServerAttachStation>().Item=second;
        counter=w.Delivery();counter.GetComponent<Placement>().Accepts=cargo=>cargo==second;
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="fetch_ingredient",Selector="Tomato_Raw",Destination=w.IdOf(counter)})==null &&
            (string)((Json.Record)((Json.Record)w.Logistics.Describe())["details"])["sourceId"]==w.IdOf(otherSource),"executor chooses a compatible source instead of blindly taking nearest matching cargo");w.Logistics.Cancel("test_end");

        var prefab=new GameObject("raw supply");prefab.Add(new PhysicalAttachment());var station=new GameObject("native counter");station.Add(new ServerAttachStation());var clientStation=station.Add(new ClientAttachStation());string delivery;
        Check(LogisticsChecks.Delivery(w.Chef,"fetch_ingredient",prefab,station,out delivery,true)==null,"prospective prefab queries empty native holder despite missing runtime attachment interface");
        clientStation.m_allowPlacementCallbacks.Add((cargo,context)=>true);
        Check(LogisticsChecks.Delivery(w.Chef,"fetch_ingredient",prefab,station,out delivery,true)==null,"native true callback explicitly permits prospective placement");
        clientStation.m_allowPlacementCallbacks.Add((cargo,context)=>false);
        Check(LogisticsChecks.Delivery(w.Chef,"fetch_ingredient",prefab,station,out delivery,true)=="placement_unavailable","prospective supply still respects native placement vetoes");
        clientStation.m_allowPlacementCallbacks.Clear();
        Check(LogisticsChecks.Delivery(w.Chef,"fetch_ingredient",prefab,station,out delivery)=="placement_unavailable","spawned cargo must pass full native placement predicate");
    }
    private static void RawIngredientSupplies()
    {
        var w=new World(); var destination=w.Delivery();
        var chopped=w.Ingredient("Chocolate_Chopped");
        var prefab=new GameObject("Chocolate"); prefab.Add(new WorkableItem {Next=chopped});
        w.Target.Add(new PickupItemSpawner {m_itemPrefab=prefab});
        var catalog=(Json.Record)w.Navigation.Available(w.Chef); catalog["targets"]=w.Navigation.Targets().Select(w.Navigation.TargetIdentity).ToList(); w.Logistics.Decorate(catalog,w.Chef);
        var ingredients=(List<object>)catalog["ingredients"];
        Check(ingredients.Cast<Json.Record>().Any(i=>(string)i["label"]=="Chocolate"),"raw chocolate without IngredientPropertiesComponent appears in ingredient options");
        string rawId,rawName,choppedId,choppedName;
        Check(LogisticsInventory.IngredientIdentity(prefab,out rawId,out rawName) && LogisticsInventory.IngredientIdentity(chopped,out choppedId,out choppedName) && rawId!=choppedId,"raw and chopped identities stay distinct");
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="fetch_ingredient",Selector=rawId,Destination=w.IdOf(destination)})==null,"raw ingredient job starts from its dispenser");
        w.Arrive(); var spawned=new GameObject("Chocolate(Clone)"); spawned.Add(new WorkableItem {Next=chopped}); w.Hold(spawned); w.Verify();
        Check(w.Logistics.Run.Phase=="delivering","native raw output identity verifies dispenser pickup and advances delivery");
        w.ArriveAt(destination); w.Hold(null); destination.GetComponent<ServerAttachStation>().Item=spawned; w.Verify();
        Check(w.Logistics.Run.State=="succeeded","raw ingredient fetch completes after verified delivery");

        w=new World(); w.Target.Add(new PickupItemSpawner {m_itemPrefab=prefab});
        Check(w.Start("pick_up")==null,"raw supply pickup starts");w.Arrive();
        var wrong=new GameObject("Chocolate(Clone)"); wrong.Add(new WorkableItem {Next=w.Ingredient("Onion_Chopped")});w.Hold(wrong);w.Verify();
        Check(w.Skills.Run.State!="succeeded","matching display name cannot verify a different raw ingredient");
    }
    private static void InventoryObservationCost()
    {
        var w=new World(); var first=w.Ingredient("first"); var second=w.Ingredient("second");
        w.Target.GetComponent<ServerAttachStation>().Item=first;
        var station=w.Delivery(); station.GetComponent<ServerAttachStation>().Item=second;
        var other=new GameObject("other chef").Add(new PlayerControls());
        var carrier=other.gameObject.Add(new ServerPlayerAttachmentCarrier {Item=first});
        var catalog=(Json.Record)w.Navigation.Available(w.Chef);
        UnityEngine.Object.PlayerSearches=0;
        w.Logistics.Inventory.Refresh(w.Chef,catalog);
        Check(UnityEngine.Object.PlayerSearches==1,"inventory scans players once for all items in the observation pass");
        Check(w.Logistics.Inventory.Items.Any(i=>i.Item==first && i.InUse) && w.Logistics.Inventory.Items.Any(i=>i.Item==second && !i.InUse),"shared carrier observation marks only the exact held item busy");
        w.Logistics.Inventory.BeginObservation();
        w.Logistics.Inventory.Refresh(w.Chef,catalog);UnityEngine.Object.PlayerSearches=0;
        for(int n=0;n<8;n++)w.Logistics.Inventory.Refresh(w.Chef);
        Check(UnityEngine.Object.PlayerSearches==0,"task observation reuses one inventory/navigation snapshot across availability predicates");
        w.Logistics.Inventory.EndObservation();
        carrier.Item=second;
        w.Logistics.Inventory.Refresh(w.Chef,catalog);
        Check(w.Logistics.Inventory.Items.Any(i=>i.Item==first && !i.InUse) && w.Logistics.Inventory.Items.Any(i=>i.Item==second && i.InUse),"next inventory refresh observes changed ownership without a stale carrier cache");
    }
    private static void WindAndHeight()
    {
        var w=new World();var item=w.Ingredient("test");item.Add(new BoxCollider {size=Vector3.one*.2f});
        item.Add(new AttachmentWindReceiver());item.Add(new ServerAttachmentWindReceiver());
        var zone=new GameObject("wind zone");var volume=zone.Add(new WindVolume {Velocity=new Vector3(1,0,0)});var box=zone.Add(new BoxCollider {size=Vector3.one*100});
        Physics.Penetration=(a,p,q,b,r,s)=>b.bounds.Contains(p);
        var wind=new WindField();Check(wind.Prepare(w.Chef,item)==null,"native item wind receiver and layer-filtered volumes are modeled");
        var path=ThrowFlight.Simulate(Vector3.zero,Vector3.forward,new Vector3(0,0,2),Vector3.zero,0,1,.1f,wind);
        Check(Vector3.Distance(path.End,new Vector3(1,0,2))<.0001f && path.FinalVelocity.x==0,"wind adds native positional drift rather than invented continuous acceleration");
        TimeManager.Scale=.5f;wind.Prepare(w.Chef,item);path=ThrowFlight.Simulate(Vector3.zero,Vector3.forward,new Vector3(0,0,2),Vector3.zero,0,1,.1f,wind);
        Check(Math.Abs(path.End.x-.5f)<.0001f,"wind positional drift uses the native per-layer time multiplier");TimeManager.Scale=1;
        box.size=new Vector3(.9f,100,100);
        wind.Prepare(w.Chef,item);path=ThrowFlight.Simulate(Vector3.zero,Vector3.right,new Vector3(2,0,0),Vector3.zero,0,1,.1f,wind);
        Check(Math.Abs(path.End.x-3)<.0001f && Math.Abs(path.FinalVelocity.x-3)<.0001f,"exiting a wind volume adds that source's velocity exactly once");
        box.size=Vector3.one*100;volume.Velocity=new Vector3(0,1,0);wind.Prepare(w.Chef,item);
        path=ThrowFlight.Simulate(new Vector3(0,2,0),Vector3.forward,new Vector3(0,2,2),Physics.gravity,.2f,.6f,.02f,wind);
        float calm=2+ThrowTrajectory.Displacement(2,-9.81f,.2f,.6f,.02f);
        Check(Math.Abs(path.End.y-calm-.6f)<.0001f && path.PeakHeight>2,"vertical wind and release height participate in the same damped trajectory");
        typeof(WindVolume).GetField("m_windFilter",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(volume,(LayerMask)0);
        wind.Prepare(w.Chef,item);path=ThrowFlight.Simulate(Vector3.zero,Vector3.forward,Vector3.forward,Vector3.zero,0,1,.1f,wind);
        Check(Math.Abs(path.End.y)<.0001f,"wind layer filtering is applied to the thrown item");

        var scratch=new FlightPath();
        foreach(float duration in new[] {2f,.17f,1f})
        {
            typeof(WindVolume).GetField("m_windFilter",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(volume,(LayerMask)(duration>1 ? ~0 : 0));
            wind.Prepare(w.Chef,item);
            var expected=ThrowFlight.Simulate(Vector3.up,Vector3.forward,new Vector3(1,2,3),Physics.gravity,.2f,duration,.02f,wind);
            var actual=ThrowFlight.Simulate(Vector3.up,Vector3.forward,new Vector3(1,2,3),Physics.gravity,.2f,duration,.02f,wind,scratch);
            Check(System.Object.ReferenceEquals(actual,scratch) && actual.Points.SequenceEqual(expected.Points) && actual.Times.SequenceEqual(expected.Times) && actual.Velocities.SequenceEqual(expected.Velocities),"reused flight buffer retains every step and removes the previous trajectory tail");
            Check(actual.End.Equals(expected.End) && actual.FinalVelocity.Equals(expected.FinalVelocity) && actual.WindDisplacement.Equals(expected.WindDisplacement) && actual.PeakHeight==expected.PeakHeight,"reused flight summaries match fresh trajectories");
        }
        // No wind: worker seed endpoints must equal the production integrator,
        // including fractional final steps and damping at each full physics step.
        var calmItem=w.Ingredient("calm"); var calmField=new WindField(); calmField.Prepare(w.Chef,calmItem);
        foreach(float duration in new[] {.1f,.735f,2f,3.99f})
        {
            var expected=ThrowFlight.Simulate(new Vector3(1,2,3),Vector3.forward,new Vector3(5,4,-2),Physics.gravity,.2f,duration,.02f,calmField);
            var ends=FlightBatch.Ends(new[] {new[] {1f,2f,3f,5f,4f,-2f,Physics.gravity.x,Physics.gravity.y,Physics.gravity.z,.2f,duration,.02f}});
            Check(expected.End.Equals(new Vector3(ends[0],ends[1],ends[2])),"worker calm seeds preserve every production fixed step and fractional endpoint");
        }
        long allocationStart=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<100;i++) ThrowFlight.Simulate(Vector3.zero,Vector3.forward,new Vector3(0,2,3),Physics.gravity,.2f,1,.02f,wind);
        long freshBytes=GC.GetAllocatedBytesForCurrentThread()-allocationStart;
        allocationStart=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<100;i++) ThrowFlight.Simulate(Vector3.zero,Vector3.forward,new Vector3(0,2,3),Physics.gravity,.2f,1,.02f,wind,scratch);
        long reusedBytes=GC.GetAllocatedBytesForCurrentThread()-allocationStart;
        Check(reusedBytes<freshBytes/10,"warm flight scratch removes trajectory-list allocation pressure");
        Console.WriteLine("100 flight simulations allocated "+freshBytes+" -> "+reusedBytes+" bytes");

        w=new World(t=>t.Add(new Catcher()));item=w.Ingredient("tomato");item.Add(new Throwable());item.Add(new PhysicalAttachment());item.Add(new BoxCollider {size=Vector3.one*.2f});
        w.Chef.gameObject.Add(new AttachmentThrower {m_throwForce=99,m_throwInclination=1});w.Chef.gameObject.Add(new ClientAttachmentThrower());
        var server=w.Chef.gameObject.Add(new ServerAttachmentThrower {Override=d=>new Vector3(d.x*5,3,d.y*5)});
        float time;ThrowTrajectory.FlightTime((float)Math.Sqrt(.03),3,-9.81f,0,.02f,out time);
        w.Target.transform.position=new Vector3(0,2,0);w.Chef.transform.position=new Vector3(0,2,-5*time-.4f);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        var planner=new ThrowPlanner();ThrowPlan plan;
        Check(planner.Prepare(w.Chef,item,w.Target)==null && planner.From(w.Chef.transform.position,false,out plan),"throw solver uses elevated release and target geometry");
        planner.From(w.Chef.transform.position,false,out plan);
        Check(Math.Abs(plan.InitialVelocity.z-5)<.0001f && plan.InitialVelocity.y==3 && server.Calls>0,"launch velocity comes from native calculator even when public launch settings disagree");
        Check(planner.AimIterations < 8,"calm converged aiming avoids redundant shooting iterations");
        w.Target.GetComponent<Catcher>().Allowed=false;
        Check(!planner.From(w.Chef.transform.position,false,out plan),"converged aiming still applies the native catch veto");
        w.Target.GetComponent<Catcher>().Allowed=true;
        item.Add(new AttachmentWindReceiver());item.Add(new ServerAttachmentWindReceiver());
        var crosswind=new GameObject("crosswind");var crossVolume=crosswind.Add(new WindVolume {Velocity=new Vector3(1,0,0)});crosswind.Add(new BoxCollider {size=Vector3.one*100});
        Physics.Penetration=(a,p,q,b,r,s)=>b.bounds.Contains(p);planner.Prepare(w.Chef,item,w.Target);
        Check(planner.From(w.Chef.transform.position,false,out plan) && plan.Direction.x<0 && plan.WindDisplacement.x>0,"production targeting compensates crosswind while retaining native launch speed");
        crossVolume.Velocity=new Vector3(0,1,0);
        // A higher source floor belongs to the same production walking graph.
        var elevatedFloor=w.Colliders[0];elevatedFloor.transform.position=new Vector3(0,2,0);
        Physics.Ray=(p,d,length,mask)=>d.y<-.9f && p.y>=2 && p.y-2<=length ? new[] {new RaycastHit {collider=elevatedFloor,point=new Vector3(p.x,2,p.z),normal=Vector3.up,distance=p.y-2}} : new RaycastHit[0];
        planner.Prepare(w.Chef,item,w.Target);
        Check(FindPlan(planner,w.Navigation,out plan)==null && plan.Arrival.y>3 && plan.WindDisplacement.y>0,"inverse release search adjusts flight time and positions for an updraft and elevated receiving surface");
        crossVolume.enabled=false;
        w.Chef.transform.position=new Vector3(0,0,w.Chef.transform.position.z);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);planner.Prepare(w.Chef,item,w.Target);
        Check(!planner.From(w.Chef.transform.position,false,out plan),"unreachable target height is rejected instead of projecting a flat throw");
    }
    private static void HeightOptions()
    {
        var w=new World(t=>t.Add(new Catcher()));var item=w.Ingredient("tomato");item.Add(new Throwable());
        item.Add(new PhysicalAttachment()).m_rigidBodyParams.m_drag=.2f;
        item.Add(new BoxCollider {size=Vector3.one*.2f});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        float flight;ThrowTrajectory.FlightTime(.1f,8*Mathf.Tan(15*Mathf.Deg2Rad),-9.81f,.2f,.02f,out flight);
        float distance=ThrowTrajectory.Displacement(8,0,.2f,flight,.02f);
        w.Chef.transform.position=new Vector3(0,0,-distance-.4f);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        w.Navigation.Available(w.Chef);
        var options=new ThrowOptions(w.Navigation);object info;
        var second=new GameObject("second counter");second.Add(new Catcher());second.Add(new ServerAttachStation());second.Add(new BoxCollider {size=Vector3.one});
        item.Add(new AttachmentWindReceiver());item.Add(new ServerAttachmentWindReceiver());
        int scans=UnityEngine.Object.WindSearches;
        options.Begin(w.Chef);options.Check(w.Target,out info);options.Check(second,out info);
        Check(UnityEngine.Object.WindSearches==scans+1,"one synchronous catalogue shares wind discovery across receiver planners");
        options.EndObservation();options.Begin(w.Chef);options.Reset();options.Begin(w.Chef);options.Check(w.Target,out info);
        Check(UnityEngine.Object.WindSearches==scans+2,"a new catalogue discovers wind volumes afresh instead of carrying a stale frame cache");
        options.EndObservation();second.transform.position+=Vector3.right;options.Check(second,out info);
        Check(UnityEngine.Object.WindSearches==scans+3,"planning outside a catalogue uses fresh native discovery");
        options.Reset();options.Begin(w.Chef);
        Check(!options.Check(w.Target,out info) && !options.End(),"unproven throw choices are withheld while preview is pending");
        for(int i=0;i<300 && !options.Check(w.Target,out info);i++) options.Tick(w.Chef);
        Check(options.Check(w.Target,out info),"native planner proof enables a reachable counter option");
        int inputs=w.Inputs.Count,releases=w.Releases;Vector3 original=w.Chef.transform.position;
        Check(w.Carrier.Item==item && w.Navigation.Action.State=="idle" && inputs==0 && releases==0 && Vector3.Distance(w.Chef.transform.position,original)==0,"option planning does not acquire input, navigate, or mutate cargo");
        w.Target.transform.position=Vector3.up*2;
        Check(!options.Check(w.Target,out info),"raising a previously feasible receiver immediately removes its old option");
        w.Target.activeInHierarchy=false;options.Tick(w.Chef);options.Check(w.Target,out info);
        Check((string)((Json.Record)info)["reason"]=="target_unavailable","target disappearing during preview cancels pending work without touching its geometry");
        w.Target.activeInHierarchy=true;options.Reset();options.Begin(w.Chef);options.Check(w.Target,out info);
        for(int i=0;i<300 && !options.End();i++) options.Tick(w.Chef);
        Check(!options.Check(w.Target,out info) && (string)((Json.Record)info)["state"]=="unavailable","receiver above native apex without an elevated walking stance remains unavailable");
        w.Target.transform.position=Vector3.down*2;
        Check(!options.Check(w.Target,out info),"lowered target is replanned instead of reusing an old flat arc");
        for(int i=0;i<300 && !options.Check(w.Target,out info);i++) options.Tick(w.Chef);
        Check(options.Check(w.Target,out info),"lower receiving surfaces can be offered with their longer native arc");
        var down=(Json.Record)((Json.Record)info)["plan"];
        Check((float)down["releaseHeight"]>(float)down["arrivalHeight"]+1 && (float)down["flightSeconds"]>flight,"downward preview accounts for vertical separation and increased flight time");
        var occupied=new GameObject("incompatible occupant");w.Target.GetComponent<ServerAttachStation>().Item=occupied;
        Check(!options.Check(w.Target,out info),"receiver contents invalidate a positive option without waiting for cache expiry");
        w.Hold(null);options.Begin(w.Chef);
        Check(!options.Check(w.Target,out info) && (string)((Json.Record)info)["reason"]=="hands_empty","cargo removal invalidates all throw proofs");

        w.Hold(item);var human=new GameObject("human");human.Add(new PlayerControls());human.Add(new PlayerIDProvider {Id=0});
        human.Add(new ServerPlayerAttachmentCarrier());human.Add(new AttachmentCatcher {m_catchDistance=1.8f});human.transform.yaw=Mathf.PI;
        w.Chef.transform.position=new Vector3(0,2,-8);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);
        var planner=new ThrowPlanner();ThrowPlan pass;
        Check(planner.Prepare(w.Chef,item,human)==null && planner.From(w.Chef.transform.position,false,out pass),"player pass from a higher release reaches the lower native catch volume");
        planner.From(w.Chef.transform.position,false,out pass);
        Check(pass.Origin.y-pass.Arrival.y>1 && Vector3.Distance(pass.Arrival,human.transform.position)<1.8f,"downward pass accounts for three dimensional catch distance and earlier interception");
        human.transform.position=Vector3.up*6;w.Chef.transform.position=new Vector3(0,0,-4);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);
        Check(planner.Prepare(w.Chef,item,human)==null && !planner.From(w.Chef.transform.position,false,out pass),"player above the native arc plus catch radius cannot be targeted as a flat pass");
        options.Begin(w.Chef);options.Check(human,out info);
        for(int i=0;i<300 && !options.End();i++)options.Tick(w.Chef);
        Check(!options.Check(human,out info) && (string)((Json.Record)info)["state"]=="unavailable","player above all reachable release arcs is withheld from pass options");

        w=new World(t=>t.Add(new Catcher()));item=w.Ingredient("tomato");item.Add(new Throwable());
        item.Add(new PhysicalAttachment()).m_rigidBodyParams.m_drag=.2f;item.Add(new BoxCollider {size=Vector3.one*.2f});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        w.Chef.transform.position=new Vector3(0,0,-5);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        w.Target.transform.position=new Vector3(0,2,4.5f);var ramp=w.Colliders[0];ramp.gameObject.Add(new Steppable());
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        Physics.Ray=(p,d,length,mask)=>{
            float height=p.z < -3 ? 0 : p.z < -1.4f ? (float)Math.Floor((p.z+3)/.4f+1)*.4f : 2;
            return d.y<-.9f && p.y>=height && p.y-height<=length ? new[] {new RaycastHit {collider=ramp,point=new Vector3(p.x,height,p.z),normal=Vector3.up,distance=p.y-height}} : new RaycastHit[0];};
        var elevatedNavigation=new NavigationActions((d,ms)=>{},()=>{});
        for(int i=0;i<10;i++)elevatedNavigation.Available(w.Chef);planner=new ThrowPlanner();
        Check(planner.Prepare(w.Chef,item,w.Target)==null && !planner.From(w.Chef.transform.position,false,out pass),"raised receiver cannot be reached from the lower current pose");
        string found=FindPlan(planner,elevatedNavigation,out pass);
        Check(found==null && pass.Stance.y>1.9f && pass.WalkDistance>1,"planner climbs the reachable walking graph before throwing to a raised receiver");
        options=new ThrowOptions(elevatedNavigation);options.Begin(w.Chef);
        for(int i=0;i<300 && !options.Check(w.Target,out info);i++)options.Tick(w.Chef);
        Check(options.Check(w.Target,out info),"raised receiver remains an option when a higher certified release stance is reachable");
    }
    private static void CookerThrows()
    {
        var w=new World();var pot=new GameObject("pot");pot.transform.position=new Vector3(0,.6f,0);
        var catcher=pot.Add(new ServerIngredientCatcher());var proxy=pot.Add(new ServerAttachmentCatchingProxy());
        pot.Add(new ServerIngredientContainer());
        pot.Add(new BoxCollider {center=new Vector3(0,.25f,0),size=new Vector3(.7f,.5f,.7f)});
        var trigger=pot.Add(new SphereCollider {center=new Vector3(0,.25f,0),radius=.7f,isTrigger=true,
            testBounds=new Bounds(new Vector3(0,.85f,0),Vector3.one*1.4f)});
        w.Target.GetComponent<ServerAttachStation>().Item=pot;
        var item=w.Ingredient("rice");item.Add(new Throwable());item.Add(new PhysicalAttachment());
        item.Add(new BoxCollider {center=new Vector3(0,.14f,0),size=new Vector3(.5f,.26f,.5f)});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        float flight;ThrowTrajectory.FlightTime(.71f-.55f,8*Mathf.Tan(15*Mathf.Deg2Rad),-9.81f,0,.02f,out flight);
        w.Chef.transform.position=new Vector3(0,0,-8*flight-.4f);item.transform.position=w.Chef.transform.position+new Vector3(0,.55f,.4f);w.Hold(item);
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        int queries=0;
        Physics.Penetration=(a,p,q,b,r,s)=>{
            queries++;var sphere=b as SphereCollider;if(sphere==null)return false;
            var body=new Bounds(p+new Vector3(0,.14f,0),new Vector3(.5f,.26f,.5f));
            Vector3 center=r+sphere.center;float depth=sphere.radius-Vector3.Distance(center,body.ClosestPoint(center));
            Physics.PenetrationDistance=depth;return depth>0;
        };
        var planner=new ThrowPlanner();ThrowPlan plan=null;
        Check(InteractionActions.Offered(w.Chef,w.Target).Contains("throw_to"),"occupied cooker offers its pot's native ingredient catcher");
        string prepared=planner.Prepare(w.Chef,item,w.Target);bool feasible=prepared==null && planner.From(w.Chef.transform.position,false,out plan);
        Check(feasible,"rice can enter the pot catch trigger even when the solid top exceeds its apex");
        Check(plan.PeakHeight<1.09f && plan.Arrival.y<1.09f && queries>0,"pot proof uses native overlap below the unreachable solid top");
        Check(planner.Prepare(w.Chef,item,pot)==null && planner.From(w.Chef.transform.position,false,out plan),"direct pot targets share the cooker catch proof");
        var barrier=new GameObject("unrelated wall").Add(new BoxCollider());
        Physics.Sphere=(p,r,d,l)=>new[] {new RaycastHit {collider=barrier}};
        Check(!planner.From(w.Chef.transform.position,false,out plan),"pot catches still require an unobstructed swept flight");
        Physics.Sphere=(p,r,d,l)=>new RaycastHit[0];
        w.Navigation.Available(w.Chef);var options=new ThrowOptions(w.Navigation);object preview;options.Begin(w.Chef);
        for(int i=0;i<300 && !options.Check(w.Target,out preview);i++)options.Tick(w.Chef);
        Check(options.Check(w.Target,out preview),"feasible pot catch appears in the read-only harness preview");
        catcher.Allowed=false;
        Check(!planner.From(w.Chef.transform.position,false,out plan),"incompatible or full pot still obeys the native catch veto");catcher.Allowed=true;
        catcher.Allowed=false;Check(!options.Check(w.Target,out preview),"pot filling invalidates a positive preview immediately even with the same pot identity");catcher.Allowed=true;
        trigger.enabled=false;
        Check(!planner.From(w.Chef.transform.position,false,out plan),"disabled catch trigger cannot enable a pot throw");trigger.enabled=true;
        var child=new GameObject("unrelated child trigger");child.transform.parent=pot.transform;child.transform.position=pot.transform.position;
        child.Add(new SphereCollider {center=trigger.center,radius=2,isTrigger=true,testBounds=new Bounds(new Vector3(0,.85f,0),Vector3.one*4)});
        trigger.enabled=false;
        Check(planner.Prepare(w.Chef,item,w.Target)==null && !planner.From(w.Chef.transform.position,false,out plan),"unrelated child triggers cannot replace the receiver's catch volume");trigger.enabled=true;
        trigger.gameObject.layer=18;Physics.IgnoreLayers=(a,b)=>a==18 || b==18;
        Check(planner.Prepare(w.Chef,item,w.Target)=="unsupported_receiver_geometry","noncolliding receiver and catch trigger cannot enable a pot throw");
        Physics.IgnoreLayers=(a,b)=>false;trigger.gameObject.layer=0;
        proxy.enabled=false;Check(planner.Prepare(w.Chef,item,w.Target)=="target_cannot_catch","inactive native catching proxy is rejected");proxy.enabled=true;
        w.Target.GetComponent<ServerAttachStation>().Item=null;w.Target.Add(new Catcher());int before=queries;
        Check(planner.Prepare(w.Chef,item,w.Target)==null && !planner.From(w.Chef.transform.position,false,out plan),"ordinary counter retains its physical top height guard");
        Check(queries==before,"ordinary counters add no trigger overlap queries");
        Physics.Penetration=(a,p,q,b,r,s)=>false;Physics.PenetrationDistance=.01f;
    }
    private static void NearbyHandoffs()
    {
        var w=new World();var item=w.Ingredient("tomato");item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});item.Add(new BoxCollider {size=Vector3.one*.2f});w.Hold(item);
        var human=new GameObject("human");var player=human.Add(new PlayerControls());human.Add(new PlayerIDProvider {Id=0});human.Add(new CapsuleCollider());human.Add(new ServerPlayerAttachmentCarrier {Item=new GameObject("already holding food")});human.transform.position=new Vector3(.5f,0,0);
        var options=new ThrowOptions(w.Navigation);object preview;options.Begin(w.Chef);
        Check(options.Check(human,out preview),"native nearby counter handoff remains an option for nonthrowable cargo and occupied receiver hands");
        Check(w.Skills.Start(w.Chef,1,new ControlCommand {Operation="throw_to_player",Destination="1"})==null && w.Skills.Run.Phase=="drop_press","nearby handoff prefers a usable counter even when the player cannot catch");
        w.Tick();w.Target.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="succeeded" && w.Skills.Run.Reason=="item_handed_off_nearby" && !w.Inputs.Contains(2),"nearby counter delivery uses pickup placement, not a charged throw");

        w=new World();item=w.Ingredient("tomato");var physical=item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});var attachment=item.Add(new ServerPhysicalAttachment {Attached=true});item.Add(new BoxCollider {size=Vector3.one*.2f});item.transform.position=new Vector3(0,1,-.6f);w.Hold(item);
        human=new GameObject("human");player=human.Add(new PlayerControls());human.Add(new PlayerIDProvider {Id=0});human.Add(new CapsuleCollider());human.Add(new GroundCast());human.Add(new ServerPlayerAttachmentCarrier());human.transform.position=new Vector3(0,0,-.2f);
        w.Chef.CurrentInteractionObjects.m_iHandlePlacement=null;w.Chef.CurrentInteractionObjects.m_TheOriginalHandlePickup=null;
        var floor=w.Colliders[0];
        Physics.Sphere=(p,r,d,length)=>d.y<-.001f && p.y-r>=0 && p.y+d.y*length-r<=0 ? new[] {new RaycastHit {collider=floor,point=new Vector3(p.x,0,p.z),normal=Vector3.up,distance=(p.y-r)/-d.y}} : new RaycastHit[0];
        var drops=new DropHandoff();DropPlan drop;
        Check(drops.Plan(w.Chef,item,player,out drop) && drop.Counter==null,"nearby ground drop requires a predicted supported landing within player reach");
        Check(w.Skills.Start(w.Chef,1,new ControlCommand {Operation="throw_to_player",Destination="1"})==null,"close ground handoff starts without requiring a throwable item or catcher");
        w.Tick();w.Hold(null);attachment.Attached=false;item.transform.position=new Vector3(0,.1f,-.6f);
        for(int i=0;i<10 && w.Skills.Run.Running;i++)w.Tick();
        Check(w.Skills.Run.Reason=="item_dropped_near_player" && !w.Inputs.Contains(2),"ground handoff verifies stable item and support near the player");
        floor.gameObject.Add(new RespawnCollider());w.Hold(item);item.transform.position=new Vector3(0,1,-.6f);
        Check(!drops.Plan(w.Chef,item,player,out drop),"nearby optimization cannot drop into a respawn hazard");
        human.transform.position=new Vector3(0,3,-.2f);
        Check(!drops.Plan(w.Chef,item,player,out drop),"nearby horizontal position on a different deck is not a valid drop handoff");
        Physics.Sphere=(p,r,d,length)=>new RaycastHit[0];
    }
    private static void GroundThrows()
    {
        var w=new World();var item=w.Ingredient("tomato");item.Add(new Throwable());item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});item.Add(new ServerPhysicalAttachment {Attached=true});item.Add(new BoxCollider {size=Vector3.one*.2f});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        var human=new GameObject("busy human");human.Add(new PlayerControls());human.Add(new PlayerIDProvider {Id=0});human.Add(new ServerPlayerAttachmentCarrier {Item=new GameObject("held meal")});w.Colliders.Add(human.Add(new CapsuleCollider()));human.Add(new GroundCast());human.transform.yaw=Mathf.PI;
        float flight;ThrowTrajectory.FlightTime(.1f-1,8*Mathf.Tan(15*Mathf.Deg2Rad),-9.81f,0,.02f,out flight);
        w.Chef.transform.position=new Vector3(0,0,-1.25f-8*flight-.4f);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        var planner=new ThrowPlanner();ThrowPlan plan;
        Check(planner.Prepare(w.Chef,item,human)=="player_cannot_catch","ordinary player passes retain their native catch preconditions");
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && FindPlan(planner,w.Navigation,out plan)==null && plan.Ground,"a checked floor throw works with full human hands and without a catcher");
        var groundAccess=new GroundPassAccess((receiver,landing)=>false,(receiver,cargo)=>false);
        var receiverChef=human.GetComponent<PlayerControls>();
        var rejected=new ThrowPlan {Stance=w.Chef.transform.position};
        rejected.AnchorStance(w.Chef.GroundCollider);
        groundAccess.Reject(rejected,receiverChef);
        Check(!groundAccess.Allows(rejected.CurrentStance,receiverChef) && groundAccess.Allows(rejected.CurrentStance+Vector3.right,receiverChef),
            "an unsafe release suppresses that stance but leaves alternatives open");
        human.transform.position+=Vector3.right;
        Check(groundAccess.Allows(rejected.CurrentStance,receiverChef),"moving the receiver invalidates a stale release rejection");
        human.transform.position-=Vector3.right;
        planner.GroundAccess=groundAccess;
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && FindPlan(planner,w.Navigation,out plan)!=null,
            "physical floor support alone cannot prove a recipient can reach a landing");
        planner.GroundAccess=null;
        var options=new ThrowOptions(w.Navigation);object preview;options.Begin(w.Chef);options.Check(human,out preview,true);options.End();
        bool ready=false;
        for(int n=0;n<20 && !ready;n++)
        {
            w.Chef.transform.position+=Vector3.right*.1f;item.transform.position+=Vector3.right*.1f;
            w.Navigation.Available(w.Chef);options.Tick(w.Chef);options.Begin(w.Chef);ready=options.Check(human,out preview,true);options.End();
        }
        Check(ready,"bounded throw preview can make progress while a raft translates the chef");
        var ray=Physics.Ray;
        Physics.Ray=(p,d,length,mask)=>Math.Abs(p.x)>.05f ? new RaycastHit[0] : ray(p,d,length,mask);
        w.Chef.transform.position.x=0;item.transform.position.x=0;
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && FindPlan(planner,w.Navigation,out plan)!=null,"a supported center cannot hide an unsupported edge of the landing footprint");
        Physics.Ray=ray;
        var wall=new GameObject("wall across throw").Add(new BoxCollider());Physics.Sphere=(p,r,d,length)=>new[] {new RaycastHit {collider=wall}};
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && !planner.From(w.Chef.transform.position,false,out plan),"floor throws cannot bypass solid flight obstacles");
        Physics.Sphere=(p,r,d,length)=>new RaycastHit[0];
        item.transform.position=new Vector3(0,.1f,-1.25f);item.GetComponent<ServerPhysicalAttachment>().Attached=false;w.Hold(null);
        Physics.Ray=(p,d,length,mask)=>mask==0 ? new RaycastHit[0] : ray(p,d,length,mask);
        var received=new DropHandoff();Check(received.Received(item,human.GetComponent<PlayerControls>()),"a long floor throw verifies using the receiving player's mask without an earlier close-drop plan");
        var player=human.GetComponent<PlayerControls>();player.GroundCollider=w.Colliders[0];item.transform.position=new Vector3(0,.1f,-4);
        Check(!received.Received(item,player) && received.Received(item,player,null,w.Colliders[0]),"a certified raft handoff accepts safe cargo beyond the old player-radius cutoff");
        var otherFloor=new GameObject("another raft").Add(new BoxCollider());
        Check(!received.Received(item,player,null,otherFloor),"a floor handoff cannot complete on another player's support");
        w.Colliders[0].gameObject.Add(new RespawnCollider());Check(!received.Received(item,human.GetComponent<PlayerControls>()),"a newly hazardous floor cannot count as a verified ground receipt");
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)=="no_nearby_ground","hazardous receiving ground cannot authorize a throw");
    }
    private static void RaftLandingFallback()
    {
        var w=new World();var item=w.Ingredient("potato");item.Add(new Throwable());item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});item.Add(new ServerPhysicalAttachment {Attached=true});item.Add(new BoxCollider {size=Vector3.one*.2f});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        w.Chef.transform.position=new Vector3(0,0,-8);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        var human=new GameObject("human");var player=human.Add(new PlayerControls {GroundCollider=w.Colliders[0]});human.Add(new PlayerIDProvider {Id=0});human.Add(new GroundCast());w.Colliders.Add(human.Add(new CapsuleCollider()));
        human.Add(new ServerPlayerAttachmentCarrier {Item=new GameObject("busy hands")});
        var clutter=new GameObject("previous floor deliveries");clutter.Add(new PhysicalAttachment());clutter.Add(new ServerPhysicalAttachment {Attached=false});
        w.Colliders.Add(clutter.Add(new BoxCollider {center=new Vector3(0,.15f,0),size=new Vector3(5,.3f,5)}));
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        var planner=new ThrowPlanner();ThrowPlan plan=null;
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && FindPlan(planner,w.Navigation,out plan)==null,
            "a crowded player neighbourhood does not exclude the rest of the receiving raft");
        Check(plan.LandingDistance>2.5f && plan.GroundSupport==w.Colliders[0],"farther fallback retains the exact receiving support");
        Vector3 local=plan.GroundLocalPoint;human.transform.position+=Vector3.right*4;
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && planner.RestoreGround(plan),"moving the human does not drag an already selected landing into occupied space");
        w.Colliders[0].transform.position+=Vector3.right*2;human.transform.position+=Vector3.right*2;
        clutter.transform.position+=Vector3.right*2;w.Target.transform.position+=Vector3.right*2;
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && planner.RestoreGround(plan) && plan.GroundLocalPoint.Equals(local),"a selected landing follows its moving raft while preserving its local position");
    }
    private static void CurrentPoseLandingClearance()
    {
        var w=new World();var floor=w.Colliders[0];floor.GetComponent<BoxCollider>().size=new Vector3(.96f,.1f,20);
        var ray=Physics.Ray;Physics.Ray=(p,d,length,mask)=>Math.Abs(p.x)<.48f ? ray(p,d,length,mask) : new RaycastHit[0];
        var item=w.Ingredient("potato");item.Add(new Throwable());item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});item.Add(new ServerPhysicalAttachment {Attached=true});
        item.Add(new BoxCollider {center=new Vector3(0,.15f,0),size=new Vector3(.76f,.3f,.77f)});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        var human=new GameObject("human on narrow receiving deck");human.Add(new PlayerControls {GroundCollider=floor});human.Add(new PlayerIDProvider {Id=0});human.Add(new GroundCast());w.Colliders.Add(human.Add(new CapsuleCollider()));
        human.Add(new ServerPlayerAttachmentCarrier {Item=new GameObject("busy hands")});
        float flight;ThrowTrajectory.FlightTime(-1,8*Mathf.Tan(15*Mathf.Deg2Rad),-9.81f,0,.02f,out flight);
        w.Chef.transform.position=new Vector3(0,0,-1.25f-8*flight-.4f);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        var planner=new ThrowPlanner();ThrowPlan plan=null;
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && FindPlan(planner,w.Navigation,out plan)==null && plan.WalkDistance<.01f,
            "a checked current-pose throw can use space too narrow for walking-arrival uncertainty");
        Vector3 local=plan.StanceLocalPoint;w.Colliders[0].transform.position+=Vector3.right;
        Check(Vector3.Distance(plan.CurrentStance,plan.Stance+Vector3.right)<.01f && plan.StanceLocalPoint.Equals(local),
            "cached release poses follow their support instead of gaining fictitious walking uncertainty as a raft moves");
    }
    private static void SupportedLandingArea()
    {
        var w=new World();var floor=w.Colliders[0];
        var item=w.Ingredient("potato");item.Add(new Throwable());item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});
        item.Add(new BoxCollider {size=Vector3.one*.2f});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        var human=new GameObject("human");human.Add(new PlayerControls {GroundCollider=floor});human.Add(new PlayerIDProvider {Id=0});human.Add(new GroundCast());w.Colliders.Add(human.Add(new CapsuleCollider()));
        float flight;ThrowTrajectory.FlightTime(.1f-1,8*Mathf.Tan(15*Mathf.Deg2Rad),-9.81f,0,.02f,out flight);
        w.Chef.transform.position=new Vector3(0,0,-2.25f-8*flight-.4f);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        var planner=new ThrowPlanner();ThrowPlan plan=null;
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && planner.From(w.Chef.transform.position,false,out plan),
            "a ground seed guides aim without excluding a fully supported landing elsewhere on the receiving floor");
        Check(plan.LandingDistance>2 && Math.Abs(plan.GroundLocalPoint.z-plan.Arrival.z)<.01f,
            "ground evidence and cached targets record the actual supported landing rather than the aim seed");
        var ray=Physics.Ray;
        Check(w.Skills.Start(w.Chef,1,new ControlCommand {Operation="throw_to_player",Destination="1",GroundHandoff=true})==null,"supported floor throw starts");
        for(int n=0;n<200 && w.Skills.Run.Phase=="planning_throw";n++) w.Tick();
        w.Arrive();
        Vector3 floorSize=floor.GetComponent<BoxCollider>().size;floor.GetComponent<BoxCollider>().size=new Vector3(.08f,.1f,20);
        Physics.Ray=(p,d,length,mask)=>Math.Abs(p.x)<.05f ? ray(p,d,length,mask) : new RaycastHit[0];w.Tick();
        Check(w.Skills.Run.Reason=="unsafe_release_stance" && !w.Inputs.Contains(2),
            "losing full foot support after arrival cannot arm the native throw button");
        floor.GetComponent<BoxCollider>().size=floorSize;
        var other=new GameObject("another disconnected platform").Add(new BoxCollider());
        Physics.Ray=(p,d,length,mask)=>{
            var contacts=ray(p,d,length,mask);
            if(p.z < -1.8f) for(int n=0;n<contacts.Length;n++) contacts[n].collider=other;
            return contacts;
        };
        Check(!planner.From(w.Chef.transform.position,false,out plan),"a supported landing on a different platform cannot satisfy the receiving-floor request");
    }
    private static void WalkingLandingClearance()
    {
        var w=new World();var local=w.Colliders[0];local.GetComponent<BoxCollider>().center=new Vector3(0,-.05f,-10);
        local.GetComponent<BoxCollider>().size=new Vector3(12,.1f,12);
        var floor=new GameObject("narrow receiving deck").Add(new BoxCollider {center=new Vector3(0,-.05f,0),size=new Vector3(.96f,.1f,4)});floor.gameObject.layer=9;w.Colliders.Add(floor);
        Physics.Ray=(p,d,length,mask)=>{
            Collider c=p.z < -4 && p.z > -16 && Math.Abs(p.x)<6 ? local : Math.Abs(p.x)<.48f && Math.Abs(p.z)<2 ? floor : null;
            return c!=null && d.y<-.9f && p.y>=0 && p.y<=length ? new[] {new RaycastHit {collider=c,point=new Vector3(p.x,0,p.z),normal=Vector3.up,distance=p.y}} : new RaycastHit[0];
        };
        var item=w.Ingredient("potato");item.Add(new Throwable());item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});
        item.Add(new BoxCollider {center=new Vector3(0,.15f,0),size=new Vector3(.76f,.3f,.77f)});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        w.Chef.transform.position=new Vector3(0,0,-14);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        var human=new GameObject("human");human.Add(new PlayerControls {GroundCollider=floor});human.Add(new PlayerIDProvider {Id=0});human.Add(new GroundCast());w.Colliders.Add(human.Add(new CapsuleCollider()));
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        for(int n=0;n<40;n++) {Time.realtimeSinceStartup+=.11f;Time.frameCount++;w.Navigation.Available(w.Chef);}
        var planner=new ThrowPlanner();ThrowPlan plan=null;
        string prepared=planner.Prepare(w.Chef,item,human,groundHandoff:true);string found=prepared==null ? FindPlan(planner,w.Navigation,out plan) : prepared;
        Check(found==null && plan.WalkDistance>1,
            "walking can seek a safe release for a narrow floor landing that will be revalidated at the actual stopped pose");
    }
    private static void LandingFootprintOrientation()
    {
        var w=new World();var local=w.Colliders[0];local.GetComponent<BoxCollider>().center=new Vector3(0,-.05f,-10);
        local.GetComponent<BoxCollider>().size=new Vector3(12,.1f,12);
        var floor=new GameObject("narrow receiving deck").Add(new BoxCollider {center=new Vector3(0,-.05f,0),size=new Vector3(.6f,.1f,4)});floor.gameObject.layer=9;w.Colliders.Add(floor);
        Physics.Ray=(p,d,length,mask)=>{
            Collider c=p.z < -4 && p.z > -16 && Math.Abs(p.x)<6 ? local : Math.Abs(p.x)<.3f && Math.Abs(p.z)<2 ? floor : null;
            return c!=null && d.y<-.9f && p.y>=0 && p.y<=length ? new[] {new RaycastHit {collider=c,point=new Vector3(p.x,0,p.z),normal=Vector3.up,distance=p.y}} : new RaycastHit[0];
        };
        var item=w.Ingredient("chicken");item.Add(new Throwable());item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});
        item.Add(new BoxCollider {center=new Vector3(0,.15f,0),size=new Vector3(.3f,.3f,.86f)});
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());w.Chef.gameObject.Add(new ServerAttachmentThrower());
        w.Chef.transform.position=new Vector3(0,0,-14);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);w.Hold(item);
        var human=new GameObject("human");human.Add(new PlayerControls {GroundCollider=floor});human.Add(new PlayerIDProvider {Id=0});human.Add(new GroundCast());w.Colliders.Add(human.Add(new CapsuleCollider()));
        Physics.Nearby=(p,r)=>w.Colliders.Where(c=>Vector3.Distance(p,c.bounds.ClosestPoint(p))<=r).ToArray();
        for(int n=0;n<40;n++) {Time.realtimeSinceStartup+=.11f;Time.frameCount++;w.Navigation.Available(w.Chef);}
        var planner=new ThrowPlanner();ThrowPlan plan=null;
        string prepared=planner.Prepare(w.Chef,item,human,groundHandoff:true);string found=prepared==null ? FindPlan(planner,w.Navigation,out plan) : prepared;
        Check(found==null && Math.Abs(plan.Direction.z)>.95f,"an elongated cargo lands lengthwise along a narrow receiving deck");
        w.Chef.transform.yaw=item.transform.yaw=Mathf.PI/2;item.transform.position=w.Chef.transform.position+new Vector3(.4f,1,0);
        Check(planner.Prepare(w.Chef,item,human,groundHandoff:true)==null && planner.RestoreGround(plan),
            "a selected landing stays valid while the chef faces elsewhere; the cargo lands along its planned throw");
    }
    private static void Main() { CounterForecasts();PersistentTasks();HumanSinkEligibility();NativeUtensilDefinitions();PreparationEquipmentDiscovery();PreparedIngredientSources();AvailablePreparationWork();OccupiedBoards();HeldItemTasks();ReconciledHandoffs();MixerReadiness();ReadyFoodTasks();DishDeliveryTasks();PauseTimers();MovingPickups();Transactions();IngredientsAndDisposal();WorkAndWait();ImmediateNativeWork();Activation();BallisticsAndLifecycle();NativeItemGeometry();Throws();IncrementalThrowPlanning();ClosePlayerPassPositions();GeneratedStances();LogisticsJobs();JobRecovery();PreparationSupplies();ValidJobOptions();RawIngredientSupplies();InventoryObservationCost();WindAndHeight();HeightOptions();CookerThrows();NearbyHandoffs();GroundThrows();RaftLandingFallback();CurrentPoseLandingClearance();SupportedLandingArea();WalkingLandingClearance();LandingFootprintOrientation();Console.WriteLine("PASS: "+checks+" action checks (production executor, mocked native state; no game launched)"); }
    private static ClientInteractable GetCurrentlyInteractingForTest(this PlayerControls chef) { return chef.GetComponent<ClientPlayerControlsImpl_Default>().Current; }
}
