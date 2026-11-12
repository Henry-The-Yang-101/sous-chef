using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using SousChef.Plugin.Actions;
using UnityEngine;

namespace SousChef.Plugin.Control
{
    // Replace only the existing P2 chef's logical inputs. No OS events, new
    // players, global lookup patches, menu bindings or P1 fields are modified.
    internal sealed partial class Player2Control : IDisposable
    {
        private readonly NavigationActions navigation;
        private InteractionActions interactions;
        private LogisticsActions logistics;
        private readonly ThrowOptions throwOptions;
        private TaskActions tasks;
        private bool lastWasTask;
        private readonly ReceiptBook receipts = new ReceiptBook();
        private ReceiptBook.Entry activeReceipt;
        private string kitchenEpoch = Guid.NewGuid().ToString("N");
        private long dispatchGeneration;
        private long roundSequence;
        private bool localRound;
        private string receiptOperation;
        private void ChangeEpoch()
        {
            CaptureReceipt(); activeReceipt = null; receipts.Clear();
            kitchenEpoch = Guid.NewGuid().ToString("N"); dispatchGeneration++;
        }
        private void CaptureReceipt()
        {
            if (activeReceipt == null) return;
            string state, reason; object evidence;
            if (receiptOperation == "navigate_to")
            {
                reason = navigation.Action.Reason;
                state = navigation.Action.State == "navigating" ? (navigation.MovementWaiting != null ? "waiting" : "running") :
                    reason == "arrived" || reason == "positioned_no_action" ? "succeeded" : reason == "stopped" ? "cancelled" : "failed";
                if (navigation.MovementWaiting != null) reason = navigation.MovementWaiting;
                evidence = navigation.Describe();
            }
            else
            {
                ActionRun run = receiptOperation == "task" ? tasks.Run : LogisticsActions.Handles(receiptOperation) ? logistics.Run : interactions.Run;
                state = run.State; reason = run.Reason;
                var description = (Json.Record)(receiptOperation == "task" ? tasks.Describe() : run.Describe(null));
                evidence = Json.Object("requestId", run.RequestId, "phase", run.Phase, "revision", run.Revision);
                if (receiptOperation == "task")
                {
                    var details = (Json.Record)description["details"];
                    ((Json.Record)evidence)["completedRepetitions"] = details["completedRepetitions"];
                    ((Json.Record)evidence)["completedSteps"] = details["completedSteps"];
                    if (state == "running" && details["waitingReason"] != null) { state = "waiting"; reason = (string)details["waitingReason"]; }
                }
            }
            if ((state == "running" || state == "waiting") && suspendedAt >= 0) { state = "suspended"; reason = unavailable; }
            activeReceipt.State = state; activeReceipt.Reason = reason; activeReceipt.Evidence = evidence;
            if (activeReceipt.Terminal) activeReceipt = null;
        }
        private readonly SemanticCatalog semanticCatalog;
        private bool lastWasLogistics;
        private long navigationSequence;
        private readonly ControlLease lease = new ControlLease();
        private readonly ControlServer server;
        private readonly Harmony focusPatch = new Harmony("dev.souschef.autonomous-input");
        private static Player2Control focusOwner;
        private static PlayerControls focusProbe;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private PlayerControls target;
        private readonly List<PlayerControls> localTeammates = new List<PlayerControls>(3);
        private Component flow;
        private GameInputConfig.ConfigEntry config;
        private PlayerControls.ControlSchemeData scheme;
        private ILogicalValue oldX, oldY, bridgeX, bridgeY;
        private ILogicalButton oldPickup, oldUse, oldDash, bridgePickup, bridgeUse, bridgeDash;
        private double nextScan;
        private string unavailable = "player2_absent";
        private bool disposed;
        private float suspendedAt = -1;
        private bool Autonomous { get { return navigation.Action.State == "navigating" || interactions.Run.Running || logistics.Run.Running || tasks.Run.Running; } }
        private bool PreservingSuspension(string reason)
        {
            return Autonomous && (reason == "paused" || reason == "game_unfocused" || reason == "input_suppressed");
        }
        private readonly bool traceInput = Environment.GetEnvironmentVariable("SOUS_CHEF_NAV_TRACE") == "1";
        private readonly System.Collections.Generic.Queue<object> inputTrace = new System.Collections.Generic.Queue<object>();
        private Vector3 lastWalk;
        private double lastWalkTime, lastWalkDeadline;
        private int lastExpiryFrame = -1, lastWalkFrame;
        private double Now { get { return clock.Elapsed.TotalSeconds; } }

        public Player2Control(int port, string session, int? voicePort)
        {
            navigation = new NavigationActions(Walk, NeutralNavigation, localTeammates);
            interactions = new InteractionActions(navigation, Buttons, FinishInteraction);
            logistics = new LogisticsActions(navigation, interactions, Buttons, FinishLogistics);
            tasks = new TaskActions(navigation, interactions, Buttons, FinishLogistics);
            throwOptions = new ThrowOptions(navigation);
            semanticCatalog = new SemanticCatalog(navigation);
            server = new ControlServer(port, Json.Object("type", "hello", "controlProtocolVersion", 1,
                "sessionId", session, "bridgeVersion", BridgePlugin.Version, "player", 2, "mode", "direct_logical_input", "maxHoldMs", 1000, "actions", ActionRun.AllNames, "capabilities", CapabilityRegistry.Describe(), "receiptProtocolVersion", 1, "voicePort", voicePort),
                delegate(int client) { lease.Disconnect(client); });
            try
            {
                focusPatch.Patch(AccessTools.Method(typeof(PlayerControls), nameof(PlayerControls.CanButtonBePressed)),
                    transpiler: new HarmonyMethod(typeof(Player2Control), nameof(PatchFocusCheck)));
                focusOwner = this;
                server.Start();
            }
            catch { Dispose(); throw; }
        }

        private static bool InputFocused
        {
            get { return !NativeSimulator.BlockInputFocus && !NativeSimulator.SimulateUnfocused && Application.isFocused; }
        }

        // Replace just the native focus test. Menus, dialogs and direct-control
        // suppression still run in the original CanButtonBePressed method.
        private static IEnumerable<CodeInstruction> PatchFocusCheck(IEnumerable<CodeInstruction> instructions)
        {
            var focused = AccessTools.PropertyGetter(typeof(Application), nameof(Application.isFocused));
            var allowsInput = AccessTools.Method(typeof(Player2Control), nameof(FocusAllowsInput));
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, focused))
                {
                    instruction.opcode = OpCodes.Ldarg_0;
                    instruction.operand = null;
                    yield return instruction;
                    yield return new CodeInstruction(OpCodes.Call, allowsInput);
                }
                else yield return instruction;
            }
        }

        private static bool FocusAllowsInput(PlayerControls chef)
        {
            if (ReferenceEquals(chef, focusProbe)) return true;
            return InputFocused || (!NativeSimulator.BlockInputFocus && focusOwner != null &&
                ReferenceEquals(chef, focusOwner.target) && focusOwner.Autonomous && focusOwner.HasLiveBindings());
        }

        private bool CanAutomate()
        {
            // This probe is synchronous on Unity's thread. Outside the probe,
            // only the chef currently bound to an autonomous job can bypass focus.
            focusProbe = target;
            try { return target.CanButtonBePressed(); }
            finally { focusProbe = null; }
        }

        public void Tick()
        {
            if (disposed) return;
            KitchenGate gate = KitchenGate.Inspect();
            bool inLocalRound = gate.InRound && gate.Role == "host";
            if (inLocalRound && (!localRound || flow != gate.Flow)) roundSequence++;
            localRound = inLocalRound;
            if (flow != gate.Flow) { Revoke("round_changed"); logistics.Reset("round_changed"); tasks.Reset("round_changed"); throwOptions.Reset(); semanticCatalog.Reset(); navigation.Reset("round_changed"); flow = gate.Flow; ChangeEpoch(); nextScan = 0; }
            if (Now >= nextScan) { Discover(); nextScan = Now + 0.25; }
            if (scheme != null && !Autonomous && !InputFocused) Revoke("game_unfocused");
            string reason = Check(gate);
            bool preservingSuspension = PreservingSuspension(reason);
            if (preservingSuspension)
            {
                if (suspendedAt < 0) suspendedAt = Time.realtimeSinceStartup;
                lease.Suspend(Now);
            }
            else if (suspendedAt >= 0)
            {
                float seconds = Time.realtimeSinceStartup - suspendedAt; suspendedAt = -1;
                navigation.ResumeAfterPause(seconds); interactions.ResumeAfterPause(seconds);
                logistics.ResumeAfterPause(seconds); tasks.ResumeAfterPause(seconds); lease.Resume(Now);
            }
            bool observationAllowed = reason == null || (reason == "game_unfocused" && Check(gate, true) == null);
            if (reason != null && !preservingSuspension)
            {
                if (!observationAllowed) navigation.Invalidate(reason);
                Revoke(reason);
            }
            else if (!preservingSuspension && lease.Owner(Now) == 0) Restore();
            unavailable = reason;
            if (navigation.Action.State == "navigating" && lease.Owner(Now) != navigation.Action.Owner) navigation.Stop("control_lost");
            if (interactions.Run.Running && lease.Owner(Now) != interactions.Run.Owner) interactions.Cancel("control_lost");
            if (logistics.Run.Running && lease.Owner(Now) != logistics.Run.Owner) logistics.Cancel("control_lost");
            if (tasks.Run.Running && lease.Owner(Now) != tasks.Run.Owner) tasks.Cancel("control_lost");
            server.Pump(Handle);
            CompleteSuspension();
            if (reason == null)
                foreach (var saved in suspendedTasks.Values) saved.Task.ObserveSuspended();
            if (reason == null) { navigation.Tick(target); if (tasks.Run.Running) tasks.TickChild(); else interactions.Tick(); logistics.Tick(); tasks.Tick(); }
            else if (observationAllowed) navigation.WarmCatalogue(target);
            if (observationAllowed) navigation.WarmGroundedMap(target);
            // The browser may be in front while the catalogue is observed.
            // Advance read-only previews under the observation gates; never
            // advance input-owning actions without the normal input gates.
            if (observationAllowed &&
                lease.Owner(Now) == 0 && navigation.Action.State == "idle" && !interactions.Run.Running && !logistics.Run.Running && !tasks.Run.Running)
                throwOptions.Tick(target);
            else throwOptions.Reset();
        }

        private void Discover()
        {
            PlayerControls found = null; int matches = 0;
            var inputConfig = PlayerInputLookup.GetInputConfig();
            localTeammates.Clear();
            foreach (var candidate in Resources.FindObjectsOfTypeAll<PlayerControls>())
            {
                if (!candidate.gameObject.scene.IsValid() || !candidate.gameObject.scene.isLoaded || !candidate.gameObject.activeInHierarchy) continue;
                var provider = candidate.GetComponent<PlayerIDProvider>();
                if (provider == null) continue;
                var id = provider.GetID();
                if (id == PlayerInputLookup.Player.Two) { found = candidate; matches++; }
                else
                {
                    var teammate = inputConfig == null ? null : inputConfig.GetInputConfigEntry(id);
                    if (teammate != null && teammate.IsLocal()) localTeammates.Add(candidate);
                }
            }
            if (matches != 1) found = null; // Never guess between duplicate IDs.
            var entry = inputConfig == null ? null : inputConfig.GetInputConfigEntry(PlayerInputLookup.Player.Two);
            if (target != found || config != entry || (scheme != null && (found == null || found.ControlScheme != scheme)))
                { Revoke("player_assignment_changed"); navigation.Reset("player_assignment_changed"); semanticCatalog.Reset(); ChangeEpoch(); }
            target = found; config = entry;
        }

        private string Check(KitchenGate gate, bool observation = false)
        {
            if (!gate.InRound || gate.Role != "host") return "outside_local_round";
            if (TimeManager.IsPaused(TimeManager.PauseLayer.Main)) return "paused";
            if (target == null) return "player2_absent_or_ambiguous";
            var provider = target.GetComponent<PlayerIDProvider>();
            if (provider == null || provider.GetID() != PlayerInputLookup.Player.Two || config == null || !config.IsLocal()) return "player2_not_local";
            if ((!target.enabled && !navigation.NativeTeleportInProgress(target)) || !target.gameObject.activeInHierarchy || target.ControlScheme == null ||
                target.ControlScheme.Player != PlayerInputLookup.Player.Two) return "player2_not_ready";
            if (target.m_bRespawning) return "input_suppressed";
            if (!observation && NativeSimulator.BlockInputFocus) return "game_unfocused";
            if (!CanAutomate()) return "input_suppressed";
            if (scheme != null && !OwnsBindings()) return "bindings_changed";
            return null;
        }

        // Recheck native gates at the actual read, including between Update and
        // FixedUpdate. Timeout/disconnect neutralizes values without a Unity call.
        private bool HasLiveBindings()
        {
            var serverFlow = flow as ServerFlowControllerBase;
            return !disposed && target != null && target.enabled && target.gameObject.activeInHierarchy && scheme != null &&
                OwnsBindings() && target.PlayerIDProvider != null &&
                target.PlayerIDProvider.GetID() == PlayerInputLookup.Player.Two &&
                serverFlow != null && serverFlow.InRound && !TimeManager.IsPaused(TimeManager.PauseLayer.Main) &&
                !target.m_bRespawning;
        }
        private ControlInput Input()
        {
            bool allowed = HasLiveBindings() && target.CanButtonBePressed(); double now = Now;
            ControlInput value = allowed ? lease.Read(now) : new ControlInput();
            if (traceInput && navigation.Action.State == "navigating" && lastWalk.sqrMagnitude > .001f &&
                value.X == 0 && value.Y == 0 && Time.frameCount != lastExpiryFrame)
            {
                lastExpiryFrame = Time.frameCount;
                inputTrace.Enqueue(Json.Object("time", Time.realtimeSinceStartup, "frame", Time.frameCount,
                    "reason", allowed && now >= lastWalkDeadline ? "movement_expired" : "read_suppressed",
                    "commandFrame", lastWalkFrame, "commandAgeSeconds", now - lastWalkTime,
                    "checkedSeconds", lastWalkDeadline - lastWalkTime, "input", Values.Vector(lastWalk)));
                while (inputTrace.Count > 64) inputTrace.Dequeue();
            }
            return value;
        }

        private object Handle(int client, ControlCommand command)
        {
            CaptureReceipt();
            string error = null;
            object actions = null;
            ReceiptBook.Entry receipt = null;
            if (command.Operation == "receipt")
            {
                receipt = receipts.Find(command.ReceiptId);
                return Json.Object("type", "reply", "id", command.Id, "ok", true, "control", Describe(client), "receipt", receipt == null ? null : receipt.Describe());
            }
            if (command.Fenced)
            {
                // A retransmission never reacquires input or repeats physical effects.
                receipt = receipts.Find(command.Id);
                if (receipt != null)
                    return Json.Object("type", "reply", "id", command.Id, "ok", receipt.Payload == command.Payload,
                        "error", receipt.Payload == command.Payload ? null : "request_id_conflict", "control", Describe(client), "receipt", receipt.Describe());
                Discover();
                receipt = receipts.Add(command.Id, command.Payload, kitchenEpoch);
                if (command.Epoch != kitchenEpoch || command.DispatchGeneration != dispatchGeneration)
                {
                    receipt.State = "rejected"; receipt.Reason = "stale_dispatch_context";
                    return Json.Object("type", "reply", "id", command.Id, "ok", true, "control", Describe(client), "receipt", receipt.Describe());
                }
            }
            if (command.Operation == "stop" || command.Operation == "cancel" || command.Operation == "cancel_current")
            {
                dispatchGeneration++; Revoke("stopped", command.Operation == "cancel_current"); navigation.Stop("stopped"); CaptureReceipt();
                if (receipt != null) { receipt.State = "succeeded"; receipt.Reason = "stopped"; }
            }
            else if (command.Operation == "suspend" || command.Operation == "resume" || command.Operation == "forget_task")
            {
                HandleTaskSuspension(client, command, receipt);
            }
            else if (command.Operation == "actions")
            {
                Discover(); KitchenGate gate = KitchenGate.Inspect(); unavailable = Check(gate);
                string observationError = Check(gate, true);
                if (observationError != null) { if (!PreservingSuspension(observationError)) navigation.Invalidate(observationError); actions = Json.Object("actions", new[] { Json.Object("name", "stop") }, "destinations", new object[0], "navigation", navigation.Describe()); }
                else if (lease.Owner(Now) != 0 && navigation.Action.State == "idle" && !interactions.Run.Running && !logistics.Run.Running && !tasks.Run.Running)
                    actions = Json.Object("actions", new[] { Json.Object("name", "stop") }, "destinations", new object[0], "reason", "already_claimed", "navigation", navigation.Describe());
                else actions = Catalog();
                var observed = (Json.Record)actions;
                observed["capabilities"] = CapabilityRegistry.Describe();
                // Structural observations remain available through suspension and
                // ownership. Invalid round/chef contexts publish no old candidates.
                if (gate.InRound && gate.Role == "host" && target != null && config != null && config.IsLocal())
                {
                    try { observed["semanticCatalog"] = semanticCatalog.Capture(target, DescribePlayers(false)); }
                    catch (Exception) { observed["semanticCatalog"] = Json.Object("version", CapabilityRegistry.Version, "error", "semantic_observation_unavailable"); }
                }
            }
            else if (command.Operation == "task" || command.Operation == "navigate_to" || InteractionActions.Handles(command.Operation) || LogisticsActions.Handles(command.Operation))
            {
                Discover(); unavailable = Check(KitchenGate.Inspect());
                if (unavailable != null) error = unavailable;
                else if (navigation.Action.State != "idle" || interactions.Run.Running || logistics.Run.Running || tasks.Run.Running) error = "action_in_progress";
                else if (!lease.Claim(client, Now, true)) error = "already_claimed";
                else
                {
                    Restore(); Install(); navigationSequence = 0;
                    bool requestedLogistics = LogisticsActions.Handles(command.Operation);
                    long priorRevision = command.Operation == "task" ? tasks.Run.Revision : requestedLogistics ? logistics.Run.Revision : interactions.Run.Revision;
                    if (command.Operation == "navigate_to")
                    {
                        if (!NativeSimulator.StartPortalPoint(navigation, target, client, command.Destination, out error))
                            error = navigation.StartGrounded(target, client, command.Destination);
                    }
                    else if (command.Operation == "task") error = tasks.Start(target, client, command);
                    else error = requestedLogistics ? logistics.Start(target, client, command) : interactions.Start(target, client, command);
                    if (error == null || (command.Operation == "task" ? tasks.Run.Revision : requestedLogistics ? logistics.Run.Revision : interactions.Run.Revision) != priorRevision)
                    { lastWasLogistics = requestedLogistics; lastWasTask = command.Operation == "task"; }
                    if (error != null) Revoke(error);
                }
            }
            else if (command.Operation == "claim")
            {
                Discover(); unavailable = Check(KitchenGate.Inspect());
                if (unavailable != null) error = unavailable;
                else if (!InputFocused) error = "game_unfocused";
                else if (!lease.Claim(client, Now)) error = "already_claimed";
                else { Restore(); Install(); }
            }
            else if (command.Operation == "input")
            {
                unavailable = Check(KitchenGate.Inspect());
                if (navigation.Action.State == "navigating" || interactions.Run.Running || logistics.Run.Running || tasks.Run.Running) error = "action_in_progress";
                else if (unavailable != null) { Revoke(unavailable); error = unavailable; }
                else if (!InputFocused) { error = "game_unfocused"; Revoke(error); }
                else error = lease.Set(client, command, Now);
            }
            else if (command.Operation == "release")
            {
                // Closing a client sends release before disconnecting. An
                // accepted autonomous request belongs to the game until Stop
                // or completion; only raw manual input follows socket lifetime.
                if (lease.Owner(Now) != client) error = "not_owner";
                else if (!Autonomous) Revoke("released");
            }
            if (receipt != null && command.Operation != "cancel")
            {
                if (error != null) { receipt.State = "rejected"; receipt.Reason = error; }
                else if (command.Operation != "suspend" && command.Operation != "resume" && command.Operation != "forget_task") { activeReceipt = receipt; receiptOperation = command.Operation; CaptureReceipt(); }
            }
            return Json.Object("type", "reply", "id", command.Id, "ok", receipt != null || error == null,
                "error", error, "control", Describe(client), "availableActions", actions, "receipt", receipt == null ? null : receipt.Describe());
        }

        public object Describe(int client)
        {
            int owner = lease.Owner(Now);
            return Json.Object("kitchenEpoch", kitchenEpoch, "dispatchGeneration", dispatchGeneration, "receiptProtocolVersion", 1, "taskSuspensionVersion", 1, "player", 2, "navigation", navigation.Describe(), "action", lastWasTask ? tasks.Describe() : lastWasLogistics ? logistics.Describe() : interactions.Describe(), "available", unavailable == null, "reason", unavailable,
                "roundId", localRound ? (object)roundSequence : null,
                "worker", WorkerHost.Current == null ? Json.Object("state", "disabled") : Json.Object("state", WorkerHost.Current.Ready ? "ready" : "fallback", "pid", WorkerHost.Current.ProcessId, "error", WorkerHost.Current.Error, "encodedMessages", WorkerHost.Current.EncodedMessages, "throwBatches", WorkerHost.Current.Flights == null ? 0L : WorkerHost.Current.Flights.CompletedBatches),
                "claimed", owner != 0, "ownedByYou", client != 0 && owner == client, "suspended", suspendedAt >= 0,
                "lastRelease", lease.LastRelease, "inputTrace", traceInput ? inputTrace.ToArray() : null,
                "chefInstanceId", target == null ? null : (object)target.GetInstanceID(),
                "chefObjectInstanceId", target == null ? null : (object)target.gameObject.GetInstanceID(),
                "pad", config == null ? null : config.Pad.ToString(), "side", config == null ? null : config.Side.ToString());
        }
        private void Walk(Vector3 direction, int inputMilliseconds)
        {
            int owner = navigation.Action.Owner;
            if (owner == 0 || lease.Owner(Now) != owner) { navigation.Stop("control_lost"); return; }
            float x = direction.x * (target.Movement.XAxisAllignment == PlayerControls.InversionType.Normal ? 1 : -1);
            float y = -direction.z * (target.Movement.YAxisAllignment == PlayerControls.InversionType.Normal ? 1 : -1);
            double now = Now;
            string error = lease.Set(owner, new ControlCommand { Sequence = ++navigationSequence, Milliseconds = WalkingCommand.OwnershipMilliseconds(inputMilliseconds),
                X = x, Y = y, Buttons = 0 }, now, inputMilliseconds);
            if (traceInput && error == null)
            { lastWalk = direction; lastWalkTime = now; lastWalkDeadline = now + inputMilliseconds / 1000.0; lastWalkFrame = Time.frameCount; }
            if (error != null) navigation.Stop(error);
        }
        private void NeutralNavigation()
        {
            if (interactions.Run.Running || logistics.Run.Running || tasks.Run.Running) { Buttons(0); return; }
            int owner = navigation.Action.Owner;
            if (owner != 0 && lease.Release(owner, "action_finished")) Restore();
        }
        private void Buttons(int mask)
        {
            int owner = interactions.Run.Running ? interactions.Run.Owner : tasks.Run.Running ? tasks.Run.Owner : logistics.Run.Owner;
            if (owner == 0 || lease.Owner(Now) != owner) return;
            lease.Set(owner, new ControlCommand { Sequence = ++navigationSequence, Milliseconds = 300, Buttons = mask }, Now);
        }
        private void FinishInteraction() { if (logistics.Run.Running || tasks.Run.Running) Buttons(0); else FinishLogistics(); }
        private void FinishLogistics() { lease.Revoke("action_finished"); Restore(); }
        private object Catalog()
        {
            // Discovery is shared only during this synchronous observation pass.
            // Native volume flags, geometry and velocities are still read afresh.
            try { return BuildCatalog(); }
            finally { throwOptions.EndObservation(); }
        }
        private object BuildCatalog()
        {
            var catalog = (Json.Record)navigation.Available(target);
            throwOptions.Begin(target);
            var targets = new System.Collections.Generic.List<object>();
            foreach (GameObject go in navigation.Targets())
            {
                var identity = (Json.Record)navigation.TargetIdentity(go);
                try
                {
                    HarnessOptions.Target(identity, go);
                    var offered = new System.Collections.Generic.List<string>(InteractionActions.Offered(target, go));
                    if (offered.Contains("throw_to"))
                    {
                        object preview;
                        if (!throwOptions.Check(go, out preview)) offered.Remove("throw_to");
                        identity["throw"] = preview;
                    }
                    identity["operations"] = offered.ToArray();
                    identity["workConditions"] = InteractionActions.ConditionsFor(go, true);
                    identity["waitConditions"] = InteractionActions.ConditionsFor(go, false);
                }
                catch (Exception) { identity["operations"] = new string[0]; identity["reason"] = "observation_unavailable"; }
                targets.Add(identity);
            }
            var players = DescribePlayers(true);
            var actions = (System.Collections.Generic.List<object>)catalog["actions"];
            if (!interactions.Run.Running && !logistics.Run.Running && !tasks.Run.Running && navigation.Action.State == "idle")
                foreach (string name in ActionRun.Names)
                {
                    if (name == "navigate_to" || name == "stop") continue;
                    var eligible = new System.Collections.Generic.List<object>();
                    foreach (Json.Record identity in name == "throw_to_player" ? players : targets) if (Array.IndexOf((string[])identity["operations"], name) >= 0) eligible.Add(identity);
                    if (eligible.Count == 0) continue;
                    object parameters = name == "throw_to_player" ? (object)Json.Object("player_id", Json.Object("type", "enum", "options", eligible)) :
                        Json.Object("target_id", Json.Object("type", "enum", "options", eligible));
                    if (name == "work_at" || name == "wait_until")
                    {
                        ((Json.Record)parameters)["condition"] = Json.Object("type", "enum", "options", name == "work_at" ? ActionRun.WorkConditions : ActionRun.WaitConditions);
                        ((Json.Record)parameters)["timeout_ms"] = Json.Object("type", "integer", "minimum", 100, "maximum", 120000, "default", 60000);
                    }
                    if (name == "activate") ((Json.Record)parameters)["desired_state"] = Json.Object("type", "enum", "options", new[] { "on", "off", "trigger" });
                    actions.Add(Json.Object("name", name, "parameters", parameters));
                }
            else catalog["actions"] = new[] { Json.Object("name", "stop") };
            catalog["targets"] = targets; catalog["players"] = players; catalog["action"] = lastWasTask ? tasks.Describe() : lastWasLogistics ? logistics.Describe() : interactions.Describe();
            catalog["throwOptionsComplete"] = throwOptions.End();
            logistics.Decorate(catalog, target, tasks.Run.Running); tasks.Decorate(catalog, target, logistics.Run.Running); return catalog;
        }
        private System.Collections.Generic.List<object> DescribePlayers(bool withPreview)
        {
            var players = new System.Collections.Generic.List<object>();
            foreach (PlayerControls player in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
            {
                if (player == target || player.PlayerIDProvider == null) continue;
                int playerId = (int)player.PlayerIDProvider.GetID() + 1;
                if (playerId != 1 && playerId != 3 && playerId != 4) continue;
                var entry = PlayerInputLookup.GetInputConfig().GetInputConfigEntry(player.PlayerIDProvider.GetID());
                if (entry == null || !entry.IsLocal()) continue;
                object preview;
                preview = null;
                bool canPass = withPreview && throwOptions.Check(player.gameObject, out preview);
                players.Add(Json.Object("id", "p" + playerId, "playerId", playerId, "kind", "player", "label", "Player " + playerId,
                    "objectInstanceId", player.gameObject.GetInstanceID(), "position", Values.Vector(player.transform.position),
                    "possibleOperations", new[] { "throw_to_player" },
                    "operations", canPass ? new[] { "throw_to_player" } : new string[0], "throw", preview));
            }
            return players;
        }
        private void Install()
        {
            scheme = target.ControlScheme;
            oldX = scheme.m_moveX; oldY = scheme.m_moveY;
            oldPickup = scheme.m_pickupButton; oldUse = scheme.m_worksurfaceUseButton; oldDash = scheme.m_dashButton;
            bridgeX = new Axis(delegate { return Input().X; }, oldX);
            bridgeY = new Axis(delegate { return Input().Y; }, oldY);
            bridgePickup = new Button(delegate { return (Input().Buttons & 1) != 0; }, oldPickup);
            bridgeUse = new Button(delegate { return (Input().Buttons & 2) != 0; }, oldUse);
            bridgeDash = new Button(delegate { return (Input().Buttons & 4) != 0; }, oldDash);
            scheme.m_moveX = bridgeX; scheme.m_moveY = bridgeY;
            scheme.m_pickupButton = bridgePickup; scheme.m_worksurfaceUseButton = bridgeUse; scheme.m_dashButton = bridgeDash;
        }
        private bool OwnsBindings()
        {
            return scheme == target.ControlScheme && scheme.m_moveX == bridgeX && scheme.m_moveY == bridgeY &&
                scheme.m_pickupButton == bridgePickup && scheme.m_worksurfaceUseButton == bridgeUse && scheme.m_dashButton == bridgeDash;
        }
        private void Restore()
        {
            if (scheme == null) return;
            if (scheme.m_moveX == bridgeX) scheme.m_moveX = oldX;
            if (scheme.m_moveY == bridgeY) scheme.m_moveY = oldY;
            if (scheme.m_pickupButton == bridgePickup) scheme.m_pickupButton = oldPickup;
            if (scheme.m_worksurfaceUseButton == bridgeUse) scheme.m_worksurfaceUseButton = oldUse;
            if (scheme.m_dashButton == bridgeDash) scheme.m_dashButton = oldDash;
            scheme = null;
        }
        private void Revoke(string reason, bool preserveTasks = false) { if (!preserveTasks) ClearSuspendedTasks(reason); suspendedAt = -1; tasks.Cancel(reason); logistics.Cancel(reason); interactions.Cancel(reason); if (navigation.Action.State == "navigating") navigation.Stop(reason); lease.Revoke(reason); Restore(); }
        public void Dispose()
        {
            disposed = true;
            if (focusOwner == this) focusOwner = null;
            focusPatch.UnpatchSelf();
            Revoke("shutdown"); tasks.Reset("shutdown"); navigation.Dispose(); server.Dispose();
        }

        private sealed class Axis : ILogicalValue
        {
            private readonly Func<float> read;
            private readonly ILogicalValue original;
            public Axis(Func<float> value, ILogicalValue native) { read = value; original = native; }
            public float GetValue() { return read(); }
            public void GetLogicTreeData(out AcyclicGraph<ILogicalElement, LogicalLinkInfo> graph, out AcyclicGraph<ILogicalElement, LogicalLinkInfo>.Node head)
            { original.GetLogicTreeData(out graph, out head); }
        }
        private sealed class Button : LogicalButtonBase
        {
            private readonly Func<bool> read;
            private readonly ILogicalButton original;
            public Button(Func<bool> value, ILogicalButton native)
            {
                read = value; original = native;
                // Seed the native edge tracker with neutral even if claim and
                // first input are dispatched in the same Unity update.
                Update(false);
            }
            public override bool IsDown() { return read(); }
            // IsDown reads only the gated bridge lease. Allow the native edge
            // tracker to process those synthetic presses even in the background.
            protected override bool CanProcessInput() { return true; }
            public override void GetLogicTreeData(out AcyclicGraph<ILogicalElement, LogicalLinkInfo> graph, out AcyclicGraph<ILogicalElement, LogicalLinkInfo>.Node head)
            { original.GetLogicTreeData(out graph, out head); }
        }
    }
}
