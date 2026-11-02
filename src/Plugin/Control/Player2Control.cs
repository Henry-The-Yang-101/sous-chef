using System;
using System.Diagnostics;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using SousChef.Plugin.Actions;
using UnityEngine;

namespace SousChef.Plugin.Control
{
    // Replace only the existing P2 chef's logical inputs. No OS events, new
    // players, global lookup patches, menu bindings or P1 fields are modified.
    internal sealed class Player2Control : IDisposable
    {
        private readonly NavigationActions navigation;
        private readonly InteractionActions interactions;
        private readonly LogisticsActions logistics;
        private bool lastWasLogistics;
        private long navigationSequence;
        private readonly ControlLease lease = new ControlLease();
        private readonly ControlServer server;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private PlayerControls target;
        private Component flow;
        private GameInputConfig.ConfigEntry config;
        private PlayerControls.ControlSchemeData scheme;
        private ILogicalValue oldX, oldY, bridgeX, bridgeY;
        private ILogicalButton oldPickup, oldUse, oldDash, bridgePickup, bridgeUse, bridgeDash;
        private double nextScan;
        private string unavailable = "player2_absent";
        private bool disposed;
        private double Now { get { return clock.Elapsed.TotalSeconds; } }

        public Player2Control(int port, string session)
        {
            navigation = new NavigationActions(Walk, NeutralNavigation);
            interactions = new InteractionActions(navigation, Buttons, FinishInteraction);
            logistics = new LogisticsActions(navigation, interactions, Buttons, FinishLogistics);
            server = new ControlServer(port, Json.Object("type", "hello", "controlProtocolVersion", 1,
                "sessionId", session, "bridgeVersion", "0.6.0", "player", 2, "mode", "direct_logical_input", "maxHoldMs", 1000, "actions", ActionRun.AllNames),
                delegate(int client) { lease.Release(client, "disconnected"); });
            server.Start();
        }

        public void Tick()
        {
            if (disposed) return;
            KitchenGate gate = KitchenGate.Inspect();
            if (flow != gate.Flow) { Revoke("round_changed"); logistics.Reset("round_changed"); navigation.Reset("round_changed"); flow = gate.Flow; nextScan = 0; }
            if (Now >= nextScan) { Discover(); nextScan = Now + 0.25; }
            string reason = Check(gate);
            if (reason != null) { navigation.Invalidate(reason); Revoke(reason); }
            else if (lease.Owner(Now) == 0) Restore();
            unavailable = reason;
            if (navigation.Action.State == "navigating" && lease.Owner(Now) != navigation.Action.Owner) navigation.Stop("control_lost");
            if (interactions.Run.Running && lease.Owner(Now) != interactions.Run.Owner) interactions.Cancel("control_lost");
            if (logistics.Run.Running && lease.Owner(Now) != logistics.Run.Owner) logistics.Cancel("control_lost");
            server.Pump(Handle);
            if (reason == null) { navigation.Tick(target); interactions.Tick(); logistics.Tick(); }
        }

        private void Discover()
        {
            PlayerControls found = null; int matches = 0;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<PlayerControls>())
            {
                if (!candidate.gameObject.scene.IsValid() || !candidate.gameObject.scene.isLoaded || !candidate.gameObject.activeInHierarchy) continue;
                var provider = candidate.GetComponent<PlayerIDProvider>();
                if (provider != null && provider.GetID() == PlayerInputLookup.Player.Two) { found = candidate; matches++; }
            }
            if (matches != 1) found = null; // Never guess between duplicate IDs.
            var inputConfig = PlayerInputLookup.GetInputConfig();
            var entry = inputConfig == null ? null : inputConfig.GetInputConfigEntry(PlayerInputLookup.Player.Two);
            if (target != found || config != entry || (scheme != null && (found == null || found.ControlScheme != scheme)))
                Revoke("player_assignment_changed");
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
            if (!target.CanButtonBePressed())
            {
                // A browser may inspect reachability while the game is unfocused.
                // Replicate the remaining native button gates; never permit motion
                // or ignore dialogs/menus merely because this is an observation.
                if (!observation || Application.isFocused || !target.GetDirectlyUnderPlayerControl() ||
                    T17DialogBoxManager.HasAnyOpenDialogs() || (T17InGameFlow.Instance != null &&
                    T17InGameFlow.Instance.m_Rootmenu.GetCurrentOpenMenu() != null))
                    return Application.isFocused ? "input_suppressed" : "game_unfocused";
            }
            if (scheme != null && !OwnsBindings()) return "bindings_changed";
            return null;
        }

        // Recheck native gates at the actual read, including between Update and
        // FixedUpdate. Timeout/disconnect neutralizes values without a Unity call.
        private bool CanRead()
        {
            var serverFlow = flow as ServerFlowControllerBase;
            return !disposed && target != null && target.enabled && target.gameObject.activeInHierarchy &&
                target.ControlScheme == scheme && target.PlayerIDProvider != null &&
                target.PlayerIDProvider.GetID() == PlayerInputLookup.Player.Two &&
                serverFlow != null && serverFlow.InRound && !TimeManager.IsPaused(TimeManager.PauseLayer.Main) &&
                !target.m_bRespawning && target.CanButtonBePressed();
        }
        private ControlInput Input() { return CanRead() ? lease.Read(Now) : new ControlInput(); }

        private object Handle(int client, ControlCommand command)
        {
            string error = null;
            object actions = null;
            if (command.Operation == "stop") { Revoke("stopped"); navigation.Stop("stopped"); }
            else if (command.Operation == "actions")
            {
                Discover(); KitchenGate gate = KitchenGate.Inspect(); unavailable = Check(gate);
                string observationError = Check(gate, true);
                if (observationError != null) { navigation.Invalidate(observationError); actions = Json.Object("actions", new[] { Json.Object("name", "stop") }, "destinations", new object[0], "navigation", navigation.Describe()); }
                else if (lease.Owner(Now) != 0 && navigation.Action.State == "idle" && !interactions.Run.Running && !logistics.Run.Running)
                    actions = Json.Object("actions", new[] { Json.Object("name", "stop") }, "destinations", new object[0], "reason", "already_claimed", "navigation", navigation.Describe());
                else actions = Catalog();
            }
            else if (command.Operation == "navigate_to" || InteractionActions.Handles(command.Operation) || LogisticsActions.Handles(command.Operation))
            {
                Discover(); unavailable = Check(KitchenGate.Inspect());
                if (unavailable != null) error = unavailable;
                else if (navigation.Action.State != "idle" || interactions.Run.Running || logistics.Run.Running) error = "action_in_progress";
                else if (!lease.Claim(client, Now)) error = "already_claimed";
                else
                {
                    Restore(); Install(); navigationSequence = 0;
                    bool requestedLogistics = LogisticsActions.Handles(command.Operation);
                    long priorRevision = requestedLogistics ? logistics.Run.Revision : interactions.Run.Revision;
                    error = command.Operation == "navigate_to" ? navigation.Start(target, client, command.Destination) :
                        requestedLogistics ? logistics.Start(target, client, command) : interactions.Start(target, client, command);
                    if (error == null || (requestedLogistics ? logistics.Run.Revision : interactions.Run.Revision) != priorRevision) lastWasLogistics = requestedLogistics;
                    if (error != null) Revoke(error);
                }
            }
            else if (command.Operation == "claim")
            {
                Discover(); unavailable = Check(KitchenGate.Inspect());
                if (unavailable != null) error = unavailable;
                else if (!lease.Claim(client, Now)) error = "already_claimed";
                else { Restore(); Install(); }
            }
            else if (command.Operation == "input")
            {
                unavailable = Check(KitchenGate.Inspect());
                if (navigation.Action.State == "navigating" || interactions.Run.Running || logistics.Run.Running) error = "action_in_progress";
                else if (unavailable != null) { Revoke(unavailable); error = unavailable; }
                else error = lease.Set(client, command, Now);
            }
            else if (command.Operation == "release")
            {
                if (lease.Owner(Now) == client) Revoke("released"); else error = "not_owner";
            }
            return Json.Object("type", "reply", "id", command.Id, "ok", error == null,
                "error", error, "control", Describe(client), "availableActions", actions);
        }

        public object Describe(int client)
        {
            int owner = lease.Owner(Now);
            return Json.Object("player", 2, "navigation", navigation.Describe(), "action", lastWasLogistics ? logistics.Describe() : interactions.Describe(), "available", unavailable == null, "reason", unavailable,
                "claimed", owner != 0, "ownedByYou", client != 0 && owner == client,
                "lastRelease", lease.LastRelease, "chefInstanceId", target == null ? null : (object)target.GetInstanceID(),
                "chefObjectInstanceId", target == null ? null : (object)target.gameObject.GetInstanceID(),
                "pad", config == null ? null : config.Pad.ToString(), "side", config == null ? null : config.Side.ToString());
        }
        private void Walk(Vector3 direction, int inputMilliseconds)
        {
            int owner = navigation.Action.Owner;
            if (owner == 0 || lease.Owner(Now) != owner) { navigation.Stop("control_lost"); return; }
            float x = direction.x * (target.Movement.XAxisAllignment == PlayerControls.InversionType.Normal ? 1 : -1);
            float y = -direction.z * (target.Movement.YAxisAllignment == PlayerControls.InversionType.Normal ? 1 : -1);
            string error = lease.Set(owner, new ControlCommand { Sequence = ++navigationSequence, Milliseconds = Math.Max(150, inputMilliseconds + 50),
                X = x, Y = y, Buttons = 0 }, Now, inputMilliseconds);
            if (error != null) navigation.Stop(error);
        }
        private void NeutralNavigation()
        {
            if (interactions.Run.Running || logistics.Run.Running) { Buttons(0); return; }
            int owner = navigation.Action.Owner;
            if (owner != 0 && lease.Release(owner, "action_finished")) Restore();
        }
        private void Buttons(int mask)
        {
            int owner = interactions.Run.Running ? interactions.Run.Owner : logistics.Run.Owner;
            if (owner == 0 || lease.Owner(Now) != owner) return;
            lease.Set(owner, new ControlCommand { Sequence = ++navigationSequence, Milliseconds = 300, Buttons = mask }, Now);
        }
        private void FinishInteraction() { if (logistics.Run.Running) Buttons(0); else FinishLogistics(); }
        private void FinishLogistics() { lease.Revoke("action_finished"); Restore(); }
        private object Catalog()
        {
            var catalog = (Json.Record)navigation.Available(target);
            var targets = new System.Collections.Generic.List<object>();
            foreach (GameObject go in navigation.Targets())
            {
                var identity = (Json.Record)navigation.TargetIdentity(go);
                try
                {
                    identity["operations"] = InteractionActions.Offered(target, go);
                    identity["workConditions"] = InteractionActions.ConditionsFor(go, true);
                    identity["waitConditions"] = InteractionActions.ConditionsFor(go, false);
                    identity["activationStates"] = ActionObservations.ToggleState(go).HasValue ? new[] { "trigger", "on", "off" } : new[] { "trigger" };
                }
                catch (Exception) { identity["operations"] = new string[0]; identity["reason"] = "observation_unavailable"; }
                targets.Add(identity);
            }
            var players = new System.Collections.Generic.List<object>();
            foreach (PlayerControls player in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
            {
                if (player == target || player.PlayerIDProvider == null) continue;
                int playerId = (int)player.PlayerIDProvider.GetID() + 1;
                if (playerId != 1 && playerId != 3 && playerId != 4) continue;
                var entry = PlayerInputLookup.GetInputConfig().GetInputConfigEntry(player.PlayerIDProvider.GetID());
                if (entry == null || !entry.IsLocal()) continue;
                players.Add(Json.Object("id", "p" + playerId, "playerId", playerId, "kind", "player", "label", "Player " + playerId,
                    "objectInstanceId", player.gameObject.GetInstanceID(), "position", Values.Vector(player.transform.position),
                    "operations", ActionObservations.Held(target) != null &&
                    ((GameObjectUtils.RequestInterface<IClientThrowable>(ActionObservations.Held(target)) != null && ActionObservations.Held(player) == null && player.GetComponent<AttachmentCatcher>() != null) ||
                    (ActionObservations.Held(target).GetComponent<PhysicalAttachment>() != null && Vector3.Distance(target.transform.position, player.transform.position) <= 1.6f)) ? new[] { "throw_to_player" } : new string[0]));
            }
            var actions = (System.Collections.Generic.List<object>)catalog["actions"];
            if (!interactions.Run.Running && !logistics.Run.Running && navigation.Action.State == "idle")
                foreach (string name in ActionRun.Names)
                {
                    if (name == "navigate_to" || name == "stop") continue;
                    var eligible = new System.Collections.Generic.List<object>();
                    foreach (Json.Record identity in targets) if (Array.IndexOf((string[])identity["operations"], name) >= 0) eligible.Add(identity);
                    if (name != "throw_to_player" && eligible.Count == 0) continue;
                    object parameters = name == "throw_to_player" ? (object)Json.Object("player_id", Json.Object("type", "enum", "options", players)) :
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
            catalog["targets"] = targets; catalog["players"] = players; catalog["action"] = lastWasLogistics ? logistics.Describe() : interactions.Describe();
            logistics.Decorate(catalog, target); return catalog;
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
        private void Revoke(string reason) { logistics.Cancel(reason); interactions.Cancel(reason); if (navigation.Action.State == "navigating") navigation.Stop(reason); lease.Revoke(reason); Restore(); }
        public void Dispose() { disposed = true; Revoke("shutdown"); server.Dispose(); }

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
            public override void GetLogicTreeData(out AcyclicGraph<ILogicalElement, LogicalLinkInfo> graph, out AcyclicGraph<ILogicalElement, LogicalLinkInfo>.Node head)
            { original.GetLogicTreeData(out graph, out head); }
        }
    }
}
