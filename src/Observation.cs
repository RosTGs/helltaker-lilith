using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HelltakerAILab
{
    internal static class Observation
    {
        public static Player Player => Object.FindObjectOfType<Player>();
        public static JArray XY(Vector3 v) => new JArray(Math.Round(v.x, 4), Math.Round(v.y, 4));
        static int gridScene = -1;
        static Vector2 gridOffset;
        public static JArray Cell(Vector3 v) => new JArray(Mathf.RoundToInt(v.x - gridOffset.x), Mathf.RoundToInt(v.y - gridOffset.y));
        public static bool Ready(Player p) => p && Manager.instance && Manager.instance.playerTurn && !p.frozen && !p.inMenu && !p.inMovement;

        public static JObject PlayerVisual()
        {
            var p = Player;
            var renderer = p ? p.GetComponent<SpriteRenderer>() : null;
            var sprite = renderer ? renderer.sprite : null;
            var result = new JObject { ["frame"] = Time.frameCount, ["time"] = Time.time };
            if (!p || !sprite) return result;
            result["sprite"] = sprite.name;
            result["texture_key"] = VisualPacks.Key(sprite.texture);
            result["position"] = XY(p.transform.position);
            result["scale"] = XY(p.transform.localScale);
            result["pivot"] = new JArray(sprite.pivot.x, sprite.pivot.y);
            result["pixels_per_unit"] = sprite.pixelsPerUnit;
            result["geometry"] = SpriteGeometry(sprite);
            if (Camera.main)
            {
                var screen = Camera.main.WorldToScreenPoint(p.transform.position);
                result["screen_position"] = new JArray(screen.x, Screen.height - screen.y);
            }
            result["moving"] = p.inMovement;
            result["idle_sync"] = Plugin.Instance ? Plugin.Instance.IdleSyncStatus : null;
            var animator = p.GetComponent<Animator>();
            if (animator) result["animation_speed"] = animator.speed;
            return result;
        }

        internal static JObject SpriteGeometry(Sprite sprite) => new JObject {
            ["instance_id"] = sprite.GetInstanceID(),
            ["texture_id"] = sprite.texture.GetInstanceID(),
            ["rect"] = new JArray(sprite.rect.x, sprite.rect.y, sprite.rect.width, sprite.rect.height),
            ["vertices"] = new JArray(sprite.vertices.Select(v => new JArray(v.x, v.y))),
            ["triangles"] = new JArray(sprite.triangles),
            ["uv"] = new JArray(sprite.uv.Select(v => new JArray(v.x, v.y)))
        };

        public static JObject Capture(Plugin plugin)
        {
            var scene = SceneManager.GetActiveScene();
            var p = Player;
            if (p && gridScene != scene.handle)
            {
                gridScene = scene.handle;
                var position = p.transform.position;
                gridOffset = new Vector2(position.x - Mathf.Floor(position.x), position.y - Mathf.Floor(position.y));
            }
            var entities = new JArray();
            foreach (var asset in Object.FindObjectsOfType<Asset>().OrderBy(x => x.GetInstanceID()))
            {
                var e = Entity(asset.gameObject, asset.boxCollider);
                e["kind"] = asset.voidSpace ? "void" : asset.tag;
                e["blocking"] = asset.boxCollider && asset.boxCollider.enabled;
                e["moving"] = asset.inMovement;
                e["break_points"] = asset.breakPoints;
                entities.Add(e);
            }
            foreach (var spike in Object.FindObjectsOfType<Spikes>())
            {
                var e = Entity(spike.gameObject, spike.trigger);
                e["kind"] = "spikes"; e["up"] = spike.spikeUp; e["static"] = spike.spikeStatic;
                entities.Add(e);
            }
            var dialogues = new JArray();
            foreach (var goal in Object.FindObjectsOfType<GoalSprite>())
            {
                var input = RuntimeHooks.Read<bool>(goal, "waitingForInput");
                var choice = RuntimeHooks.Read<bool>(goal, "waitingForChoice");
                var trigger = goal.GetComponent<Collider2D>();
                if (trigger)
                {
                    var e = Entity(goal.gameObject, trigger); e["kind"] = "goal";
                    entities.Add(e);
                }
                if (input || choice)
                {
                    dialogues.Add(new JObject {
                        ["id"] = goal.GetInstanceID(), ["chapter"] = goal.chapterNumber,
                        ["speaker"] = goal.nameText ? goal.nameText.text : "",
                        ["text"] = goal.dialogueText ? goal.dialogueText.text : "",
                        ["waiting"] = choice ? "choice" : "confirm",
                        ["selected"] = RuntimeHooks.Read<int>(goal, "dialogueIndex"),
                        ["choices"] = choice ? new JArray(goal.buttonTextArray.Select(t => t ? t.text : "")) : new JArray()
                    });
                }
            }
            var assetIds = new System.Collections.Generic.HashSet<int>(entities.OfType<JObject>().Select(e => (int)e["id"]));
            foreach (var c in Object.FindObjectsOfType<Collider2D>())
            {
                if (assetIds.Contains(c.gameObject.GetInstanceID()) || (p && c.gameObject == p.gameObject)) continue;
                if (new[] { "key", "armor", "lockbox", "sinHurt", "sinSpike", "skull", "utility", "wall" }.Contains(c.tag))
                {
                    var e = Entity(c.gameObject, c); e["kind"] = c.tag; entities.Add(e);
                }
            }
            var texts = new JArray(Object.FindObjectsOfType<Text>().Where(t => t.enabled && !String.IsNullOrEmpty(t.text))
                .Select(t => new JObject { ["name"] = t.name, ["text"] = t.text }));
            var result = new JObject {
                ["schema_version"] = 1, ["episode"] = plugin.Episode, ["step"] = plugin.StepIndex,
                ["frame"] = Time.frameCount, ["game_time"] = Time.time,
                ["scene"] = new JObject { ["index"] = scene.buildIndex, ["name"] = scene.name },
                ["mode"] = RuntimeHooks.Training ? "training" : "play",
                ["controlled"] = RuntimeHooks.Controlled,
                ["phase"] = plugin.Dead ? "dead" : dialogues.Count > 0 ? "dialogue" : Ready(p) ? "puzzle" : p && p.inMenu ? "menu" : "animation",
                ["ready"] = Ready(p), ["dead"] = plugin.Dead, ["victory"] = plugin.Won,
                ["goal_reached"] = plugin.GoalReached, ["entities"] = entities, ["dialogues"] = dialogues,
                ["ui_text"] = texts, ["events"] = new JArray(plugin.Events),
                ["actions"] = new JArray("up", "down", "left", "right", "confirm", "wait", "cancel", "restart", "advice"),
                ["coordinates"] = "Unity world units; +x right, +y up; one grid action spans one unit"
            };
            result["grid_offset"] = new JArray(gridOffset.x, gridOffset.y);
            result["player_visual"] = PlayerVisual();
            result["idle_sync"] = plugin.IdleSyncStatus;
            result["player"] = p ? new JObject {
                ["position"] = XY(p.transform.position), ["cell"] = Cell(p.transform.position), ["will"] = p.will,
                ["key"] = p.keyMaster, ["armored"] = p.armored, ["hearts_lost"] = p.nextHeart,
                ["protected"] = p.protection, ["moving"] = p.inMovement, ["frozen"] = p.frozen,
                ["in_menu"] = p.inMenu, ["cutscene"] = p.cutscene, ["blocking_mask"] = (int)p.blockingLayer
            } : null;
            var boss = Object.FindObjectOfType<bossHp>();
            result["boss"] = boss ? new JObject { ["name"] = boss.name, ["phase"] = Manager.instance ? Manager.instance.AMDphase : 0, ["health_scale"] = boss.bar ? boss.bar.localScale.x : 0f, ["level"] = boss.currentLevel } : null;
            var lab = Object.FindObjectOfType<LabScript>();
            result["lab"] = lab ? new JObject { ["pillars"] = lab.pillarCount, ["time_limit"] = lab.timeLimit, ["seconds"] = lab.secs ? lab.secs.text : "", ["phase"] = Manager.instance ? Manager.instance.AMDphase : 0 } : null;
            return result;
        }

        public static JArray Hierarchy()
        {
            var items = new JArray();
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>().Where(t => t.gameObject.scene.IsValid()))
            {
                var obj = transform.gameObject;
                var sprite = obj.GetComponent<SpriteRenderer>();
                var canvas = obj.GetComponent<Canvas>();
                var camera = obj.GetComponent<Camera>();
                items.Add(new JObject {
                    ["id"] = obj.GetInstanceID(), ["name"] = obj.name, ["parent"] = transform.parent ? transform.parent.gameObject.GetInstanceID() : 0,
                    ["active"] = obj.activeInHierarchy, ["tag"] = obj.tag, ["layer"] = obj.layer,
                    ["position"] = new JArray(transform.position.x, transform.position.y, transform.position.z),
                    ["components"] = new JArray(obj.GetComponents<Component>().Where(c => c).Select(c => c.GetType().FullName)),
                    ["sprite"] = sprite && sprite.sprite ? sprite.sprite.name : "",
                    ["texture_key"] = sprite && sprite.sprite && sprite.sprite.texture ? VisualPacks.Key(sprite.sprite.texture) : "",
                    ["canvas"] = canvas ? new JObject { ["mode"] = canvas.renderMode.ToString(), ["sorting_order"] = canvas.sortingOrder, ["camera"] = canvas.worldCamera ? canvas.worldCamera.name : "", ["plane"] = canvas.planeDistance } : null,
                    ["camera"] = camera ? new JObject { ["depth"] = camera.depth, ["mask"] = camera.cullingMask, ["near"] = camera.nearClipPlane, ["far"] = camera.farClipPlane } : null
                });
            }
            return items;
        }

        static JObject Entity(GameObject obj, Collider2D c)
        {
            var r = obj.GetComponent<SpriteRenderer>();
            return new JObject {
                ["id"] = obj.GetInstanceID(), ["name"] = obj.name, ["tag"] = obj.tag,
                ["position"] = XY(obj.transform.position), ["cell"] = Cell(obj.transform.position),
                ["layer"] = obj.layer, ["enabled"] = c && c.enabled,
                ["trigger"] = c && c.isTrigger,
                ["bounds_center"] = c ? XY(c.bounds.center) : XY(obj.transform.position),
                ["bounds_size"] = c ? XY(c.bounds.size) : new JArray(0, 0),
                ["sprite"] = r && r.sprite ? r.sprite.name : ""
            };
        }
        public static JArray Scenes()
        {
            var a = new JArray();
            for (var i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                a.Add(new JObject { ["index"] = i, ["path"] = SceneUtility.GetScenePathByBuildIndex(i) });
            return a;
        }
    }
}
