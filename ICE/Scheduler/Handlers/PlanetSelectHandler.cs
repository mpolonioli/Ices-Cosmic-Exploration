using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using System.Text;

namespace ICE.Scheduler.Handlers
{
    /// <summary>
    /// Drives the cosmoliner's "Select Destination" window (<c>WKSPlanetSelect</c>): a carousel with the
    /// focused planet in the middle, a chevron either side, and a Blast Off button underneath. The planets
    /// sit in registry order (Sinus Ardorum → Phaenna → Oizys → Auxesia), so the drive is
    /// "chevron towards the destination until the label matches, then confirm".
    /// <para>
    /// The window's node ids aren't published anywhere, so the buttons are found by shape - the two
    /// chevrons are the pair of unlabelled buttons sitting at the same height, Blast Off is the labelled
    /// one - and then <b>proven</b> by watching the label change after a click. Whichever chevron actually
    /// moved the carousel is remembered in config, so later hops click straight through. A button that does
    /// nothing (or closes the window) is dropped and never tried again this session, and nothing labelled is
    /// ever clicked until the focused planet already is the destination.
    /// </para>
    /// <para>
    /// Buttons live several components deep in this window, and node ids are only unique within their own
    /// component - so a button is addressed by its <em>path</em> (node list indices from the addon down),
    /// which is what gets cached.
    /// </para>
    /// </summary>
    internal static unsafe class PlanetSelectHandler
    {
        internal const string AddonName = "WKSPlanetSelect";

        private const string Tag = "[Planet Select]";

        /// <summary>How long a chevron click gets to slide the carousel before it's called a dud.</summary>
        private const long ClickSettleMs = 1500;

        private const int MaxDepth = 8;

        internal enum DriveResult
        {
            /// <summary>The window isn't up (or isn't ready yet).</summary>
            NotOpen,
            /// <summary>Mid-drive: still sliding to the destination.</summary>
            Working,
            /// <summary>Blast Off pressed - the yes/no confirmation is next.</summary>
            Confirmed,
            /// <summary>The window can't be driven; the player has to pick the planet.</summary>
            Stuck,
        }

        private static readonly HashSet<string> BadButtons = new();

        private static string _pendingClickPath = string.Empty;
        private static uint _focusBeforeClick;
        private static long _clickStamp;

        internal static void ResetLearning()
        {
            BadButtons.Clear();
            _pendingClickPath = string.Empty;
            _focusBeforeClick = 0;
            _clickStamp = 0;
        }

        internal static bool IsOpen() => GetAddon() != null;

        /// <summary>Shuts the window when a hop is called off, so it isn't left sitting over the mission board.</summary>
        internal static void CloseIfOpen()
        {
            var addon = GetAddon();
            if (addon == null)
                return;

            IceLogging.Info("Closing the destination window.", Tag);
            addon->Close(true);
        }

        internal static DriveResult Drive(uint destinationTerritory)
        {
            var addon = GetAddon();
            if (addon == null)
                return DriveResult.NotOpen;

            if (!CosmicMoonRegistry.TryGetMoon(destinationTerritory, out var destination))
                return DriveResult.Stuck;

            var focus = ReadFocusedMoon(addon);

            // A click is in flight: either it moved the carousel (which tells us what that button does) or
            // it did nothing at all (which tells us to stop pressing it).
            if (_pendingClickPath.Length > 0)
            {
                if (focus != null && focus.TerritoryId != _focusBeforeClick)
                {
                    Remember(_pendingClickPath, focus.ExpeditionTabIndex > IndexOf(_focusBeforeClick));
                    _pendingClickPath = string.Empty;
                }
                else if (Environment.TickCount64 - _clickStamp > ClickSettleMs)
                {
                    IceLogging.Info($"Button {_pendingClickPath} didn't move the carousel - not using it again.", Tag);
                    Forget(_pendingClickPath);
                    _pendingClickPath = string.Empty;
                }
                else
                {
                    return DriveResult.Working;
                }
            }

            if (focus == null)
            {
                if (EzThrottler.Throttle("Planet select: no label", 3000))
                    IceLogging.Verbose("Waiting on the destination window to name the planet it's showing.", Tag);
                return DriveResult.Working;
            }

            var buttons = CollectButtons(addon);

            if (focus.TerritoryId == destinationTerritory)
            {
                var confirm = FindConfirmButton(buttons);
                if (confirm == null)
                {
                    ReportUndrivable(addon, $"no Blast Off button found while {destination.DisplayName} is in focus");
                    return DriveResult.Stuck;
                }

                if (EzThrottler.Throttle("Planet select: blast off", 1000))
                {
                    IceLogging.Info($"{destination.DisplayName} is in focus - pressing Blast Off ({confirm.Value.Path}).", Tag);
                    Click(addon, confirm.Value);
                }

                return DriveResult.Confirmed;
            }

            var forward = destination.ExpeditionTabIndex > focus.ExpeditionTabIndex;
            var chevron = PickChevron(addon, buttons, forward);
            if (chevron == null)
            {
                ReportUndrivable(addon, $"no usable chevron to get from {focus.DisplayName} to {destination.DisplayName}");
                return DriveResult.Stuck;
            }

            if (!EzThrottler.Throttle("Planet select: chevron", 400))
                return DriveResult.Working;

            IceLogging.Verbose($"{focus.DisplayName} in focus, heading {(forward ? "right" : "left")} " +
                $"towards {destination.DisplayName} ({chevron.Value.Path}).", Tag);

            _focusBeforeClick = focus.TerritoryId;
            _pendingClickPath = chevron.Value.Path;
            _clickStamp = Environment.TickCount64;
            Click(addon, chevron.Value);

            return DriveResult.Working;
        }

        // - - - Reading the window - - - //

        private static AtkUnitBase* GetAddon()
        {
            var ptr = Svc.GameGui.GetAddonByName(AddonName, 1);
            if (ptr.Address == nint.Zero)
                return null;

            var addon = (AtkUnitBase*)ptr.Address;
            if (!addon->IsVisible || !addon->IsReady || addon->UldManager.NodeList == null)
                return null;

            return addon;
        }

        /// <summary>The moon whose name the window is currently showing, or null while it's between planets.</summary>
        private static CosmicMoonDefinition? ReadFocusedMoon(AtkUnitBase* addon)
        {
            CosmicMoonDefinition? found = null;

            foreach (var text in CollectText(addon))
            {
                var trimmed = text.Trim();
                if (trimmed.Length == 0)
                    continue;

                foreach (var moon in CosmicMoonRegistry.All)
                {
                    if (!string.Equals(trimmed, moon.DisplayName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Two different planets named at once means we're reading the wrong thing - bail out
                    // rather than steering off a guess. (The blurb mentions its planet, but never on its own.)
                    if (found != null && found.TerritoryId != moon.TerritoryId)
                        return null;

                    found = moon;
                }
            }

            return found;
        }

        private static List<string> CollectText(AtkUnitBase* addon)
        {
            var texts = new List<string>();
            CollectText(&addon->UldManager, texts, 0);
            return texts;
        }

        private static void CollectText(AtkUldManager* uld, List<string> texts, int depth)
        {
            if (uld == null || uld->NodeList == null || depth > MaxDepth)
                return;

            for (var i = 0; i < uld->NodeListCount; i++)
            {
                var node = uld->NodeList[i];
                if (node == null || !node->IsVisible())
                    continue;

                if (node->Type == NodeType.Text)
                {
                    var text = ((AtkTextNode*)node)->NodeText.GetText();
                    if (!string.IsNullOrWhiteSpace(text))
                        texts.Add(text);
                    continue;
                }

                var component = GetComponent(node);
                if (component != null)
                    CollectText(&component->UldManager, texts, depth + 1);
            }
        }

        /// <summary>
        /// A button in the window: <paramref name="Path"/> is its node list index chain from the addon down,
        /// which stays the same between sessions, while <paramref name="Address"/> is only good for this frame.
        /// Positions are the button's centre in screen pixels.
        /// </summary>
        private readonly record struct ButtonInfo(string Path, nint Address, float CenterX, float CenterY, string Text);

        private static List<ButtonInfo> CollectButtons(AtkUnitBase* addon)
        {
            var buttons = new List<ButtonInfo>();
            CollectButtons(&addon->UldManager, string.Empty, buttons, Scale(addon), 0);
            return buttons;
        }

        private static void CollectButtons(AtkUldManager* uld, string path, List<ButtonInfo> buttons, float scale, int depth)
        {
            if (uld == null || uld->NodeList == null || depth > MaxDepth)
                return;

            for (var i = 0; i < uld->NodeListCount; i++)
            {
                var node = uld->NodeList[i];
                if (node == null || !node->IsVisible())
                    continue;

                var component = GetComponent(node);
                if (component == null)
                    continue;

                var childPath = path.Length == 0 ? i.ToString() : $"{path}/{i}";
                var componentType = component->GetComponentType();

                // Window chrome (the close button) and scrollbar arrows are buttons too, and they are exactly
                // the ones that must never be mistaken for a chevron - so their subtrees are skipped whole.
                if (componentType is ComponentType.Window or ComponentType.ScrollBar)
                    continue;

                if (componentType == ComponentType.Button)
                {
                    var button = (AtkComponentButton*)component;
                    if (button->IsEnabled)
                    {
                        var texts = new List<string>();
                        CollectText(&component->UldManager, texts, 0);

                        buttons.Add(new ButtonInfo(
                            childPath,
                            (nint)button,
                            node->ScreenX + (node->Width * scale / 2f),
                            node->ScreenY + (node->Height * scale / 2f),
                            string.Join(" ", texts).Trim()));
                    }
                }

                CollectButtons(&component->UldManager, childPath, buttons, scale, depth + 1);
            }
        }

        private static float Scale(AtkUnitBase* addon) => addon->Scale > 0 ? addon->Scale : 1f;

        /// <summary>Screen-space centre of the window, which the carousel is built around.</summary>
        private static Vector2 WindowCenter(AtkUnitBase* addon)
        {
            var root = addon->RootNode;
            if (root == null)
                return Vector2.Zero;

            var scale = Scale(addon);
            return new Vector2(
                root->ScreenX + (root->Width * scale / 2f),
                root->ScreenY + (root->Height * scale / 2f));
        }

        private static AtkComponentBase* GetComponent(AtkResNode* node)
        {
            if ((int)node->Type < 1000)
                return null;

            return ((AtkComponentNode*)node)->Component;
        }

        // - - - Picking the buttons - - - //

        /// <summary>
        /// The chevrons are the unlabelled buttons flanking the planet: the one left of the window centre
        /// walks the carousel back, the one right of it walks forward. The game hides whichever chevron has
        /// nothing left to scroll to, and hidden nodes never make the list, so only usable ones are picked.
        /// </summary>
        private static ButtonInfo? PickChevron(AtkUnitBase* addon, List<ButtonInfo> buttons, bool forward)
        {
            var learned = forward ? C.Gold_PlanetSelect_NextButton : C.Gold_PlanetSelect_PrevButton;
            if (learned.Length > 0 && !BadButtons.Contains(learned))
            {
                var match = buttons.FirstOrDefault(x => x.Path == learned);
                if (match.Path == learned)
                    return match;
            }

            var center = WindowCenter(addon);
            var opposite = forward ? C.Gold_PlanetSelect_PrevButton : C.Gold_PlanetSelect_NextButton;

            var candidates = buttons
                .Where(x => x.Text.Length == 0)
                .Where(x => !BadButtons.Contains(x.Path))
                .Where(x => x.Path != opposite)
                .Where(x => forward ? x.CenterX > center.X : x.CenterX < center.X)
                // The chevrons sit level with the planet, halfway down the window.
                .OrderBy(x => Math.Abs(x.CenterY - center.Y))
                .ToList();

            return candidates.Count == 0 ? null : candidates[0];
        }

        /// <summary>
        /// Blast Off, matched on its label. The window carries a second, hidden copy of the button (the
        /// layout used when "Specify Instance" is offered), so position alone would be a coin flip - and
        /// picking wrong there means opening the instance picker instead of leaving.
        /// </summary>
        private static ButtonInfo? FindConfirmButton(List<ButtonInfo> buttons)
        {
            var labelled = buttons
                .Where(x => x.Text.Length > 0)
                .Where(x => !BadButtons.Contains(x.Path))
                .ToList();

            var byKeyword = labelled
                .Where(x => C.Gold_PlanetSelect_ConfirmKeywords.Any(keyword =>
                    !string.IsNullOrWhiteSpace(keyword)
                    && x.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (byKeyword.Count > 0)
                return byKeyword[0];

            // No wording match (a client ICE hasn't been taught): only safe when there's nothing to confuse
            // it with.
            return labelled.Count == 1 ? labelled[0] : null;
        }

        private static void Click(AtkUnitBase* addon, ButtonInfo button)
        {
            if (button.Address == nint.Zero)
                return;

            ((AtkComponentButton*)button.Address)->ClickAddonButton(addon);
        }

        private static void Remember(string path, bool movedForward)
        {
            if (movedForward)
            {
                if (C.Gold_PlanetSelect_NextButton == path)
                    return;
                C.Gold_PlanetSelect_NextButton = path;
            }
            else
            {
                if (C.Gold_PlanetSelect_PrevButton == path)
                    return;
                C.Gold_PlanetSelect_PrevButton = path;
            }

            C.Save();
            IceLogging.Info($"Button {path} moves the planet carousel {(movedForward ? "right" : "left")}.", Tag);
        }

        private static void Forget(string path)
        {
            BadButtons.Add(path);

            if (C.Gold_PlanetSelect_NextButton == path)
                C.Gold_PlanetSelect_NextButton = string.Empty;
            if (C.Gold_PlanetSelect_PrevButton == path)
                C.Gold_PlanetSelect_PrevButton = string.Empty;
            C.Save();
        }

        private static void ReportUndrivable(AtkUnitBase* addon, string why)
        {
            if (!EzThrottler.Throttle("Planet select: undrivable", 10_000))
                return;

            IceLogging.Error($"Can't drive the destination window - {why}.\n{Dump(addon)}", Tag);
        }

        // - - - Diagnostics - - - //

        /// <summary>The window's node tree as ICE sees it - the thing to paste in a bug report.</summary>
        internal static string Dump()
        {
            var addon = GetAddon();
            return addon == null ? $"{AddonName} isn't open." : Dump(addon);
        }

        private static string Dump(AtkUnitBase* addon)
        {
            var report = new StringBuilder();
            report.AppendLine($"{AddonName} buttons:");

            var center = WindowCenter(addon);
            report.AppendLine($"  (window centre {center.X:N0}, {center.Y:N0})");

            foreach (var button in CollectButtons(addon))
                report.AppendLine($"  [{button.Path}] centre ({button.CenterX:N0}, {button.CenterY:N0}) text: \"{button.Text}\"");

            report.AppendLine("Node tree:");
            DumpNodes(&addon->UldManager, string.Empty, report, 0);

            return report.ToString();
        }

        private static void DumpNodes(AtkUldManager* uld, string path, StringBuilder report, int depth)
        {
            if (uld == null || uld->NodeList == null || depth > MaxDepth)
                return;

            for (var i = 0; i < uld->NodeListCount; i++)
            {
                var node = uld->NodeList[i];
                if (node == null)
                    continue;

                var childPath = path.Length == 0 ? i.ToString() : $"{path}/{i}";
                var component = GetComponent(node);
                var kind = component != null ? $"{component->GetComponentType()}" : $"{node->Type}";
                if (component != null && component->GetComponentType() == ComponentType.Button)
                    kind += ((AtkComponentButton*)component)->IsEnabled ? " (enabled)" : " (disabled)";
                var text = node->Type == NodeType.Text ? ((AtkTextNode*)node)->NodeText.GetText() : string.Empty;

                report.AppendLine($"  [{childPath}] id {node->NodeId} {kind} " +
                    $"({node->ScreenX:N0}, {node->ScreenY:N0}) {node->Width}x{node->Height} " +
                    $"{(node->IsVisible() ? "visible" : "hidden")}{(text.Length > 0 ? $" text: \"{Shorten(text)}\"" : string.Empty)}");

                if (component != null)
                    DumpNodes(&component->UldManager, childPath, report, depth + 1);
            }
        }

        private static string Shorten(string text) =>
            text.Length <= 40 ? text : text[..40] + "...";

        private static int IndexOf(uint territoryId) =>
            CosmicMoonRegistry.TryGetMoon(territoryId, out var moon) ? moon.ExpeditionTabIndex : -1;
    }
}
