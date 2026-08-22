using UnityEngine;

namespace GauntletMod
{
    /// <summary>
    /// Sets the player to a "fully equipped" state for gauntlet testing:
    /// max health, max silk, all traversal/combat abilities, max needle upgrades,
    /// and every red/blue/yellow tool unlocked and filled.
    ///
    /// The PlayerData field names below match the live PlayerData class (checked
    /// against the decompiled Assembly-CSharp, not guessed). If a future game patch
    /// renames a field, this is the only file that needs updating.
    /// </summary>
    public static class PlayerBuffs
    {
        public static void ApplyFullLoadout()
        {
            ApplyStats();
            ApplyTools();
        }

        // ------------------------------------------------------------------

        private static void ApplyStats()
        {
            var pd = PlayerData.instance;
            if (pd == null)
            {
                Plugin.Log.LogWarning("PlayerBuffs: PlayerData.instance was null, skipping stats.");
                return;
            }

            try
            {
                // --- Health ---
                // maxHealthBase is the real stat; maxHealth is derived from it and gets
                // recomputed (HeroController.CharmUpdate does exactly
                // "maxHealth = maxHealthBase"), so writing maxHealth directly is
                // pointless - it just gets overwritten. Set the base and let the game
                // derive the rest, which is also what makes the HUD rebuild its mask
                // row instead of showing whatever it had at scene start.
                pd.SetInt(nameof(PlayerData.maxHealthBase), 11);
                pd.SetInt(nameof(PlayerData.health), 11);

                // --- Silk ---
                pd.SetInt(nameof(PlayerData.silkMax), 9);
                pd.SetInt(nameof(PlayerData.silk), 9);

                // --- Needle (nail) upgrades ---
                pd.SetInt(nameof(PlayerData.nailUpgrades), 4); // highest known needle tier
                pd.SetInt(nameof(PlayerData.silkSpecialLevel), 3);

                // --- Tool capacity ---
                // Pouch = how many tools you can carry, Kit = tool damage tier. Without
                // these, unlocking every tool still leaves you with nowhere to put them.
                pd.SetInt(nameof(PlayerData.ToolPouchUpgrades), 4);
                pd.SetInt(nameof(PlayerData.ToolKitUpgrades), 4);
                pd.SetBool(nameof(PlayerData.UnlockedExtraBlueSlot), true);
                pd.SetBool(nameof(PlayerData.UnlockedExtraYellowSlot), true);

                // --- Traversal abilities ---
                pd.SetBool(nameof(PlayerData.hasDash), true);
                pd.SetBool(nameof(PlayerData.hasBrolly), true);
                pd.SetBool(nameof(PlayerData.hasWalljump), true);
                pd.SetBool(nameof(PlayerData.hasDoubleJump), true);
                pd.SetBool(nameof(PlayerData.hasSuperJump), true);
                pd.SetBool(nameof(PlayerData.hasHarpoonDash), true);

                // --- Combat abilities ---
                pd.SetBool(nameof(PlayerData.hasNeedleThrow), true);
                pd.SetBool(nameof(PlayerData.hasThreadSphere), true);
                pd.SetBool(nameof(PlayerData.hasParry), true);
                pd.SetBool(nameof(PlayerData.hasSilkCharge), true);
                pd.SetBool(nameof(PlayerData.hasSilkBomb), true);
                pd.SetBool(nameof(PlayerData.hasSilkBossNeedle), true);
                pd.SetBool(nameof(PlayerData.hasSilkSpecial), true);
                pd.SetBool(nameof(PlayerData.hasChargeSlash), true);
                pd.SetBool(nameof(PlayerData.hasQuill), true);
                pd.SetBool(nameof(PlayerData.hasNeedolin), true);

                RefreshHealthDisplay();

                Plugin.Log.LogInfo($"PlayerBuffs: stats and abilities applied ({pd.maxHealth} masks, {pd.CurrentSilkMax} silk).");
            }
            catch (System.Exception e)
            {
                // Never let a buff failure crash the game - just log it and move on.
                Plugin.Log.LogError("PlayerBuffs: failed to apply one or more stats: " + e);
            }
        }

        /// <summary>
        /// Makes the HUD actually redraw the mask row.
        ///
        /// Broadcasting ad-hoc FSM events at it doesn't work - the row is rebuilt by
        /// the same path the game uses when you collect a mask shard, and nothing else
        /// tells it the maximum changed. CharmUpdate re-derives maxHealth from
        /// maxHealthBase, HeroController.MaxHealth fires the proxy FSM event and
        /// HeroHealedToMax, and HEALTH UPDATE is what the HUD listens on.
        /// </summary>
        private static void RefreshHealthDisplay()
        {
            try
            {
                HeroController hero = HeroController.instance;
                if (hero == null) return;

                hero.CharmUpdate();
                hero.MaxHealth();

                EventRegister.SendEvent(EventRegisterEvents.HealthUpdate);
                EventRegister.SendEvent(EventRegisterEvents.HeroHealedToMax);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("PlayerBuffs: couldn't refresh the health display: " + e.Message);
            }
        }

        /// <summary>
        /// Unlocks the crest sockets that normally cost a Memory Locket.
        ///
        /// UnlockAllCrests unlocks the crests themselves but not their locked sockets:
        /// those are per-crest save data, and a socket only opens when its SlotData
        /// says IsUnlocked. Lockets are also granted so the inventory shows a
        /// consistent story rather than sockets that opened from nowhere.
        /// </summary>
        private static void UnlockCrestSockets()
        {
            var pd = PlayerData.instance;
            if (pd?.ToolEquips == null) return;

            int opened = 0;

            try
            {
                foreach (ToolCrest crest in ToolItemManager.GetAllCrests())
                {
                    if (crest == null) continue;

                    ToolCrestsData.Data data = pd.ToolEquips.GetData(crest.name);
                    if (data.Slots == null) continue;

                    for (int i = 0; i < data.Slots.Count; i++)
                    {
                        ToolCrestsData.SlotData slot = data.Slots[i];
                        if (slot.IsUnlocked) continue;

                        // SlotData is a struct inside a List, so it has to be copied
                        // out, changed and written back.
                        slot.IsUnlocked = true;
                        data.Slots[i] = slot;
                        opened++;
                    }

                    data.IsUnlocked = true;
                    pd.ToolEquips.SetData(crest.name, data);
                }

                Plugin.Log.LogInfo($"PlayerBuffs: opened {opened} locked crest socket(s).");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("PlayerBuffs: couldn't unlock crest sockets: " + e);
            }
        }

        /// <summary>
        /// The bit the old version was missing entirely. Tools aren't PlayerData
        /// bools - they're ToolItem ScriptableObjects tracked through
        /// ToolItemManager, so no amount of SetBool would ever grant them.
        ///
        /// ToolItemManager.UnlockAllTools() walks every tool in the game's own list,
        /// marks its unlock conditions complete, unlocks it and fills its charges -
        /// red, blue and yellow alike. UnlockAllCrests() then unlocks the crests and
        /// the extra blue/yellow equip slots, so there's somewhere to put them.
        ///
        /// This has to run in a gameplay scene: ToolItemManager is a
        /// ManagerSingleton that doesn't exist yet while you're still on the menu,
        /// which is why the gauntlet applies the loadout again on arrival rather
        /// than only at new-game time.
        /// </summary>
        private static void ApplyTools()
        {
            try
            {
                if (ToolItemManager.Instance == null)
                {
                    Plugin.Log.LogInfo("PlayerBuffs: ToolItemManager not up yet, tools will be granted on arrival.");
                    return;
                }

                ToolItemManager.UnlockAllTools();
                ToolItemManager.UnlockAllCrests();
                UnlockCrestSockets();

                // Nudge the HUD so the new tools show up without a scene change.
                ToolItemManager.SendEquippedChangedEvent(force: true);

                Plugin.Log.LogInfo("PlayerBuffs: all tools and crests unlocked.");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("PlayerBuffs: failed to unlock tools: " + e);
            }
        }
    }
}
