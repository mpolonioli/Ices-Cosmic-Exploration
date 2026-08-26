using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.ConfigFiles;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static ICE.Utilities.Cosmic_Helper.CosmicMissionAvailability;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// Gold Completion's cross-planet hop: when the moon we're on has nothing left that can be worked on
    /// right now, fly to the hub that does instead of idling on the mission board.
    /// <para>
    /// The game drops the plugin over the loading screen (no player, no cosmic zone), so the hop is split
    /// in two: everything up to picking the destination runs as a normal task chain, and
    /// <see cref="TryResumeAfterArrival"/> - driven from <see cref="PlayerHandlers"/> - picks the run back up
    /// on the other side.
    /// </para>
    /// </summary>
    internal static class Task_PlanetTravel
    {
        private const string Tag = "[Planet Travel]";

        /// <summary>Real minutes the hop itself costs (walk to the NPC, cutscene, loading screen).</summary>
        private const double TravelCostMinutes = 2;

        /// <summary>Don't bounce straight back off a hub we just landed on.</summary>
        private const long DwellMs = 90_000;

        private const long WarpTimeoutMs = 180_000;
        private const long ResumeTimeoutMs = 300_000;

        /// <summary>Territory we're flying to (0 when we're not).</summary>
        internal static uint Destination { get; private set; }

        /// <summary>Set once the destination is picked; survives the plugin stopping itself over the load.</summary>
        internal static uint PendingArrival { get; private set; }

        private const long NpcSearchTimeoutMs = 180_000;

        /// <summary>How long the player gets to finish the trip by hand when the window can't be driven.</summary>
        private const long ManualPickTimeoutMs = 300_000;

        private static long _pendingStamp;
        private static long _lastArrival;
        private static long _interactStamp;
        private static long _searchStamp;
        private static long _stuckStamp;

        internal static bool Travelling => Destination != 0 || PendingArrival != 0;

        // - - - Deciding to go - - - //

        /// <summary>
        /// Nothing to do here right now: look for a hub with an open weather/timed window (or plain missions
        /// left to gold) and start flying there. Returns false when staying put is the better call.
        /// </summary>
        internal static bool TryStartTravel(string callerTag)
        {
            if (!C.Gold_CrossPlanetTravel)
                return false;
            if (Mission_Settings.Mode != ModeSelect.MissionGoldMode)
                return false;
            // Agenda goals are counted on the moon you're standing on ("gold every WVR mission here"), so a
            // hop would quietly move the goalposts. Leave the agenda to the player's planet order.
            if (C.SelectedMode == ModeSelect.AgendaMode)
                return false;
            if (Travelling)
                return false;
            if (!PlayerHelper.IsInCosmicZone())
                return false;
            if (CosmicHelper.CurrentLunarMission != 0)
                return false;
            if (_lastArrival != 0 && Environment.TickCount64 - _lastArrival < DwellMs)
                return false;

            var currentTerritory = Player.Territory.RowId;
            if (!CosmicMoonRegistry.TryGetMoon(currentTerritory, out var currentMoon))
                return false;

            // The caller only gets here once nothing on this moon can be started right now, so the local
            // reading deliberately ignores always-available missions - they're already known to be a dead end.
            var here = Evaluate(currentMoon, WorthFlyingFor);
            var hereEta = Eta(here, countAnytimeWork: false);

            MoonGoldStatus? best = null;
            var bestEta = double.MaxValue;

            foreach (var moon in CosmicMoonRegistry.All)
            {
                if (moon.TerritoryId == currentTerritory)
                    continue;
                if (!TravelAllowed(moon))
                    continue;

                var status = Evaluate(moon, WorthFlyingFor);
                if (status.Remaining == 0)
                    continue;

                var eta = Eta(status);
                if (eta >= bestEta)
                    continue;

                bestEta = eta;
                best = status;
            }

            if (best == null)
            {
                if (EzThrottler.Throttle("Planet travel: nothing anywhere", 60_000))
                    IceLogging.Info("No other hub has missions left that we're allowed to gold, staying put.", Tag);
                return false;
            }

            // Nothing on the forecast is worth flying for: either the closest window anywhere is further out
            // than we're willing to travel for, or this moon is already the best seat in the house. Both are
            // cases where the rotation - which trades a known wait for red alert coverage we can't see from
            // here - gets its say before we settle in for another wait.
            var nothingCloseEnough = bestEta > C.Gold_TravelMaxWaitMinutes;
            var hereIsAsGood = bestEta + TravelCostMinutes >= hereEta;

            if (nothingCloseEnough || hereIsAsGood)
            {
                // Every hub looks the same because the work left on all of them is red alerts, which can't
                // be seen from off-world. Camping one moon then only ever catches that moon's alerts, so
                // move along on a timer instead.
                if (TryRotate(currentMoon, here, out var rotation))
                {
                    Depart(rotation, $"Nothing is up anywhere right now - moving on to {rotation.Moon.DisplayName}: " +
                        $"{Describe(rotation, Eta(rotation))}", callerTag);
                    return true;
                }

                if (nothingCloseEnough)
                {
                    if (EzThrottler.Throttle("Planet travel: everything too far out", 60_000))
                        IceLogging.Info($"Closest window anywhere else is {CosmicMoonRegistry.GetDisplayName(best.Moon.TerritoryId)} " +
                            $"in {bestEta:N0} min, which is past the {C.Gold_TravelMaxWaitMinutes} min travel cutoff. Waiting here instead.", Tag);
                }
                else if (EzThrottler.Throttle("Planet travel: here is better", 60_000))
                {
                    IceLogging.Verbose($"Staying on {currentMoon.DisplayName}: next window here is ~{hereEta:N0} min out, " +
                        $"{best.Moon.DisplayName} is ~{bestEta:N0} min (+{TravelCostMinutes:N0} min of travel).", Tag);
                }

                return false;
            }

            Depart(best, $"Nothing left to gold on {currentMoon.DisplayName} right now - flying to " +
                $"{best.Moon.DisplayName}: {Describe(best, bestEta)}", callerTag);
            return true;
        }

        private static void Depart(MoonGoldStatus target, string announcement, string callerTag)
        {
            Destination = target.Moon.TerritoryId;
            _interactStamp = Environment.TickCount64;

            IceLogging.Info(announcement, Tag);
            IceLogging.Info($"Cross-planet hop from {callerTag}: {CosmicMoonRegistry.GetDisplayName(Player.Territory.RowId)} " +
                $"-> {target.Moon.DisplayName} ({target.Remaining} mission(s) left to gold there)", Tag);

            SchedulerMain.State = IceState.PlanetTravel;
        }

        /// <summary>
        /// The rotation: when the only work left anywhere is red alerts (or sequence chains that never show
        /// up), sitting still is a bet on one moon. After <see cref="Config.Gold_TravelRotateMinutes"/> on a
        /// hub with nothing on its board to gold, move to whichever hub with work left has been left alone
        /// the longest.
        /// </summary>
        private static bool TryRotate(CosmicMoonDefinition currentMoon, MoonGoldStatus here, out MoonGoldStatus target)
        {
            target = null!;

            if (C.Gold_TravelRotateMinutes <= 0)
                return false;

            // A window is open here, or there is still something rerollable on this moon - the timer never
            // overrides work we can actually do where we are.
            if (here.OpenMissionId != 0 || here.RemainingAnytime > 0)
                return false;

            // Same rule against the live board rather than the forecast: one mission we could gold sitting
            // there is reason enough to stay, whatever the clock says.
            if (AnyMissionAvailableHere())
            {
                if (EzThrottler.Throttle("Planet travel: work on this board", 60_000))
                    IceLogging.Verbose($"Not rotating off {currentMoon.DisplayName} - the board still has something to gold.", Tag);
                return false;
            }

            // Something predictable is coming here soon enough to be worth waiting out.
            if (here.NextMissionId != 0 && here.NextMinutes <= C.Gold_TravelRotateMinutes)
                return false;

            // A red alert being up on this moon is deliberately not a reason to stay. The alert that fires
            // here need not be one we still owe a gold, and AnyMissionAvailableHere above already answers
            // the only question that matters - is there something on this board we could be golding?
            var minutesHere = MinutesOnCurrentMoon();
            if (minutesHere < C.Gold_TravelRotateMinutes)
                return false;

            var candidates = CosmicMoonRegistry.All
                .Where(moon => moon.TerritoryId != currentMoon.TerritoryId)
                .Where(TravelAllowed)
                .Select(moon => Evaluate(moon, WorthFlyingFor))
                .Where(status => status.Remaining > 0)
                .ToList();

            if (candidates.Count == 0)
                return false;

            // Any mission still owed a gold is reason enough to go and sit there - what kind it is doesn't
            // come into it. Nothing is runnable here, so any hub with work left beats standing still.
            target = candidates
                // Longest since we last set foot there, so the rotation actually goes round.
                .OrderBy(status => LastVisit(status.Moon.TerritoryId))
                .ThenByDescending(status => status.Remaining)
                .First();

            IceLogging.Info($"{minutesHere:N0} min on {currentMoon.DisplayName} with nothing on the board to gold - " +
                $"rotating to {target.Moon.DisplayName} ({target.Remaining} mission(s) left to gold there).", Tag);
            return true;
        }

        /// <summary>
        /// Anything on this moon's mission board right now that still needs a gold - read from the live
        /// board (basic, provisional, critical and tool mastery tabs), not from the forecast.
        /// </summary>
        private static bool AnyMissionAvailableHere()
        {
            var territory = Player.Territory.RowId;

            foreach (var missionId in CosmicHandler.All_AvailableMissions())
            {
                if (!CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mission))
                    continue;
                if (mission.TerritoryId != territory)
                    continue;
                if (WorthFlyingFor(missionId, mission))
                    return true;
            }

            return false;
        }

        private static bool RedAlertBrewing()
        {
            var eventInfo = CosmicHandler.EventInfo();
            if (eventInfo == null)
                return false;

            return eventInfo.Value.wksEvent
                is CosmicHandler.WKSEvents.RedAlert_Incoming
                or CosmicHandler.WKSEvents.RedAlert_Progressing;
        }

        /// <summary>Visit stamps per moon, so the rotation can pick the one we've ignored longest.</summary>
        private static readonly Dictionary<uint, long> Visits = new();

        private static void StampVisit(uint territoryId)
        {
            if (territoryId != 0)
                Visits[territoryId] = Environment.TickCount64;
        }

        private static long LastVisit(uint territoryId) => Visits.TryGetValue(territoryId, out var stamp) ? stamp : 0;

        private static double MinutesOnCurrentMoon()
        {
            var territory = Player.Territory.RowId;
            if (!Visits.TryGetValue(territory, out var stamp))
            {
                // First time we've looked at this moon this session - start its clock now rather than
                // treating "unknown" as "been here forever".
                StampVisit(territory);
                return 0;
            }

            return (Environment.TickCount64 - stamp) / 60_000.0;
        }

        /// <summary>
        /// Gold Completion's own filter, minus anything ICE can't actually run - flying somewhere for a
        /// mission that ends in manual mode is worse than staying put.
        /// </summary>
        private static bool WorthFlyingFor(uint missionId, CosmicHelper.CosmicInfo mission) =>
            !UnsupportedMissions.Ids.Contains(missionId)
            && Task_CheckMissions.GoldMissionWanted(missionId, mission);

        /// <summary>Per-moon reading behind the hop decision, for the debug table.</summary>
        internal static MoonGoldStatus Snapshot(CosmicMoonDefinition moon) => Evaluate(moon, WorthFlyingFor);

        /// <inheritdoc cref="MinutesOnCurrentMoon"/>
        internal static double MinutesHere() => MinutesOnCurrentMoon();

        /// <inheritdoc cref="RedAlertBrewing"/>
        internal static bool RedAlertHere() => RedAlertBrewing();

        /// <inheritdoc cref="Eta(MoonGoldStatus, bool)"/>
        internal static double SnapshotEta(MoonGoldStatus status, bool isCurrentMoon) => Eta(status, !isCurrentMoon);

        /// <summary>Real minutes until this moon has something we can actually start.</summary>
        private static double Eta(MoonGoldStatus status, bool countAnytimeWork = true)
        {
            if (status.Remaining == 0)
                return double.MaxValue;

            // Standard / tool mastery missions are always on the board - reroll and go.
            if (countAnytimeWork && status.RemainingAnytime > 0)
                return 0;

            if (status.OpenMissionId != 0 && status.OpenMinutesLeft >= C.Gold_TravelMinWindowMinutes)
                return 0;

            var eta = status.NextMissionId != 0 ? status.NextMinutes : double.MaxValue;

            // A red alert can pop at any time but can't be seen from another moon, so it gets a nominal wait
            // rather than counting as "nothing here".
            if (status.RemainingUnpredictable > 0)
                eta = Math.Min(eta, C.Gold_RedAlertWaitMinutes);

            return eta;
        }

        private static string Describe(MoonGoldStatus status, double eta)
        {
            if (status.OpenMissionId != 0 && status.OpenMinutesLeft >= C.Gold_TravelMinWindowMinutes)
                return $"{status.Describe(status.OpenMissionId)} is up for another {status.OpenMinutesLeft:N0} min.";
            if (status.RemainingAnytime > 0)
                return $"{status.RemainingAnytime} standard mission(s) there still need a gold.";
            if (status.NextMissionId != 0 && status.NextMinutes <= eta)
                return $"{status.Describe(status.NextMissionId)} opens in ~{status.NextMinutes:N0} min.";
            if (status.RemainingUnpredictable > 0)
                return $"{status.RemainingUnpredictable} red alert / sequence mission(s) left there.";
            return $"{status.Remaining} mission(s) left to gold.";
        }

        private static bool TravelAllowed(CosmicMoonDefinition moon) =>
            C.Gold_TravelMoons.Count == 0 || C.Gold_TravelMoons.Contains(moon.TerritoryId);

        // - - - Getting there - - - //

        public static void Enqueue()
        {
            P.TaskManager.EnqueueMulti
            (
                new(() => ValidateTravel(), "Checking the planet hop is still worth making"),
                new(() => Task_Repair.HubCheck(), "Returning to the hub"),
                new(() => PathToTravelNpc(), "Pathing to the hub's travel NPC", Utils.TaskConfig),
                new(() => SelectDestination(), "Asking for a ride to the other moon", Utils.TaskConfig),
                new(() => WaitForWarp(), "Waiting on the planet transfer", Utils.TaskConfig)
            );
        }

        private static bool? ValidateTravel()
        {
            if (Destination == 0)
            {
                Abort("no destination is set");
                return true;
            }

            if (Destination == Player.Territory.RowId)
            {
                IceLogging.Info("We're already on the destination moon, carrying on with missions.", Tag);
                Arrived();
                return true;
            }

            if (Mission_Settings.Mode != ModeSelect.MissionGoldMode)
            {
                Abort("we're no longer in Gold Completion mode");
                return true;
            }

            _searchStamp = Environment.TickCount64;
            _interactStamp = Environment.TickCount64;
            _stuckStamp = 0;
            return true;
        }

        private static bool? PathToTravelNpc()
        {
            if (Destination == 0)
                return true;

            if (TryResolveTravelNpc(out var npcPosition))
            {
                var walkTo = NpcData.GetRandomPointInCircle(npcPosition, 0.5f);
                if (Task_NavmeshMove.Task_NavTo(walkTo, distance: 4, npcLoc: npcPosition) == true)
                {
                    IceLogging.Debug("Close enough to the travel NPC.", Tag);
                    _interactStamp = Environment.TickCount64;
                    return true;
                }

                if (EzThrottler.Throttle("Planet travel: pathing message", 2000))
                    IceLogging.Verbose($"Pathing to the travel NPC. Distance: {Player.DistanceTo(npcPosition):N1}", Tag);

                return false;
            }

            // Only nearby objects are in the table, and every travel NPC stands at the hub - so walk over
            // there and look again rather than giving up on the hop.
            if (CosmicMoonRegistry.TryGetHubCenter(Player.Territory.RowId, out var hubCenter)
                && Player.DistanceTo(hubCenter) > 15)
            {
                Task_NavmeshMove.Task_NavTo(hubCenter, distance: 10);
                return false;
            }

            if (Environment.TickCount64 - _searchStamp > NpcSearchTimeoutMs)
            {
                Abort("the hub's travel NPC couldn't be found");
                return true;
            }

            return false;
        }

        /// <summary>
        /// The travel NPC is the same character on every hub so far (Cruisingway), so it's resolved by name
        /// off the object table and then cached per moon - a hub we've used once paths straight there.
        /// </summary>
        private static bool TryResolveTravelNpc(out Vector3 position)
        {
            position = Vector3.Zero;
            var territory = Player.Territory.RowId;

            var live = FindTravelNpcObject();
            if (live != null)
            {
                position = live.Position;
                RememberTravelNpc(territory, live);
                return true;
            }

            if (C.Gold_TravelNpcCache.TryGetValue(territory, out var cached) && cached.NpcId != 0)
            {
                // A cache entry naming someone who is no longer on the list (an earlier build picked the
                // nearest match, which on Sinus Ardorum could be Drivingway) has to go, not be walked to.
                if (NameIndex(cached.Name) < 0)
                {
                    IceLogging.Info($"Forgetting {cached.Name} as the travel NPC on " +
                        $"{CosmicMoonRegistry.GetDisplayName(territory)} - not one of {string.Join(" / ", C.Gold_TravelNpcNames)}.", Tag);
                    C.Gold_TravelNpcCache.Remove(territory);
                    C.Save();
                }
                else
                {
                    position = new Vector3(cached.X, cached.Y, cached.Z);
                    return true;
                }
            }

            if (NpcData.TryGetNpc(territory, NpcData.NpcType.PlanetTravel, out var known))
            {
                position = known.Location_Npc;
                return true;
            }

            // He can stand well outside the object table's reach - on Sinus Ardorum the landing pad is a
            // good 100y from the hub - so fall back to where the game data says he spawns and walk there.
            if (TryGetSpawnPosition(territory, out position, out var spawnNpcId, out var spawnName))
            {
                RememberTravelNpc(territory, spawnNpcId, spawnName, position, "the game's NPC spawn data");
                return true;
            }

            if (EzThrottler.Throttle("Planet travel: npc missing", 15_000))
            {
                var nearby = string.Join(", ", Svc.Objects
                    .Where(x => x.ObjectKind == ObjectKind.EventNpc)
                    .OrderBy(Player.DistanceTo)
                    .Take(15)
                    .Select(x => $"{x.Name.TextValue} [{x.BaseId}]"));

                IceLogging.Error($"Couldn't find a travel NPC named {string.Join(" / ", C.Gold_TravelNpcNames)} on " +
                    $"{CosmicMoonRegistry.GetDisplayName(territory)}. NPCs nearby: {nearby}", Tag);
            }

            return false;
        }

        /// <summary>
        /// The travel NPC standing nearby, by name priority rather than by distance: on Sinus Ardorum
        /// Drivingway stands in front of Cruisingway and only sends you back to the standard moon area, so
        /// the nearer of the two is the wrong one.
        /// </summary>
        private static IGameObject? FindTravelNpcObject() =>
            Svc.Objects
                .Where(x => x.ObjectKind == ObjectKind.EventNpc)
                .Select(x => (Npc: x, Priority: NameIndex(x.Name.TextValue)))
                .Where(x => x.Priority >= 0)
                .OrderBy(x => x.Priority)
                .ThenBy(x => Player.DistanceTo(x.Npc))
                .Select(x => x.Npc)
                .FirstOrDefault();

        /// <summary>Where this name sits in the configured priority order, or -1 when it isn't one of them.</summary>
        private static int NameIndex(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return -1;

            for (var i = 0; i < C.Gold_TravelNpcNames.Count; i++)
            {
                if (string.Equals(C.Gold_TravelNpcNames[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static void RememberTravelNpc(uint territory, IGameObject npc) =>
            RememberTravelNpc(territory, npc.BaseId, npc.Name.TextValue, npc.Position, "the object table");

        internal static void RememberTravelNpc(uint territory, uint npcId, string name, Vector3 position, string source)
        {
            if (C.Gold_TravelNpcCache.TryGetValue(territory, out var cached)
                && cached.NpcId == npcId
                && Vector3.Distance(new Vector3(cached.X, cached.Y, cached.Z), position) < 1f)
                return;

            C.Gold_TravelNpcCache[territory] = new Config.TravelNpcInfo
            {
                NpcId = npcId,
                Name = name,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
            };
            C.Save();

            IceLogging.Info($"Remembered {name} [{npcId}] at {position:N1} as the travel NPC on " +
                $"{CosmicMoonRegistry.GetDisplayName(territory)} (from {source}).", Tag);
        }

        /// <summary>Territories whose spawn data has already been walked, so the sheet is only read once each.</summary>
        private static readonly HashSet<uint> SpawnLookups = new();

        /// <summary>
        /// Where the game data says the travel NPC stands. Level rows carry a position, a territory and the
        /// ENpcResident they belong to, which is enough to find him by name without ever having seen him.
        /// </summary>
        private static bool TryGetSpawnPosition(uint territoryId, out Vector3 position, out uint npcId, out string name)
        {
            position = Vector3.Zero;
            npcId = 0;
            name = string.Empty;

            if (!SpawnLookups.Add(territoryId))
                return false;

            var levels = Svc.Data.GetExcelSheet<Level>();
            var residents = Svc.Data.GetExcelSheet<ENpcResident>();
            if (levels == null || residents == null)
                return false;

            foreach (var level in levels)
            {
                if (level.Territory.RowId != territoryId)
                    continue;

                var objectId = level.Object.RowId;
                if (!residents.TryGetRow(objectId, out var resident))
                    continue;

                var residentName = resident.Singular.ExtractText();
                if (NameIndex(residentName) < 0)
                    continue;

                position = new Vector3(level.X, level.Y, level.Z);
                npcId = objectId;
                name = residentName;
                return true;
            }

            IceLogging.Info($"No spawn data for {string.Join(" / ", C.Gold_TravelNpcNames)} on " +
                $"{CosmicMoonRegistry.GetDisplayName(territoryId)} - falling back to walking the hub.", Tag);
            return false;
        }

        private static unsafe bool? SelectDestination()
        {
            if (Destination == 0)
                return true;

            if (Player.Territory.RowId == Destination)
            {
                Arrived();
                return true;
            }

            // The ride out is a cutscene plus a loading screen - that is progress, not a stall.
            if (!PlayerHelper.IsScreenReady())
            {
                _interactStamp = Environment.TickCount64;
                return false;
            }

            // Once the trip is booked the rest is just waiting on the zone change.
            if (PendingArrival != 0)
                return true;

            if (Environment.TickCount64 - _interactStamp > WarpTimeoutMs)
            {
                Abort("the travel NPC never offered the destination");
                return true;
            }

            var moonName = CosmicMoonRegistry.GetDisplayName(Destination);

            // "Blast off?" - the last step of the cosmoliner flow.
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesNo) && yesNo.IsAddonReady)
            {
                if (EzThrottler.Throttle("Planet travel: confirm", 500))
                {
                    IceLogging.Info($"Confirming the trip to {moonName}.", Tag);
                    yesNo.Yes();
                    MarkWarpFired();
                }

                return false;
            }

            // The destination carousel: chevron across to the moon we want, then Blast Off.
            if (PlanetSelectHandler.IsOpen())
            {
                switch (PlanetSelectHandler.Drive(Destination))
                {
                    case PlanetSelectHandler.DriveResult.Stuck:
                        // Everything up to here worked, so let the player finish the last click rather than
                        // throwing the hop away - the arrival handler picks the run back up either way.
                        if (_stuckStamp == 0)
                            _stuckStamp = Environment.TickCount64;

                        if (Environment.TickCount64 - _stuckStamp > ManualPickTimeoutMs)
                        {
                            Abort("nobody picked a destination in the cosmoliner window");
                            return true;
                        }

                        _interactStamp = Environment.TickCount64;
                        if (EzThrottler.Throttle("Planet travel: manual planet select", 15_000))
                            IceLogging.Error($"Can't drive the destination window - pick {moonName} and hit Blast Off, " +
                                "and the run carries on from there.", Tag);
                        break;

                    case PlanetSelectHandler.DriveResult.Confirmed:
                        _interactStamp = Environment.TickCount64;
                        IceLogging.Verbose("Blast Off pressed, waiting on the confirmation.", Tag);
                        break;
                }

                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var selectString) && selectString.IsAddonReady)
            {
                var entries = selectString.Entries.ToArray();

                var travelIndex = IndexOfEntry(entries, IsTravelEntry);
                if (travelIndex >= 0)
                {
                    if (EzThrottler.Throttle("Planet travel: select destination", 500))
                    {
                        IceLogging.Info($"Selecting \"{entries[travelIndex].Text}\" to open the destination window.", Tag);
                        entries[travelIndex].Select();
                        _interactStamp = Environment.TickCount64;
                    }

                    return false;
                }

                // Nothing else on this menu gets clicked - picking an unknown entry is how a character ends
                // up somewhere it was never asked to go. Log the options instead and let the hop time out.
                if (EzThrottler.Throttle("Planet travel: unknown menu", 5000))
                    IceLogging.Error("The travel NPC's menu had no entry about travelling to another cosmic " +
                        $"exploration area. Options were: {string.Join(" | ", entries.Select(x => x.Text))}", Tag);

                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Planet travel: talk", 200))
                    talk.Click();

                return false;
            }

            var npc = FindTravelNpcObject();
            if (npc == null)
            {
                if (EzThrottler.Throttle("Planet travel: npc gone", 5000))
                    IceLogging.Verbose("Waiting on the travel NPC to load in.", Tag);
                return false;
            }

            if (EzThrottler.Throttle("Planet travel: interact", 1000))
            {
                Utils.TargetgameObject(npc);
                Utils.InteractWithObject(npc);
            }

            return false;
        }

        private static int IndexOfEntry(SelectString.Entry[] entries, Func<string, bool> matches)
        {
            for (var i = 0; i < entries.Length; i++)
            {
                if (matches(entries[i].Text))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// The travel NPC's own menu entry - "Travel to another cosmic exploration area." on an English
        /// client, matched on the wording in <see cref="Config.Gold_TravelMenuKeywords"/> so other clients
        /// can be taught their own.
        /// </summary>
        private static bool IsTravelEntry(string entryText) =>
            !string.IsNullOrWhiteSpace(entryText)
            && C.Gold_TravelMenuKeywords.Any(keyword =>
                !string.IsNullOrWhiteSpace(keyword)
                && entryText.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        private static bool? WaitForWarp()
        {
            if (Destination == 0)
                return true;

            if (Player.Territory.RowId == Destination)
            {
                if (!PlayerHelper.IsScreenReady() || !Player.Interactable)
                    return false;

                Arrived();
                return true;
            }

            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesNo) && yesNo.IsAddonReady)
            {
                if (EzThrottler.Throttle("Planet travel: confirm warp", 500))
                    yesNo.Yes();

                return false;
            }

            if (_pendingStamp != 0 && Environment.TickCount64 - _pendingStamp > WarpTimeoutMs)
            {
                Abort("the transfer never went through");
                return true;
            }

            if (EzThrottler.Throttle("Planet travel: waiting on warp", 5000))
                IceLogging.Verbose($"Waiting to arrive on {CosmicMoonRegistry.GetDisplayName(Destination)}.", Tag);

            return false;
        }

        // - - - Coming back up on the other side - - - //

        /// <summary>
        /// The loading screen stops the plugin (no player, no cosmic zone), so the run is restarted here once
        /// we're standing on the destination moon. Driven from <see cref="PlayerHandlers.Tick"/>.
        /// </summary>
        internal static bool TryResumeAfterArrival()
        {
            if (PendingArrival == 0)
                return false;

            // Still mid-hop with the plugin running - the task chain handles that case itself.
            if (SchedulerMain.State == IceState.PlanetTravel)
                return false;

            if (Environment.TickCount64 - _pendingStamp > ResumeTimeoutMs)
            {
                IceLogging.Info("Gave up waiting on the planet transfer to finish.", Tag);
                Reset();
                return false;
            }

            if (Player.Territory.RowId != PendingArrival)
                return false;
            if (!Player.Interactable || !PlayerHelper.IsScreenReady())
                return false;

            var mode = Mission_Settings.Mode;
            var job = Mission_Settings.SelectedJob;
            var moonName = CosmicMoonRegistry.GetDisplayName(PendingArrival);

            Arrived(resumeMissions: false);

            if (SchedulerMain.State == IceState.Idle)
            {
                SchedulerMain.EnablePlugin();
                Mission_Settings.Mode = mode;
                Mission_Settings.SelectedJob = job;
            }

            SchedulerMain.State = IceState.Start;
            PlayerHandlers.PlayerFirstCosmicZone = true;

            IceLogging.Info($"Landed on {moonName} - picking the gold grind back up here.", Tag);
            return true;
        }

        /// <summary>The player stopped the plugin themselves - drop any hop we were in the middle of.</summary>
        internal static void Cancel()
        {
            if (!Travelling)
                return;

            IceLogging.Info("Cancelling the planet hop.", Tag);
            Reset();
        }

        private static void MarkWarpFired()
        {
            PendingArrival = Destination;
            _pendingStamp = Environment.TickCount64;
        }

        private static void Arrived(bool resumeMissions = true)
        {
            _lastArrival = Environment.TickCount64;
            StampVisit(Player.Territory.RowId);
            Reset();

            if (!resumeMissions)
                return;

            SchedulerMain.State = IceState.Start;
            P.TaskManager.Tasks.Clear();
        }

        private static void Reset()
        {
            Destination = 0;
            PendingArrival = 0;
            _pendingStamp = 0;
        }

        private static void Abort(string why)
        {
            IceLogging.Info($"Planet hop called off - {why}. Going back to the mission board.", Tag);
            PlanetSelectHandler.CloseIfOpen();
            Reset();
            _lastArrival = Environment.TickCount64;
            SchedulerMain.State = IceState.GrabMission;
            P.TaskManager.Tasks.Clear();
        }
    }
}
