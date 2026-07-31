using HarmonyLib;
using Lumos.Core;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;

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
    // POSTFIX: InitForWorld
    // Обновляет LumosChunkIlluminator, если он уже создан через GetOrCreate
    // ДО вызова InitForWorld (lazy init сработал раньше инициализации мира)
    // ═══════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ChunkIlluminator), "InitForWorld")]
    public static class InitForWorld_Postfix
    {
        static void Postfix(
            ChunkIlluminator __instance,
            IList<Block> blockTypes,
            ushort defaultSunLight,
            int mapsizex, int mapsizey, int mapsizez)
        {
            // Если LumosChunkIlluminator уже создан — обновляем
            if (LumosStateHolder.TryGet(__instance, out var lumos))
            {
                lumos.InitForWorld(blockTypes, defaultSunLight, mapsizex, mapsizey, mapsizez);
            }
            // Если ещё не создан — GetOrCreate прочитает актуальные поля позже
        }
    }


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

    [HarmonyPatch(
        typeof(ServerSystemRelight),
        nameof(ServerSystemRelight.ProcessLightingQueue)
    )]
    public static class ProcessLightingQueue_Postfix
    {
        static void Postfix(
            ServerSystemRelight __instance)
        {
            if (__instance.chunkIlluminator == null)
                return;

            if (!LumosStateHolder.TryGet(
                    __instance.chunkIlluminator,
                    out LumosChunkIlluminator lumos))
            {
                return;
            }

            lumos.FlushPendingBlockLightUpdates();
        }
    }


}





/// <summary>
/// Client-side integration for Lumos batched block-light updates.
///
/// IMPORTANT:
/// We must intercept ProcessLightingTask itself, not only ProcessLightingQueue.
/// Vanilla ProcessLightingTask calls WorldMap.SetChunkDirty() BEFORE our batched
/// Lumos flush would normally run. That creates a race:
///
///     SetChunkDirty(old lighting)
///             -> Lumos changes lighting
///             -> client mesh rebuild may already be queued with old values
///
/// Therefore this patch completely replaces the vanilla ProcessLightingTask.
/// It performs the same lighting operations, but defers all client chunk-dirty
/// notifications until the entire lighting-task queue has been drained and the
/// Lumos batch has been flushed.
/// </summary>
public static class ClientSystemRelightPatches
{
    private sealed class BatchState
    {
        // Normal dirty chunks: full mesh update.
        // Value = whether the update should be high priority.
        public readonly Dictionary<long, bool> RegularDirtyChunks =
            new Dictionary<long, bool>(128);

        // Neighbouring chunks that only need the edge update, matching vanilla.
        public readonly Dictionary<long, bool> EdgeOnlyDirtyChunks =
            new Dictionary<long, bool>(256);

        public void Clear()
        {
            RegularDirtyChunks.Clear();
            EdgeOnlyDirtyChunks.Clear();
        }

        public void AddRegular(long chunkIndex, bool priority)
        {
            if (RegularDirtyChunks.TryGetValue(chunkIndex, out bool existing))
            {
                RegularDirtyChunks[chunkIndex] = existing || priority;
                EdgeOnlyDirtyChunks.Remove(chunkIndex);
                return;
            }

            RegularDirtyChunks.Add(chunkIndex, priority);
            EdgeOnlyDirtyChunks.Remove(chunkIndex);
        }

        public void AddEdgeOnly(long chunkIndex, bool priority)
        {
            // A full dirty update always wins over edge-only.
            if (RegularDirtyChunks.ContainsKey(chunkIndex))
                return;

            if (EdgeOnlyDirtyChunks.TryGetValue(chunkIndex, out bool existing))
            {
                EdgeOnlyDirtyChunks[chunkIndex] = existing || priority;
                return;
            }

            EdgeOnlyDirtyChunks.Add(chunkIndex, priority);
        }
    }

    private static readonly ConditionalWeakTable<ClientSystemRelight, BatchState> States =
        new ConditionalWeakTable<ClientSystemRelight, BatchState>();

    private static readonly FieldInfo ChunkIlluminatorField =
        FindField(typeof(ClientSystemRelight), "chunkIlluminator");

    private static readonly FieldInfo GameField =
        FindField(typeof(ClientSystemRelight), "game");

    private static FieldInfo FindField(Type type, string fieldName)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(
                fieldName,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly
            );

            if (field != null)
                return field;

            type = type.BaseType;
        }

        return null;
    }

    private static BatchState GetState(ClientSystemRelight instance)
    {
        return States.GetOrCreateValue(instance);
    }

    private static ChunkIlluminator GetChunkIlluminator(
        ClientSystemRelight instance)
    {
        if (ChunkIlluminatorField == null)
            return null;

        return ChunkIlluminatorField.GetValue(instance)
            as ChunkIlluminator;
    }

    private static ClientMain GetGame(
        ClientSystemRelight instance)
    {
        if (GameField == null)
            return null;

        return GameField.GetValue(instance)
            as ClientMain;
    }

    private static LumosChunkIlluminator GetLumos(
        ClientSystemRelight instance)
    {
        ChunkIlluminator chunkIlluminator =
            GetChunkIlluminator(instance);

        if (chunkIlluminator == null)
            return null;

        return LumosStateHolder.GetOrCreate(
            chunkIlluminator
        );
    }

    private static void AddReturnedChunks(
        BatchState state,
        FastSetOfLongs chunks,
        bool priority)
    {
        if (chunks == null)
            return;

        foreach (long chunkIndex in chunks)
        {
            state.AddRegular(
                chunkIndex,
                priority
            );
        }
    }

    private static void AddVanillaNeighbourDirtyChunks(
        BatchState state,
        ClientMain game,
        int x,
        int y,
        int z,
        bool priority)
    {
        if (game?.WorldMap == null)
            return;

        long centerChunkIndex =
            game.WorldMap.ChunkIndex3D(
                new ChunkPos(
                    x / 32,
                    y / 32,
                    z / 32
                )
            );

        // The center/source chunk itself needs a full update.
        state.AddRegular(
            centerChunkIndex,
            priority
        );

        // Match vanilla's edge-only neighbour invalidation.
        for (int i = -1; i < 2; i++)
        {
            for (int j = -1; j < 2; j++)
            {
                for (int k = -1; k < 2; k++)
                {
                    if (i == 0 && j == 0 && k == 0)
                        continue;

                    long neighbourChunkIndex =
                        game.WorldMap.ChunkIndex3D(
                            new ChunkPos(
                                (x + i) / 32,
                                (y + j) / 32,
                                (z + k) / 32
                            )
                        );

                    if (neighbourChunkIndex == centerChunkIndex)
                        continue;

                    state.AddEdgeOnly(
                        neighbourChunkIndex,
                        priority
                    );
                }
            }
        }
    }

    private static void ProcessLightingTaskDeferred(
        ClientSystemRelight instance,
        EntityPos playerPos,
        UpdateLightingTask task)
    {
        ClientMain game =
            GetGame(instance);

        LumosChunkIlluminator lumos =
            GetLumos(instance);

        if (game == null ||
            game.WorldMap == null ||
            lumos == null ||
            task == null)
        {
            return;
        }

        BatchState state =
            GetState(instance);

        int x = task.pos.X;
        int internalY = task.pos.InternalY;
        int z = task.pos.Z;

        bool priority =
            playerPos != null &&
            playerPos.SquareDistanceTo(
                x,
                internalY,
                z
            ) < 2304f;

        int oldAbsorb = 0;
        int newAbsorb = 0;
        bool blockLightChanged = false;

        if (task.absorbUpdate)
        {
            oldAbsorb = task.oldAbsorb;
            newAbsorb = task.newAbsorb;
        }
        else if (task.removeLightHsv != null)
        {
            blockLightChanged = true;

            AddReturnedChunks(
                state,
                lumos.RemoveBlockLight(
                    task.removeLightHsv,
                    x,
                    internalY,
                    z
                ),
                priority
            );
        }
        else
        {
            Block oldBlock =
                game.Blocks[task.oldBlockId];

            Block newBlock =
                game.Blocks[task.newBlockId];

            byte[] oldLightHsv =
                oldBlock.GetLightHsv(
                    game.BlockAccessor,
                    task.pos
                );

            byte[] newLightHsv =
                newBlock.GetLightHsv(
                    game.BlockAccessor,
                    task.pos
                );

            if (oldLightHsv[2] > 0)
            {
                blockLightChanged = true;

                AddReturnedChunks(
                    state,
                    lumos.RemoveBlockLight(
                        oldLightHsv,
                        x,
                        internalY,
                        z
                    ),
                    priority
                );
            }

            if (newLightHsv[2] > 0)
            {
                blockLightChanged = true;

                AddReturnedChunks(
                    state,
                    lumos.PlaceBlockLight(
                        newLightHsv,
                        x,
                        internalY,
                        z
                    ),
                    priority
                );
            }

            oldAbsorb =
                oldBlock.GetLightAbsorption(
                    game.BlockAccessor,
                    task.pos
                );

            newAbsorb =
                newBlock.GetLightAbsorption(
                    game.BlockAccessor,
                    task.pos
                );

            if (oldLightHsv[2] == 0 &&
                newLightHsv[2] == 0 &&
                oldAbsorb != newAbsorb)
            {
                AddReturnedChunks(
                    state,
                    lumos.UpdateBlockLight(
                        oldAbsorb,
                        newAbsorb,
                        x,
                        internalY,
                        z
                    ),
                    priority
                );
            }
        }

        bool sunlightChanged =
            oldAbsorb != newAbsorb;

        if (sunlightChanged)
        {
            AddReturnedChunks(
                state,
                lumos.UpdateSunLight(
                    x,
                    internalY,
                    z,
                    oldAbsorb,
                    newAbsorb
                ),
                priority
            );
        }

        if (sunlightChanged ||
            blockLightChanged)
        {
            AddVanillaNeighbourDirtyChunks(
                state,
                game,
                x,
                internalY,
                z,
                priority
            );
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // PREFIX: replace vanilla ProcessLightingTask completely.
    //
    // The vanilla method dirties chunks BEFORE our batch is flushed.
    // We suppress it and defer all SetChunkDirty calls until the queue
    // has been fully consumed and Lumos has finished recalculating light.
    // ═══════════════════════════════════════════════════════════════════
    [HarmonyPatch(
        typeof(ClientSystemRelight),
        "ProcessLightingTask"
    )]
    public static class ProcessLightingTask_Prefix
    {
        static bool Prefix(
            ClientSystemRelight __instance,
            EntityPos playerPos,
            UpdateLightingTask task)
        {
            ProcessLightingTaskDeferred(
                __instance,
                playerPos,
                task
            );

            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // POSTFIX: ProcessLightingQueue
    //
    // 1. Flush the complete Lumos batch.
    // 2. Mark all chunks whose Lighting data changed as full dirty.
    // 3. Mark vanilla neighbour chunks as edge-only dirty.
    // ═══════════════════════════════════════════════════════════════════
    [HarmonyPatch(
        typeof(ClientSystemRelight),
        nameof(ClientSystemRelight.ProcessLightingQueue)
    )]
    public static class ProcessLightingQueue_Postfix
    {
        static void Postfix(
            ClientSystemRelight __instance)
        {
            ClientMain game =
                GetGame(__instance);

            LumosChunkIlluminator lumos =
                GetLumos(__instance);

            if (game == null ||
                game.WorldMap == null ||
                lumos == null)
            {
                return;
            }

            BatchState state =
                GetState(__instance);

            // The actual light calculation happens now, after ALL tasks
            // from the current queue drain have been collected.
            FastSetOfLongs touchedChunks =
                lumos.FlushPendingBlockLightUpdates();

            // Any chunk whose lighting array changed gets a full rebuild.
            AddReturnedChunks(
                state,
                touchedChunks,
                true
            );

            // Now, and only now, tell the client renderer that the data changed.
            foreach (
                KeyValuePair<long, bool> pair
                in state.RegularDirtyChunks)
            {
                game.WorldMap.SetChunkDirty(
                    pair.Key,
                    pair.Value,
                    relight: false,
                    edgeOnly: false
                );
            }

            foreach (
                KeyValuePair<long, bool> pair
                in state.EdgeOnlyDirtyChunks)
            {
                if (state.RegularDirtyChunks.ContainsKey(
                    pair.Key))
                {
                    continue;
                }

                game.WorldMap.SetChunkDirty(
                    pair.Key,
                    pair.Value,
                    relight: false,
                    edgeOnly: true
                );
            }

            state.Clear();
        }
    }
}

