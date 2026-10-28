using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HarmonyLib;
using UnityEngine;

namespace HelltakerAILab
{
    public static class RuntimeHooks
    {
        public static bool Controlled;
        public static bool Training;
        public static string Action = "wait";
        public static readonly Dictionary<string, int> Profile = new Dictionary<string, int>();
        static Dictionary<string, int> localProfile = new Dictionary<string, int>();
        static string profilePath;
        public static bool IsolatedProfile;
        public static void ConfigureProfile(string path, bool isolated)
        {
            profilePath = path; IsolatedProfile = isolated;
            if (isolated && File.Exists(path))
                foreach (var entry in JObject.Parse(File.ReadAllText(path)).Properties()) localProfile[entry.Name] = (int)entry.Value;
        }

        public static float GetAxis(string name)
        {
            if (!Controlled) return Input.GetAxis(name);
            if (name == "Horizontal") return Action == "left" ? -1 : Action == "right" ? 1 : 0;
            if (name == "Vertical") return Action == "down" ? -1 : Action == "up" ? 1 : 0;
            return 0;
        }
        public static float GetAxisRaw(string name) => Controlled ? GetAxis(name) : Input.GetAxisRaw(name);
        public static bool GetButton(string name)
        {
            if (!Controlled) return Input.GetButton(name);
            return (name == "Submit" && Action == "confirm") || (name == "Cancel" && Action == "cancel")
                || (name == "Restart" && Action == "restart") || (name == "Advice" && Action == "advice");
        }
        public static int GetInt(string key, int fallback)
        {
            if (Training && Profile.TryGetValue(key, out var transient)) return transient;
            if (IsolatedProfile && localProfile.TryGetValue(key, out var local)) return local;
            return PlayerPrefs.GetInt(key, fallback);
        }
        public static void SetInt(string key, int value)
        {
            if (Training) Profile[key] = value;
            else if (IsolatedProfile)
            {
                localProfile[key] = value;
                var json = new JObject();
                foreach (var entry in localProfile) json[entry.Key] = entry.Value;
                File.WriteAllText(profilePath, json.ToString());
            }
            else PlayerPrefs.SetInt(key, value);
        }

        public static void Install(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(RuntimeHooks), nameof(RewriteCalls));
            var methods = typeof(Player).Assembly.GetTypes().SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
            foreach (var method in methods)
            {
                if (method.IsAbstract || method.GetMethodBody() == null) continue;
                var il = method.GetMethodBody().GetILAsByteArray();
                if (il.Length > 0) harmony.Patch(method, transpiler: transpiler);
            }
            harmony.Patch(AccessTools.Method(typeof(Player), "GameOver"), prefix: new HarmonyMethod(typeof(RuntimeHooks), nameof(OnDeath)));
            harmony.Patch(AccessTools.Method(typeof(Player), "Win"), prefix: new HarmonyMethod(typeof(RuntimeHooks), nameof(OnWin)));
            harmony.Patch(AccessTools.Method(typeof(GoalSprite), "OnTriggerEnter2D"), prefix: new HarmonyMethod(typeof(RuntimeHooks), nameof(OnGoal)));
            harmony.Patch(AccessTools.Method(typeof(Manager), "Awake"), postfix: new HarmonyMethod(typeof(RuntimeHooks), nameof(OnManager)));
        }

        public static IEnumerable<CodeInstruction> RewriteCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method)
                {
                    var replaceInput = method.DeclaringType == typeof(Input) &&
                        (method.Name == "GetAxis" || method.Name == "GetAxisRaw" || method.Name == "GetButton");
                    var replacePrefs = method.DeclaringType == typeof(PlayerPrefs) &&
                        (method.Name == "SetInt" || (method.Name == "GetInt" && method.GetParameters().Length == 2));
                    if (replaceInput || replacePrefs)
                        instruction.operand = AccessTools.Method(typeof(RuntimeHooks), method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
                    if (method.DeclaringType == typeof(File) && method.Name == "ReadAllLines" && method.GetParameters().Length == 1)
                        instruction.operand = AccessTools.Method(typeof(Localization), nameof(Localization.ReadAllLines));
                }
                yield return instruction;
            }
        }

        static void OnDeath(int i)
        {
            // Positive destinations and -1 are scene transitions requested by dialogue.
            if (i == -2) Plugin.Instance?.Event("death");
        }
        static void OnWin() => Plugin.Instance?.Event("victory");
        static void OnManager(Manager __instance) { if (IsolatedProfile || Training) __instance.steam = false; }
        static void OnGoal(GoalSprite __instance, Collider2D player)
        {
            if (player.CompareTag("player") && !Read<bool>(__instance, "triggered"))
                Plugin.Instance?.Event("dialogue_entered");
        }
        public static T Read<T>(object target, string field)
        {
            if (target == null) return default(T);
            var f = AccessTools.Field(target.GetType(), field);
            return f == null ? default(T) : (T)f.GetValue(target);
        }
    }
}
