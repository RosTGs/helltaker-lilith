using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HelltakerAILab
{
    [BepInPlugin("local.helltaker.ai-lab", "Helltaker AI Lab", "0.2.0")]
    [DefaultExecutionOrder(-32000)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        internal readonly List<string> Events = new List<string>();
        internal int Episode;
        internal int StepIndex;
        internal bool Dead, Won, GoalReached;
        BridgeServer server;
        VisualPacks visuals;
        Harmony harmony;
        bool busy;
        float lastRequest;
        bool savedBackground;
        bool savedSteam;
        bool steamCaptured;
        int episodeScene;
        string root;
        JArray animationTrace;
        internal JObject IdleSyncStatus => visuals?.DanceStatus();

        void LateUpdate()
        {
            Localization.Tick();
            visuals?.TickAnimation();
            if (animationTrace != null) animationTrace.Add(Observation.PlayerVisual());
        }

        void Awake()
        {
            Instance = this;
            root = Path.Combine(Paths.GameRootPath, "HelltakerAILab");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "captures"));
            savedBackground = Application.runInBackground;
            Localization.Configure(root, Logger);
            RuntimeHooks.ConfigureProfile(Path.Combine(root, "profile.json"),
                Config.Bind("Profile", "SeparateProfile", true, "Keep manual game progress in the mod folder; training uses an episode profile.").Value);
            harmony = new Harmony("local.helltaker.ai-lab");
            RuntimeHooks.Install(harmony);
            visuals = new VisualPacks(root, Logger);
            var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var port = Config.Bind("Bridge", "Port", 17654, "Loopback TCP port; 0 selects an available port.").Value;
            server = new BridgeServer(port, token);
            File.WriteAllText(Path.Combine(root, "connection.json"), new JObject {
                ["protocol"] = "helltaker-ai-lab/1", ["host"] = "127.0.0.1", ["port"] = server.Port,
                ["token"] = token, ["pid"] = Process.GetCurrentProcess().Id, ["game_root"] = Paths.GameRootPath
            }.ToString());
            SceneManager.sceneLoaded += Loaded;
            Logger.LogInfo("Helltaker AI Lab ready on loopback port " + server.Port);
        }
        void Loaded(Scene scene, LoadSceneMode mode)
        {
            Localization.ApplyFonts();
            if ((RuntimeHooks.Training || RuntimeHooks.IsolatedProfile) && Manager.instance) Manager.instance.steam = false;
            visuals.Schedule();
            if (RuntimeHooks.Controlled && scene.buildIndex != episodeScene) Event("scene_changed");
        }
        void Update()
        {
            if ((RuntimeHooks.Training || RuntimeHooks.IsolatedProfile) && Manager.instance) Manager.instance.steam = false;
            if (!busy && server != null && server.Queue.TryDequeue(out var request))
            {
                if (request.Cancelled) return;
                lastRequest = Time.realtimeSinceStartup;
                busy = true;
                StartCoroutine(Guarded(request));
            }
            if (RuntimeHooks.Controlled && !busy && Time.realtimeSinceStartup - lastRequest > 120f) Release();
            if (Input.GetKeyDown(KeyCode.F8)) { visuals.Schedule(); Logger.LogInfo("Reloading active visual pack"); }
            if (Input.GetKeyDown(KeyCode.F9)) Release();
            if (!busy) visuals.Tick();
        }
        IEnumerator Guarded(Request request)
        {
            var routine = Execute(request);
            while (true)
            {
                object current;
                try
                {
                    if (!routine.MoveNext()) break;
                    current = routine.Current;
                }
                catch (Exception error)
                {
                    animationTrace = null;
                    Logger.LogError(error);
                    RuntimeHooks.Action = "wait";
                    if (RuntimeHooks.Controlled) Time.timeScale = 0;
                    request.Complete(BridgeServer.Error("operation", error.Message));
                    break;
                }
                yield return current;
            }
            busy = false;
        }
        IEnumerator Execute(Request request)
        {
            var body = request.Body;
            var cmd = (string)body["command"] ?? "observe";
            JObject response = new JObject { ["ok"] = true };
            if (cmd == "observe") response["observation"] = Observation.Capture(this);
            else if (cmd == "scenes") response["scenes"] = Observation.Scenes();
            else if (cmd == "reset")
            {
                var scene = (int?)body["scene"] ?? SceneManager.GetActiveScene().buildIndex;
                if (scene < 0 || scene >= SceneManager.sceneCountInBuildSettings) throw new ArgumentException("Choose a scene index from scenes");
                TakeControl();
                RuntimeHooks.Profile.Clear();
                UnityEngine.Random.InitState((int?)body["seed"] ?? 0);
                RuntimeHooks.Action = "wait";
                Episode++; StepIndex = 0; Dead = Won = GoalReached = false; Events.Clear();
                episodeScene = scene;
                Time.timeScale = 1;
                var load = SceneManager.LoadSceneAsync(scene);
                while (!load.isDone) yield return null;
                var until = Time.time + 0.8f;
                while (Time.time < until) yield return null;
                Time.timeScale = 0;
                Events.Clear(); Dead = Won = GoalReached = false;
                visuals.Apply();
                response["observation"] = Observation.Capture(this);
            }
            else if (cmd == "step")
            {
                var action = (string)body["action"] ?? "wait";
                var allowed = new[] { "up", "down", "left", "right", "confirm", "wait", "cancel", "restart", "advice" };
                if (!allowed.Contains(action)) throw new ArgumentException("Choose an action from the observation actions list");
                var duration = Mathf.Clamp((float?)body["seconds"] ?? 0.35f, 0.02f, 2f);
                var hold = Mathf.Clamp((int?)body["hold_frames"] ?? 1, 1, 30);
                TakeControl();
                Events.Clear(); StepIndex++;
                animationTrace = (bool?)body["trace_animation"] == true ? new JArray() : null;
                Time.timeScale = 1;
                RuntimeHooks.Action = "wait";
                yield return null;
                yield return null;
                RuntimeHooks.Action = action;
                var started = Time.time;
                for (var i = 0; i < hold; i++) yield return null;
                RuntimeHooks.Action = "wait";
                // Opening the pause menu finishes the step at its current game time.
                while (Time.timeScale > 0 && Time.time - started < duration) yield return null;
                var deadline = Time.realtimeSinceStartup + 2f;
                while (Time.realtimeSinceStartup < deadline && UnityEngine.Object.FindObjectsOfType<Moving>().Any(m => m.inMovement)) yield return null;
                Time.timeScale = 0;
                visuals.ApplyPending();
                response["observation"] = Observation.Capture(this);
                response["reward"] = Events.Contains("death") ? -1.0 : Events.Contains("victory") ? 1.0 : Events.Contains("dialogue_entered") ? 1.0 : -0.01;
                response["terminated"] = Dead || Won || GoalReached;
                if (animationTrace != null) response["animation_frames"] = animationTrace;
                animationTrace = null;
            }
            else if (cmd == "release") { Release(); response["observation"] = Observation.Capture(this); }
            else if (cmd == "render")
            {
                if (Application.isBatchMode) yield return null;
                else yield return new WaitForEndOfFrame();
                var tex = Application.isBatchMode ? CaptureOffscreen() : ScreenCapture.CaptureScreenshotAsTexture();
                var png = tex.EncodeToPNG();
                UnityEngine.Object.Destroy(tex);
                response["png_base64"] = Convert.ToBase64String(png);
            }
            else if (cmd == "catalog") response["textures"] = visuals.Catalog();
            else if (cmd == "hierarchy") response["objects"] = Observation.Hierarchy();
            else if (cmd == "export_texture") response["texture"] = visuals.Export((string)body["key"]);
            else if (cmd == "reload_visuals") { visuals.Apply(); response["visuals"] = visuals.Status(); }
            else if (cmd == "visual_status") response["visuals"] = visuals.Status();
            else if (cmd == "localization_status") { Localization.ApplyFonts(); response["localization"] = Localization.Status(true); }
            else if (cmd == "music_test")
            {
                if (!RuntimeHooks.Controlled) throw new InvalidOperationException("Start a controlled episode with reset for music diagnostics");
                var source = Manager.instance ? Manager.instance.musicSource : null;
                if (!source) throw new InvalidOperationException("Load a scene with the music manager");
                var operation = (string)body["operation"] ?? "observe";
                if (operation == "select")
                {
                    var name = (string)body["track"];
                    var clip = Resources.FindObjectsOfTypeAll<AudioClip>().FirstOrDefault(c => c.name == name);
                    if (!clip) throw new ArgumentException("Choose a music clip loaded by the current scenes");
                    source.clip = clip; source.Play();
                }
                else if (operation == "seek")
                {
                    if (!source.clip) throw new InvalidOperationException("Select a loaded music clip first");
                    var sample = (long)Math.Round(((double?)body["seconds"] ?? 0) * source.clip.frequency);
                    source.timeSamples = (int)Math.Max(0, Math.Min(source.clip.samples - 1, sample));
                }
                else if (operation == "pause") source.Pause();
                else if (operation == "resume") source.UnPause();
                else if (operation == "mute") source.mute = (bool?)body["value"] ?? true;
                else if (operation != "observe") throw new ArgumentException("Music operations: observe, select, seek, pause, resume, mute");
                yield return null;
                yield return null;
                response["music"] = new JObject { ["track"] = source.clip ? source.clip.name : "", ["playing"] = source.isPlaying,
                    ["muted"] = source.mute, ["time_samples"] = source.timeSamples, ["sample_rate"] = source.clip ? source.clip.frequency : 0 };
                response["idle_sync"] = IdleSyncStatus;
            }
            else if (cmd == "shutdown") { Release(); request.Complete(response); Application.Quit(); yield break; }
            else throw new ArgumentException("Commands: observe, scenes, reset, step, release, render, catalog, hierarchy, export_texture, reload_visuals, visual_status, shutdown");
            request.Complete(response);
        }
        void TakeControl()
        {
            if (!RuntimeHooks.Controlled)
            {
                episodeScene = SceneManager.GetActiveScene().buildIndex;
                if (Manager.instance) { savedSteam = Manager.instance.steam; steamCaptured = true; }
            }
            RuntimeHooks.Training = true;
            RuntimeHooks.Controlled = true;
            Application.runInBackground = true;
            if (Manager.instance) Manager.instance.steam = false;
        }
        static Texture2D CaptureOffscreen()
        {
            var width = Math.Max(320, Screen.width);
            var height = Math.Max(180, Screen.height);
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>().Where(c => c.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
            var cameras = Camera.allCameras.OrderBy(c => c.depth).ToArray();
            var savedCameras = canvases.Select(c => c.worldCamera).ToArray();
            var savedDistances = canvases.Select(c => c.planeDistance).ToArray();
            try
            {
                foreach (var canvas in canvases) { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = Camera.main; canvas.planeDistance = 1f; }
                Canvas.ForceUpdateCanvases();
                foreach (var camera in cameras)
                {
                    var originalTarget = camera.targetTexture;
                    try { camera.targetTexture = rt; camera.Render(); }
                    finally { camera.targetTexture = originalTarget; }
                }
                RenderTexture.active = rt;
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.name = "AILab_capture";
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture;
            }
            finally
            {
                for (var i = 0; i < canvases.Length; i++) { canvases[i].renderMode = RenderMode.ScreenSpaceOverlay; canvases[i].worldCamera = savedCameras[i]; canvases[i].planeDistance = savedDistances[i]; }
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        void Release()
        {
            RuntimeHooks.Action = "wait";
            RuntimeHooks.Controlled = false;
            // Training profile remains isolated for the current scene after control returns to the player.
            Time.timeScale = 1;
            Application.runInBackground = savedBackground;
            Logger.LogInfo("Player control active; training profile retained until game restart");
        }
        internal void Event(string name)
        {
            if (!RuntimeHooks.Controlled) return;
            Events.Add(name);
            if (name == "death") Dead = true;
            if (name == "victory") Won = true;
            if (name == "dialogue_entered" && SceneManager.GetActiveScene().buildIndex > 0) GoalReached = true;
        }
        void OnDestroy()
        {
            SceneManager.sceneLoaded -= Loaded;
            server?.Dispose();
            harmony?.UnpatchSelf();
            Time.timeScale = 1;
            Application.runInBackground = savedBackground;
            if (steamCaptured && Manager.instance) Manager.instance.steam = savedSteam;
        }
    }
}
