using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;

namespace ICE.Ui.Debug_Tabs.Debug_Tables
{
    /// <summary>
    /// What Gold Completion's cross-planet hop sees: per moon, what still needs a gold and when the next
    /// weather / timed window opens there. Same numbers the hop decision runs on.
    /// </summary>
    internal class Table_PlanetTravel
    {
        public static void Draw()
        {
            if (!PlayerHelper.IsInCosmicZone())
            {
                ImGui.Text("Not in a cosmic zone - mission gold flags and forecasts aren't readable from here.");
                return;
            }

            var current = Player.Territory.RowId;

            ImGui.Text($"Mode: {Mission_Settings.Mode} | Scheduler: {SchedulerMain.State}");
            ImGui.Text($"Cross-planet travel: {(C.Gold_CrossPlanetTravel ? "on" : "off")} | " +
                       $"Destination: {(Task_PlanetTravel.Destination == 0 ? "none" : CosmicMoonRegistry.GetDisplayName(Task_PlanetTravel.Destination))} | " +
                       $"Pending arrival: {(Task_PlanetTravel.PendingArrival == 0 ? "none" : CosmicMoonRegistry.GetDisplayName(Task_PlanetTravel.PendingArrival))}");

            ImGui.Text($"Eorzea hour: {CosmicMissionAvailability.CurrentEorzeaHour():N2}");
            ImGui.Text($"On this moon: {Task_PlanetTravel.MinutesHere():N1} min | rotate after: " +
                       $"{(C.Gold_TravelRotateMinutes > 0 ? $"{C.Gold_TravelRotateMinutes} min" : "off")} | " +
                       $"red alert brewing: {(Task_PlanetTravel.RedAlertHere() ? "yes" : "no")}");

            if (ImGui.Button("Run the hop check now"))
            {
                var started = Task_PlanetTravel.TryStartTravel("[Debug]");
                IceLogging.Info(started
                    ? "Planet hop started."
                    : "No hop: staying on this moon.", "[Planet Travel]");
            }

            ImGui.Separator();
            ImGui.Text($"Cosmoliner window ({PlanetSelectHandler.AddonName}): " +
                       $"{(PlanetSelectHandler.IsOpen() ? "open" : "closed")} | learned buttons - " +
                       $"left \"{C.Gold_PlanetSelect_PrevButton}\", right \"{C.Gold_PlanetSelect_NextButton}\"");

            if (ImGui.Button("Travel NPC is here"))
            {
                // For a hub where he stands outside the object table's reach and the game data doesn't say
                // where: park next to him (or target him) and press this once.
                var target = Svc.Targets.Target;
                var name = target?.Name.TextValue is { Length: > 0 } targetName
                    ? targetName
                    : C.Gold_TravelNpcNames.FirstOrDefault() ?? "Cruisingway";

                Task_PlanetTravel.RememberTravelNpc(
                    current,
                    target?.DataId ?? 1,
                    name,
                    target?.Position ?? Player.Position,
                    "the debug window");
            }
            ImGuiEx.HelpMarker("Records where this moon's travel NPC stands, so a hop can path to him before he loads in.\n" +
                "Target him for the exact spot, or just stand next to him.");

            ImGui.SameLine();
            if (ImGui.Button("Dump the destination window"))
                IceLogging.Info(PlanetSelectHandler.Dump(), "[Planet Select]");

            ImGui.SameLine();
            if (ImGui.Button("Forget learned buttons"))
            {
                PlanetSelectHandler.ResetLearning();
                C.Gold_PlanetSelect_PrevButton = string.Empty;
                C.Gold_PlanetSelect_NextButton = string.Empty;
                C.Save();
            }

            ImGui.Separator();

            if (!ImGui.BeginTable("PlanetTravelStatus", 8, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                return;

            ImGui.TableSetupColumn("Moon");
            ImGui.TableSetupColumn("Left to gold");
            ImGui.TableSetupColumn("Anytime");
            ImGui.TableSetupColumn("Weather/Timed");
            ImGui.TableSetupColumn("Red alert/Seq");
            ImGui.TableSetupColumn("Open now");
            ImGui.TableSetupColumn("Next up");
            ImGui.TableSetupColumn("ETA (min)");
            ImGui.TableHeadersRow();

            foreach (var moon in CosmicMoonRegistry.All)
            {
                var isCurrent = moon.TerritoryId == current;
                var status = Task_PlanetTravel.Snapshot(moon);
                var eta = Task_PlanetTravel.SnapshotEta(status, isCurrent);

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.Text(isCurrent ? $"{moon.DisplayName} (here)" : moon.DisplayName);

                ImGui.TableNextColumn();
                ImGui.Text($"{status.Remaining}");

                ImGui.TableNextColumn();
                ImGui.Text($"{status.RemainingAnytime}");

                ImGui.TableNextColumn();
                ImGui.Text($"{status.RemainingScheduled}");

                ImGui.TableNextColumn();
                ImGui.Text($"{status.RemainingUnpredictable}");

                ImGui.TableNextColumn();
                ImGui.Text(status.OpenMissionId == 0
                    ? "-"
                    : $"{status.Describe(status.OpenMissionId)} ({status.OpenMinutesLeft:N0}m left)");

                ImGui.TableNextColumn();
                ImGui.Text(status.NextMissionId == 0
                    ? "-"
                    : $"{status.Describe(status.NextMissionId)} (in {status.NextMinutes:N0}m)");

                ImGui.TableNextColumn();
                ImGui.Text(double.IsPositiveInfinity(eta) || eta >= double.MaxValue / 2 ? "-" : $"{eta:N0}");
            }

            ImGui.EndTable();
        }
    }
}
