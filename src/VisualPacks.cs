using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HelltakerAILab
{
    internal sealed class VisualPacks
    {
        readonly string root;
        readonly ManualLogSource log;
        readonly Dictionary<int, byte[]> originals = new Dictionary<int, byte[]>();
        readonly Dictionary<int, Texture2D> changed = new Dictionary<int, Texture2D>();
        sealed class SpriteGeometry
        {
            public Sprite Sprite;
            public Vector2[] RectVertices;
            public ushort[] Triangles;
        }
        readonly Dictionary<int, SpriteGeometry> geometry = new Dictionary<int, SpriteGeometry>();
        readonly List<string> errors = new List<string>();
        int applied;
        bool pending = true;
        float nextScan;
        string packName = "original";
        readonly Dictionary<string, float> animationSpeeds = new Dictionary<string, float>();
        Animator animatedPlayer;
        float originalAnimationSpeed;
        readonly IdleDanceSync dance = new IdleDanceSync();
        public JObject DanceStatus() => dance.Status();

        void RestoreAnimationSpeed()
        {
            if (animatedPlayer) animatedPlayer.speed = originalAnimationSpeed;
            animatedPlayer = null;
        }

        public void TickAnimation()
        {
            var player = Observation.Player;
            var animator = player ? player.GetComponent<Animator>() : null;
            if (animator != animatedPlayer)
            {
                RestoreAnimationSpeed();
                if (animator) { animatedPlayer = animator; originalAnimationSpeed = animator.speed; }
            }
            if (!animator || !animator.isActiveAndEnabled || !animator.runtimeAnimatorController) return;
            var clips = animator.GetCurrentAnimatorClipInfo(0);
            var multiplier = clips.Length > 0 && animationSpeeds.TryGetValue(clips[0].clip.name, out var speed) ? speed : 1f;
            animator.speed = originalAnimationSpeed * multiplier;
            dance.Tick(player.GetComponent<SpriteRenderer>(), clips.Length>0 ? clips[0].clip.name : "");
        }

        public VisualPacks(string root, ManualLogSource log)
        {
            this.root = root; this.log = log;
            Directory.CreateDirectory(Path.Combine(root, "exports"));
            Directory.CreateDirectory(Path.Combine(root, "packs", "custom"));
            var config = Path.Combine(root, "visuals.json");
            if (!File.Exists(config)) File.WriteAllText(config, "{\"active_pack\":\"custom\"}");
            var manifest = Path.Combine(root, "packs", "custom", "pack.json");
            if (!File.Exists(manifest)) File.WriteAllText(manifest, "{\"name\":\"My visuals\",\"textures\":[]}");
        }
        public static string Key(Texture2D texture) => Regex.Replace(texture.name, "[^a-zA-Z0-9_.-]", "_") + "__" + texture.width + "x" + texture.height;
        static IEnumerable<Texture2D> Textures() => Resources.FindObjectsOfTypeAll<Texture2D>().Where(t => t && t.width > 4 && t.height > 4 && !t.name.StartsWith("AILab_"));
        public void Schedule() { pending = true; nextScan = Time.realtimeSinceStartup + 0.8f; }
        public void Tick() { if (pending && Time.realtimeSinceStartup >= nextScan) Apply(); }
        public void ApplyPending() { if (pending) Apply(); }
        public JArray Catalog()
        {
            var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            var result = new JArray();
            foreach (var group in Textures().GroupBy(Key).OrderBy(g => g.Key))
            {
                var t = group.First();
                result.Add(new JObject {
                    ["key"] = group.Key, ["name"] = t.name, ["width"] = t.width, ["height"] = t.height,
                    ["instances"] = group.Count(), ["format"] = t.format.ToString(),
                    ["sprites"] = new JArray(sprites.Where(s => s.texture == t).Select(s => new JObject {
                        ["name"] = s.name, ["rect"] = new JArray(s.rect.x, s.rect.y, s.rect.width, s.rect.height),
                        ["pivot"] = new JArray(s.pivot.x, s.pivot.y), ["pixels_per_unit"] = s.pixelsPerUnit,
                        ["geometry"] = Observation.SpriteGeometry(s)
                    }))
                });
            }
            return result;
        }
        static byte[] ReadPng(Texture2D source)
        {
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                copy.name = "AILab_export";
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                copy.Apply();
                return copy.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (copy) UnityEngine.Object.Destroy(copy);
            }
        }
        public JObject Export(string key)
        {
            var matches = Textures().Where(t => Key(t) == key).ToArray();
            if (matches.Length == 0) throw new ArgumentException("Load the scene containing this texture, then refresh catalog");
            var path = Path.Combine(root, "exports", key + ".png");
            var first = matches[0];
            File.WriteAllBytes(path, originals.TryGetValue(first.GetInstanceID(), out var original) ? original : ReadPng(first));
            return new JObject { ["key"] = key, ["path"] = path, ["width"] = first.width, ["height"] = first.height };
        }
        public void Apply()
        {
            RestoreAnimationSpeed(); animationSpeeds.Clear();
            dance.Configure(null);
            pending = false; errors.Clear(); applied = 0;
            foreach (var saved in geometry.Values)
                if (saved.Sprite) saved.Sprite.OverrideGeometry(saved.RectVertices, saved.Triangles);
            geometry.Clear();
            foreach (var pair in changed)
                if (pair.Value && originals.TryGetValue(pair.Key, out var png)) pair.Value.LoadImage(png, false);
            changed.Clear();
            try
            {
                var config = JObject.Parse(File.ReadAllText(Path.Combine(root, "visuals.json")));
                packName = (string)config["active_pack"] ?? "original";
                if (packName == "original") return;
                if (!Regex.IsMatch(packName, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("Pack names use letters, digits, underscore and dash");
                var folder = Path.GetFullPath(Path.Combine(root, "packs", packName)) + Path.DirectorySeparatorChar;
                var manifest = JObject.Parse(File.ReadAllText(Path.Combine(folder, "pack.json")));
                var speeds = manifest["animation_speeds"] as JObject;
                if (speeds != null)
                    foreach (var property in speeds.Properties())
                        animationSpeeds[property.Name] = Mathf.Clamp((float)property.Value, 0.25f, 8f);
                var textures = Textures().ToArray();
                foreach (var item in (JArray)manifest["textures"] ?? new JArray())
                {
                    try
                    {
                        var key = (string)item["key"];
                        var file = Path.GetFullPath(Path.Combine(folder, (string)item["file"]));
                        if (!file.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Texture files belong inside their pack folder");
                        var bytes = File.ReadAllBytes(file);
                        if (bytes.Length > 64 * 1024 * 1024) throw new ArgumentException("PNG size limit: 64 MiB");
                        var probe = new Texture2D(2, 2); probe.name = "AILab_probe";
                        int w, h;
                        try
                        {
                            if (!probe.LoadImage(bytes, false)) throw new ArgumentException("Provide a readable PNG image");
                            w = probe.width; h = probe.height;
                        }
                        finally { UnityEngine.Object.Destroy(probe); }
                        foreach (var texture in textures.Where(t => Key(t) == key))
                        {
                            if (texture.width != w || texture.height != h) throw new ArgumentException(key + ": use original atlas dimensions");
                            var id = texture.GetInstanceID();
                            if (!originals.ContainsKey(id)) originals[id] = ReadPng(texture);
                            var filter = texture.filterMode; var wrap = texture.wrapMode;
                            if (!texture.LoadImage(bytes, false)) throw new InvalidDataException("Unity image upload failed for " + key);
                            texture.filterMode = filter; texture.wrapMode = wrap;
                            changed[id] = texture; applied++;
                        }
                    }
                    catch (Exception e) { errors.Add(e.Message); log.LogWarning(e.Message); }
                }
                // A replacement silhouette uses the complete original sprite rectangle.
                foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (!sprite || !sprite.texture || !changed.ContainsKey(sprite.texture.GetInstanceID())) continue;
                    geometry[sprite.GetInstanceID()] = new SpriteGeometry {
                        Sprite = sprite,
                        // vertices reads local units; OverrideGeometry writes rectangle pixels.
                        RectVertices = sprite.vertices.Select(v => {
                            var pixel = v * sprite.pixelsPerUnit + sprite.pivot;
                            return new Vector2(Mathf.Clamp(pixel.x, 0, sprite.rect.width),
                                               Mathf.Clamp(pixel.y, 0, sprite.rect.height));
                        }).ToArray(),
                        Triangles = sprite.triangles
                    };
                    var rect = sprite.rect;
                    var corners = new[] {
                        Vector2.zero, new Vector2(0, rect.height),
                        new Vector2(rect.width, 0), new Vector2(rect.width, rect.height)
                    };
                    var triangles = new ushort[] { 0, 1, 2, 2, 1, 3 };
                    sprite.OverrideGeometry(corners, triangles);
                    var actual = sprite.vertices;
                    if (actual.Length != corners.Length || !sprite.triangles.SequenceEqual(triangles) ||
                        actual.Where((v, i) => Vector2.Distance(v * sprite.pixelsPerUnit + sprite.pivot, corners[i]) > 0.01f).Any())
                        throw new InvalidDataException("Sprite rectangle verification failed: " + sprite.name);
                }
                dance.Configure(manifest["idle_sync"] as JObject);
            }
            catch (Exception e) { errors.Add(e.Message); log.LogWarning(e.Message); }
            log.LogInfo("Visual pack " + packName + ": " + applied + " textures applied");
            // Release saved pixels for textures unloaded with the previous scene.
            var live = new HashSet<int>(Textures().Select(t => t.GetInstanceID()));
            foreach (var id in originals.Keys.Where(k => !live.Contains(k)).ToArray()) originals.Remove(id);
        }
        public JObject Status() => new JObject { ["pack"] = packName, ["applied"] = applied, ["sprite_geometry"] = geometry.Count, ["errors"] = new JArray(errors), ["root"] = Path.Combine(root, "packs") };
    }
}
