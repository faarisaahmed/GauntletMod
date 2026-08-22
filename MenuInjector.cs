using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace GauntletMod
{
    /// <summary>
    /// Adds the "Custom Gauntlet" entry to Silksong's play-mode menu (the screen
    /// that normally offers Normal / Steel Soul).
    ///
    /// Rather than build a button from scratch - which would mean matching the
    /// menu's fonts, animators, flash effects, audio hooks and navigation by hand -
    /// we clone the existing "Normal" button and swap out what it does. That keeps
    /// it visually identical to a real menu entry through any future style change.
    /// </summary>
    internal static class MenuInjector
    {
        private const string CloneName = "GauntletMod_CustomGauntlet";

        /// <summary>Default gap used only if we can't measure the menu's own spacing.</summary>
        private const float FallbackSpacing = 105f;

        private static int _injectedScreenId;

        /// <summary>Called when we're about to show the play-mode menu.</summary>
        public static void RequestInject() => TryInject();

        /// <summary>
        /// Safe to call every frame - it bails immediately once the current
        /// play-mode screen instance has been injected.
        /// </summary>
        public static void TryInject()
        {
            try
            {
                UIManager ui = UIManager.instance;
                if (ui == null) return;

                MenuScreen screen = ui.playModeMenuScreen;
                if (screen == null) return;

                int id = screen.gameObject.GetInstanceID();
                if (id == _injectedScreenId) return;

                if (Inject(screen))
                {
                    _injectedScreenId = id;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: menu injection failed: " + e);
                // Don't retry forever on a broken menu - the dev menu (F9) can still
                // start the gauntlet manually.
                _injectedScreenId = -1;
            }
        }

        public static void Forget() => _injectedScreenId = 0;

        private static bool Inject(MenuScreen screen)
        {
            // Already there? (e.g. the screen object survived a menu reload). The
            // button isn't a direct child of the screen root, so this has to be a
            // recursive look-up.
            foreach (Transform t in screen.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == CloneName) return true;
            }

            StartGameEventTrigger template = FindTemplate(screen);
            if (template == null)
            {
                Plugin.Log.LogError(
                    "Custom Gauntlet: no StartGameEventTrigger found under playModeMenuScreen - " +
                    "can't clone a mode button. Use the dev menu (F9) to start the gauntlet instead.");
                return false;
            }

            GameObject src = template.gameObject;
            Transform parent = src.transform.parent;

            GameObject clone = UnityEngine.Object.Instantiate(src, parent, false);
            clone.name = CloneName;
            clone.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + 1);
            CopyTransform(src.transform, clone.transform);
            clone.SetActive(true);

            // The cloned trigger would start a *normal* game, and it doubles as the
            // MenuButtonListCondition that could hide the button. Both have to go.
            foreach (MenuButtonListCondition cond in clone.GetComponentsInChildren<MenuButtonListCondition>(true))
            {
                UnityEngine.Object.DestroyImmediate(cond);
            }

            RelabelButton(clone);
            WireSubmit(clone);
            PositionBelowSiblings(parent, clone.transform, src.transform);
            RegisterWithButtonList(screen, clone);

            Plugin.Log.LogInfo($"Custom Gauntlet: menu button injected (cloned from '{src.name}').");
            return true;
        }

        // ------------------------------------------------------------------

        private static StartGameEventTrigger FindTemplate(MenuScreen screen)
        {
            StartGameEventTrigger[] triggers = screen.GetComponentsInChildren<StartGameEventTrigger>(true);
            if (triggers == null || triggers.Length == 0) return null;

            FieldInfo permaField = AccessTools.Field(typeof(StartGameEventTrigger), "permaDeath");
            FieldInfo bossField = AccessTools.Field(typeof(StartGameEventTrigger), "bossRush");

            // Prefer the plain "Normal" entry: it's the one that's always visible and
            // always interactable, so its styling is the safest thing to copy.
            foreach (StartGameEventTrigger t in triggers)
            {
                bool perma = permaField != null && (bool)permaField.GetValue(t);
                bool boss = bossField != null && (bool)bossField.GetValue(t);
                if (!perma && !boss) return t;
            }

            return triggers[0];
        }

        private static void CopyTransform(Transform src, Transform dst)
        {
            var srcRect = src as RectTransform;
            var dstRect = dst as RectTransform;

            if (srcRect != null && dstRect != null)
            {
                dstRect.anchorMin = srcRect.anchorMin;
                dstRect.anchorMax = srcRect.anchorMax;
                dstRect.pivot = srcRect.pivot;
                dstRect.sizeDelta = srcRect.sizeDelta;
                dstRect.anchoredPosition3D = srcRect.anchoredPosition3D;
            }
            else
            {
                dst.localPosition = src.localPosition;
            }

            dst.localRotation = src.localRotation;
            dst.localScale = src.localScale;
        }

        private static void RelabelButton(GameObject clone)
        {
            // AutoLocalizeTextUI re-reads its string from the localisation sheet on
            // every language refresh, which would stamp over whatever we set.
            foreach (AutoLocalizeTextUI loc in clone.GetComponentsInChildren<AutoLocalizeTextUI>(true))
            {
                UnityEngine.Object.DestroyImmediate(loc);
            }

            Text[] texts = clone.GetComponentsInChildren<Text>(true);
            if (texts.Length == 0)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: cloned button has no Text component to relabel.");
                return;
            }

            // Mode buttons are a big title plus a smaller description line. Largest
            // font wins the title.
            Text title = texts[0];
            foreach (Text t in texts)
            {
                if (t.fontSize > title.fontSize) title = t;
            }

            foreach (Text t in texts)
            {
                t.text = (t == title)
                    ? GauntletConfig.ModeName.Value
                    : GauntletConfig.ModeDescription.Value;
            }
        }

        private static void WireSubmit(GameObject clone)
        {
            MenuButton button = clone.GetComponent<MenuButton>();
            if (button == null)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: cloned button has no MenuButton component.");
                return;
            }

            UnityEvent submitted = button.OnSubmitPressed;
            if (submitted == null)
            {
                button.OnSubmitPressed = submitted = new UnityEvent();
            }

            // RemoveAllListeners only clears runtime listeners; anything wired up in
            // the scene is "persistent" and has to be switched off individually.
            for (int i = 0; i < submitted.GetPersistentEventCount(); i++)
            {
                submitted.SetPersistentListenerState(i, UnityEventCallState.Off);
            }
            submitted.RemoveAllListeners();
            submitted.AddListener(GauntletMode.StartFromMenu);
        }

        /// <summary>
        /// The menu buttons are absolutely positioned, so a fresh clone sits exactly
        /// on top of the button it was copied from. Drop it below the lowest sibling,
        /// spaced the same as the menu's own entries.
        /// </summary>
        private static void PositionBelowSiblings(Transform parent, Transform clone, Transform template)
        {
            if (parent == null) return;

            // A layout group already handles placement - leave it alone.
            if (parent.GetComponent<LayoutGroup>() != null) return;

            var cloneRect = clone as RectTransform;
            if (cloneRect == null) return;

            var ys = new List<float>();
            foreach (Transform child in parent)
            {
                if (child == clone) continue;
                if (child.GetComponent<MenuButton>() == null) continue;
                var rect = child as RectTransform;
                if (rect != null) ys.Add(rect.anchoredPosition.y);
            }

            if (ys.Count == 0) return;

            ys.Sort();                 // ascending; ys[0] is the lowest button
            float lowest = ys[0];

            float spacing = FallbackSpacing;
            if (ys.Count >= 2)
            {
                float measured = Mathf.Abs(ys[1] - ys[0]);
                if (measured > 1f) spacing = measured;
            }
            else
            {
                var templateRect = template as RectTransform;
                if (templateRect != null && templateRect.rect.height > 1f)
                {
                    spacing = templateRect.rect.height;
                }
            }

            Vector2 pos = cloneRect.anchoredPosition;
            pos.y = lowest - spacing;
            cloneRect.anchoredPosition = pos;
        }

        /// <summary>
        /// MenuButtonList owns keyboard/controller navigation and the show/hide
        /// pass. Its entry array is a private array of a private nested type, so
        /// this has to go through reflection (the patcher publicises these, but the
        /// plugin is built against the stock assembly and doesn't rely on that).
        /// </summary>
        private static void RegisterWithButtonList(MenuScreen screen, GameObject clone)
        {
            MenuButtonList list = screen.GetComponent<MenuButtonList>()
                                  ?? screen.GetComponentInChildren<MenuButtonList>(true);
            if (list == null)
            {
                Plugin.Log.LogWarning(
                    "Custom Gauntlet: no MenuButtonList on the play-mode screen - the button will " +
                    "work with the mouse but won't be reachable with a controller.");
                return;
            }

            Selectable selectable = clone.GetComponent<Selectable>();
            if (selectable == null)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: cloned button has no Selectable.");
                return;
            }

            FieldInfo entriesField = AccessTools.Field(typeof(MenuButtonList), "entries");
            if (entriesField == null)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: MenuButtonList.entries not found; skipping navigation hookup.");
                return;
            }

            Type entryType = entriesField.FieldType.GetElementType();
            var current = (Array)entriesField.GetValue(list);
            int length = current?.Length ?? 0;

            object entry = Activator.CreateInstance(entryType, nonPublic: true);
            FieldInfo selectableField = AccessTools.Field(entryType, "selectable");
            if (selectableField == null)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: MenuButtonList.Entry.selectable not found; skipping navigation hookup.");
                return;
            }
            selectableField.SetValue(entry, selectable);

            // condition stays null so the entry is unconditionally shown, which is
            // exactly what SetupActive() treats as "always available".

            Array grown = Array.CreateInstance(entryType, length + 1);
            if (length > 0) Array.Copy(current, grown, length);
            grown.SetValue(entry, length);
            entriesField.SetValue(list, grown);

            // Rebuild the active list so up/down navigation includes us.
            list.SetupActive();
        }
    }
}
