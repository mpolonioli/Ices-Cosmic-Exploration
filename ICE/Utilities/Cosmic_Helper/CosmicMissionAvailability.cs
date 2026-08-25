using FFXIVClientStructs.FFXIV.Client.System.Framework;
using ICE.Scheduler.Handlers;
using System.Collections.Generic;

namespace ICE.Utilities.Cosmic_Helper;

/// <summary>
/// Weather / timed mission availability for <b>any</b> moon, not just the one the player is standing on.
/// Weather forecasts (<see cref="WeatherForecastHandler.GetTerritoryForecast"/>) and Eorzea time are both
/// readable for every territory from anywhere, so the whole board of provisional missions can be predicted
/// remotely - which is what lets Gold Completion decide it is worth flying to another hub.
/// <para>
/// Red alerts are the exception: they are a live per-zone event with no remote read, so they only count
/// towards "this moon still has work left", never towards "something is up right now".
/// </para>
/// </summary>
internal static class CosmicMissionAvailability
{
    /// <summary>One Eorzea hour in real seconds.</summary>
    private const double EorzeaHourSeconds = 175.0;

    /// <summary>A weather block lasts 8 Eorzea hours (23m20s real).</summary>
    private const double WeatherBlockMinutes = (8 * EorzeaHourSeconds) / 60.0;

    internal sealed class MoonGoldStatus
    {
        public required CosmicMoonDefinition Moon { get; init; }

        /// <summary>Every mission on this moon that still needs a gold and that we're allowed to run.</summary>
        public int Remaining { get; set; }

        /// <summary>Standard + tool mastery missions - always grabbable, so this moon is workable right now.</summary>
        public int RemainingAnytime { get; set; }

        /// <summary>Weather / timed missions, i.e. the ones this class can put a clock on.</summary>
        public int RemainingScheduled { get; set; }

        /// <summary>Red alerts and sequence chains - still work left, but not predictable from off-world.</summary>
        public int RemainingUnpredictable { get; set; }

        /// <summary>A scheduled mission whose window is open right now (0 when there is none).</summary>
        public uint OpenMissionId { get; set; }

        /// <summary>Real minutes left on <see cref="OpenMissionId"/>'s window.</summary>
        public double OpenMinutesLeft { get; set; }

        /// <summary>The next scheduled mission to open up (0 when nothing is coming inside the forecast).</summary>
        public uint NextMissionId { get; set; }

        /// <summary>Real minutes until <see cref="NextMissionId"/> opens.</summary>
        public double NextMinutes { get; set; } = double.MaxValue;

        public string Describe(uint missionId) =>
            CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var info)
                ? $"[{missionId}] {info.Name}"
                : $"[{missionId}]";
    }

    /// <summary>
    /// Walks every mission on <paramref name="moon"/> that <paramref name="wanted"/> accepts and works out
    /// what is open now and what opens next.
    /// </summary>
    internal static MoonGoldStatus Evaluate(CosmicMoonDefinition moon, Func<uint, CosmicHelper.CosmicInfo, bool> wanted)
    {
        var status = new MoonGoldStatus { Moon = moon };
        var forecast = SafeForecast(moon.TerritoryId);
        var eorzeaHour = CurrentEorzeaHour();

        foreach (var (missionId, mission) in CosmicHelper.SheetMissionDict)
        {
            if (mission.TerritoryId != moon.TerritoryId)
                continue;
            if (!wanted(missionId, mission))
                continue;

            status.Remaining++;

            if (mission.IsWeather || mission.IsTimed)
            {
                status.RemainingScheduled++;

                var window = mission.IsWeather
                    ? WeatherWindow(mission, forecast)
                    : TimedWindow(mission, eorzeaHour);

                if (window.OpenNow)
                {
                    // Prefer whichever open mission leaves the most room to actually finish it.
                    if (status.OpenMissionId == 0 || window.MinutesLeft > status.OpenMinutesLeft)
                    {
                        status.OpenMissionId = missionId;
                        status.OpenMinutesLeft = window.MinutesLeft;
                    }
                }
                else if (window.MinutesUntil < status.NextMinutes)
                {
                    status.NextMinutes = window.MinutesUntil;
                    status.NextMissionId = missionId;
                }
            }
            else if (mission.IsCritical || mission.IsSequence)
            {
                status.RemainingUnpredictable++;
            }
            else
            {
                status.RemainingAnytime++;
            }
        }

        return status;
    }

    private readonly record struct Window(bool OpenNow, double MinutesLeft, double MinutesUntil);

    private static Window WeatherWindow(CosmicHelper.CosmicInfo mission, List<WeatherForecast> forecast)
    {
        if (forecast.Count == 0 || !CosmicHelper.WeatherIds.TryGetValue(mission.Weather, out var iconId))
            return new(false, 0, double.MaxValue);

        var wantedIcon = (uint)iconId;

        if (forecast[0].IconId == wantedIcon)
        {
            // GetTerritoryForecast only records weather *changes*, so the next entry is when this block ends.
            var minutesLeft = forecast.Count > 1
                ? (forecast[1].Time - DateTime.UtcNow).TotalMinutes
                : WeatherBlockMinutes;
            return new(true, Math.Max(0, minutesLeft), 0);
        }

        for (var i = 1; i < forecast.Count; i++)
        {
            if (forecast[i].IconId != wantedIcon)
                continue;

            var minutesUntil = (forecast[i].Time - DateTime.UtcNow).TotalMinutes;
            return new(false, 0, Math.Max(0, minutesUntil));
        }

        return new(false, 0, double.MaxValue);
    }

    private static Window TimedWindow(CosmicHelper.CosmicInfo mission, double eorzeaHour)
    {
        var start = mission.StartTime % 24;
        var end = mission.EndTime % 24;

        // A mission with no window at all (start == end) behaves like an always-on mission.
        if (start == end)
            return new(true, double.MaxValue, 0);

        bool openNow = start < end
            ? eorzeaHour >= start && eorzeaHour < end
            : eorzeaHour >= start || eorzeaHour < end;

        return openNow
            ? new(true, EorzeaHoursToMinutes(HoursUntil(eorzeaHour, end)), 0)
            : new(false, 0, EorzeaHoursToMinutes(HoursUntil(eorzeaHour, start)));
    }

    private static double HoursUntil(double fromHour, double targetHour)
    {
        var hours = targetHour - fromHour;
        if (hours <= 0)
            hours += 24;
        return hours;
    }

    private static double EorzeaHoursToMinutes(double eorzeaHours) => (eorzeaHours * EorzeaHourSeconds) / 60.0;

    internal static unsafe double CurrentEorzeaHour()
    {
        var framework = Framework.Instance();
        if (framework == null)
            return 0;

        var eorzea = DateTimeOffset.FromUnixTimeSeconds(framework->ClientTime.EorzeaTime);
        return eorzea.Hour + (eorzea.Minute / 60.0);
    }

    private static List<WeatherForecast> SafeForecast(uint territoryId)
    {
        try
        {
            return WeatherForecastHandler.GetTerritoryForecast((ushort)territoryId);
        }
        catch (Exception ex)
        {
            IceLogging.Error($"Failed to read the weather forecast for {CosmicMoonRegistry.GetDisplayName(territoryId)}: {ex.Message}");
            return new List<WeatherForecast>();
        }
    }
}
