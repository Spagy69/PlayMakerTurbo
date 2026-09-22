using MSCLoader;
using UnityEngine;

namespace PlayMakerTurboAddon
{
    // cInputGUI draws the key binding menu in OnGUI and does nothing while the menu is closed, but Unity still sets
    // up IMGUI for it on every GUI event, about 0.5 KB of garbage per frame. It is switched off while the menu is
    // closed. cGUI raises OnGUIToggled on every open and close, so nothing has to check it every frame.
    internal static class CInputGuiSwitch
    {
        private static cInputGUI gui;

        public static void Apply()
        {
            GameObject cObject = GameObject.Find("cObject");
            gui = cObject != null ? cObject.GetComponent<cInputGUI>() : null;
            // cInputGUI.Start sets cInputExists; a disabled component would never run it and the menu would not open.
            if (gui == null || !cGUI.cInputExists)
            {
                ModConsole.Print("PlayMaker Turbo Addon: cInputGUI not found or not started, the key binding menu is left as it is.");
                return;
            }
            cGUI.OnGUIToggled -= Sync;
            cGUI.OnGUIToggled += Sync;
            Sync();
        }

        public static string Summary()
        {
            return "Key binding menu GUI: " + (gui == null ? "off (left as it is)" : gui.enabled ? "on (menu open)" : "off while the menu is closed");
        }

        private static void Sync()
        {
            if (gui != null)
                gui.enabled = cGUI.showingInputGUI;
        }
    }
}
