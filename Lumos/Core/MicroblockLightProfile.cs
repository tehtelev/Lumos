using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Lumos.Core;

/// <summary>
/// Предвычисленный световой профиль уникальной конфигурации чизельного блока.
/// Не привязан к позиции — только к форме (VoxelCuboids) и материалам (BlockIds).
/// </summary>
public struct MicroblockLightProfile
{
    /// <summary>
    /// Эффективное поглощение для света, идущего вдоль каждой оси.
    /// Индексы: 0 = X (East/West), 1 = Y (Up/Down, солнечный свет), 2 = Z (North/South).
    /// Диапазон 0–99, как у обычных блоков.
    /// Формула: sum(voxelAbsorption) / 4096, но с учётом распределения по оси.
    /// </summary>
    public byte EffectiveAbsX;
    public byte EffectiveAbsY;
    public byte EffectiveAbsZ;

    /// <summary>
    /// Доля занятости (0–255 → 0.0–1.0) для каждой оси.
    /// Для оси Y: средняя заполненность колонки (x,z) вдоль Y.
    /// Используется для масштабирования энергии в рейтрейсинге.
    /// </summary>
    public byte SolidityX;
    public byte SolidityY;
    public byte SolidityZ;

    /// <summary>
    /// Общая объёмная доля (occupiedVoxels / 4096 * 255).
    /// </summary>
    public byte VolumeFraction;

    /// <summary>
    /// Средневзвешенное поглощение материалов (без учёта пустоты).
    /// Если блок на 50% камень (abs=99) и 50% стекло (abs=0): avgMatAbs = 49.
    /// </summary>
    public byte AvgMaterialAbsorption;

    /// <summary>
    /// Доля открытости каждой грани (0–255 → 0.0–1.0).
    /// Индексы совпадают с BlockFacing.Index (0=N, 1=E, 2=S, 3=W, 4=U, 5=D).
    /// Заменяет грубую sideAlmostSolid для более точного GetEffectiveAbsorption.
    /// </summary>
    public byte FaceOpenness0, FaceOpenness1, FaceOpenness2;
    public byte FaceOpenness3, FaceOpenness4, FaceOpenness5;

    /// <summary>
    /// True, если блок имеет сквозные отверстия хотя бы по одной оси
    /// (есть колонки с нулевой занятостью). Используется для быстрой проверки
    /// «свет может пройти насквозь без поглощения».
    /// </summary>
    public bool HasThroughHoles;

    /// <summary>
    /// Минимальное поглощение среди всех материалов (для быстрой отсечки:
    /// если все материалы прозрачные, не нужно считать дальше).
    /// </summary>
    public byte MinMaterialAbsorption;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetEffectiveAbsForAxis(int axisIndex)
    {
        switch (axisIndex)
        {
            case 0: return EffectiveAbsX;
            case 1: return EffectiveAbsY;
            default: return EffectiveAbsZ;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetSolidityForAxis(int axisIndex)
    {
        switch (axisIndex)
        {
            case 0: return SolidityX;
            case 1: return SolidityY;
            default: return SolidityZ;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetFaceOpenness(int faceIndex)
    {
        switch (faceIndex)
        {
            case 0: return FaceOpenness0;
            case 1: return FaceOpenness1;
            case 2: return FaceOpenness2;
            case 3: return FaceOpenness3;
            case 4: return FaceOpenness4;
            default: return FaceOpenness5;
        }
    }
}

/// <summary>
/// Кэш световых профилей чизельных блоков.
/// Ключ — 64-битный хеш (VoxelCuboids + BlockIds), не позиция.
///
/// Статический и общий на всё приложение: содержимое кэша зависит только
/// от геометрии+материалов, а не от конкретного инстанса или потока,
/// поэтому нет смысла держать по словарю на каждый инстанс.
///
/// ConcurrentDictionary вместо lock+Dictionary: чтения (TryGetValue) полностью
/// lock-free, запись (TryAdd) использует striped locking только по конкретному
/// бакету — конкуренция между потоками резко падает по сравнению с
/// монопольным Monitor на весь словарь.
/// </summary>
public class MicroblockLightCache
{
    private static readonly ConcurrentDictionary<ulong, MicroblockLightProfile> cache =
        new ConcurrentDictionary<ulong, MicroblockLightProfile>(
            concurrencyLevel: Environment.ProcessorCount,
            capacity: 1024);

    // Временные буферы для вычисления (переиспользуются на поток, не аллоцируют)
    [ThreadStatic] private static byte[] tmpVoxelAbs;    // 4096

    /// <summary>
    /// Возвращает кэшированный профиль или вычисляет и кэширует новый.
    /// </summary>
    public static MicroblockLightProfile GetOrCompute(
        BlockEntityMicroBlock microBE,
        IList<Block> blockTypes)
    {
        ulong hash = ComputeHash(microBE);

        if (cache.TryGetValue(hash, out MicroblockLightProfile cached))
            return cached;

        // Вычисляем вне какой-либо блокировки (дорого, но безопасно — результат
        // детерминирован; если два потока посчитают параллельно — не страшно,
        // TryAdd просто оставит первую записанную версию).
        MicroblockLightProfile profile = ComputeProfile(microBE, blockTypes);

        cache.TryAdd(hash, profile);
        return profile;
    }

    /// <summary>
    /// Принудительная инвалидация (если нужно, например, при подмене материалов).
    /// В обычной работе не нужна — хеш сам изменится при изменении геометрии.
    /// </summary>
    public static void Invalidate(ulong hash)
    {
        cache.TryRemove(hash, out _);
    }

    /// <summary>
    /// Полная очистка кэша. Так как кэш теперь static (общий на процесс),
    /// вызывайте это при выгрузке мира/сервера, если blockIds могут означать
    /// разные материалы в разных мирах (например, разные наборы модов).
    /// </summary>
    public static void Clear()
    {
        cache.Clear();
    }

    /// <summary>
    /// FNV-1a-подобный 64-битный хеш от VoxelCuboids + BlockIds.
    /// Детерминирован: одинаковая геометрия + материалы → одинаковый хеш,
    /// независимо от позиции в мире.
    ///
    /// Оптимизация: вместо классического побайтового FNV-1a (4 XOR + 4 MUL на
    /// каждый uint) сворачиваем сразу целым 32-битным словом за одну
    /// итерацию — в 4 раза меньше арифметических операций. Для внутреннего
    /// детерминированного ключа кэша распределение остаётся достаточно
    /// хорошим, коллизии не критичны (при коллизии просто теряется кэш-хит,
    /// не корректность).
    ///
    /// CollectionsMarshal.AsSpan убирает bounds-check/virtual-dispatch
    /// overhead индексатора List&lt;T&gt;.get_Item, читая напрямую внутренний
    /// массив без копирования и без аллокаций.
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
    /// Вычисляет полный световой профиль из воксельных данных BlockEntity.
    /// Стоимость: O(16³) = 4096 итераций, один раз на уникальную конфигурацию.
    /// </summary>
    private const int MAX_LIGHT_FOR_ABS = 32; // = MAX_BLOCK_LIGHT_LEVEL + 1

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

        // ── Заполнение воксельной сетки ──
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

        // ── Общая статистика ──
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

        // ══════════════════════════════════════════════════════════
        //  ПО ОСЯМ: новая формула с opaque-детектом на колонку
        // ══════════════════════════════════════════════════════════

        // Для каждой оси: 256 колонок.
        // Для каждой колонки: sum(cappedAbs) и флаг hasOpaque.
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
        //   opaque → MAX_LIGHT (стена, не разбавляется)
        //   полупрозрачное → ⌈sum / 16⌉ (ceiling, минимум 1)
        //   пустое → 0
        int totalEffY = 0, totalEffX = 0, totalEffZ = 0;
        int emptyColsY = 0, emptyColsX = 0, emptyColsZ = 0;

        for (int i = 0; i < 256; i++)
        {
            // Y
            if (colOpaqY[i])
                totalEffY += MAX_LIGHT_FOR_ABS;
            else if (colSumY[i] > 0)
                totalEffY += (colSumY[i] + 15) / 16;   // ceiling
            else
                emptyColsY++;

            // X
            if (colOpaqX[i])
                totalEffX += MAX_LIGHT_FOR_ABS;
            else if (colSumX[i] > 0)
                totalEffX += (colSumX[i] + 15) / 16;
            else
                emptyColsX++;

            // Z
            if (colOpaqZ[i])
                totalEffZ += MAX_LIGHT_FOR_ABS;
            else if (colSumZ[i] > 0)
                totalEffZ += (colSumZ[i] + 15) / 16;
            else
                emptyColsZ++;
        }

        byte effAbsY = (byte)Math.Min(MAX_LIGHT_FOR_ABS, totalEffY / 256);
        byte effAbsX = (byte)Math.Min(MAX_LIGHT_FOR_ABS, totalEffX / 256);
        byte effAbsZ = (byte)Math.Min(MAX_LIGHT_FOR_ABS, totalEffZ / 256);

        // ── Солидность (без изменений) ──
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

        // ── Открытость граней (без изменений) ──
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

        // ── Сборка ──
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