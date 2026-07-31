using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;


namespace Lumos.Core;

// Stratum start:
// Improvements:
// - Zero-GC: Object pool and struct arrays eliminate micro-stutters from the garbage collector.
// - Generational Grid: The iteration counter replaces expensive array clearing (Array.Clear).
// - Ray Tracing: Direct light with single-bounce reflections (50% normal blocks, 100% glass/water)
// - Directional Absorption: Universal face-checking system correctly handles light propagation through slabs, stairs, and volumetric transparent blocks
// - Sunlight Invalidation: Rewrote UpdateSunLight to correctly extinguish trapped sunlight in sealed caves and rooms
// Limitations:
// Hard limit of 128³: Light is clipped at boundaries (same as vanilla)
// Color distortion: Top-4 limit for HSV mixing
// Not thread-safe: Shared arrays require separate instances per thread
public class LumosChunkIlluminator
{


    // Unified block-light dirty batching.
    // Individual light changes only enqueue dirty regions.
    // One FlushPendingBlockLightUpdates() recalculates the complete batch.
    private const int MAX_BLOCK_LIGHT_LEVEL = 31;
    private const int DIRTY_RADIUS_PADDING = 1;

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

    // One entry per changed source position within the current batch.
    private readonly Dictionary<long, DirtyLightSphere> pendingDirtySpheres =
        new Dictionary<long, DirtyLightSphere>(256);

    private readonly List<DirtyLightSphere> dirtySphereBuffer =
        new List<DirtyLightSphere>(256);

    // Exact union of all dirty spheres.
    private readonly HashSet<long> dirtyLightCells =
        new HashSet<long>();

    // Unique source-id mapping for the current trace batch.
    private readonly Dictionary<long, int> nearbySourceIndexByPosition =
        new Dictionary<long, int>(512);

    private bool isFlushingBlockLight;

    private ushort defaultSunLight;

    private const int DARKNESS_SPREAD_WIDTH = 63;
    private const int DARKNESS_SPREAD_HALF = 31;
    private const int DARKNESS_VISITED_SIZE = DARKNESS_SPREAD_WIDTH * DARKNESS_SPREAD_WIDTH * DARKNESS_SPREAD_WIDTH;
    private const int DARKNESS_VISITED_CENTER = 125023;

    private int mapsizex;
    private int mapsizey;
    private int mapsizez;

    private int XPlus = 1;
    private int YPlus;
    private int ZPlus;

    private IList<Block> blockTypes;

    private int chunkSize;
    private int chunkSizeLog2;
    private int chunkSizeMask;

    internal IChunkProvider chunkProvider;
    private IBlockAccessor readBlockAccess;

    private BlockPos tmpDiPos = new BlockPos(0);
    private BlockPos tmpPos = new BlockPos(0);
    private BlockPos tmpPos2 = new BlockPos(0);
    private BlockPos tmpPosDimensionAware = new BlockPos(0);

    private int[] currentVisited;
    private int iteration;

    private const int GRID_BITS = 7;
    private const int GRID_MASK = 127;
    private List<int> touchedCells = new List<int>(4096);

    #region Object Pools
    private Stack<QueueOfInt> queueOfIntPool = new Stack<QueueOfInt>();

    private QueueOfInt GetQueueOfInt()
    {
        if (queueOfIntPool.Count > 0)
        {
            var q = queueOfIntPool.Pop();
            while (q.Count > 0) q.Dequeue();
            return q;
        }
        return new QueueOfInt();
    }
    #endregion

    // Кэш поглощения света для каждого BlockId. 
    // Заполняется один раз при инициализации мира, чтобы убрать виртуальные вызовы в горячих циклах.
    private int[] absorptionCache;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GetBlockAndAbsorption(IWorldChunk chunk, int index3d, out Block block, out int baseAbsorption)
    {
        // Layer 0 = твердые блоки, Layer 1 = жидкости (вода, лава)
        int solidId = chunk.Data.GetBlockId(index3d, 0); // Аналог chunk.Data[index3d]
        int fluidId = chunk.Data.GetBlockId(index3d, 1);

        // Мгновенное чтение из L1-кэша процессора, без виртуальных вызовов
        baseAbsorption = absorptionCache[solidId];

        // Учитываем слой жидкостей, как это делает оригинальный метод
        if (fluidId != 0)
        {
            int fluidAbs = absorptionCache[fluidId];
            if (fluidAbs > baseAbsorption)
            {
                baseAbsorption = fluidAbs;
            }
        }

        // Возвращаем сам объект блока для дальнейших проверок (SideSolid, Replaceable и т.д.)
        block = blockTypes[solidId != 0 ? solidId : fluidId];
    }


    #region Dictionary-based light tracking (replaces 128³ grid)

    /// <summary>
    /// Хранит вклад каждого источника в данную ячейку.
    /// Динамический список — нет ограничения на 4 источника.
    /// </summary>
    private class LightSourcesAtBlock
    {
        public int[] srcIds;
        public byte[] levels;
        public int count;

        public LightSourcesAtBlock()
        {
            srcIds = new int[4];
            levels = new byte[4];
            count = 0;
        }

        public void Reset()
        {
            count = 0;
        }

        public void AddOrUpdate(int srcId, byte level)
        {
            for (int i = 0; i < count; i++)
            {
                if (srcIds[i] == srcId)
                {
                    if (level > levels[i]) levels[i] = level;
                    return;
                }
            }

            if (count >= srcIds.Length)
            {
                int newLen = srcIds.Length * 2;
                Array.Resize(ref srcIds, newLen);
                Array.Resize(ref levels, newLen);
            }

            srcIds[count] = srcId;
            levels[count] = level;
            count++;
        }
    }

    private void QueueDirtyLightSphere(
        int posX,
        int posY,
        int posZ,
        int lightRadius)
    {
        if (lightRadius <= 0)
            return;

        int radius =
            lightRadius + DIRTY_RADIUS_PADDING;

        long key =
            PackPos(posX, posY, posZ);

        if (pendingDirtySpheres.TryGetValue(
            key,
            out DirtyLightSphere existing))
        {
            if (radius > existing.Radius)
            {
                existing.Radius = radius;
                pendingDirtySpheres[key] = existing;
            }

            return;
        }

        pendingDirtySpheres.Add(
            key,
            new DirtyLightSphere(
                posX,
                posY,
                posZ,
                radius
            )
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UnpackPos(
        long key,
        out int x,
        out int y,
        out int z)
    {
        x = (int)(key & 0x1FFFFF);
        y = (int)((key >> 21) & 0x1FFFFF);
        z = (int)((key >> 42) & 0x1FFFFF);

        if ((x & 0x100000) != 0)
            x |= unchecked((int)0xFFE00000);

        if ((y & 0x100000) != 0)
            y |= unchecked((int)0xFFE00000);

        if ((z & 0x100000) != 0)
            z |= unchecked((int)0xFFE00000);
    }

    private Dictionary<long, LightSourcesAtBlock> visitedNodes = new Dictionary<long, LightSourcesAtBlock>(4096);
    private Stack<LightSourcesAtBlock> lsabPool = new Stack<LightSourcesAtBlock>(256);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackPos(int x, int y, int z)
    {
        // 21 бит на координату: диапазон [-1048576, 1048575]
        return ((long)(x & 0x1FFFFF)) | ((long)(y & 0x1FFFFF) << 21) | ((long)(z & 0x1FFFFF) << 42);
    }

    private LightSourcesAtBlock GetOrCreateLsab(long key)
    {
        if (visitedNodes.TryGetValue(key, out var lsab))
            return lsab;

        lsab = lsabPool.Count > 0 ? lsabPool.Pop() : new LightSourcesAtBlock();
        lsab.Reset();
        visitedNodes[key] = lsab;
        return lsab;
    }

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



    #region Generational Grid for Light Tracking




    private struct LightCell
    {
        public int generation;
        public int src0, src1, src2, src3;
        public byte lvl0, lvl1, lvl2, lvl3;
        public byte maxLevel;
        public byte trackedCount;
    }

    private LightCell[] lightGrid;

    private static readonly int[] dx = { 0, 1, 0, -1, 0, 0 };
    private static readonly int[] dy = { 0, 0, 0, 0, 1, -1 };
    private static readonly int[] dz = { -1, 0, 1, 0, 0, 0 };
    #endregion

    #region Struct-based nearby sources
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

    private struct FastBlockPos
    {
        public int X, Y, Z, Dim;
        public FastBlockPos(int x, int y, int z, int dim)
        {
            X = x; Y = y; Z = z; Dim = dim;
        }
    }

    #region Ray Tracing System
    private struct LightRay
    {
        public float OriginX, OriginY, OriginZ;
        public float DirX, DirY, DirZ;
        public float Energy;
        public byte H, S, B;
        public int BounceCount;
        public int SourceId;
    }

    private LightRay[] rayPool;
    private int rayPoolHead;
    private int rayPoolTail;
    private int activeRayCount;

    private const int MAX_RAY_POOL_SIZE = 200000;
    private const int REFLECTION_RAYS_COUNT = 128;

    private const float TARGET_GAP = 1.3f;             // желаемый зазор между лучами на поверхности сферы, в блоках
    private const int MIN_RAYS = 512;                  // нижний предел, чтобы слабые источники не были совсем "дырявыми"
    private const int MAX_RAYS = 40000;                // верхний предел, страховка от чрезмерной нагрузки
    private const int RADIUS_BUCKET_STEP = 2;          // шаг квантования радиуса для кэша (округление вверх)

    // Кэш: bucketedRadius -> (массив направлений, фактическое количество точек)
    private static ConcurrentDictionary<int, (float[][] dirs, int count)> sphereCache =
        new ConcurrentDictionary<int, (float[][], int)>();

    private float[][] fibonacciSphere;

    private void InitRayTracing()
    {
        rayPool = new LightRay[MAX_RAY_POOL_SIZE];
        rayPoolHead = 0;
        rayPoolTail = 0;
        activeRayCount = 0;

        // Сферы теперь строятся лениво под конкретный радиус источника через GetOrBuildSphereForRadius.
        // При желании можно "прогреть" кэш заранее для типичных значений яркости :
        for (int r = 7; r <= 23; r++)
            GetOrBuildSphereForRadius(r);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalcRayCountForRadius(int radius)
    {
        if (radius <= 0) return MIN_RAYS;
        // N = 24 * R² / gap²,  выведено из gap(R) = 2R * sqrt(6/N)
        float n = 24f * radius * radius / (TARGET_GAP * TARGET_GAP);
        int result = (int)Math.Ceiling(n);
        return Math.Clamp(result, MIN_RAYS, MAX_RAYS);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketRadius(int radius)
    {
        // Квантуем радиус, чтобы близкие по яркости источники переиспользовали одну и ту же сферу
        return ((radius + RADIUS_BUCKET_STEP - 1) / RADIUS_BUCKET_STEP) * RADIUS_BUCKET_STEP;
    }

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

    private static float[][] GenerateFibonacciSphere(int count, out int actualCount)
    {
        var points = new float[count][];
        float goldenAngle = (float)(Math.PI * (3.0 - Math.Sqrt(5.0)));
        for (int i = 0; i < count; i++)
        {
            // y идёт от +1 до -1 равномерно
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

    private void ResetRayPool()
    {
        rayPoolHead = 0;
        rayPoolTail = 0;
        activeRayCount = 0;
    }

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

    private LightRay DequeueRay()
    {
        var ray = rayPool[rayPoolTail];
        rayPoolTail = (rayPoolTail + 1) % MAX_RAY_POOL_SIZE;
        activeRayCount--;
        return ray;
    }

    #region Precomputed reflection directions

    // Таблицы cos(theta_i)/sin(theta_i) по золотому углу — не зависят от того,
    // сколько лучей реально используется в конкретном вызове (N ≤ REFLECTION_RAYS_COUNT).
    // Это убирает Math.Cos/Math.Sin из горячего пути SpawnReflectionRays.
    private static readonly float[] reflCosTheta = new float[REFLECTION_RAYS_COUNT];
    private static readonly float[] reflSinTheta = new float[REFLECTION_RAYS_COUNT];

    private const int MIN_REFLECTION_RAYS = 16;

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

    // Адаптивное число лучей отражения в зависимости от энергии.
    // Слабые отражения (дальний тусклый источник) вносят едва заметный вклад —
    // экономим на плотности выборки. Сильные — оставляем плотную выборку
    // для гладкого блика.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalcReflectionRayCount(float reflectedEnergy)
    {
        float t = reflectedEnergy / MAX_BLOCK_LIGHT_LEVEL;
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;

        int n = MIN_REFLECTION_RAYS +
            (int)((REFLECTION_RAYS_COUNT - MIN_REFLECTION_RAYS) * t);

        return n;
    }

    #endregion

    private void SpawnReflectionRays(float x, float y, float z,
        float normalX, float normalY, float normalZ,
        float energy, byte h, byte s, byte b, int sourceId)
    {
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
        float rayEnergy = energy;

        for (int i = 0; i < rayCount; i++)
        {
            // cosAngle/sinAngle — дешёвые, зависят от N (rayCount), поэтому
            // пересчитываются каждый раз, но это одно деление + один Sqrt,
            // а не полноценный Cos/Sin.
            float cosAngle = 1.0f - ((i + 0.5f) / rayCount);
            float sinAngle = (float)Math.Sqrt(Math.Max(0f, 1.0f - cosAngle * cosAngle));

            // theta берём из статической таблицы — без тригонометрии.
            float cosTheta = reflCosTheta[i];
            float sinTheta = reflSinTheta[i];

            float localX = sinAngle * cosTheta;
            float localY = cosAngle;
            float localZ = sinAngle * sinTheta;

            float worldDirX = tx * localX + normalX * localY + bx * localZ;
            float worldDirY = ty * localX + normalY * localY + by * localZ;
            float worldDirZ = tz * localX + normalZ * localY + bz * localZ;

            float dirLen = (float)Math.Sqrt(worldDirX * worldDirX + worldDirY * worldDirY + worldDirZ * worldDirZ);
            if (dirLen > 0)
            {
                worldDirX /= dirLen;
                worldDirY /= dirLen;
                worldDirZ /= dirLen;
            }

            float weightedEnergy = rayEnergy * cosAngle; // закон Ламберта

            if (weightedEnergy > 0.01f)
            {
                SpawnRay(x, y, z, worldDirX, worldDirY, worldDirZ,
                    weightedEnergy, h, s, b, 1, sourceId);
            }
        }
    }

    private static int GetReflectivity(Block block)
    {
        // Стекло: прозрачное, отражение слабое, луч идёт дальше.
        // 10–15 % даёт видимый блик, не перетягивая внимание.
        if (block.Code.Path.ToString().Contains("glass"))
            return 12;

        // Вода / жидкости: чуть выше стекла за счёт ряби и объёма.
        if (block.IsLiquid())
            return 18;

        // Диффузное отражение (камень, дерево, земля, листва).
        // 50 % — баланс: отражение заметно, но не конкурирует
        // с прямым светом. При MAX-семантике пик отражённого луча
        // на расстоянии 3 блока от стены ≈ E_surface × 0.5 − 3.
        // Для факела B=20, стена на 5 блоках: E_surface=15,
        // пик отражения на 3 блока дальше = 15×0.5−3 = 4.5
        // (уровень света ~4 из 20 — мягкая подсветка тени).
        return 50;
    }

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

        float prevDistance = 0f;

        // ── КЕШ ЧАНКА ──
        // DDA-шаг двигается на 1 блок, чанк обычно chunkSize³ (32³), поэтому
        // подавляющее большинство соседних шагов луча остаются в том же чанке.
        // Дёргаем GetUnpackedChunkFast (с локом внутри) только когда реально
        // перешли в другой чанк.
        int lastChunkX = int.MinValue;
        int lastChunkY = int.MinValue;
        int lastChunkZ = int.MinValue;
        IWorldChunk cachedChunk = null;

        while (energy > 0.01f)
        {
            float tNext = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));

            if (float.IsInfinity(tNext) || float.IsNaN(tNext))
                break;

            const float TIE_EPS = 1e-5f;
            bool crossX = (tMaxX - tNext) <= TIE_EPS;
            bool crossY = (tMaxY - tNext) <= TIE_EPS;
            bool crossZ = (tMaxZ - tNext) <= TIE_EPS;

            float faceNormalX = 0, faceNormalY = 0, faceNormalZ = 0;
            if (crossX) { x += stepX; tMaxX += tDeltaX; faceNormalX = -stepX; }
            if (crossY) { y += stepY; tMaxY += tDeltaY; faceNormalY = -stepY; }
            if (crossZ) { z += stepZ; tMaxZ += tDeltaZ; faceNormalZ = -stepZ; }

            BlockFacing hitFace;
            if (crossX)
                hitFace = stepX > 0 ? BlockFacing.WEST : BlockFacing.EAST;
            else if (crossY)
                hitFace = stepY > 0 ? BlockFacing.DOWN : BlockFacing.UP;
            else
                hitFace = stepZ > 0 ? BlockFacing.SOUTH : BlockFacing.NORTH;

            float nextDistance = tNext;
            float stepDistance = nextDistance - prevDistance;
            prevDistance = nextDistance;
            if (stepDistance < 0f) break;

            // ── Чанк: берём из кеша, если координата чанка не изменилась ──
            int cx = x / chunkSize;
            int cy = y / chunkSize;
            int cz = z / chunkSize;

            if (cx != lastChunkX || cy != lastChunkY || cz != lastChunkZ)
            {
                cachedChunk = chunkProvider.GetUnpackedChunkFast(
                    cx, cy, cz, notRecentlyAccessed: true);

                lastChunkX = cx;
                lastChunkY = cy;
                lastChunkZ = cz;

                if (cachedChunk == null)
                    break;
            }

            IWorldChunk chunk = cachedChunk;

            int index3d = (y % chunkSize * chunkSize + z % chunkSize) * chunkSize + x % chunkSize;
            GetBlockAndAbsorption(chunk, index3d, out Block block, out int baseAbsorption);
            tmpPos.Set(x, y, z);

            energy -= stepDistance;
            float energyAtSurface = energy;

            float effectiveAbs = GetEffectiveAbsorption(block, baseAbsorption, hitFace, energy, tmpPos);
            if (effectiveAbs > 0f)
                energy -= effectiveAbs;

            bool isOpaque = effectiveAbs > 0 && (
                block.SideSolid[hitFace.Index] ||
                block.SideSolid[hitFace.Opposite.Index] ||
                block.IsLiquid() ||
                block.Replaceable >= 6000
            );

            int solidMask = 0;
            if (block.SideSolid[0]) solidMask |= 1;
            if (block.SideSolid[1]) solidMask |= 2;
            if (block.SideSolid[2]) solidMask |= 4;
            if (block.SideSolid[3]) solidMask |= 8;
            if (block.SideSolid[4]) solidMask |= 16;
            if (block.SideSolid[5]) solidMask |= 32;

            if (energy > 0 && (!isOpaque || solidMask == 63))
                ApplyLightToBlock(x, y, z, energy, ray.SourceId);

            if (ray.BounceCount == 0 && effectiveAbs > 0)
            {
                int reflectivity = GetReflectivity(block);
                if (reflectivity > 0)
                {
                    float reflectedEnergy = energyAtSurface * reflectivity / 100f;
                    if (reflectedEnergy > 0.01f)
                    {
                        float hitX = posX + dirX * nextDistance;
                        float hitY = posY + dirY * nextDistance;
                        float hitZ = posZ + dirZ * nextDistance;

                        SpawnReflectionRays(
                            hitX + faceNormalX * 0.01f,
                            hitY + faceNormalY * 0.01f,
                            hitZ + faceNormalZ * 0.01f,
                            faceNormalX, faceNormalY, faceNormalZ,
                            reflectedEnergy, ray.H, ray.S, ray.B, ray.SourceId);
                    }
                }
            }

            if (isOpaque)
                break;
            if (energy <= 0) break;
        }
    }

    private void ApplyLightToBlock(int x, int y, int z, float energy, int sourceId)
    {
        int lightLevel = (int)energy;
        if (lightLevel <= 0) return;

        long key = PackPos(x, y, z);
        var lsab = GetOrCreateLsab(key);
        lsab.AddOrUpdate(sourceId, (byte)lightLevel);
    }

    private int baseX, baseY, baseZ;  // Добавляем поля класса для хранения базы

    private void TraceNearbyBlockLights()
    {
        // Keep all source contributions in visitedNodes for this batch.
        RecycleVisitedNodes();

        for (int srcIdx = 0; srcIdx < nearbyCount; srcIdx++)
        {
            byte h = nearbyH[srcIdx];
            byte s = nearbyS[srcIdx];
            byte brightness = nearbyB[srcIdx];

            if (brightness <= 0)
                continue;

            NearbyLightSourceStruct source =
                nearbyLightSourcesArray[srcIdx];

            // Reuse the ray pool for one source at a time.
            // visitedNodes remains shared across all sources.
            ResetRayPool();

            ApplyLightToBlock(
                source.posX,
                source.posY,
                source.posZ,
                brightness,
                srcIdx
            );

            float sourceX = source.posX + 0.5f;
            float sourceY = source.posY + 0.5f;
            float sourceZ = source.posZ + 0.5f;

            var sphere = GetOrBuildSphereForRadius(brightness);
            float[][] dirs = sphere.dirs;
            int rayCount = sphere.count;

            for (int rayIndex = 0; rayIndex < rayCount; rayIndex++)
            {
                float[] direction = dirs[rayIndex];

                SpawnRay(
                    sourceX,
                    sourceY,
                    sourceZ,
                    direction[0],
                    direction[1],
                    direction[2],
                    brightness,
                    h,
                    s,
                    brightness,
                    0,
                    srcIdx
                );
            }

            // Finish this source completely, including reflection rays,
            // before starting the next source.
            while (activeRayCount > 0)
            {
                LightRay ray = DequeueRay();
                ProcessRay(ray);
            }
        }
    }
    #endregion

    public LumosChunkIlluminator()
    {
        // Пустой конструктор — инициализация происходит через InitFromVanillaConstructor
    }

    /// <summary>
    /// Вызывается из postfix-патча конструктора ChunkIlluminator.
    /// Копирует параметры оригинального конструктора.
    /// </summary>
    public void InitFromVanillaConstructor(IChunkProvider chunkProvider, IBlockAccessor readBlockAccess, int chunkSize)
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

        currentVisited = new int[DARKNESS_VISITED_SIZE];
        // lightGrid больше не нужен — используем visitedNodes (Dictionary)

        int maxSources = 27 * YPlus * chunkSize;
        nearbyLightSourcesArray = new NearbyLightSourceStruct[maxSources];
        nearbyH = new byte[maxSources];
        nearbyS = new byte[maxSources];
        nearbyB = new byte[maxSources];

        InitRayTracing();
    }

    /// <summary>
    /// Вызывается из postfix-патча InitForWorld.
    /// </summary>
    public void InitForWorld(IList<Block> blockTypes, ushort defaultSunLight, int mapsizex, int mapsizey, int mapsizez)
    {
        this.blockTypes = blockTypes;
        this.defaultSunLight = defaultSunLight;
        this.mapsizex = mapsizex;
        this.mapsizey = mapsizey;
        this.mapsizez = mapsizez;

        // Инициализация кэша поглощения
        absorptionCache = new int[blockTypes.Count];
        for (int i = 0; i < blockTypes.Count; i++)
        {
            absorptionCache[i] = blockTypes[i].LightAbsorption;
        }
    }

    public FastSetOfLongs PlaceBlockLight(
        byte[] lightHsv,
        int posX,
        int posY,
        int posZ)
    {
        FastSetOfLongs result = new FastSetOfLongs();

        if (blockTypes == null ||
            lightHsv == null ||
            lightHsv.Length < 3 ||
            lightHsv[2] <= 0)
        {
            return result;
        }

        IWorldChunk chunk = GetChunkAtPos(posX, posY, posZ);
        if (chunk == null)
            return result;

        int lightPosition = InChunkIndex(posX, posY, posZ);

        if (!chunk.LightPositions.Contains(lightPosition))
        {
            chunk.LightPositions.Add(lightPosition);
        }

        // Do not recalculate immediately. The ServerSystemRelight postfix
        // flushes the entire lighting-task batch once.
        QueueDirtyLightSphere(posX, posY, posZ, lightHsv[2]);

        return result;
    }

    public FastSetOfLongs RemoveBlockLight(
        byte[] oldLightHsv,
        int posX,
        int posY,
        int posZ)
    {
        FastSetOfLongs result = new FastSetOfLongs();

        if (blockTypes == null ||
            oldLightHsv == null ||
            oldLightHsv.Length < 3)
        {
            return result;
        }

        IWorldChunk chunk = GetChunkAtPos(posX, posY, posZ);
        if (chunk == null)
            return result;

        int lightPosition = InChunkIndex(posX, posY, posZ);
        chunk.LightPositions.Remove(lightPosition);

        int oldRadius = oldLightHsv[2];

        // Preserve the vanilla special case.
        if (oldRadius == 18)
            oldRadius = 20;

        // Old block-light is cleared by the unified dirty-region pass.
        QueueDirtyLightSphere(posX, posY, posZ, oldRadius);

        return result;
    }

    


    public FastSetOfLongs UpdateBlockLight(
        int oldLightAbsorb,
        int newLightAbsorb,
        int posX,
        int posY,
        int posZ)
    {
        FastSetOfLongs result = new FastSetOfLongs();

        if (blockTypes == null)
            return result;

        if (oldLightAbsorb == newLightAbsorb)
            return result;

        // A transparency change can both remove existing light and reveal
        // a source that was previously blocked. Use the maximum light radius.
        QueueDirtyLightSphere(
            posX,
            posY,
            posZ,
            MAX_BLOCK_LIGHT_LEVEL
        );

        return result;
    }



    public void UpdateLightAt(
        int range,
        int posX,
        int posY,
        int posZ,
        FastSetOfLongs touchedChunks)
    {
        if (range <= 0)
            return;

        QueueDirtyLightSphere(
            posX,
            posY,
            posZ,
            range
        );

        FastSetOfLongs flushedChunks =
            FlushPendingBlockLightUpdates();

        foreach (long chunkIndex in flushedChunks)
        {
            touchedChunks.Add(chunkIndex);
        }
    }




    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetEffectiveAbsorption(
        Block block,
        int baseAbsorption,
        BlockFacing dir,
        float incomingEnergy,
        BlockPos pos = null)
    {
        // ── Ступень 0: прозрачные ──
        if (baseAbsorption <= 0) return 0f;

        // ── Ступень 1: маска солидности ──
        int solidMask = 0;
        if (block.SideSolid[0]) solidMask |= 1;
        if (block.SideSolid[1]) solidMask |= 2;
        if (block.SideSolid[2]) solidMask |= 4;
        if (block.SideSolid[3]) solidMask |= 8;
        if (block.SideSolid[4]) solidMask |= 16;
        if (block.SideSolid[5]) solidMask |= 32;

        // ── Ступень 2: полный блок ──
        if (solidMask == 63)
            return baseAbsorption;

        // ── Ступень 3: объёмный поглотитель (листва, вода) ──
        if (solidMask == 0)
            return baseAbsorption;

        // ── Ступень 4: частичная солидность (1–5 граней) ──
        BlockFacing incoming = dir.Opposite;
        BlockFacing outgoing = dir;

        bool incomingSolid = (pos != null && readBlockAccess != null)
            ? block.SideIsSolid(readBlockAccess, pos, incoming.Index)
            : block.SideSolid[incoming.Index];
        if (incomingSolid) return baseAbsorption;

        bool outgoingSolid = (pos != null && readBlockAccess != null)
            ? block.SideIsSolid(readBlockAccess, pos, outgoing.Index)
            : block.SideSolid[outgoing.Index];
        if (outgoingSolid) return baseAbsorption;

        // ── Ступень 5: ребро / проём ──
        // Свет проходит через открытую часть, но задевает часть материала.
        // Гарантированно проходит ≥ 50 %, остальное — пропорционально baseAbs.
        float clampedAbs = Math.Min(baseAbsorption, 32);
        return incomingEnergy * clampedAbs / 64f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IWorldChunk GetChunkAtPos(int posX, int posY, int posZ)
    {
        return chunkProvider.GetUnpackedChunkFast(posX / chunkSize, posY / chunkSize, posZ / chunkSize, notRecentlyAccessed: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int InChunkIndex(int posX, int posY, int posZ)
    {
        return (posY % chunkSize * chunkSize + posZ % chunkSize) * chunkSize + posX % chunkSize;
    }


    private void LoadSourcesIntersectingDirtySpheres(
        List<DirtyLightSphere> spheres)
    {
        nearbyCount = 0;
        nearbySourceIndexByPosition.Clear();

        if (spheres.Count == 0 ||
            blockTypes == null ||
            chunkProvider == null)
        {
            return;
        }

        // Build one conservative search AABB around all dirty spheres.
        // This is particularly effective for the arena case, where dirty
        // regions overlap heavily.
        int minX = mapsizex - 1;
        int minY = mapsizey - 1;
        int minZ = mapsizez - 1;
        int maxX = 0;
        int maxY = 0;
        int maxZ = 0;

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
        minY = Math.Max(0, minY);
        minZ = Math.Max(0, minZ);
        maxX = Math.Min(mapsizex - 1, maxX);
        maxY = Math.Min(mapsizey - 1, maxY);
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
                    IWorldChunk chunk =
                        chunkProvider.GetChunk(chunkX, chunkY, chunkZ);

                    if (chunk == null)
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

                        long sourceKey = PackPos(sourceX, sourceY, sourceZ);

                        if (nearbySourceIndexByPosition.ContainsKey(sourceKey))
                            continue;

                        Block block = blockTypes[chunk.Data[lightPosition]];

                        byte[] hsv = block.GetLightHsv(
                            readBlockAccess,
                            tmpPos.Set(sourceX, sourceY, sourceZ)
                        );

                        if (hsv == null || hsv.Length < 3 || hsv[2] <= 0)
                            continue;

                        int sourceRadius = hsv[2];
                        bool intersectsDirtyRegion = false;

                        for (int sphereIndex = 0;
                             sphereIndex < spheres.Count;
                             sphereIndex++)
                        {
                            DirtyLightSphere sphere = spheres[sphereIndex];

                            long dx = (long)sourceX - sphere.X;
                            long dy = (long)sourceY - sphere.Y;
                            long dz = (long)sourceZ - sphere.Z;
                            long allowedDistance = sourceRadius + sphere.Radius;

                            if (dx * dx + dy * dy + dz * dz <=
                                allowedDistance * allowedDistance)
                            {
                                intersectsDirtyRegion = true;
                                break;
                            }
                        }

                        if (!intersectsDirtyRegion)
                            continue;

                        TryAddNearbyLightSource(
                            sourceX,
                            sourceY,
                            sourceZ,
                            hsv[0],
                            hsv[1],
                            hsv[2]
                        );
                    }
                }
            }
        }
    }

    private bool TryAddNearbyLightSource(
        int posX,
        int posY,
        int posZ,
        byte hue,
        byte saturation,
        byte brightness)
    {
        if (brightness == 0)
            return false;

        long positionKey = PackPos(posX, posY, posZ);

        if (nearbySourceIndexByPosition.ContainsKey(positionKey))
            return false;

        if (nearbyCount >= nearbyLightSourcesArray.Length)
            return false;

        int sourceIndex = nearbyCount++;

        nearbyLightSourcesArray[sourceIndex] =
            new NearbyLightSourceStruct
            {
                posX = posX,
                posY = posY,
                posZ = posZ
            };

        nearbyH[sourceIndex] = hue;
        nearbyS[sourceIndex] = saturation;
        nearbyB[sourceIndex] = brightness;

        nearbySourceIndexByPosition.Add(
            positionKey,
            sourceIndex
        );

        return true;
    }

    /// <summary>
    /// Calculates the packed block-light value from all source contributions
    /// accumulated in the staging dictionary for one block.
    ///
    /// This method does not touch chunk.Lighting. It is intentionally pure with
    /// respect to the current light state, so the expensive ray-tracing phase
    /// can finish while the world still displays the previous valid lighting.
    /// </summary>
    private int CalculatePackedLight(
        LightSourcesAtBlock lsab)
    {
        int lightCount = lsab.count;

        if (lightCount <= 0)
            return 0;

        float totalWeight = 0f;
        int maxLevel = 0;

        for (int i = 0; i < lightCount; i++)
        {
            int level = lsab.levels[i];

            if (level > maxLevel)
                maxLevel = level;

            totalWeight += level;
        }

        if (maxLevel <= 0 || totalWeight <= 0f)
            return 0;

        float r = 0.5f;
        float g = 0.5f;
        float b = 0.5f;

        for (int i = 0; i < lightCount; i++)
        {
            int sourceIndex = lsab.srcIds[i];
            int level = lsab.levels[i];

            if ((uint)sourceIndex >= (uint)nearbyCount)
                continue;

            byte hue = nearbyH[sourceIndex];
            byte saturation = nearbyS[sourceIndex];

            int rgb = ColorUtil.HsvToRgb(
                hue * 4,
                saturation * 32,
                level * 8
            );

            float weight = (float)level / totalWeight;

            r += (rgb >> 16) * weight;
            g += ((rgb >> 8) & 0xFF) * weight;
            b += (rgb & 0xFF) * weight;
        }

        int mixedHsv = ColorUtil.Rgb2Hsv(r, g, b);

        int mixedHue = Math.Min(
            (int)((mixedHsv & 0xFF) / 4f + 0.5f),
            ColorUtil.HueQuantities - 1
        );

        int mixedSaturation = Math.Min(
            (int)(((mixedHsv >> 8) & 0xFF) / 32f + 0.5f),
            ColorUtil.SatQuantities - 1
        );

        return
            (maxLevel << 5) |
            (mixedHue << 10) |
            (mixedSaturation << 16);
    }

    /// <summary>
    /// Commits the fully calculated staging result into chunk.Lighting.
    ///
    /// IMPORTANT:
    /// This is the only method in the normal batched block-light path that
    /// writes the new BlockLight values into the live world state.
    ///
    /// Until this method runs, the old valid lighting remains visible and
    /// untouched while ray tracing is still in progress.
    ///
    /// Blocks that are part of the dirty region but absent from visitedNodes
    /// receive zero light. This is what correctly handles source removal.
    /// </summary>
    private void CommitDirtyLightCells(
        FastSetOfLongs touchedChunks,
        Dictionary<long, IWorldChunk> modifiedChunks)
    {
        int num = chunkSize;

        foreach (long key in dirtyLightCells)
        {
            UnpackPos(
                key,
                out int x,
                out int y,
                out int z
            );

            IWorldChunk chunk =
                chunkProvider.GetUnpackedChunkFast(
                    x / num,
                    y / num,
                    z / num,
                    notRecentlyAccessed: true
                );

            if (chunk == null)
                continue;

            int index3d =
                (y % num * num + z % num) * num +
                x % num;

            // No contribution in the new staging result means the light
            // must disappear from this dirty cell.
            int newLight = 0;

            if (visitedNodes.TryGetValue(
                key,
                out LightSourcesAtBlock lsab))
            {
                newLight = CalculatePackedLight(lsab);
            }

            int oldLight =
                chunk.Lighting.GetBlocklight(index3d);

            // Avoid unnecessary Lighting writes and chunk invalidation.
            if (oldLight == newLight)
                continue;

            chunk.Lighting.SetBlocklight(
                index3d,
                newLight
            );

            long chunkKey =
                chunkProvider.ChunkIndex3D(
                    x / num,
                    y / num,
                    z / num
                );

            touchedChunks.Add(
                chunkKey
            );

            modifiedChunks.TryAdd(
                chunkKey,
                chunk
            );
        }
    }

    private void BuildDirtyLightCellSet(
        List<DirtyLightSphere> spheres)
    {
        dirtyLightCells.Clear();

        for (int sphereIndex = 0;
             sphereIndex < spheres.Count;
             sphereIndex++)
        {
            DirtyLightSphere sphere = spheres[sphereIndex];

            int radius = sphere.Radius;
            int radiusSquared = radius * radius;

            int minX = Math.Max(0, sphere.X - radius);
            int maxX = Math.Min(mapsizex - 1, sphere.X + radius);
            int minY = Math.Max(0, sphere.Y - radius);
            int maxY = Math.Min(mapsizey - 1, sphere.Y + radius);

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

                    int zRadius = (int)Math.Sqrt(
                        radiusSquared - xySquared);

                    int minZ = Math.Max(0, sphere.Z - zRadius);
                    int maxZ = Math.Min(mapsizez - 1, sphere.Z + zRadius);

                    for (int z = minZ; z <= maxZ; z++)
                    {
                        dirtyLightCells.Add(
                            PackPos(x, y, z)
                        );
                    }
                }
            }
        }
    }

    /// <summary>
    /// Calculates and commits one complete block-light batch.
    ///
    /// Pipeline:
    ///
    /// 1. Detach pending dirty spheres.
    /// 2. Build the exact dirty-cell union.
    /// 3. Find every source that can affect that region.
    /// 4. Trace all relevant sources into visitedNodes (staging).
    /// 5. Commit the new result to chunk.Lighting in one short pass.
    ///
    /// The live Lighting arrays are NOT cleared before tracing. Therefore the
    /// player continues to see the previous valid lighting while the expensive
    /// ray-tracing stage is running.
    /// </summary>
    public FastSetOfLongs FlushPendingBlockLightUpdates()
    {
        FastSetOfLongs touchedChunks =
            new FastSetOfLongs();

        if (isFlushingBlockLight ||
            pendingDirtySpheres.Count == 0)
        {
            return touchedChunks;
        }

        isFlushingBlockLight = true;

        Dictionary<long, IWorldChunk> modifiedChunks =
            new Dictionary<long, IWorldChunk>(128);

        try
        {
            dirtySphereBuffer.Clear();

            foreach (
                DirtyLightSphere sphere
                in pendingDirtySpheres.Values)
            {
                dirtySphereBuffer.Add(sphere);
            }

            // Detach the current batch before calculating it.
            // Changes arriving during this calculation remain queued for the
            // next flush instead of being lost or recursively merged here.
            pendingDirtySpheres.Clear();

            // -------------------------------------------------------------
            // 1. Build the exact union of all dirty spheres.
            //    No live Lighting data is changed here.
            // -------------------------------------------------------------
            BuildDirtyLightCellSet(
                dirtySphereBuffer
            );

            if (dirtyLightCells.Count == 0)
                return touchedChunks;

            // -------------------------------------------------------------
            // 2. Find all active sources whose light spheres can affect
            //    at least one dirty cell.
            // -------------------------------------------------------------
            LoadSourcesIntersectingDirtySpheres(
                dirtySphereBuffer
            );

            // -------------------------------------------------------------
            // 3. Expensive stage: calculate the NEW lighting into the
            //    staging dictionary visitedNodes.
            //
            //    IMPORTANT: chunk.Lighting is still untouched here.
            // -------------------------------------------------------------
            TraceNearbyBlockLights();

            // -------------------------------------------------------------
            // 4. Fast stage: atomically-ish commit the complete result.
            //    Missing visitedNodes entries become zero light.
            // -------------------------------------------------------------
            CommitDirtyLightCells(
                touchedChunks,
                modifiedChunks
            );

            // Mark each modified chunk once, after all of its changed cells
            // have been committed.
            foreach (
                IWorldChunk chunk
                in modifiedChunks.Values)
            {
                chunk.MarkModified();
            }

            return touchedChunks;
        }
        finally
        {
            dirtyLightCells.Clear();
            dirtySphereBuffer.Clear();
            isFlushingBlockLight = false;
        }
    }

    /// <summary> Full recalculation of light in a given cubic region </summary>
    public void FullRelight(BlockPos minPos, BlockPos maxPos)
    {
        int num = chunkSize;
        Dictionary<Vec3i, IWorldChunk> dictionary = new Dictionary<Vec3i, IWorldChunk>();

        // 1. Expand the region boundaries by 1 chunk in all directions so that light can correctly flow across boundaries
        int num2 = GameMath.Clamp(Math.Min(minPos.X, maxPos.X) - num, 0, mapsizex - 1);
        int num3 = GameMath.Clamp(Math.Min(minPos.Y, maxPos.Y) - num, 0, mapsizey - 1);
        int num4 = GameMath.Clamp(Math.Min(minPos.Z, maxPos.Z) - num, 0, mapsizez - 1);
        int num5 = GameMath.Clamp(Math.Max(minPos.X, maxPos.X) + num, 0, mapsizex - 1);
        int num6 = GameMath.Clamp(Math.Max(minPos.Y, maxPos.Y) + num, 0, mapsizey - 1);
        int num7 = GameMath.Clamp(Math.Max(minPos.Z, maxPos.Z) + num, 0, mapsizez - 1);

        // Convert world coordinates to chunk coordinates
        int num8 = num2 / num;
        int num9 = num3 / num;
        int num10 = num4 / num;
        int num11 = num5 / num;
        int num12 = num6 / num;
        int num13 = num7 / num;

        int num14 = minPos.dimension * 1024; // Offset for dimensions

        IWorldChunk chunk;
        // 2. Load and unpack all necessary chunks
        for (int i = num8; i <= num11; i++)
        {
            for (int j = num9; j <= num12; j++)
            {
                for (int k = num10; k <= num13; k++)
                {
                    chunk = chunkProvider.GetChunk(i, j + num14, k);
                    if (chunk != null)
                    {
                        chunk.Unpack();
                        dictionary[new Vec3i(i, j, k)] = chunk;
                    }
                }
            }
        }

        // 3. Completely clear the old light in all affected chunks
        foreach (IWorldChunk value2 in dictionary.Values)
        {
            value2?.Lighting.ClearLight();
        }

        IWorldChunk[] array = new IWorldChunk[mapsizey / num];
        IWorldChunk chunk2;

        // 4. Calculate sunlight top-down for each column of chunks
        for (int l = num8; l <= num11; l++)
        {
            for (int m = num10; m <= num13; m++)
            {
                bool flag = false;
                for (int n = 0; n < array.Length; n++)
                {
                    chunk2 = chunkProvider.GetChunk(l, n + num14, m);
                    if (chunk2 == null) flag = true;
                    array[n] = chunk2;
                }

                if (!flag) // If the chunk column is fully loaded
                {
                    Sunlight(array, l, array.Length - 1, m, minPos.dimension);           // Direct light from above
                    SunlightFlood(array, l, array.Length - 1, m);                         // Scattering inside the chunk
                    SunLightFloodNeighbourChunks(array, l, array.Length - 1, m, minPos.dimension); // Flowing into neighboring chunks
                }
            }
        }

        // 5. Recalculate the complete region that was cleared above.
        // Build a sphere containing the cleared bounding box.
        // Flush will discover every source whose light can reach this region,
        // including sources outside the region itself.
        int centerX =
            (num2 + num5) / 2;

        int centerY =
            (num3 + num6) / 2;

        int centerZ =
            (num4 + num7) / 2;

        double halfX =
            (num5 - num2) * 0.5;

        double halfY =
            (num6 - num3) * 0.5;

        double halfZ =
            (num7 - num4) * 0.5;

        int fullRelightRadius =
            (int)Math.Ceiling(
                Math.Sqrt(
                    halfX * halfX +
                    halfY * halfY +
                    halfZ * halfZ
                )
            );

        FastSetOfLongs touchedChunks =
            new FastSetOfLongs();

        UpdateLightAt(
            fullRelightRadius,
            centerX,
            centerY,
            centerZ,
            touchedChunks
        );

        foreach (IWorldChunk value in dictionary.Values)
        {
            value?.MarkModified();
        }
    }

    /// <summary> Calculation of direct sunlight falling from top to bottom, taking into account absorption by blocks. </summary>
    public void Sunlight(IWorldChunk[] chunks, int chunkX, int chunkY, int chunkZ, int dim)
    {
        tmpPosDimensionAware.SetDimension(dim);
        int num = chunkSize;

        if (chunkY != chunks.Length - 1)
            chunks[chunkY + 1].Unpack();
        for (int num2 = chunkY; num2 >= 0; num2--)
            chunks[num2].Unpack();

        int num3 = chunkX * num;
        int num4 = chunkZ * num;

        IWorldChunk worldChunk;
        IChunkLight lighting;

        for (int i = 0; i < num; i++)
        {
            for (int j = 0; j < num; j++)
            {
                int num5 = defaultSunLight;
                if (chunkY != chunks.Length - 1) num5 = chunks[chunkY + 1].Lighting.GetSunlight(j * num + i);

                for (int num6 = chunkY; num6 >= 0; num6--)
                {
                    int num7 = ((num - 1) * num + j) * num + i;
                    worldChunk = chunks[num6];
                    lighting = chunks[num6].Lighting;
                    tmpPosDimensionAware.Set(num3 + i, num6 * num + num - 1, num4 + j);

                    for (int num8 = num - 1; num8 >= 0; num8--)
                    {
                        GetBlockAndAbsorption(worldChunk, num7, out Block block, out int lightAbsorptionAt);

                        // Light travels from top to bottom
                        tmpPosDimensionAware.Set(num3 + i, num6 * num + num8, num4 + j);

                        float effectiveAbs = GetEffectiveAbsorption(
                            block, lightAbsorptionAt, BlockFacing.DOWN, num5, tmpPosDimensionAware);

                        if (effectiveAbs > num5)
                        {
                            // маска солидности
                            int solidMask = 0;
                            if (block.SideSolid[0]) solidMask |= 1;
                            if (block.SideSolid[1]) solidMask |= 2;
                            if (block.SideSolid[2]) solidMask |= 4;
                            if (block.SideSolid[3]) solidMask |= 8;
                            if (block.SideSolid[4]) solidMask |= 16;
                            if (block.SideSolid[5]) solidMask |= 32;

                            // ── Ступень 2: полный блок ──
                            if (solidMask == 63)
                                lighting.SetSunlight(num7, num5); // чтобы листа не темнела у полных блоков
                            else
                                lighting.SetSunlight(num7, 0);

                            num6 = -1;
                            break;
                        }

                        lighting.SetSunlight(num7, num5);
                        num7 -= YPlus;
                        num5 -= (ushort)effectiveAbs;
                        tmpPosDimensionAware.Y--;
                    }
                }
            }
        }
    }

    /// <summary> Horizontal propagation of sunlight inside chunks. </summary>
    public void SunlightFlood(IWorldChunk[] chunks, int chunkX, int chunkY, int chunkZ)
    {
        int num = chunkSize;
        Stack<FastBlockPos> stack = new Stack<FastBlockPos>();

        int num2 = chunkX * num;
        int num3 = chunkZ * num;

        IWorldChunk worldChunk;
        IChunkLight lighting;

        for (int num4 = chunkY; num4 >= 0; num4--)
        {
            worldChunk = chunks[num4];
            worldChunk.Unpack();
            lighting = worldChunk.Lighting;

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
                        int num7 = lighting.GetSunlight(num5) - 1;

                        if (num7 <= 0) break;

                        // Absorption is calculated later in SpreadSunLightInColumn, taking direction into account
                        if ((i < num - 1 && lighting.GetSunlight(num5 + XPlus) < num7) ||
                            (j < num - 1 && lighting.GetSunlight(num5 + ZPlus) < num7) ||
                            (i > 0 && lighting.GetSunlight(num5 - XPlus) < num7) ||
                            (j > 0 && lighting.GetSunlight(num5 - ZPlus) < num7))
                        {
                            stack.Push(new FastBlockPos(
                                num2 + i,
                                num4 * num + num6,
                                num3 + j,
                                tmpPosDimensionAware.dimension));
                            if (stack.Count > 50)
                                SpreadSunLightInColumn(stack, chunks);
                        }
                    }
                }
            }
        }
        SpreadSunLightInColumn(stack, chunks);
    }

    /// <summary> Exchange of sunlight across the boundaries of neighboring chunks. </summary>
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

        BlockFacing[] hORIZONTALS = BlockFacing.HORIZONTALS;
        foreach (BlockFacing blockFacing in hORIZONTALS)
        {
            bool flag = true;
            int x = blockFacing.Normali.X;
            int z = blockFacing.Normali.Z;

            for (int j = 0; j < curChunks.Length; j++)
            {
                array3[j] = chunkProvider.GetChunk(chunkX + x, j + dimension * 1024, chunkZ + z);
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
            int num5 = 0;
            if (x == 0) array[num5++] = 0;
            if (y == 0) array[num5++] = 1;
            if (z == 0) array[num5++] = 2;

            int fixedNeighbourX = (x != 0) ? GameMath.Mod(array2[0] + x, num) : 0;
            int fixedNeighbourZ = (z != 0) ? GameMath.Mod(array2[2] + z, num) : 0;

            IWorldChunk worldChunk;
            IWorldChunk worldChunk2;
            IChunkLight lighting;
            IChunkLight lighting2;

            for (int num6 = chunkY; num6 >= 0; num6--)
            {
                worldChunk = array3[num6];
                worldChunk2 = curChunks[num6];
                lighting = worldChunk.Lighting;
                lighting2 = worldChunk2.Lighting;

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

                        int curLight = lighting2.GetSunlight(index3d);
                        int nLight = lighting.GetSunlight(index3d2);

                        BlockFacing dir = blockFacing;
                        BlockFacing oppDir = dir.Opposite;

                        GetBlockAndAbsorption(worldChunk2, index3d, out Block curBlock, out int curBaseAbs);

                        tmpPosDimensionAware.Set(num4 + num9, num6 * num + array2[1], num4 + num10);
                        GetBlockAndAbsorption(worldChunk, index3d2, out Block nBlock, out int nBaseAbs);

                        // Ray from Current chunk to Neighbor chunk
                        tmpPos2.Set(num2 + array2[0], num6 * num + array2[1], num3 + array2[2]);
                        tmpPos2.dimension = dimension;
                        float absCurToN = GetEffectiveAbsorption(curBlock, curBaseAbs, dir, curLight, tmpPos2);
                        int lightArrivingAtN = curLight - (int)absCurToN - 1;

                        tmpPosDimensionAware.Set(num4 + num9, num6 * num + array2[1], num4 + num10);
                        float absNFromCur = GetEffectiveAbsorption(nBlock, nBaseAbs, dir, lightArrivingAtN,
                            tmpPosDimensionAware);
                        int finalLightToN = lightArrivingAtN;
                        if (absNFromCur > lightArrivingAtN) finalLightToN = 0;

                        // Ray from Neighbor chunk to Current chunk
                        float absNToCur = GetEffectiveAbsorption(nBlock, nBaseAbs, oppDir, nLight);
                        int lightArrivingAtCur = nLight - (int)absNToCur - 1;
                        float absCurFromN = GetEffectiveAbsorption(curBlock, curBaseAbs, oppDir, lightArrivingAtCur);
                        int finalLightToCur = lightArrivingAtCur;
                        if (absCurFromN > lightArrivingAtCur) finalLightToCur = 0;

                        if (finalLightToN > nLight)
                        {
                            lighting.SetSunlight(index3d2, finalLightToN);
                            stack2.Push(new FastBlockPos(num4 + num9, num6 * num + array2[1], num4 + num10, dimension));
                            b |= blockFacing.Flag;
                        }
                        else if (finalLightToCur > curLight)
                        {
                            lighting2.SetSunlight(index3d, finalLightToCur);
                            stack.Push(new FastBlockPos(num2 + array2[0], num6 * num + array2[1], num3 + array2[2], dimension));
                        }
                    }
                }
            }

            if (stack2.Count > 0)
            {
                SpreadSunLightInColumn(stack2, array3);
                for (int k = 0; k < array3.Length; k++) array3[k].MarkModified();
            }
            if (stack.Count > 0) SpreadSunLightInColumn(stack, curChunks);
        }
        return b;
    }

    /// <summary> Processing a batch of deferred block light updates  </summary>
    public void ProcessScheduledBlockLightUpdates(
        List<Vec4i> scheduledUpdates)
    {
        if (scheduledUpdates == null ||
            scheduledUpdates.Count == 0)
        {
            return;
        }

        BlockPos blockPos = new BlockPos(0);

        foreach (Vec4i item in scheduledUpdates)
        {
            Block block = blockTypes[item.W];

            blockPos.SetAndCorrectDimension(
                item.X,
                item.Y,
                item.Z
            );

            byte[] hsv = block.GetLightHsv(
                readBlockAccess,
                blockPos
            );

            if (hsv == null ||
                hsv.Length < 3 ||
                hsv[2] <= 0)
            {
                continue;
            }

            int x = blockPos.X;
            int y = blockPos.InternalY;
            int z = blockPos.Z;

            IWorldChunk chunk =
                GetChunkAtPos(x, y, z);

            if (chunk == null)
                continue;

            int lightPosition =
                InChunkIndex(x, y, z);

            if (!chunk.LightPositions.Contains(lightPosition))
            {
                chunk.LightPositions.Add(lightPosition);
            }

            QueueDirtyLightSphere(
                x,
                y,
                z,
                hsv[2]
            );
        }

        // The scheduled list is already one batch.
        FlushPendingBlockLightUpdates();
    }

    /// <summary> BFS propagation of sunlight over a stack of coordinates. </summary>
    private void SpreadSunLightInColumn(Stack<FastBlockPos> stack, IWorldChunk[] chunks)
    {
        int num = chunkSize;
        IWorldChunk worldChunk;

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

            worldChunk = chunks[chunkY];

            tmpPos.Set(pos.X, pos.Y, pos.Z);
            tmpPos.dimension = pos.Dim;

            GetBlockAndAbsorption(worldChunk, index3d, out Block posBlock, out int baseAbsorption);
            int currentLight = worldChunk.Lighting.GetSunlight(index3d);

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
                    if (nChunkY != lastChunkY)
                    {
                        worldChunk = chunks[nChunkY];
                        lastChunkY = nChunkY;
                    }

                    int nIndex3d = ((ny & chunkSizeMask) * num + nlz) * num + nlx;
                    BlockFacing dir = BlockFacing.ALLFACES[i];

                    float effectiveAbs = GetEffectiveAbsorption(
                        posBlock, baseAbsorption, dir, currentLight, tmpPos);

                    int newLight = currentLight - (int)effectiveAbs - 1;

                    // Prevent light from propagating into fully opaque blocks or dying out
                    if (newLight <= 0) continue;

                    // Absorption by the neighboring block
                    GetBlockAndAbsorption(worldChunk, nIndex3d, out Block nBlock, out int nBaseAbs);
                    tmpPos.Set(chunkX * num + nlx, ny, chunkZ * num + nlz);
                    tmpPos2.Set(chunkX * num + nlx, ny, chunkZ * num + nlz);

                    float nEffectiveAbs = GetEffectiveAbsorption(
                        nBlock, nBaseAbs, dir, newLight, tmpPos2);

                    int finalLight = newLight;

                    if (nEffectiveAbs > newLight)
                    {
                        // маска солидности
                        int solidMask = 0;
                        if (nBlock.SideSolid[0]) solidMask |= 1;
                        if (nBlock.SideSolid[1]) solidMask |= 2;
                        if (nBlock.SideSolid[2]) solidMask |= 4;
                        if (nBlock.SideSolid[3]) solidMask |= 8;
                        if (nBlock.SideSolid[4]) solidMask |= 16;
                        if (nBlock.SideSolid[5]) solidMask |= 32;

                        // ── Ступень 2: не полный блок ──
                        if (solidMask != 63) // чтобы листва не темнела у полных блоков
                            finalLight = 0;
                    }

                    if (worldChunk.Lighting.GetSunlight(nIndex3d) < finalLight)
                    {
                        worldChunk.Lighting.SetSunlight(nIndex3d, finalLight);
                        int absX = chunkX * num + nlx;
                        int absZ = chunkZ * num + nlz;
                        stack.Push(new FastBlockPos(absX, ny, absZ, pos.Dim));
                    }
                }
            }
        }
    }

    /// <summary> Gets the sunlight level at world coordinates. </summary>
    private int SunLightLevelAt(int posX, int posY, int posZ, bool substractAbsorb = false)
    {
        int num = chunkSize;
        IWorldChunk unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(posX / num, posY / num, posZ / num, notRecentlyAccessed: true);
        if (unpackedChunkFast == null) return defaultSunLight;
        int index3d = (posY % num * num + posZ % num) * num + posX % num;

        if (!substractAbsorb) return unpackedChunkFast.Lighting.GetSunlight(index3d);

        GetBlockAndAbsorption(unpackedChunkFast, index3d, out _, out int abs);
        tmpPos.Set(posX, posY, posZ);
        return unpackedChunkFast.Lighting.GetSunlight(index3d) - abs;
    }

    /// <summary>
    /// Updates sunlight when a block's transparency changes.
    /// Fully recalculates a cross of 5 chunk columns, but ONLY from posY and below.
    /// Sunlight above the changed block remains unchanged.
    /// </summary>
    public FastSetOfLongs UpdateSunLight(int posX, int posY, int posZ, int oldAbsorb, int newAbsorb)
    {
        FastSetOfLongs touchedChunks = new FastSetOfLongs();
        if (newAbsorb == oldAbsorb) return touchedChunks;

        if (posX < 0 || posY < 0 || posZ < 0 ||
            posX >= mapsizex || posY >= mapsizey || posZ >= mapsizez)
            return touchedChunks;

        int num = chunkSize;
        int chunkX = posX >> chunkSizeLog2;
        int chunkZ = posZ >> chunkSizeLog2;

        int dim = posY / 32768;
        int dimOffset = dim * 1024;

        int chunksPerColumn = mapsizey / num;

        // Recalculate only from this chunk and below.
        // Sunlight propagates from top to bottom. A transparency change at
        // posY only affects blocks at posY and below. Chunks above startChunkY
        // retain correct light, so we don't touch them.
        int startChunkY = posY >> chunkSizeLog2;

        // Gather 5 columns (cross pattern: center + N/S/E/W)
        var columns = new List<(int cx, int cz, IWorldChunk[] chunks)>(5);

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx != 0 && dz != 0) continue; // Skip diagonals

                int cx = chunkX + dx;
                int cz = chunkZ + dz;

                if (cx < 0 || cz < 0 || cx * num >= mapsizex || cz * num >= mapsizez)
                    continue;

                IWorldChunk[] chunks = new IWorldChunk[chunksPerColumn];
                bool allLoaded = true;

                for (int cy = 0; cy < chunksPerColumn; cy++)
                {
                    chunks[cy] = chunkProvider.GetChunk(cx, cy + dimOffset, cz);
                    if (chunks[cy] == null) { allLoaded = false; break; }
                    chunks[cy].Unpack();
                }

                if (allLoaded)
                    columns.Add((cx, cz, chunks));
            }
        }

        if (columns.Count == 0) return touchedChunks;

        int totalBlocks = num * num * num;

        // Pass 1: Extinguishing + Direct light + Horizontal flood
        foreach (var (cx, cz, chunks) in columns)
        {
            // Extinguish sunlight ONLY in chunks from startChunkY down to 0
            for (int cy = startChunkY; cy >= 0; cy--)
            {
                IChunkLight lighting = chunks[cy].Lighting;
                for (int idx = 0; idx < totalBlocks; idx++)
                    lighting.SetSunlight(idx, 0);
            }

            // Direct sunlight: start from startChunkY.
            // Sunlight() will read the initial level from chunks[startChunkY+1] itself
            // (which was NOT extinguished and still contains correct light).
            Sunlight(chunks, cx, startChunkY, cz, dim);

            // Horizontal propagation inside the column (also from startChunkY downwards)
            SunlightFlood(chunks, cx, startChunkY, cz);
        }

        // Pass 2: Boundary exchange + marking modified chunks
        foreach (var (cx, cz, chunks) in columns)
        {
            SunLightFloodNeighbourChunks(chunks, cx, startChunkY, cz, dim);

            // Mark only the chunks that were actually recalculated
            for (int cy = startChunkY; cy >= 0; cy--)
            {
                touchedChunks.Add(chunkProvider.ChunkIndex3D(cx, cy + dimOffset, cz));
                chunks[cy].MarkModified();
            }
        }

        return touchedChunks;
    }

    /// <summary> Checks if the sun rays reach the block directly (no obstacles from above). </summary>
    public bool IsDirectlyIlluminated(int posX, int posY, int posZ)
    {
        int num = chunkSize;
        int num2 = 0;
        int num3 = SunLightLevelAt(posX, posY, posZ);

        IWorldChunk unpackedChunkFast;

        while (posY < mapsizey)
        {
            posY++;
            unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(posX / num, posY / num, posZ / num);
            if (unpackedChunkFast == null) break;

            int index3d = (posY % num * num + posZ % num) * num + posX % num;
            int sunlight = unpackedChunkFast.Lighting.GetSunlight(index3d);

            GetBlockAndAbsorption(unpackedChunkFast, index3d, out Block block, out int baseAbs);
            tmpDiPos.Set(posX, posY, posZ);

            // Light falls from top to bottom through this block
            num2 += (int)GetEffectiveAbsorption(block, baseAbs, BlockFacing.DOWN, defaultSunLight - num2);

            if (defaultSunLight - num2 < num3) return false;
            if (sunlight == defaultSunLight) return true;
            if (num3 > sunlight) return false;
        }

        return defaultSunLight - num2 == num3;
    }

    /// <summary> BFS propagation of sunlight (queue) </summary>
    public void SpreadSunlightAt(QueueOfInt unhandledPositions, BlockPos centerPos, bool isDirectlyIlluminated, FastSetOfLongs touchedChunks)
    {
        int num = chunkSize;
        tmpPos.SetDimension(centerPos.dimension);

        IWorldChunk unpackedChunkFast;

        while (unhandledPositions.Count > 0)
        {
            int num2 = unhandledPositions.Dequeue();
            int num3 = (num2 >> 24) & 0x1F;
            if (num3 == 0) continue;

            int num4 = (num2 & 0xFF) - 128 + centerPos.X;
            int num5 = ((num2 >> 8) & 0xFF) - 128 + centerPos.Y;
            int num6 = ((num2 >> 16) & 0xFF) - 128 + centerPos.Z;

            unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(num4 / num, num5 / num + centerPos.dimension * 1024, num6 / num);
            if (unpackedChunkFast == null) continue;

            int index3d = (num5 % num * num + num6 % num) * num + num4 % num;
            unpackedChunkFast.Lighting.SetSunlight_Buffered(index3d, num3);

            GetBlockAndAbsorption(unpackedChunkFast, index3d, out Block curBlock, out int baseAbsorption);
            tmpPos.Set(num4, num5, num6);

            int num7 = ((num2 >> 29) & 7) - 1;

            for (int i = 0; i < 6; i++)
            {
                if (i == num7) continue;

                Vec3i vec3i = BlockFacing.ALLNORMALI[i];
                int num8 = num4 + vec3i.X;
                int num9 = num5 + vec3i.Y;
                int num10 = num6 + vec3i.Z;

                if ((num8 | num9 | num10) < 0 || num8 >= mapsizex || num9 >= mapsizey || num10 >= mapsizez) continue;

                unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num);
                if (unpackedChunkFast != null)
                {
                    touchedChunks.Add(chunkProvider.ChunkIndex3D(num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num));

                    index3d = (num9 % num * num + num10 % num) * num + num8 % num;
                    BlockFacing dir = BlockFacing.ALLFACES[i];

                    float effectiveAbs = GetEffectiveAbsorption(curBlock, baseAbsorption, dir, num3);
                    int distLoss = ((!isDirectlyIlluminated || num8 != centerPos.X || num10 != centerPos.Z || i != 5) ? 1 : 0);
                    int lightArrivingAtN = num3 - (int)effectiveAbs - distLoss;

                    if (lightArrivingAtN <= 0) continue;

                    GetBlockAndAbsorption(unpackedChunkFast, index3d, out Block nBlock, out int nBaseAbs);
                    tmpPos.Set(num8, num9, num10);

                    float nEffectiveAbs = GetEffectiveAbsorption(nBlock, nBaseAbs, dir, lightArrivingAtN);

                    int finalLight = lightArrivingAtN;
                    if (nEffectiveAbs > lightArrivingAtN) finalLight = 0;

                    if (unpackedChunkFast.Lighting.GetSunlight(index3d) < finalLight)
                    {
                        unhandledPositions.EnqueueIfLarger(num8 - centerPos.X, num9 - centerPos.Y, num10 - centerPos.Z, finalLight + (TileSideEnum.GetOpposite(i) + 1 << 5));
                    }
                }
            }
        }
        tmpPos.SetDimension(0);
    }

    /// <summary> BFS "clearing" (shadow spreading) when an obstacle for the sun appears. </summary>
    public void ClearSunlightAt(QueueOfInt positionsToClear, BlockPos centerPos, bool isDirectlyIlluminated, QueueOfInt retainedLightToSpread, FastSetOfLongs touchedChunks)
    {
        int num = chunkSize;
        FastSetOfInts fastSetOfInts = new FastSetOfInts();
        tmpPos.SetDimension(centerPos.dimension);

        IWorldChunk unpackedChunkFast;

        while (positionsToClear.Count > 0)
        {
            int num2 = positionsToClear.Dequeue();
            int num3 = (num2 & 0xFF) - 128 + centerPos.X;
            int num4 = ((num2 >> 8) & 0xFF) - 128 + centerPos.Y;
            int num5 = ((num2 >> 16) & 0xFF) - 128 + centerPos.Z;

            unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(num3 / num, num4 / num + centerPos.dimension * 1024, num5 / num);
            if (unpackedChunkFast == null) continue;

            int index3d = (num4 % num * num + num5 % num) * num + num3 % num;
            int sunlight = unpackedChunkFast.Lighting.GetSunlight(index3d);

            if (sunlight != 0) fastSetOfInts.RemoveIfMatches(num3 - centerPos.X, num4 - centerPos.Y, num5 - centerPos.Z, sunlight);

            unpackedChunkFast.Lighting.SetSunlight_Buffered(index3d, 0);

            GetBlockAndAbsorption(unpackedChunkFast, index3d, out Block curBlock, out int baseAbsorption);
            tmpPos.Set(num3, num4, num5);

            int num7 = ((num2 >> 29) & 7) - 1;

            for (int i = 0; i < 6; i++)
            {
                if (i == num7) continue;

                Vec3i vec3i = BlockFacing.ALLNORMALI[i];
                int num8 = num3 + vec3i.X;
                int num9 = num4 + vec3i.Y;
                int num10 = num5 + vec3i.Z;

                if ((num8 | num9 | num10) < 0 || num8 >= mapsizex || num9 >= mapsizey || num10 >= mapsizez) continue;

                unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num);
                if (unpackedChunkFast == null) continue;

                touchedChunks.Add(chunkProvider.ChunkIndex3D(num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num));

                BlockFacing dir = BlockFacing.ALLFACES[i];
                float effectiveAbs = GetEffectiveAbsorption(curBlock, baseAbsorption, dir, (num2 >> 24) & 0x1F);
                int distLoss = 1 - ((isDirectlyIlluminated && num8 == centerPos.X && num10 == centerPos.Z && i == 5) ? 1 : 0);
                int num11 = ((num2 >> 24) & 0x1F) - (int)effectiveAbs - distLoss;

                if (num11 <= 0) continue;

                index3d = (num9 % num * num + num10 % num) * num + num8 % num;
                int sunlight2 = unpackedChunkFast.Lighting.GetSunlight(index3d);

                if (sunlight2 != 0)
                {
                    if (sunlight2 <= num11) positionsToClear.EnqueueIfLarger(num8 - centerPos.X, num9 - centerPos.Y, num10 - centerPos.Z, num11 + (TileSideEnum.GetOpposite(i) + 1 << 5));
                    else fastSetOfInts.Add(num8 - centerPos.X, num9 - centerPos.Y, num10 - centerPos.Z, sunlight2);
                }
            }
        }

        foreach (int item in fastSetOfInts) retainedLightToSpread.Enqueue(item);
        tmpPos.SetDimension(0);
    }

    
}