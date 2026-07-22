namespace WSCrafter
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using ClickableTransparentOverlay.Win32;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;
    using GameHelper.Utils;

    public sealed class WSCrafterCore : PCore<WSCrafterSettings>
    {
        private const int InventoryColumns = 12;
        private const int InventoryRows = 5;
        private const int InventorySlots = InventoryColumns * InventoryRows;
        private const int DefaultScanStartOffset = 0x20;
        private const int DefaultScanEndOffset = 0x600;
        private const int KnownItemPointerOffset = 0x4F8;
        private const long IdleScanIntervalMs = 250;
        private const long AutomationScanIntervalMs = 100;
        private const long CurrencyScanIntervalMs = 3000;

        private object? handleObj;
        private MethodInfo? readStdWStringMethod;
        private readonly Dictionary<Type, MethodInfo> readMemoryMethods = new();
        private readonly Dictionary<Type, MethodInfo> tryReadMemoryMethods = new();
        private readonly Dictionary<Type, MethodInfo> readStdVectorMethods = new();
        private readonly Dictionary<IntPtr, int> itemPointerOffsetByElement = new();
        private readonly Dictionary<CurrencyKind, SlotInfo> cachedCurrencies = new();
        private readonly List<CurrencyDebugInfo> cachedCurrencyCandidates = new();

        private readonly List<CraftingSlot> latestCraftingSlots = new();
        private readonly Dictionary<CurrencyKind, SlotInfo> latestCurrencies = new();
        private readonly List<CurrencyDebugInfo> latestCurrencyCandidates = new();
        private string latestStatus = "Waiting for stash/inventory UI.";
        private IntPtr latestGameUiAddress = IntPtr.Zero;
        private IntPtr latestLeftPanelAddress = IntPtr.Zero;
        private IntPtr latestRightPanelAddress = IntPtr.Zero;
        private bool latestLeftPanelVisible;
        private bool latestRightPanelVisible;
        private int latestLeftPanelItemsScanned;
        private int latestRightPanelItemsScanned;
        private IntPtr latestInventoryGridRoot = IntPtr.Zero;
        private bool automationActive;
        private AutomationPhase automationPhase = AutomationPhase.Idle;
        private DateTime nextAutomationActionAt = DateTime.MinValue;
        private CurrencyKind pendingCurrencyKind;
        private Vector2 pendingCurrencyCenter;
        private Vector2 pendingWaystoneCenter;
        private int pendingWaystoneSlotIndex = -1;
        private int pendingWaystoneColumn = -1;
        private int pendingWaystoneRow = -1;
        private Rarity pendingWaystoneRarity;
        private int pendingWaystoneExplicitMods;
        private DateTime pendingItemUpdateDeadline = DateTime.MinValue;
        private int automationActionsSent;
        private string automationStatus = "Idle.";
        private readonly HashSet<int> vaalAttemptedSlotIndexes = new();
        private string latestMemoryScanStatus = "No memory scan yet.";
        private long nextVisibleTargetsUpdateTick;
        private long nextCurrencyScanTick;
        private IntPtr cachedCurrencyLeftPanelAddress = IntPtr.Zero;
        private IntPtr cachedCurrencyRightPanelAddress = IntPtr.Zero;
        private int cachedLeftPanelItemsScanned;
        private int cachedRightPanelItemsScanned;
        private bool forceCurrencyRescan = true;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                try
                {
                    this.Settings = JsonConvert.DeserializeObject<WSCrafterSettings>(File.ReadAllText(this.SettingPathname)) ?? new WSCrafterSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WSCrafter] Failed to load settings: {ex.Message}");
                    this.Settings = new WSCrafterSettings();
                }
            }

            this.NormalizeSettings();
            this.InitReflection();
        }

        public override void OnDisable()
        {
            this.handleObj = null;
            this.readStdWStringMethod = null;
            this.readMemoryMethods.Clear();
            this.tryReadMemoryMethods.Clear();
            this.readStdVectorMethods.Clear();
            this.itemPointerOffsetByElement.Clear();
            this.cachedCurrencies.Clear();
            this.cachedCurrencyCandidates.Clear();
            this.latestCraftingSlots.Clear();
            this.latestCurrencies.Clear();
            this.latestCurrencyCandidates.Clear();
            this.StopAutomation("Disabled.");
        }

        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
                File.WriteAllText(this.SettingPathname, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WSCrafter] Failed to save settings: {ex.Message}");
            }
        }

        public override void DrawSettings()
        {
            this.NormalizeSettings();

            var changed = false;
            changed |= ImGui.Checkbox("Enable overlay", ref this.Settings.EnableOverlay);
            changed |= ImGui.Checkbox("Highlight inventory waystones", ref this.Settings.ShowInventoryHighlights);
            changed |= ImGui.Checkbox("Highlight located currency", ref this.Settings.ShowCurrencyHighlights);
            changed |= ImGui.Checkbox("Show debug info", ref this.Settings.ShowDebugInfo);

            ImGui.SeparatorText("Crafting Plan");
            changed |= ImGui.Checkbox("Alchemy maps below rare", ref this.Settings.ApplyAlchemyToNormalMaps);
            changed |= ImGui.Checkbox("Exalt rare maps to target mod count", ref this.Settings.ApplyExaltedToRareMaps);
            changed |= ImGui.Checkbox("Vaal as final step", ref this.Settings.ApplyVaalAsFinalStep);
            changed |= ImGui.SliderInt("Target explicit mods", ref this.Settings.TargetExplicitMods, 1, 6);

            ImGui.SeparatorText("Automation");
            changed |= ImGui.SliderFloat("Start delay (sec)", ref this.Settings.AutomationStartDelaySeconds, 0.5f, 5f, "%.1f");
            changed |= ImGui.SliderInt("Click delay (ms)", ref this.Settings.AutomationClickDelayMs, 50, 500);
            changed |= ImGui.SliderInt("Step delay (ms)", ref this.Settings.AutomationStepDelayMs, 150, 1500);
            changed |= ImGui.SliderInt("Item update timeout (ms)", ref this.Settings.AutomationItemUpdateTimeoutMs, 500, 5000);
            changed |= ImGuiHelper.NonContinuousEnumComboBox("Emergency stop key", ref this.Settings.AutomationAbortKey);
            if (!this.automationActive)
            {
                if (ImGui.Button("Start crafting selected waystones"))
                {
                    this.StartAutomation();
                }
            }
            else if (ImGui.Button("Stop crafting"))
            {
                this.StopAutomation("Stopped by user.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Kill automation now"))
            {
                this.StopAutomation("Killed by user.");
            }

            ImGui.TextWrapped($"Automation: {this.automationStatus}");

            ImGui.SeparatorText("Inventory Slots");
            changed |= this.DrawSelectionControls();
            changed |= this.DrawSelectionGrid(interactive: true);

            ImGui.SeparatorText("Colors");
            changed |= ImGui.ColorEdit4("Missing currency", ref this.Settings.MissingMapColor);
            changed |= ImGui.ColorEdit4("Pending craft", ref this.Settings.PendingCraftColor);
            changed |= ImGui.ColorEdit4("Ready map", ref this.Settings.ReadyMapColor);
            changed |= ImGui.ColorEdit4("Currency", ref this.Settings.CurrencyColor);
            changed |= ImGui.SliderFloat("Border thickness", ref this.Settings.BorderThickness, 1f, 8f, "%.1f");

            if (changed)
            {
                this.SaveSettings();
            }
        }

        public override void DrawUI()
        {
            if (!this.Settings.EnableOverlay)
            {
                return;
            }

            if (this.automationActive && NativeMouse.IsKeyDown((int)this.Settings.AutomationAbortKey))
            {
                this.StopAutomation($"Emergency stopped by {this.Settings.AutomationAbortKey}.");
            }

            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            if (this.handleObj == null && !this.InitReflection())
            {
                return;
            }

            if (this.ShouldUpdateVisibleTargets())
            {
                this.UpdateVisibleTargets();
            }

            this.UpdateAutomation();
            this.DrawOverlayHighlights();
            this.DrawAutomationStatusOverlay();

            if (this.Settings.ShowDebugInfo)
            {
                this.DrawDebugWindow();
            }
        }

        private bool ShouldUpdateVisibleTargets()
        {
            var now = Environment.TickCount64;
            if (now < this.nextVisibleTargetsUpdateTick)
            {
                return false;
            }

            this.nextVisibleTargetsUpdateTick = now + (this.automationActive ? AutomationScanIntervalMs : IdleScanIntervalMs);
            return true;
        }

        private void UpdateVisibleTargets()
        {
            this.latestCraftingSlots.Clear();
            this.latestCurrencies.Clear();
            this.latestCurrencyCandidates.Clear();
            this.latestStatus = "Open your currency stash tab and inventory.";
            this.latestGameUiAddress = IntPtr.Zero;
            this.latestLeftPanelAddress = IntPtr.Zero;
            this.latestRightPanelAddress = IntPtr.Zero;
            this.latestLeftPanelVisible = false;
            this.latestRightPanelVisible = false;
            this.latestLeftPanelItemsScanned = 0;
            this.latestRightPanelItemsScanned = 0;
            this.latestInventoryGridRoot = IntPtr.Zero;

            var gameUi = Core.States.InGameStateObject.GameUi;
            if (gameUi == null || gameUi.Address == IntPtr.Zero)
            {
                return;
            }

            this.latestGameUiAddress = gameUi.Address;
            this.latestLeftPanelAddress = gameUi.LeftPanel.Address;
            this.latestRightPanelAddress = gameUi.RightPanel.Address;
            this.latestLeftPanelVisible = gameUi.LeftPanel.Address != IntPtr.Zero && this.IsElementVisible(gameUi.LeftPanel.Address);
            this.latestRightPanelVisible = gameUi.RightPanel.Address != IntPtr.Zero && this.IsElementVisible(gameUi.RightPanel.Address);

            this.UpdateCurrencyTargets(gameUi);

            if (this.latestRightPanelVisible)
            {
                var inventoryRoot = this.ResolveInventoryGridRoot(gameUi.RightPanel.Address);
                this.latestInventoryGridRoot = inventoryRoot;
                if (inventoryRoot != IntPtr.Zero)
                {
                    var inventorySlots = this.ScanDirectChildItemSlots(inventoryRoot)
                        .OrderBy(slot => slot.Column)
                        .ThenBy(slot => slot.Row)
                        .ToList();

                    foreach (var slot in inventorySlots)
                    {
                        if (slot.Index < 0 || slot.Index >= InventorySlots || !this.Settings.SelectedInventorySlots[slot.Index])
                        {
                            continue;
                        }

                        if (IsWaystoneLike(slot.Item))
                        {
                            this.latestCraftingSlots.Add(this.BuildCraftingSlot(slot));
                        }
                    }

                    this.latestStatus = $"Inventory grid found. Selected waystones: {this.latestCraftingSlots.Count}.";
                }
            }
        }

        private void UpdateCurrencyTargets(ImportantUiElements gameUi)
        {
            var panelsChanged = this.cachedCurrencyLeftPanelAddress != this.latestLeftPanelAddress ||
                                this.cachedCurrencyRightPanelAddress != this.latestRightPanelAddress;
            var now = Environment.TickCount64;
            if (panelsChanged || this.forceCurrencyRescan || now >= this.nextCurrencyScanTick)
            {
                this.RescanCurrencyTargets(gameUi);
                this.cachedCurrencyLeftPanelAddress = this.latestLeftPanelAddress;
                this.cachedCurrencyRightPanelAddress = this.latestRightPanelAddress;
                this.nextCurrencyScanTick = now + CurrencyScanIntervalMs;
                this.forceCurrencyRescan = false;
                return;
            }

            foreach (var pair in this.cachedCurrencies)
            {
                this.latestCurrencies[pair.Key] = pair.Value;
            }

            this.latestCurrencyCandidates.AddRange(this.cachedCurrencyCandidates);
            this.latestLeftPanelItemsScanned = this.cachedLeftPanelItemsScanned;
            this.latestRightPanelItemsScanned = this.cachedRightPanelItemsScanned;
        }

        private void RescanCurrencyTargets(ImportantUiElements gameUi)
        {
            this.cachedCurrencies.Clear();
            this.cachedCurrencyCandidates.Clear();

            if (this.latestLeftPanelVisible)
            {
                var leftSlots = this.ScanItemSlots(gameUi.LeftPanel.Address);
                this.latestLeftPanelItemsScanned = leftSlots.Count;
                foreach (var slot in leftSlots)
                {
                    this.TrackCurrency(slot);
                }
            }

            if (this.latestRightPanelVisible)
            {
                var rightSlots = this.ScanItemSlots(gameUi.RightPanel.Address);
                this.latestRightPanelItemsScanned = rightSlots.Count;
                foreach (var slot in rightSlots)
                {
                    this.TrackCurrency(slot);
                }
            }

            foreach (var pair in this.latestCurrencies)
            {
                this.cachedCurrencies[pair.Key] = pair.Value;
            }

            this.cachedCurrencyCandidates.AddRange(this.latestCurrencyCandidates);
            this.cachedLeftPanelItemsScanned = this.latestLeftPanelItemsScanned;
            this.cachedRightPanelItemsScanned = this.latestRightPanelItemsScanned;
        }

        private IntPtr ResolveInventoryGridRoot(IntPtr rightPanel)
        {
            var inventoryPanel = this.ResolvePath(rightPanel, new[] { 5 });
            if (inventoryPanel != IntPtr.Zero && this.IsElementVisible(inventoryPanel))
            {
                var grid = this.ResolvePath(inventoryPanel, new[] { 36 });
                if (grid != IntPtr.Zero)
                {
                    return grid;
                }
            }

            var coopInventoryPanel = this.ResolvePath(rightPanel, new[] { 3, 0, 0, 1 });
            if (coopInventoryPanel != IntPtr.Zero && this.IsElementVisible(coopInventoryPanel))
            {
                return this.ResolvePath(coopInventoryPanel, new[] { 0, 2 });
            }

            return IntPtr.Zero;
        }

        private void TrackCurrency(SlotInfo slot)
        {
            var name = GetItemNameOrFallback(slot.Item);
            if (TryGetCurrencyKind(slot.Item, out var kind))
            {
                this.latestCurrencyCandidates.Add(new CurrencyDebugInfo(kind.ToString(), true, name, slot.Item.Path ?? string.Empty, slot));
                this.latestCurrencies.TryAdd(kind, slot);
                return;
            }

            if (LooksCurrencyLike(slot.Item, name))
            {
                this.latestCurrencyCandidates.Add(new CurrencyDebugInfo("Unmatched", false, name, slot.Item.Path ?? string.Empty, slot));
            }
        }

        private CraftingSlot BuildCraftingSlot(SlotInfo slot)
        {
            if (!TryGetItemName(slot.Item, out var name))
            {
                name = "Unknown item";
            }

            var rarity = Rarity.Normal;
            var explicitMods = 0;
            if (slot.Item.TryGetComponent<Mods>(out var mods))
            {
                rarity = mods.Rarity;
                explicitMods = mods.ExplicitMods.Count;
            }

            var state = CraftingState.Ready;
            var nextStep = "Ready";
            CurrencyKind? neededCurrency = null;

            if (this.IsCraftingComplete(slot))
            {
                nextStep = "Already corrupted";
            }
            else if (slot.Index >= 0 && this.vaalAttemptedSlotIndexes.Contains(slot.Index))
            {
                nextStep = "Vaal attempted";
            }
            else if (this.Settings.ApplyAlchemyToNormalMaps && IsBelowRare(rarity))
            {
                neededCurrency = CurrencyKind.Alchemy;
                state = this.latestCurrencies.ContainsKey(CurrencyKind.Alchemy) ? CraftingState.NeedsAlchemy : CraftingState.MissingCurrency;
                nextStep = this.latestCurrencies.ContainsKey(CurrencyKind.Alchemy) ? "Use Orb of Alchemy" : "Missing Orb of Alchemy";
            }
            else if (this.Settings.ApplyExaltedToRareMaps && rarity == Rarity.Rare && explicitMods < this.Settings.TargetExplicitMods)
            {
                neededCurrency = CurrencyKind.Exalted;
                state = this.latestCurrencies.ContainsKey(CurrencyKind.Exalted) ? CraftingState.NeedsExalted : CraftingState.MissingCurrency;
                nextStep = this.latestCurrencies.ContainsKey(CurrencyKind.Exalted)
                    ? $"Use Exalted Orb ({explicitMods}/{this.Settings.TargetExplicitMods} explicit)"
                    : "Missing Exalted Orb";
            }
            else if (this.Settings.ApplyVaalAsFinalStep)
            {
                neededCurrency = CurrencyKind.Vaal;
                state = this.latestCurrencies.ContainsKey(CurrencyKind.Vaal) ? CraftingState.NeedsVaal : CraftingState.MissingCurrency;
                nextStep = this.latestCurrencies.ContainsKey(CurrencyKind.Vaal) ? "Use Vaal Orb" : "Missing Vaal Orb";
            }

            return new CraftingSlot(slot, name, rarity, explicitMods, state, nextStep, neededCurrency);
        }

        private void StartAutomation()
        {
            this.automationActive = true;
            this.automationPhase = AutomationPhase.PrepareStep;
            this.automationActionsSent = 0;
            this.vaalAttemptedSlotIndexes.Clear();
            this.forceCurrencyRescan = true;
            this.nextAutomationActionAt = DateTime.Now.AddSeconds(this.Settings.AutomationStartDelaySeconds);
            this.HideSettingsWindow();
            NativeMouse.FocusWindow(this.TryGetGameWindowHandle());
            this.automationStatus = $"Starting in {this.Settings.AutomationStartDelaySeconds:0.0}s. Focus the game window. Emergency stop: {this.Settings.AutomationAbortKey}.";
        }

        private void StopAutomation(string reason)
        {
            this.automationActive = false;
            this.automationPhase = AutomationPhase.Idle;
            this.nextAutomationActionAt = DateTime.MinValue;
            this.automationStatus = reason;
        }

        private void UpdateAutomation()
        {
            if (!this.automationActive)
            {
                return;
            }

            var now = DateTime.Now;
            if (now < this.nextAutomationActionAt)
            {
                var remaining = Math.Max(0, (this.nextAutomationActionAt - now).TotalSeconds);
                if (this.automationPhase == AutomationPhase.PrepareStep && this.automationActionsSent == 0)
                {
                    this.automationStatus = $"Starting in {remaining:0.0}s. Focus the game window. Emergency stop: {this.Settings.AutomationAbortKey}.";
                }

                return;
            }

            if (!this.CanRunAutomation(out var blockedReason))
            {
                this.automationStatus = blockedReason;
                this.nextAutomationActionAt = now.AddMilliseconds(500);
                return;
            }

            switch (this.automationPhase)
            {
                case AutomationPhase.PrepareStep:
                    if (!this.TryPrepareNextCraftAction(out var prepareReason))
                    {
                        this.StopAutomation(prepareReason);
                        return;
                    }

                    this.automationPhase = AutomationPhase.RightClickCurrency;
                    this.nextAutomationActionAt = now;
                    break;

                case AutomationPhase.RightClickCurrency:
                    NativeMouse.RightClick(this.pendingCurrencyCenter);
                    this.automationStatus = $"Selected {this.pendingCurrencyKind}.";
                    this.forceCurrencyRescan = true;
                    this.automationPhase = AutomationPhase.LeftClickWaystone;
                    this.nextAutomationActionAt = now.AddMilliseconds(this.Settings.AutomationClickDelayMs);
                    break;

                case AutomationPhase.LeftClickWaystone:
                    NativeMouse.LeftClick(this.pendingWaystoneCenter);
                    this.automationActionsSent++;
                    this.automationStatus = $"Applied {this.pendingCurrencyKind}. Waiting for item update...";
                    this.automationPhase = AutomationPhase.WaitForItemUpdate;
                    this.pendingItemUpdateDeadline = now.AddMilliseconds(this.Settings.AutomationItemUpdateTimeoutMs);
                    this.nextAutomationActionAt = now.AddMilliseconds(this.Settings.AutomationStepDelayMs);
                    break;

                case AutomationPhase.WaitForItemUpdate:
                    if (this.IsPendingItemUpdateObserved())
                    {
                        this.automationStatus = $"{this.pendingCurrencyKind} confirmed on slot {this.pendingWaystoneColumn},{this.pendingWaystoneRow}.";
                        this.automationPhase = AutomationPhase.PrepareStep;
                        this.nextAutomationActionAt = now.AddMilliseconds(75);
                    }
                    else if (now >= this.pendingItemUpdateDeadline)
                    {
                        if (this.pendingCurrencyKind == CurrencyKind.Vaal && this.pendingWaystoneSlotIndex >= 0)
                        {
                            this.vaalAttemptedSlotIndexes.Add(this.pendingWaystoneSlotIndex);
                            this.automationStatus = $"No Vaal result observed on slot {this.pendingWaystoneColumn},{this.pendingWaystoneRow}; skipping further Vaal attempts this run.";
                        }
                        else
                        {
                            this.automationStatus = $"No {this.pendingCurrencyKind} result observed on slot {this.pendingWaystoneColumn},{this.pendingWaystoneRow}; retrying.";
                        }

                        this.automationPhase = AutomationPhase.PrepareStep;
                        this.nextAutomationActionAt = now.AddMilliseconds(150);
                    }
                    else
                    {
                        this.automationStatus = $"Waiting for {this.pendingCurrencyKind} result on slot {this.pendingWaystoneColumn},{this.pendingWaystoneRow}...";
                        this.nextAutomationActionAt = now.AddMilliseconds(100);
                    }

                    break;
            }
        }

        private bool CanRunAutomation(out string reason)
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                reason = "Waiting: game is not in-game.";
                return false;
            }

            if (!Core.Process.Foreground)
            {
                reason = "Waiting: focus the Path of Exile window.";
                return false;
            }

            var gameUi = Core.States.InGameStateObject.GameUi;
            if (gameUi == null || gameUi.Address == IntPtr.Zero)
            {
                reason = "Waiting: game UI is not ready.";
                return false;
            }

            if (gameUi.ChatParent.IsChatActive)
            {
                reason = "Waiting: chat is active.";
                return false;
            }

            if (this.latestInventoryGridRoot == IntPtr.Zero)
            {
                reason = "Waiting: inventory grid is not visible.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private void HideSettingsWindow()
        {
            try
            {
                var settingsType = typeof(Core).Assembly.GetType("GameHelper.Settings.SettingsWindow");
                var visibleField = settingsType?.GetField("isSettingsWindowVisible", BindingFlags.Static | BindingFlags.NonPublic);
                visibleField?.SetValue(null, false);

                var menuOpenProperty = typeof(Core).GetProperty(nameof(Core.IsSettingsMenuOpen), BindingFlags.Static | BindingFlags.Public);
                menuOpenProperty?.SetValue(null, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WSCrafter] Failed to hide settings window: {ex.Message}");
            }
        }

        private IntPtr TryGetGameWindowHandle()
        {
            try
            {
                var infoProperty = Core.Process.GetType().GetProperty("Information", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (infoProperty?.GetValue(Core.Process) is System.Diagnostics.Process process)
                {
                    return process.MainWindowHandle;
                }
            }
            catch
            {
            }

            return IntPtr.Zero;
        }

        private bool TryPrepareNextCraftAction(out string reason)
        {
            var target = this.latestCraftingSlots
                .Where(slot => slot.NeededCurrency.HasValue)
                .OrderBy(slot => slot.Slot.Column)
                .ThenBy(slot => slot.Slot.Row)
                .FirstOrDefault();

            if (target == null)
            {
                reason = $"Complete. Sent {this.automationActionsSent} craft action(s).";
                return false;
            }

            var currencyKind = target.NeededCurrency!.Value;
            if (!this.latestCurrencies.TryGetValue(currencyKind, out var currencySlot))
            {
                reason = $"Stopped: {currencyKind} is not visible.";
                return false;
            }

            if (target.State == CraftingState.MissingCurrency)
            {
                reason = $"Stopped: {target.NextStep}.";
                return false;
            }

            this.pendingCurrencyKind = currencyKind;
            this.pendingCurrencyCenter = currencySlot.Center;
            this.pendingWaystoneCenter = target.Slot.Center;
            this.pendingWaystoneSlotIndex = target.Slot.Index;
            this.pendingWaystoneColumn = target.Slot.Column;
            this.pendingWaystoneRow = target.Slot.Row;
            this.pendingWaystoneRarity = target.Rarity;
            this.pendingWaystoneExplicitMods = target.ExplicitMods;
            this.automationStatus = $"{target.NextStep} on slot {target.Slot.Column},{target.Slot.Row}.";
            reason = string.Empty;
            return true;
        }

        private bool IsPendingItemUpdateObserved()
        {
            if (this.pendingWaystoneSlotIndex < 0)
            {
                return true;
            }

            var current = this.latestCraftingSlots.FirstOrDefault(slot => slot.Slot.Index == this.pendingWaystoneSlotIndex);
            if (current == null)
            {
                return false;
            }

            return this.pendingCurrencyKind switch
            {
                CurrencyKind.Alchemy => current.Rarity == Rarity.Rare,
                CurrencyKind.Exalted => current.ExplicitMods > this.pendingWaystoneExplicitMods ||
                                        current.ExplicitMods >= this.Settings.TargetExplicitMods,
                CurrencyKind.Vaal => this.IsCorrupted(current.Slot.Item),
                _ => true,
            };
        }

        private void DrawDebugWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(660f, 520f), ImGuiCond.FirstUseEver);
            ImGui.Begin("WSCrafter Debugger");

            ImGui.Text($"Game State: {Core.States.GameCurrentState}");
            ImGui.Text($"Handle ready: {this.handleObj != null}");
            ImGui.Text($"GameUi: 0x{this.latestGameUiAddress.ToInt64():X}");
            ImGui.Text($"LeftPanel: 0x{this.latestLeftPanelAddress.ToInt64():X} | Visible: {this.latestLeftPanelVisible} | Items scanned: {this.latestLeftPanelItemsScanned}");
            ImGui.Text($"RightPanel: 0x{this.latestRightPanelAddress.ToInt64():X} | Visible: {this.latestRightPanelVisible} | Items scanned: {this.latestRightPanelItemsScanned}");
            ImGui.Text($"InventoryGridRoot: 0x{this.latestInventoryGridRoot.ToInt64():X}");
            ImGui.Text($"Planner status: {this.latestStatus}");

            ImGui.SeparatorText("Target Currency Detection");
            this.DrawDebugCurrencyTarget(CurrencyKind.Alchemy, "Orb of Alchemy");
            this.DrawDebugCurrencyTarget(CurrencyKind.Exalted, "Exalted Orb");
            this.DrawDebugCurrencyTarget(CurrencyKind.Vaal, "Vaal Orb");

            ImGui.SeparatorText($"Selected Waystones ({this.latestCraftingSlots.Count})");
            this.DrawDebugWaystoneTable();

            ImGui.SeparatorText("Waystone Memory Scanner");
            ImGui.TextWrapped("Dump the currently visible selected waystones. Put a corrupted and clean waystone in selected slots, then compare their blocks in the output file.");
            if (ImGui.Button("Dump visible waystone memory scan"))
            {
                this.DumpVisibleWaystoneMemoryScan();
            }

            ImGui.TextWrapped(this.latestMemoryScanStatus);

            ImGui.SeparatorText($"Currency Candidates ({this.latestCurrencyCandidates.Count})");
            if (this.latestCurrencyCandidates.Count == 0)
            {
                ImGui.TextWrapped("No target or currency-like items were detected in the visible left/right panels. Open the currency tab and make sure the stash/inventory panels are visible.");
                ImGui.End();
                return;
            }

            if (ImGui.BeginTable("WSCrafterCurrencyDebugTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0f, 260f)))
            {
                ImGui.TableSetupColumn("Kind");
                ImGui.TableSetupColumn("Match");
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Path");
                ImGui.TableSetupColumn("Ptr");
                ImGui.TableSetupColumn("Pos");
                ImGui.TableSetupColumn("Size");
                ImGui.TableHeadersRow();

                foreach (var candidate in this.latestCurrencyCandidates.Take(100))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(candidate.Kind);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(candidate.IsTargetMatch ? new Vector4(0.35f, 1f, 0.35f, 1f) : new Vector4(1f, 0.75f, 0.25f, 1f), candidate.IsTargetMatch ? "yes" : "no");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(candidate.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(candidate.Path);
                    ImGui.TableNextColumn();
                    ImGui.Text($"0x{candidate.Slot.ItemPointer.ToInt64():X}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{candidate.Slot.Pos.X:0},{candidate.Slot.Pos.Y:0}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{candidate.Slot.Size.X:0}x{candidate.Slot.Size.Y:0}");
                }

                ImGui.EndTable();
            }

            if (this.latestCurrencyCandidates.Count > 100)
            {
                ImGui.Text($"Showing first 100 of {this.latestCurrencyCandidates.Count} candidates.");
            }

            ImGui.End();
        }

        private void DrawDebugWaystoneTable()
        {
            if (this.latestCraftingSlots.Count == 0)
            {
                ImGui.TextWrapped("No selected waystone slots were detected in the visible inventory grid.");
                return;
            }

            if (ImGui.BeginTable("WSCrafterWaystoneDebugTable", 10, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0f, 180f)))
            {
                ImGui.TableSetupColumn("Slot");
                ImGui.TableSetupColumn("Selected");
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Rarity");
                ImGui.TableSetupColumn("Explicit");
                ImGui.TableSetupColumn("Corrupted");
                ImGui.TableSetupColumn("Vaal tried");
                ImGui.TableSetupColumn("Next");
                ImGui.TableSetupColumn("Ptr");
                ImGui.TableSetupColumn("Corrupt evidence");
                ImGui.TableHeadersRow();

                foreach (var slot in this.latestCraftingSlots.Take(100))
                {
                    var selected = slot.Slot.Index >= 0 &&
                                   slot.Slot.Index < this.Settings.SelectedInventorySlots.Length &&
                                   this.Settings.SelectedInventorySlots[slot.Slot.Index];

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text($"{slot.Slot.Column},{slot.Slot.Row} ({slot.Slot.Index})");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(selected ? new Vector4(0.35f, 1f, 0.35f, 1f) : new Vector4(1f, 0.35f, 0.35f, 1f), selected ? "yes" : "no");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(slot.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(slot.Rarity.ToString());
                    ImGui.TableNextColumn();
                    ImGui.Text(slot.ExplicitMods.ToString());
                    ImGui.TableNextColumn();
                    var isCorrupted = this.IsCorrupted(slot.Slot.Item);
                    ImGui.TextColored(isCorrupted ? new Vector4(0.35f, 1f, 0.35f, 1f) : new Vector4(1f, 0.75f, 0.25f, 1f), isCorrupted ? "yes" : "no");
                    ImGui.TableNextColumn();
                    ImGui.Text(slot.Slot.Index >= 0 && this.vaalAttemptedSlotIndexes.Contains(slot.Slot.Index) ? "yes" : "no");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(slot.NextStep);
                    ImGui.TableNextColumn();
                    ImGui.Text($"0x{slot.Slot.ItemPointer.ToInt64():X}");
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(this.GetCorruptionEvidence(slot.Slot.Item));
                }

                ImGui.EndTable();
            }
        }

        private void DumpVisibleWaystoneMemoryScan()
        {
            var slots = this.latestCraftingSlots
                .OrderBy(slot => slot.Slot.Column)
                .ThenBy(slot => slot.Slot.Row)
                .ToList();

            if (slots.Count == 0)
            {
                this.latestMemoryScanStatus = "No visible selected waystones to scan.";
                return;
            }

            try
            {
                var dir = Path.Combine(this.DllDirectory, "config");
                Directory.CreateDirectory(dir);
                var outputPath = Path.Combine(dir, "waystone_corruption_scan.txt");

                var sb = new StringBuilder();
                sb.AppendLine($"=== WSCrafter Waystone Corruption Scan ===");
                sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"Waystones: {slots.Count}");
                sb.AppendLine("Tip: compare a known clean waystone block against a known corrupted waystone block.");
                sb.AppendLine();
                sb.AppendLine("Known corruption stat enum values checked by WSCrafter:");
                sb.AppendLine($"  map_is_corrupted = {(int)GameStats.map_is_corrupted}");
                sb.AppendLine($"  map_is_corrupted_waystone = {(int)GameStats.map_is_corrupted_waystone}");
                sb.AppendLine($"  map_corrupted_waystone_additional_mods_positive_ = {(int)GameStats.map_corrupted_waystone_additional_mods_positive_}");
                sb.AppendLine($"  map_corrupted_waystone_world_area_is_random_map = {(int)GameStats.map_corrupted_waystone_world_area_is_random_map}");
                sb.AppendLine();

                foreach (var slot in slots)
                {
                    this.AppendWaystoneMemoryScan(sb, slot);
                }

                File.WriteAllText(outputPath, sb.ToString());
                this.latestMemoryScanStatus = $"Wrote {slots.Count} waystone scan(s) to {outputPath}";
            }
            catch (Exception ex)
            {
                this.latestMemoryScanStatus = $"Memory scan failed: {ex.Message}";
            }
        }

        private void AppendWaystoneMemoryScan(StringBuilder sb, CraftingSlot craft)
        {
            var item = craft.Slot.Item;
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"Slot: column={craft.Slot.Column}, row={craft.Slot.Row}, index={craft.Slot.Index}");
            sb.AppendLine($"Entity: 0x{craft.Slot.ItemPointer.ToInt64():X}");
            sb.AppendLine($"Name: {craft.Name}");
            sb.AppendLine($"Path: {item.Path}");
            sb.AppendLine($"Planner: {craft.NextStep}");
            sb.AppendLine($"Rarity: {craft.Rarity}");
            sb.AppendLine($"ExplicitMods: {craft.ExplicitMods}");
            sb.AppendLine($"WSCrafter IsCorrupted: {this.IsCorrupted(item)}");
            sb.AppendLine($"Corruption evidence: {this.GetCorruptionEvidence(item)}");
            sb.AppendLine();

            if (item.TryGetComponent<Base>(out var baseComp, false))
            {
                sb.AppendLine("[Base]");
                sb.AppendLine($"  BaseItemName: {baseComp.BaseItemName}");
                sb.AppendLine($"  InternalName: {baseComp.InternalName}");
                sb.AppendLine();
            }

            if (item.TryGetComponent<Stats>(out var stats, false))
            {
                AppendStats(sb, "[StatsChangedByItems]", stats.StatsChangedByItems);
                AppendStats(sb, "[StatsChangedByBuffAndActions]", stats.StatsChangedByBuffAndActions);
            }

            if (item.TryGetComponent<ObjectMagicProperties>(out var magicProps, false))
            {
                sb.AppendLine("[ObjectMagicProperties]");
                sb.AppendLine($"  Rarity: {magicProps.Rarity}");
                AppendStats(sb, "  ModStats", magicProps.ModStats);
                AppendModNames(sb, "  ModNames", magicProps.ModNames);
                AppendMods(sb, "  Mods", magicProps.Mods);
            }

            if (item.TryGetComponent<Mods>(out var mods, false))
            {
                sb.AppendLine("[Mods]");
                sb.AppendLine($"  Rarity: {mods.Rarity}");
                AppendMods(sb, "  ImplicitMods", mods.ImplicitMods);
                AppendMods(sb, "  ExplicitMods", mods.ExplicitMods);
                AppendMods(sb, "  EnchantMods", mods.EnchantMods);
                AppendMods(sb, "  HellscapeMods", mods.HellscapeMods);
                this.AppendStatsFromModsOffset(sb, mods);
            }

            var components = GetComponentAddresses(item);
            sb.AppendLine("[Component Addresses]");
            if (components.Count == 0)
            {
                sb.AppendLine("  (none/reflection failed)");
            }
            else
            {
                foreach (var pair in components.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"  {pair.Key}: 0x{pair.Value.ToInt64():X}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("[Raw Component Dumps]");
            var interestingComponents = new[]
            {
                "Base",
                "Mods",
                "ObjectMagicProperties",
                "Stats",
                "Map",
                "LocalStats",
                "Quality",
                "Stack",
            };

            foreach (var componentName in interestingComponents)
            {
                if (components.TryGetValue(componentName, out var address) && address != IntPtr.Zero)
                {
                    this.AppendRawMemoryDump(sb, componentName, address, 0x100);
                    if (this.TryReadMemory<IntPtr>(address + 0x10, out var detailsPtr) && IsReasonablePointer(detailsPtr))
                    {
                        this.AppendRawMemoryDump(sb, $"{componentName}+0x10 pointer target", detailsPtr, 0x100);
                    }
                }
            }

            sb.AppendLine();
        }

        private void AppendStatsFromModsOffset(StringBuilder sb, Mods mods)
        {
            try
            {
                var data = this.ReadMemory<GameOffsets.Objects.Components.ModsOffsets>(mods.Address);
                var stats = this.ReadStdVector<GameOffsets.Objects.Components.StatArrayStruct>(data.Details0.StatsFromMods);
                sb.AppendLine("  StatsFromMods raw vector:");
                if (stats.Length == 0)
                {
                    sb.AppendLine("    (none)");
                    return;
                }

                foreach (var stat in stats.OrderBy(stat => stat.key).Take(200))
                {
                    var key = (GameStats)stat.key;
                    sb.AppendLine($"    {key} ({stat.key}) = {stat.value}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  StatsFromMods raw vector: failed ({ex.Message})");
            }
        }

        private void AppendRawMemoryDump(StringBuilder sb, string label, IntPtr address, int byteCount)
        {
            sb.AppendLine($"  [{label}] 0x{address.ToInt64():X} ({byteCount:X} bytes)");
            for (var offset = 0; offset < byteCount; offset += 8)
            {
                var ptr = address + offset;
                var ok64 = this.TryReadMemory<long>(ptr, out var val64);
                var ok32a = this.TryReadMemory<int>(ptr, out var val32a);
                var ok32b = this.TryReadMemory<int>(ptr + 4, out var val32b);
                if (!ok64 && !ok32a && !ok32b)
                {
                    sb.AppendLine($"    +0x{offset:X3}: unreadable");
                    continue;
                }

                var marker = CorruptionProbeMarker(val32a, val32b);
                sb.AppendLine($"    +0x{offset:X3}: qword=0x{val64:X16} int32=({val32a}, {val32b}){marker}");
            }
        }

        private static string CorruptionProbeMarker(int left, int right)
        {
            var hits = new List<string>();
            AddCorruptionProbeHit(hits, left);
            AddCorruptionProbeHit(hits, right);
            return hits.Count == 0 ? string.Empty : $"  <-- {string.Join(", ", hits)}";
        }

        private static void AddCorruptionProbeHit(List<string> hits, int value)
        {
            if (value == (int)GameStats.map_is_corrupted)
            {
                hits.Add("map_is_corrupted id");
            }
            else if (value == (int)GameStats.map_is_corrupted_waystone)
            {
                hits.Add("map_is_corrupted_waystone id");
            }
            else if (value == (int)GameStats.map_corrupted_waystone_additional_mods_positive_)
            {
                hits.Add("map_corrupted_waystone_additional_mods_positive_ id");
            }
            else if (value == (int)GameStats.map_corrupted_waystone_world_area_is_random_map)
            {
                hits.Add("map_corrupted_waystone_world_area_is_random_map id");
            }
        }

        private static void AppendStats(StringBuilder sb, string title, Dictionary<GameStats, int> stats)
        {
            sb.AppendLine(title);
            if (stats == null || stats.Count == 0)
            {
                sb.AppendLine("  (none)");
                return;
            }

            foreach (var pair in stats.Where(pair => pair.Value != 0).OrderBy(pair => (int)pair.Key).Take(200))
            {
                sb.AppendLine($"  {pair.Key} ({(int)pair.Key}) = {pair.Value}");
            }
        }

        private static void AppendModNames(StringBuilder sb, string title, HashSet<string> modNames)
        {
            sb.AppendLine(title);
            if (modNames == null || modNames.Count == 0)
            {
                sb.AppendLine("    (none)");
                return;
            }

            foreach (var modName in modNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(200))
            {
                sb.AppendLine($"    {modName}");
            }
        }

        private static void AppendMods(StringBuilder sb, string title, List<(string name, (float value0, float value1) values)> mods)
        {
            sb.AppendLine(title);
            if (mods == null || mods.Count == 0)
            {
                sb.AppendLine("    (none)");
                return;
            }

            foreach (var mod in mods.Take(200))
            {
                sb.AppendLine($"    {mod.name}: ({mod.values.value0}, {mod.values.value1})");
            }
        }

        private static Dictionary<string, IntPtr> GetComponentAddresses(Item item)
        {
            var field = typeof(Entity).GetField("componentAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(item) is System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> dict)
            {
                return dict.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        }

        private void DrawDebugCurrencyTarget(CurrencyKind kind, string label)
        {
            if (!this.latestCurrencies.TryGetValue(kind, out var slot))
            {
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"{label}: missing");
                return;
            }

            var name = GetItemNameOrFallback(slot.Item);
            ImGui.TextColored(new Vector4(0.35f, 1f, 0.35f, 1f),
                $"{label}: found | Name: {name} | Ptr: 0x{slot.ItemPointer.ToInt64():X} | Rect: {slot.Pos.X:0},{slot.Pos.Y:0} {slot.Size.X:0}x{slot.Size.Y:0}");
            ImGui.TextWrapped($"Path: {slot.Item.Path}");
        }

        private bool DrawSelectionControls()
        {
            var changed = false;

            if (ImGui.Button("Clear all"))
            {
                Array.Fill(this.Settings.SelectedInventorySlots, false);
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("Select all"))
            {
                Array.Fill(this.Settings.SelectedInventorySlots, true);
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("Left 5 columns"))
            {
                this.SelectColumns(0, 4);
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("Right 5 columns"))
            {
                this.SelectColumns(7, 11);
                changed = true;
            }

            return changed;
        }

        private bool DrawSelectionGrid(bool interactive)
        {
            var changed = false;
            var cellSize = new Vector2(25f, 25f);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(3f, 3f));

            for (var row = 0; row < InventoryRows; row++)
            {
                for (var col = 0; col < InventoryColumns; col++)
                {
                    var index = row * InventoryColumns + col;
                    var selected = this.Settings.SelectedInventorySlots[index];
                    var color = selected ? this.Settings.SelectedSlotColor : new Vector4(0.18f, 0.20f, 0.22f, 0.95f);
                    ImGui.PushStyleColor(ImGuiCol.Button, color);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, selected ? new Vector4(0.25f, 0.80f, 0.30f, 1f) : new Vector4(0.32f, 0.35f, 0.38f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, selected ? new Vector4(0.12f, 0.48f, 0.16f, 1f) : new Vector4(0.12f, 0.13f, 0.14f, 1f));

                    if (ImGui.Button($"##WSCrafter_slot_{index}", cellSize) && interactive)
                    {
                        this.Settings.SelectedInventorySlots[index] = !selected;
                        changed = true;
                    }

                    ImGui.PopStyleColor(3);

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip($"Column {col}, Row {row}");
                    }

                    if (col < InventoryColumns - 1)
                    {
                        ImGui.SameLine();
                    }
                }
            }

            ImGui.PopStyleVar();

            if (changed)
            {
                this.SaveSettings();
            }

            return changed;
        }

        private void DrawOverlayHighlights()
        {
            var fg = ImGui.GetForegroundDrawList();

            if (this.Settings.ShowCurrencyHighlights)
            {
                foreach (var (kind, slot) in this.latestCurrencies)
                {
                    this.DrawSlotBox(fg, slot.Pos, slot.Size, this.Settings.CurrencyColor, kind.ToString());
                }
            }

            if (!this.Settings.ShowInventoryHighlights)
            {
                return;
            }

            foreach (var slot in this.latestCraftingSlots)
            {
                var color = slot.State switch
                {
                    CraftingState.Ready => this.Settings.ReadyMapColor,
                    CraftingState.MissingCurrency => this.Settings.MissingMapColor,
                    _ => this.Settings.PendingCraftColor,
                };

                this.DrawSlotBox(fg, slot.Slot.Pos, slot.Slot.Size, color, slot.NextStep);
            }
        }

        private void DrawAutomationStatusOverlay()
        {
            if (!this.automationActive)
            {
                return;
            }

            var draw = ImGui.GetForegroundDrawList();
            var text = $"WSCrafter: {this.automationStatus}";
            var pos = new Vector2(24f, 120f);
            var textSize = ImGui.CalcTextSize(text);
            draw.AddRectFilled(pos - new Vector2(8f, 5f), pos + textSize + new Vector2(8f, 5f), 0xCC000000u, 4f);
            draw.AddText(pos, 0xFFFFFFFFu, text);
        }

        private void DrawSlotBox(ImDrawListPtr drawList, Vector2 pos, Vector2 size, Vector4 color, string label)
        {
            var u32 = ImGui.ColorConvertFloat4ToU32(color);
            drawList.AddRect(pos, pos + size, u32, 0f, ImDrawFlags.None, this.Settings.BorderThickness);

            var font = ImGui.GetFont();
            var fontSize = ImGui.GetFontSize();
            var labelSize = ImGui.CalcTextSize(label);
            var labelPos = pos + new Vector2(2f, Math.Max(0f, size.Y - fontSize - 2f));
            drawList.AddRectFilled(labelPos - new Vector2(2f, 1f), labelPos + new Vector2(labelSize.X + 4f, fontSize + 1f), 0xB0000000u, 3f);
            drawList.AddText(font, fontSize, labelPos, 0xFFFFFFFFu, label);
        }

        private List<SlotInfo> ScanDirectChildItemSlots(IntPtr gridRoot)
        {
            var results = new List<SlotInfo>();
            if (gridRoot == IntPtr.Zero)
            {
                return results;
            }

            if (!PluginUiElementReflection.TryGetAbsoluteRect(gridRoot, out var gridPos, out var gridSize) ||
                gridSize.X <= 0f || gridSize.Y <= 0f)
            {
                return results;
            }

            var cellSize = new Vector2(gridSize.X / InventoryColumns, gridSize.Y / InventoryRows);
            if (cellSize.X <= 0f || cellSize.Y <= 0f)
            {
                return results;
            }

            var rootOff = this.ReadMemory<UiElementBaseOffset>(gridRoot);
            var slots = this.ReadStdVector<IntPtr>(rootOff.ChildrensPtr);
            for (var i = 0; i < slots.Length; i++)
            {
                var slotAddr = slots[i];
                if (slotAddr == IntPtr.Zero || !this.IsElementVisible(slotAddr))
                {
                    continue;
                }

                if (!PluginUiElementReflection.TryGetAbsoluteRect(slotAddr, out var pos, out var size) || size.X <= 0f || size.Y <= 0f)
                {
                    continue;
                }

                var itemAddr = this.GetItemAddressFromElement(slotAddr);
                if (itemAddr == IntPtr.Zero)
                {
                    continue;
                }

                var item = ReadFreshItem(itemAddr);
                if (item == null)
                {
                    continue;
                }

                var slotIndex = TryGetInventorySlotIndexFromPosition(pos, size, gridPos, cellSize);
                if (slotIndex < 0)
                {
                    continue;
                }

                results.Add(new SlotInfo(slotAddr, itemAddr, pos, size, item, slotIndex));
            }

            return results
                .GroupBy(slot => slot.Index)
                .Select(group => group.OrderByDescending(slot => slot.Size.X * slot.Size.Y).First())
                .ToList();
        }

        private static int TryGetInventorySlotIndexFromPosition(Vector2 pos, Vector2 size, Vector2 gridPos, Vector2 cellSize)
        {
            var center = pos + (size * 0.5f);
            var relative = center - gridPos;
            var column = (int)MathF.Floor(relative.X / cellSize.X);
            var row = (int)MathF.Floor(relative.Y / cellSize.Y);

            if (column < 0 || column >= InventoryColumns || row < 0 || row >= InventoryRows)
            {
                return -1;
            }

            return row * InventoryColumns + column;
        }

        private List<SlotInfo> ScanItemSlots(IntPtr panelAddr)
        {
            var candidates = new List<SlotInfo>();
            if (panelAddr == IntPtr.Zero)
            {
                return candidates;
            }

            var queue = new Queue<(IntPtr Address, IntPtr Parent)>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue((panelAddr, IntPtr.Zero));

            while (queue.Count > 0 && visited.Count < 5000)
            {
                var (el, parent) = queue.Dequeue();
                if (el == IntPtr.Zero || !visited.Add(el))
                {
                    continue;
                }

                UiElementBaseOffset off;
                try
                {
                    off = this.ReadMemory<UiElementBaseOffset>(el);
                }
                catch
                {
                    continue;
                }

                if (!UiElementBaseFuncs.IsVisibleChecker(off.Flags))
                {
                    continue;
                }

                foreach (var child in this.ReadStdVector<IntPtr>(off.ChildrensPtr))
                {
                    queue.Enqueue((child, el));
                }

                var itemAddr = this.GetItemAddressFromElement(el);
                if (itemAddr == IntPtr.Zero)
                {
                    continue;
                }

                var item = ReadFreshItem(itemAddr);
                if (item == null || string.IsNullOrEmpty(item.Path) || !item.Path.StartsWith("Metadata/Items", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!PluginUiElementReflection.TryGetAbsoluteRect(el, out var pos, out var size))
                {
                    continue;
                }

                if (parent != IntPtr.Zero &&
                    PluginUiElementReflection.TryGetAbsoluteRect(parent, out var parentPos, out var parentSize) &&
                    parentSize.X >= 20f && parentSize.Y >= 20f &&
                    parentSize.X <= 256f && parentSize.Y <= 256f)
                {
                    pos = parentPos;
                    size = parentSize;
                }

                candidates.Add(new SlotInfo(el, itemAddr, pos, size, item, -1));
            }

            return candidates
                .GroupBy(x => x.ItemPointer)
                .Select(x => x.OrderByDescending(slot => slot.Size.X * slot.Size.Y).First())
                .ToList();
        }

        private IntPtr GetItemAddressFromElement(IntPtr elementAddr)
        {
            if (elementAddr == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            if (this.TryReadMemory<IntPtr>(elementAddr + KnownItemPointerOffset, out var knownOffsetCandidate) &&
                this.IsValidItemEntity(knownOffsetCandidate))
            {
                this.itemPointerOffsetByElement[elementAddr] = KnownItemPointerOffset;
                return knownOffsetCandidate;
            }

            if (this.itemPointerOffsetByElement.TryGetValue(elementAddr, out var cachedOffset))
            {
                if (this.TryReadMemory<IntPtr>(elementAddr + cachedOffset, out var cachedCandidate) &&
                    this.IsValidItemEntity(cachedCandidate))
                {
                    return cachedCandidate;
                }

                this.itemPointerOffsetByElement.Remove(elementAddr);
            }

            for (var offset = DefaultScanStartOffset; offset + 8 <= DefaultScanEndOffset; offset += 8)
            {
                if (this.TryReadMemory<IntPtr>(elementAddr + offset, out var candidate) && this.IsValidItemEntity(candidate))
                {
                    this.itemPointerOffsetByElement[elementAddr] = offset;
                    return candidate;
                }
            }

            return IntPtr.Zero;
        }

        private bool IsValidItemEntity(IntPtr address)
        {
            if (!IsReasonablePointer(address))
            {
                return false;
            }

            try
            {
                if (!this.TryReadMemory<IntPtr>(address + 0x08, out var detailsPtr) || !IsReasonablePointer(detailsPtr))
                {
                    return false;
                }

                if (!this.TryReadMemory<StdWString>(detailsPtr + 0x08, out var nativeContainer))
                {
                    return false;
                }

                var path = this.ReadStdWString(nativeContainer);
                return path.StartsWith("Metadata/Items", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private bool InitReflection()
        {
            try
            {
                var handleProp = Core.Process.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                this.handleObj = handleProp?.GetValue(Core.Process);
                if (this.handleObj == null)
                {
                    return false;
                }

                this.readStdWStringMethod = this.handleObj.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .First(m => m.Name == "ReadStdWString" && m.GetParameters().Length == 1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private T ReadMemory<T>(IntPtr address)
            where T : unmanaged
        {
            if (this.handleObj == null)
            {
                return default;
            }

            if (!this.readMemoryMethods.TryGetValue(typeof(T), out var method))
            {
                var genericMethod = this.handleObj.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .First(m => m.Name == "ReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 1);
                method = genericMethod.MakeGenericMethod(typeof(T));
                this.readMemoryMethods[typeof(T)] = method;
            }

            return (T)method.Invoke(this.handleObj, new object[] { address })!;
        }

        private bool TryReadMemory<T>(IntPtr address, out T result)
            where T : unmanaged
        {
            result = default;
            if (this.handleObj == null)
            {
                return false;
            }

            try
            {
                if (!this.tryReadMemoryMethods.TryGetValue(typeof(T), out var method))
                {
                    var genericMethod = this.handleObj.GetType()
                        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                        .First(m => m.Name == "TryReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 2);
                    method = genericMethod.MakeGenericMethod(typeof(T));
                    this.tryReadMemoryMethods[typeof(T)] = method;
                }

                var args = new object[] { address, default(T) };
                var success = (bool)method.Invoke(this.handleObj, args)!;
                result = (T)args[1]!;
                return success;
            }
            catch
            {
                result = default;
                return false;
            }
        }

        private T[] ReadStdVector<T>(StdVector nativeContainer)
            where T : unmanaged
        {
            if (this.handleObj == null)
            {
                return Array.Empty<T>();
            }

            if (!this.readStdVectorMethods.TryGetValue(typeof(T), out var method))
            {
                var genericMethod = this.handleObj.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .First(m => m.Name == "ReadStdVector" && m.IsGenericMethod);
                method = genericMethod.MakeGenericMethod(typeof(T));
                this.readStdVectorMethods[typeof(T)] = method;
            }

            return (T[])method.Invoke(this.handleObj, new object[] { nativeContainer })!;
        }

        private string ReadStdWString(StdWString nativeContainer)
        {
            try
            {
                return this.readStdWStringMethod?.Invoke(this.handleObj, new object[] { nativeContainer }) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private IntPtr ResolvePath(IntPtr root, int[] path)
        {
            var current = root;
            foreach (var index in path)
            {
                if (current == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                UiElementBaseOffset off;
                try
                {
                    off = this.ReadMemory<UiElementBaseOffset>(current);
                }
                catch
                {
                    return IntPtr.Zero;
                }

                var children = this.ReadStdVector<IntPtr>(off.ChildrensPtr);
                if (index < 0 || index >= children.Length)
                {
                    return IntPtr.Zero;
                }

                current = children[index];
            }

            return current;
        }

        private bool IsElementVisible(IntPtr element)
        {
            if (element == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var off = this.ReadMemory<UiElementBaseOffset>(element);
                return UiElementBaseFuncs.IsVisibleChecker(off.Flags);
            }
            catch
            {
                return false;
            }
        }

        private void SelectColumns(int firstColumn, int lastColumn)
        {
            Array.Fill(this.Settings.SelectedInventorySlots, false);
            for (var row = 0; row < InventoryRows; row++)
            {
                for (var col = firstColumn; col <= lastColumn; col++)
                {
                    this.Settings.SelectedInventorySlots[row * InventoryColumns + col] = true;
                }
            }
        }

        private void NormalizeSettings()
        {
            if (this.Settings.SelectedInventorySlots == null || this.Settings.SelectedInventorySlots.Length != InventorySlots)
            {
                var existing = this.Settings.SelectedInventorySlots ?? Array.Empty<bool>();
                this.Settings.SelectedInventorySlots = new bool[InventorySlots];
                for (var i = 0; i < this.Settings.SelectedInventorySlots.Length; i++)
                {
                    this.Settings.SelectedInventorySlots[i] = i < existing.Length ? existing[i] : true;
                }
            }

            this.Settings.TargetExplicitMods = Math.Clamp(this.Settings.TargetExplicitMods, 1, 6);
            this.Settings.AutomationStartDelaySeconds = Math.Clamp(this.Settings.AutomationStartDelaySeconds, 0.5f, 5f);
            this.Settings.AutomationClickDelayMs = Math.Clamp(this.Settings.AutomationClickDelayMs, 50, 500);
            this.Settings.AutomationStepDelayMs = Math.Clamp(this.Settings.AutomationStepDelayMs, 150, 1500);
            this.Settings.AutomationItemUpdateTimeoutMs = Math.Clamp(this.Settings.AutomationItemUpdateTimeoutMs, 500, 5000);
        }

        private static bool IsWaystoneLike(Item item)
        {
            var path = item.Path ?? string.Empty;
            if (path.Contains("Waystone", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("MapKey", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return TryGetItemName(item, out var name) && name.Contains("Waystone", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCraftingComplete(SlotInfo slot)
        {
            return this.IsCorrupted(slot.Item);
        }

        private bool IsCorrupted(Item item)
        {
            if (this.HasBaseCorruptedFlag(item))
            {
                return true;
            }

            if (item.TryGetComponent<Stats>(out var stats, false) &&
                (HasPositiveStat(stats.StatsChangedByItems, GameStats.map_is_corrupted) ||
                 HasPositiveStat(stats.StatsChangedByItems, GameStats.map_is_corrupted_waystone) ||
                 HasPositiveStat(stats.StatsChangedByBuffAndActions, GameStats.map_is_corrupted) ||
                 HasPositiveStat(stats.StatsChangedByBuffAndActions, GameStats.map_is_corrupted_waystone)))
            {
                return true;
            }

            if (item.TryGetComponent<ObjectMagicProperties>(out var magicProps, false) &&
                (HasPositiveStat(magicProps.ModStats, GameStats.map_is_corrupted) ||
                 HasPositiveStat(magicProps.ModStats, GameStats.map_is_corrupted_waystone) ||
                 HasPositiveStat(magicProps.ModStats, GameStats.map_corrupted_waystone_additional_mods_positive_) ||
                 HasPositiveStat(magicProps.ModStats, GameStats.map_corrupted_waystone_world_area_is_random_map) ||
                 magicProps.ModNames.Any(x => x.Contains("corrupt", StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }

            if (item.TryGetComponent<Mods>(out var mods, false))
            {
                return mods.ImplicitMods.Any(x => x.name.Contains("corrupted", StringComparison.OrdinalIgnoreCase)) ||
                       mods.ExplicitMods.Any(x => x.name.Contains("corrupted", StringComparison.OrdinalIgnoreCase)) ||
                       mods.EnchantMods.Any(x => x.name.Contains("corrupted", StringComparison.OrdinalIgnoreCase)) ||
                       mods.HellscapeMods.Any(x => x.name.Contains("corrupted", StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        private string GetCorruptionEvidence(Item item)
        {
            var evidence = new List<string>();

            if (TryReadBaseItemFlags(item, this.ReadMemory<long>, out var baseFlags))
            {
                evidence.Add($"Base+0xC0=0x{baseFlags:X16}");
            }

            if (item.TryGetComponent<Stats>(out var stats, false))
            {
                AddCorruptionStats(evidence, "Stats.Items", stats.StatsChangedByItems);
                AddCorruptionStats(evidence, "Stats.Buffs", stats.StatsChangedByBuffAndActions);
            }

            if (item.TryGetComponent<ObjectMagicProperties>(out var magicProps, false))
            {
                AddCorruptionStats(evidence, "OMP", magicProps.ModStats);
                evidence.AddRange(magicProps.ModNames
                    .Where(name => name.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                    .Select(name => $"OMP.Mod:{name}")
                    .Take(8));
            }

            if (item.TryGetComponent<Mods>(out var mods, false))
            {
                AddCorruptionModNames(evidence, "Implicit", mods.ImplicitMods);
                AddCorruptionModNames(evidence, "Explicit", mods.ExplicitMods);
                AddCorruptionModNames(evidence, "Enchant", mods.EnchantMods);
                AddCorruptionModNames(evidence, "Hellscape", mods.HellscapeMods);
            }

            return evidence.Count == 0 ? "(none)" : string.Join(" | ", evidence.Take(12));
        }

        private bool HasBaseCorruptedFlag(Item item)
        {
            return TryReadBaseItemFlags(item, this.ReadMemory<long>, out var flags) &&
                   (flags & 0x0100000000000000L) != 0;
        }

        private static bool TryReadBaseItemFlags(Item item, Func<IntPtr, long> readInt64, out long flags)
        {
            flags = 0;
            if (!item.TryGetComponent<Base>(out var baseComponent, false) || baseComponent.Address == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                flags = readInt64(baseComponent.Address + 0xC0);
                return true;
            }
            catch
            {
                flags = 0;
                return false;
            }
        }

        private static void AddCorruptionStats(List<string> evidence, string source, Dictionary<GameStats, int> stats)
        {
            if (stats == null)
            {
                return;
            }

            evidence.AddRange(stats
                .Where(pair => pair.Value != 0 && pair.Key.ToString().Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                .Select(pair => $"{source}.{pair.Key}={pair.Value}")
                .Take(8));
        }

        private static void AddCorruptionModNames(List<string> evidence, string source, List<(string name, (float value0, float value1) values)> mods)
        {
            evidence.AddRange(mods
                .Where(mod => mod.name.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                .Select(mod => $"{source}:{mod.name}")
                .Take(8));
        }

        private static bool HasPositiveStat(Dictionary<GameStats, int> stats, GameStats stat)
        {
            return stats != null && stats.TryGetValue(stat, out var value) && value > 0;
        }

        private static bool IsBelowRare(Rarity rarity)
        {
            return rarity == Rarity.Normal || rarity == Rarity.Magic;
        }

        private static bool TryGetCurrencyKind(Item item, out CurrencyKind kind)
        {
            kind = CurrencyKind.Alchemy;
            var path = item.Path ?? string.Empty;
            TryGetItemName(item, out var name);
            var combined = $"{name} {path}";

            if (combined.Contains("Orb of Alchemy", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("AlchemyOrb", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("CurrencyUpgradeToRare", StringComparison.OrdinalIgnoreCase))
            {
                kind = CurrencyKind.Alchemy;
                return true;
            }

            if (combined.Contains("Exalted Orb", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("ExaltedOrb", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("CurrencyAddModToRare", StringComparison.OrdinalIgnoreCase))
            {
                kind = CurrencyKind.Exalted;
                return true;
            }

            if (combined.Contains("Vaal Orb", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("VaalOrb", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("CurrencyCorrupt", StringComparison.OrdinalIgnoreCase))
            {
                kind = CurrencyKind.Vaal;
                return true;
            }

            return false;
        }

        private static bool TryGetItemName(Item item, out string name)
        {
            name = string.Empty;
            if (item.TryGetComponent<Base>(out var baseComponent))
            {
                name = baseComponent.BaseItemName?.Trim() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(name);
            }

            return false;
        }

        private static string GetItemNameOrFallback(Item item)
        {
            return TryGetItemName(item, out var name) ? name : "(no BaseItemName)";
        }

        private static bool LooksCurrencyLike(Item item, string name)
        {
            var path = item.Path ?? string.Empty;
            var combined = $"{name} {path}";
            return combined.Contains("Currency", StringComparison.OrdinalIgnoreCase) ||
                   combined.Contains("Orb", StringComparison.OrdinalIgnoreCase);
        }

        private static Item? ReadFreshItem(IntPtr itemAddress)
        {
            if (itemAddress == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Activator.CreateInstance(
                    typeof(Item),
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new object[] { itemAddress },
                    null) as Item;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsReasonablePointer(IntPtr ptr)
        {
            var value = (ulong)ptr.ToInt64();
            return ptr != IntPtr.Zero && value >= 0x10000 && value <= 0x7FFFFFFFFFFF;
        }

        private enum CurrencyKind
        {
            Alchemy,
            Exalted,
            Vaal,
        }

        private enum CraftingState
        {
            Ready,
            NeedsAlchemy,
            NeedsExalted,
            NeedsVaal,
            MissingCurrency,
        }

        private enum AutomationPhase
        {
            Idle,
            PrepareStep,
            RightClickCurrency,
            LeftClickWaystone,
            WaitForItemUpdate,
        }

        private sealed record CraftingSlot(SlotInfo Slot, string Name, Rarity Rarity, int ExplicitMods, CraftingState State, string NextStep, CurrencyKind? NeededCurrency);

        private sealed record CurrencyDebugInfo(string Kind, bool IsTargetMatch, string Name, string Path, SlotInfo Slot);

        private sealed record SlotInfo(IntPtr UiElement, IntPtr ItemPointer, Vector2 Pos, Vector2 Size, Item Item, int Index)
        {
            public int Row => this.Index >= 0 ? this.Index / InventoryColumns : -1;

            public int Column => this.Index >= 0 ? this.Index % InventoryColumns : -1;

            public Vector2 Center => this.Pos + (this.Size * 0.5f);
        }

        private static class NativeMouse
        {
            private const int MouseeventfLeftdown = 0x0002;
            private const int MouseeventfLeftup = 0x0004;
            private const int MouseeventfRightdown = 0x0008;
            private const int MouseeventfRightup = 0x0010;

            public static void LeftClick(Vector2 position)
            {
                Click(position, MouseeventfLeftdown, MouseeventfLeftup);
            }

            public static void RightClick(Vector2 position)
            {
                Click(position, MouseeventfRightdown, MouseeventfRightup);
            }

            public static bool IsKeyDown(int virtualKey)
            {
                return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
            }

            public static void FocusWindow(IntPtr windowHandle)
            {
                if (windowHandle != IntPtr.Zero)
                {
                    SetForegroundWindow(windowHandle);
                }
            }

            private static void Click(Vector2 position, int downFlag, int upFlag)
            {
                SetCursorPos((int)MathF.Round(position.X), (int)MathF.Round(position.Y));
                Thread.Sleep(20);
                mouse_event(downFlag, 0, 0, 0, 0);
                Thread.Sleep(35);
                mouse_event(upFlag, 0, 0, 0, 0);
            }

            [DllImport("user32.dll")]
            private static extern bool SetCursorPos(int x, int y);

            [DllImport("user32.dll")]
            private static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern short GetAsyncKeyState(int vKey);

            [DllImport("user32.dll")]
            private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);
        }
    }
}
