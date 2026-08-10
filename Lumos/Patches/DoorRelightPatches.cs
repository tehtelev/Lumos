using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Lumos.Patches;

public static class DoorRelightPatches
{
    /// <summary>Поглощение "фантомного" блока-призрака, через который пробрасывается свет для пересчёта.</summary>
    public const int FAKE_BLOCKER_ABSORPTION = 33;

    /// <summary>
    /// Пробрасывает FAKE_BLOCKER_ABSORPTION через все позиции двери,
    /// чтобы MarkAbsorptionChanged триггернул пересчёт света для всей области.
    /// Двойные двери занимают 2 позиции — IterateOverEach проходит по обоим.
    /// </summary>
    private static void KickDoor(BEBehaviorDoor door)
    {
        if (door?.Api?.World == null || door.doorBh == null) return;

        door.doorBh.IterateOverEach(door.Pos, door.RotateYRad, door.InvertHandles, pos =>
        {
            door.Api.World.BlockAccessor.MarkAbsorptionChanged(0, FAKE_BLOCKER_ABSORPTION, pos.Copy());
            return true;
        });
    }

    /// <summary>Перекрытие состояния двери → пересчёт света вокруг.</summary>
    [HarmonyPatch(typeof(BEBehaviorDoor), "ToggleDoorState")]
    public static class DoorTogglePatch
    {
        [HarmonyPostfix]
        public static void Postfix(BEBehaviorDoor __instance) => KickDoor(__instance);
    }

    /// <summary>Установка двери → пересчёт света вокруг.</summary>
    [HarmonyPatch(typeof(BEBehaviorDoor), "OnBlockPlaced")]
    public static class DoorPlacePatch
    {
        [HarmonyPostfix]
        public static void Postfix(BEBehaviorDoor __instance) => KickDoor(__instance);
    }

    /// <summary>Удаление двери: если есть BE — пробрасываем поглощение; иначе ставим 0 (блок удалён).</summary>
    [HarmonyPatch(typeof(BlockBehaviorDoor), "OnBlockRemoved")]
    public static class DoorRemovePatch
    {
        [HarmonyPrefix]
        public static void Prefix(BlockBehaviorDoor __instance, IWorldAccessor world, BlockPos pos, ref EnumHandling handling)
        {
            var be = world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorDoor>();
            if (be != null) KickDoor(be);
            else world.BlockAccessor.MarkAbsorptionChanged(FAKE_BLOCKER_ABSORPTION, 0, pos);
        }

    }

    /// <summary>
    /// То же, что KickDoor, но для люка (всегда 1 позиция).
    /// </summary>
    private static void KickTrapDoor(BEBehaviorTrapDoor trapdoor)
    {
        if (trapdoor?.Api?.World == null) return;
        trapdoor.Api.World.BlockAccessor.MarkAbsorptionChanged(0, FAKE_BLOCKER_ABSORPTION, trapdoor.Pos.Copy());
    }

    /// <summary>Перекрытие люка → пересчёт света.</summary>
    [HarmonyPatch(typeof(BEBehaviorTrapDoor), "ToggleDoorState")]
    public static class TrapDoorTogglePatch
    {
        [HarmonyPostfix]
        public static void Postfix(BEBehaviorTrapDoor __instance) => KickTrapDoor(__instance);
    }

    /// <summary>Установка люка → пересчёт света.</summary>
    [HarmonyPatch(typeof(BEBehaviorTrapDoor), "OnBlockPlaced")]
    public static class TrapDoorPlacePatch
    {
        [HarmonyPostfix]
        public static void Postfix(BEBehaviorTrapDoor __instance) => KickTrapDoor(__instance);
    }

    /// <summary>
    /// Удаление блока, который может быть люком.
    /// Проверяем через BlockEntity, т.к. OnBlockRemoved вызывается и для других блоков.
    /// </summary>
    [HarmonyPatch(typeof(Block), "OnBlockRemoved")]
    public static class TrapDoorRemovePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Block __instance, IWorldAccessor world, BlockPos pos)
        {
            var be = world.BlockAccessor.GetBlockEntity(pos);
            var trapdoor = be?.GetBehavior<BEBehaviorTrapDoor>();
            if (trapdoor != null)
            {
                KickTrapDoor(trapdoor);
            }
        }
    }
}