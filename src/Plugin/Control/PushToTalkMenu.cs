using System;
using System.Collections.Generic;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.UI;
using UINavigation = UnityEngine.UI.Navigation;

namespace SousChef.Plugin.Control
{
    internal sealed class PushToTalkMenu : IDisposable
    {
        private const string Label = "[Sous Chef] Push to Talk";
        private static readonly AccessTools.FieldRef<KeyboardRebindElement, T17Text> ActionText =
            AccessTools.FieldRefAccess<KeyboardRebindElement, T17Text>("m_ActionText");
        private static readonly AccessTools.FieldRef<KeyboardRebindElement, T17Text> BindingText =
            AccessTools.FieldRefAccess<KeyboardRebindElement, T17Text>("m_KeyBindingsText");
        private static readonly AccessTools.FieldRef<KeyboardRebindController, T17DialogBox> RebindDialog =
            AccessTools.FieldRefAccess<KeyboardRebindController, T17DialogBox>("m_RebindDialog");
        private static readonly System.Reflection.MethodInfo HideDialog =
            AccessTools.Method(typeof(KeyboardRebindController), "HideRebindDialog");

        private static PushToTalkMenu instance;
        private readonly Harmony patches = new Harmony("dev.souschef.push-to-talk");
        private readonly PushToTalk input;
        private readonly Action<string> log;
        private readonly Dictionary<KeyboardRebindElementSet, Row> rows = new Dictionary<KeyboardRebindElementSet, Row>();
        private KeyboardRebindController controller, capturing;
        private BaseMenuBehaviour screen;
        private KeyboardRebindElementSet[] sets = new KeyboardRebindElementSet[0];
        private KeyboardRebindElementSet visibleSet;

        public bool IsOpen { get { return controller != null && controller.isActiveAndEnabled && screen.isActiveAndEnabled; } }

        public PushToTalkMenu(PushToTalk input, Action<string> log)
        {
            this.input = input;
            this.log = log;
            try
            {
                patches.Patch(AccessTools.Method(typeof(BaseMenuBehaviour), nameof(BaseMenuBehaviour.Show)),
                    postfix: new HarmonyMethod(typeof(PushToTalkMenu), nameof(Shown)));
                patches.Patch(HideDialog, postfix: new HarmonyMethod(typeof(PushToTalkMenu), nameof(DialogHidden)));
                instance = this;
            }
            catch { patches.UnpatchSelf(); throw; }
        }

        private static void Shown(BaseMenuBehaviour __instance, bool __result)
        {
            var menu = instance;
            if (menu == null || !__result ||
                !(__instance is FrontendControllerOptionsMenu || __instance is InGameControllerOptionsMenu)) return;
            var controls = __instance.GetComponentInChildren<KeyboardRebindController>(true);
            if (controls == null) return;
            menu.CancelCapture();
            menu.controller = controls;
            menu.screen = __instance;
            menu.visibleSet = null;
            menu.sets = controls.GetComponentsInChildren<KeyboardRebindElementSet>(true);
            menu.input.Cancel();
            foreach (var set in new List<KeyboardRebindElementSet>(menu.rows.Keys))
                if (set == null) menu.rows.Remove(set);
        }

        public void Tick()
        {
            if (!IsOpen)
            {
                visibleSet = null;
                CancelCapture();
                return;
            }

            var active = Array.Find(sets, set => set != null && set.isActiveAndEnabled);
            if (active == visibleSet) return;
            visibleSet = active;
            if (active == null) return;

            try
            {
                Row row;
                if (!rows.TryGetValue(active, out row))
                {
                    row = CreateRow(active);
                    rows.Add(active, row);
                }
                if (row != null)
                {
                    row.Button.SetEventSystem(screen.CachedEventSystem);
                    row.Link();
                    row.Refresh(input.BindingLabel);
                }
            }
            catch (Exception e) { log("Could not add the Sous Chef binding row: " + e); }
        }

        private Row CreateRow(KeyboardRebindElementSet set)
        {
            var elements = set.GetComponentsInChildren<KeyboardRebindElement>();
            if (elements.Length == 0) return null;
            var template = elements[elements.Length - 1];
            var button = template.GetComponent<T17Button>();
            if (button == null || template.transform.parent.GetComponent<VerticalLayoutGroup>() == null) return null;

            var clone = UnityEngine.Object.Instantiate(template, template.transform.parent, false);
            var row = new Row(button, clone.GetComponent<T17Button>(), BindingText(clone));
            ActionText(clone).SetNonLocalizedText(Label);
            // Strip the copied native action and its serialized callback. The row
            // remains a normal game button, outside every native binding set.
            UnityEngine.Object.DestroyImmediate(clone);
            row.Button.gameObject.name = "SousChefPushToTalk";
            row.Button.onClick = new Button.ButtonClickedEvent();
            row.Button.onClick.AddListener(() => BeginCapture(row));
            return row;
        }

        private void BeginCapture(Row row)
        {
            if (!IsOpen || controller.IsRebinding || !Application.isFocused) return;
            var events = screen.CachedEventSystem;
            if (events == null) return;
            events.SetSelectedGameObject(row.Button.gameObject);
            var dialog = T17DialogBoxManager.GetDialog(false);
            if (dialog == null) return;
            capturing = controller;
            RebindDialog(controller) = dialog;
            try
            {
                dialog.Initialize(Label, "Press a key. Esc cancels. Backspace clears the binding.",
                    null, null, null, T17DialogBox.Symbols.Unassigned, false, false, false);
                dialog.Show();
                PCPadInputProvider.StartListeningForBinding(KeyCaptured);
            }
            catch (Exception e)
            {
                CancelCapture();
                log("Could not open the Sous Chef binding prompt: " + e);
            }
        }

        private void KeyCaptured(Key key)
        {
            bool canSave = capturing != null && capturing.isActiveAndEnabled && Application.isFocused;
            CancelCapture();
            if (!canSave || key == Key.Escape) return;
            try
            {
                input.Rebind(key == Key.Backspace ? Key.None : key);
                foreach (var row in rows.Values)
                    if (row != null && row.Button != null) row.Refresh(input.BindingLabel);
            }
            catch (Exception e) { log("Could not save the Sous Chef key: " + e); }
        }

        private static void DialogHidden(KeyboardRebindController __instance)
        {
            if (instance != null && instance.capturing == __instance) instance.StopListening();
        }

        public void CancelCapture()
        {
            if (ReferenceEquals(capturing, null)) return;
            if (capturing != null) HideDialog.Invoke(capturing, null);
            else StopListening();
        }

        private void StopListening()
        {
            capturing = null;
            PCPadInputProvider.StopListeningForBinding();
        }

        public void Dispose()
        {
            CancelCapture();
            foreach (var row in rows.Values)
                if (row != null && row.Button != null) row.Remove();
            instance = null;
            patches.UnpatchSelf();
        }

        private sealed class Row
        {
            public readonly T17Button Button;
            private readonly T17Button previous;
            private readonly T17Text keyText;
            private Selectable next;
            private Selectable nextOriginalUp;

            public Row(T17Button previous, T17Button button, T17Text keyText)
            {
                this.previous = previous;
                Button = button;
                this.keyText = keyText;
            }

            public void Refresh(string label)
            {
                keyText.SetNonLocalizedText(String.IsNullOrEmpty(label) ? "Unbound" : label);
            }

            public void Link()
            {
                var nav = previous.navigation;
                if (nav.selectOnDown != Button) next = nav.selectOnDown;
                nav.selectOnDown = Button;
                previous.navigation = nav;
                Button.navigation = new UINavigation { mode = UINavigation.Mode.Explicit, selectOnUp = previous, selectOnDown = next };
                if (next == null) return;
                nav = next.navigation;
                if (nav.selectOnUp != Button) nextOriginalUp = nav.selectOnUp;
                nav.selectOnUp = Button;
                next.navigation = nav;
            }

            public void Remove()
            {
                if (previous != null)
                {
                    var nav = previous.navigation;
                    if (nav.selectOnDown == Button) { nav.selectOnDown = next; previous.navigation = nav; }
                }
                if (next != null)
                {
                    var nav = next.navigation;
                    if (nav.selectOnUp == Button) { nav.selectOnUp = nextOriginalUp; next.navigation = nav; }
                }
                UnityEngine.Object.Destroy(Button.gameObject);
            }
        }
    }
}
