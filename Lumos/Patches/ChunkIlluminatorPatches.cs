using HarmonyLib;
using Lumos.Core;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Lumos.Patches;

/// <summary>
/// Harmony-патчи на ванильный ChunkIlluminator. Стратегия: Prefix-патчи,
/// лениво создающие LumosChunkIlluminator через LumosStateHolder.GetOrCreate().
/// </summary>
public static class ChunkIlluminatorPatches
{
    [HarmonyPatch(typeof(ChunkIlluminator), "InitForWorld")]
    public static class InitForWorld_Postfix
    {
        static void Postfix(
            ChunkIlluminator __instance,
            IList<Block> blockTypes,
            ushort defaultSunLight,
            int mapsizex, int mapsizey, int mapsizez)
        {
            if (LumosStateHolder.TryGet(__instance, out var lumos))
            {
                lumos.InitForWorld(blockTypes, defaultSunLight, mapsizex, mapsizey, mapsizez);
            }
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), MethodType.Constructor, new[] { typeof(IChunkProvider), typeof(IBlockAccessor), typeof(int) })]
    public static class ChunkIlluminator_Constructor_Postfix
    {
        static void Postfix(ChunkIlluminator __instance, IBlockAccessor readBlockAccess)
        {
            LumosStateHolder.RegisterBlockAccessor(readBlockAccess, __instance);
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "PlaceBlockLight")]
    public static class PlaceBlockLight_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            byte[] lightHsv, int posX, int posY, int posZ,
            ref FastSetOfLongs __result)
        {
            __result = LumosStateHolder.GetOrCreate(__instance)
                .PlaceBlockLight(lightHsv, posX, posY, posZ);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "RemoveBlockLight")]
    public static class RemoveBlockLight_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            byte[] oldLightHsv, int posX, int posY, int posZ,
            ref FastSetOfLongs __result)
        {
            __result = LumosStateHolder.GetOrCreate(__instance)
                .RemoveBlockLight(oldLightHsv, posX, posY, posZ);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "UpdateBlockLight")]
    public static class UpdateBlockLight_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            int oldLightAbsorb, int newLightAbsorb, int posX, int posY, int posZ,
            ref FastSetOfLongs __result)
        {
            __result = LumosStateHolder.GetOrCreate(__instance)
                .UpdateBlockLight(oldLightAbsorb, newLightAbsorb, posX, posY, posZ);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "FullRelight")]
    public static class FullRelight_Prefix
    {
        static bool Prefix(ChunkIlluminator __instance, BlockPos minPos, BlockPos maxPos)
        {
            LumosStateHolder.GetOrCreate(__instance).FullRelight(minPos, maxPos);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "Sunlight")]
    public static class Sunlight_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            IWorldChunk[] chunks, int chunkX, int chunkY, int chunkZ, int dim)
        {
            LumosStateHolder.GetOrCreate(__instance).Sunlight(chunks, chunkX, chunkY, chunkZ, dim);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "SunlightFlood")]
    public static class SunlightFlood_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            IWorldChunk[] chunks, int chunkX, int chunkY, int chunkZ)
        {
            LumosStateHolder.GetOrCreate(__instance).SunlightFlood(chunks, chunkX, chunkY, chunkZ);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "SunLightFloodNeighbourChunks")]
    public static class SunLightFloodNeighbourChunks_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            IWorldChunk[] curChunks, int chunkX, int chunkY, int chunkZ, int dimension,
            ref byte __result)
        {
            __result = LumosStateHolder.GetOrCreate(__instance)
                .SunLightFloodNeighbourChunks(curChunks, chunkX, chunkY, chunkZ, dimension);
            return false;
        }
    }

    /// <summary>
    /// Фейковое поглощение 33 от дверей/люков: дублируем в UpdateBlockLight
    /// (блочный свет) И в UpdateSunLight (солнечный свет — двери его тоже блокируют).
    /// </summary>
    [HarmonyPatch(typeof(ChunkIlluminator), "UpdateSunLight")]
    public static class UpdateSunLight_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            int posX, int posY, int posZ, int oldAbsorb, int newAbsorb,
            ref FastSetOfLongs __result)
        {
            var lumos = LumosStateHolder.GetOrCreate(__instance);

            if (oldAbsorb == DoorRelightPatches.FAKE_BLOCKER_ABSORPTION ||
                newAbsorb == DoorRelightPatches.FAKE_BLOCKER_ABSORPTION)
            {
                lumos.UpdateBlockLight(oldAbsorb, newAbsorb, posX, posY, posZ);
            }

            __result = lumos.UpdateSunLight(posX, posY, posZ, oldAbsorb, newAbsorb);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "IsDirectlyIlluminated")]
    public static class IsDirectlyIlluminated_Prefix
    {
        static bool Prefix(ChunkIlluminator __instance, int posX, int posY, int posZ, ref bool __result)
        {
            __result = LumosStateHolder.GetOrCreate(__instance).IsDirectlyIlluminated(posX, posY, posZ);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "SpreadSunlightAt")]
    public static class SpreadSunlightAt_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            QueueOfInt unhandledPositions, BlockPos centerPos,
            bool isDirectlyIlluminated, FastSetOfLongs touchedChunks)
        {
            LumosStateHolder.GetOrCreate(__instance)
                .SpreadSunlightAt(unhandledPositions, centerPos, isDirectlyIlluminated, touchedChunks);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChunkIlluminator), "ClearSunlightAt")]
    public static class ClearSunlightAt_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            QueueOfInt positionsToClear, BlockPos centerPos,
            bool isDirectlyIlluminated,
            QueueOfInt retainedLightToSpread, FastSetOfLongs touchedChunks)
        {
            LumosStateHolder.GetOrCreate(__instance)
                .ClearSunlightAt(positionsToClear, centerPos, isDirectlyIlluminated,
                    retainedLightToSpread, touchedChunks);
            return false;
        }
    }

    /// <summary>Серверный постфикс: сброс накопленного пакета в конце обработки очереди.</summary>
    [HarmonyPatch(typeof(ServerSystemRelight), nameof(ServerSystemRelight.ProcessLightingQueue))]
    public static class ProcessLightingQueue_Postfix
    {
        static void Postfix(ServerSystemRelight __instance)
        {
            if (__instance.chunkIlluminator == null) return;
            if (!LumosStateHolder.TryGet(__instance.chunkIlluminator, out LumosChunkIlluminator lumos)) return;
            lumos.FlushPendingLightUpdates();
        }
    }
}