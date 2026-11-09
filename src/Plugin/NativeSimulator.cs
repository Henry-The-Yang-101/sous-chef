using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using SousChef.Core;
using Team17.Online;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SousChef.Plugin
{
    // Opt-in automation of the installed player. No replacement physics or level data.
    internal sealed partial class NativeSimulator : MonoBehaviour
    {
        public static bool Enabled { get { return !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_SIM")) && Array.IndexOf(Environment.GetCommandLineArgs(), "-batchmode") >= 0 && Array.IndexOf(Environment.GetCommandLineArgs(), "-nographics") >= 0; } }
        internal string GameAssemblyHash, BridgeSession;
        private string directory, phase = "booting", error, requested, lastCommand, commandError;
        private float nextStatus, nextIntroSkip;
        private bool introReady;
        private ServerPilotRotation aimingCannon;
        private PlayerControls.ControlSchemeData savedCannonControls;
        private float aimUntil;
        private bool engaged, split, loaded;
        private int generation, loadedGeneration;
        private int dlc = -1;
        private Harmony patches;
        private static ServerDynamicFlowController advanceFlow;
        internal static bool BlockInputFocus { get; private set; }
        internal static bool SimulateUnfocused { get; private set; }
        private static GameObject fixtureStanceAim;
        private static Vector3 fixtureStancePoint;
        private bool interruptingTarget;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static object Call(object obj, string method, params object[] args) { return obj.GetType().GetMethod(method, Flags).Invoke(obj, args); }
        private void Awake()
        {
            directory = Path.GetFullPath(Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_SIM"));
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "saves"));
            AudioListener.volume = 0f;
            AudioListener.pause = true;
            int fps;
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_FPS"), out fps)) fps = 60;
            SetFrameRate(fps);
            patches = new Harmony("dev.souschef.native-simulator");
            patches.Patch(typeof(SteamSaveManager).GetMethod("GetSaveDirectory", Flags), new HarmonyMethod(typeof(NativeSimulator).GetMethod("SaveDirectory", Flags)));
            var phaseWrapper = new HarmonyMethod(typeof(NativeSimulator).GetMethod("PhaseRoutine", Flags));
            patches.Patch(typeof(ServerDynamicFlowController).GetMethod("BuildPhaseRoutine", Flags), postfix: phaseWrapper);
            patches.Patch(typeof(ServerBossFlowController).GetMethod("BuildPhaseRoutine", Flags), postfix: phaseWrapper);
            requested = Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_LEVEL");
            SceneManager.sceneLoaded += OnSceneLoaded;
        }
        private static bool SaveDirectory(ref string __result)
        {
            __result = Path.Combine(Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_SIM"), "saves") + Path.DirectorySeparatorChar;
            return false;
        }
        private static void PhaseRoutine(ServerDynamicFlowController __instance, ref IEnumerator __result)
        { __result = RunPhase(__instance, __result); }
        private static IEnumerator RunPhase(ServerDynamicFlowController flow, IEnumerator native)
        {
            try
            {
                while (true)
                {
                    if (System.Object.ReferenceEquals(flow, advanceFlow)) { advanceFlow = null; yield break; }
                    if (!native.MoveNext()) yield break;
                    yield return native.Current;
                }
            }
            finally { var disposable = native as IDisposable; if (disposable != null) disposable.Dispose(); }
        }
        private void Update()
        {
            AudioListener.volume = 0f;
            AudioListener.pause = true;
            try
            {
                if (aimingCannon != null && Time.realtimeSinceStartup >= aimUntil)
                { aimingCannon.AssignPlayer(savedCannonControls); aimingCannon = null; }
                if (error == null) Bootstrap();
                if (!introReady && loadedGeneration == generation && generation > 0)
                {
                    introReady = KitchenGate.Inspect().InRound;
                    if (!introReady && Time.realtimeSinceStartup >= nextIntroSkip)
                    {
                        nextIntroSkip = Time.realtimeSinceStartup + 1f;
                        // Use the same native skip event as the held UI button.
                        // This only runs during initial kitchen entry.
                        foreach (var cutscene in UnityEngine.Object.FindObjectsOfType<CutsceneController>())
                            if (cutscene.GetComponent<ClientCutsceneController>() != null) cutscene.OnCutsceneSkipped();
                    }
                }
                string command = Path.Combine(directory, "command");
                if (File.Exists(command))
                {
                    string value = File.ReadAllText(command).Trim(); File.Delete(command);
                    int separator = value.IndexOf(' ');
                    if (separator < 1) throw new ArgumentException("Invalid simulator command");
                    lastCommand = value.Substring(0, separator); value = value.Substring(separator + 1);
                    commandError = null;
                    try
                    {
                        if (value == "quit") { phase = "stopping"; Application.Quit(); }
                        else if (value == "reset") Load(requested);
                        else if (value == "launch-cannon") LaunchLoadedCannon();
                        else if (value == "aim-cannon-down") AimLoadedCannon();
                        else if (value == "advance-phase") AdvancePhase();
                        else if (value == "input-focus off" || value == "input-focus on") BlockInputFocus = value.EndsWith("off");
                        else if (value == "application-focus off" || value == "application-focus on") SimulateUnfocused = value.EndsWith("off");
                        else if (value == "pause on" || value == "pause off")
                        {
                            var timeManager = UnityEngine.Object.FindObjectOfType<TimeManager>();
                            if (timeManager == null) throw new InvalidOperationException("native_time_manager_missing");
                            timeManager.SetPaused(TimeManager.PauseLayer.Main, value == "pause on", this);
                        }
                        else if (value.StartsWith("stage-portal ", StringComparison.Ordinal)) StagePortal(Int32.Parse(value.Substring(13)));
                        else if (value.StartsWith("stage-player ", StringComparison.Ordinal)) StagePlayer(value.Substring(13));
                        else if (value.StartsWith("probe-throw ", StringComparison.Ordinal)) ProbeThrow(Int32.Parse(value.Substring(12)));
                        else if (value.StartsWith("probe-walk ", StringComparison.Ordinal)) ProbeWalk(value.Substring(11));
                        else if (value.StartsWith("interrupt-target ", StringComparison.Ordinal)) InterruptTarget(value.Substring(17));
                        else if (value.StartsWith("task-fixture ", StringComparison.Ordinal)) TaskFixture(value.Substring(13));
                        else if (value.StartsWith("load ", StringComparison.Ordinal)) Load(value.Substring(5));
                        else if (value.StartsWith("fps ", StringComparison.Ordinal)) SetFrameRate(Int32.Parse(value.Substring(4)));
                        else throw new ArgumentException("Unknown simulator command");
                    }
                    catch (Exception e) { commandError = e.Message; }
                }
            }
            catch (Exception e) { error = e.ToString(); phase = "error"; Debug.LogError("Native simulator: " + e); }
            if (Time.realtimeSinceStartup >= nextStatus)
            {
                nextStatus = Time.realtimeSinceStartup + .25f;
                bool inRound = loadedGeneration == generation && generation > 0 && KitchenGate.Inspect().InRound;
                var players = new List<object>();
                foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                    players.Add(Json.Object("name", p.name, "position", new[] { p.transform.position.x, p.transform.position.y, p.transform.position.z }, "inputAllowed", p.CanButtonBePressed(), "enabled", p.enabled, "grounded", p.GroundCollider != null));
                var cannons = new List<object>();
                foreach (var cannon in UnityEngine.Object.FindObjectsOfType<ServerCannon>())
                {
                    var occupant = (GameObject)typeof(ServerCannon).GetField("m_loadedObject", Flags).GetValue(cannon);
                    var provider = occupant == null ? null : occupant.GetComponent<PlayerIDProvider>();
                    cannons.Add(Json.Object("id", cannon.gameObject.GetInstanceID(), "name", cannon.name,
                        "loadedPlayer", provider == null ? 0 : (int)provider.GetID() + 1,
                        "ready", (bool)typeof(ServerCannon).GetField("m_readyToLaunch", Flags).GetValue(cannon), "flying", cannon.IsFlying(),
                        "aiming", aimingCannon != null && aimingCannon.gameObject == cannon.gameObject,
                        "target", Values.Vector(cannon.GetComponent<Cannon>().m_target.position)));
                }
                var portals = new List<object>();
                foreach (var portal in Resources.FindObjectsOfTypeAll<Teleportal>())
                {
                    if (!portal.gameObject.scene.IsValid() || !portal.gameObject.scene.isLoaded) continue;
                    portals.Add(Json.Object("id", portal.GetInstanceID(), "objectId", portal.gameObject.GetInstanceID(), "position", Values.Vector(portal.transform.position),
                        "exitObjectId", portal.m_exitPortal == null ? 0 : portal.m_exitPortal.GetInstanceID(),
                        "playerSender", portal.GetComponent<TeleportalPlayerSender>() != null,
                        "enabled", portal.isActiveAndEnabled));
                }
                var dynamic = UnityEngine.Object.FindObjectOfType<ServerDynamicFlowController>();
                var phaseData = dynamic == null ? null : (DynamicLevelMessage)typeof(ServerDynamicFlowController).GetField("m_data", Flags).GetValue(dynamic);
                var clientDynamic = UnityEngine.Object.FindObjectOfType<ClientDynamicFlowController>();
                var transitions = clientDynamic == null ? null : (Queue<IEnumerator>)typeof(ClientDynamicFlowController).GetField("m_phaseQueue", Flags).GetValue(clientDynamic);
                var phaseOrders = dynamic == null || !inRound ? null : (ServerDynamicOrderController)Call(dynamic, "GetOrderController");
                Write("status.json", Json.Object("dynamicPhase", phaseData == null ? -1 : phaseData.m_phase, "boss", dynamic is ServerBossFlowController,
                    "phaseTransitionsPending", transitions == null ? 0 : transitions.Count, "phaseCanAdvance", phaseOrders != null && phaseOrders.HasMorePhases(),
                    "dlc", dlc, "paused", TimeManager.IsPaused(TimeManager.PauseLayer.Main), "cannons", cannons, "portals", portals,"phase", error != null ? "error" : inRound ? "ready" : phase, "error", error,
                    "scene", SceneManager.GetActiveScene().name, "requestedLevel", requested, "generation", generation,
                    "inRound", inRound, "players", players, "serverUsers", ServerUserSystem.m_Users.Count,
                    "clientUsers", ClientUserSystem.m_Users.Count, "realtime", Time.realtimeSinceStartup,
                    "pid", System.Diagnostics.Process.GetCurrentProcess().Id, "sessionId", BridgeSession, "lastCommand", lastCommand, "commandError", commandError,
                    "targetFrameRate", Application.targetFrameRate,
                    "applicationFocused", Application.isFocused, "unfocusedInputFixture", BlockInputFocus,
                    "simulatedUnfocused", SimulateUnfocused,
                    "unityVersion", Application.unityVersion, "gameAssemblySha256", GameAssemblyHash, "audioPaused", AudioListener.pause, "audioVolume", AudioListener.volume,
                    "graphicsDevice", SystemInfo.graphicsDeviceType.ToString(), "frame", Time.frameCount, "physicsAutoSimulation", Physics.autoSimulation, "fixedDeltaTime", Time.fixedDeltaTime));
            }
        }
        internal static bool StartPortalPoint(Navigation.NavigationActions navigation, PlayerControls chef, int owner, string destination, out string reason)
        {
            reason = null;
            if (!Enabled || destination == null || !destination.StartsWith("d000", StringComparison.Ordinal)) return false;
            // Ordinary identities never have leading zeroes. Reserve this form
            // only in the opt-in simulator; the wire protocol stays unchanged.
            uint encoded;
            if (!UInt32.TryParse(destination.Substring(4), out encoded)) { reason = "invalid_fixture_portal"; return true; }
            if (encoded == 0 && fixtureStanceAim != null)
            {
                reason = navigation.StartPosition(chef, owner, fixtureStanceAim, fixtureStancePoint, true);
                return true;
            }
            var physics = new Navigation.WalkPhysics();
            reason = physics.Refresh(chef);
            if (reason != null) return true;
            var portal = physics.Portal(unchecked((int)encoded));
            if (portal == null || !portal.Valid) { reason = "fixture_portal_unavailable"; return true; }
            reason = navigation.StartPosition(chef, owner, portal.Receiver.gameObject, portal.Exit);
            return true;
        }
        private void StagePortal(int id)
        {
            // Explicit test initial condition, never invoked by navigation. This
            // isolates portal traversal from unrelated access mechanisms.
            PlayerControls chef = null;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                if (p.PlayerIDProvider != null && p.PlayerIDProvider.GetID() == PlayerInputLookup.Player.Two) chef = p;
            if (chef == null || !chef.enabled || chef.Motion.GetVelocity().magnitude > .05f ||
                Navigation.PortalLink.Travelling(chef) || !KitchenGate.Inspect().InRound)
                throw new InvalidOperationException("Staging requires a stationary, enabled Player 2 in a kitchen");
            var physics = new Navigation.WalkPhysics();
            if (physics.Refresh(chef) != null) throw new InvalidOperationException("Staging requires grounded Player 2");
            var portal = physics.Portal(id);
            if (portal == null || !portal.Valid) throw new InvalidOperationException("No valid selected portal");
            Vector3 stance; Collider ground;
            Vector3 guess = portal.Entry + portal.Source.transform.right * portal.StagingDistance;
            if (!physics.Stand(guess, out stance, out ground) || Mathf.Abs(stance.y - portal.Entry.y) > .03f ||
                !physics.PortalSegment(stance, portal.Entry, portal.Trigger))
                throw new InvalidOperationException("No checked portal staging stance");
            Vector3 before = chef.transform.position;
            chef.transform.position += stance - physics.Position;
            Physics.SyncTransforms();
            Write("staging.json", Json.Object("kind", "explicit_supported_portal_initial_pose", "entranceId", id,
                "before", Values.Vector(before), "after", Values.Vector(chef.transform.position)));
        }
        private void ProbeThrow(int id)
        {
            PlayerControls chef = null; GameObject target = null;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                if (p.PlayerIDProvider != null && p.PlayerIDProvider.GetID() == PlayerInputLookup.Player.Two) chef = p;
            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>()) if (go.GetInstanceID() == id) target = go;
            if (chef == null || target == null || !KitchenGate.Inspect().InRound) throw new InvalidOperationException("No current chef/throw target");
            var planner = new Actions.ThrowPlanner(); Actions.ThrowPlan plan = null;
            string error = planner.Prepare(chef, Actions.ActionObservations.Held(chef), target);
            Vector3 foot = chef.transform.position; foot.y = chef.GetComponent<CapsuleCollider>().bounds.min.y;
            bool feasible = error == null && planner.From(foot, false, out plan);
            Write("throw-probe.json", Json.Object("feasible", feasible, "error", error, "diagnostics", planner.Diagnostics,
                "plan", plan == null ? null : plan.Describe()));
        }
        private void InterruptTarget(string arguments)
        {
            // Deliberate fault injection in an opt-in isolated native runtime.
            // Wait for a real pickup, then briefly remove the delivery location
            // or its route-discovery colliders. Never alter chef/cargo state.
            string[] values = arguments.Split(' ');
            if (values.Length != 4) throw new ArgumentException("interrupt-target requires instance ID, duration ms, target|route, and delay after pickup ms");
            int id = Int32.Parse(values[0]), milliseconds = Int32.Parse(values[1]), delay = Int32.Parse(values[3]);
            if (milliseconds < 50 || milliseconds > 2000 || delay < 0 || delay > 2000 || (values[2] != "target" && values[2] != "route"))
                throw new ArgumentException("Invalid interruption fixture parameters");
            GameObject target = null; PlayerControls chef = null;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>()) if (t.gameObject.GetInstanceID() == id) target = t.gameObject;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                if (p.PlayerIDProvider != null && p.PlayerIDProvider.GetID() == PlayerInputLookup.Player.Two) chef = p;
            if (interruptingTarget || target == null || chef == null || target.GetComponent<ServerAttachStation>() == null ||
                Actions.ActionObservations.Occupant(target) != null || !KitchenGate.Inspect().InRound)
                throw new InvalidOperationException("Interruption requires an empty station, Player 2, and no pending fixture");
            interruptingTarget = true;
            StartCoroutine(InterruptTargetRoutine(target, chef, milliseconds, values[2], delay));
        }
        private IEnumerator InterruptTargetRoutine(GameObject target, PlayerControls chef, int milliseconds, string kind, int delay)
        {
            var colliders = new List<Collider>(); float hiddenAt = -1;
            var record = Json.Object("targetInstanceId", target.GetInstanceID(), "kind", kind, "durationMs", milliseconds, "delayMs", delay, "phase", "armed");
            Write("interruption.json", record);
            try
            {
                float until = Time.realtimeSinceStartup + 60;
                while (target != null && chef != null && Actions.ActionObservations.Held(chef) == null && Time.realtimeSinceStartup < until) yield return null;
                if (target == null || chef == null || Actions.ActionObservations.Held(chef) == null) yield break;
                float pickedAt = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - pickedAt < delay / 1000f) yield return null;
                if (target == null || chef == null || Actions.ActionObservations.Held(chef) == null || Actions.ActionObservations.Occupant(target) != null) yield break;
                record["cargoInstanceId"] = Actions.ActionObservations.Held(chef).GetInstanceID();
                if (kind == "target") target.SetActive(false);
                else foreach (var c in target.GetComponentsInChildren<Collider>()) if (c.enabled) { colliders.Add(c); c.enabled = false; }
                Physics.SyncTransforms(); hiddenAt = Time.realtimeSinceStartup;
                record["hiddenAt"] = hiddenAt; record["phase"] = "interrupted"; Write("interruption.json", record);
                while (Time.realtimeSinceStartup - hiddenAt < milliseconds / 1000f) yield return null;
            }
            finally
            {
                if (hiddenAt >= 0)
                {
                    if (kind == "target" && target != null) target.SetActive(true);
                    foreach (var c in colliders) if (c != null) c.enabled = true;
                    Physics.SyncTransforms(); record["restoredAt"] = Time.realtimeSinceStartup;
                }
                record["phase"] = hiddenAt >= 0 ? "restored" : "not_triggered"; Write("interruption.json", record);
                interruptingTarget = false;
            }
        }
        private void StagePlayer(string arguments)
        {
            // Explicit fixture setup in the isolated simulator, never called
            // by an action. Keep native colliders, physics and catch controls.
            string[] values = arguments.Split(' ');
            if (values.Length != 5) throw new ArgumentException("stage-player requires player ID, foot XYZ and yaw degrees");
            int id = Int32.Parse(values[0]); PlayerControls player = null;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                if (p.PlayerIDProvider != null && (int)p.PlayerIDProvider.GetID() + 1 == id) player = p;
            if (player == null || !player.enabled || player.m_bRespawning || player.Motion.GetVelocity().magnitude > .05f ||
                Navigation.PortalLink.Travelling(player) || !KitchenGate.Inspect().InRound)
                throw new InvalidOperationException("Staging requires a stationary, enabled chef in a kitchen");
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var point = new Vector3(Single.Parse(values[1], culture), Single.Parse(values[2], culture), Single.Parse(values[3], culture));
            float yaw = Single.Parse(values[4], culture);
            var physics = new Navigation.WalkPhysics(); Vector3 stance; Collider ground;
            if (physics.Refresh(player) != null || !physics.Stand(point, out stance, out ground) || Vector3.Distance(point, stance) > .05f ||
                Single.IsNaN(yaw) || Single.IsInfinity(yaw)) throw new InvalidOperationException("No supported clear staging stance");
            Vector3 before = player.transform.position;
            player.transform.position += stance - physics.Position;
            player.transform.rotation = Quaternion.Euler(0, yaw, 0);
            Physics.SyncTransforms();
            Write("staging.json", Json.Object("kind", "explicit_supported_throw_initial_pose", "playerId", id,
                "before", Values.Vector(before), "after", Values.Vector(player.transform.position), "forward", Values.Vector(player.transform.forward)));
        }
        private void ProbeWalk(string arguments)
        {
            string[] values = arguments.Split(' ');
            if (values.Length != 6) throw new ArgumentException("probe-walk requires two XYZ positions");
            var v = new float[6];
            for (int i = 0; i < 6; i++) v[i] = Single.Parse(values[i], System.Globalization.CultureInfo.InvariantCulture);
            PlayerControls chef = null;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                if (p.PlayerIDProvider != null && p.PlayerIDProvider.GetID() == PlayerInputLookup.Player.Two) chef = p;
            if (chef == null) throw new InvalidOperationException("No Player 2");
            var physics = new Navigation.WalkPhysics { TraceEnabled = true };
            string error = physics.Refresh(chef);
            Vector3 from, to; Collider a, b;
            bool start = physics.Stand(new Vector3(v[0],v[1],v[2]), out from, out a);
            bool end = physics.Stand(new Vector3(v[3],v[4],v[5]), out to, out b);
            bool clear = start && end && physics.Segment(from, to);
            Write("probe.json", Json.Object("error", error, "start", start, "end", end,
                "from", Values.Vector(from), "to", Values.Vector(to),
                "fromFlow", start ? Values.Vector(physics.Flow(from, a)) : null,
                "toFlow", end ? Values.Vector(physics.Flow(to, b)) : null,
                "clear", clear, "failure", physics.Failure));
        }
        private void AdvancePhase()
        {
            if (advanceFlow != null) throw new InvalidOperationException("A phase request is pending");
            var flow = UnityEngine.Object.FindObjectOfType<ServerDynamicFlowController>();
            if (flow == null || !KitchenGate.Inspect().InRound) throw new InvalidOperationException("No active native dynamic kitchen");
            var orders = (ServerDynamicOrderController)Call(flow, "GetOrderController");
            if (!orders.HasMorePhases()) throw new InvalidOperationException("Already in the final native phase");
            // Fixture skips only the phase prerequisite once. The game's own
            // coroutine sends phase events, changes orders and runs transitions.
            advanceFlow = flow;
        }
        private ServerCannon LoadedCannon()
        {
            // Test fixture only: fire the native teammate button after Player 2
            // boarded through ordinary interaction. Never move a chef directly.
            ServerCannon selected = null;
            foreach (var cannon in UnityEngine.Object.FindObjectsOfType<ServerCannon>())
            {
                var occupant = (GameObject)typeof(ServerCannon).GetField("m_loadedObject", Flags).GetValue(cannon);
                var provider = occupant == null ? null : occupant.GetComponent<PlayerIDProvider>();
                var chef = occupant == null ? null : occupant.GetComponent<PlayerControls>();
                var config = cannon.GetComponent<Cannon>();
                if (provider == null || provider.GetID() != PlayerInputLookup.Player.Two || chef == null || chef.enabled ||
                    config == null || config.m_attachPoint == null || Vector3.Distance(chef.transform.position, config.m_attachPoint.position) > 2f || cannon.IsFlying() ||
                    !(bool)typeof(ServerCannon).GetField("m_readyToLaunch", Flags).GetValue(cannon)) continue;
                if (selected != null) throw new InvalidOperationException("Ambiguous loaded Player 2 cannon");
                selected = cannon;
            }
            if (selected == null) throw new InvalidOperationException("Player 2 must already be loaded in one ready native cannon");
            return selected;
        }
        private void LaunchLoadedCannon()
        {
            if (aimingCannon != null) throw new InvalidOperationException("Cannon is still aiming");
            var cannon = LoadedCannon();
            cannon.OnTrigger(cannon.GetComponent<Cannon>().m_launchTrigger);
        }
        private void AimLoadedCannon()
        {
            if (aimingCannon != null) throw new InvalidOperationException("Cannon is already aiming");
            var cannon = LoadedCannon();
            var pilot = cannon.GetComponent<ServerPilotRotation>();
            if (pilot == null) throw new InvalidOperationException("Cannon has no native pilot rotation");
            savedCannonControls = (PlayerControls.ControlSchemeData)typeof(ServerPilotMovement).GetField("m_controlScheme", Flags).GetValue(pilot);
            var occupant = (GameObject)typeof(ServerCannon).GetField("m_loadedObject", Flags).GetValue(cannon);
            var controls = occupant.GetComponent<PlayerControls>().ControlScheme;
            var x = new NetworkLogicalValue(controls.m_moveX, (PlayerInputLookup.LogicalValueID)0);
            var y = new NetworkLogicalValue(controls.m_moveY, (PlayerInputLookup.LogicalValueID)0);
            x.SetValue(0); y.SetValue(1);
            pilot.AssignPlayer(new PlayerControls.ControlSchemeData(PlayerInputLookup.Player.Two, occupant.GetComponent<PlayerControls>()) { m_moveX = x, m_moveY = y });
            aimingCannon = pilot; aimUntil = Time.realtimeSinceStartup + 1f;
        }
        private void Bootstrap()
        {
            if (loaded || StartScreenFlow.Instance == null) return;
            var pm = GameUtils.RequireManager<PlayerManager>();
            var save = GameUtils.RequireManager<SaveManager>();
            if (!engaged)
            {
                // Create an unsaved meta session and use the game's keyboard engagement path.
                if (save.GetMetaGameProgress() == null) save.CreateMetaSession();
                Call(pm, "EngagePadToSlot", ControlPadInput.PadNum.One, EngagementSlot.One);
                var user = pm.GetUser(EngagementSlot.One);
                if (T17EventSystemsManager.Instance.GetEventSystemForGamepadUser(user) == null)
                    T17EventSystemsManager.Instance.AssignFreeEventSystemToGamepadUser(user);
                engaged = true; phase = "engaged"; return;
            }
            if (ServerUserSystem.m_Users.Count == 0) return;
            if (!split) { split = ServerUserSystem.SplitUser(EngagementSlot.One); phase = "splitting"; return; }
            if (ClientUserSystem.m_Users.Count != 2) return;
            ServerGameSetup.Mode = GameMode.Party;
            var frontend = T17FrontendFlow.Instance;
            if (frontend == null) frontend = Resources.FindObjectsOfTypeAll<T17FrontendFlow>()[0];
            var sessions = (DLCSerializedData<GameSession>)typeof(T17FrontendFlow).GetField("m_CoopGameSessionPrefabs", Flags).GetValue(frontend);
            var all = new List<object>();
            foreach (var prefab in sessions.AllData)
            {
                if (prefab == null) continue;
                var progress = prefab.GetComponentInChildren<GameProgress>();
                if (progress == null) continue;
                foreach (var entry in progress.GetSceneDirectory().Scenes)
                {
                    var variant = entry.GetSceneVarient(2);
                    if (variant != null) all.Add(Json.Object("dlc", prefab.DLC, "name", variant.SceneName, "label", entry.Label, "theme", entry.Theme.ToString()));
                }
            }
            Write("all-levels.json", all);
            Int32.TryParse(Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_DLC") ?? "-1", out dlc);
            var session = frontend.StartEmptySession(GameSession.GameType.Cooperative, dlc);
            if (session == null) throw new InvalidOperationException("Unavailable DLC session: " + dlc);
            session.SaveSlot = -1;
            session.TypeSettings.WorldMapScene = "StartScreen";
            var levels = new List<object>();
            foreach (var entry in session.Progress.GetSceneDirectory().Scenes)
            {
                var variant = entry.GetSceneVarient(2);
                if (variant != null) levels.Add(Json.Object("name", variant.SceneName, "label", entry.Label, "theme", entry.Theme.ToString()));
            }
            Write("levels.json", levels);
            loaded = true;
            Load(requested);
        }
        private void Load(string level)
        {
            if (!loaded) throw new InvalidOperationException("Session not initialized");
            if (generation > 0 && loadedGeneration != generation) throw new InvalidOperationException("A level is already loading");
            var session = GameUtils.GetGameSession();
            SceneDirectoryData.PerPlayerCountDirectoryEntry match = null;
            foreach (var entry in session.Progress.GetSceneDirectory().Scenes)
            {
                var variant = entry.GetSceneVarient(2);
                if (variant != null && variant.SceneName == level) { match = variant; break; }
            }
            if (match == null) throw new ArgumentException("Unknown two-player kitchen: " + level);
            advanceFlow = null;
            requested = level; generation++; phase = "loading"; introReady = false; nextIntroSkip = Time.realtimeSinceStartup + 1f;
            session.LevelSettings = new GameSession.GameLevelSettings { SceneDirectoryVarientEntry = match };
            var persist = session.GetComponent<PersistentObject>();
            persist.AddPersistingLevel(level); persist.AddPersistingLevel("Loading");
            ClientTime.Update();
            var messenger = typeof(PlayerControls).Assembly.GetType("ServerMessenger", true);
            messenger.GetMethod("TimeSync", Flags).Invoke(null, new object[] { ClientTime.Time() });
            messenger.GetMethod("LoadLevel", Flags, null, new[] { typeof(string), typeof(GameState), typeof(bool), typeof(GameState) }, null)
                .Invoke(null, new object[] { level, GameState.LoadKitchen, false, GameState.RunKitchen });
        }
        private void Write(string name, object value)
        {
            string path = Path.Combine(directory, name), temp = path + ".tmp";
            File.WriteAllText(temp, Json.Encode(value));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        private static void SetFrameRate(int fps)
        {
            if (fps < 10 || fps > 240) throw new ArgumentOutOfRangeException("fps", "Use 10..240 FPS");
            Application.targetFrameRate = fps;
        }
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single && scene.name == requested)
            {
                loadedGeneration = generation;
                // No render camera exists in batchmode. Native animations and
                // their events must still advance, including kitchen intros.
                foreach (var animator in Resources.FindObjectsOfTypeAll<Animator>())
                    if (animator.gameObject.scene.IsValid() && animator.gameObject.scene.isLoaded)
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
        }
        private void OnDestroy()
        {
            if (aimingCannon != null) aimingCannon.AssignPlayer(savedCannonControls);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            advanceFlow = null;
            if (patches != null) patches.UnpatchSelf();
        }
    }
}
