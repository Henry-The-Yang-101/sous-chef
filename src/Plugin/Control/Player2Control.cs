using System;
using System.Diagnostics;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Control
{
    // Replace only the existing P2 chef's logical inputs. No OS events, new
    // players, global lookup patches, menu bindings or P1 fields are modified.
    internal sealed class Player2Control : IDisposable
    {
        private readonly NavigationActions navigation;
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
            server = new ControlServer(port, Json.Object("type", "hello", "controlProtocolVersion", 1,
                "sessionId", session, "player", 2, "mode", "direct_logical_input", "maxHoldMs", 1000, "actions", new[] { "navigate_to", "stop" }),
                delegate(int client) { lease.Release(client, "disconnected"); });
            server.Start();
        }

        public void Tick()
        {
            if (disposed) return;
            KitchenGate gate = KitchenGate.Inspect();
            if (flow != gate.Flow) { navigation.Reset("round_changed"); Revoke("round_changed"); flow = gate.Flow; nextScan = 0; }
            if (Now >= nextScan) { Discover(); nextScan = Now + 0.25; }
            string reason = Check(gate);
            if (reason != null) { navigation.Invalidate(reason); Revoke(reason); }
            else if (lease.Owner(Now) == 0) Restore();
            unavailable = reason;
            if (navigation.Action.State == "navigating" && lease.Owner(Now) != navigation.Action.Owner) navigation.Stop("control_lost");
            server.Pump(Handle);
            if (reason == null) navigation.Tick(target);
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
            if (!target.enabled || !target.gameObject.activeInHierarchy || target.ControlScheme == null ||
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
            if (command.Operation == "stop") { navigation.Stop("stopped"); Revoke("stopped"); }
            else if (command.Operation == "actions")
            {
                Discover(); KitchenGate gate = KitchenGate.Inspect(); unavailable = Check(gate);
                string observationError = Check(gate, true);
                if (observationError != null) { navigation.Invalidate(observationError); actions = Json.Object("actions", new[] { Json.Object("name", "stop") }, "destinations", new object[0], "navigation", navigation.Describe()); }
                else if (lease.Owner(Now) != 0 && navigation.Action.State == "idle")
                    actions = Json.Object("actions", new[] { Json.Object("name", "stop") }, "destinations", new object[0], "reason", "already_claimed", "navigation", navigation.Describe());
                else actions = navigation.Available(target);
            }
            else if (command.Operation == "navigate_to")
            {
                Discover(); unavailable = Check(KitchenGate.Inspect());
                if (unavailable != null) error = unavailable;
                else if (navigation.Action.State != "idle") error = "action_in_progress";
                else if (!lease.Claim(client, Now)) error = "already_claimed";
                else
                {
                    Restore(); Install(); navigationSequence = 0;
                    error = navigation.Start(target, client, command.Destination);
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
                if (navigation.Action.State == "navigating") error = "action_in_progress";
                else if (unavailable != null) { Revoke(unavailable); error = unavailable; }
                else error = lease.Set(client, command, Now);
            }
            else if (command.Operation == "release")
            {
                if (lease.Release(client, "released")) { navigation.Stop("released"); Restore(); } else error = "not_owner";
            }
            return Json.Object("type", "reply", "id", command.Id, "ok", error == null,
                "error", error, "control", Describe(client), "availableActions", actions);
        }

        public object Describe(int client)
        {
            int owner = lease.Owner(Now);
            return Json.Object("player", 2, "navigation", navigation.Describe(), "available", unavailable == null, "reason", unavailable,
                "claimed", owner != 0, "ownedByYou", client != 0 && owner == client,
                "lastRelease", lease.LastRelease, "chefInstanceId", target == null ? null : (object)target.GetInstanceID(),
                "chefObjectInstanceId", target == null ? null : (object)target.gameObject.GetInstanceID(),
                "pad", config == null ? null : config.Pad.ToString(), "side", config == null ? null : config.Side.ToString());
        }
        private void Walk(Vector3 direction)
        {
            int owner = navigation.Action.Owner;
            if (owner == 0 || lease.Owner(Now) != owner) { navigation.Stop("control_lost"); return; }
            float x = direction.x * (target.Movement.XAxisAllignment == PlayerControls.InversionType.Normal ? 1 : -1);
            float y = -direction.z * (target.Movement.YAxisAllignment == PlayerControls.InversionType.Normal ? 1 : -1);
            string error = lease.Set(owner, new ControlCommand { Sequence = ++navigationSequence, Milliseconds = 150,
                X = x, Y = y, Buttons = 0 }, Now);
            if (error != null) navigation.Stop(error);
        }
        private void NeutralNavigation()
        {
            int owner = navigation.Action.Owner;
            if (owner != 0 && lease.Release(owner, "action_finished")) Restore();
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
        private void Revoke(string reason) { if (navigation.Action.State == "navigating") navigation.Stop(reason); lease.Revoke(reason); Restore(); }
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
