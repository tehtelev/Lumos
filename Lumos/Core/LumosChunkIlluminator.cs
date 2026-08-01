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

/// <summary>
/// Ray-traced block-light illuminator with sunlight support.
///
/// Improvements over vanilla ChunkIlluminator:
/// - Zero-GC: object pools and struct arrays eliminate GC micro-stutters.
/// - Ray Tracing: direct light with single-bounce reflections
///   (50 % normal blocks, 12 % glass, 18 % liquids).
/// - Directional Absorption: per-face solid-mask checks correctly handle
///   slabs, stairs, and volumetric transparent blocks.
/// - Sunlight Invalidation: UpdateSunLight correctly extinguishes trapped
///   sunlight in sealed caves and rooms.
/// - Microblock-aware absorption: chisel blocks (BlockMicroBlock) resolve
///   through BlockEntityMicroBlock instead of the meaningless static
///   LightAbsorption / SideSolid (see GetBlockAndAbsorption / GetSolidMask).
///
/// Limitations:
/// - Hard limit 128³: light is clipped at boundaries (same as vanilla).
/// - Color distortion: top-4 limit for HSV mixing.
/// - Not thread-safe: shared arrays require separate instances per thread.
/// </summary>
public class LumosChunkIlluminator
{
    // ─── Constants ───────────────────────────────────────────────────────

    /// <summary>Maximum block-light level (inclusive).</summary>
    private const int MAX_BLOCK_LIGHT_LEVEL = 31;

    /// <summary>Extra padding (in blocks) added to dirty-sphere radius.</summary>
    private const int DIRTY_RADIUS_PADDING = 1;

    // ─── Dirty-region batching ───────────────────────────────────────────

    /// <summary>
    /// Spherical dirty region queued by PlaceBlockLight / RemoveBlockLight /
    /// UpdateBlockLight. One FlushPendingBlockLightUpdates() recalculates
    /// the complete batch.
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

    /// <summary>Pending dirty spheres keyed by packed source position.</summary>
    private readonly Dictionary<long, DirtyLightSphere> pendingDirtySpheres =
        new Dictionary<long, DirtyLightSphere>(256);

    /// <summary>Reusable buffer: snapshot of pendingDirtySpheres for one flush.</summary>
    private readonly List<DirtyLightSphere> dirtySphereBuffer =
        new List<DirtyLightSphere>(256);

    /// <summary>Exact union of all dirty spheres (packed cell keys).</summary>
    private readonly HashSet<long> dirtyLightCells =
        new HashSet<long>();

    /// <summary>Deduplication map: packed position → index in nearbyLightSourcesArray.</summary>
    private readonly Dictionary<long, int> nearbySourceIndexByPosition =
        new Dictionary<long, int>(512);

    /// <summary>Re-entrancy guard for FlushPendingBlockLightUpdates.</summary>
    private bool isFlushingBlockLight;

    // ─── World / chunk geometry ──────────────────────────────────────────

    /// <summary>Default sunlight level for the current dimension.</summary>
    private ushort defaultSunLight;

    private int mapsizex;
    private int mapsizey;
    private int mapsizez;

    /// <summary>Stride multipliers for flat chunk indexing (X=1, Z=chunkSize, Y=chunkSize²).</summary>
    private int XPlus = 1;
    private int YPlus;
    private int ZPlus;

    private IList<Block> blockTypes;

    private int chunkSize;
    private int chunkSizeLog2;
    private int chunkSizeMask;

    internal IChunkProvider chunkProvider;
    private IBlockAccessor readBlockAccess;

    // ─── Reusable temporary positions (avoid per-call allocation) ────────

    private BlockPos tmpDiPos = new BlockPos(0);
    private BlockPos tmpPos = new BlockPos(0);
    private BlockPos tmpPos2 = new BlockPos(0);
    private BlockPos tmpPosDimensionAware = new BlockPos(0);

    // ─── Block property caches ───────────────────────────────────────────

    /// <summary>
    /// Light absorption per BlockId. Filled once in InitForWorld.
    /// For chisel blocks (BlockMicroBlock) this is a static JSON stub
    /// (usually 99) and is NOT used directly — see isMicroblockCache and
    /// GetBlockAndAbsorption for the real per-entity path.
    /// </summary>
    private int[] absorptionCache;

    /// <summary>
    /// Per-BlockId flag: "is this a chisel microblock?".
    /// Allows a single array lookup instead of a virtual `is` check
    /// on every block in the hot tracing loop.
    /// </summary>
    private bool[] isMicroblockCache;

    // ─── Microblock helpers ──────────────────────────────────────────────

    /// <summary>
    /// Builds a packed bitmask of face solidity (bits 0..5 = BlockFacing.Index).
    ///
    /// For chisel blocks: reads the precomputed MicroblockLightProfile
    /// (bit i = "face i is nearly solid", openness &lt; 25 %).
    /// For normal blocks: static Block.SideSolid.
    ///
    /// Single source of truth used by GetEffectiveAbsorption, ProcessRay,
    /// Sunlight, and SpreadSunLightInColumn.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetSolidMask(Block block, BlockEntityMicroBlock microBE)
    {
        if (microBE != null)
        {
            MicroblockLightProfile profile =
                MicroblockLightCache.GetOrCompute(microBE, blockTypes);

            int mask = 0;
            // Face is "nearly solid" when openness < 25 % (64/255)
            if (profile.FaceOpenness0 < 64) mask |= 1;  // N
            if (profile.FaceOpenness1 < 64) mask |= 2;  // E
            if (profile.FaceOpenness2 < 64) mask |= 4;  // S
            if (profile.FaceOpenness3 < 64) mask |= 8;  // W
            if (profile.FaceOpenness4 < 64) mask |= 16; // U
            if (profile.FaceOpenness5 < 64) mask |= 32; // D
            return mask;
        }

        int m = 0;
        if (block.SideSolid[0]) m |= 1;
        if (block.SideSolid[1]) m |= 2;
        if (block.SideSolid[2]) m |= 4;
        if (block.SideSolid[3]) m |= 8;
        if (block.SideSolid[4]) m |= 16;
        if (block.SideSolid[5]) m |= 32;
        return m;
    }

    /// <summary>
    /// Resolves the effective block, its base absorption, and an optional
    /// BlockEntityMicroBlock for the given chunk-local index.
    /// Handles solid layer, fluid overlay, and chisel microblocks.
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

        if (isMicroblockCache[solidId])
        {
            microBE = chunk.GetLocalBlockEntityAtBlockPos(pos) as BlockEntityMicroBlock;

            if (microBE != null)
            {
                MicroblockLightProfile profile =
                    MicroblockLightCache.GetOrCompute(microBE, blockTypes);

                // Average across axes; the specific axis is refined in GetEffectiveAbsorption
                baseAbsorption = (profile.EffectiveAbsX +
                                  profile.EffectiveAbsY +
                                  profile.EffectiveAbsZ) / 3;

                // Fast path: all materials are transparent
                if (profile.MinMaterialAbsorption == 0 && profile.VolumeFraction < 128)
                    baseAbsorption = Math.Min(baseAbsorption, profile.AvgMaterialAbsorption);
            }
            else
            {
                baseAbsorption = absorptionCache[solidId];
            }
        }
        else
        {
            baseAbsorption = absorptionCache[solidId];
        }

        // Fluid overlay can only increase absorption
        if (fluidId != 0)
        {
            int fluidAbs = absorptionCache[fluidId];
            if (fluidAbs > baseAbsorption)
                baseAbsorption = fluidAbs;
        }

        block = blockTypes[solidId != 0 ? solidId : fluidId];
    }

    // ─── Dictionary-based light staging (replaces the old 128³ grid) ─────

    #region Light staging

    /// <summary>
    /// Accumulates per-source light contributions for a single block.
    /// Dynamic list — no hard limit on the number of overlapping sources.
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

        /// <summary>Adds a new source or raises the level of an existing one (max-wins).</summary>
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

    /// <summary>Staging dictionary: packed world position → accumulated light sources.</summary>
    private Dictionary<long, LightSourcesAtBlock> visitedNodes =
        new Dictionary<long, LightSourcesAtBlock>(4096);

    /// <summary>Pool of recycled LightSourcesAtBlock instances.</summary>
    private Stack<LightSourcesAtBlock> lsabPool =
        new Stack<LightSourcesAtBlock>(256);

    /// <summary>
    /// Packs three signed 21-bit coordinates into a single long.
    /// Range per axis: [-1 048 576, +1 048 575].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackPos(int x, int y, int z)
    {
        return ((long)(x & 0x1FFFFF)) |
               ((long)(y & 0x1FFFFF) << 21) |
               ((long)(z & 0x1FFFFF) << 42);
    }

    /// <summary>Inverse of PackPos.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UnpackPos(long key, out int x, out int y, out int z)
    {
        x = (int)(key & 0x1FFFFF);
        y = (int)((key >> 21) & 0x1FFFFF);
        z = (int)((key >> 42) & 0x1FFFFF);

        if ((x & 0x100000) != 0) x |= unchecked((int)0xFFE00000);
        if ((y & 0x100000) != 0) y |= unchecked((int)0xFFE00000);
        if ((z & 0x100000) != 0) z |= unchecked((int)0xFFE00000);
    }

    /// <summary>Returns the existing entry or creates a fresh one from the pool.</summary>
    private LightSourcesAtBlock GetOrCreateLsab(long key)
    {
        if (visitedNodes.TryGetValue(key, out var lsab))
            return lsab;

        lsab = lsabPool.Count > 0 ? lsabPool.Pop() : new LightSourcesAtBlock();
        lsab.Reset();
        visitedNodes[key] = lsab;
        return lsab;
    }

    /// <summary>Returns all staging entries to the pool and clears the dictionary.</summary>
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

    // ─── Nearby light sources (struct arrays, zero-GC) ───────────────────

    #region Nearby sources

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

    // ─── Lightweight position struct (avoids BlockPos allocation in BFS) ─

    private struct FastBlockPos
    {
        public int X, Y, Z, Dim;
        public FastBlockPos(int x, int y, int z, int dim)
        {
            X = x; Y = y; Z = z; Dim = dim;
        }
    }

    // ─── Ray Tracing System ──────────────────────────────────────────────

    #region Ray Tracing

    private struct LightRay
    {
        public float OriginX, OriginY, OriginZ;
        public float DirX, DirY, DirZ;
        public float Energy;
        public byte H, S, B;
        public int BounceCount;
        public int SourceId;
    }

    /// <summary>Fixed-size ring buffer of active rays.</summary>
    private LightRay[] rayPool;
    private int rayPoolHead;
    private int rayPoolTail;
    private int activeRayCount;

    private const int MAX_RAY_POOL_SIZE = 200000;
    private const int REFLECTION_RAYS_COUNT = 128;

    /// <summary>Desired gap (in blocks) between adjacent rays on the sphere surface.</summary>
    private const float TARGET_GAP = 1.3f;
    /// <summary>Lower bound so dim sources are not completely "holey".</summary>
    private const int MIN_RAYS = 512;
    /// <summary>Upper bound to prevent excessive load.</summary>
    private const int MAX_RAYS = 40000;
    /// <summary>Radius quantisation step for the sphere cache.</summary>
    private const int RADIUS_BUCKET_STEP = 2;

    /// <summary>
    /// Thread-safe cache: bucketed radius → (direction array, actual point count).
    /// Shared across all illuminator instances.
    /// </summary>
    private static ConcurrentDictionary<int, (float[][] dirs, int count)> sphereCache =
        new ConcurrentDictionary<int, (float[][], int)>();

    /// <summary>Allocates the ray ring buffer and pre-warms the sphere cache.</summary>
    private void InitRayTracing()
    {
        rayPool = new LightRay[MAX_RAY_POOL_SIZE];
        rayPoolHead = 0;
        rayPoolTail = 0;
        activeRayCount = 0;

        // Pre-warm cache for typical brightness values
        for (int r = 7; r <= 23; r++)
            GetOrBuildSphereForRadius(r);
    }

    /// <summary>N = 24·R² / gap², clamped to [MIN_RAYS, MAX_RAYS].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalcRayCountForRadius(int radius)
    {
        if (radius <= 0) return MIN_RAYS;
        float n = 24f * radius * radius / (TARGET_GAP * TARGET_GAP);
        int result = (int)Math.Ceiling(n);
        return Math.Clamp(result, MIN_RAYS, MAX_RAYS);
    }

    /// <summary>Quantises radius upward to the nearest bucket boundary.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketRadius(int radius)
    {
        return ((radius + RADIUS_BUCKET_STEP - 1) / RADIUS_BUCKET_STEP) * RADIUS_BUCKET_STEP;
    }

    /// <summary>Returns cached Fibonacci-sphere directions or builds them on first use.</summary>
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

    /// <summary>Generates uniformly distributed unit vectors via the Fibonacci spiral.</summary>
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

    /// <summary>Resets the ring buffer for a new source trace.</summary>
    private void ResetRayPool()
    {
        rayPoolHead = 0;
        rayPoolTail = 0;
        activeRayCount = 0;
    }

    /// <summary>Enqueues a ray into the ring buffer (drops silently if full).</summary>
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

    /// <summary>Dequeues the oldest ray from the ring buffer.</summary>
    private LightRay DequeueRay()
    {
        var ray = rayPool[rayPoolTail];
        rayPoolTail = (rayPoolTail + 1) % MAX_RAY_POOL_SIZE;
        activeRayCount--;
        return ray;
    }

    // ─── Precomputed reflection tables ───────────────────────────────────

    /// <summary>
    /// cos(θᵢ) / sin(θᵢ) by golden angle — independent of the actual ray
    /// count per call. Removes Math.Cos/Sin from the hot SpawnReflectionRays path.
    /// </summary>
    private static readonly float[] reflCosTheta = new float[REFLECTION_RAYS_COUNT];
    private static readonly float[] reflSinTheta = new float[REFLECTION_RAYS_COUNT];

    private const int MIN_REFLECTION_RAYS = 16;

    /// <summary>Static constructor: fills the reflection angle tables once.</summary>
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
    /// Adaptive reflection ray count: weak reflections get fewer rays
    /// (saves sampling density), strong ones keep a dense hemisphere.
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
    /// Spawns a hemisphere of diffuse reflection rays around the hit normal.
    /// Uses Lambert's cosine law for energy weighting.
    /// </summary>
    private void SpawnReflectionRays(float x, float y, float z,
        float normalX, float normalY, float normalZ,
        float energy, byte h, byte s, byte b, int sourceId)
    {
        // Build an orthonormal basis (tangent, bitangent, normal)
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
            // cosAngle/sinAngle depend on N — one division + one Sqrt, no trig
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

            float weightedEnergy = energy * cosAngle; // Lambert's law

            if (weightedEnergy > 0.01f)
            {
                SpawnRay(x, y, z, worldDirX, worldDirY, worldDirZ,
                    weightedEnergy, h, s, b, 1, sourceId);
            }
        }
    }

    /// <summary>
    /// Returns the reflectivity percentage for a block type.
    /// Glass: 12 %, liquids: 18 %, everything else: 50 % (diffuse).
    /// </summary>
    private static int GetReflectivity(Block block)
    {
        if (block.Code.Path.ToString().Contains("glass"))
            return 12;

        if (block.IsLiquid())
            return 18;

        return 50;
    }

    /// <summary>
    /// DDA ray march: walks the ray through the voxel grid, applying light,
    /// absorption, and optional single-bounce reflections.
    /// Caches the current chunk to avoid redundant provider lookups.
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

        float prevDistance = 0f;

        // Chunk cache: DDA steps are 1 block, chunks are chunkSize³,
        // so most consecutive steps stay in the same chunk.
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

            // Fetch chunk only when the chunk coordinate actually changes
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
            tmpPos.Set(x, y, z);
            GetBlockAndAbsorption(chunk, index3d, tmpPos,
                out Block block, out int baseAbsorption, out BlockEntityMicroBlock microBE);

            energy -= stepDistance;
            float energyAtSurface = energy;          // энергия ДО поглощения

            float effectiveAbs = GetEffectiveAbsorption(
                block, baseAbsorption, hitFace, energy, microBE, tmpPos);
            if (effectiveAbs > 0f)
                energy -= effectiveAbs;


            bool isOpaque;
            int solidMask = GetSolidMask(block, microBE);   // ← передавайте microBE!

            if (microBE != null)
            {
                isOpaque = false;
            }
            else
            {
                isOpaque = effectiveAbs > 0 && (
                    (solidMask & (1 << hitFace.Index)) != 0 ||
                    (solidMask & (1 << hitFace.Opposite.Index)) != 0 ||
                    block.IsLiquid() ||
                    block.Replaceable >= 6000
                );
            }

            // ПРИМЕНЕНИЕ СВЕТА
            if (energy > 0f)
            {
                // Луч ещё жив — ставим свет в текущий блок
                ApplyLightToBlock(x, y, z, energy, ray.SourceId);
            }
            else if (energyAtSurface > 0f && solidMask == 63)
            {
                // Поглощение убило энергию, но блок полностью твёрдый.
                // Ставим «поверхностный» свет (аналог sunlight solidMask==63).
                // Используем энергию ДО поглощения, иначе будет 0.
                ApplyLightToBlock(x, y, z, energyAtSurface, ray.SourceId);
            }

            // Single-bounce reflection (scaled by volume fraction for microblocks)
            if (ray.BounceCount == 0 && effectiveAbs > 0)
            {
                int reflectivity = GetReflectivity(block);

                if (microBE != null && reflectivity > 0)
                {
                    MicroblockLightProfile profile =
                        MicroblockLightCache.GetOrCompute(microBE, blockTypes);
                    reflectivity = reflectivity * profile.VolumeFraction / 255;
                }

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

    /// <summary>Records a source contribution into the staging dictionary.</summary>
    private void ApplyLightToBlock(int x, int y, int z, float energy, int sourceId)
    {
        int lightLevel = (int)energy;
        if (lightLevel <= 0) return;

        long key = PackPos(x, y, z);
        var lsab = GetOrCreateLsab(key);
        lsab.AddOrUpdate(sourceId, (byte)lightLevel);
    }

    /// <summary>
    /// Traces all nearby sources (direct + reflection rays) into visitedNodes.
    /// Each source is processed fully before the next one starts.
    /// </summary>
    private void TraceNearbyBlockLights()
    {
        RecycleVisitedNodes();

        for (int srcIdx = 0; srcIdx < nearbyCount; srcIdx++)
        {
            byte h = nearbyH[srcIdx];
            byte s = nearbyS[srcIdx];
            byte brightness = nearbyB[srcIdx];

            if (brightness <= 0)
                continue;

            NearbyLightSourceStruct source = nearbyLightSourcesArray[srcIdx];

            ResetRayPool();

            // The source block itself always receives full brightness
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

            // Drain all rays (including reflections) for this source
            while (activeRayCount > 0)
            {
                LightRay ray = DequeueRay();
                ProcessRay(ray);
            }
        }
    }

    #endregion

    // ─── Initialisation ──────────────────────────────────────────────────

    public LumosChunkIlluminator()
    {
        // Empty — real init happens in InitFromVanillaConstructor / InitForWorld
    }

    /// <summary>
    /// Called from the ChunkIlluminator constructor postfix patch.
    /// Copies the original constructor parameters and allocates arrays.
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
    /// Called from the InitForWorld postfix patch.
    /// Builds per-BlockId caches and stores world dimensions.
    /// </summary>
    public void InitForWorld(IList<Block> blockTypes, ushort defaultSunLight,
        int mapsizex, int mapsizey, int mapsizez)
    {
        this.blockTypes = blockTypes;
        this.defaultSunLight = defaultSunLight;
        this.mapsizex = mapsizex;
        this.mapsizey = mapsizey;
        this.mapsizez = mapsizez;

        absorptionCache = new int[blockTypes.Count];
        isMicroblockCache = new bool[blockTypes.Count];
        for (int i = 0; i < blockTypes.Count; i++)
        {
            absorptionCache[i] = blockTypes[i].LightAbsorption;
            isMicroblockCache[i] = blockTypes[i] is BlockMicroBlock;
        }
    }

    // ─── Public API: block-light placement / removal ─────────────────────

    /// <summary>
    /// Registers a new light source and queues a dirty sphere.
    /// Actual recalculation is deferred to FlushPendingBlockLightUpdates.
    /// </summary>
    public FastSetOfLongs PlaceBlockLight(
        byte[] lightHsv, int posX, int posY, int posZ)
    {
        FastSetOfLongs result = new FastSetOfLongs();

        if (blockTypes == null || lightHsv == null ||
            lightHsv.Length < 3 || lightHsv[2] <= 0)
            return result;

        IWorldChunk chunk = GetChunkAtPos(posX, posY, posZ);
        if (chunk == null)
            return result;

        int lightPosition = InChunkIndex(posX, posY, posZ);

        if (!chunk.LightPositions.Contains(lightPosition))
            chunk.LightPositions.Add(lightPosition);

        QueueDirtyLightSphere(posX, posY, posZ, lightHsv[2]);

        return result;
    }

    /// <summary>
    /// Removes a light source and queues a dirty sphere for cleanup.
    /// </summary>
    public FastSetOfLongs RemoveBlockLight(
        byte[] oldLightHsv, int posX, int posY, int posZ)
    {
        FastSetOfLongs result = new FastSetOfLongs();

        if (blockTypes == null || oldLightHsv == null || oldLightHsv.Length < 3)
            return result;

        IWorldChunk chunk = GetChunkAtPos(posX, posY, posZ);
        if (chunk == null)
            return result;

        int lightPosition = InChunkIndex(posX, posY, posZ);
        chunk.LightPositions.Remove(lightPosition);

        int oldRadius = oldLightHsv[2];

        // Preserve the vanilla special case
        if (oldRadius == 18)
            oldRadius = 20;

        QueueDirtyLightSphere(posX, posY, posZ, oldRadius);

        return result;
    }

    /// <summary>
    /// Handles a transparency change (old vs new absorption).
    /// Queues a maximum-radius dirty sphere because the change can both
    /// remove existing light and reveal a previously blocked source.
    /// </summary>
    public FastSetOfLongs UpdateBlockLight(
        int oldLightAbsorb, int newLightAbsorb,
        int posX, int posY, int posZ)
    {
        FastSetOfLongs result = new FastSetOfLongs();

        if (blockTypes == null)
            return result;

        if (oldLightAbsorb == newLightAbsorb)
            return result;

        QueueDirtyLightSphere(posX, posY, posZ, MAX_BLOCK_LIGHT_LEVEL);

        return result;
    }

    /// <summary>
    /// Queues a dirty sphere and immediately flushes the batch.
    /// Used by FullRelight and external callers that need synchronous results.
    /// </summary>
    public void UpdateLightAt(
        int range, int posX, int posY, int posZ,
        FastSetOfLongs touchedChunks)
    {
        if (range <= 0)
            return;

        QueueDirtyLightSphere(posX, posY, posZ, range);

        FastSetOfLongs flushedChunks = FlushPendingBlockLightUpdates();

        foreach (long chunkIndex in flushedChunks)
            touchedChunks.Add(chunkIndex);
    }

    // ─── Absorption ──────────────────────────────────────────────────────

    /// <summary>
    /// Computes effective light absorption for a ray hitting a block face.
    ///
    /// Microblocks: returns the axis-specific effective absorption from the
    /// precomputed profile (already accounts for shape, materials, voids).
    ///
    /// Normal blocks: full absorption if the hit face or its opposite is solid;
    /// otherwise a fractional value proportional to baseAbsorption / 64.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetEffectiveAbsorption(
        Block block, int baseAbsorption, BlockFacing dir,
        float incomingEnergy,
        BlockEntityMicroBlock microBE = null, BlockPos pos = null)
    {
        if (baseAbsorption <= 0) return 0f;

        if (microBE != null)
        {
            MicroblockLightProfile profile =
                MicroblockLightCache.GetOrCompute(microBE, blockTypes);

            int axisIndex = dir.Axis == EnumAxis.X ? 0
                : dir.Axis == EnumAxis.Y ? 1 : 2;

            byte effAbs = profile.GetEffectiveAbsForAxis(axisIndex);

            if (effAbs == 0) return 0f;

            return effAbs;
        }

        // Normal blocks
        int solidMask = GetSolidMask(block, null);

        if (solidMask == 63)
            return baseAbsorption;

        if (solidMask == 0)
            return baseAbsorption;

        BlockFacing incoming = dir.Opposite;
        BlockFacing outgoing = dir;

        bool incomingSolid = (pos != null && readBlockAccess != null)
            ? block.SideIsSolid(readBlockAccess, pos, incoming.Index)
            : block.SideSolid[incoming.Index];

        bool outgoingSolid = (pos != null && readBlockAccess != null)
            ? block.SideIsSolid(readBlockAccess, pos, outgoing.Index)
            : block.SideSolid[outgoing.Index];

        if (incomingSolid) return baseAbsorption;
        if (outgoingSolid) return baseAbsorption;

        float clampedAbs = Math.Min(baseAbsorption, 32);
        return incomingEnergy * clampedAbs / 64f;
    }

    // ─── Chunk helpers ───────────────────────────────────────────────────

    /// <summary>Returns the unpacked chunk containing the given world position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IWorldChunk GetChunkAtPos(int posX, int posY, int posZ)
    {
        return chunkProvider.GetUnpackedChunkFast(
            posX / chunkSize, posY / chunkSize, posZ / chunkSize,
            notRecentlyAccessed: true);
    }

    /// <summary>Converts world coordinates to a flat chunk-local index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int InChunkIndex(int posX, int posY, int posZ)
    {
        return (posY % chunkSize * chunkSize + posZ % chunkSize) * chunkSize + posX % chunkSize;
    }

    // ─── Dirty-batch internals ───────────────────────────────────────────

    /// <summary>Enqueues (or merges) a dirty sphere for the next flush.</summary>
    private void QueueDirtyLightSphere(int posX, int posY, int posZ, int lightRadius)
    {
        if (lightRadius <= 0)
            return;

        int radius = lightRadius + DIRTY_RADIUS_PADDING;
        long key = PackPos(posX, posY, posZ);

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
    /// Scans all chunks whose light sources can intersect the dirty region
    /// and populates the nearby-source arrays.
    /// </summary>
    private void LoadSourcesIntersectingDirtySpheres(List<DirtyLightSphere> spheres)
    {
        nearbyCount = 0;
        nearbySourceIndexByPosition.Clear();

        if (spheres.Count == 0 || blockTypes == null || chunkProvider == null)
            return;

        // Build one conservative AABB around all dirty spheres
        int minX = mapsizex - 1, minY = mapsizey - 1, minZ = mapsizez - 1;
        int maxX = 0, maxY = 0, maxZ = 0;

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
                    IWorldChunk chunk = chunkProvider.GetChunk(chunkX, chunkY, chunkZ);
                    if (chunk == null) continue;

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
                            tmpPos.Set(sourceX, sourceY, sourceZ));

                        if (hsv == null || hsv.Length < 3 || hsv[2] <= 0)
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

    /// <summary>Adds a source to the nearby arrays if not already present.</summary>
    private bool TryAddNearbyLightSource(
        int posX, int posY, int posZ,
        byte hue, byte saturation, byte brightness)
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
            new NearbyLightSourceStruct { posX = posX, posY = posY, posZ = posZ };

        nearbyH[sourceIndex] = hue;
        nearbyS[sourceIndex] = saturation;
        nearbyB[sourceIndex] = brightness;

        nearbySourceIndexByPosition.Add(positionKey, sourceIndex);

        return true;
    }

    /// <summary>
    /// Calculates the packed block-light value from all source contributions
    /// accumulated in the staging dictionary for one block.
    ///
    /// Pure with respect to the current light state — does not touch
    /// chunk.Lighting. The expensive ray-tracing phase can finish while
    /// the world still displays the previous valid lighting.
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

        // Weighted HSV→RGB mixing across all contributing sources
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
    /// Commits the fully calculated staging result into chunk.Lighting.
    ///
    /// This is the ONLY method in the batched block-light path that writes
    /// new BlockLight values into the live world state.
    ///
    /// Blocks in the dirty region but absent from visitedNodes receive zero
    /// light — this correctly handles source removal.
    /// </summary>
    private void CommitDirtyLightCells(
        FastSetOfLongs touchedChunks,
        Dictionary<long, IWorldChunk> modifiedChunks)
    {
        int num = chunkSize;

        foreach (long key in dirtyLightCells)
        {
            UnpackPos(key, out int x, out int y, out int z);

            IWorldChunk chunk = chunkProvider.GetUnpackedChunkFast(
                x / num, y / num, z / num, notRecentlyAccessed: true);

            if (chunk == null)
                continue;

            int index3d = (y % num * num + z % num) * num + x % num;

            // No contribution → light must disappear
            int newLight = 0;

            if (visitedNodes.TryGetValue(key, out LightSourcesAtBlock lsab))
                newLight = CalculatePackedLight(lsab);

            int oldLight = chunk.Lighting.GetBlocklight(index3d);

            // Skip unchanged cells to avoid unnecessary chunk invalidation
            if (oldLight == newLight)
                continue;

            chunk.Lighting.SetBlocklight(index3d, newLight);

            long chunkKey = chunkProvider.ChunkIndex3D(x / num, y / num, z / num);
            touchedChunks.Add(chunkKey);
            modifiedChunks.TryAdd(chunkKey, chunk);
        }
    }

    /// <summary>
    /// Builds the exact set of dirty cells (packed keys) from all dirty spheres.
    /// Uses sphere-equation clipping per axis for a tight fit.
    /// </summary>
    private void BuildDirtyLightCellSet(List<DirtyLightSphere> spheres)
    {
        dirtyLightCells.Clear();

        for (int si = 0; si < spheres.Count; si++)
        {
            DirtyLightSphere sphere = spheres[si];

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

                    int zRadius = (int)Math.Sqrt(radiusSquared - xySquared);

                    int minZ = Math.Max(0, sphere.Z - zRadius);
                    int maxZ = Math.Min(mapsizez - 1, sphere.Z + zRadius);

                    for (int z = minZ; z <= maxZ; z++)
                        dirtyLightCells.Add(PackPos(x, y, z));
                }
            }
        }
    }

    /// <summary>
    /// Calculates and commits one complete block-light batch.
    ///
    /// Pipeline:
    /// 1. Detach pending dirty spheres.
    /// 2. Build the exact dirty-cell union.
    /// 3. Find every source that can affect that region.
    /// 4. Trace all relevant sources into visitedNodes (staging).
    /// 5. Commit the new result to chunk.Lighting in one short pass.
    ///
    /// Live Lighting arrays are NOT cleared before tracing, so the player
    /// continues to see the previous valid lighting during ray tracing.
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

            // Detach before calculating: changes arriving during this pass
            // remain queued for the next flush.
            pendingDirtySpheres.Clear();

            // 1. Build dirty-cell union (no live data changed)
            BuildDirtyLightCellSet(dirtySphereBuffer);

            if (dirtyLightCells.Count == 0)
                return touchedChunks;

            // 2. Discover all sources affecting the dirty region
            LoadSourcesIntersectingDirtySpheres(dirtySphereBuffer);

            // 3. Expensive: trace into staging (chunk.Lighting untouched)
            TraceNearbyBlockLights();

            // 4. Fast: commit the complete result
            CommitDirtyLightCells(touchedChunks, modifiedChunks);

            // Mark each modified chunk once
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

    // ─── Full relight ────────────────────────────────────────────────────

    /// <summary>
    /// Full recalculation of sunlight and block light in a cubic region.
    /// Expands by one chunk in each direction for correct boundary flow.
    /// </summary>
    public void FullRelight(BlockPos minPos, BlockPos maxPos)
    {
        int num = chunkSize;
        Dictionary<Vec3i, IWorldChunk> dictionary = new Dictionary<Vec3i, IWorldChunk>();

        // Expand region by 1 chunk for boundary correctness
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

        // Load and unpack all affected chunks
        IWorldChunk chunk;
        for (int i = num8; i <= num11; i++)
            for (int j = num9; j <= num12; j++)
                for (int k = num10; k <= num13; k++)
                {
                    chunk = chunkProvider.GetChunk(i, j + num14, k);
                    if (chunk != null)
                    {
                        chunk.Unpack();
                        dictionary[new Vec3i(i, j, k)] = chunk;
                    }
                }

        // Clear old light
        foreach (IWorldChunk value2 in dictionary.Values)
            value2?.Lighting.ClearLight();

        // Sunlight: top-down direct + horizontal flood + neighbour exchange
        IWorldChunk[] array = new IWorldChunk[mapsizey / num];

        for (int l = num8; l <= num11; l++)
        {
            for (int m = num10; m <= num13; m++)
            {
                bool flag = false;
                for (int n = 0; n < array.Length; n++)
                {
                    array[n] = chunkProvider.GetChunk(l, n + num14, m);
                    if (array[n] == null) flag = true;
                }

                if (!flag)
                {
                    Sunlight(array, l, array.Length - 1, m, minPos.dimension);
                    SunlightFlood(array, l, array.Length - 1, m);
                    SunLightFloodNeighbourChunks(array, l, array.Length - 1, m, minPos.dimension);
                }
            }
        }

        // Block light: build a bounding sphere and flush
        int centerX = (num2 + num5) / 2;
        int centerY = (num3 + num6) / 2;
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

    // ─── Sunlight ────────────────────────────────────────────────────────

    /// <summary>
    /// Direct sunlight: top-down column pass with per-block absorption.
    /// Reads the initial level from the chunk above (if any).
    /// </summary>
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

        for (int i = 0; i < num; i++)
        {
            for (int j = 0; j < num; j++)
            {
                int num5 = defaultSunLight;
                if (chunkY != chunks.Length - 1)
                    num5 = chunks[chunkY + 1].Lighting.GetSunlight(j * num + i);

                for (int num6 = chunkY; num6 >= 0; num6--)
                {
                    int num7 = ((num - 1) * num + j) * num + i;
                    IWorldChunk worldChunk = chunks[num6];
                    IChunkLight lighting = chunks[num6].Lighting;

                    for (int num8 = num - 1; num8 >= 0; num8--)
                    {
                        tmpPosDimensionAware.Set(num3 + i, num6 * num + num8, num4 + j);

                        GetBlockAndAbsorption(worldChunk, num7, tmpPosDimensionAware,
                            out Block block, out int lightAbsorptionAt, out BlockEntityMicroBlock microBE);

                        float effectiveAbs = GetEffectiveAbsorption(
                            block, lightAbsorptionAt, BlockFacing.DOWN, num5,
                            microBE, tmpPosDimensionAware);

                        if (effectiveAbs > num5)
                        {
                            int solidMask = GetSolidMask(block, microBE);

                            // Full blocks keep the incoming level on their surface
                            // so that leaves adjacent to walls don't go dark
                            if (solidMask == 63)
                                lighting.SetSunlight(num7, num5);
                            else
                                lighting.SetSunlight(num7, 0);

                            num6 = -1; // stop this column
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

    /// <summary>
    /// Horizontal sunlight propagation (BFS) inside a chunk column.
    /// Seeds the BFS from vertical gradients, then calls SpreadSunLightInColumn.
    /// </summary>
    public void SunlightFlood(IWorldChunk[] chunks, int chunkX, int chunkY, int chunkZ)
    {
        int num = chunkSize;
        Stack<FastBlockPos> stack = new Stack<FastBlockPos>();

        int num2 = chunkX * num;
        int num3 = chunkZ * num;

        for (int num4 = chunkY; num4 >= 0; num4--)
        {
            IWorldChunk worldChunk = chunks[num4];
            worldChunk.Unpack();
            IChunkLight lighting = worldChunk.Lighting;

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

                        if ((i < num - 1 && lighting.GetSunlight(num5 + XPlus) < num7) ||
                            (j < num - 1 && lighting.GetSunlight(num5 + ZPlus) < num7) ||
                            (i > 0 && lighting.GetSunlight(num5 - XPlus) < num7) ||
                            (j > 0 && lighting.GetSunlight(num5 - ZPlus) < num7))
                        {
                            stack.Push(new FastBlockPos(
                                num2 + i, num4 * num + num6, num3 + j,
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

    /// <summary>
    /// Exchanges sunlight across horizontal chunk boundaries (4 directions).
    /// Returns a bitmask of BlockFacing flags that had any light transfer.
    /// </summary>
    public byte SunLightFloodNeighbourChunks(
        IWorldChunk[] curChunks, int chunkX, int chunkY, int chunkZ, int dimension)
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

            for (int num6 = chunkY; num6 >= 0; num6--)
            {
                IWorldChunk worldChunk = array3[num6];
                IWorldChunk worldChunk2 = curChunks[num6];
                IChunkLight lighting = worldChunk.Lighting;
                IChunkLight lighting2 = worldChunk2.Lighting;

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

                        // Current → Neighbour
                        tmpPos2.Set(num2 + array2[0], num6 * num + array2[1], num3 + array2[2]);
                        tmpPos2.dimension = dimension;
                        GetBlockAndAbsorption(worldChunk2, index3d, tmpPos2,
                            out Block curBlock, out int curBaseAbs, out BlockEntityMicroBlock curMicroBE);

                        tmpPosDimensionAware.Set(num4 + num9, num6 * num + array2[1], num4 + num10);
                        GetBlockAndAbsorption(worldChunk, index3d2, tmpPosDimensionAware,
                            out Block nBlock, out int nBaseAbs, out BlockEntityMicroBlock nMicroBE);

                        float absCurToN = GetEffectiveAbsorption(
                            curBlock, curBaseAbs, dir, curLight, curMicroBE, tmpPos2);
                        int lightArrivingAtN = curLight - (int)absCurToN - 1;

                        float absNFromCur = GetEffectiveAbsorption(
                            nBlock, nBaseAbs, dir, lightArrivingAtN, nMicroBE, tmpPosDimensionAware);
                        int finalLightToN = lightArrivingAtN;
                        if (absNFromCur > lightArrivingAtN) finalLightToN = 0;

                        // Neighbour → Current
                        float absNToCur = GetEffectiveAbsorption(
                            nBlock, nBaseAbs, oppDir, nLight, nMicroBE, tmpPosDimensionAware);
                        int lightArrivingAtCur = nLight - (int)absNToCur - 1;
                        float absCurFromN = GetEffectiveAbsorption(
                            curBlock, curBaseAbs, oppDir, lightArrivingAtCur, curMicroBE, tmpPos2);
                        int finalLightToCur = lightArrivingAtCur;
                        if (absCurFromN > lightArrivingAtCur) finalLightToCur = 0;

                        if (finalLightToN > nLight)
                        {
                            lighting.SetSunlight(index3d2, finalLightToN);
                            stack2.Push(new FastBlockPos(
                                num4 + num9, num6 * num + array2[1], num4 + num10, dimension));
                            b |= blockFacing.Flag;
                        }
                        else if (finalLightToCur > curLight)
                        {
                            lighting2.SetSunlight(index3d, finalLightToCur);
                            stack.Push(new FastBlockPos(
                                num2 + array2[0], num6 * num + array2[1], num3 + array2[2], dimension));
                        }
                    }
                }
            }

            if (stack2.Count > 0)
            {
                SpreadSunLightInColumn(stack2, array3);
                for (int k = 0; k < array3.Length; k++) array3[k].MarkModified();
            }
            if (stack.Count > 0)
                SpreadSunLightInColumn(stack, curChunks);
        }
        return b;
    }

    /// <summary>
    /// Processes a list of scheduled block-light updates (e.g. from chunk
    /// loading). Registers sources and flushes the entire batch once.
    /// </summary>
    public void ProcessScheduledBlockLightUpdates(List<Vec4i> scheduledUpdates)
    {
        if (scheduledUpdates == null || scheduledUpdates.Count == 0)
            return;

        BlockPos blockPos = new BlockPos(0);

        foreach (Vec4i item in scheduledUpdates)
        {
            Block block = blockTypes[item.W];

            blockPos.SetAndCorrectDimension(item.X, item.Y, item.Z);

            byte[] hsv = block.GetLightHsv(readBlockAccess, blockPos);

            if (hsv == null || hsv.Length < 3 || hsv[2] <= 0)
                continue;

            int x = blockPos.X;
            int y = blockPos.InternalY;
            int z = blockPos.Z;

            IWorldChunk chunk = GetChunkAtPos(x, y, z);
            if (chunk == null) continue;

            int lightPosition = InChunkIndex(x, y, z);

            if (!chunk.LightPositions.Contains(lightPosition))
                chunk.LightPositions.Add(lightPosition);

            QueueDirtyLightSphere(x, y, z, hsv[2]);
        }

        FlushPendingBlockLightUpdates();
    }

    /// <summary>
    /// BFS propagation of sunlight from a stack of seed positions.
    /// Handles cross-chunk boundaries via the chunks[] array.
    /// </summary>
    private void SpreadSunLightInColumn(Stack<FastBlockPos> stack, IWorldChunk[] chunks)
    {
        int num = chunkSize;

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

            GetBlockAndAbsorption(worldChunk, index3d, tmpPos,
                out Block posBlock, out int baseAbsorption, out BlockEntityMicroBlock posMicroBE);
            int currentLight = worldChunk.Lighting.GetSunlight(index3d);

            if (currentLight <= 0) continue;

            int lastChunkY = chunkY;

            for (int i = 0; i < 6; i++)
            {
                Vec3i vec3i = BlockFacing.ALLNORMALI[i];
                int ny = pos.Y + vec3i.Y;
                int nlx = localX + vec3i.X;
                int nlz = localZ + vec3i.Z;

                if (nlx >= 0 && ny >= 0 && nlz >= 0 &&
                    nlx < num && ny < mapsizey && nlz < num)
                {
                    int nChunkY = ny >> chunkSizeLog2;
                    if (nChunkY != lastChunkY)
                    {
                        worldChunk = chunks[nChunkY];
                        lastChunkY = nChunkY;
                    }

                    int nIndex3d = ((ny & chunkSizeMask) * num + nlz) * num + nlx;
                    BlockFacing dir = BlockFacing.ALLFACES[i];

                    // tmpPos belongs to posBlock — do NOT overwrite with
                    // neighbour coords here (neighbour uses tmpPos2).
                    float effectiveAbs = GetEffectiveAbsorption(
                        posBlock, baseAbsorption, dir, currentLight, posMicroBE, tmpPos);

                    int newLight = currentLight - (int)effectiveAbs - 1;

                    if (newLight <= 0) continue;

                    // Neighbour absorption
                    tmpPos2.Set(chunkX * num + nlx, ny, chunkZ * num + nlz);
                    GetBlockAndAbsorption(worldChunk, nIndex3d, tmpPos2,
                        out Block nBlock, out int nBaseAbs, out BlockEntityMicroBlock nMicroBE);

                    float nEffectiveAbs = GetEffectiveAbsorption(
                        nBlock, nBaseAbs, dir, newLight, nMicroBE, tmpPos2);

                    int finalLight = newLight;

                    if (nEffectiveAbs > newLight)
                    {
                        int solidMask = GetSolidMask(nBlock, nMicroBE);

                        // Non-full blocks go dark; full blocks keep surface light
                        if (solidMask != 63)
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

    /// <summary>Returns the sunlight level at world coordinates.</summary>
    private int SunLightLevelAt(int posX, int posY, int posZ, bool substractAbsorb = false)
    {
        int num = chunkSize;
        IWorldChunk unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
            posX / num, posY / num, posZ / num, notRecentlyAccessed: true);
        if (unpackedChunkFast == null) return defaultSunLight;

        int index3d = (posY % num * num + posZ % num) * num + posX % num;

        if (!substractAbsorb)
            return unpackedChunkFast.Lighting.GetSunlight(index3d);

        tmpPos.Set(posX, posY, posZ);
        GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpPos,
            out _, out int abs, out _);
        return unpackedChunkFast.Lighting.GetSunlight(index3d) - abs;
    }

    /// <summary>
    /// Updates sunlight when a block's transparency changes.
    /// Recalculates a cross of 5 chunk columns from posY downward.
    /// Sunlight above the changed block remains untouched.
    /// </summary>
    public FastSetOfLongs UpdateSunLight(
        int posX, int posY, int posZ, int oldAbsorb, int newAbsorb)
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

        // Only recalculate from this chunk downward
        int startChunkY = posY >> chunkSizeLog2;

        // Gather 5 columns (cross: center + N/S/E/W)
        var columns = new List<(int cx, int cz, IWorldChunk[] chunks)>(5);

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx != 0 && dz != 0) continue;

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

        // Pass 1: extinguish + direct light + horizontal flood
        foreach (var (cx, cz, chunks) in columns)
        {
            for (int cy = startChunkY; cy >= 0; cy--)
            {
                IChunkLight lighting = chunks[cy].Lighting;
                for (int idx = 0; idx < totalBlocks; idx++)
                    lighting.SetSunlight(idx, 0);
            }

            Sunlight(chunks, cx, startChunkY, cz, dim);
            SunlightFlood(chunks, cx, startChunkY, cz);
        }

        // Pass 2: boundary exchange + mark modified
        foreach (var (cx, cz, chunks) in columns)
        {
            SunLightFloodNeighbourChunks(chunks, cx, startChunkY, cz, dim);

            for (int cy = startChunkY; cy >= 0; cy--)
            {
                touchedChunks.Add(chunkProvider.ChunkIndex3D(cx, cy + dimOffset, cz));
                chunks[cy].MarkModified();
            }
        }

        return touchedChunks;
    }

    /// <summary>
    /// Checks whether sunlight reaches the block directly from above
    /// (no opaque obstacles in the column).
    /// </summary>
    public bool IsDirectlyIlluminated(int posX, int posY, int posZ)
    {
        int num = chunkSize;
        int num2 = 0;
        int num3 = SunLightLevelAt(posX, posY, posZ);

        while (posY < mapsizey)
        {
            posY++;
            IWorldChunk unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
                posX / num, posY / num, posZ / num);
            if (unpackedChunkFast == null) break;

            int index3d = (posY % num * num + posZ % num) * num + posX % num;
            int sunlight = unpackedChunkFast.Lighting.GetSunlight(index3d);

            tmpDiPos.Set(posX, posY, posZ);
            GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpDiPos,
                out Block block, out int baseAbs, out BlockEntityMicroBlock microBE);

            num2 += (int)GetEffectiveAbsorption(
                block, baseAbs, BlockFacing.DOWN, defaultSunLight - num2, microBE, tmpDiPos);

            if (defaultSunLight - num2 < num3) return false;
            if (sunlight == defaultSunLight) return true;
            if (num3 > sunlight) return false;
        }

        return defaultSunLight - num2 == num3;
    }

    /// <summary>
    /// BFS sunlight propagation from a queue of packed positions.
    /// Used by the vanilla relight pipeline for incremental updates.
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
            int num3 = (num2 >> 24) & 0x1F;
            if (num3 == 0) continue;

            int num4 = (num2 & 0xFF) - 128 + centerPos.X;
            int num5 = ((num2 >> 8) & 0xFF) - 128 + centerPos.Y;
            int num6 = ((num2 >> 16) & 0xFF) - 128 + centerPos.Z;

            IWorldChunk unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
                num4 / num, num5 / num + centerPos.dimension * 1024, num6 / num);
            if (unpackedChunkFast == null) continue;

            int index3d = (num5 % num * num + num6 % num) * num + num4 % num;
            unpackedChunkFast.Lighting.SetSunlight_Buffered(index3d, num3);

            tmpPos.Set(num4, num5, num6);
            GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpPos,
                out Block curBlock, out int baseAbsorption, out BlockEntityMicroBlock curMicroBE);

            int num7 = ((num2 >> 29) & 7) - 1; // face we came from (skip back-propagation)

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
                if (unpackedChunkFast == null) continue;

                touchedChunks.Add(chunkProvider.ChunkIndex3D(
                    num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num));

                index3d = (num9 % num * num + num10 % num) * num + num8 % num;
                BlockFacing dir = BlockFacing.ALLFACES[i];

                float effectiveAbs = GetEffectiveAbsorption(
                    curBlock, baseAbsorption, dir, num3, curMicroBE, tmpPos);

                // No distance loss for the direct downward column
                int distLoss = ((!isDirectlyIlluminated ||
                    num8 != centerPos.X || num10 != centerPos.Z || i != 5) ? 1 : 0);

                int lightArrivingAtN = num3 - (int)effectiveAbs - distLoss;
                if (lightArrivingAtN <= 0) continue;

                tmpPos2.Set(num8, num9, num10);
                tmpPos2.dimension = centerPos.dimension;
                GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpPos2,
                    out Block nBlock, out int nBaseAbs, out BlockEntityMicroBlock nMicroBE);

                float nEffectiveAbs = GetEffectiveAbsorption(
                    nBlock, nBaseAbs, dir, lightArrivingAtN, nMicroBE, tmpPos2);

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
    /// BFS "shadow spreading": clears sunlight when an obstacle appears.
    /// Retained light (brighter than the clearing wave) is collected into
    /// retainedLightToSpread for re-propagation.
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
            int num3 = (num2 & 0xFF) - 128 + centerPos.X;
            int num4 = ((num2 >> 8) & 0xFF) - 128 + centerPos.Y;
            int num5 = ((num2 >> 16) & 0xFF) - 128 + centerPos.Z;

            IWorldChunk unpackedChunkFast = chunkProvider.GetUnpackedChunkFast(
                num3 / num, num4 / num + centerPos.dimension * 1024, num5 / num);
            if (unpackedChunkFast == null) continue;

            int index3d = (num4 % num * num + num5 % num) * num + num3 % num;
            int sunlight = unpackedChunkFast.Lighting.GetSunlight(index3d);

            if (sunlight != 0)
                fastSetOfInts.RemoveIfMatches(
                    num3 - centerPos.X, num4 - centerPos.Y, num5 - centerPos.Z, sunlight);

            unpackedChunkFast.Lighting.SetSunlight_Buffered(index3d, 0);

            tmpPos.Set(num3, num4, num5);
            GetBlockAndAbsorption(unpackedChunkFast, index3d, tmpPos,
                out Block curBlock, out int baseAbsorption, out BlockEntityMicroBlock curMicroBE);

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
                if (unpackedChunkFast == null) continue;

                touchedChunks.Add(chunkProvider.ChunkIndex3D(
                    num8 / num, num9 / num + centerPos.dimension * 1024, num10 / num));

                BlockFacing dir = BlockFacing.ALLFACES[i];
                float effectiveAbs = GetEffectiveAbsorption(
                    curBlock, baseAbsorption, dir, (num2 >> 24) & 0x1F, curMicroBE, tmpPos);

                int distLoss = 1 - ((isDirectlyIlluminated &&
                    num8 == centerPos.X && num10 == centerPos.Z && i == 5) ? 1 : 0);

                int num11 = ((num2 >> 24) & 0x1F) - (int)effectiveAbs - distLoss;
                if (num11 <= 0) continue;

                index3d = (num9 % num * num + num10 % num) * num + num8 % num;
                int sunlight2 = unpackedChunkFast.Lighting.GetSunlight(index3d);

                if (sunlight2 != 0)
                {
                    if (sunlight2 <= num11)
                        positionsToClear.EnqueueIfLarger(
                            num8 - centerPos.X, num9 - centerPos.Y, num10 - centerPos.Z,
                            num11 + (TileSideEnum.GetOpposite(i) + 1 << 5));
                    else
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
}