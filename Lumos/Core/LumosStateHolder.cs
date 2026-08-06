using HarmonyLib;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;

namespace Lumos.Core;

/// <summary>
/// Связывает каждый ванильный ChunkIlluminator с его LumosChunkIlluminator.
/// Создание происходит лениво — при первом вызове любого пропатченного метода.
/// </summary>
public static class LumosStateHolder
{
    private static readonly ConditionalWeakTable<ChunkIlluminator, LumosChunkIlluminator> _map = new();

    /// <summary>Маппинг IBlockAccessor -> ChunkIlluminator для внешних триггеров (двери и т.п.).</summary>
    private static readonly ConcurrentDictionary<IBlockAccessor, ChunkIlluminator> _blockAccessMap = new();

    private static readonly System.Reflection.FieldInfo _fChunkProvider;
    private static readonly System.Reflection.FieldInfo _fReadBlockAccess;
    private static readonly System.Reflection.FieldInfo _fChunkSize;
    private static readonly System.Reflection.FieldInfo _fBlockTypes;
    private static readonly System.Reflection.FieldInfo _fDefaultSunLight;
    private static readonly System.Reflection.FieldInfo _fMapSizeX;
    private static readonly System.Reflection.FieldInfo _fMapSizeY;
    private static readonly System.Reflection.FieldInfo _fMapSizeZ;

    static LumosStateHolder()
    {
        var t = typeof(ChunkIlluminator);
        _fChunkProvider = AccessTools.Field(t, "chunkProvider");
        _fReadBlockAccess = AccessTools.Field(t, "readBlockAccess");
        _fChunkSize = AccessTools.Field(t, "chunkSize");
        _fBlockTypes = AccessTools.Field(t, "blockTypes");
        _fDefaultSunLight = AccessTools.Field(t, "defaultSunLight");
        _fMapSizeX = AccessTools.Field(t, "mapsizex");
        _fMapSizeY = AccessTools.Field(t, "mapsizey");
        _fMapSizeZ = AccessTools.Field(t, "mapsizez");
    }

    public static void RegisterBlockAccessor(IBlockAccessor blockAccess, ChunkIlluminator illuminator)
    {
        if (blockAccess != null && illuminator != null)
            _blockAccessMap.TryAdd(blockAccess, illuminator);
    }

    public static LumosChunkIlluminator GetByBlockAccessor(IBlockAccessor blockAccess)
    {
        if (blockAccess != null && _blockAccessMap.TryGetValue(blockAccess, out var vanilla))
            return GetOrCreate(vanilla);
        return null;
    }

    /// <summary>Возвращает LumosChunkIlluminator для данного ванильного инстанса, создавая при необходимости.</summary>
    public static LumosChunkIlluminator GetOrCreate(ChunkIlluminator vanilla)
    {
        return _map.GetValue(vanilla, CreateLumos);
    }

    /// <summary>Пробует получить без создания.</summary>
    public static bool TryGet(ChunkIlluminator vanilla, out LumosChunkIlluminator lumos)
    {
        return _map.TryGetValue(vanilla, out lumos);
    }

    private static LumosChunkIlluminator CreateLumos(ChunkIlluminator vanilla)
    {
        var lumos = new LumosChunkIlluminator();

        var chunkProvider = (IChunkProvider)_fChunkProvider.GetValue(vanilla);
        var readBlockAccess = (IBlockAccessor)_fReadBlockAccess.GetValue(vanilla);
        var chunkSize = (int)_fChunkSize.GetValue(vanilla);

        lumos.InitFromVanillaConstructor(chunkProvider, readBlockAccess, chunkSize);

        var blockTypes = (IList<Block>)_fBlockTypes.GetValue(vanilla);
        if (blockTypes != null)
        {
            var defaultSunLight = (ushort)_fDefaultSunLight.GetValue(vanilla);
            var mapsizex = (int)_fMapSizeX.GetValue(vanilla);
            var mapsizey = (int)_fMapSizeY.GetValue(vanilla);
            var mapsizez = (int)_fMapSizeZ.GetValue(vanilla);
            lumos.InitForWorld(blockTypes, defaultSunLight, mapsizex, mapsizey, mapsizez);
        }

        return lumos;
    }
}