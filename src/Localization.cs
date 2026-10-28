using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace HelltakerAILab
{
    public static class Localization
    {
        static readonly Dictionary<string, string[]> texts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<int, FontState> fontStates = new Dictionary<int, FontState>();
        static readonly List<string> errors = new List<string>();
        sealed class FontState
        {
            public Text Text;
            public Font Font;
            public bool BestFit;
            public int Min, Max;
        }
        static string gameRoot, selected = "original";
        static Font font;
        static float nextScan;
        static int redirectedReads;
        static string glyphs = "";
        static Manager localizedManager;
        static bool Enabled => texts.Count > 0;

        internal static void Configure(string root, ManualLogSource log)
        {
            gameRoot = Path.GetFullPath(Path.GetDirectoryName(root)) + Path.DirectorySeparatorChar;
            var config = Path.Combine(root, "localization.json");
            if (!File.Exists(config)) return;
            try
            {
                selected = (string)JObject.Parse(File.ReadAllText(config))["active_pack"] ?? "original";
                if (selected == "original") return;
                if (!Regex.IsMatch(selected, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("Choose a localization pack identifier");
                var folder = Path.Combine(root, "localizations", selected);
                var manifest = JObject.Parse(File.ReadAllText(Path.Combine(folder, "pack.json")));
                var prepared = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in (JArray)manifest["files"])
                {
                    var key = (string)entry["path"];
                    if (!Regex.IsMatch(key, "^(local|localHM)/[A-Za-z0-9_]+\\.json$")) throw new ArgumentException("Use a game localization path: " + key);
                    var lines = File.ReadAllLines(Path.Combine(folder, key));
                    var original = File.ReadAllLines(Path.Combine(gameRoot, key));
                    if (lines.Length != original.Length || lines.Length != (int)entry["lines"])
                        throw new InvalidDataException("Dialogue line count must match: " + key);
                    prepared.Add(key, lines);
                }
                var fontName = (string)manifest["font"] ?? "Georgia";
                font = Font.CreateDynamicFontFromOSFont(new[] { fontName, "Times New Roman", "Arial" }, 32);
                if (!font) throw new InvalidOperationException("Install a Cyrillic font listed in the localization manifest");
                font.name = "AILab_Russian_" + fontName;
                UnityEngine.Object.DontDestroyOnLoad(font);
                glyphs = new string(prepared.Values.SelectMany(lines => lines).SelectMany(line => line)
                    .Where(c => c >= '\u0400' && c <= '\u04ff').Distinct().OrderBy(c => c).ToArray());
                font.RequestCharactersInTexture(glyphs, 32, FontStyle.Normal);
                var missing = new string(glyphs.Where(c => !font.HasCharacter(c)).ToArray());
                if (missing.Length > 0) throw new InvalidOperationException("Font requires these Cyrillic glyphs: " + missing);
                foreach (var pair in prepared) texts.Add(pair.Key, pair.Value);
                log.LogInfo("Localization " + selected + ": " + texts.Count + " files, " + texts.Values.Sum(x => x.Length) + " lines");
            }
            catch (Exception e) { texts.Clear(); errors.Add(e.Message); log.LogError(e); }
        }

        // Only calls originating in the game's managed assembly pass through this adapter.
        public static string[] ReadAllLines(string path)
        {
            if (Enabled)
            {
                var full = Path.GetFullPath(path);
                if (full.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var key = full.Substring(gameRoot.Length).Replace('\\', '/');
                    if (texts.TryGetValue(key, out var lines)) { redirectedReads++; return (string[])lines.Clone(); }
                }
            }
            return File.ReadAllLines(path);
        }

        internal static void Tick()
        {
            RefreshManager();
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + .25f;
            ApplyFonts();
        }

        internal static void RefreshManager()
        {
            var manager = Manager.instance;
            if (Enabled && manager && manager != localizedManager && !String.IsNullOrEmpty(manager.directory))
            {
                manager.menuTxt = ReadAllLines(Path.Combine(manager.directory, "local/m.json"));
                manager.dlcTxt = ReadAllLines(Path.Combine(manager.directory, "localHM/hm_m.json"));
                localizedManager = manager;
            }
        }

        internal static void ApplyFonts()
        {
            if (!Enabled || !font) return;
            foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
            {
                if (!text || !text.font || text.font == font) continue;
                // Segment7 keeps the timer's original digital appearance.
                if (text.font.name.StartsWith("Segment7", StringComparison.Ordinal)) continue;
                var id = text.GetInstanceID();
                if (!fontStates.ContainsKey(id)) fontStates.Add(id, new FontState {
                    Text=text, Font=text.font, BestFit=text.resizeTextForBestFit,
                    Min=text.resizeTextMinSize, Max=text.resizeTextMaxSize
                });
                var maximum = text.resizeTextForBestFit ? text.resizeTextMaxSize : text.fontSize;
                text.font = font;
                text.resizeTextForBestFit = true;
                text.resizeTextMaxSize = maximum;
                text.resizeTextMinSize = Math.Min(maximum, Math.Max(12, (int)(maximum * .65f)));
            }
            foreach (var id in fontStates.Where(p => !p.Value.Text).Select(p => p.Key).ToArray()) fontStates.Remove(id);
        }

        internal static JObject Status(bool includeUi) => new JObject {
            ["pack"]=selected, ["enabled"]=Enabled, ["files"]=texts.Count,
            ["lines"]=texts.Values.Sum(x => x.Length), ["redirected_reads"]=redirectedReads,
            ["font"]=font ? font.name : "", ["cyrillic_glyphs"]=glyphs,
            ["errors"]=new JArray(errors),
            ["ui"]=includeUi ? new JArray(UnityEngine.Object.FindObjectsOfType<Text>()
                .Where(t => t.isActiveAndEnabled && !String.IsNullOrWhiteSpace(t.text))
                .Select(t => new JObject {
                    ["name"]=t.name, ["text"]=t.text, ["font"]=t.font ? t.font.name : "",
                    ["rect"]=new JArray(t.rectTransform.rect.width, t.rectTransform.rect.height),
                    ["font_size"]=t.fontSize, ["best_fit_size"]=t.cachedTextGenerator.fontSizeUsedForBestFit,
                    ["visible_characters"]=t.cachedTextGenerator.characterCountVisible,
                    ["generated_lines"]=t.cachedTextGenerator.lineCount
                })) : null
        };
    }
}
