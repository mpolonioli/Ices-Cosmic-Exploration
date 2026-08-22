using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ICE.Sounds;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.GatheringHelper;
using ICE.Utilities.GatheringHelper.RouteLoader;
using System.Collections.Generic;
using System.Linq;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_CheckMissions
    {
        public enum MissionKind
        {
            Critical,
            Weather,
            Timed,
            Sequence,
            Ex,
            Master,
            A,
            B,
            C,
            D,
            Unknown,
        }

        private static Dictionary<MissionKind, List<uint>> MissionLibrary = new()
        {
            [MissionKind.Critical] = new(),
            [MissionKind.Weather] = new(),
            [MissionKind.Timed] = new(),
            [MissionKind.Sequence] = new(),
            [MissionKind.Ex] = new(),
            [MissionKind.Master] = new(),
            [MissionKind.A] = new(),
            [MissionKind.B] = new(),
            [MissionKind.C] = new(),
            [MissionKind.D] = new(),
            [MissionKind.Unknown] = new(),
        };

        private static readonly Random _random = new Random();

        public static void Enqueue()
        {
            P.TaskManager.EnqueueMulti
                (
                    new(() => RefreshMissionLibrary(), "Refreshing the mission library"),
                    new(() => OpenMissionUi(), "Opening Mission Ui"),
                    new(() => CheckTabs(), "Checking tabs for valid missions")
                );
        }
        private static void ReOpenMissionUi(string tag)
        {
            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var missionUi) && missionUi.IsAddonReady)
                return;

            if (GenericHelpers.TryGetAddonMaster<WKSHud>("WKSHud", out var moonHud) && moonHud.IsAddonReady)
            {
                if (EzThrottler.Throttle("Opening the mission ui"))
                {
                    IceLogging.Info("Opening the moon mission selection hud", tag);
                    moonHud.Mission();
                }
            }
        }

        private static readonly MissionKind[] HuntSpecialMissionKinds =
            [MissionKind.Critical, MissionKind.Weather, MissionKind.Timed, MissionKind.Sequence];

        private static readonly MissionKind[] StandardMissionKinds =
            [MissionKind.Ex, MissionKind.A, MissionKind.B, MissionKind.C, MissionKind.D];

        private static int EnabledStandardMissionCount()
        {
            var count = 0;
            foreach (var rank in StandardMissionKinds)
                count += MissionLibrary[rank].Count;
            return count;
        }

        /// <summary>
        /// Gold completion grind only: idle-wait when special missions remain ungolded,
        /// none are on the board, and there are no standard missions left to reroll for.
        /// </summary>
        private static bool WaitingForSpecialMissions() =>
            Mission_Settings.Mode == ModeSelect.MissionGoldMode
            && HuntSpecialMissionKinds.Any(kind => MissionLibrary[kind].Count > 0)
            && EnabledStandardMissionCount() == 0;

        private static void EnterWaitForSpecialMissions(string tag)
        {
            if (SchedulerMain.State != IceState.Waiting)
            {
                IceLogging.Info("Gold completion grind: waiting for a timed, weather, or critical mission to appear on the board.", tag);
                SchedulerMain.State = IceState.Waiting;
            }

            CosmicHandler.EnsureStandardMissionTab(Mission_Settings.SelectedJob);
            P.TaskManager.Tasks.Clear();
        }

        public static void EnqueueWaitRecheck()
        {
            P.TaskManager.Enqueue(() => WaitForSpecialMissionRecheck(), "Waiting for special mission availability");
        }

        private static bool? WaitForSpecialMissionRecheck()
        {
            string tag = "[Check Missions: Wait for Special]";

            if (!EzThrottler.Throttle("Recheck special missions", 15_000))
                return false;

            CosmicHandler.EnsureStandardMissionTab(Mission_Settings.SelectedJob);
            IceLogging.Verbose("Rechecking mission board for timed/weather/critical missions", tag);
            SchedulerMain.State = IceState.GrabMission;
            return true;
        }
        private static MissionKind LibraryInfo(KeyValuePair<uint, CosmicHelper.CosmicInfo> mission)
        {
            MissionKind entry = MissionKind.Unknown;
            var attribute = mission.Value.Attributes;
            var rank = mission.Value.Rank;

            if (attribute.HasFlag(MissionAttributes.ProvisionalWeather))
                entry = MissionKind.Weather;
            else if (attribute.HasFlag(MissionAttributes.ProvisionalTimed))
                entry = MissionKind.Timed;
            else if (attribute.HasFlag(MissionAttributes.ProvisionalSequential))
                entry = MissionKind.Sequence;
            else if (attribute.HasFlag(MissionAttributes.Critical))
                entry = MissionKind.Critical;
            else if (mission.Value.IsMaster)
                entry = MissionKind.Master;
            else if (rank != 0)
            {
                entry = rank switch
                {
                    5 => MissionKind.Ex,
                    4 => MissionKind.A,
                    3 => MissionKind.B,
                    2 => MissionKind.C,
                    1 => MissionKind.D,
                    _ => MissionKind.Unknown,
                };
            }

            return entry;
        }
        public static bool? RefreshMissionLibrary()
        {
            string tag = "Task Check Mission: Refresh Mission Library";

            foreach (var entry in MissionLibrary)
            {
                entry.Value.Clear();
            }

            var playerTerritory = Player.Territory.RowId;

            var enabledPerMoon = string.Join("\n",
                CosmicMoonRegistry.All.Select(m =>
                    $"{m.DisplayName} [{m.TerritoryId}] = [{CosmicMoonRegistry.CountEnabledMissions(m.TerritoryId)}]"));

            IceLogging.Info("This is just general message to let me know WHAT planet you're on, and where you have things enabled\n" +
                "If you're not running things that requires these to be enabled, you can ignore this if you're reading this.\n" +
                $"{enabledPerMoon}\n" +
                $"Current TerritoryID: {playerTerritory}");

            var modeSelected = Mission_Settings.Mode;

            if (modeSelected == ModeSelect.MissionGoldMode)
                RefreshGoldSequencePrereqs();

            foreach (var mission in CosmicHelper.SheetMissionDict)
            {
                if (mission.Value.TerritoryId != Player.Territory.RowId)
                    continue;

                bool provisional = mission.Value.IsProvisional;

                var missionId = mission.Key;

                if (C.MissionConfig.TryGetValue(missionId, out var config))
                {
                    if (modeSelected == ModeSelect.LevelMode && CosmicHelper.QuickLevelList.Contains(mission.Key))
                    {
                        var job = Mission_Settings.SelectedJob;
                        var jobLevel = Player.GetLevel((Job)job);
                        var missionLevel = mission.Value.Level;

                        if (!mission.Value.Jobs.Contains(job))
                            continue;

                        // Short end of it all, making sure to see what tier the player should be doing
                        // Taking the players level and making sure it matches to the tier
                        // 90+ = 90
                        // 50-89 = 50
                        // 10-49 = 10
                        int playerTier = jobLevel >= 90 ? 90 : jobLevel >= 50 ? 50 : 10;

                        if (missionLevel != playerTier)
                            continue;
                        else
                        {
                            MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                    }
                    else if (modeSelected == ModeSelect.RelicMode)
                    {
                        if (provisional)
                            continue;

                        if (mission.Value.IsCritical && !C.Relic_IncludeCriticals)
                            continue;

                        var jobLevel = Math.Min(Player.GetLevel((Job)mission.Value.Jobs.First()), Player.GetLevel((Job)mission.Value.Jobs.Last()));
                        if (jobLevel < mission.Value.Level)
                            continue;

                        if (C.XPRelicOnlyEnabled)
                        {
                            if (config.Enabled && mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                        else
                        {
                            if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                    }
                    else if (modeSelected == ModeSelect.Standard)
                    {
                        if (!config.Enabled)
                            continue;

                        var jobLevel = Math.Min(Player.GetLevel((Job)mission.Value.Jobs.First()), Player.GetLevel((Job)mission.Value.Jobs.Last()));
                        if (jobLevel < mission.Value.Level)
                            continue;

                        if (provisional)
                        {
                            if (C.GrindAllProvisionals)
                            {
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                            }
                            else if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                            {
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                            }
                        }
                        else if (mission.Value.Attributes.HasFlag(MissionAttributes.Critical))
                        {
                            if (C.GrindOffClassRedAlert)
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                            else if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                        else
                        {
                            if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                    }
                    else if (modeSelected == ModeSelect.MissionGoldMode)
                    {
                        // Gold completion spans every class in JobPrio - not just the equipped one - since the
                        // goal is to gold the whole board and we're free to swap to whatever job a mission wants.
                        if (MissionGolded(missionId))
                            continue;

                        if (!GoldJobAllowed(mission.Value))
                            continue;

                        if (!LevelRequirementMet(mission.Value))
                            continue;

                        if (provisional)
                        {
                            // Sequence chains only offer their later steps once the earlier ones are done,
                            // so the whole chain gets pulled in even where individual steps are already gold.
                            foreach (var seqMissionId in mission.Value.SequenceMissions_Previous.Concat(mission.Value.SequenceMissions_Next))
                            {
                                if (!CosmicHelper.SheetMissionDict.TryGetValue(seqMissionId, out var seqInfo))
                                    continue;

                                var seqKind = LibraryInfo(new(seqMissionId, seqInfo));
                                if (!MissionLibrary[seqKind].Contains(seqMissionId))
                                    MissionLibrary[seqKind].Add(seqMissionId);
                            }
                        }

                        MissionLibrary[LibraryInfo(mission)].Add(missionId);
                    }
                }
                else
                {
                    IceLogging.Error("We're missing a mission from the config, please report back so I can fix this.\n" +
                        $"MissionID: {mission.Key} | Job (First) {mission.Value.Jobs.First()} | Rank: {mission.Value.Rank}");
                }
            }

            // In Standard mode the library above only covers the current SelectedJob. Another selected
            // job may still have enabled missions (any type), which CheckGlobalPriority can pick cross-job —
            // so don't go Idle just because the current job's library came up empty.
            bool standardCrossJobAvailable = modeSelected == ModeSelect.Standard
                && ActiveJobPool().Any(job => CosmicHelper.SheetMissionDict.Keys.Any(id => GlobalEligible(id, job)));


            if (MissionLibrary.All(x => x.Value.Count == 0) && !standardCrossJobAvailable)
            {
                if (modeSelected == ModeSelect.RelicMode && C.XPRelicOnlyEnabled)
                {
                    IceLogging.ChatInfo("\"Only selected missions\" is enabled for Relic Grind, but no selected missions match your current job. Please select missions for this job, switch jobs, or disable the option.", "[I.C.E.]");
                    if (C.PlaySoundAlert)
                    {
                        _ = SoundPlayer.PlaySoundAsync();
                    }
                }
                else
                {
                    IceLogging.Verbose("We currently have no viable missions... which is odd. Please make sure you have some enabled, or report back if this is incorrect\n" +
                        $"Config Mode: {C.SelectedMode} | Mode going into this: {Mission_Settings.Mode}", tag);
                }

                SchedulerMain.State = IceState.Idle;
                P.TaskManager.Tasks.Clear();
                return true;
            }
            else
            {
                IceLogging.Verbose("We've reached the end of the mission sorter, going to report back what our current mission counts are at:", tag);
                foreach (var key in MissionLibrary)
                {
                    IceLogging.Verbose($"[{key.Key}] = {key.Value.Count()}", tag);
                }
                IceLogging.Verbose("Going to run the sorter one more time to make sure that the priority is set for all of these (it should but ya never know)", tag);
                foreach (var key in MissionLibrary.Keys.ToList())
                {
                    MissionLibrary[key] = MissionLibrary[key]
                        // Gold completion pushes the missions we already failed to gold to the back.
                        .OrderBy(x => GoldDifficultyTier(x))
                        .ThenBy(x => GoldAttemptPenalty(x))
                        .ThenBy(x =>
                        {
                            var jobs = CosmicHelper.SheetMissionDict[x].Jobs;
                            var bestIndex = jobs
                                .Select(job => C.JobPrio.IndexOf(job))
                                .Where(i => i >= 0)
                                .DefaultIfEmpty(int.MaxValue)
                                .Min();
                            return bestIndex;
                        })
                        .ToList();
                }

                IceLogging.Verbose($"Mission finder says we have a valid mission list. So we gonna go find one", tag);
                IceLogging.Verbose($"Stardard tab missions job: {Mission_Settings.SelectedJob}");
                return true;
            }
        }
        public static bool? OpenMissionUi()
        {
            string tag = "[Task Check Mission: Open Mission UI]";

            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talkUi) && talkUi.IsAddonReady)
            {
                if (EzThrottler.Throttle("Closing the talk"))
                {
                    IceLogging.Info("Talk ui was visible, clicking through", tag);
                    talkUi.Click();
                }

                return false;
            }

            if (CosmicHandler.CanQueryMissionsWithoutUi())
            {
                CosmicHandler.EnsureStandardMissionTab(Mission_Settings.SelectedJob);

                if (WaitingForSpecialMissions())
                {
                    IceLogging.Verbose("Mission agent is active — reading the board without opening WKSMission UI", tag);
                    return true;
                }
            }

            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var hud) && hud.IsAddonReady)
            {
                IceLogging.Info("The Mission Selection Ui is visible! Continuing on", tag);
                return true;
            }

            ReOpenMissionUi(tag);

            return false;
        }
        public static bool? CheckTabs()
        {
            string tag = "Check Missions: Check Tabs";
            var priority = C.MissionTypePrio;

            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var x) && x.IsAddonReady
                || CosmicHandler.CanQueryMissionsWithoutUi())
            {
                // Standard and Gold Completion do a single cross-job global pick spanning ALL mission types
                // (Critical/Provisional/Standard/Tool Mastery/Drone) in C.MissionTypePrio order, so they
                // bypass the per-type single-job loop below.
                if (Mission_Settings.Mode is ModeSelect.Standard or ModeSelect.MissionGoldMode)
                {
                    // Drone search is a side activity (no-op if no box/marker); keep it ahead of the
                    // mission pick like the legacy default ordering - but never in the middle of a sequence
                    // chain, where wandering off to a box costs us the follow-up step.
                    if (!SequenceContinuationPending()
                        && C.MissionTypePrio.Contains(MissionTypes.DroneSearch) && C.Cosmodrone_Run
                        && CosmicMoonRegistry.TryGetMoon(Player.Territory.RowId, out var droneHub) && droneHub.HasCosmodrome)
                    {
                        P.TaskManager.Enqueue(() => Task_ArtifactSearch.RefreshMapInfo(), "Inserting Drone Task");
                    }
                    P.TaskManager.Enqueue(() => WaitForSequenceFollowUp(), "Waiting on a sequence follow-up");
                    P.TaskManager.Enqueue(() => CheckGlobalPriority(), "Global cross-job priority selection");
                }
                else
                {
                foreach (var type in C.MissionTypePrio)
                {
                    switch (type)
                    {
                        case MissionTypes.Critical:
                        {
                            if (MissionLibrary[MissionKind.Critical].Count > 0)
                            {
                                P.TaskManager.Enqueue(() => CheckMissions(MissionLibrary[MissionKind.Critical], type), "Checking Critical tab for missions");
                            }
                            break;
                        }
                        case MissionTypes.Provisional:
                        {
                            List<uint> provisionals = new();
                            foreach (var ProvisionalPrio in C.MissionPrio)
                            {
                                MissionKind key = ProvisionalPrio switch
                                {
                                    ProvisionalTypes.ProvisionalWeather => MissionKind.Weather,
                                    ProvisionalTypes.ProvisionalSequential => MissionKind.Sequence,
                                    ProvisionalTypes.ProvisionalTimed => MissionKind.Timed,
                                    _ => MissionKind.Unknown
                                };

                                if (MissionLibrary.TryGetValue(key, out var missionList))
                                {
                                    foreach (var jobId in C.JobPrio)
                                    {
                                        // Add missions that match this provisional type AND this job
                                        foreach (var missionId in missionList)
                                        {
                                            if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var missionInfo) && missionInfo.Jobs.Contains(jobId) && !provisionals.Contains(missionId))
                                            {
                                                provisionals.Add(missionId);
                                            }
                                        }
                                    }
                                }
                            }
                            if (provisionals.Count > 0)
                            {
                                P.TaskManager.Enqueue(() => CheckMissions(provisionals, type), "Checking Provisional tab for missions");
                            }
                            break;
                        }
                        case MissionTypes.Standard:
                        {
                            List<uint> basicMissions = new();
                            List<MissionKind> MissionRanks = new() { MissionKind.Ex, MissionKind.A, MissionKind.B, MissionKind.C, MissionKind.D };
                            foreach (var rank in MissionRanks)
                            {
                                if (MissionLibrary.TryGetValue(rank, out var missionList))
                                {
                                    foreach (var mission in missionList)
                                    {
                                        if (!basicMissions.Contains(mission))
                                            basicMissions.Add(mission);
                                    }
                                }
                            }
                            P.TaskManager.Enqueue(() => CheckMissions(basicMissions, type), "Checking Basic Mission tab for missions");
                            break;
                        }
                        case MissionTypes.DroneSearch:
                        {
                            if (C.Cosmodrone_Run && CosmicMoonRegistry.TryGetMoon(Player.Territory.RowId, out var hub) && hub.HasCosmodrome)
                            {
                                P.TaskManager.Enqueue(() => Task_ArtifactSearch.RefreshMapInfo(), "Inserting Drone Task");
                            }
                            break;
                        }
                        case MissionTypes.ToolMastery:
                        {
                            if (MissionLibrary[MissionKind.Master].Count > 0)
                            {
                                P.TaskManager.Enqueue(() => CheckMissions(MissionLibrary[MissionKind.Master], type), "Checking for master missions");
                            }
                            break;
                        }
                    }
                }

                // Tool Mastery missions live in their own in-game tab (no dedicated getter),
                // so handle them explicitly regardless of MissionTypePrio ordering.
                if (MissionLibrary[MissionKind.Master].Count > 0)
                {
                    P.TaskManager.Enqueue(() => CheckMissions(MissionLibrary[MissionKind.Master], MissionTypes.ToolMastery), "Checking Tool Mastery tab for missions");
                }
                }

                if (Mission_Settings.Mode == ModeSelect.MissionGoldMode)
                    P.TaskManager.Enqueue(() => AlignGoldRerollJob(), "Picking which job to reroll on");

                P.TaskManager.Enqueue(() => FindReroll(), "Find mission to reroll for");
            }
            else
            {
                ReOpenMissionUi(tag);
            }
            return true;
        }
        // Eligibility for the cross-job global pick: on this moon + matches the probed job + level-valid
        // (Min of dual jobs, mirroring RefreshMissionLibrary). Standard mode additionally requires the
        // mission to be enabled; Gold Completion instead requires it to still need a gold. Mission-type
        // agnostic — type filtering happens in GlobalMatchesType.
        private static bool GlobalEligible(uint missionId, uint job)
        {
            if (!CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var m))
                return false;
            if (m.TerritoryId != Player.Territory.RowId)
                return false;
            if (!m.Jobs.Contains(job))
                return false;
            if (!LevelRequirementMet(m))
                return false;

            if (Mission_Settings.Mode == ModeSelect.MissionGoldMode)
            {
                // Golded missions are only worth repeating when a chain we still need hangs off them.
                return !MissionGolded(missionId) || GoldSequencePrereqs.Contains(missionId);
            }

            if (!C.MissionConfig.TryGetValue(missionId, out var config) || !config.Enabled)
                return false;

            return true;
        }
        private static bool GlobalMatchesType(uint missionId, MissionTypes type)
        {
            var m = CosmicHelper.SheetMissionDict[missionId];
            return type switch
            {
                MissionTypes.Critical => m.IsCritical,
                MissionTypes.Provisional => m.IsProvisional,
                MissionTypes.ToolMastery => m.IsMaster,
                MissionTypes.Standard => !m.IsProvisional && !m.IsCritical && !m.IsMaster,
                _ => false,
            };
        }
        // Lower value = higher priority within a mission type. Standard orders by rank (EX->D);
        // Provisional by the configured C.MissionPrio sub-type order; others have no sub-order.
        private static int GlobalWithinTypeKey(uint missionId, MissionTypes type)
        {
            var m = CosmicHelper.SheetMissionDict[missionId];
            switch (type)
            {
                case MissionTypes.Standard:
                    // Gold Completion takes the cheapest rank first (D -> EX); everything else wants the
                    // biggest payout first (EX -> D).
                    return Mission_Settings.Mode == ModeSelect.MissionGoldMode
                        ? (int)m.Rank
                        : -(int)m.Rank; // EX(5) -> -5 wins over D(1) -> -1
                case MissionTypes.Provisional:
                {
                    ProvisionalTypes sub = m.IsWeather ? ProvisionalTypes.ProvisionalWeather
                        : m.IsTimed ? ProvisionalTypes.ProvisionalTimed
                        : m.IsSequence ? ProvisionalTypes.ProvisionalSequential
                        : ProvisionalTypes.ProvisionalWeather;
                    int idx = C.MissionPrio.IndexOf(sub);
                    return idx >= 0 ? idx : int.MaxValue - 1;
                }
                default:
                    return 0;
            }
        }
        // Global pick for Standard and Gold Completion: across ALL jobs in C.JobPrio and ALL mission types
        // (in C.MissionTypePrio order), choose the single best available mission, switch SelectedJob to its
        // job, and grab it. Mission-type priority is the outer loop; within a type, GlobalWithinTypeKey then
        // JobPrio. Gold Completion wraps that in a difficulty pass so missions already completed without a
        // gold are only picked once nothing else on the board still needs one.
        private static bool? CheckGlobalPriority()
        {
            string tag = "[Check Missions: Global Priority]";

            if (Mission_Settings.Mode is not (ModeSelect.Standard or ModeSelect.MissionGoldMode))
                return true;

            // A mission was already grabbed earlier in the queue.
            if (CosmicHelper.CurrentLunarMission != 0)
                return true;

            // The mission agent can be read without the window being up (gold completion idles on the board
            // that way), so only force the UI open when neither is available.
            if (!CosmicHandler.CanQueryMissionsWithoutUi()
                && (!GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var hud) || !hud.IsAddonReady))
            {
                ReOpenMissionUi(tag);
                return false;
            }

            var startJob = Mission_Settings.SelectedJob;

            // Probe each selected job once and collect everything currently available on its board.
            // Availability getters are per-job-tab, so we must switch tabs to read each job.
            var availableByJob = new Dictionary<uint, HashSet<uint>>();
            foreach (var job in ActiveJobPool())
            {
                var eligible = CosmicHelper.SheetMissionDict.Keys.Where(id => GlobalEligible(id, job)).ToList();
                if (eligible.Count == 0)
                    continue;

                bool hasMaster = eligible.Any(id => CosmicHelper.SheetMissionDict[id].IsMaster);
                bool hasNonMaster = eligible.Any(id => !CosmicHelper.SheetMissionDict[id].IsMaster);

                var set = new HashSet<uint>();
                if (hasNonMaster && CorrectJobTab(job, 0))
                {
                    foreach (var id in CosmicHandler.Basic_AvailableMissions()) set.Add(id);
                    foreach (var id in CosmicHandler.Provisional_AvailableMissions()) set.Add(id);
                    foreach (var id in CosmicHandler.Critical_AvailableMissions()) set.Add(id);
                }
                if (hasMaster && CorrectJobTab(job, CosmicHandler.ToolMasteryTab))
                {
                    foreach (var id in CosmicHandler.Mastery_AvailableMissions()) set.Add(id);
                }

                if (set.Count > 0)
                    availableByJob[job] = set;
            }

            if (availableByJob.Count == 0)
            {
                CorrectJobTab(startJob, 0);
                return true;
            }

            // Walk mission types in priority order; first type with any available+enabled mission wins.
            // Tool Mastery is also checked even if the user removed it from MissionTypePrio (parity with
            // the legacy "always check mastery" safety net).
            var typeOrder = new List<MissionTypes>(C.MissionTypePrio);
            if (!typeOrder.Contains(MissionTypes.ToolMastery))
                typeOrder.Add(MissionTypes.ToolMastery);

            // Sequence chains come before everything else. A follow-up step is only on the board because we
            // just cleared the step before it and it vanishes the moment we take something else, so the
            // configured type/job/difficulty order doesn't get a say - we finish what we started.
            var chainPick = availableByJob
                .SelectMany(kvp => kvp.Value.Select(id => (Mission: id, Job: kvp.Key)))
                .Where(c => GlobalEligible(c.Mission, c.Job) && IsLiveSequenceFollowUp(c.Mission))
                // Deepest step first, so a chain already underway finishes before another one starts.
                .OrderByDescending(c => CosmicHelper.SheetMissionDict[c.Mission].SequenceMissions_Previous.Count)
                .ThenBy(c => C.JobPrio.IndexOf(c.Job))
                .FirstOrDefault();

            if (chainPick.Mission != 0)
            {
                IceLogging.Info($"Sequence chain: taking step [{chainPick.Mission}] on job [{chainPick.Job}] ahead of " +
                    "the normal priority order - its earlier steps are all gold and the chain still needs one.", tag);
                Mission_Settings.SelectedJob = chainPick.Job;
                CorrectJobTab(chainPick.Job, 0);
                Insert_GrabMissionTask(chainPick.Mission);
                return true;
            }

            // Difficulty tiers only exist for Gold Completion: tier 0 is everything that has never been
            // completed below gold, tier 1 the ones we've already failed to gold. While any tier 0 standard
            // mission is still out there we keep rerolling for it rather than accepting a tier 1 one, so the
            // hard missions genuinely come last instead of just losing ties. Outside gold mode every mission
            // is tier 0, so the extra pass costs nothing.
            var difficultyTiers = GoldDeprioritisingHardMissions() && GoldEasyMissionsRemain()
                ? new[] { 0 }
                : new[] { 0, 1 };

            foreach (var tier in difficultyTiers)
            {
                foreach (var type in typeOrder)
                {
                    // Drone search is handled separately in CheckTabs (it's a side activity, not a mission grab).
                    if (type == MissionTypes.DroneSearch)
                        continue;

                    uint bestMission = 0, bestJob = 0;
                    int bestWithin = int.MaxValue, bestPenalty = int.MaxValue, bestJobPrio = int.MaxValue;

                    foreach (var kvp in availableByJob.OrderBy(k => C.JobPrio.IndexOf(k.Key)))
                    {
                        var job = kvp.Key;
                        int jobPrio = C.JobPrio.IndexOf(job);
                        foreach (var id in kvp.Value)
                        {
                            if (!GlobalEligible(id, job) || !GlobalMatchesType(id, type))
                                continue;
                            if (GoldDifficultyTier(id) != tier)
                                continue;

                            int within = GlobalWithinTypeKey(id, type);
                            int penalty = GoldAttemptPenalty(id);
                            if (within < bestWithin
                                || (within == bestWithin && penalty < bestPenalty)
                                || (within == bestWithin && penalty == bestPenalty && jobPrio < bestJobPrio))
                            {
                                bestWithin = within;
                                bestPenalty = penalty;
                                bestMission = id;
                                bestJob = job;
                                bestJobPrio = jobPrio;
                            }
                        }
                    }

                    if (bestMission != 0)
                    {
                        byte catTab = CosmicHelper.SheetMissionDict[bestMission].IsMaster ? CosmicHandler.ToolMasteryTab : (byte)0;
                        IceLogging.Info($"Global priority pick: type [{type}], mission [{bestMission}] on job [{bestJob}]" +
                            $"{(tier > 0 ? " (previously completed without gold)" : string.Empty)}" +
                            $"{SequenceNote(bestMission)}. " +
                            $"Was on job [{startJob}].", tag);
                        Mission_Settings.SelectedJob = bestJob;
                        CorrectJobTab(bestJob, catTab);
                        Insert_GrabMissionTask(bestMission);
                        return true;
                    }
                }
            }

            // Nothing grabbable on any job right now; restore the board so FindReroll targets the original job.
            CorrectJobTab(startJob, 0);
            return true;
        }
        private static bool? CheckMissions(List<uint> missionList, MissionTypes type)
        {
            string tag = "[Check Missions: Queue]";
            void LogInfo(uint missionId)
            {
                var sheetInfo = CosmicHelper.SheetMissionDict[missionId];
                bool provisional = sheetInfo.IsProvisional;
                var redAlert = sheetInfo.IsCritical;
                string jobs = string.Join(", ", sheetInfo.Jobs);

                IceLogging.Info($"We found a mission! We're going to exit out of this task and grab the following: \n " +
                    $"[Id] = {missionId}\n" +
                    $"[Selected Job] = {Mission_Settings.SelectedJob}\n" +
                    $"[Mission Job] = {jobs}\n" +
                    $"Red Alert: {redAlert}\n" +
                    $"Provisional: {provisional}", tag);
            }

            if (CosmicHandler.CanQueryMissionsWithoutUi() || (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var missionInfo) && missionInfo.IsAddonReady))
            {
                var basicMissionList = CosmicHandler.Basic_AvailableMissions();
                var specialMissionList = CosmicHandler.Provisional_AvailableMissions();
                var criticalMissions = CosmicHandler.Critical_AvailableMissions();
                var masteryMissions = CosmicHandler.Mastery_AvailableMissions();
                var mode = Mission_Settings.Mode;

                var job = Mission_Settings.SelectedJob;

                // Tool Mastery missions live on their own category tab (3); everything else is Basic (0).
                byte categoryTab = type is MissionTypes.ToolMastery ? CosmicHandler.ToolMasteryTab : (byte)0;

                if (CorrectJobTab(job, categoryTab))
                {
                    if (mode == ModeSelect.LevelMode)
                    {
                        var levelingMission = missionList.FirstOrDefault();
                        IceLogging.Verbose($"Leveling Mission: Job: {Mission_Settings.SelectedJob} | Mission: {levelingMission} | Level: {CosmicHelper.SheetMissionDict[levelingMission].Level}", debugOnly: true);
                        if (basicMissionList.Contains(levelingMission))
                        {
                            LogInfo(levelingMission);
                            Insert_GrabMissionTask(levelingMission);
                            return true;
                        }

                        IceLogging.Verbose($"We seem to have not found the mission. Going to double check to make sure we have the tab unlocked", tag);

                        var highestRank = basicMissionList.Max(x => CosmicHelper.SheetMissionDict[x].Rank);
                        var level = Player.GetLevel((Job)Mission_Settings.SelectedJob);
                        uint missionId = 0;

                        if (level >= 50 && highestRank < 2)
                        {
                            IceLogging.Verbose("We need to unlock the Lv. 50 Missions [C Rank] so we get better exp gains", tag);
                            missionId = basicMissionList
                                .Where(x => CosmicHelper.Unlock_MissionList.Contains(x))
                                .Where(x => CosmicHelper.SheetMissionDict[x].Drank)
                                .Where(x => CosmicHelper.SheetMissionDict[x].CompletionStatus is CosmicHelper.Status.None)
                                .FirstOrDefault();
                            IceLogging.Verbose($"Lv. 50 Mission: {missionId}", tag);
                        }
                        else if (level >= 90 && highestRank < 3)
                        {
                            IceLogging.Verbose("We need to unlock the Lv. 90 Missions [B Rank] so we get better exp gains", tag);
                            missionId = basicMissionList
                                .Where(x => CosmicHelper.Unlock_MissionList.Contains(x))
                                .Where(x => CosmicHelper.SheetMissionDict[x].CRank)
                                .Where(x => CosmicHelper.SheetMissionDict[x].CompletionStatus is CosmicHelper.Status.None)
                                .FirstOrDefault();
                            IceLogging.Verbose($"Lv. 90 Mission: {missionId}", tag);
                        }

                        if (missionId != 0)
                        {
                            IceLogging.Verbose("We found a mission that we need to complete for one reason or another, going to queue it up for leveling!", tag);
                            LogInfo(missionId);
                            Insert_GrabMissionTask(missionId);
                            return true;
                        }
                        else
                        {
                            IceLogging.Verbose("For one reason or another, we seem to have reached the bottom. Which either means rerolling for specific mission or just rerolling for unlocking purposes", tag);
                            return true;
                        }
                    }
                    else if (mode == ModeSelect.RelicMode)
                    {
                        var relicInfo = CosmicHelper.Cosmic_ClassInfo();
                        var classInfo = relicInfo[job];

                        var jobLv = Player.GetLevel((Job)job);

                        var urgency = new Dictionary<int, float>();
                        IceLogging.Verbose("Relic mode was enabled. So going to do checks to see what exp we need", tag);
                        IceLogging.Verbose($"Current Stage is the max stage? {classInfo.Stage_Current == classInfo.Stage_Next}", tag);
                        IceLogging.Verbose($"Exp Current Tallies: [Check before finding missions]", tag);
                        foreach (var exp in classInfo.CurrentExp)
                        {
                            IceLogging.Verbose($"Kind: [{exp.Key}] | Current: {exp.Value.Current} / Needed: {exp.Value.Needed} | Max: {exp.Value.Max}", tag);
                            if (classInfo.Stage_Current != classInfo.Stage_Next)
                                urgency[exp.Key] = exp.Value.Needed > 0 ? 1f - (float)exp.Value.Current / exp.Value.Needed : 0f;
                            else
                                urgency[exp.Key] = 1f - (float)exp.Value.Current / exp.Value.Max;
                        }
                        if (urgency.Count() == 0 || urgency.All(x => x.Value <= 0))
                        {
                            IceLogging.Verbose("We seem to be still grinding out relic exp (either by choice or cause someone didn't turnin) so we're going to just assign it to go for maxing exp", tag);
                            foreach (var exp in classInfo.CurrentExp)
                                urgency[exp.Key] = 1f - (float)exp.Value.Current / exp.Value.Max;
                        }
                        
                        if (urgency.All(x => x.Value <= 0))
                        {
                            IceLogging.Verbose("We seem to be completed with the exp, but also, I don't have a mode setup for score farming yet. So setting the last exp value to be 1 so it just grabs a mission", tag);
                            var lastEntry = urgency.LastOrDefault();
                            urgency[lastEntry.Key] = 1;
                        }

                        IceLogging.Verbose("Going to check to see if we need to complete a specific mission...", tag);

                        var highestRank = basicMissionList.Max(x => CosmicHelper.SheetMissionDict[x].Rank);

                        bool TryQueueFirstIncomplete(Func<uint, bool> rankFilter, string rankLabel)
                        {
                            var mission = basicMissionList
                                .Where(x => CosmicHelper.SheetMissionDict[x].CompletionStatus < CosmicHelper.Status.Completed)
                                .Where(x => rankFilter(x))
                                .FirstOrDefault();

                            if (mission != 0)
                            {
                                IceLogging.Verbose($"Found an incomplete mission, queuing it now [{rankLabel}]", tag);
                                Insert_GrabMissionTask(mission);
                                return true;
                            }
                            return false;
                        }

                        bool TryQueueFirstNonGold(Func<uint, bool> rankFilter, string rankLabel)
                        {
                            var mission = basicMissionList
                                .Where(x => CosmicHelper.SheetMissionDict[x].CompletionStatus < CosmicHelper.Status.Gold)
                                .Where(x => rankFilter(x))
                                .FirstOrDefault();

                            if (mission != 0)
                            {
                                IceLogging.Verbose($"Found a mission that can be golded, queuing it [{rankLabel}]", tag);
                                Insert_GrabMissionTask(mission);
                                return true;
                            }
                            return false;
                        }

                        bool isDRank(uint x) => CosmicHelper.SheetMissionDict[x].Drank;
                        bool isCRank(uint x) => CosmicHelper.SheetMissionDict[x].CRank;
                        bool isBRank(uint x) => CosmicHelper.SheetMissionDict[x].BRank;

                        if (jobLv >= 100 && highestRank < 4)
                        {
                            IceLogging.Verbose("Hey! Lv 100 Missions still need to be unlocked, so going to check to see what need to do unlock those..", tag);
                            if (highestRank < 2)
                            {
                                IceLogging.Verbose("ABSOLUTELY no ranks are unlocked yet (We're at D Rank Currently) Going to start with that and work our way up.", tag);
                                if (TryQueueFirstIncomplete(isDRank, "D Rank")) return true;
                            }
                            else if (highestRank < 3)
                            {
                                IceLogging.Verbose("Status Report. C Ranks are unlocked, but missing B Ranks, so we're going to aim to complete a C Rank.", tag);
                                if (TryQueueFirstIncomplete(isCRank, "C Rank")) return true;
                            }
                            else
                            {
                                IceLogging.Verbose("Woooooo B Ranks unlocked! Checking to see if there's a gold need to be completed, or just general completions.", tag);

                                var goldCount = CosmicHelper.SheetMissionDict
                                    .Where(x => x.Value.TerritoryId == Player.Territory.RowId)
                                    .Where(x => x.Value.BRank)
                                    .Where(x => x.Value.CompletionStatus == CosmicHelper.Status.Gold)
                                    .Where(x => x.Value.Jobs.Contains(job))
                                    .Count();

                                var completedStatus = CosmicHelper.SheetMissionDict
                                    .Where(x => x.Value.TerritoryId == Player.Territory.RowId)
                                    .Where(x => x.Value.BRank)
                                    .Where(x => x.Value.CompletionStatus > CosmicHelper.Status.None)
                                    .Where(x => x.Value.Jobs.Contains(job))
                                    .Count();
                                IceLogging.Verbose($"Status | Gold [{goldCount} / 3] | Completed: [{completedStatus} / 5]", tag);

                                if (goldCount < 3)
                                {
                                    IceLogging.Verbose($"Missing Gold to help unlock B Ranks... so going to find one with that ideally", tag);
                                    if (TryQueueFirstNonGold(isBRank, "B Rank")) return true;
                                }

                                if (completedStatus < 5)
                                {
                                    IceLogging.Verbose($"We just need to complete more B Rank Missions (So close...).", tag);
                                    if (TryQueueFirstIncomplete(isBRank, "B Rank")) return true;
                                }
                            }
                        }
                        else if (jobLv >= 90 && highestRank < 3)
                        {
                            IceLogging.Verbose("We've hit Lv 90, and we STILL don't have B ranks unlocked, so going to focus that down.", tag);
                            if (TryQueueFirstIncomplete(isCRank, "C Rank")) return true;
                        }
                        else if (jobLv >= 50 && highestRank < 2)
                        {
                            IceLogging.Verbose("We've atleast hit Lv. 50, and Absolutely no ranks unlocked right now besides D Ranks, going to focus on getting that done.", tag);
                            if (TryQueueFirstIncomplete(isDRank, "D Rank")) return true;
                        }


                        IceLogging.Verbose($"Relic Mode, Exp Requirements/Results", tag);
                        foreach (var exp in urgency)
                        {
                            IceLogging.Verbose($"{exp.Key} : Value: {exp.Value:N2}", tag);
                        }

                        var filteredList = missionList.Where(x => CosmicHelper.SheetMissionDict[x].RelicXpInfo.Any(kvp => urgency.ContainsKey(kvp.Key) && urgency[kvp.Key] > 0));
                        if (filteredList.Count() == 0)
                        {
                            if (EzThrottler.Throttle("No viable missions throttle"))
                                IceLogging.Info("We've hit a point where somehow, there's no possible missions that could be grabbed to help you increase your exp to the point it's needed\n" +
                                                "So... this is an interesting spot... check to make sure that you're on the right planet to ", tag);

                            return true;
                        }
                        else
                        {
                            uint bestMissionId = 0;
                            float bestScore = float.NegativeInfinity;

                            foreach (var missionId in filteredList)
                            {
                                if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var sheetInfo) && (basicMissionList.Contains(missionId) || specialMissionList.Contains(missionId)))
                                {
                                    bool allLeveled = true;
                                    foreach (var classes in sheetInfo.Jobs)
                                    {
                                        var classLv = Player.GetLevel((Job)classes);
                                        IceLogging.Verbose($"{classes}: Lv: {classLv}");
                                        allLeveled &= classLv >= sheetInfo.Level;
                                    }

                                    if (!allLeveled)
                                    {
                                        IceLogging.Verbose($"Skipping Mission: {missionId} due to not high enough lv [Player: {jobLv} | Mission: {sheetInfo.Level}].\n");
                                        continue;
                                    }


                                    float score = 0;
                                    foreach (var reward in sheetInfo.RelicXpInfo)
                                    {
                                        if (urgency.TryGetValue(reward.Key, out var info))
                                        {
                                            float contribution = info * reward.Value;
                                            if (contribution > 0)
                                            {
                                                score += contribution;
                                            }
                                        }
                                    }
                                    if (score > bestScore)
                                    {
                                        bestScore = score;
                                        bestMissionId = missionId;
                                    }
                                }
                            }

                            if (bestMissionId != 0)
                            {
                                LogInfo(bestMissionId);
                                Insert_GrabMissionTask(bestMissionId);
                                return true;
                            }
                            else
                            {
                                IceLogging.Info("We've searched through all the missions and none had the exp we needed, which means time to reoll WOOOO!\n" +
                                    $"Best Score: {bestScore}\n" +
                                    $"Best Mission (not): {bestMissionId}", tag);

                                return true;
                            }
                        }
                    }
                    else if (mode is ModeSelect.Standard or ModeSelect.MissionGoldMode)
                    {
                        if (type is MissionTypes.Standard)
                        {
                            IceLogging.Verbose($"Checking Standard missions.\n" +
                                $"Loaded mission Count: {missionList.Count()}\n" +
                                $"Amount of viable missions: {basicMissionList.Count()}", tag);

                            foreach (var missionId in missionList)
                            {
                                if (basicMissionList.Contains(missionId))
                                {
                                    LogInfo(missionId);
                                    Insert_GrabMissionTask(missionId);
                                    return true;
                                }
                            }

                            IceLogging.Info("No missions were found for basic missions tab. Continuing on", tag);
                            return true;
                        }
                        else if (type is MissionTypes.Provisional)
                        {
                            IceLogging.Verbose($"Checking missions for the following mode:\n" +
                                $"Mode: {type}\n" +
                                $"Loaded mission count: {missionList.Count()}\n" +
                                $"Amount of viable missions: {specialMissionList.Count()}", tag);

                            foreach (var missionId in missionList)
                            {
                                if (specialMissionList.Contains(missionId))
                                {
                                    LogInfo(missionId);
                                    Insert_GrabMissionTask(missionId);
                                    return true;
                                }
                            }

                            IceLogging.Verbose($"No missions were found for: {type}. Continuing on", tag);
                            return true;
                        }
                        else if (type is MissionTypes.Critical)
                        {
                            IceLogging.Verbose($"Checking missions for the following mode:\n" +
                                $"Mode: {type}\n" +
                                $"Loaded mission count: {missionList.Count()}\n" +
                                $"Amount of available missions: {criticalMissions.Count()}", tag);

                            foreach (var missionId in missionList)
                            {
                                if (criticalMissions.Contains(missionId))
                                {
                                    LogInfo(missionId);
                                    Insert_GrabMissionTask(missionId);
                                    return true;
                                }
                            }

                            IceLogging.Info("No missions were found for the critical missions, so continuing on", tag);
                            return true;
                        }
                        else if (type is MissionTypes.ToolMastery)
                        {
                            IceLogging.Verbose($"Checking missions for the following mode:\n" +
                                $"Mode: {type}\n" +
                                $"Loaded mission count: {missionList.Count()}\n" +
                                $"Amount of available missions: {masteryMissions.Count()}", tag);

                            foreach (var missionId in missionList)
                            {
                                if (masteryMissions.Contains(missionId))
                                {
                                    LogInfo(missionId);
                                    Insert_GrabMissionTask(missionId);
                                    return true;
                                }
                            }
                            return true;
                        }
                    }
                    else
                    {
                        if (EzThrottler.Throttle("Dumb dumb message"))
                            IceLogging.Verbose("Not a valid mode was found. ICE. FIX THIS", tag);
                    }
                }
            }
            else
            {
                ReOpenMissionUi(tag);
            }

            return false;
        }
        private static void Insert_GrabMissionTask(uint missionId)
        {
            P.TaskManager.Tasks.Clear();

            // Extract materia between missions if spiritbond is ready and next mission is not EX+
            if (C.SelfSpiritbondGather && Task_Spiritbond.IsSpiritbondReadyAny())
            {
                if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var nextMission) && nextMission.Rank < 6)
                {
                    IceLogging.Info($"Next mission rank {nextMission.Rank} is below EX+, extracting materia first");
                    P.TaskManager.Enqueue(() => Task_Spiritbond.ExtractMateria(), "Extracting materia before next mission");
                }
            }

            if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var sheetInfo))
            {
                bool isCollectable = sheetInfo.Attributes.HasFlag(MissionAttributes.Collectables);

            }

            P.TaskManager.EnqueueMulti
                (
                    new(() => CheckForMovementRequired(missionId), "Checking to see if we need to move to mission"),
                    new(() => Mission_ChangeJob(missionId), "Changing to correct job for mission"),
                    new(() => GrabMission(missionId), "Grabbing mission to initate")
                );
        }
        private static bool? Mission_ChangeJob(uint missionId)
        {
            IceLogging.Verbose("Starting to change job");

            var mission = CosmicHelper.SheetMissionDict[missionId];
            var jobId = mission.Jobs.First();

            if ((uint)Player.Job == jobId)
                return true;
            else
            {
                if (EzThrottler.Throttle("Swapping to job for mission"))
                {
                    GearsetHandler.TaskClassChange((Job)jobId);
                    IceLogging.Debug($"Swapping to job: {jobId}");
                }
                return false;
            }
        }
        private static Vector3 randomFishingHole = Vector3.Zero;
        private static bool? CheckForMovementRequired(uint missionId)
        {
            string tag = "[Check Missions: Movement Check]";

            var sheetInfo = CosmicHelper.SheetMissionDict[missionId];
            var missionConfig = C.MissionConfig[missionId];

            IceLogging.Info($"[MoveCheck] id={missionId} attrs=[{sheetInfo.Attributes}] gather={sheetInfo.IsGatherMission} fish={sheetInfo.IsFishMission} unsupported={UnsupportedMissions.Ids.Contains(missionId)} mapPos=({sheetInfo.MapPosition.X},{sheetInfo.MapPosition.Y})", tag);

            if (UnsupportedMissions.Ids.Contains(missionId))
            {
                IceLogging.Info("Mission is currently in manual mode, or not supported. So not going to pathfind to it.", tag);
                return true;
            }
            else if (!P.Navmesh.Installed)
            {
                IceLogging.Error("HEY. YOU DIDN'T READ THE HELP ME PAGE. AND NOW YOU'RE MISSING NAVMESH. So... yeah... if things break this is why");
                return true;
            }
            else if (sheetInfo.IsGatherMission)
            {
                var route = sheetInfo.Gather_MapKey;

                var gatherInfo = GatheringRouteLoader.GetRoute(route);

                if (gatherInfo == null || gatherInfo.Nodes.Count == 0)
                {
                    IceLogging.Error("Hey, so this is actually missing the information for it. So going to just actually add it to the unsupported mission list", tag);
                    UnsupportedMissions.Ids.Add(missionId);
                    return true;
                }
                else
                {
                    var startNode = gatherInfo.Nodes[0];

                    foreach (var node in gatherInfo.Nodes)
                    {
                        if (Player.DistanceTo(node.Position) < 5)
                        {
                            IceLogging.Info("We're close enough to the node! So continuing onto grabbing the mission", tag);
                            return true;
                        }
                    }

                    IceLogging.Verbose("If we've gotten this far, that means we need to figure out a path to go to the node. Doing so now", tag);
                    var randomPosition = Task_NavmeshMove.Gather_RandomFanPosition(startNode);
                    Task_NavmeshMove.Enqueue_NavmeshTask(randomPosition);

                    return true;
                }
            }
            else if (sheetInfo.IsFishMission)
            {
                var location = sheetInfo.MapPosition;
                var territory = sheetInfo.TerritoryId;
                if (!GatheringUtil.MoonFishingLocations.TryGetValue(territory, out var zoneFishing)
                    || !zoneFishing.TryGetValue(location, out var fishingHole)
                    || fishingHole.Count == 0)
                {
                    IceLogging.Error("We've seemed to have ran into a problem with the fishing hole... either it's missing spots, or it doesn't exist. Please report back to me on this with logs leading up to this\n" +
                        $"Mission ID: {missionId} | Map Position: {location} | Moon Territory: {territory}\n" +
                        $"Adding to the unsupported list so it's marked on your side for now", tag);
                    UnsupportedMissions.Ids.Add(missionId);
                    return true;
                }

                var customFishingHole = C.Personal_FishLocation.Where(x => x.MapCoords == location).FirstOrDefault();
                if (customFishingHole != null)
                {
                    var fishingLoc = customFishingHole.WorldPosition;

                    if (fishingLoc != null)
                    {
                        if (Player.DistanceTo(fishingLoc.Value) < 3)
                        {
                            IceLogging.Info($"We have a custom fishing hole set, and we're close to it. {fishingLoc.Value}", tag);
                            randomFishingHole = Vector3.Zero;
                            return true;
                        }
                        else
                        {
                            IceLogging.Verbose($"We have a custom fishing hole set, and we're not within fishing range. Queueing up moving to it: {fishingLoc.Value}");
                            Task_NavmeshMove.Enqueue_NavmeshTask(fishingLoc.Value);
                            randomFishingHole = Vector3.Zero;
                            return true;
                        }
                    }
                }

                foreach (var fishingSpot in fishingHole)
                {
                    if (Player.DistanceTo(fishingSpot.FishingSpot) < 3)
                    {
                        IceLogging.Info($"We've reached our fishing spot! We are current at: {fishingSpot.FishingSpot}", tag);
                        randomFishingHole = Vector3.Zero;
                        return true;
                    }
                }

                if (randomFishingHole == Vector3.Zero)
                {
                    var _random = new Random();
                    var randomIndex = _random.Next(fishingHole.Count);
                    if (EzThrottler.Throttle("Setting fishing hole destination"))
                    {
                        IceLogging.Debug($"Random number spot said we're going to the following fishing hole #: {randomIndex}");
                        randomFishingHole = fishingHole[randomIndex].FishingSpot;
                    }
                }
                else
                {
                    IceLogging.Verbose("If we've gotten this far, that means we need to figure out a path to go to the node. Doing so now");
                    Task_NavmeshMove.Enqueue_NavmeshTask(randomFishingHole);
                    randomFishingHole = Vector3.Zero;
                    return true;
                }
            }
            else if (C.PersonalReturnSpot)
            {
                if (sheetInfo.Attributes.HasFlag(MissionAttributes.Critical))
                {
                    IceLogging.Info($"We are currently aimed to do a critical mission, and we're on a crafter(?) so we're not going to move from our spot", tag);
                    return true;
                }
                else
                {
                    var territory = Player.Territory.RowId;
                    if (C.CrafterLocations.TryGetValue(territory, out var location))
                    {
                        IceLogging.Verbose("If we've gotten this far, that means we need to figure out a path to go to the node. Doing so now");
                        Task_NavmeshMove.Enqueue_NavmeshTask(location);
                        return true;
                    }
                    else
                    {
                        IceLogging.Debug("No location is set for this place, so continuing on", tag);
                        return true;
                    }
                }
            }
            else
            {
                IceLogging.Info("Mission was not a gathering or critical mission. Navmesh moving was not necessary. Moving onto next step", tag);
                return true;
            }

            return false;
        }
        private static int retryCheck = 0;
        private static bool? GrabMission(uint missionId, bool reroll = false)
        {
            string tag = "[Check Missions: Grab Mission]";

            if (CosmicHelper.CurrentLunarMission != 0)
            {
                retryCheck = 0;
                Mission_Settings.ResetNodeCounter();

                if (reroll)
                {
                    SchedulerMain.State = IceState.AbandonMission;
                    Task_AbandonMission.ForceAbandon = true;
                }
                else
                {
                    SchedulerMain.State = IceState.ExecutingMission;
                    Task_AbandonMission.ForceAbandon = false;
                }
                Mission_Settings.nodeTotal = 0;
                P.TaskManager.Tasks.Clear();
                IceLogging.Debug($"State upon exiting: {SchedulerMain.State}");
                return true;
            }
            else
            {
                if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var missionInfo) && missionInfo.IsAddonReady)
                {
                    List<uint> viableMissions = new();
                    viableMissions.Add(missionId);

                    var sheetInfo = CosmicHelper.SheetMissionDict[missionId];

                    // Tool Mastery missions are only readable and grabbable from their own tab (3).
                    if (sheetInfo.IsMaster)
                        CorrectJobTab(sheetInfo.Jobs.First(), CosmicHandler.ToolMasteryTab);

                    var allmissions = CosmicHandler.All_AvailableMissions();
                    if (allmissions.Contains(missionId))
                    {
                        if (EzThrottler.Throttle("Selecting Mission", 1000))
                            InitiateMission(missionId);
                    }
                    else
                    {
                        if (EzThrottler.Throttle("Reporting Current Missions"))
                        {
                            IceLogging.Verbose("We couldn't find the mission? So we're reporting back all visible missions currently", tag);
                            foreach (var mission in allmissions.OrderBy(x => CosmicHelper.SheetMissionDict[x].Rank))
                            {
                                var allMissionInfo = CosmicHelper.SheetMissionDict[mission];
                                string jobs = string.Join(", ", allMissionInfo.Jobs);
                                IceLogging.Verbose($"Job: [{jobs}] | ID: [{mission}] [{allMissionInfo.Name}] | Rank: [{allMissionInfo.Rank}]", tag);
                            }

                            if (sheetInfo.Jobs.Count > 1)
                            {
                                if (EzThrottler.Throttle("Swapping tabs"))
                                {
                                    IceLogging.Verbose("We seem to be on a dual class mission, and it also seems like we're on the gathering class... and missing it from that list. So we're just gonna swap", tag);
                                    CorrectJobTab(sheetInfo.Jobs[0]);
                                }
                            }

                            if (FrameThrottler.Throttle("Counter added", 8))
                                retryCheck += 1;

                            if (retryCheck >= 4)
                            {
                                IceLogging.Verbose($"Mission could no longer be found: {missionId}, retrying the process", tag);
                                retryCheck = 0;
                                P.TaskManager.Tasks.Clear();
                                return true;
                            }
                        }
                    }

                }
                else
                {
                    ReOpenMissionUi(tag);
                }
            }

            return false;
        }
        private static unsafe void InitiateMission(uint missionId)
        {
            var WKSInstance = WKSManager.Instance();
            if (WKSInstance != null)
            {
                WKSInstance->MissionModule->InitiateMission((ushort)missionId);
            }
        }
        private static bool? FindReroll()
        {
            string tag = "[Check Missions: Find Reroll]";

            if (WaitingForSpecialMissions())
            {
                EnterWaitForSpecialMissions(tag);
                return true;
            }

            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var missionInfo) && missionInfo.IsAddonReady)
            {
                var testMission = missionInfo.StellerMissions.FirstOrDefault();
                uint missionToAbandon = 0;
                if (testMission != null)
                {
                    var attribute = CosmicHelper.SheetMissionDict[testMission.MissionId].Attributes;
                    bool nonStandard = attribute.HasFlag(MissionAttributes.ProvisionalSequential) || attribute.HasFlag(MissionAttributes.ProvisionalTimed) 
                                    || attribute.HasFlag(MissionAttributes.ProvisionalWeather) || attribute.HasFlag(MissionAttributes.Critical);
             
                    if (nonStandard)
                    {
                        if (FrameThrottler.Throttle("Selecting proper tab", 8))
                        {
                            missionInfo.BasicMissions();
                        }
                        return false;
                    }
                    else
                    {
                        List<uint> AExRank = new List<uint>();
                        List<uint> ARank = new List<uint>();
                        List<uint> BRank = new List<uint>();
                        List<uint> CRank = new List<uint>();
                        List<uint> DRank = new List<uint>();

                        IceLogging.Info($"We're abandoning mission... so this should be the right tab for this: Rank: {CosmicHelper.SheetMissionDict[testMission.MissionId].Rank}");

                        // Track mission appearance counts
                        foreach (var mission in missionInfo.StellerMissions)
                        {
                            var missionId = mission.MissionId;

                            // Increment appearance count
                            if (!Mission_Settings.missionApperenceCount.ContainsKey(missionId))
                                Mission_Settings.missionApperenceCount[missionId] = 0;
                            Mission_Settings.missionApperenceCount[missionId]++;

                            var rank = CosmicHelper.SheetMissionDict[missionId].Rank;
                            IceLogging.Verbose($"Checking: {missionId} | Rank: {rank}");

                            switch (rank)
                            {
                                case 6: // Master, treated as EX+ tier
                                case 5: AExRank.Add(missionId); break;
                                case 4: ARank.Add(missionId); break;
                                case 3: BRank.Add(missionId); break;
                                case 2: CRank.Add(missionId); break;
                                case 1:
                                default: DRank.Add(missionId); break;
                            }
                        }

                        bool CheckARanks = (LibraryRankCount(MissionKind.Ex) > 0 || LibraryRankCount(MissionKind.A) > 0) && (AExRank.Count > 0 || ARank.Count > 0);
                        bool CheckBRanks = (LibraryRankCount(MissionKind.B) > 0 && BRank.Count > 0);
                        bool CheckCRanks = (LibraryRankCount(MissionKind.C) > 0 && CRank.Count > 0);
                        bool CheckDRanks = (LibraryRankCount(MissionKind.D) > 0 && DRank.Count > 0);

                        IceLogging.Verbose($"[Ex] = {AExRank.Count()}\n" +
                            $"[A] = {ARank.Count()}\n" +
                            $"[B] = {BRank.Count()}\n" +
                            $"[C] = {CRank.Count()}\n" +
                            $"[D] = {DRank.Count()}", tag);

                        List<MissionKind> ranks = new() { MissionKind.Ex, MissionKind.A, MissionKind.B, MissionKind.C, MissionKind.D };
                        var enabledCount = 0;
                        foreach (var rank in ranks)
                        {
                            enabledCount += LibraryRankCount(rank);
                        }

                        if (enabledCount == 0)
                        {
                            IceLogging.Info("We don't have any basic missions enabled under the following class\n" +
                                $"{Mission_Settings.SelectedJob}. So we're just going to clear -> Reset (Assuming we're checking for timed and such)");
                            P.TaskManager.Tasks.Clear();
                            return true;
                        }

                        var random = new Random();
                        void ShuffleList<T>(List<T> list, Random rnd)
                        {
                            for (int i = list.Count - 1; i > 0; i--)
                            {
                                int j = rnd.Next(i + 1);
                                (list[i], list[j]) = (list[j], list[i]);
                            }
                        }

                        ShuffleList(AExRank, random);
                        ShuffleList(ARank, random);
                        ShuffleList(BRank, random);
                        ShuffleList(CRank, random);
                        ShuffleList(DRank, random);

                        // small function to find a frequent mission that might be locking us
                        uint FindFrequentMission(List<uint> missionList, int threshold = 3)
                        {
                            foreach (var missionId in missionList)
                            {
                                if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mission))
                                {
                                    if (mission.Jobs.Count == 2)
                                    {
                                        if (!(Player.GetLevel((Job)mission.Jobs[0]) >= 100 && Player.GetLevel((Job)mission.Jobs[1]) >= 100))
                                            continue;
                                    }
                                }

                                if (Mission_Settings.missionApperenceCount.TryGetValue(missionId, out int count) && count >= threshold)
                                {
                                    return missionId;
                                }
                            }
                            return 0;
                        }

                        if (CheckARanks)
                        {
                            // Check for frequent missions in A/AEx ranks first
                            uint frequentAEx = FindFrequentMission(AExRank, Mission_Settings.rerollThreshold);
                            uint frequentA = FindFrequentMission(ARank, Mission_Settings.rerollThreshold);

                            if (AExRank.Count > 2)
                            {
                                if (frequentAEx != 0)
                                {
                                    missionToAbandon = frequentAEx;
                                    IceLogging.Debug($"Abandoning frequently appearing AEX mission (appeared {Mission_Settings.missionApperenceCount[frequentAEx]} times)", tag);
                                    Mission_Settings.previousAbandonRank = 5;
                                }
                                else
                                {
                                    IceLogging.Debug($"Only AEX Rank missions are available. Forcing an AEX rank to be accepted");
                                    missionToAbandon = AExRank.First();
                                    Mission_Settings.previousAbandonRank = 5;
                                }
                            }
                            else if (ARank.Count > 2)
                            {
                                if (frequentA != 0)
                                {
                                    missionToAbandon = frequentA;
                                    IceLogging.Debug($"Abandoning frequently appearing A mission (appeared {Mission_Settings.missionApperenceCount[frequentA]} times)", tag);
                                    Mission_Settings.previousAbandonRank = 4;
                                }
                                else
                                {
                                    IceLogging.Debug($"Only A Rank missions are available. Forcing an A rank to be accepted", tag);
                                    missionToAbandon = ARank.First();
                                    Mission_Settings.previousAbandonRank = 4;
                                }
                            }
                            else
                            {
                                if (Mission_Settings.previousAbandonRank == 5)
                                {
                                    if (frequentA != 0)
                                    {
                                        missionToAbandon = frequentA;
                                        IceLogging.Debug($"Abandoning frequently appearing A mission (appeared {Mission_Settings.missionApperenceCount[frequentA]} times)", tag);
                                        Mission_Settings.previousAbandonRank = 4;
                                    }
                                    else
                                    {
                                        missionToAbandon = ARank.First();
                                        IceLogging.Debug($"Abandoning Rank 4 Mission.");
                                        Mission_Settings.previousAbandonRank = 4;
                                    }
                                }
                                else if (Mission_Settings.previousAbandonRank == 4)
                                {
                                    if (frequentAEx != 0)
                                    {
                                        missionToAbandon = frequentAEx;
                                        IceLogging.Debug($"Abandoning frequently appearing AEX mission (appeared {Mission_Settings.missionApperenceCount[frequentAEx]} times)", tag);
                                        Mission_Settings.previousAbandonRank = 5;
                                    }
                                    else
                                    {
                                        missionToAbandon = AExRank.First();
                                        IceLogging.Debug($"Abandoning Rank 5 Mission", tag);
                                        Mission_Settings.previousAbandonRank = 5;
                                    }
                                }
                                else
                                {
                                    missionToAbandon = ARank.First();
                                    IceLogging.Debug($"Starting off w/ abandoning an A rank", tag);
                                    Mission_Settings.previousAbandonRank = 4;
                                }
                            }
                        }
                        else if (CheckBRanks)
                        {
                            uint frequentB = FindFrequentMission(BRank, Mission_Settings.rerollThreshold);
                            if (frequentB != 0)
                            {
                                missionToAbandon = frequentB;
                                IceLogging.Debug($"Abandoning frequently appearing B mission (appeared {Mission_Settings.missionApperenceCount[frequentB]} times)", tag);
                            }
                            else
                            {
                                missionToAbandon = BRank.First();
                            }
                            Mission_Settings.previousAbandonRank = 3;
                        }
                        else if (CheckCRanks)
                        {
                            uint frequentC = FindFrequentMission(CRank, Mission_Settings.rerollThreshold);
                            if (frequentC != 0)
                            {
                                missionToAbandon = frequentC;
                                IceLogging.Debug($"Abandoning frequently appearing C mission (appeared {Mission_Settings.missionApperenceCount[frequentC]} times)", tag);
                            }
                            else
                            {
                                missionToAbandon = CRank.First();
                            }
                            Mission_Settings.previousAbandonRank = 2;
                        }
                        else if (CheckDRanks)
                        {
                            uint frequentD = FindFrequentMission(DRank, Mission_Settings.rerollThreshold);
                            if (frequentD != 0)
                            {
                                missionToAbandon = frequentD;
                                IceLogging.Debug($"Abandoning frequently appearing D mission (appeared {Mission_Settings.missionApperenceCount[frequentD]} times)", tag);
                            }
                            else
                            {
                                missionToAbandon = DRank.First();
                            }
                            Mission_Settings.previousAbandonRank = 1;
                        }
                        else if (Mission_Settings.Mode == ModeSelect.LevelMode)
                        {
                            if (MissionLibrary[MissionKind.B].Count > 0)
                            {
                                IceLogging.Debug("Leveling mode is active. Need to find a valid C or D Rank mission", tag);
                                var mission = missionInfo.StellerMissions.Where(m => CosmicHelper.SheetMissionDict[m.MissionId].Level == 50).FirstOrDefault();

                                if (mission != null)
                                {
                                    missionToAbandon = mission.MissionId;
                                }
                                else
                                {
                                    mission = missionInfo.StellerMissions.Where(m => CosmicHelper.SheetMissionDict[m.MissionId].Level == 10).FirstOrDefault();

                                    if (mission != null)
                                        missionToAbandon = mission.MissionId;
                                }
                            }
                            else if (MissionLibrary[MissionKind.C].Count > 0)
                            {
                                var mission = missionInfo.StellerMissions.Where(m => CosmicHelper.SheetMissionDict[m.MissionId].Level == 10).FirstOrDefault();
                                if (mission != null)
                                    missionToAbandon = mission.MissionId;
                            }
                        }

                        if (missionToAbandon != 0)
                        {
                            P.TaskManager.EnqueueMulti
                                (
                                    new(() => Mission_ChangeJob(missionToAbandon)),
                                    new(() => GrabMission(missionToAbandon, true))
                                );
                            return true;
                        }
                    }
                }
                else
                {
                    if (FrameThrottler.Throttle("Selecting proper tab", 8))
                    {
                        missionInfo.BasicMissions();
                    }
                    return false;
                }
            }
            else
            {
                ReOpenMissionUi(tag);
            }

            return false;
        }
        private static unsafe bool MissionGolded(uint id)
        {
            var managerPtr = WKSManager.Instance();
            if (managerPtr == null) return false;

            var isGolded = managerPtr->IsMissionGolded(id);

            return isGolded;
        }

        /// <summary>A mission we have finished before, but never at gold - the hard ones.</summary>
        private static unsafe bool MissionCompletedWithoutGold(uint id)
        {
            var managerPtr = WKSManager.Instance();
            if (managerPtr == null) return false;

            if (managerPtr->IsMissionGolded(id))
                return false;

            if (managerPtr->IsMissionCompleted(id))
                return true;

            // Fall back on our own turn-in history for missions the game no longer flags (or that were
            // run before the completion flag was tracked).
            return C.MissionConfig.TryGetValue(id, out var config)
                && config.GoldCompletions == 0
                && config.TotalCompletions > 0;
        }

        /// <summary>Missions whose level requirement the player meets (Min of both jobs on dual-job missions).</summary>
        private static bool LevelRequirementMet(CosmicHelper.CosmicInfo mission)
        {
            if (mission.Jobs.Count == 0)
                return false;

            var jobLevel = Math.Min(Player.GetLevel((Job)mission.Jobs.First()), Player.GetLevel((Job)mission.Jobs.Last()));
            return jobLevel >= mission.Level;
        }

        // - - - Gold Completion helpers - - - //

        /// <summary>
        /// Gold Completion works across every class in the configured job priority, so a mission counts as
        /// long as one of its jobs is in that list. Off-class criticals/provisionals stay behind their
        /// existing toggles for jobs that aren't in the list at all.
        /// </summary>
        private static bool GoldJobAllowed(CosmicHelper.CosmicInfo mission)
        {
            var jobPool = ActiveJobPool();
            if (mission.Jobs.Any(job => jobPool.Contains(job)))
                return true;

            if (mission.IsCritical)
                return C.GrindOffClassRedAlert;

            if (mission.IsProvisional)
                return C.GrindAllProvisionals;

            return false;
        }

        private static List<uint> _jobPoolCache = new();
        private static long _jobPoolCacheStamp = 0;

        /// <summary>
        /// Jobs the cross-job pick is allowed to roam over. An agenda entry pins its goal to a single class
        /// ("gold every mission on WVR"), so a mode running under the agenda stays on that job; a standalone
        /// run covers every class in the configured job priority order that we can actually equip.
        /// </summary>
        private static List<uint> ActiveJobPool()
        {
            if (C.SelectedMode == ModeSelect.AgendaMode)
                return new List<uint> { Mission_Settings.SelectedJob };

            // Walking every gearset per mission would be wasteful, and gearsets barely ever change mid-run.
            var now = Environment.TickCount64;
            if (_jobPoolCacheStamp != 0 && now - _jobPoolCacheStamp < 1000)
                return _jobPoolCache;

            var usable = C.JobPrio.Where(job => GearsetHandler.HasGearset((Job)job)).ToList();

            // Something's off with the gearset read - better to try every job than to grind to a halt.
            _jobPoolCache = usable.Count > 0 ? usable : C.JobPrio.ToList();
            _jobPoolCacheStamp = now;
            return _jobPoolCache;
        }

        private static bool GoldDeprioritisingHardMissions() =>
            Mission_Settings.Mode == ModeSelect.MissionGoldMode && C.Gold_HardMissionsLast;

        /// <summary>
        /// 0 = still worth doing now, 1 = already completed without a gold, so save it for last.
        /// Applies to every mission type. Weather/timed/critical missions are on the game's clock, but they
        /// rotate - passing on a hard one means taking a different one now, not losing the type - and those
        /// are exactly the missions that can eat a whole run without ever producing a gold.
        /// </summary>
        private static int GoldDifficultyTier(uint missionId)
        {
            if (!GoldDeprioritisingHardMissions())
                return 0;

            if (MissionCompletedWithoutGold(missionId))
                return 1;

            // An already-gold mission is only in the pool as a stepping stone to a later chain step, and
            // starting that chain means running every step again - so it is exactly as hard as the step it
            // is being run for.
            if (MissionGolded(missionId))
            {
                var target = NextUngoldedChainStep(missionId);
                return target != 0 && MissionCompletedWithoutGold(target) ? 1 : 0;
            }

            return 0;
        }

        // - - - Sequence chains - - - //

        /// <summary>The first step after this one that still needs a gold, or 0 if the chain is done.</summary>
        private static uint NextUngoldedChainStep(uint missionId)
        {
            if (!CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mission))
                return 0;

            // SequenceMissions_Next is built nearest-first, so this walks the chain in running order.
            foreach (var next in mission.SequenceMissions_Next)
            {
                if (!MissionGolded(next))
                    return next;
            }

            return 0;
        }

        /// <summary>Whether this step, or something further down its chain, still needs a gold.</summary>
        private static bool SequenceChainWanted(uint missionId) =>
            !MissionGolded(missionId) || NextUngoldedChainStep(missionId) != 0;

        /// <summary>
        /// A chain step that is ready to run right now: it has earlier steps, every one of them is gold, and
        /// the chain still has a gold left in it. The game only offers such a step because we just cleared
        /// the one before it, and it goes away again the moment we take something else - so these are picked
        /// ahead of the configured priorities. A chain sitting on a step we could not gold is not carried on
        /// with; it goes back through normal selection instead.
        /// </summary>
        private static bool SequenceChainReady(uint missionId)
        {
            if (!CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mission))
                return false;
            if (mission.SequenceMissions_Previous.Count == 0)
                return false;
            if (!mission.SequenceMissions_Previous.All(previous => MissionGolded(previous)))
                return false;

            return SequenceChainWanted(missionId);
        }

        /// <summary>
        /// A chain step the run we just finished unlocked. Only these are taken out of priority order: a
        /// follow-up that has been sitting on the board on its own gets no such pass, or a chain we can
        /// never gold would be retried every time it came round.
        /// </summary>
        private static bool IsLiveSequenceFollowUp(uint missionId)
        {
            var finished = Task_TurninMission.PreviousMissionId;
            if (finished == 0 || finished == missionId)
                return false;

            if (!CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mission))
                return false;

            return mission.SequenceMissions_Previous.Contains(finished) && SequenceChainReady(missionId);
        }

        /// <summary>
        /// Whether the mission we just finished should be handing us a follow-up step. Used to hold off on
        /// side activities and to give the board a moment to offer it.
        /// </summary>
        internal static bool SequenceContinuationPending()
        {
            var finished = Task_TurninMission.PreviousMissionId;
            if (finished == 0)
                return false;
            if (!CosmicHelper.SheetMissionDict.TryGetValue(finished, out var mission))
                return false;

            // The chain is only worth carrying on with while the step underneath it came out gold.
            if (!MissionGolded(finished))
                return false;

            var next = mission.SequenceMissions_Next.FirstOrDefault();
            if (next == 0 || !CosmicHelper.SheetMissionDict.TryGetValue(next, out var nextMission))
                return false;

            return LevelRequirementMet(nextMission) && SequenceChainWanted(next);
        }

        /// <summary>Suffix for pick logs, so a chain step is recognisable in the log even when it was picked
        /// by the normal priority order rather than by the chain pass.</summary>
        private static string SequenceNote(uint missionId)
        {
            if (!CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mission) || !mission.IsSequence)
                return string.Empty;

            var step = mission.SequenceMissions_Previous.Count + 1;
            var total = step + mission.SequenceMissions_Next.Count;
            return $" | sequence step {step}/{total}";
        }

        /// <summary>
        /// One line per finished sequence mission saying which way the chain went: carried on, dropped
        /// because the step wasn't gold, or done. Silent for everything that isn't part of a chain.
        /// </summary>
        private static void LogSequenceChainState(uint finished, string tag)
        {
            if (finished == 0 || !CosmicHelper.SheetMissionDict.TryGetValue(finished, out var mission))
                return;

            var next = mission.SequenceMissions_Next.FirstOrDefault();
            if (next == 0)
                return;

            if (!MissionGolded(finished))
            {
                IceLogging.Info($"Sequence chain dropped: [{finished}] did not come out gold, so its follow-up [{next}] " +
                    "goes back through normal mission selection.", tag);
                return;
            }

            if (!SequenceChainWanted(next))
            {
                IceLogging.Info($"Sequence chain done: nothing after [{finished}] still needs a gold.", tag);
                return;
            }

            IceLogging.Info($"Sequence chain live: [{finished}] is gold, follow-up [{next}] is next in line.", tag);
        }

        private static uint _sequenceWaitMission = 0;
        private static int _sequenceWaitAttempts = 0;

        /// <summary>
        /// The board does not always list a sequence follow-up the instant its prerequisite is turned in.
        /// Give it a few seconds before the picker moves on, otherwise a reroll throws the chain away.
        /// </summary>
        private static bool? WaitForSequenceFollowUp()
        {
            string tag = "[Check Missions: Sequence Follow-up]";

            var finished = Task_TurninMission.PreviousMissionId;
            if (finished != _sequenceWaitMission)
            {
                _sequenceWaitMission = finished;
                _sequenceWaitAttempts = 0;
                LogSequenceChainState(finished, tag);
            }

            if (CosmicHelper.CurrentLunarMission != 0 || !SequenceContinuationPending())
                return true;

            var next = CosmicHelper.SheetMissionDict[finished].SequenceMissions_Next.First();
            var job = CosmicHelper.SheetMissionDict[next].Jobs.First();

            // Chain steps are normally provisional, but check the whole board so a basic-tab step counts too.
            if (CorrectJobTab(job, 0) && CosmicHandler.All_AvailableMissions().Contains(next))
                return true;

            if (_sequenceWaitAttempts >= 5)
            {
                IceLogging.Info($"Sequence follow-up [{next}] never showed up on the board after finishing [{finished}]. " +
                    "Carrying on with normal mission selection.", tag);
                return true;
            }

            if (EzThrottler.Throttle("Waiting on sequence follow-up", 500))
            {
                _sequenceWaitAttempts++;
                IceLogging.Verbose($"Waiting for sequence follow-up [{next}] to appear on the board " +
                    $"(attempt {_sequenceWaitAttempts})", tag);
            }

            return false;
        }

        /// <summary>
        /// Tiebreaker inside a difficulty tier: the more non-gold completions a mission has racked up, the
        /// further back it goes.
        /// </summary>
        private static int GoldAttemptPenalty(uint missionId)
        {
            if (!GoldDeprioritisingHardMissions())
                return 0;

            if (!C.MissionConfig.TryGetValue(missionId, out var config))
                return 0;

            return Math.Max(0, config.TotalCompletions - config.GoldCompletions);
        }

        /// <summary>
        /// Golded missions that later steps of an ungolded sequence chain still depend on. Rebuilt with the
        /// mission library so the pick path can check membership cheaply.
        /// </summary>
        private static readonly HashSet<uint> GoldSequencePrereqs = new();

        private static void RefreshGoldSequencePrereqs()
        {
            GoldSequencePrereqs.Clear();

            foreach (var (missionId, mission) in CosmicHelper.SheetMissionDict)
            {
                if (mission.TerritoryId != Player.Territory.RowId)
                    continue;
                if (mission.SequenceMissions_Previous.Count == 0)
                    continue;
                if (MissionGolded(missionId) || !GoldJobAllowed(mission) || !LevelRequirementMet(mission))
                    continue;

                foreach (var prereq in mission.SequenceMissions_Previous)
                    GoldSequencePrereqs.Add(prereq);
            }
        }

        /// <summary>
        /// Whether any class still has a standard mission that we've never completed below gold. Those are
        /// the ones worth rerolling for; once they're gone, the previously-failed missions are all that's
        /// left to grind. Deliberately standard-only: a rare weather mission we've never attempted must not
        /// hold the gate open forever, since no amount of rerolling makes it appear.
        /// </summary>
        private static bool GoldEasyMissionsRemain() =>
            ActiveJobPool().Any(job => GoldRemainingBasicMissions(job).Any(id => GoldDifficultyTier(id) == 0));

        /// <summary>Standard-tab missions on this moon that the given job still needs a gold on.</summary>
        private static List<uint> GoldRemainingBasicMissions(uint job)
        {
            return CosmicHelper.SheetMissionDict
                .Where(x => x.Value.TerritoryId == Player.Territory.RowId)
                .Where(x => !x.Value.IsProvisional && !x.Value.IsCritical && !x.Value.IsMaster)
                .Where(x => x.Value.Jobs.Contains(job))
                .Where(x => LevelRequirementMet(x.Value))
                .Where(x => !MissionGolded(x.Key))
                .Select(x => x.Key)
                .ToList();
        }

        /// <summary>
        /// Nothing was grabbable, so we're about to reroll the board. Reroll on the highest priority job that
        /// still has missions to gold - preferring one with missions we haven't already failed to gold -
        /// instead of burning rerolls on a class that is already finished.
        /// </summary>
        private static bool? AlignGoldRerollJob()
        {
            string tag = "[Check Missions: Gold Reroll Job]";

            if (Mission_Settings.Mode != ModeSelect.MissionGoldMode)
                return true;

            // A mission was already grabbed earlier in the queue; nothing to reroll for.
            if (CosmicHelper.CurrentLunarMission != 0)
                return true;

            uint bestJob = 0;
            int bestTier = int.MaxValue, bestJobPrio = int.MaxValue;

            foreach (var job in ActiveJobPool())
            {
                var remaining = GoldRemainingBasicMissions(job);
                if (remaining.Count == 0)
                    continue;

                int tier = remaining.Min(id => GoldDifficultyTier(id));
                int jobPrio = C.JobPrio.IndexOf(job);

                if (tier < bestTier || (tier == bestTier && jobPrio < bestJobPrio))
                {
                    bestTier = tier;
                    bestJobPrio = jobPrio;
                    bestJob = job;
                }
            }

            if (bestJob == 0)
                return true;

            if (bestJob != Mission_Settings.SelectedJob)
            {
                IceLogging.Info($"Rerolling on job [{bestJob}] instead of [{Mission_Settings.SelectedJob}]: " +
                    $"it still has missions that need a gold{(bestTier > 0 ? " (all of them previously completed without one)" : string.Empty)}.", tag);
                Mission_Settings.SelectedJob = bestJob;
            }

            CorrectJobTab(Mission_Settings.SelectedJob, 0);
            return true;
        }

        /// <summary>
        /// Rank counts backing the reroll decisions. Gold Completion fills the library from every class, but
        /// a reroll only ever happens on the board of one job, so the count is narrowed to that job.
        /// </summary>
        private static int LibraryRankCount(MissionKind rank)
        {
            var missions = MissionLibrary[rank];

            if (Mission_Settings.Mode != ModeSelect.MissionGoldMode)
                return missions.Count;

            var job = Mission_Settings.SelectedJob;
            return missions.Count(id => CosmicHelper.SheetMissionDict.TryGetValue(id, out var m) && m.Jobs.Contains(job));
        }

        // functions that are used across things
        private static unsafe bool CorrectJobTab(uint job, byte categoryTab = 0)
        {
            var agent = AgentWKSMission.Instance();
            if (agent == null)
            {
                if (EzThrottler.Throttle("AgentWKSMission Error", 2000))
                    IceLogging.Error("AgentWKSMission has returned null. CS code might need an update...", "Task: Check Mission | Open Job Tab");

                return false;
            }

            return AgentWKSMissionEx.SetSelectedJobTab(agent, (byte)job, categoryTab);
        }
        private static void Notes()
        {
            /*
             * This is kind of my place to just... figure out how tf the logic is going to work. 
             * Right now, the logic is 
             * 1: Store all the missions in the dictionary.
             *   - This doesn't matter if what kind of mode, we're just storing it. It should... allow for re-rolling of missions even when in relic mode on weird edge cases (aka, only selected missions for some reason)
             * 2: Added in logic for checking each tab, and adding drone checking somewhere in there. 
             *   - The way this works should be: Check each tab for a mission. If one exist in that place, we're just going to clear the queue -> just proceed to the grab mission task 
             *   - If not, then it continues onto the next kind
             *   - Drone mode is in there as a general "Hey, we gonna check to see if we can open a drone/have a drone running -> find it between missions (this is nice cause it allows users to dictate when they're going to go looking for a box in case of weather. red alert...)
             * 3: If we get to this point in the queue and we STILL haven't grabbed a mission, it means that we need to reroll for one. 
             *   - Logic will be the same here as before. Check to see what ones need to be rerolled if possible
             *
            */
        }
    }
}
