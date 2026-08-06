using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Lumos.Patches;

public static class DoorRelightPatches
{
    public const int FAKE_BLOCKER_ABSORPTION = 33;

    private static void KickDoor(BEBehaviorDoor door)
    {
        if (door?.Api?.World == null || door.doorBh == null) return;

        door.doorBh.IterateOverEach(door.Pos, door.RotateYRad, door.InvertHandles, pos =>
        {
            door.Api.World.BlockAccessor.MarkAbsorptionChanged(0, FAKE_BLOCKER_ABSORPTION, pos.Copy());
            return true;
        });
    }

    [HarmonyPatch(typeof(BEBehaviorDoor), "ToggleDoorState")]
    public static class DoorTogglePatch
    {
        [HarmonyPostfix]
        public static void Postfix(BEBehaviorDoor __instance) => KickDoor(__instance);
    }

    [HarmonyPatch(typeof(BEBehaviorDoor), "OnBlockPlaced")]
    public static class DoorPlacePatch
    {
        [HarmonyPostfix]
        public static void Postfix(BEBehaviorDoor __instance) => KickDoor(__instance);
    }


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

    private static void KickTrapDoor(BEBehaviorTrapDoor trapdoor)
    {
        if (trapdoor?.Api?.World == null) return;
        trapdoor.Api.World.BlockAccessor.MarkAbsorptionChanged(0, FAKE_BLOCKER_ABSORPTION, trapdoor.Pos.Copy());
    }

    [HarmonyPatch(typeof(BEBehaviorTrapDoor), "ToggleDoorState")]
    public static class TrapDoorTogglePatch
    {
        [HarmonyPostfix]
        public static void Postfix(BEBehaviorTrapDoor __instance) => KickTrapDoor(__instance);
    }

    [HarmonyPatch(typeof(BEBehaviorTrapDoor), "OnBlockPlaced")]
    public static class TrapDoorPlacePatch
    {
        [HarmonyPostfix]
        public static void Postfix(BEBehaviorTrapDoor __instance) => KickTrapDoor(__instance);
    }


    [HarmonyPatch(typeof(Block), "OnBlockRemoved")]
    public static class TrapDoorRemovePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Block __instance, IWorldAccessor world, BlockPos pos)
        {
            // Проверяем, является ли удаляемый блок люком
            var be = world.BlockAccessor.GetBlockEntity(pos);
            var trapdoor = be?.GetBehavior<BEBehaviorTrapDoor>();
            if (trapdoor != null)
            {
                KickTrapDoor(trapdoor);
            }
        }
    }
}