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
        var options=new ThrowOptions(w.Navigation);object info;options.Begin(w.Chef);
        Check(!options.Check(w.Target,out info) && !options.End(),"unproven throw choices are withheld while preview is pending");
        for(int i=0;i<300 && !options.Check(w.Target,out info);i++) options.Tick(w.Chef);
        Check(options.Check(w.Target,out info),"native planner proof enables a reachable counter option");
        int inputs=w.Inputs.Count,releases=w.Releases;Vector3 original=w.Chef.transform.position;
        Check(w.Carrier.Item==item && w.Navigation.Action.State=="idle" && inputs==0 && releases==0 && Vector3.Distance(w.Chef.transform.position,original)==0,"option planning does not acquire input, navigate, or mutate cargo");
        w.Target.transform.position=Vector3.up*2;
        Check(!options.Check(w.Target,out info),"raising a previously feasible receiver immediately removes its old option");
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
    private static void Main() { Transactions();IngredientsAndDisposal();WorkAndWait();Activation();BallisticsAndLifecycle();NativeItemGeometry();Throws();IncrementalThrowPlanning();ClosePlayerPassPositions();GeneratedStances();LogisticsJobs();PreparationSupplies();ValidJobOptions();RawIngredientSupplies();WindAndHeight();HeightOptions();NearbyHandoffs();Console.WriteLine("PASS: "+checks+" action checks (production executor, mocked native state; no game launched)"); }
    private static ClientInteractable GetCurrentlyInteractingForTest(this PlayerControls chef) { return chef.GetComponent<ClientPlayerControlsImpl_Default>().Current; }
}
