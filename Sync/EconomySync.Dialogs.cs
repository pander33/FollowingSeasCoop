using System;
using System.Reflection;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The trade and the transfer dialogs read every store when they open and write every store
    /// back, as figures, when the player confirms. Meanwhile another player may have bought, eaten
    /// or stored something: the dialog's figures would undo that, and the undoing would travel as
    /// this player's change. So just before the dialog writes, its figures are moved onto the stores
    /// as they are now: what the player chose in it is kept as a change, not as a figure.
    /// </summary>
    internal sealed partial class EconomySync
    {
        // The dialogs name their fields by the stores' names ashore (ShoreNames).
        private static readonly string[] TransferSliders =
            { "foodBSSlider", "drinkBSSlider", "oakumBSSlider", "timberBSSlider", "canvasBSSlider", "oilBSSlider", "aburaeSlider" };

        private sealed class DialogFields
        {
            public FieldInfo Controller;
            public FieldInfo[] Amount = new FieldInfo[EconomyWire.Stores];
            public FieldInfo[] Start = new FieldInfo[EconomyWire.Stores];
            public FieldInfo[] StoredStart;
            public FieldInfo[] Slider;
            public FieldInfo Money;
        }

        private static DialogFields s_trade, s_transfer;
        private static FieldInfo s_fishHold, s_fishLeft, s_fishStart, s_fishChange;

        private static DialogFields Fields(Type type, bool transfer)
        {
            var f = new DialogFields { Controller = Need(type, "controller") };
            for (int i = 0; i < EconomyWire.Stores; i++)
            {
                string name = ShoreNames[i];
                f.Amount[i] = Need(type, char.ToLowerInvariant(name[0]) + name.Substring(1) + "Amt");
                f.Start[i] = Need(type, "start" + name + "Amt");
            }
            if (transfer)
            {
                f.StoredStart = new FieldInfo[EconomyWire.Stores];
                f.Slider = new FieldInfo[EconomyWire.Stores];
                for (int i = 0; i < EconomyWire.Stores; i++)
                {
                    f.StoredStart[i] = Need(type, "startStored" + ShoreNames[i] + "Amt");
                    f.Slider[i] = Need(type, TransferSliders[i]);
                }
            }
            else f.Money = Need(type, "moneyAmt");
            return f;
        }

        private static FieldInfo Need(Type type, string name) =>
            AccessTools.Field(type, name) ?? throw new MissingFieldException(type.Name, name);

        public static void InstallDialogs(Harmony harmony)
        {
            s_trade = Fields(typeof(BuySellProvisionsDialog), false);
            s_transfer = Fields(typeof(TransferProvisionsDialog), true);
            harmony.Patch(AccessTools.Method(typeof(BuySellProvisionsDialog), "CompleteTransaction")
                              ?? throw new MissingMethodException(nameof(BuySellProvisionsDialog), "CompleteTransaction"),
                          prefix: new HarmonyMethod(typeof(EconomySync), nameof(TradePrefix)));
            harmony.Patch(AccessTools.Method(typeof(TransferProvisionsDialog), "CompleteTransaction")
                              ?? throw new MissingMethodException(nameof(TransferProvisionsDialog), "CompleteTransaction"),
                          prefix: new HarmonyMethod(typeof(EconomySync), nameof(TransferPrefix)));
        }

        public static void InstallFishSale(Harmony harmony)
        {
            s_fishHold = Need(typeof(SellFishDialog), "fishCargoManager");
            s_fishLeft = Need(typeof(SellFishDialog), "qsAmt");
            s_fishStart = Need(typeof(SellFishDialog), "startQSAmt");
            s_fishChange = Need(typeof(SellFishDialog), "moneyChangeAmt");
            harmony.Patch(AccessTools.Method(typeof(SellFishDialog), "CompleteTransaction")
                              ?? throw new MissingMethodException(nameof(SellFishDialog), "CompleteTransaction"),
                          prefix: new HarmonyMethod(typeof(EconomySync), nameof(SellFishPrefix)));
        }

        // The dialog's figure and its starting figure both move by what the stores did meanwhile.
        // Done again on a second press (the first was refused for money) it changes nothing more.
        private static void Rebase(object dialog, DialogFields f, float[] now)
        {
            for (int i = 0; i < EconomyWire.Stores; i++)
            {
                float chosen = (float)f.Amount[i].GetValue(dialog) - (float)f.Start[i].GetValue(dialog);
                f.Amount[i].SetValue(dialog, Mathf.Max(0f, now[i] + chosen));
                f.Start[i].SetValue(dialog, now[i]);
            }
        }

        private static void TradePrefix(BuySellProvisionsDialog __instance)
        {
            PatchGuard.Run(() =>
            {
                if (!(s_trade.Controller.GetValue(__instance) is ProvisionsController c)) return;
                var now = new float[EconomyWire.Stores];
                Read(c, now);
                Rebase(__instance, s_trade, now);
                // Whether the purse holds the price is asked of the purse as it is now.
                DataPersistenceManager dp = GameAccess.Dp;
                if (dp != null && dp.gameData != null) s_trade.Money.SetValue(__instance, dp.gameData.tearCount);
            }, error => Plugin.Log.LogError("[Economy] " + error));
        }

        private static void TransferPrefix(TransferProvisionsDialog __instance)
        {
            PatchGuard.Run(() =>
            {
                if (!(s_transfer.Controller.GetValue(__instance) is ProvisionsController c)) return;
                DataPersistenceManager dp = GameAccess.Dp;
                if (dp == null || dp.gameData == null) return;
                var now = new float[EconomyWire.Stores];
                Read(c, now);
                Rebase(__instance, s_transfer, now);
                // Ashore the dialog writes its sliders.
                var shore = new float[EconomyWire.Stores];
                ReadShore(dp.gameData, shore);
                for (int i = 0; i < EconomyWire.Stores; i++)
                {
                    if (!(s_transfer.Slider[i].GetValue(__instance) is Slider slider)) continue;
                    float start = (float)s_transfer.StoredStart[i].GetValue(__instance);
                    float target = Mathf.Max(0f, shore[i] + slider.value - start);
                    if (slider.maxValue < target) slider.maxValue = target;
                    slider.SetValueWithoutNotify(target);
                    s_transfer.StoredStart[i].SetValue(__instance, shore[i]);
                }
            }, error => Plugin.Log.LogError("[Economy] " + error));
        }

        // The fish dialog writes the hold's count from the tonnes left in it when it opened: a fish
        // put in or sold by another player meanwhile would be undone. The fish this player sold
        // are taken from the hold as it is now; the price stays the dialog's. The slider cannot go
        // above the hold, so the dialog only ever sells: nothing to sell is its one refusal, after
        // which it stays open and must keep its own figures.
        private static void SellFishPrefix(SellFishDialog __instance)
        {
            PatchGuard.Run(() =>
            {
                if ((float)s_fishChange.GetValue(__instance) <= 0f) return;
                if (!(s_fishHold.GetValue(__instance) is FishCargoManager hold) || hold.unitWeight <= 0f) return;
                int start = Mathf.RoundToInt((float)s_fishStart.GetValue(__instance) / hold.unitWeight * 1000f);
                if (start == hold.currentFishCount) return;
                // As the game counts it: the whole fish below the tonnes chosen.
                int sold = start - (int)((float)s_fishLeft.GetValue(__instance) / hold.unitWeight * 1000f);
                int left = Math.Max(0, hold.currentFishCount - sold);
                s_fishLeft.SetValue(__instance, (left + 0.5f) * hold.unitWeight / 1000f);
                s_fishStart.SetValue(__instance, hold.currentFishCount * hold.unitWeight / 1000f);
            }, error => Plugin.Log.LogError("[Economy] " + error));
        }
    }
}
