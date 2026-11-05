using System;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // First-layer skills use ordinary logical button edges. Navigation is a child
    // action under the same lease; no ownership gap occurs at station arrival.
    internal sealed class InteractionActions
    {
        public readonly ActionRun Run = new ActionRun();
        private readonly NavigationActions navigation;
        private readonly Action<int> input;
        private readonly Action release;
        private readonly ThrowPlanner throws = new ThrowPlanner();
        private readonly DropHandoff drops = new DropHandoff();
        private PlayerControls chef;
        private ControlCommand command;
        private GameObject target, held, receiver, expectedPickup, expectedTopPlate, initialOccupant, initialServedPlate;
        private GameObject pickupPrefab;
        private Vector3 pickupOrigin, pickupForward;
        private ServerIngredientContainer container;
        private AssembledDefinitionNode ingredient, expectedOutput;
        private ServerWorkableItem workable;
        private ServerInteractable interactable;
        private ServerInteractable.BeginInteractCallback triggered;
        private int initialDirty, initialClean, initialStack, initialMatches, initialContents, pressFrame, planningFrame;
        private int initialSourceContents;
        private float phaseStarted, lastProgressTime, lastProgress;
        private bool triggeredByChef, pressed, throwReleased;
        internal GroundPassAccess GroundAccess { set { throws.GroundAccess = value; } }
        private ThrowPlan throwPlan;
        private DropPlan dropPlan;
        private float dropStableSince = -1;
        private Vector3 lastDropPosition;
        private int groundDropDirection;
        private float groundDropTurnAt;
        private GameObject groundDropAwayFrom;
        private Vector3 groundDropOrigin;
        private int groundDropStance;
        private float groundDropSeekAt;
        private ServerAttachmentThrower launchObserver;
        private GenericVoid<GameObject> launchCallback;
        private bool sawLaunch;
        private Vector3 observedLaunchVelocity;
        private object evidence;
        private readonly ActionRecovery recovery = new ActionRecovery();
        private float nextRecoveryAttempt;
        private float lastTick;

        public InteractionActions(NavigationActions walking, Action<int> buttons, Action finished)
        { navigation = walking; input = buttons; release = finished; }
        public object Describe()
        {
            var receipt = (Json.Record)Run.Describe(sawLaunch ? Json.Object("result", evidence, "observedLaunchVelocity", Values.Vector(observedLaunchVelocity)) : evidence);
            receipt["recovery"] = recovery.Describe(); return receipt;
        }
        public static bool Handles(string operation)
        { return Array.IndexOf(ActionRun.Names, operation) >= 0 && operation != "navigate_to" && operation != "stop"; }
        public bool RouteUnavailable { get { return recovery.Active && recovery.Elapsed > .5f &&
            (recoveryReason == "destination_unavailable" || recoveryReason == "destination_unreachable"); } }
        public bool TransferUnavailable { get { return RouteUnavailable || recovery.Active &&
            recoveryReason == "placement_unavailable" && recovery.Elapsed > .5f; } }
        public bool CanRetry { get { return !pressed && !throwReleased && Run.State != "cancelled"; } }
        public bool NavigationStalled { get { return Run.Phase == "recovering_navigation" &&
            (recoveryReason == "no_progress" || recoveryReason == "alignment_failed" || recoveryReason == "navigation_timeout") ||
            Obstruction != null && recovery.Elapsed > .5f; } }
        // Positioned at the target while the native selection holds another
        // loose object. Waiting cannot change that selection; moving it can.
        public GameObject Obstruction
        {
            get
            {
                if (!Run.Running || Run.Phase != "navigating" || navigation.Action.State != "idle" || navigation.Action.Reason != "positioned_no_action") return null;
                GameObject selected = chef.CurrentInteractionObjects.m_TheOriginalHandlePickup;
                return selected != null && !ActionObservations.Matches(selected, target) && ActionObservations.Loose(selected) ? selected : null;
            }
        }
        public void ContinueRecovery(ActionRecovery previous)
        { if (Run.Phase == "recovering_navigation") recovery.ContinueFrom(previous); }
        public void ResumeAfterPause(float seconds)
        {
            if (!Run.Running) return;
            Run.Delay(seconds); phaseStarted += seconds; lastProgressTime += seconds;
            nextRecoveryAttempt += seconds; if (dropStableSince >= 0) dropStableSince += seconds;
            lastTick = Time.realtimeSinceStartup;
        }
        private string WalkDestination()
        { return command.Operation == "pick_up" ? navigation.RegisterItem(ActionObservations.PickupApproach(target)) : command.Destination; }
        public static string[] Offered(PlayerControls chef, GameObject go)
        {
            var options = new List<string> { "wait_until" };
            GameObject held = ActionObservations.Held(chef), receiver = ActionObservations.Receiver(go);
            var carrier = chef.GetComponent<ClientPlayerAttachmentCarrier>();
            var pickup = PlayerControlsHelper.GetControllingPickupHandler_Client(go);
            var placement = PlayerControlsHelper.GetControllingPlacementHandler_Client(go);
            var interact = go.GetComponent<ClientInteractable>();
            Vector3 delta = go.transform.position - chef.transform.position; delta.y = 0; delta.Normalize();
            bool canPlace = held != null && carrier != null && placement != null && placement.CanHandlePlacement(carrier, new Vector2(delta.x, delta.z), new PlacementContext(PlacementContext.Source.Player));
            if (held == null)
            {
                if (pickup != null && carrier != null && pickup.CanHandlePickup(carrier) && ActionObservations.Dispensing(go)) options.Add("pick_up");
                if (interact != null && interact.CanInteract(chef.gameObject))
                {
                    if (go.GetComponent<ServerWashingStation>() != null || receiver.GetComponent<ServerWorkableItem>() != null) options.Add("work_at");
                    else if (!interact.UsePlacementButton || pickup == null || carrier == null || !pickup.CanHandlePickup(carrier)) options.Add("activate");
                }
            }
            else
            {
                if (go.GetComponent<ServerRubbishBin>() != null) { if (canPlace) options.Add("discard"); }
                else if (canPlace && (ActionObservations.Occupant(go) == null || ActionObservations.StackCount(receiver) > 0 || go.GetComponent<ServerWashingStation>() != null)) options.Add("place_at");
                var container = ActionObservations.Container(go);
                var transfer = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(held);
                var composition = ActionObservations.Composition(held);
                if (canPlace && held.GetComponent<IngredientPropertiesComponent>() != null && container != null && transfer != null && composition != null &&
                    transfer.CanTransferToContainer(container) && container.CanAddIngredient(composition)) options.Add("add_ingredient");
                var recipient = held.GetComponent<ServerIngredientContainer>();
                var sourceTransfer = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(receiver);
                if (canPlace && recipient != null && container != null && sourceTransfer != null && TaskNative.ReadyAt(go) &&
                    sourceTransfer.CanTransferToContainer(recipient)) options.Add("collect_contents");
                if (GameObjectUtils.RequestInterface<IClientThrowable>(held) != null && GameObjectUtils.RequestInterface<IHandleCatch>(receiver) != null && go.GetComponent<ServerRubbishBin>() == null) options.Add("throw_to");
            }
            return options.ToArray();
        }
        public static string[] ConditionsFor(GameObject go, bool workOnly)
        {
            var result = new List<string>(); GameObject receiver = ActionObservations.Receiver(go);
            if (receiver.GetComponent<ServerWorkableItem>() != null) result.Add("work_complete");
            if (go.GetComponent<ServerWashingStation>() != null) { result.Add("sink_empty"); result.Add("one_plate_clean"); }
            if (!workOnly)
            {
                result.Add("reachable"); result.Add("ready");
                if (go.GetComponent<ServerAttachStation>() != null || go.GetComponent<ServerWashingStation>() != null || go.GetComponent<ServerIngredientContainer>() != null || go.GetComponent<ServerStack>() != null) { result.Add("occupied"); result.Add("empty"); }
                if (receiver.GetComponent<ClientCookingHandler>() != null) result.Add("cooked");
                if (receiver.GetComponent<ClientMixingHandler>() != null) result.Add("mixed");
            }
            return result.ToArray();
        }

        public string Start(PlayerControls player, int owner, ControlCommand request)
        {
            if (Run.Running || navigation.Action.State != "idle") return "action_in_progress";
            recovery.Reset(); recoveryReason = null; nextRecoveryAttempt = 0; lastTick = Time.realtimeSinceStartup;
            chef = player; command = request; target = null;
            if (request.GroundDrop && request.Operation == "place_at") target = chef.gameObject;
            else if (request.Operation == "throw_to_player")
            {
                foreach (PlayerControls candidate in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                {
                    var id = candidate.PlayerIDProvider;
                    if (id != null && ((int)id.GetID() + 1).ToString() == request.Destination && candidate != chef)
                    { if (target != null) return "player_ambiguous"; target = candidate.gameObject; }
                }
            }
            else target = navigation.Resolve(request.Destination);
            if (target == null) return "target_unavailable";
            if (request.Operation == "pick_up")
            {
                GameObject pickup = ActionObservations.PickupTarget(target);
                if (pickup != target) { target = pickup; request.Destination = navigation.RegisterItem(target); }
            }
            pickupOrigin = target.transform.position;
            pickupForward = target.transform.forward;
            var pickupSupply = target.GetComponent<PickupItemSpawner>();
            pickupPrefab = pickupSupply == null ? null : pickupSupply.m_itemPrefab;
            held = ActionObservations.Held(chef); receiver = ActionObservations.Receiver(target);
            initialOccupant = ActionObservations.Occupant(target); expectedPickup = initialOccupant;
            initialServedPlate = DeliveredPlate(target);
            container = ActionObservations.Container(target); ingredient = expectedOutput = null;
            initialDirty = ActionObservations.DirtyCount(target); initialClean = ActionObservations.CleanCount(target);
            initialStack = ActionObservations.StackCount(receiver); initialMatches = 0;
            var stack = receiver == null ? null : receiver.GetComponent<ServerStack>();
            expectedTopPlate = stack == null ? null : stack.InspectTopOfStack();
            initialContents = held == null || held.GetComponent<ServerIngredientContainer>() == null ? 0 : held.GetComponent<ServerIngredientContainer>().GetContentsCount();
            initialSourceContents = container == null ? 0 : container.GetContentsCount();
            pressed = throwReleased = triggeredByChef = sawLaunch = false; evidence = null; throwPlan = null; dropPlan = null; dropStableSince = -1; workable = null;
            groundDropAwayFrom = request.GroundDrop ? navigation.Resolve(request.GroundDropAwayFrom) : null;
            groundDropDirection = groundDropAwayFrom == null ? -1 : 0; groundDropTurnAt = Time.realtimeSinceStartup + .3f;
            groundDropOrigin = Foot(); groundDropStance = 0; groundDropSeekAt = Time.realtimeSinceStartup + 1;
            string error = ValidateRequest();
            if (error != null) return error;
            if (!Run.Start(owner, request, Time.realtimeSinceStartup)) return "action_in_progress";
            try
            {
                if (request.Operation == "wait_until") { Run.Advance("waiting"); input(0); return null; }
                if (request.GroundDrop) { SetPhase("positioning_ground_drop"); input(0); return null; }
                // A placement already leaves us stopped at the selected board.
                // Use that native interaction opportunity without navigating
                // and settling a second time. All work/identity checks remain.
                if (request.Operation == "work_at" && CanWorkHere())
                {
                    if (BeforePress() && Run.Running) { SetPhase("working"); Work(); }
                    return null;
                }
                if (request.Operation == "throw_to_player" && drops.Plan(chef, held, target.GetComponent<PlayerControls>(), out dropPlan))
                { evidence = dropPlan.Describe(); SetPhase("drop_press"); return null; }
                if (request.Operation == "throw_to" || request.Operation == "throw_to_player")
                {
                    error = throws.Prepare(chef, held, target, groundHandoff: command.GroundHandoff);
                    if (error == null) error = PlanThrow(owner);
                }
                else
                {
                    Run.Advance("navigating");
                    error = navigation.Start(chef, owner, WalkDestination());
                }
                if (error != null)
                {
                    if (request.Operation == "throw_to" || request.Operation == "throw_to_player") Finish("blocked", error);
                    else if (RecoverNavigation(error)) return null;
                }
                return error;
            }
            catch (Exception) { Finish("failed", "observation_unavailable"); throw; }
        }
        private string PlanThrow(int owner)
        {
            planningFrame = Time.frameCount;
            string error = throws.Find(navigation, out throwPlan);
            if (error == ThrowPlanner.Pending) { SetPhase("planning_throw"); input(0); return null; }
            if (error != null) { evidence = throws.Diagnostics; return error; }
            launchObserver = chef.GetComponent<ServerAttachmentThrower>();
            launchCallback = delegate(GameObject launched)
            {
                if (!System.Object.ReferenceEquals(launched, held)) return;
                // Native input expiry can release the throw before our next
                // Update. The launch event is the irreversible boundary even
                // when the executor still believes it is holding the button.
                throwReleased = true;
                var physical = launched.GetComponent<PhysicalAttachment>();
                if (physical != null && physical.m_motion != null) { observedLaunchVelocity = physical.m_motion.GetVelocity(); sawLaunch = true; }
            };
            launchObserver.RegisterThrowCallback(launchCallback);
            evidence = throwPlan.Describe(); Run.Advance("navigating");
            return navigation.StartPosition(chef, owner, target, throwPlan.CurrentStance, true);
        }
        private string ValidateRequest()
        {
            string op = command.Operation;
            if (op == "pick_up")
            {
                if (held != null) return "hands_full";
                if (!ActionObservations.Dispensing(target)) return "pickup_unavailable";
                if (expectedPickup == null && (target.GetComponent<PhysicalAttachment>() != null || target.GetComponent<ServerStack>() != null)) expectedPickup = target;
                if (expectedPickup == null && target.GetComponent<PickupItemSpawner>() == null && target.GetComponent<PlateStackBase>() == null)
                    return "nothing_to_pick_up";
            }
            if (op == "place_at" || op == "add_ingredient" || op == "collect_contents" || op == "discard" || op == "throw_to" || op == "throw_to_player")
                if (held == null) return "hands_empty";
            if (op == "place_at" && target.GetComponent<ServerRubbishBin>() != null) return "use_discard";
            if (op == "add_ingredient")
            {
                // Container-to-container transfer belongs to the higher layer.
                if (held.GetComponent<IngredientPropertiesComponent>() == null) return "held_item_is_not_single_ingredient";
                ingredient = ActionObservations.Composition(held);
                if (container == null || ingredient == null) return "target_is_not_ingredient_container";
                var transfer = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(held);
                if (transfer == null || !transfer.CanTransferToContainer(container) || !container.CanAddIngredient(ingredient)) return "incompatible_ingredient";
                initialMatches = ActionObservations.MatchingCount(container, ingredient);
            }
            if (op == "discard" && target.GetComponent<ServerRubbishBin>() == null) return "target_is_not_disposal";
            if (op == "collect_contents")
            {
                var recipient = held.GetComponent<ServerIngredientContainer>();
                var transfer = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(receiver);
                if (recipient == null || container == null || transfer == null || !transfer.CanTransferToContainer(recipient)) return "incompatible_contents_transfer";
                if (!TaskNative.ReadyAt(target)) return "processing_not_ready";
            }
            if (op == "throw_to" || op == "throw_to_player")
            {
                ingredient = ActionObservations.Composition(held);
                initialMatches = ActionObservations.MatchingCount(container, ingredient);
            }
            if (op == "work_at" || op == "activate")
            {
                if (held != null) return "hands_full"; // Releasing use with a held throwable would throw it.
                if (target.GetComponent<ClientInteractable>() == null) return "target_is_not_interactable";
            }
            if (op == "activate" && command.DesiredState != "trigger" && !ActionObservations.ToggleState(target).HasValue)
                return "unsupported_activation_state";
            if (op == "work_at" || op == "wait_until")
            {
                if (command.Condition == "work_complete")
                {
                    GameObject item = ActionObservations.Receiver(target);
                    workable = item == null ? null : item.GetComponent<ServerWorkableItem>();
                    var definition = item == null ? null : item.GetComponent<WorkableItem>();
                    if (workable == null || definition == null) return "no_workable_item";
                    expectedOutput = ActionObservations.Composition(definition.GetNextPrefab());
                }
                if ((command.Condition == "sink_empty" || command.Condition == "one_plate_clean") && initialDirty < 0) return "target_is_not_sink";
                if (command.Condition == "one_plate_clean" && initialDirty == 0) return "sink_empty";
                if (command.Condition == "cooked" && receiver.GetComponent<ClientCookingHandler>() == null) return "target_is_not_cookable";
                if (command.Condition == "mixed" && receiver.GetComponent<ClientMixingHandler>() == null) return "target_is_not_mixable";
                if ((command.Condition == "occupied" || command.Condition == "empty") && Array.IndexOf(ConditionsFor(target, false), command.Condition) < 0) return "occupancy_unavailable";
            }
            return null;
        }
        public void Tick()
        {
            if (!Run.Running) return;
            try
            {
                float elapsed = Time.realtimeSinceStartup - lastTick; lastTick = Time.realtimeSinceStartup;
                if (recovery.Active) { Run.Delay(elapsed); lastProgressTime += elapsed; }
                if (Run.Expired(Time.realtimeSinceStartup)) { Finish("failed", "action_timeout"); return; }
                if (throwReleased && Run.Phase == "throw_hold") { input(0); SetPhase("flight"); }
                // The last plate can destroy its own source stack. Verify the
                // surviving carried identity before requiring a live source.
                if (pressed && command.Operation == "pick_up" && Outcome()) { Finish("succeeded", "outcome_observed"); return; }
                if (target == null || !target.activeInHierarchy)
                {
                    if (Run.Phase == "navigating")
                    {
                        if (navigation.Action.State == "navigating") navigation.Stop("target_unavailable");
                        Run.Advance("recovering_navigation");
                    }
                    WaitFor("target_unavailable"); return;
                }
                if (command.Operation == "pick_up" && !pressed && (Run.Phase == "navigating" || Run.Phase == "recovering_navigation") &&
                    chef.GroundCollider != null && (Vector3.Distance(pickupOrigin, target.transform.position) > .02f || Vector3.Angle(pickupForward, target.transform.forward) > .5f) &&
                    ActionObservations.CanPickUpExact(chef, target, expectedPickup))
                {
                    // Native selection/range is authoritative at a moving pickup
                    // opportunity. A stationary stance relative to the moving
                    // receiver is not required. Recheck identity before the edge.
                    if (navigation.Action.State == "navigating") navigation.Stop("arrived");
                    if (!BeforePress()) return;
                    SetPhase("pressing");
                }
                if (command.Operation == "work_at" && (Run.Phase == "navigating" || Run.Phase == "recovering_navigation") && CanWorkHere())
                {
                    // Native selection can refresh one frame after placement.
                    if (navigation.Action.State == "navigating") navigation.Stop("arrived");
                    if (!BeforePress()) return;
                    SetPhase("working");
                }
                // The native selection/placement predicate can be satisfied a
                // fraction short of a conservative geometric station stance.
                // Once stopped, use that actual opportunity instead of looping
                // on alignment (as with immediate native work above).
                if (!command.GroundDrop && (command.Operation == "place_at" || command.Operation == "add_ingredient" ||
                    command.Operation == "collect_contents" || command.Operation == "discard") &&
                    (Run.Phase == "navigating" || Run.Phase == "recovering_navigation") &&
                    navigation.AligningAt(target) &&
                    chef.GroundCollider != null && !chef.m_bRespawning && chef.Motion.GetVelocityXZ().magnitude <= .1f &&
                    ActionObservations.CanPlace(chef, target))
                {
                    if (navigation.Action.State == "navigating") navigation.Stop("arrived");
                    if (!BeforePress()) return;
                    SetPhase("pressing");
                }
                if (recoveryReason == "target_unavailable" && Run.Phase != "navigating" &&
                    Run.Phase != "recovering_navigation" && Run.Phase != "pressing" && Run.Phase != "working") recovery.Clear();
                if (Run.Phase == "recovering_navigation")
                {
                    input(0);
                    if (held != null && !System.Object.ReferenceEquals(ActionObservations.Held(chef), held))
                    { Finish("blocked", "held_item_changed"); return; }
                    recovery.Wait(recoveryReason);
                    if (Time.realtimeSinceStartup < nextRecoveryAttempt) return;
                    nextRecoveryAttempt = Time.realtimeSinceStartup + .1f;
                    // Recovery has no usable route to retain. Give rebuilding
                    // a bounded recovery planning budget, rather than an idle
                    // catalogue slice that may repeatedly stop short of the goal.
                    string error = throwPlan == null ? navigation.Start(chef, Run.Owner, WalkDestination(), true) :
                        navigation.StartPosition(chef, Run.Owner, target, throwPlan.CurrentStance, true);
                    if (error != null) { RecoverNavigation(error); return; }
                    Run.Advance("navigating"); return;
                }
                if (Run.Phase == "planning_throw")
                {
                    input(0);
                    if (planningFrame == Time.frameCount) return;
                    if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), held)) { Finish("blocked", "held_item_changed"); return; }
                    string error = PlanThrow(Run.Owner);
                    if (error != null) Finish("blocked", error);
                    return;
                }
                if (Run.Phase == "navigating")
                {
                    if (command.Operation == "pick_up" && navigation.Action.State == "navigating" && navigation.Action.Destination != WalkDestination())
                    {
                        navigation.Stop("pickup_holder_changed");
                        string error = navigation.Start(chef, Run.Owner, WalkDestination(), true);
                        if (error != null) { RecoverNavigation(error); return; }
                    }
                    if (navigation.Action.State == "navigating") { recovery.Clear(); return; }
                    input(0);
                    if (navigation.Action.Reason != "arrived" && navigation.Action.Reason != "positioned_no_action")
                    { RecoverNavigation(navigation.Action.Reason); return; }
                    if (command.Operation == "throw_to" || command.Operation == "throw_to_player") { SetPhase("aiming"); return; }
                    if (!BeforePress()) return;
                    if (Run.Running) SetPhase(command.Operation == "work_at" ? "working" : "pressing");
                }
                if (Run.Phase == "waiting")
                {
                    input(0);
                    if (Condition()) Finish("succeeded", "condition_met");
                    else if ((command.Condition == "work_complete" || command.Condition == "cooked" || command.Condition == "mixed") && ActionObservations.Receiver(target) != receiver)
                        Finish("blocked", "station_contents_changed");
                    return;
                }
                if (Run.Phase == "seeking_ground_drop")
                {
                    if (navigation.Action.State == "navigating") return;
                    input(0);
                    SetPhase("positioning_ground_drop"); groundDropSeekAt = Time.realtimeSinceStartup + 1;
                }
                if (Run.Phase == "positioning_ground_drop")
                {
                    input(0);
                    if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), held)) { Finish("blocked", "held_item_changed"); return; }
                    if (Time.realtimeSinceStartup >= groundDropTurnAt)
                    { groundDropDirection = (groundDropDirection + 1) % 8; groundDropTurnAt = Time.realtimeSinceStartup + .3f; }
                    bool facing = true;
                    if (groundDropDirection >= 0)
                    {
                        float angle = groundDropDirection * Mathf.PI / 4;
                        if (groundDropAwayFrom != null)
                        {
                            Vector3 away = chef.transform.position - groundDropAwayFrom.transform.position;
                            // Try behind the chef's next pickup, then either
                            // side. Dropping into that route can block walking.
                            int offset = groundDropDirection % 2 == 0 ? -groundDropDirection / 2 : (groundDropDirection + 1) / 2;
                            angle = (float)Math.Atan2(away.x, away.z) + offset * Mathf.PI / 4;
                        }
                        Vector3 direction = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle));
                        PlayerControlsHelper.TurnTowardsDirection(chef.gameObject, direction, chef.Movement.TurnSpeed, Mathf.Min(Time.deltaTime, .05f));
                        facing = Vector3.Angle(chef.transform.forward, direction) <= .5f;
                        if (!facing) groundDropTurnAt = Time.realtimeSinceStartup + .3f;
                    }
                    if (!facing || chef.GroundCollider == null || chef.Motion.GetVelocityXZ().magnitude > .1f ||
                        !PlanGroundDrop())
                    {
                        // Some native counter selections persist while turning.
                        // Use a short checked walk to nearby open floor instead.
                        if (Time.realtimeSinceStartup >= groundDropSeekAt && SeekGroundDrop()) return;
                        WaitFor("waiting_for_ground_drop"); return;
                    }
                    recovery.Clear(); SetPhase("pressing");
                }
                if (Run.Phase == "drop_press")
                {
                    if (!pressed)
                    {
                        if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), held) || !drops.Plan(chef, held, target.GetComponent<PlayerControls>(), out dropPlan))
                        { Finish("blocked", "nearby_handoff_became_invalid"); return; }
                        pressed = true; pressFrame = Time.frameCount; phaseStarted = Time.realtimeSinceStartup; evidence = dropPlan.Describe();
                    }
                    if (Time.frameCount <= pressFrame + 1 && Time.realtimeSinceStartup - phaseStarted < .15f) { input(1); return; }
                    input(0); SetPhase("dropping");
                }
                if (Run.Phase == "dropping")
                {
                    input(0);
                    if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), held) && drops.Received(held, target.GetComponent<PlayerControls>(), dropPlan))
                    {
                        if (dropPlan.Counter != null || System.Object.ReferenceEquals(ActionObservations.Held(target.GetComponent<PlayerControls>()), held))
                        { Finish("succeeded", "item_handed_off_nearby"); return; }
                        if (dropStableSince < 0 || Vector3.Distance(lastDropPosition, held.transform.position) > .03f) dropStableSince = Time.realtimeSinceStartup;
                        lastDropPosition = held.transform.position;
                        if (Time.realtimeSinceStartup - dropStableSince >= .1f) { Finish("succeeded", "item_dropped_near_player"); return; }
                    }
                    else dropStableSince = -1;
                    if (Time.realtimeSinceStartup - phaseStarted > dropPlan.FlightSeconds + 2) Finish("failed", "nearby_handoff_not_observed");
                    return;
                }
                if (Run.Phase == "aiming") { Aim(); return; }
                if (Run.Phase == "throw_hold")
                {
                    if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), held)) { Finish("blocked", "held_item_changed"); return; }
                    if (Time.frameCount <= pressFrame + 1 || Time.realtimeSinceStartup - phaseStarted < .08f) { input(2); return; }
                    ThrowPlan fresh; string error = PrepareThrow();
                    if (error == null && throws.From(Foot(), true, out fresh)) { throwPlan = fresh; evidence = fresh.Describe(); }
                    else evidence = Json.Object("releaseRevalidation", error ?? "throw_became_invalid", "throw", throwPlan.Describe(), "diagnostics", throws.Diagnostics);
                    // Releasing an armed native use button launches the item,
                    // including during cleanup after a failed final proof. Keep
                    // observing the original cargo instead of retrying a throw
                    // whose input has already committed it.
                    throwReleased = true; input(0); SetPhase("flight"); return;
                }
                if (Run.Phase == "flight")
                {
                    input(0);
                    if (ThrowReceived()) { Finish("succeeded", "throw_received"); return; }
                    if (Time.realtimeSinceStartup - phaseStarted > throwPlan.FlightSeconds + 1.5f) Finish("failed", "throw_missed");
                    return;
                }
                if (Run.Phase == "working") { Work(); return; }
                if (Run.Phase == "pressing")
                {
                    if (!pressed)
                    {
                        if (!BeforePress()) return;
                        pressed = true; pressFrame = Time.frameCount; phaseStarted = Time.realtimeSinceStartup;
                    }
                    if (Time.frameCount <= pressFrame + 1 && Time.realtimeSinceStartup - phaseStarted < .15f) { input(Button()); return; }
                    input(0); SetPhase("verifying");
                }
                if (Run.Phase == "verifying")
                {
                    input(0);
                    if (Outcome()) { Finish("succeeded", "outcome_observed"); return; }
                    if (Time.realtimeSinceStartup - phaseStarted > 2)
                    {
                        // A local placement edge can be suppressed after arrival
                        // (e.g. a contact/selection change). Only retry when both
                        // authoritative sides prove that nothing was transferred.
                        var contents = held == null ? null : held.GetComponent<ServerIngredientContainer>();
                        if (command.Indefinite && command.Operation == "place_at" && held != null &&
                            System.Object.ReferenceEquals(ActionObservations.Held(chef), held) &&
                            System.Object.ReferenceEquals(ActionObservations.Occupant(target), initialOccupant) &&
                            (contents == null ? initialContents == 0 : contents.GetContentsCount() == initialContents) &&
                            ActionObservations.DirtyCount(target) == initialDirty && ActionObservations.StackCount(ActionObservations.Receiver(target)) == initialStack)
                        { pressed = false; Finish("blocked", "placement_not_applied"); }
                        else Finish("failed", "outcome_not_observed");
                    }
                }
            }
            catch (Exception) { Finish("failed", "observation_unavailable"); }
        }
        private void SetPhase(string phase) { Run.Advance(phase); phaseStarted = Time.realtimeSinceStartup; }
        private string recoveryReason;
        private bool WaitFor(string reason)
        {
            input(0); recoveryReason = reason;
            if (recovery.Wait(reason)) return true;
            Finish("blocked", reason); return false;
        }
        private bool RecoverNavigation(string reason)
        {
            if (!WaitFor(reason)) return false;
            Run.Advance("recovering_navigation"); return true;
        }
        private int Button()
        {
            if (command.Operation != "activate") return 1;
            var interact = target.GetComponent<ClientInteractable>(); return interact != null && interact.UsePlacementButton ? 1 : 2;
        }
        private bool CanWorkHere()
        {
            return chef.GroundCollider != null && !chef.m_bRespawning && chef.Motion.GetVelocityXZ().magnitude <= .1f &&
                Vector3.Distance(chef.transform.position, target.transform.position) <= 1.6f && ActionObservations.CanUse(chef, target);
        }
        private bool BeforePress()
        {
            string op = command.Operation;
            if (command.GroundDrop)
            {
                if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), held)) { Finish("blocked", "held_item_changed"); return false; }
                if (!PlanGroundDrop()) { input(0); SetPhase("positioning_ground_drop"); return false; }
                return true;
            }
            if (op == "activate" && command.DesiredState != "trigger" && ActionObservations.ToggleState(target) == (command.DesiredState == "on"))
            { Finish("succeeded", "already_in_desired_state"); return false; }
            if (op == "work_at" && Condition()) { Finish("succeeded", "condition_met"); return false; }
            if (op == "pick_up")
            {
                if (ActionObservations.Held(chef) != null) { Finish("blocked", "hands_full"); return false; }
                var stack = ActionObservations.Receiver(target).GetComponent<ServerStack>();
                if (expectedTopPlate != null && (stack == null || !System.Object.ReferenceEquals(stack.InspectTopOfStack(), expectedTopPlate)))
                { Finish("blocked", "pickup_target_changed"); return false; }
                if (initialOccupant != ActionObservations.Occupant(target))
                { Finish("blocked", "pickup_target_changed"); return false; }
                var supply = target.GetComponent<PickupItemSpawner>();
                if (supply != null && !System.Object.ReferenceEquals(supply.m_itemPrefab, pickupPrefab))
                { Finish("blocked", "pickup_target_changed"); return false; }
                if (!ActionObservations.CanPickUpExact(chef, target, expectedPickup)) { WaitFor("pickup_unavailable"); return false; }
            }
            else if (op == "work_at" || op == "activate")
            {
                if (ActionObservations.Held(chef) != null) { Finish("blocked", "hands_full"); return false; }
                if (!ActionObservations.CanUse(chef, target)) { WaitFor("interaction_unavailable"); return false; }
                var client = target.GetComponent<ClientInteractable>();
                if (op == "activate" && client.UsePlacementButton && ActionObservations.CanPickUp(chef, target))
                { Finish("blocked", "pickup_would_take_precedence"); return false; }
                Subscribe();
            }
            else
            {
                if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), held)) { Finish("blocked", "held_item_changed"); return false; }
                if (op == "add_ingredient" && ActionObservations.Container(target) != container) { Finish("blocked", "container_changed"); return false; }
                if (op == "collect_contents")
                {
                    if (ActionObservations.Container(target) != container) { Finish("blocked", "container_changed"); return false; }
                    if (!TaskNative.ReadyAt(target)) { WaitFor("processing_not_ready"); return false; }
                }
                if (op == "place_at" && initialOccupant != ActionObservations.Occupant(target)) { Finish("blocked", "station_contents_changed"); return false; }
                if (op == "place_at" && initialOccupant != null && initialStack == 0) { Finish("blocked", "station_occupied_use_add_ingredient"); return false; }
                if (!ActionObservations.CanPlace(chef, target)) { WaitFor("placement_unavailable"); return false; }
            }
            recovery.Clear();
            return true;
        }
        private void Subscribe()
        {
            if (interactable != null) return;
            interactable = target.GetComponent<ServerInteractable>();
            if (interactable == null) return;
            triggered = delegate(GameObject actor, Vector2 direction) { if (chef != null && actor == chef.gameObject) triggeredByChef = true; };
            interactable.RegisterTriggerCallbacks(triggered);
        }
        private bool Outcome()
        {
            string op = command.Operation; GameObject carrying = ActionObservations.Held(chef);
            if (op == "pick_up")
            {
                if (carrying == null) return false;
                bool matches = carrying == expectedPickup;
                if (!matches && expectedTopPlate != null) matches = System.Object.ReferenceEquals(carrying, expectedTopPlate);
                if (matches) { evidence = ItemEvidence(carrying); return true; }
                if (target == null) return false;
                var supply = target.GetComponent<PickupItemSpawner>();
                if (!matches && supply != null)
                {
                    AssembledDefinitionNode expected = ActionObservations.Composition(pickupPrefab), actual = ActionObservations.Composition(carrying);
                    matches = expected != null && actual != null && AssembledDefinitionNode.Matching(expected, actual);
                    if (!matches)
                    {
                        string prefabId, prefabName, carriedId, carriedName;
                        matches = LogisticsInventory.IngredientIdentity(pickupPrefab, out prefabId, out prefabName) &&
                            LogisticsInventory.IngredientIdentity(carrying, out carriedId, out carriedName) && prefabId == carriedId;
                    }
                }
                if (matches) evidence = ItemEvidence(carrying); return matches;
            }
            if (op == "add_ingredient")
            {
                bool added = container != null && ActionObservations.Container(target) == container &&
                    ActionObservations.MatchingCount(container, ingredient) > initialMatches;
                if (added) evidence = Json.Object("containerInstanceId", container.gameObject.GetInstanceID(), "matchingIngredients", ActionObservations.MatchingCount(container, ingredient));
                return added && !System.Object.ReferenceEquals(carrying, held);
            }
            if (op == "collect_contents")
            {
                var recipient = held == null ? null : held.GetComponent<ServerIngredientContainer>();
                bool collected = System.Object.ReferenceEquals(carrying, held) && recipient != null && recipient.GetContentsCount() > initialContents &&
                    container != null && ActionObservations.Container(target) == container && container.GetContentsCount() < initialSourceContents;
                if (collected) evidence = Json.Object("sourceRemaining", container.GetContentsCount(), "recipientCount", recipient.GetContentsCount(), "heldItem", ItemEvidence(held));
                return collected;
            }
            if (op == "place_at")
            {
                if (command.GroundDrop)
                {
                    bool dropped = !System.Object.ReferenceEquals(carrying, held) && drops.Received(held, chef, dropPlan);
                    if (dropped) evidence = Json.Object("groundDrop", true, "item", ItemEvidence(held));
                    return dropped;
                }
                // A native serving window takes its plate back off the holder
                // immediately. Its delivery message pins the original plate;
                // empty hands or somebody else's score cannot prove our serve.
                GameObject delivered = DeliveredPlate(target);
                if (!System.Object.ReferenceEquals(carrying, held) && !System.Object.ReferenceEquals(delivered, initialServedPlate) &&
                    System.Object.ReferenceEquals(delivered, held))
                { evidence = Json.Object("served", true, "targetInstanceId", target.GetInstanceID(), "item", ItemEvidence(held)); return true; }
                bool placed = !System.Object.ReferenceEquals(carrying, held) && ((held != null && System.Object.ReferenceEquals(ActionObservations.Occupant(target), held)) ||
                    (initialDirty >= 0 && ActionObservations.DirtyCount(target) > initialDirty) ||
                    (initialStack > 0 && ActionObservations.StackCount(ActionObservations.Receiver(target)) > initialStack));
                if (placed) evidence = Json.Object("targetInstanceId", target.GetInstanceID(), "heldItem", ItemEvidence(carrying));
                return placed;
            }
            if (op == "discard")
            {
                var contents = held == null ? null : held.GetComponent<ServerIngredientContainer>();
                bool disposed = held == null || !held.activeInHierarchy ||
                    (initialContents > 0 && contents != null && contents.GetContentsCount() == 0);
                if (disposed) evidence = Json.Object("disposed", true, "heldItem", ItemEvidence(carrying)); return disposed;
            }
            if (op == "activate")
            {
                bool done = command.DesiredState == "trigger" ? triggeredByChef : ActionObservations.ToggleState(target) == (command.DesiredState == "on");
                if (done) evidence = Json.Object("nativeTriggerObserved", triggeredByChef, "desiredState", command.DesiredState); return done;
            }
            return false;
        }
        private static GameObject DeliveredPlate(GameObject station)
        {
            var server = station == null ? null : station.GetComponent<ServerPlateStation>();
            if (server == null) return null;
            var message = ActionObservations.Read<PlateStationMessage>(server, "m_data");
            return message != null && message.m_success ? message.m_delivered : null;
        }
        private bool PlanGroundDrop()
        {
            if (!drops.Plan(chef, held, chef, out dropPlan, true)) return false;
            if (groundDropAwayFrom == null) return true;
            Vector3 toward = groundDropAwayFrom.transform.position - chef.transform.position; toward.y = 0;
            Vector3 landing = dropPlan.Landing - chef.transform.position; landing.y = 0;
            return Vector3.Dot(landing, toward) <= .05f * toward.magnitude;
        }
        private bool SeekGroundDrop()
        {
            groundDropSeekAt = Time.realtimeSinceStartup + .2f;
            if (groundDropStance >= 16) return false;
            int side = groundDropStance % 8;
            int offset = side % 2 == 0 ? -side / 2 : (side + 1) / 2;
            Vector3 away = groundDropAwayFrom == null ? chef.transform.forward : groundDropOrigin - groundDropAwayFrom.transform.position;
            float angle = (float)Math.Atan2(away.x, away.z) + offset * Mathf.PI / 4;
            Vector3 seed = groundDropOrigin + new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * (groundDropStance < 8 ? 1.2f : 2);
            string error = navigation.StartPosition(chef, Run.Owner, groundDropAwayFrom ?? held, seed, true);
            if (error != "planning_in_progress" && error != "planning_budget_exceeded") groundDropStance++;
            if (error != null) return false;
            SetPhase("seeking_ground_drop"); return true;
        }
        private bool Condition()
        {
            string condition = command.Condition;
            if (condition == "work_complete")
            {
                if (workable != null && workable.HasFinished()) return true;
                var actual = ActionObservations.Composition(ActionObservations.Receiver(target));
                return expectedOutput != null && actual != null && AssembledDefinitionNode.Matching(expectedOutput, actual);
            }
            if (condition == "sink_empty") return ActionObservations.DirtyCount(target) == 0;
            if (condition == "one_plate_clean") return ActionObservations.DirtyCount(target) < initialDirty;
            if (condition == "occupied" || condition == "empty")
            {
                bool occupied;
                if (target.GetComponent<ServerWashingStation>() != null) occupied = ActionObservations.DirtyCount(target) > 0;
                else if (target.GetComponent<ServerAttachStation>() != null) occupied = ActionObservations.Occupant(target) != null;
                else if (target.GetComponent<ServerIngredientContainer>() != null) occupied = target.GetComponent<ServerIngredientContainer>().GetContentsCount() > 0;
                else occupied = ActionObservations.StackCount(target) > 0;
                return condition == "occupied" ? occupied : !occupied;
            }
            if (condition == "ready") return ActionObservations.CanPickUp(chef, target) || ActionObservations.CanPlace(chef, target) || ActionObservations.CanUse(chef, target);
            if (condition == "reachable")
            {
                var catalog = (Json.Record)navigation.Available(chef);
                foreach (Json.Record d in (List<object>)catalog["destinations"]) if ((string)d["id"] == command.Destination) return true;
                return false;
            }
            if (condition == "cooked")
            {
                if (ActionObservations.Receiver(target) != receiver) return false;
                var cooking = receiver.GetComponent<ClientCookingHandler>();
                return cooking != null && ActionObservations.Read<object>(cooking, "m_cachedCookingState").ToString() == "Completed";
            }
            if (condition == "mixed")
            {
                if (ActionObservations.Receiver(target) != receiver) return false;
                var mixing = receiver.GetComponent<ClientMixingHandler>();
                return mixing != null && ActionObservations.Read<object>(mixing, "m_cachedMixingState").ToString() == "Completed";
            }
            return false;
        }
        private void Work()
        {
            if (Condition())
            {
                evidence = Json.Object("condition", command.Condition, "dirtyPlatesBefore", initialDirty,
                    "dirtyPlatesAfter", ActionObservations.DirtyCount(target), "cleanPlatesBefore", initialClean, "cleanPlatesAfter", ActionObservations.CleanCount(target));
                Finish("succeeded", "condition_met"); return;
            }
            if (ActionObservations.Held(chef) != null) { Finish("blocked", "hands_full"); return; }
            if (command.Condition == "work_complete" && (workable == null || ActionObservations.Receiver(target) != workable.gameObject)) { Finish("blocked", "work_item_changed"); return; }
            if (!ActionObservations.CanUse(chef, target)) { WaitFor("interaction_unavailable"); return; }
            recovery.Clear();
            float progress = workable != null ? workable.GetProgress() + ActionObservations.Read<int>(workable, "m_subProgress") * .001f :
                ActionObservations.Read<float>(target.GetComponent<ServerWashingStation>(), "m_cleaningTimer") - ActionObservations.DirtyCount(target);
            if (!pressed || progress != lastProgress) { lastProgressTime = Time.realtimeSinceStartup; lastProgress = progress; }
            if (Time.realtimeSinceStartup - lastProgressTime > 5) { Finish("failed", "no_work_progress"); return; }
            pressed = true; input(2);
        }
        private Vector3 Foot()
        { return chef.transform.position - Vector3.up * (chef.transform.position.y - chef.GetComponent<CapsuleCollider>().bounds.min.y); }
        private string PrepareThrow()
        {
            string error = throws.Prepare(chef, held, target, groundHandoff: command.GroundHandoff);
            return error ?? (throwPlan == null || throws.RestoreGround(throwPlan) ? null : "ground_landing_became_invalid");
        }
        private void Aim()
        {
            input(0);
            if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), held)) { Finish("blocked", "held_item_changed"); return; }
            Vector3 supported;
            if (!navigation.TryStance(Foot(), out supported) || Vector3.Distance(supported, Foot()) > .04f)
            { Finish("blocked", "unsafe_release_stance"); return; }
            string error = PrepareThrow(); ThrowPlan current;
            if (error != null || !throws.From(Foot(), false, out current)) { evidence = throws.Diagnostics; Finish("blocked", error ?? "throw_became_invalid"); return; }
            PlayerControlsHelper.TurnTowardsDirection(chef.gameObject, current.Direction, chef.Movement.TurnSpeed, Mathf.Min(Time.deltaTime, .05f));
            if (chef.Motion.GetVelocityXZ().magnitude > .1f || Vector3.Angle(chef.transform.forward, current.Direction) > .5f)
            { if (Time.realtimeSinceStartup - phaseStarted > 2) Finish("failed", "throw_alignment_failed"); return; }
            if (!throws.From(Foot(), true, out current)) return;
            throwPlan = current; evidence = current.Describe(); SetPhase("throw_hold"); pressFrame = Time.frameCount; pressed = true; input(2);
        }
        private bool ThrowReceived()
        {
            var player = target.GetComponent<PlayerControls>();
            bool received = player != null ? held != null && System.Object.ReferenceEquals(ActionObservations.Held(player), held) :
                (held != null && ActionObservations.Occupant(target) == held);
            if (!received && player != null && throwPlan.Ground && drops.Received(held, player, null, throwPlan.GroundSupport))
            {
                Vector3 position = throwPlan.GroundSupport == null ? held.transform.position : throwPlan.GroundSupport.transform.InverseTransformPoint(held.transform.position);
                if (dropStableSince < 0 || Vector3.Distance(lastDropPosition, position) > .03f)
                { dropStableSince = Time.realtimeSinceStartup; lastDropPosition = position; }
                received = Time.realtimeSinceStartup - dropStableSince >= .1f &&
                    (throws.GroundAccess == null || throws.GroundAccess.CanCollect(player, held));
            }
            else if (!received) dropStableSince = -1;
            if (!received && container != null && ingredient != null)
                received = ActionObservations.MatchingCount(container, ingredient) > initialMatches && (held == null || !held.activeInHierarchy);
            if (received) evidence = Json.Object("throw", throwPlan.Describe(), "receivedByInstanceId", target.GetInstanceID(), "item", ItemEvidence(held));
            return received;
        }
        private static object ItemEvidence(GameObject item)
        { return item == null ? null : Json.Object("objectInstanceId", item.GetInstanceID(), "label", item.name); }
        public void Cancel(string reason) { if (Run.Running) Finish("cancelled", reason); }
        private void Finish(string state, string reason)
        {
            // Ending a sticky interaction uses the game's own ordinary end-event;
            // no progress, item ownership, position or station state is forced.
            try
            {
                if (chef != null && pressed && (command.Operation == "work_at" || command.Operation == "activate"))
                    ActionObservations.EndOwnedInteraction(chef, target);
            }
            catch (Exception) { state = "failed"; reason = "interaction_cleanup_failed"; }
            if (interactable != null && triggered != null) interactable.UnregisterTriggerCallbacks(triggered);
            interactable = null; triggered = null;
            if (launchObserver != null && launchCallback != null) launchObserver.UnregisterThrowCallback(launchCallback);
            launchObserver = null; launchCallback = null; throws.CancelPlanning();
            if (reason == "unsafe_release_stance" && throwPlan != null && throwPlan.Ground && throws.GroundAccess != null)
                throws.GroundAccess.Reject(throwPlan, target.GetComponent<PlayerControls>());
            if (throwReleased && state != "succeeded") evidence = Json.Object("throwReleased", true, "throw", throwPlan.Describe());
            if (dropPlan != null && pressed && state != "succeeded") evidence = Json.Object("handoffRequested", true, "handoff", dropPlan.Describe());
            // Cancel the child while the parent still owns the input lease.
            if (navigation.Action.State == "navigating") navigation.Stop(reason);
            Run.Finish(state, reason); release();
        }
    }
}
