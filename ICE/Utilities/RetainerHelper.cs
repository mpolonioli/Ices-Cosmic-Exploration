using System;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ICE.Utilities;

/// <summary>
/// Helpers for the "go to the summoning bell when ventures are done" hub activity.
/// Detection reads the logged-in character's <see cref="RetainerManager"/> directly; the actual
/// retainer processing (collect + reassign + close) is left to AutoRetainer once the bell is open.
/// </summary>
internal static unsafe class RetainerHelper
{
    /// <summary>
    /// True if any of the current character's retainers has a venture whose completion
    /// timestamp is in the past (i.e. there is something for AutoRetainer to collect).
    /// </summary>
    public static bool AnyVentureComplete()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady)
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var count = rm->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var retainer = rm->GetRetainerBySortedIndex(i);
            if (retainer == null)
                continue;

            // VentureId == 0 means the retainer isn't on a venture.
            // VentureComplete is a unix timestamp; only count it once it's actually elapsed.
            if (retainer->VentureId != 0 && retainer->VentureComplete != 0 && retainer->VentureComplete <= now)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the nearest loaded summoning bell event object for the current moon (matched by the
    /// data id stored in <see cref="NpcData"/>), or null if none is loaded.
    /// </summary>
    public static IGameObject? FindSummoningBell()
    {
        if (!NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.SummonerBell, out var npc))
            return null;

        Utils.TryGetObjectByDataId(npc.NpcId, out var bell);
        return bell;
    }
}
