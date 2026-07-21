namespace WSCrafter
{
    using System.Numerics;
    using ClickableTransparentOverlay.Win32;
    using GameHelper.Plugin;

    public sealed class WSCrafterSettings : IPSettings
    {
        public bool EnableOverlay = true;
        public bool ShowInventoryHighlights = true;
        public bool ShowCurrencyHighlights = true;
        public bool ShowDebugInfo = false;

        public bool ApplyAlchemyToNormalMaps = true;
        public bool ApplyExaltedToRareMaps = true;
        public bool ApplyVaalAsFinalStep = true;
        public int TargetExplicitMods = 6;

        public float AutomationStartDelaySeconds = 2.0f;
        public int AutomationClickDelayMs = 120;
        public int AutomationStepDelayMs = 450;
        public int AutomationItemUpdateTimeoutMs = 2500;
        public VK AutomationAbortKey = VK.F8;

        public bool[] SelectedInventorySlots = CreateDefaultSelection();

        public Vector4 SelectedSlotColor = new(0.15f, 0.65f, 0.20f, 0.85f);
        public Vector4 MissingMapColor = new(0.75f, 0.20f, 0.20f, 0.85f);
        public Vector4 PendingCraftColor = new(0.95f, 0.70f, 0.15f, 0.9f);
        public Vector4 ReadyMapColor = new(0.20f, 0.85f, 0.75f, 0.9f);
        public Vector4 CurrencyColor = new(0.45f, 0.65f, 1.0f, 0.95f);

        public float BorderThickness = 3f;

        private static bool[] CreateDefaultSelection()
        {
            var slots = new bool[60];
            for (var i = 0; i < slots.Length; i++)
            {
                slots[i] = true;
            }

            return slots;
        }
    }
}
