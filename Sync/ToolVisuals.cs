using System;
using System.Collections.Generic;
using System.Reflection;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The tool another player has out: quadrant, spyglass, chronometer, lantern, compass, caulking
    /// hammer, log, gun. Which one is the index of the active button in the game's
    /// <c>InventoryManager.buttons</c>, plus one; the list is the same on every copy.
    ///
    /// What the others see is a copy of this copy's own tool of that slot with everything but its
    /// meshes taken off: no scripts (they read input, move the camera, mend leaks, change the sky),
    /// no cameras, sounds, bodies or colliders, and none of the first-person arms. The game builds
    /// a tool in the space of its owner's camera, so the copy is put at the other player's eye,
    /// turned the way that player looks — where its owner sees it. A tool with a light that is not
    /// on the tool's own layer (the lantern) keeps its lights and particles: the flame.
    /// </summary>
    internal sealed class ToolVisuals
    {
        private const float SwingSeconds = 0.3f;
        private const float SwingDegrees = 40f;
        /// <summary>The eyes above the head bone of a crew model, which sits at the top of the neck.</summary>
        private const float HeadToEye = 0.08f;

        private static readonly FieldInfo ButtonsField = AccessTools.Field(typeof(InventoryManager), "buttons");
        private static readonly FieldInfo ItemField = AccessTools.Field(typeof(InventoryButton), "item");
        private static readonly FieldInfo HammerReadyField = AccessTools.Field(typeof(CaulkHammerController), "animComplete");
        private static readonly FieldInfo SpyglassAimedField = AccessTools.Field(typeof(SpyglassController), "aimed");
        private static readonly FieldInfo QuadrantAnimatorField = AccessTools.Field(typeof(QuadrantController), "quadrantAnimations");
        private static readonly FieldInfo NeedleField = AccessTools.Field(typeof(QuadrantController), "needleObject");
        private static readonly FieldInfo GunAnimatorField = AccessTools.Field(typeof(GunEvents), "animator");
        private static readonly FieldInfo GunSoundsField = AccessTools.Field(typeof(GunEvents), "fireSounds");
        private static readonly FieldInfo StartEffectField = AccessTools.Field(typeof(GunEvents), "startEffect");
        private static readonly FieldInfo FireEffectField = AccessTools.Field(typeof(GunEvents), "fireEffect");
        private static readonly FieldInfo HourHandField = AccessTools.Field(typeof(ChronoController), "hourHand");
        private static readonly FieldInfo MinuteHandField = AccessTools.Field(typeof(ChronoController), "minuteHand");

        // The local eye above the feet, as last seen standing and crouching. Every player has the
        // same body, so this copy's own numbers place the other players' tools.
        private static float s_eyeStanding = 1.6f, s_eyeCrouching = 1f;
        private static bool s_crouchMeasured;

        private static InventoryManager s_manager;
        private static float s_managerSoughtAt = -10f;
        private static byte s_useCount;

        // -----------------------------------------------------------------
        // The local player's tool
        // -----------------------------------------------------------------

        private static List<InventoryButton> Buttons()
        {
            if (ButtonsField == null || ItemField == null) return null;
            if (s_manager == null && Time.unscaledTime - s_managerSoughtAt > 1f)
            {
                s_managerSoughtAt = Time.unscaledTime;
                foreach (InventoryManager manager in UnityEngine.Object.FindObjectsByType<InventoryManager>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!manager.disabledInScene && manager.gameObject.scene.buildIndex == GameAccess.WorldScene)
                    {
                        s_manager = manager;
                        break;
                    }
            }
            return s_manager != null ? ButtonsField.GetValue(s_manager) as List<InventoryButton> : null;
        }

        /// <summary>The tool the local player has out: its slot plus one, 0 for none.</summary>
        public static byte LocalKind()
        {
            List<InventoryButton> buttons = Buttons();
            if (buttons == null) return 0;
            for (int i = 0; i < buttons.Count && i < 254; i++)
                if (buttons[i] != null && buttons[i].isActive) return (byte)(i + 1);
            return 0;
        }

        /// <summary>Grows by one with every blow of the local caulking hammer and every shot of the gun.</summary>
        public static byte LocalUseCount => s_useCount;

        private static GameObject s_stateItem;
        private static SpyglassController s_spyglass;
        private static Animator s_quadrant, s_gun;
        private static bool s_firing;

        /// <summary>
        /// Whether the local tool is raised — the spyglass or the quadrant to the eye, the gun to the
        /// shoulder — and counts the gun's shots. The game's own flag of each tool; "fire" stays set
        /// for the whole shot, so a look at every pose sent sees it.
        /// </summary>
        public static bool LocalRaised(byte kind)
        {
            List<InventoryButton> buttons = Buttons();
            var item = kind != 0 && buttons != null && kind <= buttons.Count && buttons[kind - 1] != null
                ? ItemField.GetValue(buttons[kind - 1]) as GameObject : null;
            if (item != s_stateItem)
            {
                s_stateItem = item;
                s_spyglass = item != null ? item.GetComponentInChildren<SpyglassController>(true) : null;
                QuadrantController quadrant = item != null ? item.GetComponentInChildren<QuadrantController>(true) : null;
                s_quadrant = quadrant != null ? QuadrantAnimatorField?.GetValue(quadrant) as Animator : null;
                GunEvents gun = item != null ? item.GetComponentInChildren<GunEvents>(true) : null;
                s_gun = gun != null ? GunAnimatorField?.GetValue(gun) as Animator : null;
                s_firing = false;
            }
            if (item == null) return false;
            if (s_spyglass != null) return SpyglassAimedField != null && (bool)SpyglassAimedField.GetValue(s_spyglass);
            if (s_quadrant != null) return s_quadrant.isActiveAndEnabled && s_quadrant.GetInteger("zoomLevel") > 0;
            if (s_gun == null || !s_gun.isActiveAndEnabled) return false;
            bool firing = s_gun.GetBool("fire");
            if (firing && !s_firing) s_useCount++;
            s_firing = firing;
            return s_gun.GetBool("ready");
        }

        /// <summary>Called with every local pose: remembers how high the eye is above the feet.</summary>
        public static void Measure(PlayerHandler player, Vector3 feet, Vector3 up)
        {
            GameObject camera = null;
            try { camera = player.GetCameraObject(); } catch { }
            if (camera == null) return;
            float height = Vector3.Dot(camera.transform.position - feet, up);
            if (height < 0.2f || height > 3f) return;
            if (PlayerSync.IsCrouching(player)) { s_eyeCrouching = height; s_crouchMeasured = true; }
            else if (player.state != PlayerHandler.MovementState.swimming)
            {
                s_eyeStanding = height;
                if (!s_crouchMeasured) s_eyeCrouching = height * 0.6f;
            }
        }

        // -----------------------------------------------------------------
        // Another player's tool
        // -----------------------------------------------------------------

        // How a tool is held seen from outside. The first-person model is built for its owner's
        // camera: five times its size and off to the side, so the copy is brought to a real size
        // and put where hands hold it.
        private enum Hold { Lantern, Spyglass, Quadrant, Gun, TwoHands, OneHand }

        private GameObject _tool;
        private byte _kind, _use;
        private bool _hasUse, _raised;
        private float _swingAt = -10f, _fireAt = -10f, _retryAt;
        // What the tool shows, on the copy: the quadrant's plumb needle, the chronometer's hands.
        private Transform _needle, _hourHand, _minuteHand;
        // The gun's shot: its sounds and its two effects in the game's order, and the events of its
        // firing clip, played on the copy as the clip plays them.
        private AudioSource[] _sounds = new AudioSource[0];
        private readonly ParticleSystem[] _effects = new ParticleSystem[2];
        private readonly List<AnimationEvent> _shot = new List<AnimationEvent>();
        private float _bangAt = 0.8f;
        private byte _warnedKind;
        private Hold _hold;
        private bool _measured;
        // The drawn box of the copy in the frame it is held in, at its real size; the turn that lays
        // a long tool along that frame's forward; the frame of this frame's placing.
        private Vector3 _center, _extents;
        private Quaternion _align = Quaternion.identity, _frame = Quaternion.identity;
        // The meshes of the tool itself, which its size is taken from: the log's copy also has a
        // long piece of line on another layer.
        private readonly List<Renderer> _body = new List<Renderer>();

        /// <summary>The tool is held to the eye: it stays there whatever the hands do.</summary>
        public bool AtEye => _tool != null && _raised && (_hold == Hold.Spyglass || _hold == Hold.Quadrant);

        /// <summary>Moves the tool for this frame, into a hand that could not quite reach it.</summary>
        public void Shift(Vector3 by)
        {
            if (_tool != null) _tool.transform.position += by;
        }

        /// <summary>Every frame after the avatar is placed.</summary>
        public void Apply(GameObject avatar, Transform head, float pitch, bool crouching, byte kind, byte use, bool raised)
        {
            float now = Time.unscaledTime;
            if (kind != _kind || (kind != 0 && _tool == null && now >= _retryAt))
            {
                Drop();
                _kind = kind;
                if (kind != 0)
                {
                    try { _tool = Build(kind, avatar); }
                    catch (Exception e)
                    {
                        // Tried again every two seconds: said once per tool.
                        if (_warnedKind != kind) Plugin.Log.LogWarning("[Tools] No copy of the tool in slot " + (kind - 1) + ": " + e.Message);
                        _warnedKind = kind;
                    }
                    // This copy's own tool of that slot may not exist yet (the gun is made anew each time).
                    if (_tool == null) _retryAt = now + 2f;
                }
            }
            if (_tool == null) return;
            if (!_measured && !Measure())
            {
                // Nothing of it is drawn to measure: no copy rather than one left where it was measured.
                if (_warnedKind != kind) Plugin.Log.LogWarning("[Tools] The copy of the tool in slot " + (kind - 1) + " has nothing drawn to hold");
                _warnedKind = kind;
                UnityEngine.Object.Destroy(_tool);
                _tool = null; _body.Clear();
                _retryAt = now + 2f;
                return;
            }

            float firing = now - _fireAt;
            if (_hasUse && use != _use)
            {
                if (_hold != Hold.Gun) _swingAt = now;
                // A shot already playing is not cut short by the next.
                else if (firing > ShotLength()) { _fireAt = now; firing = 0f; }
            }
            _use = use; _hasUse = true;
            _raised = raised;
            if (_hold == Hold.Gun) PlayShot(firing, firing - Time.unscaledDeltaTime);

            Transform body = avatar.transform;
            Vector3 up = body.up, right = body.right;
            pitch = Mathf.Clamp(pitch, -70f, 70f);
            Quaternion look = body.rotation * Quaternion.Euler(pitch, 0f, 0f);
            Vector3 ahead = look * Vector3.forward;
            ShowReadings(pitch);
            // The avatar's own head where it has one: its crouch is not the camera's.
            Vector3 eye = head != null ? head.position + up * HeadToEye
                : body.position + up * (crouching ? s_eyeCrouching : s_eyeStanding);

            // The point of the tool (in its frame) that goes to a place by the body.
            Quaternion rotation = look;
            Vector3 point = _center, at;
            switch (_hold)
            {
                case Hold.Lantern:
                    // Hangs upright from the handle, held up before the chest.
                    rotation = body.rotation;
                    point = _center + Vector3.up * _extents.y;
                    at = eye - up * 0.25f + right * 0.2f + body.forward * 0.38f;
                    break;
                case Hold.Spyglass when _raised:
                case Hold.Quadrant when _raised:
                    point = _center - Vector3.forward * _extents.z;
                    at = eye + ahead * (_hold == Hold.Spyglass ? 0.04f : 0.06f);
                    break;
                case Hold.Spyglass:
                    // Down in the right hand, pointing ahead and down.
                    rotation = body.rotation * Quaternion.Euler(35f, 0f, 0f);
                    at = eye - up * 0.5f + right * 0.2f + body.forward * 0.25f;
                    break;
                case Hold.Gun:
                    // The barrel along the look, held at the chest; aiming raises it to the neck.
                    point = _center - Vector3.forward * _extents.z;
                    at = _raised
                        ? eye - up * 0.14f + right * 0.1f + ahead * 0.05f
                        : eye - up * 0.3f + right * 0.1f + ahead * 0.05f;
                    float kick = firing - _bangAt;
                    if (kick >= 0f && kick < 0.25f) rotation *= Quaternion.Euler(-8f * Mathf.Sin(Mathf.PI * kick / 0.25f), 0f, 0f);
                    break;
                case Hold.OneHand:
                    at = eye - up * 0.35f + right * 0.15f + ahead * 0.35f;
                    break;
                default:
                    at = eye - up * 0.3f + ahead * 0.35f;
                    break;
            }
            float swing = now - _swingAt;
            if (swing < SwingSeconds) rotation *= Quaternion.Euler(-SwingDegrees * Mathf.Sin(Mathf.PI * swing / SwingSeconds), 0f, 0f);
            _frame = rotation;
            _tool.transform.SetPositionAndRotation(at - rotation * point, rotation * _align);
        }

        // The needle hangs plumb whatever the look (the game turns it by the camera's pitch, in the
        // same frame as the copy's); the chronometer's hands go by this copy's time of day, which
        // is the host's.
        private void ShowReadings(float pitch)
        {
            if (_needle != null) _needle.localRotation = Quaternion.Euler(-90f, pitch + (pitch > 0f ? 0.8f : -0.8f), 0f);
            if (_hourHand == null && _minuteHand == null) return;
            Enviro.EnviroManager enviro = DataPersistenceManager.dpInstance != null ? DataPersistenceManager.dpInstance.enviro : null;
            if (enviro == null || enviro.Time == null) return;
            float minutes = enviro.Time.minutes;
            if (_minuteHand != null) _minuteHand.localRotation = Quaternion.Euler(0f, minutes * 6f, 0f);
            if (_hourHand != null) _hourHand.localRotation = Quaternion.Euler(0f, (enviro.Time.hours % 12 + minutes / 60f) * 30f, 0f);
        }

        private float ShotLength() => _shot.Count > 0 ? _shot[_shot.Count - 1].time : 0f;

        // The events of the clip that fall between the last frame and this one.
        private void PlayShot(float now, float before)
        {
            if (now < 0f || before > ShotLength()) return;
            foreach (AnimationEvent e in _shot)
            {
                if (e.time > now || e.time <= before) continue;
                int i = e.intParameter;
                if (e.functionName == "PlayFireSound" && i >= 0 && i < _sounds.Length && _sounds[i] != null) _sounds[i].Play();
                else if (e.functionName == "PlayEffectEvent" && i >= 0 && i < _effects.Length && _effects[i] != null) _effects[i].Play(true);
            }
        }

        /// <summary>
        /// Where the avatar's hands close on the tool this frame: the right one always, the left one
        /// too for a tool held in both hands.
        /// </summary>
        public bool Grips(out Vector3 right, out Vector3 left, out bool both)
        {
            right = left = default; both = false;
            if (_tool == null || !_measured) return false;
            Vector3 origin = _tool.transform.position;
            Vector3 c = _center, e = _extents;
            Vector3 r, l = c;
            switch (_hold)
            {
                case Hold.Lantern:
                    r = c + Vector3.up * e.y;
                    break;
                case Hold.Spyglass when !_raised:
                    r = c;
                    break;
                case Hold.Spyglass:
                    // Each hand from its own side of the tube, so the arms do not cross.
                    r = c - Vector3.forward * (e.z * 0.45f) - Vector3.up * e.y + Vector3.right * (e.x + 0.02f);
                    l = c + Vector3.forward * (e.z * 0.3f) - Vector3.up * e.y - Vector3.right * (e.x + 0.02f);
                    both = true;
                    break;
                case Hold.Gun:
                    // The right hand at the grip behind the trigger, the left under the fore-end.
                    r = c - Vector3.forward * (e.z * 0.6f) - Vector3.up * (e.y - 0.02f) + Vector3.right * (e.x + 0.03f);
                    l = c + Vector3.forward * (e.z * 0.15f) - Vector3.up * (e.y - 0.02f) - Vector3.right * (e.x + 0.03f);
                    both = true;
                    break;
                case Hold.TwoHands:
                case Hold.Quadrant:
                    r = c + Vector3.right * (e.x + 0.03f);
                    l = c - Vector3.right * (e.x + 0.03f);
                    both = true;
                    break;
                default:
                    // The handle, a third of the way up.
                    r = c - Vector3.up * (e.y * 0.35f);
                    break;
            }
            right = origin + _frame * r;
            left = origin + _frame * l;
            return true;
        }

        public bool PalmGrips(out PalmGrip right, out PalmGrip left, out bool both)
        {
            right = left = default;
            if (!Grips(out Vector3 r, out Vector3 l, out both)) return false;
            Vector3 forward = _frame * Vector3.forward;
            Vector3 up = _frame * Vector3.up;
            Vector3 side = _frame * Vector3.right;
            if (_hold == Hold.Lantern)
            {
                right = new PalmGrip(r, forward, -up, FingerGrip.Handle);
            }
            else if (_tool.name.Contains("hammer"))
            {
                right = new PalmGrip(r, forward, -side, FingerGrip.Handle);
            }
            else if (_hold == Hold.Gun)
            {
                right = new PalmGrip(r, up, -side, FingerGrip.Trigger);
                left = new PalmGrip(l, forward, up, FingerGrip.Support);
            }
            else if (_hold == Hold.Spyglass)
            {
                right = new PalmGrip(r, up, -side, FingerGrip.Cylinder);
                left = new PalmGrip(l, up, side, FingerGrip.Cylinder);
            }
            else if (_hold == Hold.TwoHands || _hold == Hold.Quadrant)
            {
                right = new PalmGrip(r, up, -side, FingerGrip.Side);
                left = new PalmGrip(l, up, side, FingerGrip.Side);
            }
            else
            {
                right = new PalmGrip(r, forward, up, FingerGrip.Handle);
                left = new PalmGrip(l, forward, up, FingerGrip.Handle);
            }
            return true;
        }

        // A real size, metres, for the longest side of each tool.
        private static float RealSize(Hold hold, string name)
        {
            switch (hold)
            {
                case Hold.Lantern: return 0.35f;
                case Hold.Spyglass: return 0.45f;
                case Hold.Gun: return 1.1f;
                case Hold.TwoHands:
                case Hold.Quadrant: return 0.3f;
            }
            return name.Contains("chronometer") ? 0.12f : name.Contains("hammer") ? 0.35f : 0.25f;
        }

        private static Hold HoldOf(string name)
        {
            if (name.Contains("lantern")) return Hold.Lantern;
            if (name.Contains("spyglass")) return Hold.Spyglass;
            if (name.Contains("FPSController")) return Hold.Gun;
            if (name.Contains("quadrant")) return Hold.Quadrant;
            if (name.Contains("compass")) return Hold.TwoHands;
            return Hold.OneHand;
        }

        // Once, on the first frame of a copy: its size brought to a real one, its box in its own frame.
        private bool Measure()
        {
            Transform t = _tool.transform;
            _hold = HoldOf(_tool.name);
            _align = _hold == Hold.Gun ? LongAxis(_tool) : Quaternion.identity;
            t.SetPositionAndRotation(Vector3.zero, _align);
            KeepMainPart();
            if (!BodyBounds(out Bounds box)) return false;
            float longest = Mathf.Max(box.size.x, Mathf.Max(box.size.y, box.size.z));
            if (longest > 0.001f) t.localScale *= RealSize(_hold, _tool.name) / longest;
            if (!BodyBounds(out box)) return false;
            _center = box.center; _extents = box.extents;
            _measured = true;
            return true;
        }

        // A first-person tool carries more than the tool on its layer: the hammer's chisel and a brace,
        // spheres and a curve by the gun. What is seen in a hand is the main part and what touches
        // it; the rest is hidden on the copy, and is not measured.
        private void KeepMainPart()
        {
            Renderer main = null;
            float largest = -1f;
            bool hammer = _tool.name.Contains("hammer");
            foreach (Renderer renderer in _body)
            {
                if (renderer == null || !renderer.enabled) continue;
                Vector3 s = renderer.bounds.size;
                float volume = s.x * s.y * s.z;
                bool named = hammer && renderer.name.Contains("hammer");
                if (named) volume += 1e6f;
                if (volume > largest) { largest = volume; main = renderer; }
            }
            if (main == null) return;
            Bounds near = main.bounds;
            near.Expand(near.size.magnitude * 0.3f);
            foreach (Renderer renderer in _body)
                if (renderer != null && !renderer.bounds.Intersects(near)) renderer.enabled = false;
        }

        private bool BodyBounds(out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer renderer in _body)
            {
                if (renderer == null || !renderer.enabled) continue;
                if (any) bounds.Encapsulate(renderer.bounds);
                else { bounds = renderer.bounds; any = true; }
            }
            return any || AvatarIK.Bounds(_tool, out bounds);
        }

        // The turn that lays the longest side of the tool's largest mesh along forward, the far end
        // ahead: the first-person gun is held slanting across the view.
        private static Quaternion LongAxis(GameObject tool)
        {
            MeshFilter largest = null;
            float volume = 0f;
            foreach (MeshFilter filter in tool.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                Vector3 size = Vector3.Scale(filter.sharedMesh.bounds.size, filter.transform.lossyScale);
                float v = Mathf.Abs(size.x * size.y * size.z);
                if (v > volume) { volume = v; largest = filter; }
            }
            if (largest == null) return Quaternion.identity;
            Vector3 s = largest.sharedMesh.bounds.size;
            Vector3 axis = s.x >= s.y && s.x >= s.z ? Vector3.right : s.y >= s.z ? Vector3.up : Vector3.forward;
            Vector3 along = Quaternion.Inverse(tool.transform.rotation) * (largest.transform.rotation * axis);
            if (Vector3.Dot(along, Vector3.forward) < 0f) along = -along;
            return Quaternion.FromToRotation(along, Vector3.forward);
        }

        public void Drop()
        {
            if (_tool != null) UnityEngine.Object.Destroy(_tool);
            _tool = null; _kind = 0; _hasUse = false; _swingAt = -10f; _fireAt = -10f; _measured = false;
            _body.Clear();
            _needle = _hourHand = _minuteHand = null;
            _sounds = new AudioSource[0];
            _effects[0] = _effects[1] = null;
            _shot.Clear();
        }

        // The way from a tool down to one of its parts, by child index: the copy has the same tree.
        private static int[] PathOf(Transform root, Transform part)
        {
            var path = new List<int>();
            for (; part != null && part != root; part = part.parent) path.Insert(0, part.GetSiblingIndex());
            return part == root ? path.ToArray() : null;
        }

        private static Transform Follow(Transform root, int[] path)
        {
            if (path == null) return null;
            foreach (int i in path)
            {
                if (i >= root.childCount) return null;
                root = root.GetChild(i);
            }
            return root;
        }

        private static Transform PartOf(GameObject item, object part, GameObject copy)
        {
            Transform t = part is GameObject go && go != null ? go.transform : part is Component c && c != null ? c.transform : null;
            return t != null ? Follow(copy.transform, PathOf(item.transform, t)) : null;
        }

        // The parts of the copy that show what the tool reads, and the gun's shot.
        private void FindParts(GameObject item, GameObject copy)
        {
            QuadrantController quadrant = item.GetComponentInChildren<QuadrantController>(true);
            if (quadrant != null) _needle = PartOf(item, NeedleField?.GetValue(quadrant), copy);
            ChronoController chrono = item.GetComponentInChildren<ChronoController>(true);
            if (chrono != null)
            {
                _hourHand = PartOf(item, HourHandField?.GetValue(chrono), copy);
                _minuteHand = PartOf(item, MinuteHandField?.GetValue(chrono), copy);
            }
            GunEvents gun = item.GetComponentInChildren<GunEvents>(true);
            if (gun == null) return;
            if (GunSoundsField?.GetValue(gun) is List<AudioSource> sounds)
            {
                _sounds = new AudioSource[sounds.Count];
                for (int i = 0; i < sounds.Count; i++)
                    _sounds[i] = PartOf(item, sounds[i], copy)?.GetComponent<AudioSource>();
            }
            _effects[0] = PartOf(item, StartEffectField?.GetValue(gun), copy)?.GetComponent<ParticleSystem>();
            _effects[1] = PartOf(item, FireEffectField?.GetValue(gun), copy)?.GetComponent<ParticleSystem>();
            Animator animator = GunAnimatorField?.GetValue(gun) as Animator;
            RuntimeAnimatorController controller = animator != null ? animator.runtimeAnimatorController : null;
            if (controller == null) return;
            foreach (AnimationClip clip in controller.animationClips)
            {
                if (clip == null || !clip.name.Contains("fire")) continue;
                _shot.AddRange(clip.events);
                break;
            }
            _shot.Sort((a, b) => a.time.CompareTo(b.time));
            foreach (AnimationEvent e in _shot)
                if (e.functionName == "PlayFireSound" && e.intParameter == 0) _bangAt = e.time;
        }

        private GameObject Build(byte kind, GameObject avatar)
        {
            List<Renderer> body = _body;
            List<InventoryButton> buttons = Buttons();
            if (buttons == null || kind > buttons.Count || buttons[kind - 1] == null) return null;
            var item = ItemField.GetValue(buttons[kind - 1]) as GameObject;
            if (item == null) return null;

            Renderer skin = avatar.GetComponentInChildren<Renderer>(true);
            int layer = skin != null ? skin.gameObject.layer : 0;
            // The game draws a tool with its own overlay camera on this layer; a mesh that is on
            // another one is not part of the tool's body (the spyglass's picture of the far view).
            int toolLayer = LayerMask.NameToLayer("UI_Physical");

            // Under an inactive parent the copy never wakes: no Awake or OnEnable of the tool's
            // scripts runs before they are removed.
            var holder = new GameObject("CoopToolHolder");
            holder.SetActive(false);
            GameObject copy;
            // The compass's light is on the tool's layer and lights only the tool; the lantern's lights the world.
            bool lit = false;
            foreach (Light light in item.GetComponentsInChildren<Light>(true))
                if (light.gameObject.layer != toolLayer) lit = true;
            // The gun keeps its sounds and its fire and smoke, for the shot; and its match burns.
            bool gun = item.GetComponentInChildren<GunEvents>(true) != null;
            try
            {
                copy = UnityEngine.Object.Instantiate(item, holder.transform);
                foreach (MeshRenderer renderer in copy.GetComponentsInChildren<MeshRenderer>(true))
                {
                    int own = renderer.gameObject.layer;
                    if (own != 0 && own != toolLayer) UnityEngine.Object.DestroyImmediate(renderer);
                    // HandSelector gives the tool's meshes probes of its own; the copy has no HandSelector.
                    else
                    {
                        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;
                        if (own == toolLayer) body.Add(renderer);
                    }
                }
                // A component cannot be removed while another one requires it (Unity logs an error
                // and leaves it). The first pass takes what requires — scripts, joints, filters —
                // and leaves what is required; the next ones take the rest.
                for (int pass = 0; pass < 5; pass++)
                {
                    bool removed = false;
                    foreach (Component component in copy.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null || Keep(component) || (lit && KeepLit(component)) || (gun && KeepShot(component)) ||
                            (pass == 0 && Required(component))) continue;
                        // Goes with its particle system.
                        if (component.GetType().Name == "ParticleSystemRenderer" && component.GetComponent("ParticleSystem") != null) continue;
                        UnityEngine.Object.DestroyImmediate(component);
                        removed = true;
                    }
                    if (!removed && pass > 0) break;
                }
                // A first-person light may light only the tool's own layer; the copy is in the world.
                foreach (Light light in copy.GetComponentsInChildren<Light>(true))
                {
                    light.shadows = LightShadows.None;
                    light.cullingMask = ~0;
                }
                foreach (AudioSource source in copy.GetComponentsInChildren<AudioSource>(true))
                    source.playOnAwake = false;
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.tag = "Untagged";
                    t.gameObject.layer = layer;
                }
                FindParts(item, copy);
                copy.name = "CoopTool " + item.name;
                copy.transform.SetParent(null, false);
                // The size it has in front of its owner's eye, whatever the avatar's own scale is.
                copy.transform.localScale = item.transform.lossyScale;
                copy.transform.SetParent(avatar.transform, true);
                copy.SetActive(true);
            }
            finally { UnityEngine.Object.Destroy(holder); }
            return copy;
        }

        private static readonly HashSet<string> RequiredNames = new HashSet<string>
        {
            "Canvas", "CanvasRenderer", "AudioSource", "ParticleSystem", "ParticleSystemRenderer",
        };

        private static bool Required(Component component)
        {
            return component is Rigidbody || component is Collider || component is Camera || component is Light ||
                   component is Animator || RequiredNames.Contains(component.GetType().Name);
        }

        // Skinned meshes are the first-person arms; an avatar has its own.
        private static bool Keep(Component component)
        {
            return component is Transform || component is MeshRenderer || component is MeshFilter || component is LODGroup;
        }

        private static bool KeepShot(Component component) =>
            component is AudioSource || component is ParticleSystem || component is ParticleSystemRenderer;

        private static bool KeepLit(Component component)
        {
            string name = component.GetType().Name;
            return component is Light || name == "UniversalAdditionalLightData" || name == "ParticleSystem" || name == "ParticleSystemRenderer";
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(CaulkHammerController), "Hammer")
                              ?? throw new MissingMethodException(nameof(CaulkHammerController), "Hammer"),
                          prefix: new HarmonyMethod(typeof(ToolVisuals), nameof(HammerPrefix)));
        }

        // The game ignores a blow while the last one is still playing; only a blow that lands counts.
        private static void HammerPrefix(CaulkHammerController __instance)
        {
            PatchGuard.Run(() =>
            {
                if (HammerReadyField == null || (bool)HammerReadyField.GetValue(__instance)) s_useCount++;
            }, error => Plugin.Log.LogError("[Tools] " + error));
        }
    }
}
