using System;
using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using ICE.Utilities;
using ICE.Utilities.Cosmic_Helper;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// Hub activity: walk to the summoning bell and open it when retainer ventures are complete.
    /// The actual retainer processing (collect/reassign) and closing the bell is handled by
    /// AutoRetainer, which the user is expected to have configured. We only get the bell open and
    /// then wait for the summoning-bell session to end.
    /// </summary>
    internal static class Task_Retainer
    {
        private const string Tag = "[Task: Retainer]";

        // Bounds the path-to + open-bell phases so we never get stuck if the bell can't be found.
        // The wait-for-AutoRetainer phase is intentionally not bounded by this (processing many
        // retainers can take minutes).
        private static long _deadline;
        private static bool _bellSessionStarted;

        // AutoRetainer briefly drops the summoning-bell condition when stepping between individual
        // retainers, so we only treat the session as finished once it has stayed clear for this long.
        private const long SettleMs = 3_000;
        private static long _settleUntil;

        public static void Enqueue()
        {
            // Deadline is armed lazily on the first PathToBell frame, since other hub activities
            // (repair, gamba, etc.) may drain ahead of this and we don't want them eating the budget.
            _deadline = 0;
            _bellSessionStarted = false;
            _settleUntil = 0;

            P.TaskManager.EnqueueMulti
            (
                new(() => IceLogging.Info("Starting retainer task at the summoning bell", "Task_HubActivities")),
                new(PathToBell, "Pathing to the summoning bell"),
                new(OpenBell, "Opening the summoning bell"),
                new(WaitForAutoRetainer, "Waiting for AutoRetainer to process retainers", Utils.TaskConfig)
            );
        }

        public static bool? PathToBell()
        {
            if (_deadline == 0)
                _deadline = Environment.TickCount64 + 45_000;

            Vector3 dest = ResolveBellLocation();

            if (dest == Vector3.Zero)
            {
                if (EzThrottler.Throttle("Retainer: no bell location", 2000))
                    IceLogging.Verbose("Summoning bell not loaded yet and no stored position; waiting/looking.", Tag);

                if (Environment.TickCount64 >= _deadline)
                {
                    IceLogging.Info("Could not locate a summoning bell in time. Skipping retainers.", Tag);
                    return true;
                }
                return false;
            }

            if (Player.DistanceTo(dest) <= 4f)
                return true;

            if (!Task_NavmeshMove.Task_NavTo(dest, distance: 3, npcLoc: dest).Value)
            {
                if (EzThrottler.Throttle("Retainer: bell pathing", 1000))
                    IceLogging.Verbose($"Pathing to summoning bell. Distance: {Player.DistanceTo(dest):N2}", Tag);

                if (Environment.TickCount64 >= _deadline)
                {
                    IceLogging.Info("Timed out pathing to the summoning bell. Skipping retainers.", Tag);
                    return true;
                }
                return false;
            }

            return true;
        }

        public static unsafe bool? OpenBell()
        {
            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                _bellSessionStarted = true;
                IceLogging.Debug("Summoning bell session started, handing off to AutoRetainer.", Tag);
                return true;
            }

            if (Environment.TickCount64 >= _deadline)
            {
                IceLogging.Info("Couldn't open the summoning bell in time. Skipping retainers.", Tag);
                return true;
            }

            if (Svc.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("Retainer: dismount for bell"))
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9);
                return false;
            }

            var bell = RetainerHelper.FindSummoningBell();
            if (bell == null)
            {
                if (EzThrottler.Throttle("Retainer: bell not in range", 2000))
                    IceLogging.Verbose("Summoning bell not in range yet, waiting for it to load.", Tag);
                return false;
            }

            if (EzThrottler.Throttle("Retainer: interact with bell", 1000))
            {
                IceLogging.Debug($"Interacting with the summoning bell ({bell.Name}).", Tag);
                Utils.TargetgameObject(bell);
                Utils.InteractWithObject(bell);
            }

            return false;
        }

        public static bool? WaitForAutoRetainer()
        {
            // Nothing was opened (timed out / bell missing) — don't wait on a session that never started.
            if (!_bellSessionStarted)
                return true;

            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                _settleUntil = Environment.TickCount64 + SettleMs;
                if (EzThrottler.Throttle("Retainer: AutoRetainer working", 5000))
                    IceLogging.Info("AutoRetainer is processing retainers...", Tag);
                return false;
            }

            // Not occupied — wait for the condition to stay clear before declaring it finished,
            // so we don't bail out while AutoRetainer is stepping between retainers.
            if (Environment.TickCount64 < _settleUntil)
                return false;

            IceLogging.Info("Summoning bell session finished. Retainer handling complete.", Tag);
            return true;
        }

        private static Vector3 ResolveBellLocation()
        {
            // Prefer the live object position (handles moons without a stored coordinate),
            // falling back to the stored hub location only if the bell isn't loaded yet.
            var bell = RetainerHelper.FindSummoningBell();
            if (bell != null)
                return bell.Position;

            if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.SummonerBell, out var npc)
                && npc.Location_Npc != Vector3.Zero)
                return npc.Location_Npc;

            return Vector3.Zero;
        }
    }
}
