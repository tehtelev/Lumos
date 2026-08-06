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

namespace Lumos.Patches;

/// <summary>
/// Клиентская интеграция: заменяет ванильный ProcessLightingTask, откладывая
/// SetChunkDirty до пакетного флаша Lumos (устраняет гонку и мерцание меша).
/// </summary>
public static class ClientSystemRelightPatches
{
    private sealed class BatchState
    {
        public readonly Dictionary<long, bool> RegularDirtyChunks = new(128);
        public readonly Dictionary<long, bool> EdgeOnlyDirtyChunks = new(256);

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
            if (RegularDirtyChunks.ContainsKey(chunkIndex)) return;
            if (EdgeOnlyDirtyChunks.TryGetValue(chunkIndex, out bool existing))
            {
                EdgeOnlyDirtyChunks[chunkIndex] = existing || priority;
                return;
            }
            EdgeOnlyDirtyChunks.Add(chunkIndex, priority);
        }
    }

    private static readonly ConditionalWeakTable<ClientSystemRelight, BatchState> States = new();

    private static readonly FieldInfo ChunkIlluminatorField = FindField(typeof(ClientSystemRelight), "chunkIlluminator");
    private static readonly FieldInfo GameField = FindField(typeof(ClientSystemRelight), "game");

    private static FieldInfo FindField(Type type, string fieldName)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static BatchState GetState(ClientSystemRelight instance) => States.GetOrCreateValue(instance);

    private static ChunkIlluminator GetChunkIlluminator(ClientSystemRelight instance)
        => ChunkIlluminatorField?.GetValue(instance) as ChunkIlluminator;

    private static ClientMain GetGame(ClientSystemRelight instance)
        => GameField?.GetValue(instance) as ClientMain;

    private static LumosChunkIlluminator GetLumos(ClientSystemRelight instance)
    {
        ChunkIlluminator chunkIlluminator = GetChunkIlluminator(instance);
        return chunkIlluminator == null ? null : LumosStateHolder.GetOrCreate(chunkIlluminator);
    }

    private static void AddReturnedChunks(BatchState state, FastSetOfLongs chunks, bool priority)
    {
        if (chunks == null) return;
        foreach (long chunkIndex in chunks)
            state.AddRegular(chunkIndex, priority);
    }

    private static void AddVanillaNeighbourDirtyChunks(BatchState state, ClientMain game, int x, int y, int z, bool priority)
    {
        if (game?.WorldMap == null) return;

        long centerChunkIndex = game.WorldMap.ChunkIndex3D(new ChunkPos(x / 32, y / 32, z / 32));
        state.AddRegular(centerChunkIndex, priority);

        for (int i = -1; i < 2; i++)
            for (int j = -1; j < 2; j++)
                for (int k = -1; k < 2; k++)
                {
                    if (i == 0 && j == 0 && k == 0) continue;
                    long neighbourChunkIndex = game.WorldMap.ChunkIndex3D(
                        new ChunkPos((x + i) / 32, (y + j) / 32, (z + k) / 32));
                    if (neighbourChunkIndex == centerChunkIndex) continue;
                    state.AddEdgeOnly(neighbourChunkIndex, priority);
                }
    }

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

        bool priority = playerPos != null && playerPos.SquareDistanceTo(x, internalY, z) < 2304f;

        int oldAbsorb = 0;
        int newAbsorb = 0;
        bool blockLightChanged = false;

        if (task.absorbUpdate)
        {
            oldAbsorb = task.oldAbsorb;
            newAbsorb = task.newAbsorb;

            // Фейковое поглощение от двери/люка: клиент идёт этим путём, МИМО
            // UpdateSunLight_Prefix, поэтому обе очереди ставим здесь.
            if (oldAbsorb == DoorRelightPatches.FAKE_BLOCKER_ABSORPTION ||
                newAbsorb == DoorRelightPatches.FAKE_BLOCKER_ABSORPTION)
            {
                blockLightChanged = true;
                lumos.UpdateBlockLight(oldAbsorb, newAbsorb, x, internalY, z);
                lumos.UpdateSunLight(x, internalY, z, oldAbsorb, newAbsorb);
                oldAbsorb = 0;
                newAbsorb = 0;
            }
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

    [HarmonyPatch(typeof(ClientSystemRelight), "ProcessLightingTask")]
    public static class ProcessLightingTask_Prefix
    {
        static bool Prefix(ClientSystemRelight __instance, EntityPos playerPos, UpdateLightingTask task)
        {
            ProcessLightingTaskDeferred(__instance, playerPos, task);
            return false;
        }
    }

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

            FastSetOfLongs touchedChunks = lumos.FlushPendingLightUpdates();
            AddReturnedChunks(state, touchedChunks, true);

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