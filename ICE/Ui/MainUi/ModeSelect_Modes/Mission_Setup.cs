using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ICE.Ui.MainUi.ModeSelect_Modes.CosmicTable;
using ICE.Ui.MainUi.Settings;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.ImGuiTools;
using System.Collections.Generic;

namespace ICE.Ui.MainUi.ModeSelect_Modes
{
    internal class Mission_Setup
    {
        private static readonly Dictionary<string, uint> BattleJobs = new()
        {
            // Tanks
            { "Paladin", 19 },
            { "Warrior", 21 },
            { "Dark Knight", 32 },
            { "Gunbreaker", 37 },
    
            // Healers
            { "White Mage", 24 },
            { "Scholar", 28 },
            { "Astrologian", 33 },
            { "Sage", 40 },
    
            // Melee DPS
            { "Monk", 20 },
            { "Dragoon", 22 },
            { "Ninja", 30 },
            { "Samurai", 34 },
            { "Reaper", 39 },
            { "Viper", 41 },
    
            // Physical Ranged DPS
            { "Bard", 23 },
            { "Machinist", 31 },
            { "Dancer", 38 },
    
            // Magical Ranged DPS
            { "Black Mage", 25 },
            { "Summoner", 27 },
            { "Red Mage", 35 },
            { "Pictomancer", 42 }
        };

        public static CosmicTables.Mission_Table? MissionTable;
        private static List<CosmicHelper.MissionInfo> TableItems = [];
        private static int ItemCount = 0;
        private static string newListName = string.Empty;

        public static void Draw()
        {
            using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 10).Push(ImGuiStyleVar.ChildBorderSize, 1);

            // Header at the top
            float scale = ImGuiHelpers.GlobalScale;

            using (var headerChild = ImRaii.Child("##modeSelect_StandardHeader", new Vector2(0, 45 * scale), true, ImGuiWindowFlags.NoScrollbar))
            {
                if (!headerChild.Success) return;

                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10 * scale);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5 * scale);

                string modeType = string.Empty;
                FontAwesomeIcon modeIcon = FontAwesomeIcon.List;

                bool standard = C.SelectedMode == ModeSelect.Standard;
                bool relicMode = C.SelectedMode == ModeSelect.RelicMode;
                bool xpLeveling = C.SelectedMode == ModeSelect.LevelMode;
                bool goldMode = C.SelectedMode == ModeSelect.MissionGoldMode;
                bool agendaMode = C.SelectedMode == ModeSelect.AgendaMode;


                if (standard)
                    modeType = "Standard";
                else if (relicMode)
                {
                    modeType = "Relic Grind";
                    modeIcon = FontAwesomeIcon.ArrowUpRightDots;
                }
                else if (xpLeveling)
                {
                    modeType = "Leveling Grind";
                    modeIcon = FontAwesomeIcon.Leaf;
                }
                else if (goldMode)
                {
                    modeType = "Gold Completion Grind";
                    modeIcon = FontAwesomeIcon.Trophy;
                }
                else if (agendaMode)
                {
                    modeType = "Cosmic Agenda";
                    modeIcon = FontAwesomeIcon.ClipboardList;
                }

                ImGuiEx.IconWithText(modeIcon, $"{modeType} Mode");

                ImGui.SameLine(0, 10 * scale);

                // Adjust the Y position to center the button vertically with the text
                float textHeight = ImGui.GetTextLineHeight();
                float buttonHeight = ImGui.GetFrameHeight();
                float yOffset = (textHeight - buttonHeight) / 2f;
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);

                if (ImGuiEx.IconButtonWithText(FontAwesomeIcon.Play, "Mode Selection"))
                {
                    ImGui.OpenPopup("Mode Select | Select Mode Window");
                }
                if (ImGui.BeginPopup("Mode Select | Select Mode Window"))
                {
                    MainWindow.ModeSelection();

                    ImGui.EndPopup();
                }

                uint currentJobId = (uint)Player.Job;
                bool usingSupportedJob = CosmicHelper.CrafterJobList.Contains(currentJobId) || CosmicHelper.GatheringJobList.Contains(currentJobId);

                bool AnyStop = C.StopOnceHitCosmicScore
                             | C.StopWhenLevel
                            || C.StopOnceHitCosmoCredits
                            || C.StopOnceHitLunarCredits
                            || C.StopOnceRelicFinished
                            || C.StopOnceStandardMissionsGolded;
                if (AnyStop)
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                    ImGuiEx.Icon(FontAwesomeIcon.ExclamationTriangle);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();

                        ImGui.Text("It appears that you have on of the following enabled");
                        if (C.StopOnceHitCosmicScore)
                            ImGui.BulletText($"Stop at Cosmic Score [{C.CosmicScoreCap:N0}]");
                        if (C.StopWhenLevel)
                            ImGui.BulletText($"Stop When Level [{C.TargetLevel:N0}]");
                        if (C.StopOnceHitCosmoCredits)
                            ImGui.BulletText($"Stop once cosmo credit hit [{C.CosmoCreditsCap:N0}]");
                        if (C.StopOnceHitLunarCredits)
                            ImGui.BulletText($"Stop once planetary credit hit [{C.LunarCreditsCap:N0}]");
                        if (C.StopOnceRelicFinished)
                            ImGui.BulletText($"Stop once relic completed");
                        if (C.StopOnceStandardMissionsGolded)
                            ImGui.BulletText("Stop when all standard missions are golded");

                        ImGui.Text("So if you stop and you're unsure why... this might be why");

                        ImGui.EndTooltip();
                    }
                }

                ImGui.SameLine(0, 10 * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);

                bool unsupportedArtisan = false; // xpLeveling && CosmicHelper.CrafterJobList.Contains((uint)Player.Job);
                bool unsupportedMoon = xpLeveling 
                    && CosmicMoonRegistry.TryGetMoon(Player.Territory.RowId, out var currentMoon)
                    && !CosmicMoonRegistry.HasLevelingContent(currentMoon);

                // Leveling on a hub requires QuickLevelList entries; gathering still needs route YAML per territory
                using (ImRaii.Disabled(SchedulerMain.State != IceState.Idle || !usingSupportedJob || unsupportedMoon))
                {
                    if (ImGui.Button("Start", new Vector2(150 * scale, 0)))
                    {
                        SchedulerMain.EnablePlugin();
                    }
                }

                if (unsupportedArtisan)
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                    ImGuiEx.Icon(EColor.Red, FontAwesomeIcon.ExclamationTriangle);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Hey! You need to update artisan to use this mode, please update to at minimum:");
                        ImGui.Text("4.0.4.29");
                        ImGui.EndTooltip();
                    }
                }
                else if (unsupportedMoon && CosmicMoonRegistry.TryGetMoon(Player.Territory.RowId, out var unsupportedHub))
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                    ImGuiEx.Icon(EColor.Red, FontAwesomeIcon.ExclamationTriangle);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text($"Hey! {unsupportedHub.DisplayName} is not supported for leveling yet.");
                        var missing = new List<string>();
                        if (!CosmicMoonRegistry.HasLevelingContent(unsupportedHub))
                            missing.Add("QuickLevelList missions");
                        if (!CosmicMoonContent.HasGatheringRoutes(unsupportedHub.TerritoryId))
                            missing.Add("gathering routes");
                        if (missing.Count > 0)
                            ImGui.Text($"Still needed: {string.Join(", ", missing)}.");
                        ImGui.EndTooltip();
                    }
                }
                if (!P.AutoHook.UpdatedPlugin() && CosmicMoonRegistry.Auxesia.TerritoryId == Player.Territory.RowId)
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                    ImGuiEx.Icon(EColor.Red, FontAwesomeIcon.ExclamationTriangle);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text($"Hey! Your version of autohook is not currently supported on this planet");
                        ImGui.Text($"You need to (currently) be on the testing version to be able fish automated here");
                        ImGui.Text($"There will be another warning to pop up if you try and run this still and it selects a fishing mission...");
                        ImGui.EndTooltip();
                    }
                }

                ImGui.SameLine(0, 10 * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);

                using (ImRaii.Disabled(SchedulerMain.State == IceState.Idle))
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1.0f)))
                    using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1.0f)))
                    using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.1f, 0.1f, 1.0f)))
                    {
                        if (ImGui.Button("Stop", new Vector2(150 * scale, 0)))
                        {
                            SchedulerMain.StopByUser();
                        }
                    }
                }

                ImGui.SameLine(0, 10 * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);

                if (ImGui.Button("Mission Settings"))
                {
                    ImGui.OpenPopup("Mission Settings: Popup");
                }
                if (ImGui.BeginPopup("Mission Settings: Popup"))
                {
                    // TODO: Mission Settings
                    bool grindAllProvisionals = C.GrindAllProvisionals;
                    if (ImGui.Checkbox("Provisional: Allow All Classes", ref grindAllProvisionals))
                    {
                        C.GrindAllProvisionals = grindAllProvisionals;
                        C.Save();
                    }
                    ImGuiEx.HelpMarker("Enabling this will show you all weather/timed/sequence missions that you can grind,\n" +
                                       "ON TOP OF doing the normal missions for whichever class you start on.\n" +
                                       "If you just want to focus one specific class, set this to false");

                    bool allowCriticalsAllClass = C.GrindOffClassRedAlert;
                    if (ImGui.Checkbox("Critical: Allow All Classes", ref allowCriticalsAllClass))
                    {
                        C.GrindOffClassRedAlert = allowCriticalsAllClass;
                        C.Save();
                    }
                    ImGuiEx.HelpMarker($"This will allow you to grind other classes for criticals/red alerts. " +
                        $"(So if you're on crp, but a bsm red alert pops up)");

                    bool removeGold = C.RemoveAfterGold;
                    if (ImGui.Checkbox("Remove Mission Upon Gold Completion", ref removeGold))
                    {
                        C.RemoveAfterGold = removeGold;
                        C.Save();
                    }
                    using (ImRaii.Disabled(!removeGold))
                    {
                        bool keepARanks = C.KeepARanks;
                        if (ImGui.Checkbox("Keep \"A Rank\" missions and below", ref keepARanks))
                        {
                            C.KeepARanks = keepARanks;
                            C.Save();
                        }
                    }

                    ImGui.Checkbox("Stop after current mission", ref Mission_Settings.StopAfterCurrent);
                    bool relicTurnin = C.TurninRelic;
                    if (ImGui.Checkbox($"Turnin if relic is complete##RelicTurnin_GeneralSetting", ref relicTurnin))
                    {
                        C.TurninRelic = relicTurnin;
                        C.Save();
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled("?");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("THIS IS YOUR HEADS UP ON HOW THIS WORKS. If I change this in the future, this tooltip will also change.\n" +
                                         "1: This will check for your current CLASS [not menu class, actual current class] for relic turnin.\n" +
                                         "2: You must not have the tool eqipped for this to run full auto. \n" +
                                         "\t- This is due to the fact that I cba coding this in at this time. (might change my mind in the future *shrugs*)\n" +
                                         "3: This will take prio over \"Stop @ Relic Turnin\", in the sense that if you have both enabled, it will turnin vs stop. And continue about it's day\n" +
                                         "4: If you're on a crafting class, it will return you back to the stop you were crafting post turnin. \n" +
                                         "\t- This is optional, you can disable it at your own free will, I just like this so I can just go back to an isolated area of my choosing");
                    }

                    ImGui.Separator();
                    bool goldHardLast = C.Gold_HardMissionsLast;
                    if (ImGui.Checkbox("Gold Mode: Leave Hard Missions For Last", ref goldHardLast))
                    {
                        C.Gold_HardMissionsLast = goldHardLast;
                        C.Save();
                    }
                    ImGuiEx.HelpMarker("Gold Completion Mode only: missions you have already completed without a gold rating\n" +
                        "are the hardest ones to gold, so they get pushed behind every other mission that still needs one.\n" +
                        "Applies to weather/timed/sequence and red alerts too - those rotate, so passing on a hard one\n" +
                        "just means doing a different one now.\n" +
                        "Turn this off to treat every ungolded mission the same.");

                    bool goldTravel = C.Gold_CrossPlanetTravel;
                    if (ImGui.Checkbox("Gold Mode: Travel Between Planets", ref goldTravel))
                    {
                        C.Gold_CrossPlanetTravel = goldTravel;
                        C.Save();
                    }
                    ImGuiEx.HelpMarker("Gold Completion Mode only: when the only missions left to gold on this moon are\n" +
                        "weather / timed / red alert ones and none of them are up, fly to a hub that has one -\n" +
                        "instead of sitting on the mission board waiting for this moon to roll one.\n" +
                        "Weather forecasts and Eorzea time are readable for every moon, so the hop is planned before leaving.\n" +
                        "Red alerts can't be seen from another moon, so a hub whose only work left is a red alert\n" +
                        "is treated as \"something in about 20 minutes\" when comparing hubs.");

                    using (ImRaii.Disabled(!goldTravel))
                    {
                        ImGui.Indent();
                        var travelMoons = C.Gold_TravelMoons.Count == 0
                            ? CosmicMoonRegistry.TerritoryIds.ToList()
                            : C.Gold_TravelMoons.ToList();

                        for (int i = 0; i < CosmicMoonRegistry.All.Length; i++)
                        {
                            var moon = CosmicMoonRegistry.All[i];
                            bool allowed = travelMoons.Contains(moon.TerritoryId);
                            if (i > 0)
                                ImGui.SameLine();

                            if (ImGui.Checkbox($"{moon.DisplayName}##GoldTravelMoon", ref allowed))
                            {
                                if (allowed)
                                    travelMoons.Add(moon.TerritoryId);
                                else
                                    travelMoons.Remove(moon.TerritoryId);

                                // No destinations left means there is nothing to travel for - an empty list
                                // is the "every moon" default, so turn the feature off instead.
                                if (travelMoons.Count == 0)
                                {
                                    C.Gold_TravelMoons = new();
                                    C.Gold_CrossPlanetTravel = false;
                                }
                                else
                                {
                                    C.Gold_TravelMoons = travelMoons;
                                }

                                C.Save();
                            }
                        }

                        int minWindow = C.Gold_TravelMinWindowMinutes;
                        ImGui.SetNextItemWidth(150 * scale);
                        if (ImGui.SliderInt("Window has to be open this long (min)##GoldTravelWindow", ref minWindow, 1, 20))
                        {
                            C.Gold_TravelMinWindowMinutes = minWindow;
                            C.Save();
                        }
                        ImGuiEx.HelpMarker("A weather / timed window that is about to close isn't worth the flight.");

                        int maxWait = C.Gold_TravelMaxWaitMinutes;
                        ImGui.SetNextItemWidth(150 * scale);
                        if (ImGui.SliderInt("Fly for a window starting within (min)##GoldTravelWait", ref maxWait, 5, 120))
                        {
                            C.Gold_TravelMaxWaitMinutes = maxWait;
                            C.Save();
                        }
                        ImGuiEx.HelpMarker("Nothing anywhere inside this window means staying put instead of flying over to wait.");

                        int rotate = C.Gold_TravelRotateMinutes;
                        ImGui.SetNextItemWidth(150 * scale);
                        if (ImGui.SliderInt("Rotate hubs after (min, 0 = off)##GoldTravelRotate", ref rotate, 0, 60))
                        {
                            C.Gold_TravelRotateMinutes = rotate;
                            C.Save();
                        }
                        ImGuiEx.HelpMarker("When the only missions left anywhere are red alerts, no moon looks better than\n" +
                            "any other from a distance - and sitting on one hub only ever catches that hub's alerts.\n" +
                            "After this long on a hub with nothing to run and no alert brewing, ICE moves to the hub\n" +
                            "it has left alone the longest. It stays put while a red alert is incoming or in progress,\n" +
                            "or when a weather / timed window here opens within this same span.");
                        ImGui.Unindent();
                    }

                    ImGui.Separator();
                    bool relic_AllowRedAlert = C.Relic_IncludeCriticals;
                    if (ImGui.Checkbox("Relic Mode: Allow Red Alerts", ref relic_AllowRedAlert))
                    {
                        C.Relic_IncludeCriticals = relic_AllowRedAlert;
                        C.Save();
                    }

                    bool OnlySelected = C.XPRelicOnlyEnabled;
                    if (ImGui.Checkbox("Relic Mode: Only Enabled", ref OnlySelected))
                    {
                        C.XPRelicOnlyEnabled = OnlySelected;
                        C.Save();
                    }
                    if (ImGui.Button("Open Job Swap Settings"))
                    {
                        C.SelectedTab = WindowSelection.CharacterSettings;
                    }

                    if (ImGui.Button("Save Current Mission Preset"))
                    {
                        ImGui.OpenPopup("Preset Save Editor");
                    }

                    if (ImGui.BeginPopup("Preset Save Editor"))
                    {
                        ImGui.InputText($"Playlist Name", ref newListName);
                        using (ImRaii.Disabled(string.IsNullOrEmpty(newListName)))
                        {
                            if (ImGui.Button("Save New List"))
                            {
                                List<uint> new_Playlist = new();
                                foreach (var mission in C.MissionConfig.Where(x => x.Value.Enabled))
                                {
                                    new_Playlist.Add(mission.Key);
                                }
                                if (C.Mission_Playlist.ContainsKey(newListName))
                                {
                                    C.Mission_Playlist[newListName] = new_Playlist;
                                }
                                else
                                {
                                    C.Mission_Playlist.Add(newListName, new_Playlist);
                                }
                                C.Save();
                                ImGui.CloseCurrentPopup();
                            }
                        }

                        ImGui.EndPopup();
                    }

                    if (C.Mission_Playlist.Count > 0)
                    {
                        if (ImGui.Button("View All Presets"))
                        {
                            ImGui.OpenPopup("Preset: List Viewer");
                        }

                        if (ImGui.BeginPopup("Preset: List Viewer"))
                        {
                            ImGui.Text($"Load Mission Preset");

                            if (ImGui.BeginTable($"Preset: TableViewer", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                            {
                                ImGui.TableSetupColumn("Name");
                                ImGui.TableSetupColumn("Amount Enabled");

                                ImGui.TableHeadersRow();

                                ImGui.TableNextRow();
                                ImGui.TableSetColumnIndex(0);
                                ImGui.AlignTextToFramePadding();
                                ImGui.Text($"Clear All");
                                ImGui.SameLine();
                                if (ImGuiEx.IconButton(FontAwesomeIcon.ArrowUpRightFromSquare, $"FreshPreset_Button"))
                                {
                                    foreach (var mission in C.MissionConfig)
                                    {
                                        mission.Value.Enabled = false;
                                    }
                                    C.Save();
                                    ImGui.CloseCurrentPopup();
                                }

                                foreach (var item in C.Mission_Playlist)
                                {
                                    ImGui.TableNextRow();
                                    ImGui.TableSetColumnIndex(0);
                                    ImGui.AlignTextToFramePadding();
                                    ImGui.Text($"{item.Key}");
                                    ImGui.SameLine();
                                    if (ImGuiEx.IconButton(FontAwesomeIcon.ArrowUpRightFromSquare, $"{item.Key}_Button"))
                                    {
                                        foreach (var mission in C.MissionConfig)
                                        {
                                            if (item.Value.Contains(mission.Key))
                                                mission.Value.Enabled = true;
                                            else
                                                mission.Value.Enabled = false;
                                        }
                                        C.Save();
                                        ImGui.CloseCurrentPopup();
                                    }
                                    if (ImGui.IsItemHovered())
                                    {
                                        ImGui.SetTooltip("Import Missions");
                                    }

                                    ImGui.TableNextColumn();
                                    ImGui.AlignTextToFramePadding();
                                    ImGui.Text($"{item.Value.Count}");

                                    ImGui.TableNextColumn();
                                    if (ImGuiEx.IconButton(FontAwesomeIcon.Trash, $"{item.Key}_Remove"))
                                    {
                                        C.Mission_Playlist.Remove(item);
                                        C.Save();
                                    }
                                    if (ImGui.IsItemHovered())
                                    {
                                        ImGui.SetTooltip("Remove from list");
                                    }
                                }

                                ImGui.EndTable();
                            }

                            ImGui.EndPopup();
                        }
                    }


                ImGui.EndPopup();
                }
            }

            using (var bodyChild = ImRaii.Child("##modeSelect_Body", new Vector2(0, -1), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (!bodyChild.Success) return;

                float scrollbarSize = ImGui.GetStyle().ScrollbarSize;
                float buttonRowHeight = (ImGui.GetTextLineHeight() + 8 * scale + 4 * scale) + scrollbarSize;

                using (var missionButtons = ImRaii.Child("##tab_scroll", new Vector2(0, buttonRowHeight), false, ImGuiWindowFlags.HorizontalScrollbar))
                {
                    if (!missionButtons.Success)
                        return;

                    ImGui_Ice.DrawRankButton("Red Alert", MissionFilter.RedAlert, MissionTable);
                    ImGui_Ice.DrawRankButton("Sequence", MissionFilter.Sequence, MissionTable);
                    ImGui_Ice.DrawRankButton("Weather", MissionFilter.Weather, MissionTable);
                    ImGui_Ice.DrawRankButton("Timed", MissionFilter.Timed, MissionTable);
                    ImGui_Ice.DrawRankButton("Master", MissionFilter.Master, MissionTable);
                    ImGui_Ice.DrawRankButton("A Rank", MissionFilter.ARank, MissionTable);
                    ImGui_Ice.DrawRankButton("B Rank", MissionFilter.BRank, MissionTable);
                    ImGui_Ice.DrawRankButton("C Rank", MissionFilter.CRank, MissionTable);
                    ImGui_Ice.DrawRankButton("D Rank", MissionFilter.DRank, MissionTable);

                    ImGui_Ice.EndCategoryButtonRow();
                }

                var bottomSpace = ImGui.GetTextLineHeight() + 6f;
                bottomSpace += 12f; // prevent the tabs from creating a scrollbar

                Vector2 size = new(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - bottomSpace);
                if (ImGui.BeginChild("###MissionTableV3", size, false))
                {
                    try
                    {
                        if (MissionTable == null && CosmicHelper.SheetMissionDict.Count > 0)
                        {
                            foreach (var mission in CosmicHelper.SheetMissionDict)
                            {
                                CosmicHelper.MissionInfo missionDetails = new() { Id = mission.Key };
                                TableItems.Add(missionDetails);
                            }
                            ItemCount = TableItems.Count();
                            MissionTable = new(TableItems);
                        }
                        var filterActive = MissionTable.FilteredItems.Count != 0 && MissionTable.FilteredItems.Count != ItemCount;
                        var filterCount = filterActive ? $" (of {ItemCount})" : "";
                        var height = ImGui.GetFrameHeight();
                        MissionTable.Draw(height + 4f);
                    }
                    catch (Exception ex)
                    {
                        IceLogging.Error(ex.Message, "Drawing Mission Table");
                    }
                }
                ImGui.EndChild();
            }
        }
    }
}
