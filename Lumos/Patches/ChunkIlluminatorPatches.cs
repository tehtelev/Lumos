using HarmonyLib;
using Lumos.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace Lumos.Patches;

/// <summary>
/// Harmony-патчи на ванильный ChunkIlluminator.
/// 
/// Стратегия: ТОЛЬКО Prefix-патчи. Никаких Postfix на конструктор/InitForWorld.
/// Каждый Prefix вызывает LumosStateHolder.GetOrCreate(), который лениво создаёт
/// LumosChunkIlluminator при первом обращении. Это решает проблему тайминга:
/// ванильные ChunkIlluminator создаются ДО загрузки мода, но наши — при первом использовании.
/// </summary>
public static class ChunkIlluminatorPatches
{
    // ═══════════════════════════════════════════════════════════════════
    // 1. PREFIX: PlaceBlockLight
    // ═══════════════════════════════════════════════════════════════════
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

    // ═══════════════════════════════════════════════════════════════════
    // 2. PREFIX: RemoveBlockLight
    // ═══════════════════════════════════════════════════════════════════
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

    // ═══════════════════════════════════════════════════════════════════
    // 3. PREFIX: UpdateBlockLight
    // ═══════════════════════════════════════════════════════════════════
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

    // ═══════════════════════════════════════════════════════════════════
    // 4. PREFIX: FullRelight
    // ═══════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ChunkIlluminator), "FullRelight")]
    public static class FullRelight_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            BlockPos minPos, BlockPos maxPos)
        {
            LumosStateHolder.GetOrCreate(__instance)
                .FullRelight(minPos, maxPos);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. PREFIX: Sunlight
    // ═══════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ChunkIlluminator), "Sunlight")]
    public static class Sunlight_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            IWorldChunk[] chunks, int chunkX, int chunkY, int chunkZ, int dim)
        {
            LumosStateHolder.GetOrCreate(__instance)
                .Sunlight(chunks, chunkX, chunkY, chunkZ, dim);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. PREFIX: SunlightFlood
    // ═══════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ChunkIlluminator), "SunlightFlood")]
    public static class SunlightFlood_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            IWorldChunk[] chunks, int chunkX, int chunkY, int chunkZ)
        {
            LumosStateHolder.GetOrCreate(__instance)
                .SunlightFlood(chunks, chunkX, chunkY, chunkZ);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. PREFIX: SunLightFloodNeighbourChunks
    // ═══════════════════════════════════════════════════════════════════
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

    // ═══════════════════════════════════════════════════════════════════
    // 8. PREFIX: UpdateSunLight
    // ═══════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ChunkIlluminator), "UpdateSunLight")]
    public static class UpdateSunLight_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            int posX, int posY, int posZ, int oldAbsorb, int newAbsorb,
            ref FastSetOfLongs __result)
        {
            __result = LumosStateHolder.GetOrCreate(__instance)
                .UpdateSunLight(posX, posY, posZ, oldAbsorb, newAbsorb);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 9. PREFIX: IsDirectlyIlluminated
    // ═══════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ChunkIlluminator), "IsDirectlyIlluminated")]
    public static class IsDirectlyIlluminated_Prefix
    {
        static bool Prefix(
            ChunkIlluminator __instance,
            int posX, int posY, int posZ,
            ref bool __result)
        {
            __result = LumosStateHolder.GetOrCreate(__instance)
                .IsDirectlyIlluminated(posX, posY, posZ);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 10. PREFIX: SpreadSunlightAt
    // ═══════════════════════════════════════════════════════════════════
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

    // ═══════════════════════════════════════════════════════════════════
    // 11. PREFIX: ClearSunlightAt
    // ═══════════════════════════════════════════════════════════════════
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
}