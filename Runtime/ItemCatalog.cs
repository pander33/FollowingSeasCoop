using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Sync;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FollowingSeasCoop.Runtime
{
    // Diagnostic only: remembers the scene placement without changing an item's behaviour.
    internal static class ItemCatalog
    {
        private sealed class Placement
        {
            public string Key;
            public Vector3 Position;
            // Where it was at its Start in real space: the same on every copy, whatever its origin.
            public double X, Y, Z;
            public string Captured;
        }

        private static readonly ConditionalWeakTable<ObjectInteractionHandler, Placement> Placements =
            new ConditionalWeakTable<ObjectInteractionHandler, Placement>();

        public static void Install(Harmony harmony)
        {
            var start = AccessTools.Method(typeof(ObjectInteractionHandler), "Start") ??
                        throw new MissingMethodException(nameof(ObjectInteractionHandler), "Start");
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(ItemCatalog), nameof(Started)));
        }

        private static void Started(ObjectInteractionHandler __instance)
        {
            PatchGuard.Run(() =>
            {
                Placement placement = Remember(__instance, "Start");
                placement.Captured = "Start";
                CoopBehaviour.Instance?.Items?.PortableStarted(__instance, placement.Key);
            },
                error => Plugin.Log.LogError("[Items] Placement capture failed: " + error));
        }

        private static Placement Remember(ObjectInteractionHandler item, string captured)
        {
            return Placements.GetValue(item, value =>
            {
                var placement = new Placement
                {
                    Key = SceneKey(value.transform), Position = value.transform.position, Captured = captured
                };
                CoordSpace.LocalToReal(placement.Position, out placement.X, out placement.Y, out placement.Z);
                return placement;
            });
        }

        internal static string KeyOf(ObjectInteractionHandler item) =>
            Placements.TryGetValue(item, out Placement placement) && placement.Captured == "Start" ? placement.Key : null;

        /// <summary>The thing is no longer where its scene put it: somebody carried it off.</summary>
        internal static bool MovedFromStart(ObjectInteractionHandler item, float metres)
        {
            if (!Placements.TryGetValue(item, out Placement placement) || placement.Captured != "Start") return false;
            CoordSpace.LocalToReal(item.transform.position, out double x, out double y, out double z);
            double dx = x - placement.X, dy = y - placement.Y, dz = z - placement.Z;
            return dx * dx + dy * dy + dz * dz > (double)metres * metres;
        }

        // Same-name ordinal at every level, including roots. Escape delimiters in Unity names.
        private static string SceneKey(Transform transform)
        {
            var parts = new List<string>();
            for (Transform t = transform; t != null; t = t.parent)
            {
                int ordinal = 0;
                if (t.parent != null)
                {
                    for (int i = 0; i < t.GetSiblingIndex(); i++)
                        if (t.parent.GetChild(i).name == t.name) ordinal++;
                }
                else
                {
                    foreach (GameObject root in t.gameObject.scene.GetRootGameObjects())
                        if (root.transform.GetSiblingIndex() < t.GetSiblingIndex() && root.name == t.name) ordinal++;
                }
                parts.Add(Uri.EscapeDataString(t.name) + "[" + ordinal + "]");
            }
            parts.Reverse();
            return Uri.EscapeDataString(transform.gameObject.scene.path) + ":/" + string.Join("/", parts);
        }

        private static T Read<T>(object target, string name)
        {
            if (target == null) return default;
            var field = AccessTools.Field(target.GetType(), name) ??
                        throw new MissingFieldException(target.GetType().Name, name);
            return (T)field.GetValue(target);
        }

        private static T[] Loaded<T>() where T : Component
        {
            return Resources.FindObjectsOfTypeAll<T>()
                .Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded)
                .OrderBy(value => SceneKey(value.transform), StringComparer.Ordinal).ToArray();
        }

        private static string V(Vector3 value) => value.ToString("F4");

        private static string ObjectInfo(GameObject obj)
        {
            if (obj == null) return "none";
            return obj.name + " | layer=" + obj.layer + " (" + LayerMask.LayerToName(obj.layer) + ")" +
                   " | active=" + obj.activeInHierarchy;
        }

        public static string Write(CoopBehaviour coop)
        {
            if (!GameAccess.InWorld) throw new InvalidOperationException("No world is loaded");
            var text = new StringBuilder();
            text.AppendLine("Following Seas item catalog I0 | " + DateTime.Now.ToString("O"));
            text.AppendLine("Plugin=" + Plugin.Version + " | protocol=" + Protocol.Version +
                            " | role=" + coop.Net.Role + " | NetId=" + coop.Net.MyNetId + " | game=" + Application.version);
            text.AppendLine("Capture patch=" + PatchHealth.StateOf("Item catalog") +
                            " | Start keys are candidates, not yet verified across copies.");
            CoordSpace.Offset(out double ox, out double oy, out double oz);
            text.AppendLine("Origin=" + ox.ToString("R", CultureInfo.InvariantCulture) + "," +
                            oy.ToString("R", CultureInfo.InvariantCulture) + "," + oz.ToString("R", CultureInfo.InvariantCulture));

            text.AppendLine("\n[Loaded scenes]");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                text.AppendLine(scene.path + " | name=" + scene.name + " | build=" + scene.buildIndex + " | loaded=" + scene.isLoaded);
            }

            ObjectInteractionHandler[] items = Loaded<ObjectInteractionHandler>();
            text.AppendLine("\n[Portable items] count=" + items.Length);
            foreach (ObjectInteractionHandler item in items)
            {
                Placement placement = Remember(item, "dump fallback (Start not observed)");
                text.AppendLine("key=" + placement.Key + " | captured=" + placement.Captured + " | instance=" + item.GetInstanceID());
                text.AppendLine("  current=" + SceneKey(item.transform) + " | " + ObjectInfo(item.gameObject) + " | tag=" + item.tag);
                text.AppendLine("  startPosition=" + V(placement.Position) + " | position=" + V(item.transform.position) +
                                " | rotation=" + item.transform.rotation.ToString("F4"));
                text.AppendLine("  fauna=" + Read<bool>(item, "isFauna") + " | purchase=" + item.allowPurchase +
                                " | purchased=" + Read<bool>(item, "purchased") + " | held=" + item.isHeld +
                                " | type=" + Read<ProvisionsController.ProvisionType>(item, "type") +
                                " | name=" + item.GetName() + " | holdYOffset=" + Read<float>(item, "holdYOffset"));
                Rigidbody body = item.GetComponent<Rigidbody>();
                text.AppendLine("  body=" + (body != null) + " | kinematic=" + (body != null && body.isKinematic) +
                                " | FixedJoint=" + (item.GetComponent<FixedJoint>() != null));
                foreach (Collider collider in item.GetComponentsInChildren<Collider>(true))
                    text.AppendLine("  collider=" + collider.GetType().Name + " | " + SceneKey(collider.transform) +
                                    " | trigger=" + collider.isTrigger + " | enabled=" + collider.enabled);
            }

            PlayerHandler player = GameAccess.Player;
            Transform camera = player.GetCameraObject()?.transform;
            text.AppendLine("\n[Player viewpoint]");
            Transform viewpoint = Read<Transform>(InteractionController.interactionInstance, "playerViewpoint");
            CapsuleCollider capsule = player.GetPlayerCollider();
            Vector3 feet = capsule != null
                ? capsule.transform.TransformPoint(capsule.center - Vector3.up * capsule.height * 0.5f)
                : player.transform.position;
            text.AppendLine("state=" + player.state + " | feet=" + V(feet) + " | crouchYScale=" + player.crouchYScale);
            text.AppendLine("viewpoint=" + (viewpoint != null ? SceneKey(viewpoint) : "none") +
                            " | offsetFromFeet=" + (viewpoint != null ? V(viewpoint.position - feet) : "unknown"));
            text.AppendLine("Capture once standing and once crouching; this is an observed pose, not a predicted height.");

            text.AppendLine("\n[Inventory buttons]");
            foreach (InventoryManager manager in Loaded<InventoryManager>())
            {
                text.AppendLine("manager=" + SceneKey(manager.transform));
                List<InventoryButton> buttons = Read<List<InventoryButton>>(manager, "buttons");
                if (buttons == null) { text.AppendLine("  buttons=none"); continue; }
                for (int i = 0; i < buttons.Count; i++)
                {
                    InventoryButton button = buttons[i];
                    if (button == null) { text.AppendLine("  button=" + i + " | none"); continue; }
                    GameObject tool = Read<GameObject>(button, "item");
                    text.AppendLine("  button=" + i + " | active=" + button.isActive +
                                    " | enableAsNew=" + Read<bool>(button, "enableAsNew") + " | item=" + ObjectInfo(tool) +
                                    " | prefab=" + ObjectInfo(Read<GameObject>(button, "itemPrefab")));
                    if (tool == null) continue;
                    if (camera != null)
                        text.AppendLine("    cameraLocalPosition=" + V(camera.InverseTransformPoint(tool.transform.position)) +
                                        " | cameraLocalRotation=" + (Quaternion.Inverse(camera.rotation) * tool.transform.rotation).ToString("F4") +
                                        " | localScale=" + V(tool.transform.localScale));
                    foreach (Component component in tool.GetComponentsInChildren<Component>(true))
                        text.AppendLine(component != null ? "    " + SceneKey(component.transform) + " | " +
                            component.GetType().FullName + " | layer=" + component.gameObject.layer : "    missing script");
                }
            }

            text.AppendLine("\n[Cargo prefabs]");
            List<GameObject> types = GameAccess.Dp.availableCargoTypes;
            if (types != null)
                for (int i = 0; i < types.Count; i++) Cargo(text, "available[" + i + "]", types[i]);
            foreach (TaskPanelHandler panel in Loaded<TaskPanelHandler>())
                foreach (GameObject product in new[] { panel.woodProductTask, panel.linenProductTask, panel.ironProductTask })
                {
                    TaskHandler task = product != null ? product.GetComponent<TaskHandler>() : null;
                    Cargo(text, "productTask=" + ObjectInfo(product), task != null ? task.cargo : null);
                }
            foreach (TaskHandler task in Loaded<TaskHandler>())
                Cargo(text, "task=" + task.id, task.cargo);

            text.AppendLine("\n[Ship cargo boxes]");
            ShipHullMover ship = GameAccess.ActiveShip();
            text.AppendLine("ship=" + (ship != null ? ship.id : "none"));
            List<CargoPlacer> boxes = Read<List<CargoPlacer>>(ship, "cargoBoxes");
            if (boxes != null)
                for (int i = 0; i < boxes.Count; i++)
                {
                    CargoPlacer box = boxes[i];
                    text.AppendLine("box=" + i + " | " + (box != null ? SceneKey(box.transform) : "none"));
                    if (box == null || box.transform.parent == null) continue;
                    foreach (Transform child in box.transform.parent)
                        if (child.GetComponent<CargoDefinition>() != null) Cargo(text, "  child=" + SceneKey(child), child.gameObject);
                }

            text.AppendLine("\n[Avatar / crew right hand]");
            foreach (Animator animator in Loaded<Animator>())
            {
                if (!animator.transform.root.name.StartsWith("CoopAvatar", StringComparison.Ordinal) &&
                    animator.GetComponentInParent<NewCrewController>() == null) continue;
                Transform hand = animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
                text.AppendLine(SceneKey(animator.transform) + " | human=" + animator.isHuman +
                                " | rightHand=" + (hand != null ? SceneKey(hand) : "none"));
            }

            string path = Path.Combine(Paths.BepInExRootPath, "FollowingSeasCoop-items-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + coop.Net.Role + ".txt");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text.ToString());
            return path;
        }

        private static void Cargo(StringBuilder text, string label, GameObject obj)
        {
            CargoDefinition cargo = obj != null ? obj.GetComponent<CargoDefinition>() : null;
            text.AppendLine(label + " | " + ObjectInfo(obj) + (cargo != null
                ? " | id=" + cargo.id + " | type=" + cargo.type + " | name=" + cargo.name + " | taskID=" + cargo.taskID +
                  " | localPosition=" + V(cargo.transform.localPosition) : " | CargoDefinition=none"));
        }
    }
}
