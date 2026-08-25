using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ICE.ConfigFiles;

public partial class Config
{
    public ModeSelect SelectedMode { get; set; } = ModeSelect.Standard;
    public bool OnlyGrabMission_Debug { get; set; } = false;
    public int TargetLevel { get; set; } = 100;
    public bool StopWhenLevel { get; set; } = false;
    public bool StopOnceHitCosmoCredits { get; set; } = false;
    public int CosmoCreditsCap { get; set; } = 30_000;
    public bool StopOnceHitLunarCredits { get; set; } = false;
    public int LunarCreditsCap { get; set; } = 10_000;
    public bool StopOnceHitCosmicScore { get; set; } = false;
    public int CosmicScoreCap { get; set; } = 500_000;
    public bool StopOnceRelicFinished { get; set; } = false;
    public bool StopOnceStandardMissionsGolded { get; set; } = false;
    public bool StopWhenMasteryComplete { get; set; } = false;
    public int MasteryCap { get; set; } = 500_000;
    public bool StopAtRelicLv { get; set; } = false;
    public int RelicLv { get; set; } = 20;
    public List<ProvisionalTypes> MissionPrio { get; set; } = new()
    {
        ProvisionalTypes.ProvisionalWeather,
        ProvisionalTypes.ProvisionalSequential,
        ProvisionalTypes.ProvisionalTimed
    };
    public List<MissionTypes> MissionTypePrio { get; set; } = new()
    {
        MissionTypes.DroneSearch,
        MissionTypes.Critical,
        MissionTypes.Provisional,
        MissionTypes.Standard,
        MissionTypes.ToolMastery,
    };
    public List<uint> JobPrio { get; set; } = new()
    {
        8, 9, 10, 11, 12, 13, 14, 15,  // Crafters: CRP, BSM, ARM, GSM, LTW, WVR, ALC, CUL
        16, 17, 18                     // Gatherers: MIN, BTN, FSH
    };
    public bool AutoSelectMoon { get; set; } = true;
    public bool RemoveAfterGold { get; set; } = false;
    public bool KeepARanks { get; set; } = false;
    public bool ShowExtraMissionInfo { get; set; } = true;
    public Dictionary<uint, uint> ScoreKeeper { get; set; } = new();
    public Dictionary<uint, MissionSettings> MissionConfig { get; set; } = new();
    public Dictionary<string, List<uint>> Mission_Playlist { get; set; } = new();

    /// <summary>
    /// Gold Completion mode: missions already completed at a non-gold rating are the hardest to gold,
    /// so they get pushed behind everything else that still needs a gold.
    /// </summary>
    public bool Gold_HardMissionsLast { get; set; } = true;

    /// <summary>
    /// Gold Completion mode: when nothing on this moon can be worked on right now (only weather/timed/red
    /// alert missions left to gold and none of them are up), fly to a hub where one is - instead of idling
    /// on the mission board until the current moon rolls one.
    /// </summary>
    public bool Gold_CrossPlanetTravel { get; set; } = true;

    /// <summary>Hubs the cross-planet hop is allowed to fly to. Empty = every moon in the registry.</summary>
    public List<uint> Gold_TravelMoons { get; set; } = new();

    /// <summary>Don't fly for a window that is about to close - it has to be open this long to be worth it.</summary>
    public int Gold_TravelMinWindowMinutes { get; set; } = 5;

    /// <summary>Nothing anywhere inside this many minutes means staying put rather than flying to wait.</summary>
    public int Gold_TravelMaxWaitMinutes { get; set; } = 45;

    /// <summary>
    /// When every hub's remaining work is red alerts - which can't be seen from another moon - camping one
    /// hub only ever catches that hub's alerts. After this many minutes somewhere with nothing running and
    /// no alert brewing, move to the hub left alone the longest. 0 turns the rotation off.
    /// </summary>
    public int Gold_TravelRotateMinutes { get; set; } = 5;

    /// <summary>
    /// Red alerts can't be read from another moon, so a hub whose only remaining work is a red alert is
    /// treated as "something should turn up in about this long" when weighing it against a hub we can see.
    /// </summary>
    public int Gold_RedAlertWaitMinutes { get; set; } = 20;

    /// <summary>
    /// Names of the hub NPC that flies you between moons - Cruisingway on every hub. Order is priority:
    /// the first name that's standing nearby wins, however close the others are. On Sinus Ardorum
    /// Drivingway stands in front of him and sends you back to the standard moon area instead, so he is
    /// deliberately not in here; Cruisingway is further in, on the airship landing pad.
    /// </summary>
    public List<string> Gold_TravelNpcNames { get; set; } = new() { "Cruisingway" };

    /// <summary>
    /// Text that identifies the travel NPC's menu entry ("Travel to another cosmic exploration area.").
    /// Matched case-insensitively as a substring, so non-English clients can drop their own wording in.
    /// </summary>
    public List<string> Gold_TravelMenuKeywords { get; set; } = new()
    {
        "cosmic exploration area",
        "travel to another",
    };

    // Buttons in the cosmoliner's destination window, learned in-game by watching which one actually moves
    // the carousel. Node ids repeat between components, so a button is addressed by its node list path
    // ("6/2"). Empty = not learned yet; cleared automatically when a button stops working.
    public string Gold_PlanetSelect_PrevButton { get; set; } = string.Empty;
    public string Gold_PlanetSelect_NextButton { get; set; } = string.Empty;

    /// <summary>
    /// Label of the confirm button in that window. It has a hidden twin for the "Specify Instance" layout,
    /// so the button is picked by its wording rather than by where it sits.
    /// </summary>
    public List<string> Gold_PlanetSelect_ConfirmKeywords { get; set; } = new() { "blast off" };

    /// <summary>Travel NPC positions learned in-game, per territory, so later hops path straight to them.</summary>
    public Dictionary<uint, TravelNpcInfo> Gold_TravelNpcCache { get; set; } = new();

    public class TravelNpcInfo
    {
        public uint NpcId { get; set; }
        public string Name { get; set; } = string.Empty;
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    public bool GrindAllProvisionals { get; set; } = true;
    public bool GrindOffClassRedAlert { get; set; } = false;
    public bool Relic_IncludeCriticals { get; set; } = true;
    public bool DisableHub_Critical { get; set; } = false;

    // New Table Settings
    public ItemFilter ItemFilter { get; set; } = ItemFilter.All;
    public MissionFilter MissionFilter { get; set; } = MissionFilter.All;
    public JobFilter JobFilter { get; set; } = JobFilter.All;

    public class MissionSettings
    {
        public bool Enabled { get; set; } = false;
        public int GProfileId { get; set; } = 0;
        public TurninState TurninGoal { get; set; } = TurninState.Gold;
        public uint Master_Score { get; set; } = 1000;
        public uint Master_Items { get; set; } = 6;
        public bool Use_BuildinPreset { get; set; } = false;
        public string AutoHookPresetName { get; set; } = string.Empty;
        public int TotalCompletions { get; set; } = 0;
        public int BronzeCompletion { get; set; } = 0;
        public int SilverCompletions { get; set; } = 0;
        public int GoldCompletions { get; set; } = 0;
        public int CriticalCompletions { get; set; } = 0;
        public int Master_Completion { get; set; } = 0;
        public int FailedCounters { get; set; } = 0;
        public int TotalAttempts { get; set; } = 0;
        public List<TurninData> TurninRecords { get; set; } = new();
        public double AverageGoalTime(TurninState state)
        {
            var records = TurninRecords.Where(x => x.State == state).ToList();
            return records.Any() ? records.Average(t => t.Time) : 0;
        }
        public double AverageTime()
        {
            var records = TurninRecords.ToList();
            return records.Any() ? records.Average(t => t.Time) : 0;
        }
        public double BestGoalTime(TurninState state)
        {
            var records = TurninRecords.Where(x => x.State == state).ToList();
            return records.Any() ? records.Min(t => t.Time) : double.MaxValue;
        }
        public double BestTimeOverall()
        {
            return TurninRecords.Any() ? TurninRecords.Min(t => t.Time) : double.MaxValue;
        }
        public Dictionary<uint, ArtisanSettings> CraftSettings { get; set; } = new();

        public class TurninData
        {
            public double Time { get; set; }
            public TurninState State { get; set; }
        }

        public class ArtisanSettings
        {
            public bool UseGlobal { get; set; } = true;
            public uint FoodId { get; set; } = 0;
            public bool FoodHQ { get; set; } = true;
            public uint PotionId { get; set; } = 0;
            public bool PotionHQ { get; set; } = false;
            public uint ManualId { get; set; } = 0;
            public uint SquadronManualId { get; set; } = 0;
            public ArtisanCraftType ArtisanSolverType { get; set; } = ArtisanCraftType.Default;
            public string MacroName { get; set; } = "";
            public int SkillUsageAmount { get; set; } = -1;
            public int MinStepsForMiracle { get; set; } = -1;
            public uint ExpertProfileId = 0;
        };
    }
    public class FishingLocations
    {
        public uint ZoneId { get; set; } = 0;
        public float X { get; set; } = 0.0f;
        public float Y { get; set; } = 0.0f;
        public Vector3? WorldPosition { get; set; } = null;

        [JsonIgnore]
        public Vector2 MapCoords => new(X, Y);
    }

    public List<FishingLocations> Personal_FishLocation = new();
}
