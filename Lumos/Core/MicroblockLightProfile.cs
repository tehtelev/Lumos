using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Lumos.Core;

/// <summary>
/// Предвычисленный световой профиль конфигурации чизельного блока.
/// Зависит только от формы (VoxelCuboids) и материалов (BlockIds), не от позиции в мире.
/// </summary>
public struct MicroblockLightProfile
{
    /// <summary>Эффективное поглощение света вдоль осей X, Y (солнечный), Z. Диапазон 0–99.</summary>
    public byte EffectiveAbsX;
    public byte EffectiveAbsY;
    public byte EffectiveAbsZ;

    /// <summary>Доля занятости (0–255) для каждой оси. Используется для масштабирования энергии в рейтрейсинге.</summary>
    public byte SolidityX;
    public byte SolidityY;
    public byte SolidityZ;

    /// <summary>Общая объёмная доля занятых вокселей (0–255).</summary>
    public byte VolumeFraction;

    /// <summary>Средневзвешенное поглощение материалов (без учёта пустоты).</summary>
    public byte AvgMaterialAbsorption;

    /// <summary>Доля открытости граней (0–255). Индексы: 0=N, 1=E, 2=S, 3=W, 4=U, 5=D.</summary>
    public byte FaceOpenness0, FaceOpenness1, FaceOpenness2;
    public byte FaceOpenness3, FaceOpenness4, FaceOpenness5;

    /// <summary>Наличие сквозных отверстий хотя бы по одной оси (свет проходит без поглощения).</summary>
    public bool HasThroughHoles;

    /// <summary>Минимальное поглощение среди всех материалов блока.</summary>
    public byte MinMaterialAbsorption;

    /// <summary>Возвращает эффективное поглощение для указанной оси (0=X, 1=Y, 2=Z).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetEffectiveAbsForAxis(int axisIndex) => axisIndex switch
    {
        0 => EffectiveAbsX,
        1 => EffectiveAbsY,
        _ => EffectiveAbsZ
    };

    /// <summary>Возвращает долю занятости для указанной оси (0=X, 1=Y, 2=Z).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetSolidityForAxis(int axisIndex) => axisIndex switch
    {
        0 => SolidityX,
        1 => SolidityY,
        _ => SolidityZ
    };

    /// <summary>Возвращает открытость грани по её индексу (0..5).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetFaceOpenness(int faceIndex) => faceIndex switch
    {
        0 => FaceOpenness0,
        1 => FaceOpenness1,
        2 => FaceOpenness2,
        3 => FaceOpenness3,
        4 => FaceOpenness4,
        _ => FaceOpenness5
    };
}

/// <summary>
/// Потокобезопасный кэш световых профилей чизельных блоков.
/// Ключ — 64-битный хеш от геометрии и материалов.
/// Общий для всего процесса, так как профиль не зависит от позиции или инстанса.
/// </summary>
public static class MicroblockLightCache
{
    /// <summary>Кэш профилей. Ключ — хеш геометрии и материалов.</summary>
    private static readonly ConcurrentDictionary<ulong, MicroblockLightProfile> cache = new(
        concurrencyLevel: Environment.ProcessorCount,
        capacity: 1024);

    /// <summary>Переиспользуемый потоко-локальный буфер для воксельного поглощения (16x16x16).</summary>
    [ThreadStatic] private static byte[] tmpVoxelAbs;

    /// <summary>
    /// Возвращает кэшированный профиль или вычисляет и кэширует новый.
    /// Вычисление происходит вне блокировок: при гонке потоков просто сохранится первый результат.
    /// </summary>
    public static MicroblockLightProfile GetOrCompute(
        BlockEntityMicroBlock microBE,
        IList<Block> blockTypes)
    {
        ulong hash = ComputeHash(microBE);

        if (cache.TryGetValue(hash, out MicroblockLightProfile cached))
            return cached;

        MicroblockLightProfile profile = ComputeProfile(microBE, blockTypes);
        cache.TryAdd(hash, profile);
        return profile;
    }

    /// <summary>Принудительно удаляет профиль из кэша по хешу.</summary>
    public static void Invalidate(ulong hash)
    {
        cache.TryRemove(hash, out _);
    }

    /// <summary>Полностью очищает кэш (вызывать при выгрузке мира или смене модов).</summary>
    public static void Clear()
    {
        cache.Clear();
    }

    /// <summary>
    /// Вычисляет FNV-1a 64-битный хеш от воксельных кубоидов и ID материалов.
    /// Детерминирован: одинаковая геометрия и материалы дают одинаковый хеш.
    /// Использует CollectionsMarshal.AsSpan для прямого чтения внутреннего массива List без аллокаций.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ComputeHash(BlockEntityMicroBlock microBE)
    {
        const ulong FNV_OFFSET = 14695981039346656037UL;
        const ulong FNV_PRIME = 1099511628211UL;

        ulong hash = FNV_OFFSET;

        List<uint> cuboidsList = microBE.VoxelCuboids;
        if (cuboidsList != null)
        {
            Span<uint> cuboids = CollectionsMarshal.AsSpan(cuboidsList);
            foreach (var cub in cuboids)
            {
                hash ^= cub;
                hash *= FNV_PRIME;
            }
        }

        int[] blockIds = microBE.BlockIds;
        if (blockIds != null)
        {
            foreach (var id in blockIds)
            {
                hash ^= (uint)id;
                hash *= FNV_PRIME;
            }
        }

        return hash;
    }

    /// <summary>
    /// Вычисляет световой профиль из воксельных данных BlockEntity.
    /// Сложность: O(16³) = 4096 итераций. Выполняется один раз для уникальной конфигурации.
    /// </summary>
    private const int MAX_LIGHT_FOR_ABS = 32; // MAX_BLOCK_LIGHT_LEVEL + 1

    private static MicroblockLightProfile ComputeProfile(
        BlockEntityMicroBlock microBE,
        IList<Block> blockTypes)
    {
        byte[] voxelAbs = tmpVoxelAbs ?? (tmpVoxelAbs = new byte[4096]);
        Array.Clear(voxelAbs, 0, 4096);

        int[] blockIds = microBE.BlockIds;
        List<uint> cuboids = microBE.VoxelCuboids;

        if (blockIds == null || cuboids == null || cuboids.Capacity == 0)
            return new MicroblockLightProfile();

        // 1. Инициализация буфера и кэша поглощения материалов
        byte[] matAbsCapped = new byte[blockIds.Length];
        byte[] matAbsRaw = new byte[blockIds.Length];
        byte minMatAbs = 255;

        for (int m = 0; m < blockIds.Length; m++)
        {
            Block b = blockTypes[blockIds[m]];
            int rawAbs = b?.LightAbsorption ?? 99;
            matAbsRaw[m] = (byte)Math.Min(rawAbs, 255);
            matAbsCapped[m] = (byte)Math.Min(rawAbs, MAX_LIGHT_FOR_ABS);
            if (rawAbs < minMatAbs) minMatAbs = (byte)rawAbs;
        }

        // 2. Растеризация кубоидов в воксельную сетку 16³
        CuboidWithMaterial tmpCub = new CuboidWithMaterial();
        for (int ci = 0; ci < cuboids.Count; ci++)
        {
            BlockEntityMicroBlock.FromUint(cuboids[ci], tmpCub);
            byte cappedAbs = tmpCub.Material < matAbsCapped.Length
                ? matAbsCapped[tmpCub.Material]
                : (byte)MAX_LIGHT_FOR_ABS;

            for (int x = tmpCub.X1; x < tmpCub.X2; x++)
                for (int y = tmpCub.Y1; y < tmpCub.Y2; y++)
                    for (int z = tmpCub.Z1; z < tmpCub.Z2; z++)
                        voxelAbs[(y * 16 + z) * 16 + x] = cappedAbs;
        }

        // 3. Подсчет общей статистики
        int occupiedCount = 0;
        int totalRawSum = 0;

        for (int i = 0; i < 4096; i++)
            if (voxelAbs[i] > 0) occupiedCount++;

        for (int ci = 0; ci < cuboids.Count; ci++)
        {
            BlockEntityMicroBlock.FromUint(cuboids[ci], tmpCub);
            int vol = (tmpCub.X2 - tmpCub.X1) *
                      (tmpCub.Y2 - tmpCub.Y1) *
                      (tmpCub.Z2 - tmpCub.Z1);
            byte raw = tmpCub.Material < matAbsRaw.Length
                ? matAbsRaw[tmpCub.Material] : (byte)99;
            totalRawSum += vol * raw;
        }

        // 4. Расчет эффективного поглощения по осям (с учетом непрозрачных колонок)
        int[] colSumY = new int[256];  // колонки (x,z), суммируем по y
        int[] colSumX = new int[256];  // колонки (y,z), суммируем по x
        int[] colSumZ = new int[256];  // колонки (x,y), суммируем по z
        bool[] colOpaqY = new bool[256];
        bool[] colOpaqX = new bool[256];
        bool[] colOpaqZ = new bool[256];

        for (int x = 0; x < 16; x++)
            for (int y = 0; y < 16; y++)
                for (int z = 0; z < 16; z++)
                {
                    byte a = voxelAbs[(y * 16 + z) * 16 + x];
                    if (a == 0) continue;

                    bool opaque = a >= MAX_LIGHT_FOR_ABS;

                    int idxY = z * 16 + x;
                    colSumY[idxY] += a;
                    if (opaque) colOpaqY[idxY] = true;

                    int idxX = z * 16 + y;
                    colSumX[idxX] += a;
                    if (opaque) colOpaqX[idxX] = true;

                    int idxZ = y * 16 + x;
                    colSumZ[idxZ] += a;
                    if (opaque) colOpaqZ[idxZ] = true;
                }

        // Эффективное поглощение по колонке:
        //   непрозрачное → MAX_LIGHT (стена, не разбавляется)
        //   полупрозрачное → ⌈sum / 16⌉ (округление вверх, минимум 1)
        //   пустое → 0
        int totalEffY = 0, totalEffX = 0, totalEffZ = 0;
        int emptyColsY = 0, emptyColsX = 0, emptyColsZ = 0;

        for (int i = 0; i < 256; i++)
        {
            if (colOpaqY[i]) totalEffY += MAX_LIGHT_FOR_ABS;
            else if (colSumY[i] > 0) totalEffY += (colSumY[i] + 15) / 16;
            else emptyColsY++;

            if (colOpaqX[i]) totalEffX += MAX_LIGHT_FOR_ABS;
            else if (colSumX[i] > 0) totalEffX += (colSumX[i] + 15) / 16;
            else emptyColsX++;

            if (colOpaqZ[i]) totalEffZ += MAX_LIGHT_FOR_ABS;
            else if (colSumZ[i] > 0) totalEffZ += (colSumZ[i] + 15) / 16;
            else emptyColsZ++;
        }

        byte effAbsY = (byte)Math.Min(MAX_LIGHT_FOR_ABS, totalEffY / 256);
        byte effAbsX = (byte)Math.Min(MAX_LIGHT_FOR_ABS, totalEffX / 256);
        byte effAbsZ = (byte)Math.Min(MAX_LIGHT_FOR_ABS, totalEffZ / 256);

        // 5. Расчет доли занятости (solidity) по осям
        int[] colOccY = new int[256];
        int[] colOccX = new int[256];
        int[] colOccZ = new int[256];

        for (int x = 0; x < 16; x++)
            for (int y = 0; y < 16; y++)
                for (int z = 0; z < 16; z++)
                {
                    if (voxelAbs[(y * 16 + z) * 16 + x] > 0)
                    {
                        colOccY[z * 16 + x]++;
                        colOccX[z * 16 + y]++;
                        colOccZ[y * 16 + x]++;
                    }
                }

        int sumOccY = 0, sumOccX = 0, sumOccZ = 0;
        for (int i = 0; i < 256; i++)
        {
            sumOccY += colOccY[i];
            sumOccX += colOccX[i];
            sumOccZ += colOccZ[i];
        }

        byte solidityY = (byte)(sumOccY * 255 / 4096);
        byte solidityX = (byte)(sumOccX * 255 / 4096);
        byte solidityZ = (byte)(sumOccZ * 255 / 4096);

        // 6. Расчет открытости граней (доля пустых вокселей на поверхности)
        int openN = 0, openE = 0, openS = 0, openW = 0, openU = 0, openD = 0;
        for (int a = 0; a < 16; a++)
            for (int b = 0; b < 16; b++)
            {
                if (voxelAbs[(b * 16 + 0) * 16 + a] == 0) openN++;
                if (voxelAbs[(b * 16 + 15) * 16 + a] == 0) openS++;
                if (voxelAbs[(b * 16 + a) * 16 + 0] == 0) openW++;
                if (voxelAbs[(b * 16 + a) * 16 + 15] == 0) openE++;
                if (voxelAbs[(0 * 16 + a) * 16 + b] == 0) openD++;
                if (voxelAbs[(15 * 16 + a) * 16 + b] == 0) openU++;
            }

        // 7. Формирование итогового профиля
        MicroblockLightProfile profile;
        profile.EffectiveAbsX = effAbsX;
        profile.EffectiveAbsY = effAbsY;
        profile.EffectiveAbsZ = effAbsZ;
        profile.SolidityX = solidityX;
        profile.SolidityY = solidityY;
        profile.SolidityZ = solidityZ;
        profile.VolumeFraction = (byte)(occupiedCount * 255 / 4096);
        profile.AvgMaterialAbsorption = occupiedCount > 0
            ? (byte)Math.Min(99, totalRawSum / occupiedCount)
            : (byte)0;
        profile.MinMaterialAbsorption = minMatAbs == 255 ? (byte)0 : minMatAbs;
        profile.FaceOpenness0 = (byte)(openN * 255 / 256);
        profile.FaceOpenness1 = (byte)(openE * 255 / 256);
        profile.FaceOpenness2 = (byte)(openS * 255 / 256);
        profile.FaceOpenness3 = (byte)(openW * 255 / 256);
        profile.FaceOpenness4 = (byte)(openU * 255 / 256);
        profile.FaceOpenness5 = (byte)(openD * 255 / 256);
        profile.HasThroughHoles = emptyColsY > 0 || emptyColsX > 0 || emptyColsZ > 0;

        return profile;
    }
}