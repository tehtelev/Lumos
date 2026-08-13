using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;


namespace Lumos.Core;

/// <summary>
/// Трассируемый (ray-traced) осветитель блочного и солнечного света.
///
/// Улучшения по сравнению с ванильным ChunkIlluminator:
/// - Трассировка лучей: прямой свет с однократным отражением
///   (50% обычные блоки, 12% стекло, 18% жидкости).
/// - Направленное поглощение: проверки маски твердости граней корректно обрабатывают
///   плиты, ступени и объемные прозрачные блоки.
/// - Обнуление солнечного света: UpdateSunLight корректно гасит "застрявший"
///   солнечный свет в замкнутых пещерах и комнатах.
/// - Учет микроблоков: блоки долота (BlockMicroBlock) разрешаются
///   через BlockEntityMicroBlock вместо бессмысленных статических
///   LightAbsorption / SideSolid (см. GetBlockAndAbsorption / GetSolidMask).
/// - Буферизация солнечного света: предотвращает мерцание мобов и меша при пересчете
///   за счет работы во временном массиве (staging) с последующим атомарным коммитом.
/// </summary>
public class LumosChunkIlluminator
{
    // ─── Константы ───────────────────────────────────────────────────────

    /// <summary>Максимальный уровень блочного света (включительно).</summary>
    private const int MAX_BLOCK_LIGHT_LEVEL = 31;

    /// <summary>Дополнительный отступ (в блоках), добавляемый к радиусу "грязной" сферы.</summary>
    private const int DIRTY_RADIUS_PADDING = 1;

    // ─── Параметры мира и измерения ──────────────────────────────────────

    /// <summary>Базовый уровень солнечного света для текущего измерения.</summary>
    private ushort defaultSunLight;

    /// <summary>Размеры мира в блоках по каждой оси (включая смещение измерения).</summary>
    private int mapsizex, mapsizey, mapsizez;

    /// <summary>Текущее измерение мира, используемое при упаковке позиций.</summary>
    private int currentDim = 0;

    // ─── Геометрия чанков ────────────────────────────────────────────────

    /// <summary>Список всех блоков сервера, используемый для разрешения по ID.</summary>
    private IList<Block> blockTypes;

    /// <summary>Размер чанка в блоках (сторона куба).</summary>
    private int chunkSize;

    /// <summary>Логарифм размера чанка по основанию 2, используется для побитовых операций.</summary>
    private int chunkSizeLog2;

    /// <summary>Маска (chunkSize - 1) для получения локальной координаты внутри чанка.</summary>
    private int chunkSizeMask;

    /// <summary>Множители шага (stride) для плоского индексирования чанка (X=1, Z=chunkSize, Y=chunkSize²).</summary>
    private int XPlus = 1;
    private int YPlus;
    private int ZPlus;

    /// <summary>Провайдер чанков для загрузки и доступа к данным мира.</summary>
    internal IChunkProvider chunkProvider;

    /// <summary>Базовый аксессуар для чтения блоков (используется в трассировке).</summary>
    private IBlockAccessor readBlockAccess;

    // ─── Переиспользуемые временные позиции (избегают аллокации BlockPos) ─

    private BlockPos tmpDiPos = new(0);
    private BlockPos tmpPos = new(0);
    private BlockPos tmpPos2 = new(0);
    private BlockPos tmpPosDimensionAware = new(0);

    // ─── Батчинг грязных регионов ────────────────────────────────────────

    /// <summary>
    /// Сферическая "грязная" область, добавляемая в очередь при установке/удалении/обновлении света.
    /// Один вызов FlushPendingBlockLightUpdates() пересчитывает весь накопленный пакет.
    /// </summary>
    private struct DirtyLightSphere
    {
        public int X;
        public int Y;
        public int Z;
        public int Radius;

        public DirtyLightSphere(int x, int y, int z, int radius)
        {
            X = x;
            Y = y;
            Z = z;
            Radius = radius;
        }
    }

    /// <summary>Очередь "грязных" сфер, сгруппированных по упакованной позиции источника света.</summary>
    private readonly Dictionary<long, DirtyLightSphere> pendingDirtySpheres = new(256);

    /// <summary>Переиспользуемый буфер: снимок pendingDirtySpheres для одного сброса (flush).</summary>
    private readonly List<DirtyLightSphere> dirtySphereBuffer = new(256);

    /// <summary>Точное объединение всех "грязных" сфер (упакованные ключи ячеек).</summary>
    private readonly HashSet<long> dirtyLightCells = new();

    /// <summary>Bounding box текущего "грязного" региона (в мировых координатах). Используется как дешёвый reject-фильтр в ApplyLightToBlock и ProcessRay, чтобы не трогать словарь visitedNodes для лучей, улетевших за пределы региона, который всё равно закоммитится.</summary>
    private int dirtyMinX, dirtyMaxX, dirtyMinY, dirtyMaxY, dirtyMinZ, dirtyMaxZ;

    /// <summary>Карта дедупликации: упакованная позиция → индекс в nearbyLightSourcesArray.</summary>
    private readonly Dictionary<long, int> nearbySourceIndexByPosition = new(512);

    /// <summary>Защита от рекурсивного входа для FlushPendingBlockLightUpdates.</summary>
    private bool isFlushingBlockLight;

    // ─── Кэши свойств блоков ─────────────────────────────────────────────

    /// <summary>
    /// Флаг для каждого BlockId: "является ли это микроблоком долота?".
    /// Позволяет использовать быстрый поиск в массиве вместо виртуальной проверки `is`.
    /// </summary>
    private static bool[] isMicroblockCache;

    /// <summary>
    /// Флаг для каждого BlockId: "является ли это прокси-мультиблоком?".
    /// Позволяет быстро отсекать мультиблоки без проверки `is`.
    /// </summary>
    private static bool[] isMultiblockCache;

    /// <summary>
    /// Флаг для каждого BlockId: "сам блок является дверью/люком?"
    /// (по типу или поведению). Используется для быстрого отказа
    /// в IsDoorBlock и позволяет избежать GetBlockEntity на воздухе/камне.
    /// </summary>
    private static bool[] isDoorCache;

    // ─── Хелперы для микроблоков ─────────────────────────────────────────

    /// <summary>
    /// Строит упакованную битовую маску твердости граней (биты 0..5 = BlockFacing.Index).
    /// Для микроблоков читает предварительно вычисленный MicroblockLightProfile.
    /// Для обычных блоков использует статический Block.SideSolid.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetSolidMask(Block block, BlockEntityMicroBlock microBE, BlockPos pos)
    {
        if (microBE is not null)
        {
            var profile = MicroblockLightCache.GetOrCompute(microBE, blockTypes);
            int mask = 0;
            if (profile.FaceOpenness0 < 64) mask |= 1;
            if (profile.FaceOpenness1 < 64) mask |= 2;
            if (profile.FaceOpenness2 < 64) mask |= 4;
            if (profile.FaceOpenness3 < 64) mask |= 8;
            if (profile.FaceOpenness4 < 64) mask |= 16;
            if (profile.FaceOpenness5 < 64) mask |= 32;
            return mask;
        }

        // Двери/люки обрабатываются отдельно через IsDoorBlock + CollisionBoxes,
        // поэтому здесь используется только статическая маска SideSolid.

        int m = 0;
        if (block.SideSolid[0]) m |= 1;
        if (block.SideSolid[1]) m |= 2;
        if (block.SideSolid[2]) m |= 4;
        if (block.SideSolid[3]) m |= 8;
        if (block.SideSolid[4]) m |= 16;
        if (block.SideSolid[5]) m |= 32;
        return m;
    }

    // ─── Стейджинг солнечного света (предотвращает мерцание рендера) ─────

    /// <summary>Временные массивы для пересчета солнечного света по ключу чанка.</summary>
    private readonly Dictionary<long, byte[]> currentSunStaging = new(64);

    /// <summary>Пул переиспользуемых массивов для Zero-GC.</summary>
    private readonly Stack<byte[]> sunStagingPool = new(256);

    /// <summary>Текущее смещение измерения (dim * 1024) для батчинга солнечных обновлений.</summary>
    private int currentDimOffset;

    /// <summary>Читает солнечный свет из стейджинг-буфера (если он активен) или напрямую из чанка.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetSun(int cx, int cyWithDim, int cz, IWorldChunk chunk, int idx)
    {
        long key = chunkProvider.ChunkIndex3D(cx, cyWithDim, cz);
        if (currentSunStaging.TryGetValue(key, out var staging))
            return staging[idx];
        return chunk.Lighting.GetSunlight(idx);
    }

    /// <summary>Пишет солнечный свет в стейджинг-буфер (если он активен) или напрямую в чанк.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetSun(int cx, int cyWithDim, int cz, IWorldChunk chunk, int idx, int val)
    {
        long key = chunkProvider.ChunkIndex3D(cx, cyWithDim, cz);
        if (currentSunStaging.TryGetValue(key, out var staging))
            staging[idx] = (byte)val;
        else
            chunk.Lighting.SetSunlight(idx, val);
    }

    /// <summary>Арендует массив из пула или создает новый, если пул пуст.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] RentStagingArray()
    {
        if (sunStagingPool.Count > 0) return sunStagingPool.Pop();
        return new byte[chunkSize * chunkSize * chunkSize];
    }

    /// <summary>
    /// Разрешает эффективный блок, его базовое поглощение и опциональный
    /// BlockEntityMicroBlock для заданного индекса внутри чанка.
    /// Обрабатывает твердый слой, оверлей жидкости и микроблоки долота.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GetBlockAndAbsorption(
        IWorldChunk chunk,
        int index3d,
        BlockPos pos,
        out Block block,
        out int baseAbsorption,
        out BlockEntityMicroBlock microBE)
    {
        int solidId = chunk.Data.GetBlockId(index3d, 0);
        int fluidId = chunk.Data.GetBlockId(index3d, 1);

        microBE = null;

        // ШАГ 1: Определяем блок (приоритет у твердого слоя, fallback на жидкость)
        if (solidId >= blockTypes.Count || fluidId >= blockTypes.Count)
        {
            block = null;
        }
        else
        {
            block = blockTypes[solidId != 0 ? solidId : fluidId];
        }

        // ШАГ 2: Считаем поглощение
        if (isMicroblockCache[solidId])
        {
            microBE = chunk.GetLocalBlockEntityAtBlockPos(pos) as BlockEntityMicroBlock;

            if (microBE is not null)
            {
                MicroblockLightProfile profile =
                    MicroblockLightCache.GetOrCompute(microBE, blockTypes);

                baseAbsorption = (profile.EffectiveAbsX +
                                  profile.EffectiveAbsY +
                                  profile.EffectiveAbsZ) / 3;

                if (profile.MinMaterialAbsorption == 0 && profile.VolumeFraction < 128)
                    baseAbsorption = Math.Min(baseAbsorption, profile.AvgMaterialAbsorption);
            }
            else
            {
                // Микроблок без BlockEntity — используем статическое поглощение
                baseAbsorption = chunk.GetLightAbsorptionAt(index3d, pos, blockTypes);
            }
        }
        else
        {
            // Для обычных блоков (включая крыши!) запрашиваем актуальное поглощение.
            if (block is not null && readBlockAccess is not null && pos is not null)
            {
                baseAbsorption = block.GetLightAbsorption(readBlockAccess, pos);
            }
            else
            {
                // Fallback: статическое значение из данных чанка
                baseAbsorption = chunk.GetLightAbsorptionAt(index3d, pos, blockTypes);
            }
        }

        // Проверка fluidId != solidId предотвращает подмену динамического поглощения 
        // (например, 0 для крыши) на статическое значение того же самого блока, 
        // если они совпадают в данных чанка.
        if (fluidId != 0 && fluidId != solidId)
        {
            int fluidAbs = blockTypes[fluidId].LightAbsorption;
            if (fluidAbs > baseAbsorption)
                baseAbsorption = fluidAbs;
        }
    }

    // ─── Стейджинг света на основе словаря 

    #region Light staging

    /// <summary>
    /// Стейджинг накопленных вкладов источников света для одной позиции.
    /// Динамический список — нет жесткого лимита на количество перекрывающихся источников.
    /// </summary>
    public class LightSourcesAtBlock
    {
        public int[] srcIds;
        public byte[] levels;
        public int count;

        public LightSourcesAtBlock() { srcIds = new int[4]; levels = new byte[4]; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset() => count = 0;

        /// <summary>Развернутая версия для минимизации оверхеда при типичных малых количествах источников.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddOrUpdate(int srcId, byte level)
        {
            if (count == 0)
            {
                srcIds[0] = srcId; levels[0] = level; count = 1;
                return;
            }
            if (srcIds[0] == srcId) { if (level > levels[0]) levels[0] = level; return; }

            if (count == 1)
            {
                if (srcIds[1] == srcId) { if (level > levels[1]) levels[1] = level; return; }
                srcIds[1] = srcId; levels[1] = level; count = 2;
                return;
            }
            if (count == 2)
            {
                if (srcIds[2] == srcId) { if (level > levels[2]) levels[2] = level; return; }
                srcIds[2] = srcId; levels[2] = level; count = 3;
                return;
            }

            // Fallback для >= 3 источников (редкий случай в типичных сценах)
            for (int i = 0; i < count; i++)
            {
                if (srcIds[i] == srcId) { if (level > levels[i]) levels[i] = level; return; }
            }

            if (count >= srcIds.Length)
            {
                int newLen = srcIds.Length * 2;
                Array.Resize(ref srcIds, newLen);
                Array.Resize(ref levels, newLen);
            }

            srcIds[count] = srcId; levels[count] = level; count++;
        }
    }

    /// <summary>Стейджинг-словарь: упакованная мировая позиция → накопленные источники света (блочный свет).</summary>
    private Dictionary<long, LightSourcesAtBlock> visitedNodes = new(4096);

    /// <summary>Пул переиспользуемых экземпляров LightSourcesAtBlock.</summary>
    private Stack<LightSourcesAtBlock> lsabPool = new(256);

    /// <summary>
    /// Упаковывает три знаковые 21-битные координаты в один long.
    /// Диапазон по каждой оси: [-1 048 576, +1 048 575].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackPos(int x, int y, int z, int dim = 0)
    {
        return ((long)(x & 0x1FFFFF)) |
               ((long)(y & 0x1FFFFF) << 21) |
               ((long)(z & 0x1FFFFF) << 42) |
               ((long)(dim & 0xFF) << 63);
    }

    /// <summary>Обратная операция для PackPos с восстановлением знака.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UnpackPos(long key, out int x, out int y, out int z, out int dim)
    {
        x = (int)(key & 0x1FFFFF);
        y = (int)((key >> 21) & 0x1FFFFF);
        z = (int)((key >> 42) & 0x1FFFFF);
        dim = (int)((key >> 63) & 0xFF);

        if ((x & 0x100000) != 0) x |= unchecked((int)0xFFE00000);
        if ((y & 0x100000) != 0) y |= unchecked((int)0xFFE00000);
        if ((z & 0x100000) != 0) z |= unchecked((int)0xFFE00000);
    }

    /// <summary>Возвращает существующую запись или создает новую из пула.</summary>
    private LightSourcesAtBlock GetOrCreateLsab(long key)
    {
        if (visitedNodes.TryGetValue(key, out var lsab))
            return lsab;

        lsab = lsabPool.Count > 0 ? lsabPool.Pop() : new LightSourcesAtBlock();
        lsab.Reset();
        visitedNodes[key] = lsab;
        return lsab;
    }

    /// <summary>
    /// Закэшированный результат GetBlockAndAbsorption и GetRayCollisionBoxes для одной позиции.
    /// Живет только в пределах одного вызова TraceNearbyBlockLights.
    /// </summary>
    private struct BlockLookupCacheEntry
    {
        public Block Block;
        public int BaseAbsorption;
        public BlockEntityMicroBlock MicroBE;
        public Cuboidf[] CollisionBoxes;
        public bool HasCollisionBoxes;
    }

    /// <summary>
    /// Кэш "позиция → (блок, поглощение, microBE)" на время одного FlushPendingBlockLightUpdates.
    /// Устраняет повторные проходы по ChunkDataLayer (палитра + rwlock) для вокселей,
    /// через которые проходят десятки/сотни лучей одного и того же источника
    /// (особенно вблизи источника, где лучи ещё не разошлись).
    /// Заполняется лениво в GetBlockAndAbsorptionCached, чистится в TraceNearbyBlockLights.
    /// </summary>
    private readonly Dictionary<long, BlockLookupCacheEntry> blockLookupCache = new(8192);

    /// <summary>
    /// Кэш распакованных чанков, заполняемый в LoadSourcesIntersectingDirtySpheres
    /// и используемый ProcessRay. Устраняет повторяющиеся Dictionary lookups и lock-конкуренцию.
    /// </summary>
    private readonly Dictionary<long, IWorldChunk> chunkCache = new(4096);

    /// <summary>
    /// Кэширующая обёртка над GetBlockAndAbsorption. Безопасна в пределах одного прохода
    /// трассировки блочного света, так как живой chunk.Data не модифицируется во время
    /// трассировки (см. комментарий в FlushPendingBlockLightUpdates) — а значит результат
    /// для конкретной позиции не может измениться до следующего Clear().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GetBlockAndAbsorptionCached(
        IWorldChunk chunk,
        int index3d,
        BlockPos pos,
        out Block block,
        out int baseAbsorption,
        out BlockEntityMicroBlock microBE,
        out Cuboidf[] collisionBoxes,
        out bool hasCollisionBoxes)
    {
        long key = PackPos(pos.X, pos.Y, pos.Z, currentDim);

        if (blockLookupCache.TryGetValue(key, out BlockLookupCacheEntry cached))
        {
            block = cached.Block;
            baseAbsorption = cached.BaseAbsorption;
            microBE = cached.MicroBE;
            collisionBoxes = cached.CollisionBoxes;
            hasCollisionBoxes = cached.HasCollisionBoxes;
            return;
        }

        // Заполняем кэш из основного метода и дополнительно получаем коллайды для лучей
        GetBlockAndAbsorption(chunk, index3d, pos, out block, out baseAbsorption, out microBE);

        Cuboidf[] boxes = null;
        bool hasBoxes = false;

        // Для микроблоков коллизии не используются в трассировке, для воздуха их нет
        if (microBE is null && block.Id != 0)
        {
            boxes = GetRayCollisionBoxes(block, pos);
            hasBoxes = boxes is not null && boxes.Length > 0;
        }

        blockLookupCache[key] = new BlockLookupCacheEntry
        {
            Block = block,
            BaseAbsorption = baseAbsorption,
            MicroBE = microBE,
            CollisionBoxes = boxes,
            HasCollisionBoxes = hasBoxes
        };

        collisionBoxes = boxes;
        hasCollisionBoxes = hasBoxes;
    }


    /// <summary>Возвращает все записи стейджинга в пул и очищает словарь.</summary>
    private void RecycleVisitedNodes()
    {
        foreach (var kvp in visitedNodes)
        {
            kvp.Value.Reset();
            if (lsabPool.Count < 512)
                lsabPool.Push(kvp.Value);
        }
        visitedNodes.Clear();
    }

    #endregion

    // ─── Ближайшие источники света (массивы структур, Zero-GC) ───────────

    #region Nearby sources

    /// <summary>Позиция ближайшего источника света в массивах трассировки.</summary>
    private struct NearbyLightSourceStruct
    {
        public int posX, posY, posZ;
    }

    private NearbyLightSourceStruct[] nearbyLightSourcesArray;
    private byte[] nearbyH;
    private byte[] nearbyS;
    private byte[] nearbyB;
    private int nearbyCount;

    #endregion

    // ─── Легковесная структура позиции (для BFS) ─────────────────────────

    /// <summary>Легковесная структура позиции (для BFS).</summary>
    private struct FastBlockPos
    {
        public int X, Y, Z, Dim;
        public FastBlockPos(int x, int y, int z, int dim)
        {
            X = x; Y = y; Z = z; Dim = dim;
        }
    }

    // ─── Система трассировки лучей ───────────────────────────────────────

    #region Ray Tracing

    /// <summary>Активный луч трассировки в кольцевом буфере.</summary>
    private struct LightRay
    {
        public float OriginX, OriginY, OriginZ;
        public float DirX, DirY, DirZ;
        public float Energy;
        public byte H, S, B;
        public int BounceCount;
        public int SourceId;
    }

    /// <summary>Кольцевой буфер фиксированного размера для активных лучей.</summary>
    private LightRay[] rayPool;
    private int rayPoolHead;
    private int rayPoolTail;
    private int activeRayCount;

    private const int MAX_RAY_POOL_SIZE = 200000;
    private const int REFLECTION_RAYS_COUNT = 128;

    /// <summary>Желаемый зазор (в блоках) между соседними лучами на поверхности сферы.</summary>
    private const float TARGET_GAP = 1.3f;
    /// <summary>Нижняя граница, чтобы тусклые источники не были полностью "дырявыми".</summary>
    private const int MIN_RAYS = 512;
    /// <summary>Верхняя граница для предотвращения чрезмерной нагрузки.</summary>
    private const int MAX_RAYS = 40000;
    /// <summary>Шаг квантования радиуса для кэша сфер.</summary>
    private const int RADIUS_BUCKET_STEP = 2;

    /// <summary>
    /// Потокобезопасный кэш: квантованный радиус → (массив направлений, фактическое количество точек).
    /// Общий для всех экземпляров осветителя.
    /// </summary>
    private static ConcurrentDictionary<int, (float[][] dirs, int count)> sphereCache = new();

    /// <summary>Выделяет кольцевой буфер лучей и предварительно прогревает кэш сфер.</summary>
    private void InitRayTracing()
    {
        rayPool = new LightRay[MAX_RAY_POOL_SIZE];
        rayPoolHead = 0;
        rayPoolTail = 0;
        activeRayCount = 0;

        // Предварительный прогрев кэша для типичных значений яркости
        for (int r = 7; r <= 23; r++)
            GetOrBuildSphereForRadius(r);
    }

    /// <summary>N = 24·R² / gap², ограничено диапазоном [MIN_RAYS, MAX_RAYS].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalcRayCountForRadius(int radius)
    {
        if (radius <= 0) return MIN_RAYS;
        float n = 24f * radius * radius / (TARGET_GAP * TARGET_GAP);
        int result = (int)Math.Ceiling(n);
        return Math.Clamp(result, MIN_RAYS, MAX_RAYS);
    }

    /// <summary>Квантует радиус вверх до ближайшей границы бакета.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketRadius(int radius)
    {
        return ((radius + RADIUS_BUCKET_STEP - 1) / RADIUS_BUCKET_STEP) * RADIUS_BUCKET_STEP;
    }

    /// <summary>Возвращает кэшированные направления сферы Фибоначчи или строит их при первом использовании.</summary>
    private static (float[][] dirs, int count) GetOrBuildSphereForRadius(int radius)
    {
        int bucket = BucketRadius(radius);
        if (sphereCache.TryGetValue(bucket, out var cached))
            return cached;

        int rayCount = CalcRayCountForRadius(bucket);
        float[][] dirs = GenerateFibonacciSphere(rayCount, out int actualCount);
        var entry = (dirs, actualCount);
        sphereCache[bucket] = entry;
        return entry;
    }

    /// <summary>Генерирует равномерно распределенные единичные векторы через спираль Фибоначчи.</summary>
    private static float[][] GenerateFibonacciSphere(int count, out int actualCount)
    {
        var points = new float[count][];
        float goldenAngle = (float)(Math.PI * (3.0 - Math.Sqrt(5.0)));
        for (int i = 0; i < count; i++)
        {
            float y = 1.0f - 2.0f * (i + 0.5f) / count;
            float r = (float)Math.Sqrt(Math.Max(0f, 1.0f - y * y));
            float theta = goldenAngle * i;
            points[i] = new float[]
            {
                r * (float)Math.Cos(theta),
                y,
                r * (float)Math.Sin(theta)
            };
        }
        actualCount = count;
        return points;
    }

    /// <summary>Сбрасывает кольцевой буфер для трассировки нового источника.</summary>
    private void ResetRayPool()
    {
        rayPoolHead = 0;
        rayPoolTail = 0;
        activeRayCount = 0;
    }

    /// <summary>Добавляет луч в кольцевой буфер (молча отбрасывает, если буфер полон).</summary>
    private void SpawnRay(float ox, float oy, float oz, float dx, float dy, float dz,
        float energy, byte h, byte s, byte b, int bounce, int sourceId)
    {
        if (activeRayCount >= MAX_RAY_POOL_SIZE) return;

        rayPool[rayPoolHead] = new LightRay
        {
            OriginX = ox,
            OriginY = oy,
            OriginZ = oz,
            DirX = dx,
            DirY = dy,
            DirZ = dz,
            Energy = energy,
            H = h,
            S = s,
            B = b,
            BounceCount = bounce,
            SourceId = sourceId
        };

        rayPoolHead = (rayPoolHead + 1) % MAX_RAY_POOL_SIZE;
        activeRayCount++;
    }

    /// <summary>Извлекает старейший луч из кольцевого буфера.</summary>
    private LightRay DequeueRay()
    {
        var ray = rayPool[rayPoolTail];
        rayPoolTail = (rayPoolTail + 1) % MAX_RAY_POOL_SIZE;
        activeRayCount--;
        return ray;
    }

    // ─── Предвычисленные таблицы отражений ───────────────────────────────

    /// <summary>
    /// cos(θᵢ) / sin(θᵢ) по золотому углу — не зависят от фактического количества лучей.
    /// Убирает Math.Cos/Sin из горячего пути SpawnReflectionRays.
    /// </summary>
    private static readonly float[] reflCosTheta = new float[REFLECTION_RAYS_COUNT];
    private static readonly float[] reflSinTheta = new float[REFLECTION_RAYS_COUNT];

    /// <summary>Минимальное количество лучей отражения для слабых источников.</summary>
    private const int MIN_REFLECTION_RAYS = 16;

    /// <summary>Статический конструктор: заполняет таблицы углов отражения один раз.</summary>
    static LumosChunkIlluminator()
    {
        float goldenAngle = (float)(Math.PI * (3.0 - Math.Sqrt(5.0)));
        for (int i = 0; i < REFLECTION_RAYS_COUNT; i++)
        {
            float theta = i * goldenAngle;
            reflCosTheta[i] = (float)Math.Cos(theta);
            reflSinTheta[i] = (float)Math.Sin(theta);
        }
    }

    /// <summary>
    /// Адаптивное количество лучей отражения: слабые отражения получают меньше лучей
    /// (экономит плотность сэмплинга), сильные сохраняют плотную полусферу.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalcReflectionRayCount(float reflectedEnergy)
    {
        float t = reflectedEnergy / MAX_BLOCK_LIGHT_LEVEL;
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;

        return MIN_REFLECTION_RAYS +
            (int)((REFLECTION_RAYS_COUNT - MIN_REFLECTION_RAYS) * t);
    }

    /// <summary>
    /// Создает полусферу лучей диффузного отражения вокруг нормали попадания.
    /// Использует закон косинусов Ламберта для взвешивания энергии.
    /// </summary>
    private void SpawnReflectionRays(float x, float y, float z,
        float normalX, float normalY, float normalZ,
        float energy, byte h, byte s, byte b, int sourceId)
    {
        // Строим ортонормированный базис (касательная, бинормаль, нормаль)
        float tx, ty, tz;
        if (Math.Abs(normalY) < 0.99f)
        {
            tx = normalZ; ty = 0; tz = -normalX;
        }
        else
        {
            tx = 0; ty = normalZ; tz = -normalY;
        }

        float tLen = (float)Math.Sqrt(tx * tx + ty * ty + tz * tz);
        tx /= tLen; ty /= tLen; tz /= tLen;

        float bx = normalY * tz - normalZ * ty;
        float by = normalZ * tx - normalX * tz;
        float bz = normalX * ty - normalY * tx;

        int rayCount = CalcReflectionRayCount(energy);

        for (int i = 0; i < rayCount; i++)
        {
            // cosAngle/sinAngle зависят от N — одно деление + один Sqrt, без тригонометрии
            float cosAngle = 1.0f - ((i + 0.5f) / rayCount);
            float sinAngle = (float)Math.Sqrt(Math.Max(0f, 1.0f - cosAngle * cosAngle));

            float cosTheta = reflCosTheta[i];
            float sinTheta = reflSinTheta[i];

            float localX = sinAngle * cosTheta;
            float localY = cosAngle;
            float localZ = sinAngle * sinTheta;

            float worldDirX = tx * localX + normalX * localY + bx * localZ;
            float worldDirY = ty * localX + normalY * localY + by * localZ;
            float worldDirZ = tz * localX + normalZ * localY + bz * localZ;

            float dirLen = (float)Math.Sqrt(
                worldDirX * worldDirX + worldDirY * worldDirY + worldDirZ * worldDirZ);
            if (dirLen > 0)
            {
                worldDirX /= dirLen;
                worldDirY /= dirLen;
                worldDirZ /= dirLen;
            }

            float weightedEnergy = energy * cosAngle; // Закон Ламберта

            if (weightedEnergy > 0.01f)
            {
                SpawnRay(x, y, z, worldDirX, worldDirY, worldDirZ,
                    weightedEnergy, h, s, b, 1, sourceId);
            }
        }
    }

    /// <summary>
    /// Возвращает процент отражательной способности для типа блока.
    /// Стекло: 12%, жидкости: 18%, все остальное: 50% (диффузное).
    /// </summary>
    private static int GetReflectivity(Block block)
    {
        if (block is null)
            return 50; // treat as opaque default

        // Защита от null Code.Path
        string path = block.Code?.Path?.ToString() ?? "";


        if (path.Contains("glass"))
            return 12;

        if (block.IsLiquid())
            return 18;

        return 50;
    }




    /// <summary>
    /// Возвращает CollisionBoxes блока в мировых координатах пересчёта не требующих runtime-проверок вида.
    /// Корректно резолвит мультиблоки — верхние/боковые части дверей и т.п.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Cuboidf[] GetRayCollisionBoxes(Block block, BlockPos pos)
    {
        if (block is BlockMultiblock mb)
        {
            BlockPos mainPos = pos.AddCopy(mb.OffsetInv);
            Block mainBlock = readBlockAccess.GetBlock(mainPos);
            if (mainBlock is null) return null;

            Cuboidf[] boxes = null;
            if (mainBlock.BlockBehaviors is not null)
            {
                foreach (BlockBehavior bh in mainBlock.BlockBehaviors)
                {
                    if (bh is IMultiBlockColSelBoxes mbc)
                    {
                        boxes = mbc.MBGetCollisionBoxes(readBlockAccess, pos, mb.OffsetInv);
                        break;
                    }
                }
            }
            if (boxes is null)
                boxes = mainBlock.GetCollisionBoxes(readBlockAccess, mainPos);

            return boxes;
        }

        return block.GetCollisionBoxes(readBlockAccess, pos);
    }

    /// <summary>
    /// Проверяет, пересекает ли отрезок [start, end] хотя бы один из уже полученных
    /// CollisionBox блока. Устойчив к лучам, параллельным осям координат.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SegmentIntersectsBoxes(
        Cuboidf[] boxes, BlockPos pos,
        float startX, float startY, float startZ,
        float endX, float endY, float endZ)
    {
        if (boxes is null || boxes.Length == 0) return false;

        float dx = endX - startX;
        float dy = endY - startY;
        float dz = endZ - startZ;

        int bx = pos.X;
        int by = pos.Y;
        int bz = pos.Z;

        const float EPS = 1e-4f;

        for (int i = 0; i < boxes.Length; i++)
        {
            Cuboidf box = boxes[i];
            float minX = bx + box.X1, maxX = bx + box.X2;
            float minY = by + box.Y1, maxY = by + box.Y2;
            float minZ = bz + box.Z1, maxZ = bz + box.Z2;

            float tMin = 0f, tMax = 1f;
            bool hit = true;

            if (Math.Abs(dx) < 1e-8f)
            {
                if (startX < minX - EPS || startX > maxX + EPS) hit = false;
            }
            else
            {
                float invD = 1f / dx;
                float t1 = (minX - startX) * invD;
                float t2 = (maxX - startX) * invD;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) hit = false;
            }

            if (hit)
            {
                if (Math.Abs(dy) < 1e-8f)
                {
                    if (startY < minY - EPS || startY > maxY + EPS) hit = false;
                }
                else
                {
                    float invD = 1f / dy;
                    float t1 = (minY - startY) * invD;
                    float t2 = (maxY - startY) * invD;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tMin) tMin = t1;
                    if (t2 < tMax) tMax = t2;
                    if (tMin > tMax) hit = false;
                }
            }

            if (hit)
            {
                if (Math.Abs(dz) < 1e-8f)
                {
                    if (startZ < minZ - EPS || startZ > maxZ + EPS) hit = false;
                }
                else
                {
                    float invD = 1f / dz;
                    float t1 = (minZ - startZ) * invD;
                    float t2 = (maxZ - startZ) * invD;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tMin) tMin = t1;
                    if (t2 < tMax) tMax = t2;
                    if (tMin > tMax) hit = false;
                }
            }

            if (hit) return true;
        }

        return false;
    }

    /// <summary>
    /// Обрабатывает один луч: выполняет DDA-обход вокселей (Amanatides & Woo),
    /// применяет поглощение и генерирует отражения при ударе о непрозрачную поверхность.
    /// </summary>
    private void ProcessRay(LightRay ray)
    {
        float posX = ray.OriginX;
        float posY = ray.OriginY;
        float posZ = ray.OriginZ;
        float dirX = ray.DirX;
        float dirY = ray.DirY;
        float dirZ = ray.DirZ;
        float energy = ray.Energy;

        int x = (int)Math.Floor(posX);
        int y = (int)Math.Floor(posY);
        int z = (int)Math.Floor(posZ);

        int stepX = dirX > 0 ? 1 : -1;
        int stepY = dirY > 0 ? 1 : -1;
        int stepZ = dirZ > 0 ? 1 : -1;

        float tDeltaX = Math.Abs(1.0f / dirX);
        float tDeltaY = Math.Abs(1.0f / dirY);
        float tDeltaZ = Math.Abs(1.0f / dirZ);

        float tMaxX = ((dirX > 0 ? (x + 1 - posX) : (posX - x))) * tDeltaX;
        float tMaxY = ((dirY > 0 ? (y + 1 - posY) : (posY - y))) * tDeltaY;
        float tMaxZ = ((dirZ > 0 ? (z + 1 - posZ) : (posZ - z))) * tDeltaZ;

        if (float.IsNaN(tMaxX)) tMaxX = float.PositiveInfinity;
        if (float.IsNaN(tMaxY)) tMaxY = float.PositiveInfinity;
        if (float.IsNaN(tMaxZ)) tMaxZ = float.PositiveInfinity;

        float currentDistance = 0f;

        // Количество осей, пересеченных на текущем шаге (нужно для корректной обработки углов/ребер)
        int entryCrossCount = 0;
        float entryNormalX = 0, entryNormalY = 0, entryNormalZ = 0;

        int lastChunkX = int.MinValue;
        int lastChunkY = int.MinValue;
        int lastChunkZ = int.MinValue;
        IWorldChunk cachedChunk = null;

        while (energy > 0.01f)
        {


            float tNext = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));

            if (float.IsInfinity(tNext) || float.IsNaN(tNext))
                break;

            float segStart = currentDistance;
            float segEnd = tNext;
            if (segEnd < segStart) segEnd = segStart;

            // Возвращаем приоритет осей (X > Y > Z) для hitFace.
            // Это критически важно для дверей и люков, чтобы центральный тест 
            // в GetEffectiveAbsorption шел вдоль правильной оси и не промахивался 
            // мимо их тонкой геометрии при попаданиях в ребра/углы.
            BlockFacing hitFace;
            if (entryCrossCount > 0)
            {
                if (entryNormalX != 0) hitFace = entryNormalX > 0 ? BlockFacing.EAST : BlockFacing.WEST;
                else if (entryNormalY != 0) hitFace = entryNormalY > 0 ? BlockFacing.UP : BlockFacing.DOWN;
                else hitFace = entryNormalZ > 0 ? BlockFacing.NORTH : BlockFacing.SOUTH;
            }
            else
            {
                // Для самого первого вокселя (где луч родился) грани входа нет
                hitFace = BlockFacing.UP;
            }

            int cx = x >> chunkSizeLog2;
            int cy = y >> chunkSizeLog2; // Это уже Internal chunk Y
            int cz = z >> chunkSizeLog2;

            if (cx != lastChunkX || cy != lastChunkY || cz != lastChunkZ)
            {
                // Убираем + currentDim * 1024 — чанки в кэше хранятся без смещения измерения
                long chunkKey = chunkProvider.ChunkIndex3D(cx, cy, cz);

                cachedChunk = chunkCache.TryGetValue(chunkKey, out var cached) ? cached : null;

                if (cachedChunk is null)
                    cachedChunk = chunkProvider.GetUnpackedChunkFast(cx, cy, cz, notRecentlyAccessed: true);

                lastChunkX = cx;
                lastChunkY = cy;
                lastChunkZ = cz;

                if (cachedChunk is null) break;
            }

            IWorldChunk chunk = cachedChunk;

            int index3d = ((y & chunkSizeMask) * chunkSize + (z & chunkSizeMask)) * chunkSize + (x & chunkSizeMask);

            tmpPos.X = x;
            tmpPos.Y = y;
            tmpPos.Z = z;

            tmpPos.SetDimension(currentDim);

            GetBlockAndAbsorptionCached(chunk, index3d, tmpPos,
                out Block block, out int baseAbsorption, out BlockEntityMicroBlock microBE,
                out Cuboidf[] cachedBoxes, out bool cachedHasGeometry);

            float stepDistance = segEnd - segStart;
            energy -= stepDistance; // Потеря энергии просто от прохождения расстояния в воздухе
            float energyAtSurface = energy;

            bool isOpaque = false;
            bool isDoor = false;

            if (microBE is not null)
            {
                float effectiveAbs = GetEffectiveAbsorption(
                    block, baseAbsorption, hitFace, energy, microBE, tmpPos, false);
                if (effectiveAbs > 0f) energy -= effectiveAbs;
            }
            else
            {
                isDoor = IsDoorBlock(block, tmpPos);

                // Если блок имеет все грани твердыми, то мы точно попадем в коллизию, и нет смысла проверять геометрию.
                int solidMask = GetSolidMask(block, microBE, tmpPos);

                if (solidMask == 63 || block.Id == 0 || block.IsLiquid())
                {
                    float effectiveAbs = GetEffectiveAbsorption(
                        block, baseAbsorption, hitFace, energy, microBE, tmpPos, true, false);

                    if (effectiveAbs > 0f)
                        energy -= effectiveAbs;

                    isOpaque = (block.Id != 0);
                }
                else
                {
                    // Обычный путь: проверка коллизий
                    bool hasGeometry = cachedHasGeometry;
                    bool geometryHit = false;

                    if (hasGeometry)
                    {
                        float startX = posX + dirX * segStart;
                        float startY = posY + dirY * segStart;
                        float startZ = posZ + dirZ * segStart;
                        float endX = posX + dirX * segEnd;
                        float endY = posY + dirY * segEnd;
                        float endZ = posZ + dirZ * segEnd;

                        geometryHit = SegmentIntersectsBoxes(cachedBoxes, tmpPos,
                            startX, startY, startZ, endX, endY, endZ);
                    }

                    // Передаем cachedBoxes в GetEffectiveAbsorption для оптимизации проверки дверей
                    float effectiveAbs = GetEffectiveAbsorption(
                        block, baseAbsorption, hitFace, energy, microBE, tmpPos, geometryHit, false, cachedBoxes);

                    if (effectiveAbs > 0f)
                        energy -= effectiveAbs;

                    if (hasGeometry)
                    {
                        isOpaque = geometryHit || effectiveAbs > 0;
                    }
                    else
                    {
                        // Проверяем твердость граней, через которые прошел луч
                        isOpaque = effectiveAbs > 0 || (
                            (solidMask & (1 << hitFace.Index)) != 0 ||
                            (solidMask & (1 << hitFace.Opposite.Index)) != 0
                        );
                    }
                }
            }

            if (energy > 0f)
            {
                ApplyLightToBlock(x, y, z, energy, ray.SourceId);
            }
            else if (energyAtSurface > 0f)
            {
                if (!isDoor)
                    ApplyLightToBlock(x, y, z, energyAtSurface, ray.SourceId);
            }

            // Однократное отражение
            if (ray.BounceCount == 0 && isOpaque)
            {
                // Для отражений оставляем строгую проверку: отражаем только от чистых 
                // одноосевых попаданий, чтобы избежать артефактов на углах.
                if (entryCrossCount == 1)
                {
                    if (block is null || block.Code is null)
                    {
                        // Unknown block - treat as opaque, stop ray
                        energy = 0;
                        break;
                    }

                    int reflectivity = GetReflectivity(block);

                    if (microBE is not null && reflectivity > 0)
                    {
                        var profile = MicroblockLightCache.GetOrCompute(microBE, blockTypes);
                        reflectivity = reflectivity * profile.VolumeFraction >> 8;
                    }

                    if (reflectivity > 0)
                    {
                        float reflectedEnergy = energyAtSurface * reflectivity / 100f;
                        if (reflectedEnergy > 1f)
                        {
                            float nx = entryNormalX;
                            float ny = entryNormalY;
                            float nz = entryNormalZ;

                            // Точка на поверхности блока + микро-смещение по нормали, чтобы избежать self-intersection
                            float ox = x + 0.5f;
                            float oy = y + 0.5f;
                            float oz = z + 0.5f;

                            if (entryNormalX != 0) ox = (entryNormalX < 0) ? x : x + 1;
                            if (entryNormalY != 0) oy = (entryNormalY < 0) ? y : y + 1;
                            if (entryNormalZ != 0) oz = (entryNormalZ < 0) ? z : z + 1;

                            ox += nx * 0.01f;
                            oy += ny * 0.01f;
                            oz += nz * 0.01f;

                            SpawnReflectionRays(ox, oy, oz, nx, ny, nz,
                                reflectedEnergy, ray.H, ray.S, ray.B, ray.SourceId);
                        }
                    }
                }
            }

            if (energy <= 0)
                break;

            // Вычисляем, какие оси были пересечены на этом шаге (с учетом погрешности float)
            const float TIE_EPS = 1e-5f;
            bool crossX = (tMaxX - tNext) <= TIE_EPS;
            bool crossY = (tMaxY - tNext) <= TIE_EPS;
            bool crossZ = (tMaxZ - tNext) <= TIE_EPS;

            entryCrossCount = (crossX ? 1 : 0) + (crossY ? 1 : 0) + (crossZ ? 1 : 0);
            entryNormalX = crossX ? -stepX : 0f;
            entryNormalY = crossY ? -stepY : 0f;
            entryNormalZ = crossZ ? -stepZ : 0f;

            if (crossX) { x += stepX; tMaxX += tDeltaX; }
            if (crossY) { y += stepY; tMaxY += tDeltaY; }
            if (crossZ) { z += stepZ; tMaxZ += tDeltaZ; }

            currentDistance = tNext;
        }
    }

    /// <summary>Записывает вклад источника в стейджинг-словарь.</summary>
    private void ApplyLightToBlock(int x, int y, int z, float energy, int sourceId)
    {
        int lightLevel = (int)energy;
        if (lightLevel <= 0) return;


        long key = PackPos(x, y, z, currentDim);
        var lsab = GetOrCreateLsab(key);
        lsab.AddOrUpdate(sourceId, (byte)lightLevel);
    }

    /// <summary>
    /// Трассирует все ближайшие источники (прямые + отраженные лучи) в visitedNodes.
    /// Каждый источник обрабатывается полностью перед началом следующего.
    /// </summary>
    private void TraceNearbyBlockLights()
    {
        RecycleVisitedNodes();
        blockLookupCache.Clear();

        // Заполняем shared chunk cache на основе уже загруженных чанков
        chunkCache.Clear();
        for (int srcIdx = 0; srcIdx < nearbyCount; srcIdx++)
        {
            NearbyLightSourceStruct source = nearbyLightSourcesArray[srcIdx];
            int cx = source.posX / chunkSize;
            int cy = source.posY / chunkSize;
            int cz = source.posZ / chunkSize;

            // Добавляем сам чанк + соседей (лучи могут уйти за пределы исходного чанка)
            int radius = BucketRadius(nearbyB[srcIdx]) / chunkSize + 1;
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int nx = cx + dx, ny = cy + dy, nz = cz + dz;

                        long key = chunkProvider.ChunkIndex3D(nx, ny, nz);
                        if (!chunkCache.ContainsKey(key))
                            chunkCache[key] = chunkProvider.GetChunk(nx, ny, nz);
                    }
        }

        for (int srcIdx = 0; srcIdx < nearbyCount; srcIdx++)
        {
            byte h = nearbyH[srcIdx];
            byte s = nearbyS[srcIdx];
            byte brightness = nearbyB[srcIdx];

            if (brightness <= 0)
                continue;

            NearbyLightSourceStruct source = nearbyLightSourcesArray[srcIdx];

            ResetRayPool();

            // Сам блок-источник всегда получает полную яркость
            ApplyLightToBlock(source.posX, source.posY, source.posZ, brightness, srcIdx);

            float sourceX = source.posX + 0.5f;
            float sourceY = source.posY + 0.5f;
            float sourceZ = source.posZ + 0.5f;

            var sphere = GetOrBuildSphereForRadius(brightness);
            float[][] dirs = sphere.dirs;
            int rayCount = sphere.count;

            for (int rayIndex = 0; rayIndex < rayCount; rayIndex++)
            {
                float[] direction = dirs[rayIndex];
                SpawnRay(sourceX, sourceY, sourceZ,
                    direction[0], direction[1], direction[2],
                    brightness, h, s, brightness, 0, srcIdx);
            }

            // Обрабатываем все лучи (включая отражения) для этого источника
            while (activeRayCount > 0)
            {
                LightRay ray = DequeueRay();
                ProcessRay(ray);
            }
        }
    }

    #endregion

    // ─── Инициализация ───────────────────────────────────────────────────

    public LumosChunkIlluminator()
    {
        // Пусто — реальная инициализация происходит в InitFromVanillaConstructor / InitForWorld
    }

    /// <summary>
    /// Вызывается из постфикс-патча конструктора ChunkIlluminator.
    /// Копирует параметры оригинального конструктора и выделяет массивы.
    /// </summary>
    public void InitFromVanillaConstructor(
        IChunkProvider chunkProvider, IBlockAccessor readBlockAccess, int chunkSize)
    {
        this.readBlockAccess = readBlockAccess;
        this.chunkProvider = chunkProvider;
        this.chunkSize = chunkSize;

        int cs = chunkSize, log2 = 0;
        while ((cs >>= 1) > 0) log2++;
        chunkSizeLog2 = log2;
        chunkSizeMask = chunkSize - 1;
        YPlus = chunkSize * chunkSize;
        ZPlus = chunkSize;

        int maxSources = 27 * YPlus * chunkSize;
        nearbyLightSourcesArray = new NearbyLightSourceStruct[maxSources];
        nearbyH = new byte[maxSources];
        nearbyS = new byte[maxSources];
        nearbyB = new byte[maxSources];

        InitRayTracing();
    }

    /// <summary>
    /// Вызывается из постфикс-патча InitForWorld.
    /// Строит кэши для каждого BlockId и сохраняет размеры мира.
    /// </summary>
    public void InitForWorld(IList<Block> blockTypes, ushort defaultSunLight,
        int mapsizex, int mapsizey, int mapsizez)
    {
        this.blockTypes = blockTypes;
        this.defaultSunLight = defaultSunLight;
        this.mapsizex = mapsizex;
        this.mapsizey = mapsizey;
        this.mapsizez = mapsizez;

        isMicroblockCache = new bool[blockTypes.Count];
        isMultiblockCache = new bool[blockTypes.Count];
        isDoorCache = new bool[blockTypes.Count];

        for (int i = 0; i < blockTypes.Count; i++)
        {
            Block b = blockTypes[i];
            isMicroblockCache[i] = b is BlockMicroBlock;
            isMultiblockCache[i] = b is BlockMultiblock;

            bool door = b is BlockBaseDoor || b is BlockTrapdoor; // BlockDoor наследуется от BlockBaseDoor
            if (!door && b.BlockBehaviors is not null)
            {
                foreach (var bh in b.BlockBehaviors)
                {
                    if (bh is BlockBehaviorDoor || bh is BlockBehaviorTrapDoor) { door = true; break; }
                }
            }
            isDoorCache[i] = door;
        }
    }

    // ─── Публичный API: установка / удаление блочного света ──────────────

    /// <summary>
    /// Регистрирует новый источник света и ставит в очередь "грязную" сферу.
    /// Фактический пересчет откладывается до FlushPendingBlockLightUpdates.
    /// </summary>
    public FastSetOfLongs PlaceBlockLight(
        byte[] lightHsv, int posX, int posY, int posZ)
    {
        FastSetOfLongs result = new FastSetOfLongs();

        if (blockTypes is null || lightHsv is null ||
            lightHsv.Length < 3 || lightHsv[2] <= 0)
            return result;

        IWorldChunk chunk = GetChunkAtPos(posX, posY, posZ);
        if (chunk is null)
            return result;

        int lightPosition = InChunkIndex(posX, posY, posZ);

        chunk.LightPositions.Add(lightPosition);

        // Извлекаем dimension из InternalY (если posY >= 32768)
        currentDim = posY >= 32768 ? posY / 32768 : 0;

        QueueDirtyLightSphere(posX, posY, posZ, lightHsv[2]);

        return result;
    }

    /// <summary>
    /// Удаляет источник света и ставит в очередь "грязную" сферу для очистки.
    /// </summary>
    public FastSetOfLongs RemoveBlockLight(
        byte[] oldLightHsv, int posX, int posY, int posZ)
    {
        FastSetOfLongs result = new FastSetOfLongs();

        if (blockTypes is null || oldLightHsv is null || oldLightHsv.Length < 3)
            return result;

        IWorldChunk chunk = GetChunkAtPos(posX, posY, posZ);
        if (chunk is null)
            return result;

        int lightPosition = InChunkIndex(posX, posY, posZ);
        chunk.LightPositions.Remove(lightPosition);

        int oldRadius = oldLightHsv[2];

        // Сохраняем ванильный особый случай для радиуса 18 (используется для факелов и т.п.)
        if (oldRadius == 18)
            oldRadius = 20;

        // Извлекаем dimension из InternalY (если posY >= 32768)
        currentDim = posY >= 32768 ? posY / 32768 : 0;

        QueueDirtyLightSphere(posX, posY, posZ, oldRadius);

        return result;
    }

    /// <summary>
    /// Обрабатывает изменение прозрачности (старое против нового поглощения).
    /// Ставит в очередь сферу максимального радиуса, так как изменение может как
    /// удалить существующий свет, так и открыть ранее заблокированный источник.
    /// </summary>
    public FastSetOfLongs UpdateBlockLight(
        int oldLightAbsorb, int newLightAbsorb,
        int posX, int posY, int posZ)
    {
        FastSetOfLongs result = new FastSetOfLongs();

        if (blockTypes is null)
            return result;

        if (oldLightAbsorb == newLightAbsorb)
            return result;

        QueueDirtyLightSphere(posX, posY, posZ, MAX_BLOCK_LIGHT_LEVEL);

        return result;
    }

    /// <summary>
    /// Ставит в очередь "грязную" сферу и немедленно сбрасывает пакет.
    /// Используется FullRelight и внешними вызовами, которым нужны синхронные результаты.
    /// </summary>
    public void UpdateLightAt(
        int range, int posX, int posY, int posZ,
        FastSetOfLongs touchedChunks)
    {
        if (range <= 0)
            return;

        QueueDirtyLightSphere(posX, posY, posZ, range);

        FastSetOfLongs flushedChunks = FlushPendingLightUpdates();

        foreach (long chunkIndex in flushedChunks)
            touchedChunks.Add(chunkIndex);
    }

    // ─── Поглощение ──────────────────────────────────────────────────────

    /// <summary>
    /// Вычисляет эффективное поглощение света для луча, падающего на грань блока.
    /// Микроблоки: возвращает поглощение для конкретной оси из предрасчитанного профиля.
    /// Двери: направленная проверка через CollisionBoxes (собственный baseAbsorption
    /// в JSON равен 0, поэтому при попадании поглощение форсируется до максимума).
    /// Направленный тест по коробкам НЕ применяется к обычным блокам: пробный отрезок
    /// идёт вдоль одной оси через центр блока и корректно различает "закрыто/открыто"
    /// только для геометрии типа "дверь" (тонкая перпендикулярно направлению, почти
    /// полностью занимает грань). Для плит/ступеней (геометрия занимает лишь часть
    /// блока вдоль тестируемой оси) такой пробник всегда пересекает бокс независимо
    /// от направления и ломает направленность — поэтому для них используется
    /// solidMask/SideIsSolid, который корректно учитывает конкретную грань.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetEffectiveAbsorption(
        Block block, int baseAbsorption, BlockFacing dir,
        float incomingEnergy,
        BlockEntityMicroBlock microBE = null, BlockPos pos = null, bool geometryHit = false, bool isSunlight = true, Cuboidf[] boxes = null)
    {
        // Проверяем двери ПЕРЕД досрочным выходом по baseAbsorption <= 0.
        if (microBE is null && pos is not null && readBlockAccess is not null)
        {
            if (IsDoorBlock(block, pos))
            {
                float cx = pos.X + 0.5f;
                float cy = pos.Y + 0.5f;
                float cz = pos.Z + 0.5f;

                float startX = cx - dir.Normali.X * 0.51f;
                float startY = cy - dir.Normali.Y * 0.51f;
                float startZ = cz - dir.Normali.Z * 0.51f;
                float endX = cx + dir.Normali.X * 0.51f;
                float endY = cy + dir.Normali.Y * 0.51f;
                float endZ = cz + dir.Normali.Z * 0.51f;

                // Используем переданные из кэша коробки, если они есть, иначе вычисляем (для солнечных лучей)
                if (boxes is null) boxes = GetRayCollisionBoxes(block, pos);

                if (boxes is not null && SegmentIntersectsBoxes(boxes, pos, startX, startY, startZ, endX, endY, endZ))
                    return Math.Max(baseAbsorption, MAX_BLOCK_LIGHT_LEVEL + 1);
                return 0f;
            }
        }

        // Полностью прозрачный блок — свет проходит насквозь
        if (baseAbsorption <= 0)
            return 0f;

        // Микроблок: используем предрасчитанный профиль для конкретной оси
        if (microBE is not null)
        {
            MicroblockLightProfile profile =
                MicroblockLightCache.GetOrCompute(microBE, blockTypes);

            int axisIndex = dir.Axis == EnumAxis.X ? 0
                : dir.Axis == EnumAxis.Y ? 1 : 2;

            byte effAbs = profile.GetEffectiveAbsForAxis(axisIndex);
            if (effAbs == 0) return 0f;
            return effAbs;
        }

        // Луч попал в геометрию блока
        if (geometryHit)
        {
            if (isSunlight) // Для солнечного света — упрощенный расчет
            {
                int solidMask = GetSolidMask(block, null, pos);
                // Все грани твердые или все прозрачны — считаем полное поглощение
                if (solidMask == 63 || solidMask == 0)
                    return baseAbsorption;

                BlockFacing incoming = dir.Opposite;
                BlockFacing outgoing = dir;

                bool incomingSolid = (pos is not null && readBlockAccess is not null)
                    ? block.SideIsSolid(readBlockAccess, pos, incoming.Index)
                    : block.SideSolid[incoming.Index];

                bool outgoingSolid = (pos is not null && readBlockAccess is not null)
                    ? block.SideIsSolid(readBlockAccess, pos, outgoing.Index)
                    : block.SideSolid[outgoing.Index];

                // Если хотя бы одна из двух граней (вход/выход) твердая — поглощаем полностью
                if (incomingSolid || outgoingSolid)
                    return baseAbsorption;

                // Иначе — частичное поглощение пропорционально энергии
                float clampedAbs = Math.Min(baseAbsorption, 32);
                return incomingEnergy * clampedAbs / 64f;
            }
            else
            {
                // Для блочного света используем полное поглощение, если попали в геометрию
                return baseAbsorption;
            }
        }
        else
        {
            // Луч прошёл мимо геометрии (прозрачный/пустой внутри коллайда) — поглощения нет
            return 0f;
        }
    }

    // ─── Хелперы чанков ──────────────────────────────────────────────────

    /// <summary>Возвращает распакованный чанк, содержащий заданную мировую позицию.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IWorldChunk GetChunkAtPos(int posX, int posY, int posZ)
    {
        return chunkProvider.GetUnpackedChunkFast(
            posX / chunkSize, posY / chunkSize, posZ / chunkSize,
            notRecentlyAccessed: true);
    }

    /// <summary>Преобразует мировые координаты в плоский индекс внутри чанка.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int InChunkIndex(int posX, int posY, int posZ)
    {
        return (posY % chunkSize * chunkSize + posZ % chunkSize) * chunkSize + posX % chunkSize;
    }

    // ─── Внутренняя механика батчинга ────────────────────────────────────

    /// <summary>Добавляет (или объединяет) "грязную" сферу для следующего сброса.</summary>
    private void QueueDirtyLightSphere(int posX, int posY, int posZ, int lightRadius)
    {
        if (lightRadius <= 0)
            return;

        int radius = lightRadius + DIRTY_RADIUS_PADDING;
        long key = PackPos(posX, posY, posZ, currentDim);

        if (pendingDirtySpheres.TryGetValue(key, out DirtyLightSphere existing))
        {
            if (radius > existing.Radius)
            {
                existing.Radius = radius;
                pendingDirtySpheres[key] = existing;
            }
            return;
        }

        pendingDirtySpheres.Add(key, new DirtyLightSphere(posX, posY, posZ, radius));
    }

    /// <summary>
    /// Сканирует все чанки, источники света которых могут пересекать "грязную" область,
    /// и заполняет массивы ближайших источников (nearbyLightSourcesArray / nearbyH/S/B).
    /// </summary>
    private void LoadSourcesIntersectingDirtySpheres(List<DirtyLightSphere> spheres)
    {
        nearbyCount = 0;
        nearbySourceIndexByPosition.Clear();

        if (spheres.Count == 0 || blockTypes is null || chunkProvider is null)
            return;

        int dimBlockOffset = currentDim * 32768;
        int minValidY = dimBlockOffset;
        int maxValidY = dimBlockOffset + mapsizey - 1;

        int minX = mapsizex - 1, minY = maxValidY, minZ = mapsizez - 1;
        int maxX = 0, maxY = minValidY, maxZ = 0;

        for (int i = 0; i < spheres.Count; i++)
        {
            DirtyLightSphere sphere = spheres[i];
            int scanRadius = sphere.Radius + MAX_BLOCK_LIGHT_LEVEL;

            minX = Math.Min(minX, sphere.X - scanRadius);
            minY = Math.Min(minY, sphere.Y - scanRadius);
            minZ = Math.Min(minZ, sphere.Z - scanRadius);
            maxX = Math.Max(maxX, sphere.X + scanRadius);
            maxY = Math.Max(maxY, sphere.Y + scanRadius);
            maxZ = Math.Max(maxZ, sphere.Z + scanRadius);
        }

        minX = Math.Max(0, minX);
        minY = Math.Max(minValidY, minY);
        minZ = Math.Max(0, minZ);
        maxX = Math.Min(mapsizex - 1, maxX);
        maxY = Math.Min(maxValidY, maxY);
        maxZ = Math.Min(mapsizez - 1, maxZ);

        int minChunkX = minX / chunkSize;
        int minChunkY = minY / chunkSize;
        int minChunkZ = minZ / chunkSize;
        int maxChunkX = maxX / chunkSize;
        int maxChunkY = maxY / chunkSize;
        int maxChunkZ = maxZ / chunkSize;

        for (int chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
        {
            for (int chunkY = minChunkY; chunkY <= maxChunkY; chunkY++)
            {
                for (int chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
                {
                    IWorldChunk chunk = chunkProvider.GetChunk(chunkX, chunkY, chunkZ);
                    if (chunk is null)
                        continue;

                    chunk.Unpack_ReadOnly();

                    foreach (int lightPosition in chunk.LightPositions)
                    {
                        int localY = lightPosition / YPlus;
                        int localZ = (lightPosition / chunkSize) % chunkSize;
                        int localX = lightPosition % chunkSize;

                        int sourceX = chunkX * chunkSize + localX;
                        int sourceY = chunkY * chunkSize + localY;
                        int sourceZ = chunkZ * chunkSize + localZ;

                        long sourceKey = PackPos(sourceX, sourceY, sourceZ, currentDim);

                        // Избегаем дубликатов источников
                        if (nearbySourceIndexByPosition.ContainsKey(sourceKey))
                            continue;

                        Block block = blockTypes[chunk.Data[lightPosition]];

                        byte[] hsv = block.GetLightHsv(
                            readBlockAccess,
                            tmpPos.Set(sourceX, sourceY, sourceZ));

                        if (hsv is null || hsv.Length < 3 || hsv[2] <= 0)
                            continue;

                        int sourceRadius = hsv[2];
                        bool intersectsDirtyRegion = false;

                        for (int si = 0; si < spheres.Count; si++)
                        {
                            DirtyLightSphere sphere = spheres[si];
                            long ddx = (long)sourceX - sphere.X;
                            long ddy = (long)sourceY - sphere.Y;
                            long ddz = (long)sourceZ - sphere.Z;
                            long allowed = sourceRadius + sphere.Radius;

                            if (ddx * ddx + ddy * ddy + ddz * ddz <= allowed * allowed)
                            {
                                intersectsDirtyRegion = true;
                                break;
                            }
                        }

                        if (!intersectsDirtyRegion)
                            continue;

                        TryAddNearbyLightSource(
                            sourceX, sourceY, sourceZ,
                            hsv[0], hsv[1], hsv[2]);
                    }
                }
            }
        }
    }

    /// <summary>Добавляет источник в массивы ближайших, если его там еще нет.</summary>
    private bool TryAddNearbyLightSource(
        int posX, int posY, int posZ,
        byte hue, byte saturation, byte brightness)
    {
        if (brightness == 0)
            return false;

        long positionKey = PackPos(posX, posY, posZ, currentDim);

        if (nearbySourceIndexByPosition.ContainsKey(positionKey))
            return false;

        if (nearbyCount >= nearbyLightSourcesArray.Length)
            return false;

        int sourceIndex = nearbyCount++;

        nearbyLightSourcesArray[sourceIndex] =
            new NearbyLightSourceStruct { posX = posX, posY = posY, posZ = posZ };

        nearbyH[sourceIndex] = hue;
        nearbyS[sourceIndex] = saturation;
        nearbyB[sourceIndex] = brightness;

        nearbySourceIndexByPosition.Add(positionKey, sourceIndex);

        return true;
    }

    /// <summary>
    /// Вычисляет упакованное значение блочного света из всех вкладов источников,
    /// накопленных в стейджинг-словаре для одного блока.
    /// Не затрагивает chunk.Lighting.
    /// Формат упаковки: [0..4] Brightness, [5..9] Hue, [10..15] Saturation.
    /// </summary>
    private int CalculatePackedLight(LightSourcesAtBlock lsab)
    {
        int lightCount = lsab.count;
        if (lightCount <= 0)
            return 0;

        float totalWeight = 0f;
        int maxLevel = 0;

        for (int i = 0; i < lightCount; i++)
        {
            int level = lsab.levels[i];
            if (level > maxLevel) maxLevel = level;
            totalWeight += level;
        }

        if (maxLevel <= 0 || totalWeight <= 0f)
            return 0;

        // Взвешенное смешивание HSV→RGB по всем участвующим источникам
        float r = 0.5f, g = 0.5f, b = 0.5f;

        for (int i = 0; i < lightCount; i++)
        {
            int sourceIndex = lsab.srcIds[i];
            int level = lsab.levels[i];

            if ((uint)sourceIndex >= (uint)nearbyCount)
                continue;

            byte hue = nearbyH[sourceIndex];
            byte saturation = nearbyS[sourceIndex];

            int rgb = ColorUtil.HsvToRgb(hue * 4, saturation * 32, level * 8);

            float weight = (float)level / totalWeight;

            r += (rgb >> 16) * weight;
            g += ((rgb >> 8) & 0xFF) * weight;
            b += (rgb & 0xFF) * weight;
        }

        int mixedHsv = ColorUtil.Rgb2Hsv(r, g, b);

        int mixedHue = Math.Min(
            (int)((mixedHsv & 0xFF) / 4f + 0.5f),
            ColorUtil.HueQuantities - 1);

        int mixedSaturation = Math.Min(
            (int)(((mixedHsv >> 8) & 0xFF) / 32f + 0.5f),
            ColorUtil.SatQuantities - 1);

        return (maxLevel << 5) | (mixedHue << 10) | (mixedSaturation << 16);
    }

    /// <summary>
    /// Фиксирует полностью рассчитанный стейджинг-результат в chunk.Lighting.
    /// Это ЕДИНСТВЕННЫЙ метод в пути пакетного блочного света, который пишет
    /// новые значения BlockLight в живое состояние мира.
    /// </summary>
    private void CommitDirtyLightCells(
        FastSetOfLongs touchedChunks,
        Dictionary<long, IWorldChunk> modifiedChunks)
    {
        int num = chunkSize;

        foreach (long key in dirtyLightCells)
        {
            UnpackPos(key, out int x, out int y, out int z, out int dim);

            IWorldChunk chunk = chunkProvider.GetUnpackedChunkFast(
                x / num, y / num, z / num, notRecentlyAccessed: true);

            if (chunk is null)
                continue;

            int index3d = (y % num * num + z % num) * num + x % num;

            // Нет вклада → свет должен исчезнуть
            int newLight = 0;

            if (visitedNodes.TryGetValue(key, out LightSourcesAtBlock lsab))
                newLight = CalculatePackedLight(lsab);

            int oldLight = chunk.Lighting.GetBlocklight(index3d);

            // Пропускаем неизменившиеся ячейки для избежания лишней инвалидации чанка
            if (oldLight == newLight)
                continue;

            chunk.Lighting.SetBlocklight(index3d, newLight);

            long chunkKey = chunkProvider.ChunkIndex3D(x / num, y / num, z / num);
            touchedChunks.Add(chunkKey);
            modifiedChunks.TryAdd(chunkKey, chunk);
        }
    }

    /// <summary>
    /// Строит точный набор "грязных" ячеек (упакованные ключи) из всех "грязных" сфер.
    /// Использует отсечение по уравнению сферы для каждой оси для плотного прилегания.
    /// </summary>
    private void BuildDirtyLightCellSet(List<DirtyLightSphere> spheres)
    {
        dirtyLightCells.Clear();

        dirtyMinX = int.MaxValue; dirtyMaxX = int.MinValue;
        dirtyMinY = int.MaxValue; dirtyMaxY = int.MinValue;
        dirtyMinZ = int.MaxValue; dirtyMaxZ = int.MinValue;

        int dimOffset = currentDim * 32768; //Смещение измерения для блоков
        int minValidY = dimOffset;
        int maxValidY = dimOffset + mapsizey - 1;

        for (int si = 0; si < spheres.Count; si++)
        {
            DirtyLightSphere sphere = spheres[si];

            int radius = sphere.Radius;
            int radiusSquared = radius * radius;

            int minX = Math.Max(0, sphere.X - radius);
            int maxX = Math.Min(mapsizex - 1, sphere.X + radius);
            int minY = Math.Max(minValidY, sphere.Y - radius);
            int maxY = Math.Min(maxValidY, sphere.Y + radius);
            int minZAll = Math.Max(0, sphere.Z - radius);
            int maxZAll = Math.Min(mapsizez - 1, sphere.Z + radius);

            if (minX < dirtyMinX) dirtyMinX = minX;
            if (maxX > dirtyMaxX) dirtyMaxX = maxX;
            if (minY < dirtyMinY) dirtyMinY = minY;
            if (maxY > dirtyMaxY) dirtyMaxY = maxY;
            if (minZAll < dirtyMinZ) dirtyMinZ = minZAll;
            if (maxZAll > dirtyMaxZ) dirtyMaxZ = maxZAll;

            for (int x = minX; x <= maxX; x++)
            {
                int dx = x - sphere.X;
                int dxSquared = dx * dx;

                for (int y = minY; y <= maxY; y++)
                {
                    int dy = y - sphere.Y;
                    int xySquared = dxSquared + dy * dy;

                    if (xySquared > radiusSquared)
                        continue;

                    int zRadius = (int)Math.Sqrt(radiusSquared - xySquared);

                    int minZ = Math.Max(0, sphere.Z - zRadius);
                    int maxZ = Math.Min(mapsizez - 1, sphere.Z + zRadius);

                    for (int z = minZ; z <= maxZ; z++)
                        dirtyLightCells.Add(PackPos(x, y, z, currentDim));
                }
            }
        }
    }

    /// <summary>
    /// Вычисляет и фиксирует один полный пакет блочного света.
    /// Живые массивы Lighting НЕ очищаются перед трассировкой, поэтому игрок
    /// продолжает видеть предыдущее валидное освещение во время расчета лучей.
    /// </summary>
    public FastSetOfLongs FlushPendingBlockLightUpdates()
    {
        FastSetOfLongs touchedChunks = new FastSetOfLongs();

        if (isFlushingBlockLight || pendingDirtySpheres.Count == 0)
            return touchedChunks;

        isFlushingBlockLight = true;

        Dictionary<long, IWorldChunk> modifiedChunks =
            new Dictionary<long, IWorldChunk>(128);

        try
        {
            dirtySphereBuffer.Clear();

            foreach (DirtyLightSphere sphere in pendingDirtySpheres.Values)
                dirtySphereBuffer.Add(sphere);

            // Отсоединяем перед расчетом: изменения, прибывающие во время этого прохода,
            // останутся в очереди для следующего сброса.
            pendingDirtySpheres.Clear();

            // 1. Строим объединение "грязных" ячеек (живые данные не изменены)
            BuildDirtyLightCellSet(dirtySphereBuffer);

            if (dirtyLightCells.Count == 0)
                return touchedChunks;

            // 2. Находим все источники, влияющие на "грязную" область
            LoadSourcesIntersectingDirtySpheres(dirtySphereBuffer);

            // 3. Дорогостоящий этап: трассировка в стейджинг (chunk.Lighting не тронут)
            TraceNearbyBlockLights();

            // 4. Быстрый этап: фиксация полного результата
            CommitDirtyLightCells(touchedChunks, modifiedChunks);

            // Помечаем каждый измененный чанк один раз
            foreach (IWorldChunk chunk in modifiedChunks.Values)
                chunk.MarkModified();

            return touchedChunks;
        }
        finally
        {
            dirtyLightCells.Clear();
            dirtySphereBuffer.Clear();
            isFlushingBlockLight = false;
        }
    }

    // ─── Полный пересчет (Full relight) ──────────────────────────────────

    /// <summary>
    /// Полный пересчет солнечного и блочного света в кубической области.
    /// Расширяется на один чанк в каждом направлении для корректного потока на границах.
    /// </summary>
    public void FullRelight(BlockPos minPos, BlockPos maxPos)
    {
        int num = chunkSize;
        Dictionary<Vec3i, IWorldChunk> dictionary = new Dictionary<Vec3i, IWorldChunk>();

        // Расширяем регион на 1 чанк для корректности границ
        int num2 = GameMath.Clamp(Math.Min(minPos.X, maxPos.X) - num, 0, mapsizex - 1);
        int num3 = GameMath.Clamp(Math.Min(minPos.Y, maxPos.Y) - num, 0, mapsizey - 1);
        int num4 = GameMath.Clamp(Math.Min(minPos.Z, maxPos.Z) - num, 0, mapsizez - 1);
        int num5 = GameMath.Clamp(Math.Max(minPos.X, maxPos.X) + num, 0, mapsizex - 1);
        int num6 = GameMath.Clamp(Math.Max(minPos.Y, maxPos.Y) + num, 0, mapsizey - 1);
        int num7 = GameMath.Clamp(Math.Max(minPos.Z, maxPos.Z) + num, 0, mapsizez - 1);

        int num8 = num2 / num;
        int num9 = num3 / num;
        int num10 = num4 / num;
        int num11 = num5 / num;
        int num12 = num6 / num;
        int num13 = num7 / num;

        int num14 = minPos.dimension * 1024;

        currentDim = minPos.dimension;

        // Загружаем и распаковываем все затронутые чанки
        IWorldChunk chunk;
        for (int i = num8; i <= num11; i++)
            for (int j = num9; j <= num12; j++)
                for (int k = num10; k <= num13; k++)
                {
                    chunk = chunkProvider.GetChunk(i, j + num14, k);
                    if (chunk is not null)
                    {
                        chunk.Unpack();
                        dictionary[new Vec3i(i, j, k)] = chunk;
                    }
                }

        // Очищаем старый свет
        foreach (IWorldChunk value2 in dictionary.Values)
            value2?.Lighting.ClearLight();

        // Солнечный свет: прямой сверху вниз + горизонтальное распространение + обмен с соседями
        // Массив представляет вертикальную колонку чанков для текущего X/Z
        IWorldChunk[] array = new IWorldChunk[mapsizey / num];

        for (int l = num8; l <= num11; l++)
        {
            for (int m = num10; m <= num13; m++)
            {
                bool flag = false;
                for (int n = 0; n < array.Length; n++)
                {
                    array[n] = chunkProvider.GetChunk(l, n + num14, m);
                    if (array[n] is null) flag = true;
                }

                if (!flag)
                {
                    Sunlight(array, l, array.Length - 1, m, minPos.dimension);
                    SunlightFlood(array, l, array.Length - 1, m);
                    SunLightFloodNeighbourChunks(array, l, array.Length - 1, m, minPos.dimension);
                }
            }
        }

        // Блочный свет: строим ограничивающую сферу и сбрасываем пакет
        int dimOffset = minPos.dimension * 32768;
        int centerX = (num2 + num5) / 2;
        int centerY = ((num3 + num6) / 2) + dimOffset; // Учитываем смещение измерения!
        int centerZ = (num4 + num7) / 2;

        double halfX = (num5 - num2) * 0.5;
        double halfY = (num6 - num3) * 0.5;
        double halfZ = (num7 - num4) * 0.5;

        int fullRelightRadius = (int)Math.Ceiling(
            Math.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ));

        FastSetOfLongs touchedChunks = new FastSetOfLongs();
        UpdateLightAt(fullRelightRadius, centerX, centerY, centerZ, touchedChunks);

        foreach (IWorldChunk value in dictionary.Values)
            value?.MarkModified();
    }

    // ─── Солнечный свет ──────────────────────────────────────────────────

    /// <summary>
    /// Прямой солнечный свет: проход сверху вниз по колонкам с учетом поглощения блоков.
    /// Работает со стейджинг-буфером для предотвращения мерцания.
    /// </summary>
    public void Sunlight(IWorldChunk[] chunks, int chunkX, int chunkY, int chunkZ, int dim)
    {
        tmpPosDimensionAware.SetDimension(dim);
        int num = chunkSize;
        int dimOffset = dim * 1024;

        if (chunkY != chunks.Length - 1) chunks[chunkY + 1].Unpack();
        for (int num2 = chunkY; num2 >= 0; num2--) chunks[num2].Unpack();

        // staging/Lighting резолвятся один раз на чанк колонки, а не на каждый воксель
        bool stagingActive = currentSunStaging.Count > 0;
        byte[][] staging = new byte[chunks.Length][];
        IChunkLight[] lighting = new IChunkLight[chunks.Length];
        for (int c = 0; c < chunks.Length; c++)
        {
            lighting[c] = chunks[c].Lighting;
            if (stagingActive) staging[c] = GetStagingForChunk(chunkX, c + dimOffset, chunkZ);
        }

        int num3 = chunkX * num;
        int num4 = chunkZ * num;

        for (int i = 0; i < num; i++)
        {
            for (int j = 0; j < num; j++)
            {
                // Берем свет снизу вышестоящего чанка (или дефолтный, если это крыша мира)
                int num5 = defaultSunLight;
                if (chunkY != chunks.Length - 1)
                    num5 = ReadSun(staging[chunkY + 1], lighting[chunkY + 1], j * num + i);

                for (int num6 = chunkY; num6 >= 0; num6--)
                {
                    int num7 = ((num - 1) * num + j) * num + i;
                    IWorldChunk worldChunk = chunks[num6];
                    byte[] st = staging[num6];
                    IChunkLight lg = lighting[num6];

                    for (int num8 = num - 1; num8 >= 0; num8--)
                    {
                        tmpPosDimensionAware.Set(num3 + i, num6 * num + num8, num4 + j);
                        GetBlockAndAbsorption(worldChunk, num7, tmpPosDimensionAware,
                            out Block block, out int lightAbsorptionAt, out BlockEntityMicroBlock microBE);

                        float effectiveAbs = GetEffectiveAbsorption(
                            block, lightAbsorptionAt, BlockFacing.DOWN, num5, microBE, tmpPosDimensionAware, true);

                        if (effectiveAbs > num5)
                        {
                            WriteSun(st, lg, num7, num5);
                            num6 = -1;
                            break;
                        }

                        WriteSun(st, lg, num7, num5);
                        num7 -= YPlus;
                        num5 -= (ushort)effectiveAbs;
                        tmpPosDimensionAware.Y--;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Горизонтальное распространение солнечного света (BFS) внутри колонки чанков.
    /// Находит перепады освещения между соседними блоками и добавляет их в стек для BFS.
    /// </summary>
    public void SunlightFlood(IWorldChunk[] chunks, int chunkX, int chunkY, int chunkZ)
    {
        int num = chunkSize;
        Stack<FastBlockPos> stack = new Stack<FastBlockPos>();
        int num2 = chunkX * num;
        int num3 = chunkZ * num;
        bool stagingActive = currentSunStaging.Count > 0;

        for (int num4 = chunkY; num4 >= 0; num4--)
        {
            IWorldChunk worldChunk = chunks[num4];
            worldChunk.Unpack();

            byte[] st = stagingActive ? GetStagingForChunk(chunkX, num4 + currentDimOffset, chunkZ) : null;
            IChunkLight lg = worldChunk.Lighting;

            for (int i = 0; i < num; i++)
            {
                tmpPosDimensionAware.Set(num2 + i, num4 * num + num, num3);
                for (int j = 0; j < num; j++)
                {
                    int num5 = (num * num + j) * num + i;
                    tmpPosDimensionAware.Z = num3 + j;
                    for (int num6 = num - 1; num6 >= 0; num6--)
                    {
                        num5 -= YPlus;
                        tmpPosDimensionAware.Y--;
                        int num7 = ReadSun(st, lg, num5) - 1;

                        if (num7 <= 0) break;

                        // Проверяем 4 горизонтальных соседа на наличие градиента (распространения)
                        if ((i < num - 1 && ReadSun(st, lg, num5 + XPlus) < num7) ||
                            (j < num - 1 && ReadSun(st, lg, num5 + ZPlus) < num7) ||
                            (i > 0 && ReadSun(st, lg, num5 - XPlus) < num7) ||
                            (j > 0 && ReadSun(st, lg, num5 - ZPlus) < num7))
                        {
                            stack.Push(new FastBlockPos(num2 + i, num4 * num + num6, num3 + j, tmpPosDimensionAware.dimension));
                            if (stack.Count > 50) SpreadSunLightInColumn(stack, chunks);
                        }
                    }
                }
            }
        }
        SpreadSunLightInColumn(stack, chunks);
    }

    /// <summary>
    /// Обмен солнечным светом через горизонтальные границы чанков (4 направления).
    /// Обрабатывает переходы света между соседними чанками, учитывая направленное поглощение.
    /// </summary>
    public byte SunLightFloodNeighbourChunks(IWorldChunk[] curChunks, int chunkX, int chunkY, int chunkZ, int dimension)
    {
        tmpPosDimensionAware.SetDimension(dimension);
        int num = chunkSize;
        byte b = 0;
        Stack<FastBlockPos> stack = new Stack<FastBlockPos>();
        Stack<FastBlockPos> stack2 = new Stack<FastBlockPos>();
        int[] array = new int[2];
        int[] array2 = new int[3];
        IWorldChunk[] array3 = new IWorldChunk[curChunks.Length];
        int num2 = chunkX * num;
        int num3 = chunkZ * num;
        int dimOffset = dimension * 1024;
        bool stagingActive = currentSunStaging.Count > 0;

        foreach (BlockFacing blockFacing in BlockFacing.HORIZONTALS)
        {
            bool flag = true;
            int x = blockFacing.Normali.X;
            int z = blockFacing.Normali.Z;

            for (int j = 0; j < curChunks.Length; j++)
            {
                array3[j] = chunkProvider.GetChunk(chunkX + x, j + dimOffset, chunkZ + z);
                if (array3[j] == null) { flag = false; break; }
                array3[j].Unpack();
                curChunks[j].Unpack();
            }
            if (!flag) continue;

            int y = blockFacing.Normali.Y;
            array2[0] = (num - 1) * Math.Max(0, x);
            array2[1] = (num - 1) * Math.Max(0, y);
            array2[2] = (num - 1) * Math.Max(0, z);

            int num4 = (chunkX + x) * num;
            // Базовая мировая Z-координата соседнего чанка
            int numZBase = (chunkZ + z) * num;

            int num5 = 0;
            if (x == 0) array[num5++] = 0;
            if (y == 0) array[num5++] = 1;
            if (z == 0) array[num5++] = 2;

            int fixedNeighbourX = (x != 0) ? GameMath.Mod(array2[0] + x, num) : 0;
            int fixedNeighbourZ = (z != 0) ? GameMath.Mod(array2[2] + z, num) : 0;

            for (int num6 = chunkY; num6 >= 0; num6--)
            {
                IWorldChunk worldChunk = array3[num6];
                IWorldChunk worldChunk2 = curChunks[num6];

                // один раз на пару чанков (текущий + соседний), а не на каждый воксель границы
                byte[] stCur = stagingActive ? GetStagingForChunk(chunkX, num6 + dimOffset, chunkZ) : null;
                byte[] stN = stagingActive ? GetStagingForChunk(chunkX + x, num6 + dimOffset, chunkZ + z) : null;
                IChunkLight lgCur = worldChunk2.Lighting;
                IChunkLight lgN = worldChunk.Lighting;

                for (int num7 = num - 1; num7 >= 0; num7--)
                {
                    array2[array[0]] = num7;
                    for (int num8 = num - 1; num8 >= 0; num8--)
                    {
                        array2[array[1]] = num8;
                        int index3d = (array2[1] * num + array2[2]) * num + array2[0];
                        int num9 = (x != 0) ? fixedNeighbourX : array2[0];
                        int num10 = (z != 0) ? fixedNeighbourZ : array2[2];
                        int index3d2 = (array2[1] * num + num10) * num + num9;

                        int curLight = ReadSun(stCur, lgCur, index3d);
                        int nLight = ReadSun(stN, lgN, index3d2);

                        BlockFacing dir = blockFacing;
                        BlockFacing oppDir = dir.Opposite;

                        tmpPos2.Set(num2 + array2[0], num6 * num + array2[1], num3 + array2[2]);
                        tmpPos2.dimension = dimension;
                        GetBlockAndAbsorption(worldChunk2, index3d, tmpPos2,
                            out Block curBlock, out int curBaseAbs, out BlockEntityMicroBlock curMicroBE);

                        // Передаем корректные мировые координаты соседнего блока
                        tmpPosDimensionAware.Set(num4 + num9, num6 * num + array2[1], numZBase + num10);
                        GetBlockAndAbsorption(worldChunk, index3d2, tmpPosDimensionAware,
                            out Block nBlock, out int nBaseAbs, out BlockEntityMicroBlock nMicroBE);

                        float absCurToN = GetEffectiveAbsorption(curBlock, curBaseAbs, dir, curLight, curMicroBE, tmpPos2, true);
                        int lightArrivingAtN = curLight - (int)absCurToN - 1;
                        float absNFromCur = GetEffectiveAbsorption(nBlock, nBaseAbs, dir, lightArrivingAtN, nMicroBE, tmpPosDimensionAware, true);
                        int finalLightToN = lightArrivingAtN;
                        if (absNFromCur > lightArrivingAtN) finalLightToN = 0;

                        float absNToCur = GetEffectiveAbsorption(nBlock, nBaseAbs, oppDir, nLight, nMicroBE, tmpPosDimensionAware, true);
                        int lightArrivingAtCur = nLight - (int)absNToCur - 1;
                        float absCurFromN = GetEffectiveAbsorption(curBlock, curBaseAbs, oppDir, lightArrivingAtCur, curMicroBE, tmpPos2, true);
                        int finalLightToCur = lightArrivingAtCur;
                        if (absCurFromN > lightArrivingAtCur) finalLightToCur = 0;

                        if (finalLightToN > nLight)
                        {
                            WriteSun(stN, lgN, index3d2, finalLightToN);

                            stack2.Push(new FastBlockPos(num4 + num9, num6 * num + array2[1], numZBase + num10, dimension));
                            b |= blockFacing.Flag;
                        }
                        else if (finalLightToCur > curLight)
                        {
                            WriteSun(stCur, lgCur, index3d, finalLightToCur);
                            stack.Push(new FastBlockPos(num2 + array2[0], num6 * num + array2[1], num3 + array2[2], dimension));
                        }
                    }
                }
                if (stack2.Count > 0) SpreadSunLightInColumn(stack2, array3);
                if (stack.Count > 0) SpreadSunLightInColumn(stack, curChunks);
            }
        }
        return b;
    }

    /// <summary>
    /// Обрабатывает список запланированных обновлений блочного света (например, при загрузке чанка).
    /// Регистрирует источники и сбрасывает весь пакет один раз.
    /// </summary>
    public void ProcessScheduledBlockLightUpdates(List<Vec4i> scheduledUpdates)
    {
        if (scheduledUpdates is null || scheduledUpdates.Count == 0)
            return;

        BlockPos blockPos = new BlockPos(0);

        foreach (Vec4i item in scheduledUpdates)
        {
            Block block = blockTypes[item.W];

            blockPos.SetAndCorrectDimension(item.X, item.Y, item.Z);

            byte[] hsv = block.GetLightHsv(readBlockAccess, blockPos);

            if (hsv is null || hsv.Length < 3 || hsv[2] <= 0)
                continue;

            int x = blockPos.X;
            int y = blockPos.InternalY;
            int z = blockPos.Z;

            IWorldChunk chunk = GetChunkAtPos(x, y, z);
            if (chunk is null) continue;

            int lightPosition = InChunkIndex(x, y, z);

            chunk.LightPositions.Add(lightPosition);

            QueueDirtyLightSphere(x, y, z, hsv[2]);
        }

        FlushPendingLightUpdates();
    }

    /// <summary>
    /// BFS-распространение солнечного света из стека затравочных позиций внутри колонки.
    /// </summary>
    private void SpreadSunLightInColumn(Stack<FastBlockPos> stack, IWorldChunk[] chunks)
    {
        int num = chunkSize;
        bool stagingActive = currentSunStaging.Count > 0;

        // Кэш "последнего чанка": в BFS смена чанка редка, обычно это вертикальные переходы
        int cachedChunkY = int.MinValue;
        byte[] st = null;
        IChunkLight lg = null;

        while (stack.Count > 0)
        {
            FastBlockPos pos = stack.Pop();
            int chunkX = pos.X >> chunkSizeLog2;
            int chunkY = pos.Y >> chunkSizeLog2;
            int chunkZ = pos.Z >> chunkSizeLog2;
            int localX = pos.X & chunkSizeMask;
            int localY = pos.Y & chunkSizeMask;
            int localZ = pos.Z & chunkSizeMask;
            int index3d = (localY * num + localZ) * num + localX;

            IWorldChunk worldChunk = chunks[chunkY];
            tmpPos.Set(pos.X, pos.Y, pos.Z);
            tmpPos.dimension = pos.Dim;

            if (chunkY != cachedChunkY)
            {
                cachedChunkY = chunkY;
                st = stagingActive ? GetStagingForChunk(chunkX, chunkY + currentDimOffset, chunkZ) : null;
                lg = worldChunk.Lighting;
            }

            GetBlockAndAbsorption(worldChunk, index3d, tmpPos,
                out Block posBlock, out int baseAbsorption, out BlockEntityMicroBlock posMicroBE);
            int currentLight = ReadSun(st, lg, index3d);

            if (currentLight <= 0) continue;
            int lastChunkY = chunkY;

            for (int i = 0; i < 6; i++)
            {
                Vec3i vec3i = BlockFacing.ALLNORMALI[i];
                int ny = pos.Y + vec3i.Y;
                int nlx = localX + vec3i.X;
                int nlz = localZ + vec3i.Z;

                if (nlx >= 0 && ny >= 0 && nlz >= 0 && nlx < num && ny < mapsizey && nlz < num)
                {
                    int nChunkY = ny >> chunkSizeLog2;
                    if (nChunkY != lastChunkY) { worldChunk = chunks[nChunkY]; lastChunkY = nChunkY; }

                    int nIndex3d = ((ny & chunkSizeMask) * num + nlz) * num + nlx;
                    BlockFacing dir = BlockFacing.ALLFACES[i];

                    float effectiveAbs = GetEffectiveAbsorption(posBlock, baseAbsorption, dir, currentLight, posMicroBE, tmpPos, true);

                    int newLight = currentLight - (int)effectiveAbs - 1;
                    if (newLight <= 0) continue;

                    // Для соседа:
                    tmpPos2.Set(chunkX * num + nlx, ny, chunkZ * num + nlz);
                    GetBlockAndAbsorption(worldChunk, nIndex3d, tmpPos2,
                        out Block nBlock, out int nBaseAbs, out BlockEntityMicroBlock nMicroBE);

                    int finalLight = newLight;

                    // Сосед может лежать в другом чанке — резолвим его staging/Lighting только при пересечении границы
                    byte[] nSt = st;
                    IChunkLight nLg = lg;
                    if (nChunkY != cachedChunkY)
                    {
                        nSt = stagingActive ? GetStagingForChunk(chunkX, nChunkY + currentDimOffset, chunkZ) : null;
                        nLg = worldChunk.Lighting;
                    }

                    if (ReadSun(nSt, nLg, nIndex3d) < finalLight)
                    {
                        WriteSun(nSt, nLg, nIndex3d, finalLight);
                        stack.Push(new FastBlockPos(chunkX * num + nlx, ny, chunkZ * num + nlz, pos.Dim));
                    }
                }
            }
        }
    }

    /// <summary>Возвращает уровень солнечного света по мировым координатам.</summary>
    private int SunLightLevelAt(int posX, int posY, int posZ, bool substractAbsorb = false)
    {
        int num = chunkSize;
        IWorldChunk unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
            posX / num, posY / num, posZ / num, notRecentlyAccessed: true);
        if (unpackedChunkFast is null)
            return defaultSunLight;

        int index3d = (posY % num * num + posZ % num) * num + posX % num;

        if (!substractAbsorb)
            return unpackedChunkFast.Lighting.GetSunlight(index3d);

        tmpPos.Set(posX, posY, posZ);
        GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpPos,
            out _, out int abs, out _);
        return unpackedChunkFast.Lighting.GetSunlight(index3d) - abs;
    }

    // ─── Внутренняя механика батчинга солнечного света ───────────────────

    /// <summary>Очередь обновлений солнечного света: упакованный ключ колонки → максимальный startChunkY.</summary>
    private readonly Dictionary<long, int> pendingSunlightUpdates = new(64);

    /// <summary>Упаковывает координаты колонки чанков и измерение в один long.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackColumn(int cx, int cz, int dim)
    {
        return ((long)(cx & 0x1FFFFF)) | ((long)(cz & 0x1FFFFF) << 21) | ((long)(dim & 0x3FF) << 42);
    }

    /// <summary>
    /// Обновляет солнечный свет при изменении прозрачности блока.
    /// ОТЛОЖЕННЫЙ ВЫЗОВ: просто ставит колонку в очередь для пакетной обработки в конце тика.
    /// </summary>
    public FastSetOfLongs UpdateSunLight(
        int posX, int posY, int posZ, int oldAbsorb, int newAbsorb)
    {
        // Возвращаем пустой набор — реальные измененные чанки будут добавлены во время Flush
        FastSetOfLongs touchedChunks = new FastSetOfLongs();

        if (newAbsorb == oldAbsorb)
            return touchedChunks;

        int chunkX = posX >> chunkSizeLog2;
        int chunkZ = posZ >> chunkSizeLog2;
        int dim = posY / 32768;

        // Вычисляем локальный Y и локальный chunkY!
        int localY = posY - dim * 32768;
        int chunkY = localY >> chunkSizeLog2;

        // Ставим в очередь центральную колонку и 4 соседних (крестом)
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx != 0 && dz != 0) continue;

                int cx = chunkX + dx;
                int cz = chunkZ + dz;

                if (cx < 0 || cz < 0 || cx * chunkSize >= mapsizex || cz * chunkSize >= mapsizez)
                    continue;

                long key = PackColumn(cx, cz, dim);
                if (!pendingSunlightUpdates.TryGetValue(key, out int maxY) || chunkY > maxY)
                {
                    pendingSunlightUpdates[key] = chunkY;
                }
            }
        }

        return touchedChunks;
    }

    /// <summary>Возвращает стейджинг-массив для данного чанка (если он активен), иначе null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] GetStagingForChunk(int cx, int cyWithDim, int cz)
    {
        return currentSunStaging.TryGetValue(chunkProvider.ChunkIndex3D(cx, cyWithDim, cz), out var s) ? s : null;
    }

    /// <summary>Читает солнечный свет из staging (если не null) или напрямую из Lighting чанка.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadSun(byte[] staging, IChunkLight lighting, int idx)
    {
        return staging is not null ? staging[idx] : lighting.GetSunlight(idx);
    }

    /// <summary>Записывает солнечный свет в staging (если не null) или напрямую в Lighting чанка.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSun(byte[] staging, IChunkLight lighting, int idx, int val)
    {
        if (staging is not null) staging[idx] = (byte)val;
        else lighting.SetSunlight(idx, val);
    }


    /// <summary>
    /// Вычисляет и применяет один полный пакет обновлений солнечного света.
    /// Работает во временных массивах (staging), чтобы рендер-поток не видел
    /// промежуточное "обнуленное" состояние, что полностью устраняет мерцание мобов и меша.
    /// </summary>
    public FastSetOfLongs FlushPendingSunLightUpdates()
    {
        FastSetOfLongs touchedChunks = new FastSetOfLongs();
        if (pendingSunlightUpdates.Count == 0) return touchedChunks;

        var updates = new Dictionary<long, int>(pendingSunlightUpdates);
        pendingSunlightUpdates.Clear();

        int num = chunkSize;
        int chunksPerColumn = mapsizey / num;
        int totalBlocks = num * num * num;

        var validColumns = new List<(int cx, int cz, int dim, int startChunkY, IWorldChunk[] chunks)>();

        foreach (var kvp in updates)
        {
            long key = kvp.Key;
            int startChunkY = kvp.Value;

            int cx = (int)(key & 0x1FFFFF);
            int cz = (int)((key >> 21) & 0x1FFFFF);
            int dim = (int)((key >> 42) & 0x3FF);

            if ((cx & 0x100000) != 0) cx |= unchecked((int)0xFFE00000);
            if ((cz & 0x100000) != 0) cz |= unchecked((int)0xFFE00000);
            if ((dim & 0x200) != 0) dim |= unchecked((int)0xFFFFFC00);

            int dimOffset = dim * 1024;
            IWorldChunk[] chunks = new IWorldChunk[chunksPerColumn];
            bool allLoaded = true;

            for (int cy = 0; cy < chunksPerColumn; cy++)
            {
                chunks[cy] = chunkProvider.GetChunk(cx, cy + dimOffset, cz);
                if (chunks[cy] is null) { allLoaded = false; break; }
                chunks[cy].Unpack();
            }
            if (allLoaded) validColumns.Add((cx, cz, dim, startChunkY, chunks));
        }

        if (validColumns.Count == 0) return touchedChunks;

        currentSunStaging.Clear();

        // 1. Выделяем стейджинг-массивы и копируем старый свет
        foreach (var (cx, cz, dim, startChunkY, chunks) in validColumns)
        {
            int dimOffset = dim * 1024;
            for (int cy = 0; cy <= startChunkY; cy++)
            {
                long chunkKey = chunkProvider.ChunkIndex3D(cx, cy + dimOffset, cz);
                if (!currentSunStaging.ContainsKey(chunkKey))
                    currentSunStaging[chunkKey] = RentStagingArray();

                var staging = currentSunStaging[chunkKey];
                var lighting = chunks[cy].Lighting;
                for (int idx = 0; idx < totalBlocks; idx++)
                    staging[idx] = (byte)lighting.GetSunlight(idx);
            }
        }

        // 2. Гасим и пересчитываем свет строго внутри стейджинг-буфера
        foreach (var (cx, cz, dim, startChunkY, chunks) in validColumns)
        {
            currentDimOffset = dim * 1024;
            int dimOffset = currentDimOffset;

            for (int cy = startChunkY; cy >= 0; cy--)
            {
                long chunkKey = chunkProvider.ChunkIndex3D(cx, cy + dimOffset, cz);
                Array.Clear(currentSunStaging[chunkKey], 0, totalBlocks);
            }

            Sunlight(chunks, cx, startChunkY, cz, dim);
            SunlightFlood(chunks, cx, startChunkY, cz);
        }

        // 3. Обмен на границах чанков
        foreach (var (cx, cz, dim, startChunkY, chunks) in validColumns)
        {
            currentDimOffset = dim * 1024;
            SunLightFloodNeighbourChunks(chunks, cx, startChunkY, cz, dim);
        }

        // 4. Атомарный коммит в живые массивы Lighting
        foreach (var (cx, cz, dim, startChunkY, chunks) in validColumns)
        {
            int dimOffset = dim * 1024;
            for (int cy = startChunkY; cy >= 0; cy--)
            {
                long chunkKey = chunkProvider.ChunkIndex3D(cx, cy + dimOffset, cz);
                var staging = currentSunStaging[chunkKey];
                var lighting = chunks[cy].Lighting;

                bool chunkChanged = false;
                for (int idx = 0; idx < totalBlocks; idx++)
                {
                    int oldSun = lighting.GetSunlight(idx);
                    int newSun = staging[idx];
                    if (oldSun != newSun)
                    {
                        lighting.SetSunlight(idx, newSun);
                        chunkChanged = true;
                    }
                }

                if (chunkChanged)
                {
                    touchedChunks.Add(chunkKey);
                    chunks[cy].MarkModified();
                }
            }
        }

        // 5. Возвращаем стейджинг-массивы в пул (Zero-GC)
        foreach (var arr in currentSunStaging.Values)
        {
            if (sunStagingPool.Count < 512) sunStagingPool.Push(arr);
        }
        currentSunStaging.Clear();

        return touchedChunks;
    }

    /// <summary>
    /// Обёртка, которая сбрасывает и блочный, и солнечный свет одной пачкой.
    /// Возвращает объединённый набор изменённых чанков от обоих процессов.
    /// </summary>
    public FastSetOfLongs FlushPendingLightUpdates()
    {
        FastSetOfLongs touchedChunks = FlushPendingBlockLightUpdates();
        FastSetOfLongs sunTouched = FlushPendingSunLightUpdates();
        foreach (long chunk in sunTouched) touchedChunks.Add(chunk);
        return touchedChunks;
    }

    /// <summary>
    /// Проверяет, достигает ли солнечный свет блока напрямую сверху
    /// (нет непрозрачных препятствий в колонне). Используется для оптимизации BFS.
    /// </summary>
    public bool IsDirectlyIlluminated(int posX, int posY, int posZ)
    {
        int num = chunkSize;
        int num2 = 0;

        // Учитываем смещение измерения
        int dim = posY / 32768;
        int dimOffset = dim * 32768;
        int maxY = dimOffset + mapsizey;

        int num3 = SunLightLevelAt(posX, posY, posZ);

        while (posY < maxY) // Сравниваем с верхней границей текущего измерения
        {
            posY++;
            IWorldChunk unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
                posX / num, posY / num, posZ / num);
            if (unpackedChunkFast is null) break;

            int index3d = (posY % num * num + posZ % num) * num + posX % num;
            int sunlight = unpackedChunkFast.Lighting.GetSunlight(index3d);

            tmpDiPos.Set(posX, posY, posZ);
            GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpDiPos,
                out Block block, out int baseAbs, out BlockEntityMicroBlock microBE);

            num2 += (int)GetEffectiveAbsorption(
                block, baseAbs, BlockFacing.DOWN, defaultSunLight - num2, microBE, tmpDiPos, true);

            if (defaultSunLight - num2 < num3) return false;
            if (sunlight == defaultSunLight) return true;
            if (num3 > sunlight) return false;
        }

        return defaultSunLight - num2 == num3;
    }

    /// <summary>
    /// BFS-распространение солнечного света из очереди упакованных позиций.
    /// Используется ванильным конвейером пересчета для инкрементальных обновлений.
    /// 
    /// Формат упаковки в QueueOfInt (num2):
    /// Биты 0-7:   Смещение X (со знаком, +128)
    /// Биты 8-15:  Смещение Y (со знаком, +128)
    /// Биты 16-23: Смещение Z (со знаком, +128)
    /// Биты 24-28: Уровень света (0-31)
    /// Биты 29-31: Индекс грани, откуда пришли + 1 (используется для предотвращения обратного хода)
    /// </summary>
    public void SpreadSunlightAt(
        QueueOfInt unhandledPositions, BlockPos centerPos,
        bool isDirectlyIlluminated, FastSetOfLongs touchedChunks)
    {
        int num = chunkSize;
        tmpPos.SetDimension(centerPos.dimension);

        while (unhandledPositions.Count > 0)
        {
            int num2 = unhandledPositions.Dequeue();
            // Извлекаем уровень света из битов 24-28
            int num3 = (num2 >> 24) & 0x1F;
            if (num3 == 0) continue;

            // Декодируем относительные координаты (сдвиг +128 для хранения знака в байте)
            int num4 = (num2 & 0xFF) - 128 + centerPos.X;
            int num5 = ((num2 >> 8) & 0xFF) - 128 + centerPos.Y;
            int num6 = ((num2 >> 16) & 0xFF) - 128 + centerPos.Z;

            IWorldChunk unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
                num4 / num, num5 / num + centerPos.dimension * 1024, num6 / num);
            if (unpackedChunkFast is null) continue;

            int index3d = (num5 % num * num + num6 % num) * num + num4 % num;
            unpackedChunkFast.Lighting.SetSunlight_Buffered(index3d, num3);

            tmpPos.Set(num4, num5, num6);
            GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpPos,
                out Block curBlock, out int baseAbsorption, out BlockEntityMicroBlock curMicroBE);

            // Грань, откуда мы пришли (пропускаем обратное распространение)
            int num7 = ((num2 >> 29) & 7) - 1;

            for (int i = 0; i < 6; i++)
            {
                if (i == num7) continue;

                Vec3i vec3i = BlockFacing.ALLNORMALI[i];
                int num8 = num4 + vec3i.X;
                int num9 = num5 + vec3i.Y;
                int num10 = num6 + vec3i.Z;

                if ((num8 | num9 | num10) < 0 ||
                    num8 >= mapsizex || num9 >= mapsizey || num10 >= mapsizez)
                    continue;

                unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
                    num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num);
                if (unpackedChunkFast is null) continue;

                touchedChunks.Add(chunkProvider.ChunkIndex3D(
                    num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num));

                index3d = (num9 % num * num + num10 % num) * num + num8 % num;
                BlockFacing dir = BlockFacing.ALLFACES[i];

                float effectiveAbs = GetEffectiveAbsorption(
                    curBlock, baseAbsorption, dir, num3, curMicroBE, tmpPos, true);

                // Нет потери дистанции для прямой вертикальной колонки (солнце сверху)
                int distLoss = ((!isDirectlyIlluminated ||
                    num8 != centerPos.X || num10 != centerPos.Z || i != 5) ? 1 : 0);

                int lightArrivingAtN = num3 - (int)effectiveAbs - distLoss;
                if (lightArrivingAtN <= 0) continue;

                tmpPos2.Set(num8, num9, num10);
                tmpPos2.dimension = centerPos.dimension;
                GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpPos2,
                    out Block nBlock, out int nBaseAbs, out BlockEntityMicroBlock nMicroBE);

                float nEffectiveAbs = GetEffectiveAbsorption(
                    nBlock, nBaseAbs, dir, lightArrivingAtN, nMicroBE, tmpPos2, true);

                int finalLight = lightArrivingAtN;
                if (nEffectiveAbs > lightArrivingAtN) finalLight = 0;

                if (unpackedChunkFast.Lighting.GetSunlight(index3d) < finalLight)
                {
                    unhandledPositions.EnqueueIfLarger(
                        num8 - centerPos.X, num9 - centerPos.Y, num10 - centerPos.Z,
                        finalLight + (TileSideEnum.GetOpposite(i) + 1 << 5));
                }
            }
        }
        tmpPos.SetDimension(0);
    }

    /// <summary>
    /// BFS "распространение тени": гасит солнечный свет при появлении препятствия.
    /// Сохраненный свет (ярче, чем волна гашения) собирается в retainedLightToSpread
    /// для повторного распространения.
    /// Формат упаковки в QueueOfInt аналогичен SpreadSunlightAt.
    /// </summary>
    public void ClearSunlightAt(
        QueueOfInt positionsToClear, BlockPos centerPos,
        bool isDirectlyIlluminated, QueueOfInt retainedLightToSpread,
        FastSetOfLongs touchedChunks)
    {
        int num = chunkSize;
        FastSetOfInts fastSetOfInts = new FastSetOfInts();
        tmpPos.SetDimension(centerPos.dimension);

        while (positionsToClear.Count > 0)
        {
            int num2 = positionsToClear.Dequeue();
            // Декодируем относительные координаты
            int num3 = (num2 & 0xFF) - 128 + centerPos.X;
            int num4 = ((num2 >> 8) & 0xFF) - 128 + centerPos.Y;
            int num5 = ((num2 >> 16) & 0xFF) - 128 + centerPos.Z;

            IWorldChunk unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
                num3 / num, num4 / num + centerPos.dimension * 1024, num5 / num);
            if (unpackedChunkFast is null) continue;

            int index3d = (num4 % num * num + num5 % num) * num + num3 % num;
            int sunlight = unpackedChunkFast.Lighting.GetSunlight(index3d);

            // Если блок всё ещё освещен — убираем его из очереди очистки и сохраняем для распространения
            if (sunlight != 0)
                fastSetOfInts.RemoveIfMatches(
                    num3 - centerPos.X, num4 - centerPos.Y, num5 - centerPos.Z, sunlight);

            unpackedChunkFast.Lighting.SetSunlight_Buffered(index3d, 0);

            tmpPos.Set(num3, num4, num5);
            GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpPos,
                out Block curBlock, out int baseAbsorption, out BlockEntityMicroBlock curMicroBE);

            // Грань, откуда пришла волна гашения (пропускаем обратное распространение)
            int num7 = ((num2 >> 29) & 7) - 1;

            for (int i = 0; i < 6; i++)
            {
                if (i == num7) continue;

                Vec3i vec3i = BlockFacing.ALLNORMALI[i];
                int num8 = num3 + vec3i.X;
                int num9 = num4 + vec3i.Y;
                int num10 = num5 + vec3i.Z;

                if ((num8 | num9 | num10) < 0 ||
                    num8 >= mapsizex || num9 >= mapsizey || num10 >= mapsizez)
                    continue;

                unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
                    num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num);
                if (unpackedChunkFast is null) continue;

                touchedChunks.Add(chunkProvider.ChunkIndex3D(
                    num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num));

                BlockFacing dir = BlockFacing.ALLFACES[i];
                float effectiveAbs = GetEffectiveAbsorption(
                    curBlock, baseAbsorption, dir, (num2 >> 24) & 0x1F, curMicroBE, tmpPos, true);

                // Обратная величина: для прямой вертикальной колонки с солнцем сверху нет потери дистанции
                int distLoss = 1 - ((isDirectlyIlluminated &&
                    num8 == centerPos.X && num10 == centerPos.Z && i == 5) ? 1 : 0);

                int num11 = ((num2 >> 24) & 0x1F) - (int)effectiveAbs - distLoss;
                if (num11 <= 0) continue;

                index3d = (num9 % num * num + num10 % num) * num + num8 % num;
                int sunlight2 = unpackedChunkFast.Lighting.GetSunlight(index3d);

                // Если сосед уже затемнен — добавляем в очередь для распространения волны гашения
                if (sunlight2 != 0)
                {
                    if (sunlight2 <= num11)
                        positionsToClear.EnqueueIfLarger(
                            num8 - centerPos.X, num9 - centerPos.Y, num10 - centerPos.Z,
                            num11 + (TileSideEnum.GetOpposite(i) + 1 << 5));
                    else
                        // Сохраняем более яркий свет для повторного распространения
                        fastSetOfInts.Add(
                            num8 - centerPos.X, num9 - centerPos.Y, num10 - centerPos.Z,
                            sunlight2);
                }
            }
        }

        foreach (int item in fastSetOfInts)
            retainedLightToSpread.Enqueue(item);

        tmpPos.SetDimension(0);
    }


    /// <summary>
    /// Унифицированный детектор дверей/люков. Использует два кэша по BlockId
    /// для быстрого отказа: GetBlockEntity/GetBehavior вызываются только для
    /// блоков, которые заведомо являются дверью/люком или их прокси-мультиблоком.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDoorBlock(Block block, BlockPos pos)
    {
        if (block is null || pos is null || readBlockAccess is null)
            return false;

        int id = block.BlockId;

        // 1. Быстрый путь: блок сам по себе является дверью/люком.
        // GetBehavior не нужен: для освещения важна геометрия (CollisionBoxes), 
        // которая определяется типом блока и всегда присутствует у дверей.
        if (isDoorCache[id]) return true;

        // 2. Прокси мультиблока: проверяем базовый блок через тот же кэш.
        if (!isMultiblockCache[id]) return false;

        BlockPos mainPos = pos.AddCopy(((BlockMultiblock)block).OffsetInv);
        Block mainBlock = readBlockAccess.GetBlock(mainPos);

        // Избегаем null-чек, если mainBlock уже был проверен выше, но для безопасности оставляем.
        return mainBlock is not null && isDoorCache[mainBlock.BlockId];
    }

    /// <summary>
    /// Очищает все статические кэши (направления сфер Фибоначчи, collision boxes).
    /// Вызывается при выгрузке мода/мира для освобождения памяти.
    /// </summary>
    public static void ClearCaches()
    {
        sphereCache.Clear();

        isDoorCache = null;
        isMultiblockCache = null;
        isMicroblockCache = null;
    }
}