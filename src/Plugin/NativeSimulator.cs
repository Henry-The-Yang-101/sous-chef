using System;
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
    internal sealed class NativeSimulator : MonoBehaviour
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
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static object Call(object obj, string method, params object[] args) { return obj.GetType().GetMethod(method, Flags).Invoke(obj, args); }
        private void Awake()
        {
            directory = Path.GetFullPath(Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_SIM"));
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "saves"));
            AudioListener.volume = 0f;
            AudioListener.pause = true;
            Application.runInBackground = true;
            int fps;
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_FPS"), out fps)) fps = 60;
            SetFrameRate(fps);
            patches = new Harmony("dev.souschef.native-simulator");
            patches.Patch(typeof(SteamSaveManager).GetMethod("GetSaveDirectory", Flags), new HarmonyMethod(typeof(NativeSimulator).GetMethod("SaveDirectory", Flags)));
            patches.Patch(typeof(PlayerControls).GetMethod("CanButtonBePressed"), new HarmonyMethod(typeof(NativeSimulator).GetMethod("InputAllowed", Flags)));
            requested = Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_LEVEL");
            SceneManager.sceneLoaded += OnSceneLoaded;
        }
        private static bool SaveDirectory(ref string __result)
        {
            __result = Path.Combine(Environment.GetEnvironmentVariable("SOUS_CHEF_NATIVE_SIM"), "saves") + Path.DirectorySeparatorChar;
            return false;
        }
        private static bool InputAllowed(PlayerControls __instance, ref bool __result)
        {
            __result = __instance.GetDirectlyUnderPlayerControl() && !T17DialogBoxManager.HasAnyOpenDialogs() &&
                (T17InGameFlow.Instance == null || T17InGameFlow.Instance.m_Rootmenu.GetCurrentOpenMenu() == null);
            return false;
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
                Write("status.json", Json.Object("dlc", dlc, "cannons", cannons, "portals", portals,"phase", error != null ? "error" : inRound ? "ready" : phase, "error", error,
                    "scene", SceneManager.GetActiveScene().name, "requestedLevel", requested, "generation", generation,
                    "inRound", inRound, "players", players, "serverUsers", ServerUserSystem.m_Users.Count,
                    "clientUsers", ClientUserSystem.m_Users.Count, "realtime", Time.realtimeSinceStartup,
                    "pid", System.Diagnostics.Process.GetCurrentProcess().Id, "sessionId", BridgeSession, "lastCommand", lastCommand, "commandError", commandError,
                    "targetFrameRate", Application.targetFrameRate,
                    "unityVersion", Application.unityVersion, "gameAssemblySha256", GameAssemblyHash, "audioPaused", AudioListener.pause, "audioVolume", AudioListener.volume,
                    "graphicsDevice", SystemInfo.graphicsDeviceType.ToString(), "frame", Time.frameCount, "physicsAutoSimulation", Physics.autoSimulation, "fixedDeltaTime", Time.fixedDeltaTime));
            }
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
            if (patches != null) patches.UnpatchSelf();
        }
    }
}
