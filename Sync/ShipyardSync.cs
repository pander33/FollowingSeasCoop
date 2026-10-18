using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The shipyard (<c>ShipMarketUI</c>): the ship plans and what has been paid and delivered for
    /// them, and a new ship launched.
    ///
    /// The plans are <c>GameData.shipPlansKnown</c>, one line per kind of ship, which the shipyard's
    /// dialogs write directly. Twice a second each copy compares the lines with what it last knew and
    /// sends the changed ones. A guest sends each with the line it was before: the host adds what
    /// the guest paid and delivered to its own figures, so two players paying at once both count,
    /// and tells everybody the result. The money goes through <see cref="EconomySync"/>; a payment
    /// dialog counts from the purse and the plan as they are when it pays, not when it opened.
    ///
    /// A launch runs the game's own code on the copy of the player who pressed it: the old hull is
    /// stored (switched off), the new one is loaded from its scene and put at the launch point. That
    /// copy tells the others the kind of ship, the point and the sails; each does the same with the
    /// game's scene, outside the shipyard's dialog (whose island it may not have loaded). A guest's
    /// launch goes to the host first, and the host tells everybody once its own hull is the new one;
    /// a guest that has the new ship asks the host for the household, the controls and the crew anew.
    /// A player standing on the stored hull is put on the deck of the new one.
    /// </summary>
    internal sealed class ShipyardSync
    {
        private const float PlansPeriod = 0.5f;
        /// <summary>The shipyard's own wait before the new hull is let go into the water.</summary>
        private const float ReleaseSeconds = 2f;
        private const float MismatchSeconds = 10f;
        /// <summary>A guest's launch the host has not confirmed in this time is not coming.</summary>
        private const float ConfirmSeconds = 30f;
        // The scene the shipyard merges a new ship into.
        private const string GameplayScene = "gameplay";

        private static readonly FieldInfo IdField = AccessTools.Field(typeof(ShipMarketUI), "id");
        private static readonly FieldInfo SpawnField = AccessTools.Field(typeof(ShipMarketUI), "shipToSpawn");
        private static readonly FieldInfo SailsField = AccessTools.Field(typeof(ShipMarketUI), "shipToSpawnSailStates");
        private static readonly FieldInfo SaveStringField = AccessTools.Field(typeof(ShipMarketUI), "saveString");
        private static readonly MethodInfo BuildFieldsMethod = AccessTools.Method(typeof(ShipMarketUI), "UpdateBuildFields");
        private static readonly MethodInfo BuyFieldsMethod = AccessTools.Method(typeof(ShipMarketUI), "UpdateBuyFields");
        private static readonly FieldInfo PlayerShipField = AccessTools.Field(typeof(PlayerHandler), "currentShipID");
        private static readonly FieldInfo PayCostField = AccessTools.Field(typeof(GivePaymentHandler), "cost");
        private static readonly FieldInfo PayPaidField = AccessTools.Field(typeof(GivePaymentHandler), "paid");
        private static readonly FieldInfo PayMoneyField = AccessTools.Field(typeof(GivePaymentHandler), "money");
        private static readonly FieldInfo PaySliderField = AccessTools.Field(typeof(GivePaymentHandler), "moneySlider");
        // Fields of a plan line after the kind: three flags, then paid, wood, linen, iron.
        private const int PlanFlags = 3;

        private readonly CoopNet _net;
        private readonly Dictionary<string, string> _plans = new Dictionary<string, string>(StringComparer.Ordinal);
        private DataPersistenceManager _dp;
        private float _nextPollAt;
        private int _generation;
        private bool _loading;
        // A host's launch heard while this guest was still loading the world.
        private ShipLaunchMsg _waiting;
        // Guest: the ship this copy launched, until the host says its hull is that one too.
        private string _launchedHere;
        private float _launchedAt;
        private float _mismatchSince = -1f;
        private bool _mismatchWarned;

        public int Launches { get; private set; }

        public ShipyardSync(CoopNet net) { _net = net; }

        public void Clear()
        {
            _generation++;
            _plans.Clear();
            _dp = null;
            _loading = false;
            _waiting = null;
            _launchedHere = null;
            _mismatchSince = -1f;
            _mismatchWarned = false;
            Launches = 0;
        }

        private static string Kind(string line)
        {
            int cut = line == null ? -1 : line.IndexOf('=');
            return cut > 0 ? line.Substring(0, cut) : null;
        }

        // -----------------------------------------------------------------
        // Plans
        // -----------------------------------------------------------------

        /// <summary>Every frame for a participant.</summary>
        public void Tick(BoatSync boats)
        {
            if (_waiting != null && !_loading)
            {
                ShipLaunchMsg msg = _waiting;
                _waiting = null;
                Begin(msg, false);
            }
            if (_net.IsClient) WatchShip(boats);

            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.gameData == null || dp.gameData.shipPlansKnown == null) return;
            if (dp != _dp)
            {
                // A new world: its lines are the starting point.
                _dp = dp;
                _plans.Clear();
                Look(dp, false);
                return;
            }
            if (Time.unscaledTime < _nextPollAt) return;
            _nextPollAt = Time.unscaledTime + PlansPeriod;
            Look(dp, true);
        }

        // The lines the local game changed since the last look, sent when asked.
        private void Look(DataPersistenceManager dp, bool send)
        {
            List<string> changed = null, bases = null;
            foreach (string line in dp.gameData.shipPlansKnown)
            {
                string kind = Kind(line);
                if (kind == null || (_plans.TryGetValue(kind, out string known) && known == line)) continue;
                (changed ?? (changed = new List<string>())).Add(line);
                (bases ?? (bases = new List<string>())).Add(known ?? "");
                _plans[kind] = line;
            }
            if (!send || changed == null) return;
            var msg = new ShipPlansMsg { Lines = changed.ToArray() };
            if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            else
            {
                msg.Bases = bases.ToArray();
                _net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
            }
            CoopTrace.Note("Shipyard", "plans", "sent", string.Join(" ", changed));
        }

        public void OnPlans(ShipPlansMsg msg, NetPeer from)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.gameData == null) return;
            if (dp.gameData.shipPlansKnown == null) dp.gameData.shipPlansKnown = new List<string>();
            List<string> plans = dp.gameData.shipPlansKnown;
            if (dp == _dp) Look(dp, true);

            var result = new List<string>();
            for (int i = 0; i < msg.Lines.Length; i++)
            {
                string line = msg.Lines[i];
                string kind = Kind(line);
                if (kind == null) continue;
                int index = plans.FindIndex(known => Kind(known) == kind);
                string before = i < msg.Bases.Length ? msg.Bases[i] : "";
                if (_net.IsHost && index >= 0 && before.Length > 0) line = Merge(plans[index], before, line);
                if (index >= 0) plans[index] = line; else plans.Add(line);
                if (dp == _dp) _plans[kind] = line;
                Refresh(kind, line);
                result.Add(line);
            }
            // To the sender too: its line is now the host's figures plus its change.
            if (_net.IsHost && result.Count > 0)
                _net.Broadcast(new ShipPlansMsg { Lines = result.ToArray() }, DeliveryMethod.ReliableOrdered);
        }

        // The guest's change from its old line, put on the host's line: a flag the guest switched is
        // the guest's, a figure grows by what the guest added.
        private static string Merge(string host, string before, string after)
        {
            string[] h = host.Split('='), b = before.Split('='), a = after.Split('=');
            if (h.Length != a.Length || b.Length != a.Length) return after;
            for (int i = 1; i < a.Length; i++)
            {
                if (i <= PlanFlags) { if (a[i] != b[i]) h[i] = a[i]; continue; }
                if (TryNumber(h[i], out float hv) && TryNumber(b[i], out float bv) && TryNumber(a[i], out float av))
                    h[i] = (hv + av - bv).ToString(CultureInfo.InvariantCulture);
                else h[i] = a[i];
            }
            return string.Join("=", h);
        }

        private static bool TryNumber(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // A shipyard dialog that is open keeps the line it read when it opened, and writes it back
        // with the next payment.
        private static void Refresh(string kind, string line)
        {
            if (IdField == null || SaveStringField == null) return;
            foreach (ShipMarketUI ui in UnityEngine.Object.FindObjectsByType<ShipMarketUI>(FindObjectsSortMode.None))
            {
                if ((IdField.GetValue(ui) as string) != kind || (SaveStringField.GetValue(ui) as string ?? "").Length == 0) continue;
                SaveStringField.SetValue(ui, line);
                (ui.building ? BuildFieldsMethod : BuyFieldsMethod)?.Invoke(ui, null);
            }
        }

        /// <summary>Host: a guest came into the world, or asked for everything anew.</summary>
        public void SendAll(uint player)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (!_net.IsHost || dp == null || dp.gameData == null || dp.gameData.shipPlansKnown == null) return;
            _net.SendTo(player, new ShipPlansMsg { Lines = dp.gameData.shipPlansKnown.ToArray() }, DeliveryMethod.ReliableOrdered);
        }

        // -----------------------------------------------------------------
        // Launch
        // -----------------------------------------------------------------

        // The local player's shipyard launched a ship: the game has done it here.
        private void Launched(ShipMarketUI ui)
        {
            string id = IdField?.GetValue(ui) as string;
            var ship = SpawnField?.GetValue(ui) as GameObject;
            if (string.IsNullOrEmpty(id) || ship == null) return;
            SetCurrent(ship.GetComponent<ShipHullMover>());
            ShipLaunchMsg msg = Describe(id, ship.transform, SailsField?.GetValue(ui) as List<string>);
            Launches++;
            if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            else
            {
                _launchedHere = id;
                _launchedAt = Time.unscaledTime;
                _net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
                // A ship of the same kind as before: the host's poses of its old one would take it away.
                CoopBehaviour.Instance.Boats.HoldHull();
            }
            Plugin.Log.LogInfo("[Shipyard] Launched " + id);
            CoopTrace.Note("Shipyard", "launch", id, "here");
        }

        private static ShipLaunchMsg Describe(string id, Transform ship, List<string> sails)
        {
            var msg = new ShipLaunchMsg { Ship = id, Sails = sails != null ? sails.ToArray() : Array.Empty<string>() };
            CoordSpace.LocalToReal(ship.position, out msg.X, out msg.Y, out msg.Z);
            Quaternion rot = ship.rotation;
            msg.Qx = rot.x; msg.Qy = rot.y; msg.Qz = rot.z; msg.Qw = rot.w;
            return msg;
        }

        public void OnLaunch(ShipLaunchMsg msg, NetPeer from)
        {
            if (string.IsNullOrEmpty(msg.Ship)) return;
            if (_net.IsClient && _launchedHere == msg.Ship)
            {
                // The host's hull is now the one this copy launched.
                _launchedHere = null;
                AskAgain();
                return;
            }
            if (_net.IsClient && !CoopBehaviour.Instance.Participating) { _waiting = msg; return; }
            if (_loading)
            {
                Plugin.Log.LogWarning("[Shipyard] Launch of " + msg.Ship + " by player " + _net.PlayerNetIdForPeer(from) +
                                      " not done: another ship is being launched");
                return;
            }
            Begin(msg, _net.IsHost);
        }

        private void Begin(ShipLaunchMsg msg, bool host)
        {
            _loading = true;
            CoopBehaviour.Instance.StartCoroutine(Launch(msg, host, _generation));
        }

        // The shipyard's LoadShipData and LaunchShip, without the dialog. The new hull is made ready
        // before the old one is stored, so a player on the old deck changes ships in one frame.
        private IEnumerator Launch(ShipLaunchMsg msg, bool host, int generation)
        {
            AsyncOperation load = null;
            try { load = SceneManager.LoadSceneAsync(msg.Ship, LoadSceneMode.Additive); }
            catch (Exception e) { Plugin.Log.LogWarning("[Shipyard] No scene for " + msg.Ship + ": " + e.Message); }
            if (load == null) { _loading = false; yield break; }
            while (!load.isDone) yield return null;

            Scene scene = SceneManager.GetSceneByName(msg.Ship);
            if (generation != _generation || !GameAccess.InWorld)
            {
                // The session or the world is gone: the ship is nobody's.
                if (scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
                _loading = false;
                yield break;
            }

            ShipHullMover ship = null;
            try { ship = Place(scene, msg); }
            catch (Exception e) { Plugin.Log.LogWarning("[Shipyard] Launch of " + msg.Ship + " failed: " + e); }
            if (ship == null) { _loading = false; yield break; }

            Plugin.Log.LogInfo("[Shipyard] The ship is now " + msg.Ship);
            CoopTrace.Note("Shipyard", "launch", msg.Ship, host ? "for a guest" : "the host's");
            if (host)
            {
                _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
                yield return new WaitForSeconds(ReleaseSeconds);
                Rigidbody body = ship != null ? ship.GetComponent<Rigidbody>() : null;
                if (generation == _generation && body != null) body.isKinematic = false;
            }
            else AskAgain();
            _loading = false;
        }

        private ShipHullMover Place(Scene scene, ShipLaunchMsg msg)
        {
            if (!scene.isLoaded) throw new InvalidOperationException("the scene did not load");
            // The shipyard's own choice of the hull among the scene's roots.
            GameObject[] roots = scene.GetRootGameObjects();
            GameObject ship = roots[0].GetComponent<Rigidbody>() != null ? roots[0]
                            : roots.Length > 1 && roots[1].GetComponent<Rigidbody>() != null ? roots[1] : roots[2];
            ShipHullMover mover = ship.GetComponent<ShipHullMover>();
            if (mover == null) throw new InvalidOperationException("the scene has no hull");
            ship.GetComponent<Rigidbody>().isKinematic = true;
            ship.SetActive(false);
            SceneManager.MergeScenes(scene, SceneManager.GetSceneByName(GameplayScene));
            GameAccess.Dp.CheckAudioSources();

            PlayerHandler player = GameAccess.Player;
            bool aboard = player != null && StepOff(player);
            // The shipyard's StoreCurrentShip.
            foreach (ShipHullMover old in UnityEngine.Object.FindObjectsByType<ShipHullMover>(FindObjectsSortMode.None))
                if (old.gameObject.activeSelf) old.gameObject.SetActive(false);

            ship.transform.SetPositionAndRotation(CoordSpace.RealToLocal(msg.X, msg.Y, msg.Z),
                                                  new Quaternion(msg.Qx, msg.Qy, msg.Qz, msg.Qw).normalized);
            ship.SetActive(true);
            if (msg.Sails.Length > 0) mover.SetupSails(new List<string>(msg.Sails));
            SetCurrent(mover);
            if (aboard)
            {
                PlayerRescue.Move(player, PlayerRescue.AboveWheel(mover));
                Plugin.Log.LogInfo("[Shipyard] The local player was put aboard the new ship");
            }
            return mover;
        }

        // A hull about to be stored takes its children with it. What the game does when the player
        // leaves a deck: off the hull, and its level copy of the player back under the player.
        private static bool StepOff(PlayerHandler player)
        {
            Transform root = player.transform.root;
            if (root == player.transform || root.GetComponent<ShipHullMover>() == null) return false;
            player.transform.SetParent(null);
            PlayerCloneController clone = player.playerClone;
            if (clone != null && clone.transform.parent != player.transform)
            {
                clone.gameObject.SetActive(false);
                clone.transform.parent = player.transform;
                clone.transform.position = player.transform.position;
            }
            return true;
        }

        // The save names the ship the player last stood on; a launched one is the crew's ship now,
        // stood on or not. The hull's id, which is not always spelled as the shipyard's.
        private static void SetCurrent(ShipHullMover ship)
        {
            string id = ship != null ? ship.id : null;
            if (string.IsNullOrEmpty(id)) return;
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp != null && dp.gameData != null) dp.gameData.currentShip = id;
            if (dp != null && dp.player != null) PlayerShipField?.SetValue(dp.player, id);
        }

        private static void AskAgain()
        {
            CoopBehaviour.Instance.Economy.AskAgain();
        }

        // Guest: the host sails another kind of ship than this copy has. Said once: nothing follows.
        private void WatchShip(BoatSync boats)
        {
            if (_launchedHere != null && Time.unscaledTime - _launchedAt > ConfirmSeconds)
            {
                Plugin.Log.LogWarning("[Shipyard] The host did not launch " + _launchedHere + " too");
                _launchedHere = null;
            }
            ShipHullMover ship = GameAccess.ActiveShip();
            bool differs = ship != null && boats.HostShip.Length > 0 && (ship.id ?? "") != boats.HostShip &&
                           !_loading && _launchedHere == null && _waiting == null;
            if (!differs) { _mismatchSince = -1f; _mismatchWarned = false; return; }
            if (_mismatchSince < 0f) _mismatchSince = Time.unscaledTime;
            if (_mismatchWarned || Time.unscaledTime - _mismatchSince < MismatchSeconds) return;
            _mismatchWarned = true;
            Plugin.Log.LogWarning("[Shipyard] The host's ship is " + boats.HostShip + ", this copy's is " + ship.id +
                                  "; leave and join again");
        }

        public void Dump(StateDump d)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.gameData == null || dp.gameData.shipPlansKnown == null) { d.Info("Shipyard", "plans", "none"); return; }
            foreach (string line in dp.gameData.shipPlansKnown)
            {
                string kind = Kind(line);
                if (kind != null) d.Line("Shipyard", "plan " + kind, line.Substring(kind.Length + 1));
            }
            d.Info("Shipyard", "launches", Launches.ToString());
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            if (IdField == null || SpawnField == null) throw new MissingFieldException(nameof(ShipMarketUI), "id / shipToSpawn");
            if (PayCostField == null || PayPaidField == null || PayMoneyField == null || PaySliderField == null)
                throw new MissingFieldException(nameof(GivePaymentHandler), "cost / paid / money / moneySlider");
            harmony.Patch(AccessTools.Method(typeof(GivePaymentHandler), "Pay")
                              ?? throw new MissingMethodException(nameof(GivePaymentHandler), "Pay"),
                          prefix: new HarmonyMethod(typeof(ShipyardSync), nameof(PayPrefix)));
            harmony.Patch(AccessTools.Method(typeof(ShipMarketUI), "LaunchShip")
                              ?? throw new MissingMethodException(nameof(ShipMarketUI), "LaunchShip"),
                          postfix: new HarmonyMethod(typeof(ShipyardSync), nameof(LaunchPostfix)));
        }

        // The payment dialog sets the purse to what it held when the dialog opened, less the payment,
        // and the plan to what was paid then, plus it. Another player's spending or paying meanwhile
        // would be undone; so it pays from the purse and the plan as they are now.
        private static void PayPrefix(GivePaymentHandler __instance)
        {
            PatchGuard.Run(() =>
            {
                DataPersistenceManager dp = GameAccess.Dp;
                ShipMarketUI ui = __instance.transform.parent != null ? __instance.transform.parent.GetComponent<ShipMarketUI>() : null;
                if (dp == null || dp.gameData == null || ui == null) return;
                float paid = (float)PayPaidField.GetValue(__instance);
                string kind = IdField.GetValue(ui) as string;
                string line = dp.gameData.shipPlansKnown?.Find(known => Kind(known) == kind);
                string[] fields = line != null ? line.Split('=') : null;
                if (fields != null && fields.Length > PlanFlags + 1 && TryNumber(fields[PlanFlags + 1], out float now)) paid = now;
                float money = dp.gameData.tearCount;
                PayPaidField.SetValue(__instance, paid);
                PayMoneyField.SetValue(__instance, money);
                if (PaySliderField.GetValue(__instance) is UnityEngine.UI.Slider slider)
                {
                    float most = Mathf.Max(0f, Mathf.Min(money, (float)PayCostField.GetValue(__instance) - paid));
                    if (slider.value > most) slider.SetValueWithoutNotify(most);
                }
            }, error => Plugin.Log.LogError("[Shipyard] " + error));
        }

        private static void LaunchPostfix(ShipMarketUI __instance)
        {
            PatchGuard.Run(() =>
            {
                CoopBehaviour coop = CoopBehaviour.Instance;
                if (coop == null || coop.Shipyard == null || !coop.Participating) return;
                coop.Shipyard.Launched(__instance);
            }, error => Plugin.Log.LogError("[Shipyard] " + error));
        }
    }
}
