using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ICE.Scheduler
{
    internal static class Task_HubActivities
    {
        public static bool RepairNpc = false;
        public static bool RelicTurnin = false;
        public static bool CosmoBuy = false;
        public static bool CanGamba = false;
        public static bool CanBuyDrones = false;
        public static bool ProcessRetainers = false;
        private static Vector3 craftingSpot = Vector3.Zero;

        public static void Enqueue()
        {
            P.TaskManager.Enqueue(RegisterCraftingPosition, "Registering crafting position for later");
            P.TaskManager.Enqueue(Task_Repair.HubCheck, "Checking to see if we're in hub area");
            if (RepairNpc)
            {
                P.TaskManager.EnqueueMulti
                (
                    new(() => IceLogging.Info("Starting repair task at the npc", "Task_HubActivities")),
                    new(Task_Repair.Repair_PathTo, "Pathing to the repair NPC"),
                    new(Task_Repair.RepairAtNpc, "Repairing at the NPC Vendor"),
                    new(Task_Repair.CloseRepair, "Closing the repair window")
                );
            }
            if (RelicTurnin)
            {
                P.TaskManager.Enqueue(() => IceLogging.Info("Starting Relic Turnin task at the npc", "Task_HubActivities"));
                Task_RelicTurnin.Enqueue();
                P.TaskManager.Enqueue(() => IceLogging.Info("Task_Relic turnin is Complete"));
            }
            if (CosmoBuy)
            {
                P.TaskManager.Enqueue(() => IceLogging.Info("Starting Relic Turnin task at the npc", "Task_HubActivities"));;
                Task_BuyCosmoItems.Enqueue();
            }
            if (CanGamba)
            {
                P.TaskManager.Enqueue(() => IceLogging.Info("Starting Gamba task at the npc", "Task_HubActivities"));
                Task_Gamba.Enqueue();
            }
            if (CanBuyDrones)
            {
                P.TaskManager.Enqueue(() => IceLogging.Info("Starting the drone buying", "Task_HubActivities"));
                Task_ArtifactSearch.EnqueueBuy();
            }
            if (ProcessRetainers)
            {
                Task_Retainer.Enqueue();
            }
            P.TaskManager.EnqueueMulti
            (
                new(() => ResetAll(), "Setting all task to false"),
                new(() => IceLogging.Info("Checking to see if we need to path back to the spot")),
                new(PathBackToCraftingSpot, "Pathing back to our crafting spot", Utils.TaskConfig),
                new(() => SchedulerMain.State = IceState.Start, "Swapping back to start")
            );
        }

        public static bool? RegisterCraftingPosition()
        {
            if (CosmicHelper.CrafterJobList.Contains((uint)Player.Job))
                craftingSpot = Player.Position;

            return true;
        }

        public static bool? PathBackToCraftingSpot()
        {
            if (CosmicHelper.CrafterJobList.Contains((uint)Player.Job))
            {
                if (!Task_NavmeshMove.Task_NavTo(craftingSpot, true, 1, false).Value)
                {
                    return false;
                }
                else
                {
                    IceLogging.Debug("We're back at our spot, so continuing on");
                    return true;
                }
            }
            else
            {
                IceLogging.Info($"We're not on a crafting job. (Allegedly) which means that we don't need to path back | Player Job: {(uint)Player.Job}");
                return true;
            }
        }

        private static bool? ResetAll()
        {
            IceLogging.Info("Resetting all hub task to false");

            RepairNpc = false;
            RelicTurnin = false;
            CosmoBuy = false;
            CanGamba = false;
            CanBuyDrones = false;
            ProcessRetainers = false;
            CosmicHelper.Task_UpdateRelicMissionInfo();

            return true;
        }
    }
}
