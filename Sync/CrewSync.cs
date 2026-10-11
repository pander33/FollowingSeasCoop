using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The ship's crew. Every copy has the same crew members (they come with the world), but only
    /// the host's work: its helmsman steers the hull that sails, its hands reef and pump. What they
    /// do to the ship reaches the guests as the state of the controls.
    ///
    /// <b>Orders.</b> A guest gives orders in the game's own crew panel. The <c>Crew</c> patches
    /// take what the panel was about to hand to the guest's crew and send it to the host, which
    /// hands it to its own crew the way the panel does.
    ///
    /// <b>What is seen.</b> The host sends where each crew member stands and its animator's
    /// parameters; a guest's crew, which never gets a task, is moved towards that and its animator
    /// follows the same parameters.
    ///
    /// <b>Who is in the crew.</b> The game changes a crew on the copy of the player who does it: a
    /// hand taken aboard on an island, the crew set at a dock. The roster — the ids aboard, in the
    /// ship's own order, and the game's list of every hand the players have — is read twice a
    /// second on every copy. A guest whose own changed tells the host; the host makes its crew the
    /// same and tells everybody. The crew members themselves are made by the game's own loader.
    /// </summary>
    internal sealed class CrewSync
    {
        private const float SendInterval = 0.1f;

        private static readonly FieldInfo AnimatorField = AccessTools.Field(typeof(NewCrewController), "animator");
        private static readonly FieldInfo CourseSetField = AccessTools.Field(typeof(CrewDirector), "courseSet");
        private static readonly FieldInfo SailsSetField = AccessTools.Field(typeof(CrewDirector), "mcSet");
        private static readonly FieldInfo PumpSetField = AccessTools.Field(typeof(CrewDirector), "pumpSet");
        private static readonly FieldInfo HeadingTextField = AccessTools.Field(typeof(CrewDirector), "setHeadingText");
        private static readonly FieldInfo MastsField = AccessTools.Field(typeof(CrewDirector), "mastControllers");
        private static readonly FieldInfo SlidersField = AccessTools.Field(typeof(CrewDirector), "valueSliders");
        private static readonly FieldInfo PumpTargetField = AccessTools.Field(typeof(CrewDirector), "targetPumpingCrewCount");
        private static readonly FieldInfo PumpTextField = AccessTools.Field(typeof(CrewDirector), "pumpingCrewCount");
        private static readonly FieldInfo StopButtonField = AccessTools.Field(typeof(CrewDirector), "stopButton");

        // Read by the patches, which are static.
        private static CrewSync s_instance;
        private static bool s_guest;

        private sealed class Member
        {
            public NewCrewController Crew;
            public Animator Animator;
            public int[] Bools = new int[0];
            public int[] Floats = new int[0];
            public CrewPose Target;
            public bool HasTarget;
        }

        private readonly CoopNet _net;
        private readonly List<Member> _members = new List<Member>();
        private ShipHullMover _ship;
        private int _crewCount = -1;
        private float _nextSendAt;
        private long _lastTick;

        public int OrdersSent { get; private set; }
        public int OrdersTaken { get; private set; }

        public CrewSync(CoopNet net)
        {
            _net = net;
            s_instance = this;
        }

        // -----------------------------------------------------------------
        // Binding
        // -----------------------------------------------------------------

        private bool Bind()
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            int count = ship != null && ship.crew != null ? ship.crew.Count : -1;
            bool same = ship == _ship && count == _crewCount;
            // The game makes a crew anew in places (a change at a dock): as many, but other objects.
            for (int i = 0; same && i < _members.Count; i++) same = _members[i].Crew == ship.crew[i];
            if (same) return _members.Count > 0;
            _ship = ship;
            _crewCount = count;
            _members.Clear();
            if (count <= 0 || AnimatorField == null) return false;
            // The ship's own list, filled from the save in its order: the same on every copy.
            for (int i = 0; i < count && i < CrewStateMsg.MaxCrew; i++)
            {
                var member = new Member { Crew = ship.crew[i] };
                member.Animator = member.Crew != null ? AnimatorField.GetValue(member.Crew) as Animator : null;
                if (member.Animator != null)
                {
                    var bools = new List<int>();
                    var floats = new List<int>();
                    foreach (AnimatorControllerParameter parameter in member.Animator.parameters)
                    {
                        if (parameter.type == AnimatorControllerParameterType.Bool && bools.Count < 32) bools.Add(parameter.nameHash);
                        if (parameter.type == AnimatorControllerParameterType.Float && floats.Count < 4) floats.Add(parameter.nameHash);
                    }
                    member.Bools = bools.ToArray();
                    member.Floats = floats.ToArray();
                }
                _members.Add(member);
            }
            Plugin.Log.LogInfo("[Crew] Bound to " + _members.Count + " crew member(s)");
            return true;
        }

        // -----------------------------------------------------------------
        // Per-frame
        // -----------------------------------------------------------------

        /// <summary>From Update. The host says where its crew is.</summary>
        public void Tick()
        {
            s_guest = _net.IsClient;
            float now = Time.unscaledTime;
            if (now >= _nextRosterAt)
            {
                _nextRosterAt = now + RosterInterval;
                TickRoster();
            }
            if (!_net.IsHost || !Bind()) return;
            if (now < _nextSendAt) return;
            _nextSendAt = now + SendInterval;

            var msg = new CrewStateMsg { Tick = _net.Clock.ServerTick, Crew = new CrewPose[_members.Count] };
            for (int i = 0; i < _members.Count; i++)
            {
                Member member = _members[i];
                // A pose of nothing would send a guest's crew member to the ship's origin.
                if (member.Crew == null) return;
                Transform t = member.Crew.transform;
                Vector3 p = t.localPosition, e = t.localEulerAngles;
                CrewPose pose = default;
                pose.X = p.x; pose.Y = p.y; pose.Z = p.z;
                pose.Rx = e.x; pose.Ry = e.y; pose.Rz = e.z;
                Animator animator = member.Animator;
                if (animator != null && animator.isActiveAndEnabled)
                {
                    for (int b = 0; b < member.Bools.Length; b++)
                        if (animator.GetBool(member.Bools[b])) pose.Bools |= 1u << b;
                    if (member.Floats.Length > 0) pose.F0 = animator.GetFloat(member.Floats[0]);
                    if (member.Floats.Length > 1) pose.F1 = animator.GetFloat(member.Floats[1]);
                    if (member.Floats.Length > 2) pose.F2 = animator.GetFloat(member.Floats[2]);
                    if (member.Floats.Length > 3) pose.F3 = animator.GetFloat(member.Floats[3]);
                }
                msg.Crew[i] = pose;
            }
            _net.Broadcast(msg, DeliveryMethod.Unreliable);
        }

        public void OnState(CrewStateMsg msg)
        {
            if (!_net.IsClient || msg.Tick < _lastTick || !Bind()) return;
            _lastTick = msg.Tick;
            for (int i = 0; i < _members.Count && i < msg.Crew.Length; i++)
            {
                _members[i].Target = msg.Crew[i];
                _members[i].HasTarget = true;
            }
        }

        /// <summary>From LateUpdate, after the game's own crew code. A guest's crew goes where the host's is.</summary>
        public void LateTick()
        {
            if (!_net.IsClient || _members.Count == 0) return;
            float dt = Time.unscaledDeltaTime;
            // Ten poses a second, a crew member walks slowly: easing towards the last one is smooth.
            float ease = 1f - Mathf.Exp(-dt * 10f);
            foreach (Member member in _members)
            {
                if (!member.HasTarget || member.Crew == null) continue;
                CrewPose pose = member.Target;
                Transform t = member.Crew.transform;
                var position = new Vector3(pose.X, pose.Y, pose.Z);
                Quaternion rotation = Quaternion.Euler(pose.Rx, pose.Ry, pose.Rz);
                if ((t.localPosition - position).sqrMagnitude > 9f)
                {
                    t.localPosition = position;
                    t.localRotation = rotation;
                }
                else
                {
                    t.localPosition = Vector3.Lerp(t.localPosition, position, ease);
                    t.localRotation = Quaternion.Slerp(t.localRotation, rotation, ease);
                }
                Animator animator = member.Animator;
                if (animator == null || !animator.isActiveAndEnabled) continue;
                for (int b = 0; b < member.Bools.Length; b++) animator.SetBool(member.Bools[b], (pose.Bools & (1u << b)) != 0);
                if (member.Floats.Length > 0) animator.SetFloat(member.Floats[0], pose.F0);
                if (member.Floats.Length > 1) animator.SetFloat(member.Floats[1], pose.F1);
                if (member.Floats.Length > 2) animator.SetFloat(member.Floats[2], pose.F2);
                if (member.Floats.Length > 3) animator.SetFloat(member.Floats[3], pose.F3);
            }
        }

        public void Clear()
        {
            _members.Clear();
            _ship = null;
            _crewCount = -1;
            _lastTick = 0;
            s_guest = false;
            OrdersSent = OrdersTaken = 0;
            _rosterShip = null;
            _knownRoster = null;
            _rosterSynced = false;
            RostersApplied = 0;
        }

        public string Describe()
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            int aboard = ship != null && ship.crew != null ? ship.crew.Count : 0;
            return "crew " + aboard + (_net.IsClient && !_rosterSynced ? "  (the host's roster has not come)" : "") +
                   (RostersApplied > 0 ? "  rosters taken " + RostersApplied : "") +
                   (OrdersSent > 0 ? "  orders sent " + OrdersSent : "") + (OrdersTaken > 0 ? "  orders taken " + OrdersTaken : "");
        }

        public void Dump(StateDump d)
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            int count = ship != null && ship.crew != null ? ship.crew.Count : 0;
            d.Line("Crew", "count", count.ToString());
            if (ship != null && Read(ship, out string[] aboard, out string[] owned))
            {
                d.Line("Crew", "aboard", string.Join(",", aboard));
                d.Line("Crew", "owned", string.Join(",", owned));
            }
            for (int i = 0; i < count; i++)
            {
                NewCrewController crew = ship.crew[i];
                if (crew == null) continue;
                Vector3 p = crew.transform.localPosition;
                d.Line("Crew", "member " + i, StateDump.F(p.x) + " " + StateDump.F(p.y) + " " + StateDump.F(p.z), 1f);
            }
        }

        // -----------------------------------------------------------------
        // Who is in the crew
        // -----------------------------------------------------------------

        private const float RosterInterval = 0.5f;
        private float _nextRosterAt;
        private ShipHullMover _rosterShip;
        // The roster both sides last had, as one text; null until something is known.
        private string _knownRoster;
        // Guest: the host's roster has come for the ship this copy has now.
        private bool _rosterSynced;

        public int RostersApplied { get; private set; }
        private bool _warnedNoDeck;

        // What the game's save would write: every hand aboard belongs to this ship in the list of
        // owned hands, whether the list has heard of it yet or not (one taken aboard has not).
        private static bool Read(ShipHullMover ship, out string[] aboard, out string[] owned)
        {
            aboard = owned = null;
            DataPersistenceManager dp = GameAccess.Dp;
            if (ship == null || ship.crew == null || dp == null || dp.gameData == null) return false;
            var ids = new List<string>();
            foreach (NewCrewController crew in ship.crew)
                if (crew != null && !string.IsNullOrEmpty(crew.id)) ids.Add(crew.id);
            var list = new List<string>(dp.gameData.crewOwned ?? new List<string>());
            string mine = "=" + (ship.id ?? "");
            foreach (string id in ids)
            {
                int at = list.FindIndex(line => line != null && line.StartsWith(id + "=", StringComparison.Ordinal));
                if (at < 0) list.Add(id + mine);
                else list[at] = id + mine;
            }
            aboard = ids.ToArray();
            owned = list.ToArray();
            return true;
        }

        private static string Text(string shipId, string[] aboard, string[] owned)
            => (shipId ?? "") + "|" + string.Join(",", aboard) + "|" + string.Join(",", owned);

        private void TickRoster()
        {
            if (_net.Role == Role.None) return;
            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship != _rosterShip)
            {
                // Another ship, or this one loaded again: a guest's copy is the save's, not news.
                _rosterShip = ship;
                _knownRoster = null;
                _rosterSynced = false;
            }
            if (!Read(ship, out string[] aboard, out string[] owned)) return;
            if (_net.IsClient && !_rosterSynced) return;
            string text = Text(ship.id, aboard, owned);
            if (text == _knownRoster) return;
            _knownRoster = text;
            var msg = new CrewRosterMsg { ShipId = ship.id ?? "", Aboard = aboard, Owned = owned };
            if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            else _net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host: a player has just entered the world, or loaded it again.</summary>
        public void SendRoster(uint netId)
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            if (!_net.IsHost || !Read(ship, out string[] aboard, out string[] owned)) return;
            _net.SendTo(netId, new CrewRosterMsg { ShipId = ship.id ?? "", Aboard = aboard, Owned = owned }, DeliveryMethod.ReliableOrdered);
        }

        public void OnRoster(CrewRosterMsg msg, NetPeer from)
        {
            if (_net.IsHost && _net.PlayerNetIdForPeer(from) == 0) return;
            ShipHullMover ship = GameAccess.ActiveShip();
            DataPersistenceManager dp = GameAccess.Dp;
            if (ship == null || ship.crew == null || dp == null || dp.gameData == null || (ship.id ?? "") != msg.ShipId) return;
            if (ship != _rosterShip) { _rosterShip = ship; _knownRoster = null; }

            // The game splits every line at "=" and reads the second part.
            var owned = new List<string>();
            foreach (string line in msg.Owned)
                if (!string.IsNullOrEmpty(line) && line.IndexOf('=') > 0) owned.Add(line);
            dp.gameData.crewOwned = owned;
            SetAboard(ship, dp, msg.Aboard);
            RemoveFigures(msg);
            RostersApplied++;

            if (_net.IsClient)
            {
                // What this copy reads now is the agreed roster; only a later change is news.
                _rosterSynced = true;
                _knownRoster = Read(ship, out string[] aboard, out string[] list) ? Text(ship.id, aboard, list) : null;
            }
            // The host says what its crew is now to everybody — the sender too, whose change may
            // not have been possible here as it was asked.
            else _knownRoster = null;
        }

        // Hands are added and taken at the end of the ship's list where that is all the change is,
        // so the others keep what they are doing; any other change makes the crew anew, as the
        // game does at a dock.
        private void SetAboard(ShipHullMover ship, DataPersistenceManager dp, string[] want)
        {
            var ids = new List<string>();
            int room = Mathf.Min(ship.maxCrew, ship.crewInitPositions != null ? ship.crewInitPositions.Count : 0);
            foreach (string id in want)
                if (ids.Count < room && !string.IsNullOrEmpty(id) && !ids.Contains(id) && dp.InstantiateCrewByID(id) != null) ids.Add(id);

            int common = 0;
            while (common < ids.Count && common < ship.crew.Count && ship.crew[common] != null && ship.crew[common].id == ids[common]) common++;
            if (common == ids.Count && common == ship.crew.Count) return;
            if (common < ship.crew.Count && common < ids.Count) common = 0;   // not a change at the end

            for (int i = ship.crew.Count - 1; i >= common; i--)
            {
                NewCrewController gone = ship.crew[i];
                ship.crew.RemoveAt(i);
                if (gone == null) continue;
                // Whatever it was doing is not being done any more.
                foreach (CrewInteractionObject station in ship.GetComponentsInChildren<CrewInteractionObject>(true))
                {
                    if (station.assignedCrew != gone) continue;
                    station.assigned = false;
                    station.assignedCrew = null;
                    station.interacting = false;
                    if (station.cioType == CrewInteractionObject.ObjectType.Pump) ControlsSync.StopPump(ship, station);
                }
                gone.DestroySelf();
            }
            if (common < ids.Count && ship.epitaphNavMapper == null)
            {
                if (!_warnedNoDeck) Plugin.Log.LogWarning("[Crew] The ship '" + ship.id + "' has no place for crew; the roster is not applied");
                _warnedNoDeck = true;
            }
            else if (common < ids.Count)
            {
                // The game's own loader: it adds to the ship every hand the list gives to this ship.
                var list = new GameData { crewOwned = new List<string>() };
                for (int i = common; i < ids.Count; i++) list.crewOwned.Add(ids[i] + "=" + ship.id);
                ship.LoadCrew(list);
            }
            CoopTrace.Note("Crew", "roster applied", ship.id ?? "", string.Join(",", ids));
        }

        // A hand taken aboard on another copy still stands on this copy's island.
        private static void RemoveFigures(CrewRosterMsg msg)
        {
            foreach (WorldEpitaph figure in UnityEngine.Object.FindObjectsByType<WorldEpitaph>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (figure == null || string.IsNullOrEmpty(figure.id)) continue;
                bool taken = Array.IndexOf(msg.Aboard, figure.id) >= 0;
                for (int i = 0; !taken && i < msg.Owned.Length; i++)
                    taken = msg.Owned[i] != null && msg.Owned[i].StartsWith(figure.id + "=", StringComparison.Ordinal);
                if (taken) UnityEngine.Object.Destroy(figure.gameObject);
            }
        }

        // -----------------------------------------------------------------
        // Orders
        // -----------------------------------------------------------------

        /// <summary>Host: a guest's orders, handed to the host's crew as the game's panel hands its own.</summary>
        public void OnOrder(CrewOrderMsg msg, NetPeer from)
        {
            if (!_net.IsHost) return;
            uint sender = _net.PlayerNetIdForPeer(from);
            ShipHullMover ship = GameAccess.ActiveShip();
            if (sender == 0 || ship == null || ship.crew == null || ship.crew.Count == 0) return;
            OrdersTaken++;
            CoopTrace.Note("Crew", "order", sender.ToString(), msg.Flags.ToString());

            CrewInteractionObject helm = FindHelm(ship);
            if ((msg.Flags & CrewOrderFlags.LeaveHelm) != 0)
            {
                if (helm != null) helm.done = true;
                ship.SetCrewSteering(false, -1f);
            }
            if ((msg.Flags & CrewOrderFlags.Course) != 0 && !float.IsNaN(msg.Heading) && !float.IsInfinity(msg.Heading))
            {
                // A helmsman already at the wheel is only given the new course.
                if (ship.crewPiloting) ship.setCourseDeg = msg.Heading;
                else if (helm != null) Give(ship, ship.ToggleCrewSteering(true, msg.Heading), helm);
            }
            if ((msg.Flags & CrewOrderFlags.Sails) != 0)
            {
                List<MastController> masts = ship.GetMastControllers();
                for (int i = 0; i < masts.Count && i < msg.Reefs.Length; i++)
                {
                    float want = msg.Reefs[i];
                    MastController mast = masts[i];
                    if (mast == null || float.IsNaN(want) || want < 0f) continue;
                    CrewInteractionObject station = mast.GetCrewInteractionObject();
                    if (station == null || Mathf.Abs(mast.GetCurrentPosPercent() - want) < 0.001f) continue;
                    Give(ship, mast.ReefToPoint(Mathf.Clamp01(want)), station);
                }
            }
            if ((msg.Flags & CrewOrderFlags.Pumps) != 0) SetPumping(ship, msg.Pumps);
        }

        // The panel counts the crew at the pumps and is told a new number; so is the host here.
        private static void SetPumping(ShipHullMover ship, int want)
        {
            var pumps = new List<PumpController>();
            int working = 0;
            foreach (PumpController pump in ship.GetComponentsInChildren<PumpController>(false))
            {
                if (pump.crewTarget == null) continue;
                pumps.Add(pump);
                if (pump.crewTarget.interacting || pump.crewTarget.assigned) working++;
            }
            want = Mathf.Clamp(want, 0, Mathf.Min(pumps.Count, ship.crew.Count));
            foreach (PumpController pump in pumps)
            {
                if (working == want) break;
                bool busy = pump.crewTarget.interacting || pump.crewTarget.assigned;
                if (working > want && busy) { pump.StopPumping(); working--; }
                else if (working < want && !busy) { Give(ship, pump.PumpContinuous(), pump.crewTarget); working++; }
            }
        }


        private static CrewInteractionObject FindHelm(ShipHullMover ship)
        {
            foreach (CrewInteractionObject station in ship.GetComponentsInChildren<CrewInteractionObject>(true))
                if (station.cioType == CrewInteractionObject.ObjectType.Helm) return station;
            // The game's panel takes the first one in the scene.
            foreach (CrewInteractionObject station in UnityEngine.Object.FindObjectsByType<CrewInteractionObject>(FindObjectsSortMode.None))
                if (station.cioType == CrewInteractionObject.ObjectType.Helm) return station;
            return null;
        }

        // As the game's panel does: the crew member already at that station, otherwise the one with
        // the least to do.
        private static void Give(ShipHullMover ship, IEnumerator task, CrewInteractionObject station)
        {
            if (station.assigned && station.assignedCrew != null) { station.assignedCrew.Assign(task, station); return; }
            NewCrewController best = null;
            int least = int.MaxValue;
            foreach (NewCrewController crew in ship.crew)
            {
                if (crew == null) continue;
                if (!crew.IsBusy()) { best = crew; break; }
                if (crew.tasks.Count < least) { least = crew.tasks.Count; best = crew; }
            }
            if (best != null) best.Assign(task, station);
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            foreach (FieldInfo field in new[] { CourseSetField, SailsSetField, PumpSetField, HeadingTextField, MastsField, SlidersField, PumpTargetField })
                if (field == null) throw new MissingFieldException(nameof(CrewDirector), "a field of the crew panel");
            harmony.Patch(Method("SetOrders"), prefix: new HarmonyMethod(typeof(CrewSync), nameof(OrdersPrefix)));
            // A button's listener: it is called through a delegate, so it is not built into a caller.
            harmony.Patch(Method("CancelHelm"), prefix: new HarmonyMethod(typeof(CrewSync), nameof(LeaveHelmPrefix)));
            harmony.Patch(Method("OnEnable"), postfix: new HarmonyMethod(typeof(CrewSync), nameof(PanelOpenedPostfix)));
        }

        private static MethodInfo Method(string name)
            => AccessTools.Method(typeof(CrewDirector), name) ?? throw new MissingMethodException(nameof(CrewDirector), name);

        private static void PatchError(Exception error) => Plugin.Log.LogError("[Crew] " + error);

        // A guest's crew gets no tasks: the order goes to the host, and the panel closes as it would.
        private static bool OrdersPrefix(CrewDirector __instance)
        {
            return PatchGuard.Prefix(() =>
            {
                if (!s_guest || s_instance == null) return true;
                var order = new CrewOrderMsg();
                if ((bool)CourseSetField.GetValue(__instance) && HeadingTextField.GetValue(__instance) is TMP_Text heading &&
                    float.TryParse(heading.text, NumberStyles.Float, CultureInfo.CurrentCulture, out float course))
                {
                    order.Flags |= CrewOrderFlags.Course;
                    order.Heading = course;
                }
                if ((bool)SailsSetField.GetValue(__instance) && MastsField.GetValue(__instance) is List<MastController> masts &&
                    SlidersField.GetValue(__instance) is List<CrewControlMastSlider> sliders)
                {
                    order.Reefs = new float[Mathf.Min(masts.Count, CrewOrderMsg.MaxMasts)];
                    for (int i = 0; i < order.Reefs.Length; i++)
                    {
                        order.Reefs[i] = -1f;
                        if (i >= sliders.Count || masts[i] == null || masts[i].GetCrewInteractionObject() == null) continue;
                        float want = sliders[i].GetValue();
                        if (masts[i].GetCurrentPosPercent() == want) continue;
                        order.Reefs[i] = want;
                        order.Flags |= CrewOrderFlags.Sails;
                    }
                }
                if ((bool)PumpSetField.GetValue(__instance))
                {
                    order.Flags |= CrewOrderFlags.Pumps;
                    order.Pumps = (byte)Mathf.Clamp((int)PumpTargetField.GetValue(__instance), 0, 255);
                }
                if (order.Flags != CrewOrderFlags.None) s_instance.SendOrder(order);

                InteractionController controller = InteractionController.interactionInstance;
                if (controller != null) controller.dampenCameraMovement = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                __instance.gameObject.SetActive(false);
                return false;
            }, PatchError);
        }

        private static bool LeaveHelmPrefix()
        {
            return PatchGuard.Prefix(() =>
            {
                if (!s_guest || s_instance == null) return true;
                s_instance.SendOrder(new CrewOrderMsg { Flags = CrewOrderFlags.LeaveHelm });
                return false;
            }, PatchError);
        }

        // The panel reads the helm and the pumps of the guest's own ship, where no crew works: it
        // is shown what the host's crew is doing.
        private static void PanelOpenedPostfix(CrewDirector __instance)
        {
            PatchGuard.Run(() =>
            {
                if (!s_guest) return;
                if (StopButtonField?.GetValue(__instance) is Button stop && stop != null) stop.interactable = ControlsSync.HostCrewSteers;
                PumpTargetField.SetValue(__instance, ControlsSync.HostCrewPumping);
                if (PumpTextField?.GetValue(__instance) is TMP_Text count && count != null) count.text = ControlsSync.HostCrewPumping.ToString();
            }, PatchError);
        }

        private void SendOrder(CrewOrderMsg order)
        {
            _net.SendToHost(order, DeliveryMethod.ReliableOrdered);
            OrdersSent++;
            CoopTrace.Note("Crew", "order sent", order.Flags.ToString());
        }
    }
}
