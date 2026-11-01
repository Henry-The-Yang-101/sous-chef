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
    private sealed class Pickup : MonoBehaviour, IPickup { public bool CanHandlePickup(ClientPlayerAttachmentCarrier c) { return true; } }
    private sealed class Placement : MonoBehaviour, IPlacement { public bool CanHandlePlacement(ClientPlayerAttachmentCarrier c,Vector2 d,PlacementContext x) { return true; } }
    private sealed class Throwable : MonoBehaviour, IClientThrowable, ICatchable { public bool CanHandleThrow(ClientAttachmentThrower t,Vector2 d) { return true; } }
    private sealed class Catcher : MonoBehaviour, IHandleCatch { public bool Allowed=true; public bool CanHandleCatch(ICatchable i,Vector2 d) { return Allowed; } }
    private sealed class World
    {
        public readonly PlayerControls Chef;
        public readonly GameObject Target;
        public readonly NavigationActions Navigation;
        public readonly InteractionActions Skills;
        public readonly LogisticsActions Logistics;
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
            Chef.GroundCollider=floor;
            Target=new GameObject("station");Target.Add(new ServerWorkstation());Target.Add(new ServerAttachStation());
            var box=Target.Add(new BoxCollider {size=new Vector3(.8f,1,.8f),center=new Vector3(0,.5f,0)});
            var use=Target.Add(new ClientInteractable());Target.Add(new ServerInteractable());Target.Add(new Pickup());Target.Add(new Placement());
            if (configure != null) configure(Target);
            Colliders.Add(floor);Colliders.Add(box);
            Physics.Nearby=(p,r)=>Colliders.ToArray();
            Physics.Ray=(p,d,length,mask)=>d.y<-.9f && p.y>=0 && p.y<=length ? new[] {new RaycastHit {collider=floor,point=new Vector3(p.x,0,p.z),normal=Vector3.up,distance=p.y}} : new RaycastHit[0];
            Physics.Overlap=(a,b,r)=>new Collider[0];Physics.Cast=(a,b,r,d,l)=>new RaycastHit[0];
            Physics.Penetration=(a,p,q,b,r,s)=>false;Physics.gravity=new Vector3(0,-9.81f,0);
            Chef.CurrentInteractionObjects.m_TheOriginalHandlePickup=Target;
            Chef.CurrentInteractionObjects.m_iHandlePickup=Target.GetComponent<IPickup>();
            Chef.CurrentInteractionObjects.m_iHandlePlacement=Target.GetComponent<IPlacement>();
            Chef.CurrentInteractionObjects.m_interactable=use;
            Navigation=new NavigationActions((d,ms)=>{},()=>{});
            Skills=new InteractionActions(Navigation,b=>Inputs.Add(b),()=>{if(Logistics!=null && Logistics.Run.Running) Inputs.Add(0); else Releases++;});
            Logistics=new LogisticsActions(Navigation,Skills,b=>Inputs.Add(b),()=>Releases++);
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
        public void Tick(float delta=.05f) { Time.realtimeSinceStartup+=delta;Time.frameCount++;Skills.Tick();Logistics.Tick(); }
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
        var planner=new ThrowPlanner();ThrowPlan plan;
        Check(planner.Prepare(w.Chef,item,w.Target)==null && planner.From(w.Chef.transform.position,false,out plan),"production planner validates a damped throw to board receiving volume");
        Check(planner.Find(w.Navigation,out plan)==null && plan.WalkDistance<.1f,"inverse search prefers an already reachable valid release stance");
        Physics.Sphere=(p,r,d,length)=>new[] {new RaycastHit {collider=colliders.First(c=>c.gameObject.name=="floor")}};
        Check(!planner.From(w.Chef.transform.position,false,out plan),"flight obstruction rejects a previously valid arc");Physics.Sphere=(p,r,d,l)=>new RaycastHit[0];
        w.Target.GetComponent<Catcher>().Allowed=false;
        Check(!planner.From(w.Chef.transform.position,false,out plan),"native catcher veto rejects incompatible receipt");w.Target.GetComponent<Catcher>().Allowed=true;
        w.Chef.CurrentInteractionObjects.m_interactable=null;
        Check(w.Start("throw_to")==null,"targeted throw starts with a planned release stance");w.Arrive();w.Tick();w.Tick();w.Tick();
        Check(w.Skills.Run.Phase=="flight" && w.Skills.Run.State=="running","release is tracked separately from successful receipt");
        body.m_motion=new RigidbodyMotion {Velocity=new Vector3(1,2,3)};w.Chef.GetComponent<ServerAttachmentThrower>().Launched(item);
        var observed=(float[])((Json.Record)((Json.Record)w.Skills.Describe())["details"])["observedLaunchVelocity"];
        Check(observed[0]==1 && observed[1]==2 && observed[2]==3,"native throw callback records actual launch velocity rather than reusing a prediction");
        w.Hold(null);w.Target.GetComponent<ServerAttachStation>().Item=item;w.Tick();
        Check(w.Skills.Run.State=="succeeded" && w.Skills.Run.Reason=="throw_received","native holder edge confirms throw receipt");
        Check(w.Chef.GetComponent<ServerAttachmentThrower>().Launched==null,"throw observation callback is removed after completion");

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
    private static void GeneratedStances()
    {
        var w=new World();
        Check(w.Navigation.StartPosition(w.Chef,1,w.Target,w.Chef.transform.position)==null,"generated release stance uses production route planner");
        var catalog=(Json.Record)w.Navigation.Available(w.Chef);
        Check(((List<object>)catalog["destinations"]).All(d=>(string)((Json.Record)d)["id"]!="throw_stance"),"internal stance never leaks into wire destination options");
        w.Navigation.Stop("stopped");
        Check(w.Navigation.Action.State=="idle","generated stance navigation is interruptible");
    }
    private static void LogisticsJobs()
    {
        var w=new World();var destination=w.Delivery();var item=w.Ingredient("Tomato_Raw");w.Target.GetComponent<ServerAttachStation>().Item=item;
        Check(w.Logistics.Start(w.Chef,1,new ControlCommand {Operation="move_item",Source=w.Id,Destination=w.IdOf(destination)})==null,"move_item resolves exact source and empty destination with empty hands");
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
        item.Add(new AttachmentWindReceiver());item.Add(new ServerAttachmentWindReceiver());
        var crosswind=new GameObject("crosswind");var crossVolume=crosswind.Add(new WindVolume {Velocity=new Vector3(1,0,0)});crosswind.Add(new BoxCollider {size=Vector3.one*100});
        Physics.Penetration=(a,p,q,b,r,s)=>b.bounds.Contains(p);planner.Prepare(w.Chef,item,w.Target);
        Check(planner.From(w.Chef.transform.position,false,out plan) && plan.Direction.x<0 && plan.WindDisplacement.x>0,"production targeting compensates crosswind while retaining native launch speed");
        crossVolume.Velocity=new Vector3(0,1,0);
        // A higher source floor belongs to the same production walking graph.
        var elevatedFloor=w.Colliders[0];elevatedFloor.transform.position=new Vector3(0,2,0);
        Physics.Ray=(p,d,length,mask)=>d.y<-.9f && p.y>=2 && p.y-2<=length ? new[] {new RaycastHit {collider=elevatedFloor,point=new Vector3(p.x,2,p.z),normal=Vector3.up,distance=p.y-2}} : new RaycastHit[0];
        planner.Prepare(w.Chef,item,w.Target);
        Check(planner.Find(w.Navigation,out plan)==null && plan.Arrival.y>3 && plan.WindDisplacement.y>0,"inverse release search adjusts flight time and positions for an updraft and elevated receiving surface");
        crossVolume.enabled=false;
        w.Chef.transform.position=new Vector3(0,0,w.Chef.transform.position.z);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);planner.Prepare(w.Chef,item,w.Target);
        Check(!planner.From(w.Chef.transform.position,false,out plan),"unreachable target height is rejected instead of projecting a flat throw");
    }
    private static void NearbyHandoffs()
    {
        var w=new World();var item=w.Ingredient("tomato");item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});item.Add(new BoxCollider {size=Vector3.one*.2f});w.Hold(item);
        var human=new GameObject("human");var player=human.Add(new PlayerControls());human.Add(new PlayerIDProvider {Id=0});human.Add(new CapsuleCollider());human.Add(new ServerPlayerAttachmentCarrier {Item=new GameObject("already holding food")});human.transform.position=new Vector3(.5f,0,0);
        Check(w.Skills.Start(w.Chef,1,new ControlCommand {Operation="throw_to_player",Destination="1"})==null && w.Skills.Run.Phase=="drop_press","nearby handoff prefers a usable counter even when the player cannot catch");
        w.Tick();w.Target.GetComponent<ServerAttachStation>().Item=item;w.Hold(null);w.Verify();
        Check(w.Skills.Run.State=="succeeded" && w.Skills.Run.Reason=="item_handed_off_nearby" && !w.Inputs.Contains(2),"nearby counter delivery uses pickup placement, not a charged throw");

        w=new World();item=w.Ingredient("tomato");var physical=item.Add(new PhysicalAttachment {m_motion=new RigidbodyMotion()});var attachment=item.Add(new ServerPhysicalAttachment {Attached=true});item.Add(new BoxCollider {size=Vector3.one*.2f});item.transform.position=new Vector3(0,1,-.6f);w.Hold(item);
        human=new GameObject("human");player=human.Add(new PlayerControls());human.Add(new PlayerIDProvider {Id=0});human.Add(new CapsuleCollider());human.Add(new ServerPlayerAttachmentCarrier());human.transform.position=new Vector3(0,0,-.2f);
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
    private static void Main() { Transactions();IngredientsAndDisposal();WorkAndWait();Activation();BallisticsAndLifecycle();Throws();GeneratedStances();LogisticsJobs();WindAndHeight();NearbyHandoffs();Console.WriteLine("PASS: "+checks+" action checks (production executor, mocked native state; no game launched)"); }
    private static ClientInteractable GetCurrentlyInteractingForTest(this PlayerControls chef) { return chef.GetComponent<ClientPlayerControlsImpl_Default>().Current; }
}
