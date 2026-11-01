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
        public readonly ServerPlayerAttachmentCarrier Carrier;
        public readonly List<int> Inputs = new List<int>();
        public string Id;
        public int Releases;
        public World(Action<GameObject> configure = null)
        {
            UnityEngine.Object.All.Clear(); Time.realtimeSinceStartup=10;Time.frameCount=1;
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
            Physics.Nearby=(p,r)=>new Collider[] {floor,box};
            Physics.Ray=(p,d,length,mask)=>d.y<-.9f && p.y>=0 && p.y<=length ? new[] {new RaycastHit {collider=floor,point=new Vector3(p.x,0,p.z),normal=Vector3.up,distance=p.y}} : new RaycastHit[0];
            Physics.Overlap=(a,b,r)=>new Collider[0];Physics.Cast=(a,b,r,d,l)=>new RaycastHit[0];
            Chef.CurrentInteractionObjects.m_TheOriginalHandlePickup=Target;
            Chef.CurrentInteractionObjects.m_iHandlePickup=Target.GetComponent<IPickup>();
            Chef.CurrentInteractionObjects.m_iHandlePlacement=Target.GetComponent<IPlacement>();
            Chef.CurrentInteractionObjects.m_interactable=use;
            Navigation=new NavigationActions((d,ms)=>{},()=>{});
            Skills=new InteractionActions(Navigation,b=>Inputs.Add(b),()=>Releases++);
            for(int i=0;i<10 && Id==null;i++)
            {
                var catalog=(Json.Record)Navigation.Available(Chef);
                foreach(Json.Record d in (List<object>)catalog["destinations"]) if((int)d["objectInstanceId"]==Target.GetInstanceID()) Id=(string)d["id"];
            }
            Check(Id!=null,"fixture has a real navigable target");
        }
        public GameObject Ingredient(string label)
        {
            var go=new GameObject(label);go.Add(new IngredientPropertiesComponent {Node=new AssembledDefinitionNode(label)});go.Add(new IngredientTransfer());return go;
        }
        public void Hold(GameObject item) { Carrier.Item=item;Chef.GetComponent<ClientPlayerAttachmentCarrier>().Item=item; }
        public string Start(string op,string condition=null,string state=null,int ms=10000)
        { return Skills.Start(Chef,1,new ControlCommand {Operation=op,Destination=Id,Condition=condition,DesiredState=state,Milliseconds=ms}); }
        public void Arrive() { Navigation.Stop("arrived");Tick(); }
        public void Tick(float delta=.05f) { Time.realtimeSinceStartup+=delta;Time.frameCount++;Skills.Tick(); }
        public void Verify() { Tick();Tick();Tick(); }
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
        Check(ThrowTrajectory.FlightTime(0,2,-10,out t) && Math.Abs(t-.4f)<.0001f,"descending equal-height arc solved");
        Check(!ThrowTrajectory.FlightTime(1,2,-10,out t),"target above apex is rejected");
        Check(!ThrowTrajectory.FlightTime(0,2,0,out t),"unsupported zero gravity fails closed");
        Check(ThrowTrajectory.Intercept(4,0,1,0,5,out t) && Math.Abs(t-1)<.0001f,"moving-player horizontal intercept");
        Check(!ThrowTrajectory.Intercept(4,0,10,0,5,out t),"unreachable moving receiver is rejected");
        Check(Math.Abs(ThrowTrajectory.Height(0,2,-10,.4f,.02f)+.04f)<.0001f,"arc accounts for fixed-step gravity integration");
        foreach(float invalid in new[] {float.NaN,float.PositiveInfinity}) Check(!ThrowTrajectory.Intercept(invalid,0,0,0,5,out t),"invalid physics cannot create a plan");
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
        w.Chef.gameObject.Add(new AttachmentThrower());w.Chef.gameObject.Add(new ClientAttachmentThrower());
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
        Check(w.Start("throw_to")==null,"targeted throw starts with a planned release stance");w.Arrive();w.Tick();w.Tick();w.Tick();
        Check(w.Skills.Run.Phase=="flight" && w.Skills.Run.State=="running","release is tracked separately from successful receipt");
        w.Hold(null);w.Target.GetComponent<ServerAttachStation>().Item=item;w.Tick();
        Check(w.Skills.Run.State=="succeeded" && w.Skills.Run.Reason=="throw_received","native holder edge confirms throw receipt");

        var human=new GameObject("human");var player=human.Add(new PlayerControls());human.Add(new PlayerIDProvider {Id=0});
        human.Add(new AttachmentCatcher());human.Add(new ServerPlayerAttachmentCarrier());human.transform.yaw=Mathf.PI;
        w.Target.GetComponent<ServerAttachStation>().Item=null;w.Hold(item);
        distance=ThrowTrajectory.Displacement(8,0,.2f,.6f,.02f);w.Chef.transform.position=new Vector3(0,0,-distance-.4f);item.transform.position=w.Chef.transform.position+new Vector3(0,1,.4f);
        player.Motion.Velocity=new Vector3(1,0,0);
        Check(planner.Prepare(w.Chef,item,human)==null && planner.From(w.Chef.transform.position,false,out plan) && plan.Direction.x>0,"player pass leads a moving human who faces the incoming throw");
        human.transform.yaw=0;Check(!planner.From(w.Chef.transform.position,false,out plan),"player facing away is outside native catch cone");
        human.GetComponent<ServerPlayerAttachmentCarrier>().Item=new GameObject("occupied hands");
        Check(planner.Prepare(w.Chef,item,human)=="player_cannot_catch","player pass respects occupied hands");
    }
    private static void Main() { Transactions();IngredientsAndDisposal();WorkAndWait();Activation();BallisticsAndLifecycle();Throws();Console.WriteLine("PASS: "+checks+" action checks (production executor, mocked native state; no game launched)"); }
    private static ClientInteractable GetCurrentlyInteractingForTest(this PlayerControls chef) { return chef.GetComponent<ClientPlayerControlsImpl_Default>().Current; }
}
