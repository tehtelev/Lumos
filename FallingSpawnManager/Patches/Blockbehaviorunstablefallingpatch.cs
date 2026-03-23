using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace FallingSpawnManager.Patches;

public static class BlockBehaviorUnstableFallingPatch
{
    // Поля из оригинального класса
    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableFalling, bool> _fallSidewaysRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableFalling, bool>("fallSideways");
    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableFalling, float> _fallSidewaysChanceRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableFalling, float>("fallSidewaysChance");
    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableFalling, AssetLocation> _fallSoundRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableFalling, AssetLocation>("fallSound");
    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableFalling, float> _impactDamageMulRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableFalling, float>("impactDamageMul");
    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableFalling, float> _dustIntensityRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableFalling, float>("dustIntensity");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockBehaviorUnstableFalling), "TryFalling")]
    public static bool TryFalling_Prefix(
        BlockBehaviorUnstableFalling __instance,
        IWorldAccessor world,
        BlockPos pos,
        ref EnumHandling handling)
    {
        // Только сервер
        if (world.Side != EnumAppSide.Server)
            return true;

        var sapi = world.Api as ICoreServerAPI;
        if (sapi == null || !sapi.World.Config.GetBool("allowFallingBlocks"))
            return true;

        bool fallSideways = _fallSidewaysRef(__instance);
        float fallSidewaysChance = _fallSidewaysChanceRef(__instance);

        // Если блок не падает вбок и прикреплён — ничего не делаем
        if (!fallSideways && __instance.IsAttached(world.BlockAccessor, pos))
            return true;

        bool canFall = false;
        // Проверка на пустоту снизу
        if (IsReplacableBeneath(world, pos))
        {
            canFall = true;
        }
        // Или падение вбок с шансом
        else if (fallSideways && world.Rand.NextDouble() < fallSidewaysChance && IsReplacableBeneathAndSideways(world, pos))
        {
            canFall = true;
        }

        if (!canFall)
            return true;


        var fsm = world.Api.ModLoader.GetModSystem<FallingSpawnManager>();
        if (fsm == null)
            return true; // если менеджер не загружен, пусть работает оригинал

        Block block = __instance.block;
        BlockEntity be = world.BlockAccessor.GetBlockEntity(pos);

        // Заменяем спавн на вызов нашего менеджера
        fsm.RequestSpawn(
            block,
            be,
            pos,
            _fallSoundRef(__instance),
            _impactDamageMulRef(__instance),
            canFallSideways: true,      // в оригинале всегда true
            _dustIntensityRef(__instance),
            doRemoveBlock: true,
            positionOffset: null
        );

        // Сообщаем Harmony, что оригинальный метод вызывать не нужно
        handling = EnumHandling.PreventSubsequent;
        return false;
    }

    // Вспомогательные методы (копируют логику приватных методов оригинала)
    private static bool IsReplacableBeneath(IWorldAccessor world, BlockPos pos)
    {
        return world.BlockAccessor.GetBlockBelow(pos).Replaceable > 6000;
    }

    private static bool IsReplacableBeneathAndSideways(IWorldAccessor world, BlockPos pos)
    {
        for (int i = 0; i < 4; i++)
        {
            BlockFacing facing = BlockFacing.HORIZONTALS[i];
            BlockPos sidePos = pos.AddCopy(facing);
            Block blockOrNull = world.BlockAccessor.GetBlockOrNull(sidePos.X, sidePos.Y, sidePos.Z);
            if (blockOrNull != null && blockOrNull.Replaceable >= 6000)
            {
                BlockPos belowSide = sidePos.DownCopy();
                Block below = world.BlockAccessor.GetBlockOrNull(belowSide.X, belowSide.Y, belowSide.Z);
                if (below != null && below.Replaceable >= 6000)
                    return true;
            }
        }
        return false;
    }
}