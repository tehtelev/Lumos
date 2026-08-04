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
    /// <summary>
    /// Постфикс InitForWorld: обновляет LumosChunkIlluminator, если он уже создан через GetOrCreate
    /// ДО вызова InitForWorld (ленивая инициализация сработала раньше инициализации мира).
    /// </summary>
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

    /// <summary>Перехват установки источника блочного света.</summary>
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

    /// <summary>Перехват удаления источника блочного света.</summary>
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

    /// <summary>Перехват обновления поглощения блочного света.</summary>
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

    /// <summary>Перехват полного пересчета света в области.</summary>
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

    /// <summary>Перехват прямого солнечного света (проход сверху вниз).</summary>
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

    /// <summary>Перехват горизонтального распространения солнечного света (BFS).</summary>
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

    /// <summary>Перехват обмена солнечным светом между соседними чанками.</summary>
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

    /// <summary>Перехват обновления солнечного света при изменении прозрачности блока.</summary>
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

    /// <summary>Перехват проверки прямого солнечного освещения блока.</summary>
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

    /// <summary>Перехват инкрементального распространения солнечного света (BFS).</summary>
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

    /// <summary>Перехват гашения солнечного света при появлении препятствия (BFS).</summary>
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

    /// <summary>
    /// Серверный постфикс обработки очереди освещения.
    /// Сбрасывает накопленный пакет блочного и солнечного света в конце тика.
    /// </summary>
    [HarmonyPatch(
        typeof(ServerSystemRelight),
        nameof(ServerSystemRelight.ProcessLightingQueue)
    )]
    public static class ProcessLightingQueue_Postfix
    {
        static void Postfix(ServerSystemRelight __instance)
        {
            if (__instance.chunkIlluminator == null) return;

            if (!LumosStateHolder.TryGet(__instance.chunkIlluminator, out LumosChunkIlluminator lumos))
                return;

            lumos.FlushPendingLightUpdates();
        }
    }
}

/// <summary>
/// Клиентская интеграция для пакетных обновлений света Lumos.
/// 
/// ВАЖНО: Мы должны перехватывать сам ProcessLightingTask, а не только ProcessLightingQueue.
/// Ванильный ProcessLightingTask вызывает WorldMap.SetChunkDirty() ДО того, как наш пакетный
/// Lumos-флуш обычно выполняется. Это создает гонку (race condition):
///     SetChunkDirty(old lighting) -> Lumos changes lighting -> client mesh rebuild queued with old values.
/// 
/// Поэтому этот патч полностью заменяет ванильный ProcessLightingTask.
/// Он выполняет те же операции со светом, но откладывает все уведомления о "грязных" чанках
/// до тех пор, пока вся очередь задач не будет опустошена и пакет Lumos не будет сброшен.
/// </summary>
public static class ClientSystemRelightPatches
{
    /// <summary>
    /// Состояние батчинга для накопления "грязных" чанков в рамках одного тика.
    /// Разделяет чанки на требующие полного обновления меша и только обновления границ (edge-only).
    /// </summary>
    private sealed class BatchState
    {
        /// <summary>Обычные "грязные" чанки: полное обновление меша. Значение = приоритет.</summary>
        public readonly Dictionary<long, bool> RegularDirtyChunks = new(128);

        /// <summary>Соседние чанки, требующие только обновления границ (как в ванили).</summary>
        public readonly Dictionary<long, bool> EdgeOnlyDirtyChunks = new(256);

        /// <summary>Очищает словари для следующего тика.</summary>
        public void Clear()
        {
            RegularDirtyChunks.Clear();
            EdgeOnlyDirtyChunks.Clear();
        }

        /// <summary>Добавляет чанк в список требующих полного обновления.</summary>
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

        /// <summary>Добавляет чанк в список требующих только обновления границ.</summary>
        public void AddEdgeOnly(long chunkIndex, bool priority)
        {
            // Полное обновление всегда имеет приоритет над edge-only.
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

    /// <summary>Хранилище состояний батчинга, привязанное к экземпляру ClientSystemRelight.</summary>
    private static readonly ConditionalWeakTable<ClientSystemRelight, BatchState> States = new();

    private static readonly FieldInfo ChunkIlluminatorField = FindField(typeof(ClientSystemRelight), "chunkIlluminator");
    private static readonly FieldInfo GameField = FindField(typeof(ClientSystemRelight), "game");

    /// <summary>Ищет приватное поле по имени во всей иерархии наследования.</summary>
    private static FieldInfo FindField(Type type, string fieldName)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
            );

            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    /// <summary>Возвращает или создает состояние батчинга для системы.</summary>
    private static BatchState GetState(ClientSystemRelight instance) => States.GetOrCreateValue(instance);

    /// <summary>Извлекает ванильный ChunkIlluminator через рефлексию.</summary>
    private static ChunkIlluminator GetChunkIlluminator(ClientSystemRelight instance)
    {
        return ChunkIlluminatorField?.GetValue(instance) as ChunkIlluminator;
    }

    /// <summary>Извлекает экземпляр ClientMain через рефлексию.</summary>
    private static ClientMain GetGame(ClientSystemRelight instance)
    {
        return GameField?.GetValue(instance) as ClientMain;
    }

    /// <summary>Возвращает наш LumosChunkIlluminator, привязанный к ванильному.</summary>
    private static LumosChunkIlluminator GetLumos(ClientSystemRelight instance)
    {
        ChunkIlluminator chunkIlluminator = GetChunkIlluminator(instance);
        return chunkIlluminator == null ? null : LumosStateHolder.GetOrCreate(chunkIlluminator);
    }

    /// <summary>Добавляет чанки, возвращенные методами Lumos, в состояние батчинга как регулярные.</summary>
    private static void AddReturnedChunks(BatchState state, FastSetOfLongs chunks, bool priority)
    {
        if (chunks == null) return;
        foreach (long chunkIndex in chunks)
            state.AddRegular(chunkIndex, priority);
    }

    /// <summary>
    /// Добавляет соседние чанки для ванильной инвалидации границ (edge-only).
    /// Центральный чанк помечается как требующий полного обновления.
    /// </summary>
    private static void AddVanillaNeighbourDirtyChunks(BatchState state, ClientMain game, int x, int y, int z, bool priority)
    {
        if (game?.WorldMap == null) return;

        long centerChunkIndex = game.WorldMap.ChunkIndex3D(new ChunkPos(x / 32, y / 32, z / 32));
        state.AddRegular(centerChunkIndex, priority);

        for (int i = -1; i < 2; i++)
        {
            for (int j = -1; j < 2; j++)
            {
                for (int k = -1; k < 2; k++)
                {
                    if (i == 0 && j == 0 && k == 0) continue;

                    long neighbourChunkIndex = game.WorldMap.ChunkIndex3D(
                        new ChunkPos((x + i) / 32, (y + j) / 32, (z + k) / 32));

                    if (neighbourChunkIndex == centerChunkIndex) continue;
                    state.AddEdgeOnly(neighbourChunkIndex, priority);
                }
            }
        }
    }

    /// <summary>
    /// Отложенная обработка задачи освещения. Выполняет операции со светом,
    /// но не вызывает SetChunkDirty, накапливая изменения в BatchState.
    /// </summary>
    private static void ProcessLightingTaskDeferred(
        ClientSystemRelight instance,
        EntityPos playerPos,
        UpdateLightingTask task)
    {
        ClientMain game = GetGame(instance);
        LumosChunkIlluminator lumos = GetLumos(instance);

        if (game == null || game.WorldMap == null || lumos == null || task == null)
            return;

        BatchState state = GetState(instance);

        int x = task.pos.X;
        int internalY = task.pos.InternalY;
        int z = task.pos.Z;

        // Приоритет для блоков рядом с игроком (радиус 48 блоков, 48^2 = 2304)
        bool priority = playerPos != null && playerPos.SquareDistanceTo(x, internalY, z) < 2304f;

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
            AddReturnedChunks(state, lumos.RemoveBlockLight(task.removeLightHsv, x, internalY, z), priority);
        }
        else
        {
            Block oldBlock = game.Blocks[task.oldBlockId];
            Block newBlock = game.Blocks[task.newBlockId];

            byte[] oldLightHsv = oldBlock.GetLightHsv(game.BlockAccessor, task.pos);
            byte[] newLightHsv = newBlock.GetLightHsv(game.BlockAccessor, task.pos);

            if (oldLightHsv[2] > 0)
            {
                blockLightChanged = true;
                AddReturnedChunks(state, lumos.RemoveBlockLight(oldLightHsv, x, internalY, z), priority);
            }

            if (newLightHsv[2] > 0)
            {
                blockLightChanged = true;
                AddReturnedChunks(state, lumos.PlaceBlockLight(newLightHsv, x, internalY, z), priority);
            }

            oldAbsorb = oldBlock.GetLightAbsorption(game.BlockAccessor, task.pos);
            newAbsorb = newBlock.GetLightAbsorption(game.BlockAccessor, task.pos);

            if (oldLightHsv[2] == 0 && newLightHsv[2] == 0 && oldAbsorb != newAbsorb)
            {
                AddReturnedChunks(state, lumos.UpdateBlockLight(oldAbsorb, newAbsorb, x, internalY, z), priority);
            }
        }

        bool sunlightChanged = oldAbsorb != newAbsorb;

        if (sunlightChanged)
        {
            AddReturnedChunks(state, lumos.UpdateSunLight(x, internalY, z, oldAbsorb, newAbsorb), priority);
        }

        if (sunlightChanged || blockLightChanged)
        {
            AddVanillaNeighbourDirtyChunks(state, game, x, internalY, z, priority);
        }
    }

    /// <summary>
    /// Префикс: полностью заменяет ванильный ProcessLightingTask.
    /// Блокирует немедленную пометку чанков как "грязных", откладывая её до конца очереди.
    /// </summary>
    [HarmonyPatch(typeof(ClientSystemRelight), "ProcessLightingTask")]
    public static class ProcessLightingTask_Prefix
    {
        static bool Prefix(ClientSystemRelight __instance, EntityPos playerPos, UpdateLightingTask task)
        {
            ProcessLightingTaskDeferred(__instance, playerPos, task);
            return false;
        }
    }

    /// <summary>
    /// Постфикс: выполняется после опустошения очереди задач освещения.
    /// 1. Сбрасывает полный пакет Lumos (блочный + солнечный свет).
    /// 2. Помечает все измененные чанки как требующие полного обновления.
    /// 3. Помечает соседние чанки как требующие только обновления границ (edge-only).
    /// </summary>
    [HarmonyPatch(typeof(ClientSystemRelight), nameof(ClientSystemRelight.ProcessLightingQueue))]
    public static class ProcessLightingQueue_Postfix
    {
        static void Postfix(ClientSystemRelight __instance)
        {
            ClientMain game = GetGame(__instance);
            LumosChunkIlluminator lumos = GetLumos(__instance);

            if (game == null || game.WorldMap == null || lumos == null)
                return;

            BatchState state = GetState(__instance);

            // Фактический расчет света происходит здесь, после сбора ВСЕХ задач из очереди.
            FastSetOfLongs touchedChunks = lumos.FlushPendingLightUpdates();

            // Любой чанк, чей массив Lighting изменился, получает полное обновление меша.
            AddReturnedChunks(state, touchedChunks, true);

            // Только теперь сообщаем клиентскому рендереру об изменении данных.
            foreach (var pair in state.RegularDirtyChunks)
            {
                game.WorldMap.SetChunkDirty(pair.Key, pair.Value, relight: false, edgeOnly: false);
            }

            foreach (var pair in state.EdgeOnlyDirtyChunks)
            {
                if (state.RegularDirtyChunks.ContainsKey(pair.Key)) continue;
                game.WorldMap.SetChunkDirty(pair.Key, pair.Value, relight: false, edgeOnly: true);
            }

            state.Clear();
        }
    }
}
