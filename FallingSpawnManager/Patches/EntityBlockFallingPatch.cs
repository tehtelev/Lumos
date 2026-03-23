using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

#nullable disable

namespace FallingSpawnManager.Patches;

/// <summary>
/// Harmony patches that improve EntityBlockFalling behaviour:
///   - Sets AlwaysActive = true and corrects SimulationRange inside Initialize.
///   - Adds a periodic player-proximity check: when no players are nearby the block
///     falls instantly and the entity is destroyed, preventing ghost entities.
///
/// Static helpers SimulateInstantFall / SpawnDrops are public so that
/// FallingSpawnManager (separate mod) can call them without going through reflection.
/// </summary>
public static class EntityBlockFallingPatch
{
    // -------------------------------------------------------------------------
    //  Reflected access to private fields (FieldRef gives zero-overhead ref)
    // -------------------------------------------------------------------------

    private static readonly AccessTools.FieldRef<EntityBlockFalling, bool> _fallHandledRef =
        AccessTools.FieldRefAccess<EntityBlockFalling, bool>("fallHandled");

    private static readonly AccessTools.FieldRef<EntityBlockFalling, ItemStack[]> _dropsRef =
        AccessTools.FieldRefAccess<EntityBlockFalling, ItemStack[]>("drops");

    // -------------------------------------------------------------------------
    //  Per-instance state that we cannot add as real fields
    // -------------------------------------------------------------------------

    /// <summary>Extra data attached to each EntityBlockFalling instance.</summary>
    private sealed class ExtraData
    {
        public long LastPlayerCheckMs;
    }

    // ConditionalWeakTable keeps entries alive only as long as the key is alive —
    // no need for manual cleanup on despawn.
    private static readonly ConditionalWeakTable<EntityBlockFalling, ExtraData> _extra = new();

    private const int PlayerCheckIntervalMs = 2000;

    // -------------------------------------------------------------------------
    //  Patch 1 — Initialize
    //
    //  Two fixes in one postfix (runs AFTER base.Initialize, so our values win):
    //
    //  a) AlwaysActive = true
    //     Simpler and cheaper than patching the base-class getter: we just assign
    //     the property directly, which sets the backing field once at spawn time.
    //
    //  b) SimulationRange = GlobalConstants.DefaultSimulationRange
    //     The original writes (int)(0.75f * DefaultSimulationRange).  Using the full
    //     default range keeps physics running over the same distance the server already
    //     tracks entities — no extra overhead, but the proximity check can now fire
    //     correctly when all players leave the area.
    // -------------------------------------------------------------------------

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntityBlockFalling), "Initialize")]
    public static void EntityBlockFalling_Initialize_Postfix(
        EntityBlockFalling __instance,
        EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {

        __instance.AlwaysActive = true;
        __instance.SimulationRange = GlobalConstants.DefaultSimulationRange;
    }

    // -------------------------------------------------------------------------
    //  Patch 2 — OnGameTick: player-proximity check
    //
    //  Every PlayerCheckIntervalMs ms we check whether any player is still nearby.
    //  If not, we run an instant fall simulation and kill the entity.  This prevents
    //  falling-block entities from piling up in loaded-but-unattended chunks.
    //
    //  Using a postfix instead of a transpiler keeps the patch simple and robust
    //  across game updates.  The one trade-off is that the rest of OnGameTick already
    //  ran for that tick — acceptable, because the entity is destroyed immediately after.
    // -------------------------------------------------------------------------

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntityBlockFalling), "OnGameTick")]
    public static void EntityBlockFalling_OnGameTick_Postfix(
        EntityBlockFalling __instance,
        float dt)
    {

        // Server-only; skip dead / already-handled entities
        if (__instance.Api?.Side != EnumAppSide.Server) return;
        if (!__instance.Alive) return;
        if (_fallHandledRef(__instance)) return;

        var data = _extra.GetOrCreateValue(__instance);
        long now = __instance.Api.World.ElapsedMilliseconds;

        if (now - data.LastPlayerCheckMs < PlayerCheckIntervalMs) return;
        data.LastPlayerCheckMs = now;

        if (!IsPlayerNearby(__instance))
            FallNow(__instance);
    }

    // -------------------------------------------------------------------------
    //  Private helpers
    // -------------------------------------------------------------------------

    private static bool IsPlayerNearby(EntityBlockFalling entity)
    {
        var sapi = entity.Api as ICoreServerAPI;
        // Mirror the calculation used in FallingSpawnManager.RequestSpawn
        int range = (sapi?.World.DefaultEntityTrackingRange ?? 8) * GlobalConstants.ChunkSize;
        Vec3d pos = entity.Pos.XYZ;

        foreach (IPlayer player in entity.Api.World.AllOnlinePlayers)
        {
            EntityPlayer eplr = player.Entity;
            if (eplr != null && eplr.Pos.InRangeOf(pos, range * range, range))
                return true;
        }
        return false;
    }

    private static void FallNow(EntityBlockFalling entity)
    {
        // Guard against re-entry (OnFallToGround may have set this in the same tick)
        if (_fallHandledRef(entity)) return;
        _fallHandledRef(entity) = true;

        ItemStack[] drops = _dropsRef(entity);

        SimulateInstantFall(
            entity.Api.World,
            entity.Block,
            entity.removedBlockentity,
            entity.initialPos,
            drops,
            doRemoveBlock: false   // block was already removed in Initialize
        );

        entity.Die(EnumDespawnReason.Removed);
    }

    // =========================================================================
    //  Public static utilities
    //  (used by FallingSpawnManager and the FallNow helper above)
    // =========================================================================

    /// <summary>
    /// Drops the block's item stacks and, if the block entity was a container,
    /// its inventory contents at the centre of <paramref name="pos"/>.
    /// </summary>
    public static void SpawnDrops(
        IWorldAccessor world,
        BlockPos pos,
        ItemStack[] drops,
        BlockEntity be)
    {
        Vec3d dpos = pos.ToVec3d().Add(0.5, 0.5, 0.5);

        if (drops != null)
        {
            foreach (ItemStack drop in drops)
                world.SpawnItemEntity(drop, dpos);
        }

        if (be is IBlockEntityContainer bec)
            bec.DropContents(dpos);
    }

    /// <summary>
    /// Instantly resolves a falling-block trajectory without creating an entity.
    /// Descends until it finds a solid surface, then either places the block or
    /// drops items.  Used for blocks that are outside any player's view range.
    /// </summary>
    /// <param name="world">World accessor.</param>
    /// <param name="block">The block that is falling.</param>
    /// <param name="be">The block entity that was attached to the block (may be null).</param>
    /// <param name="startPos">The position the block fell from.</param>
    /// <param name="drops">Pre-computed drops (may be null).</param>
    /// <param name="doRemoveBlock">
    ///     When true the block is removed from <paramref name="startPos"/> first
    ///     (with a validity guard).  Pass false when it was already removed.
    /// </param>
    public static void SimulateInstantFall(
        IWorldAccessor world,
        Block block,
        BlockEntity be,
        BlockPos startPos,
        ItemStack[] drops,
        bool doRemoveBlock)
    {
        if (doRemoveBlock)
        {
            // Safety: the block may have changed while the request was queued
            if (world.BlockAccessor.GetBlock(startPos) != block)
                return;
            world.BlockAccessor.SetBlock(0, startPos);
        }

        BlockPos finalPos = startPos.Copy();

        // Serialise the block entity once so that CanAcceptFallOnto / OnFallOnto
        // handlers can inspect its data.
        TreeAttribute beTree = null;
        if (be != null)
        {
            beTree = new TreeAttribute();
            be.ToTreeAttributes(beTree);
        }

        int worldHeight = world.BlockAccessor.MapSizeY;

        // Descend through passable blocks (air, water, foliage, …)
        for (int i = 0; i < worldHeight; i++)
        {
            BlockPos belowPos = finalPos.DownCopy();
            Block belowBlock = world.BlockAccessor.GetMostSolidBlock(belowPos);

            // Let the target block handle the landing (e.g. hopper, loose soil)
            if (belowBlock.CanAcceptFallOnto(world, belowPos, block, beTree))
            {
                belowBlock.OnFallOnto(world, belowPos, block, beTree);
                return;
            }

            if (belowBlock.Replaceable >= 6000 || belowBlock.IsLiquid())
                finalPos = belowPos;  // passable — keep descending
            else
                break;               // solid — stop here
        }

        // Validate landing position: needs a solid block below and free space at finalPos
        Block targetBlock = world.BlockAccessor.GetBlock(finalPos);
        Block supportBlock = world.BlockAccessor.GetMostSolidBlock(finalPos.DownCopy());

        bool canPlace = supportBlock.Replaceable < 6000
                     && !supportBlock.IsLiquid()
                     && (targetBlock.IsLiquid() || targetBlock.Replaceable >= 6000);

        if (canPlace)
        {
            world.BlockAccessor.SetBlock(block.BlockId, finalPos);

            // Restore block-entity state at the new position
            if (be != null)
            {
                BlockEntity newBe = world.BlockAccessor.GetBlockEntity(finalPos);
                if (newBe != null)
                {
                    TreeAttribute tree = new TreeAttribute();
                    be.ToTreeAttributes(tree);
                    tree.SetInt("posx", finalPos.X);
                    tree.SetInt("posy", finalPos.InternalY);
                    tree.SetInt("posz", finalPos.Z);
                    newBe.FromTreeAttributes(tree, world);
                }
            }

            return;
        }

        // Nowhere valid to land — scatter items
        SpawnDrops(world, finalPos, drops, be);
    }
}