using HarmonyLib;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;

namespace Lumos.Core;

/// <summary>
/// Связывает каждый ванильный ChunkIlluminator с его LumosChunkIlluminator.
/// Создание происходит лениво — при первом вызове любого пропатченного метода.
/// Это решает проблему тайминга: ванильные ChunkIlluminator создаются ДО загрузки мода,
/// но наши LumosChunkIlluminator создаются при первом использовании.
/// </summary>
public static class LumosStateHolder
{
    private static readonly ConditionalWeakTable<ChunkIlluminator, LumosChunkIlluminator> _map = new();

    // Кэшированные FieldInfo для чтения приватных полей ванильного ChunkIlluminator.
    // Инициализируются один раз при загрузке класса (статический конструктор).
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

    /// <summary>
    /// Возвращает LumosChunkIlluminator для данного ванильного инстанса.
    /// Если его ещё нет — создаёт и инициализирует через reflection.
    /// Потокобезопасно: ConditionalWeakTable.GetValue атомарен.
    /// </summary>
    public static LumosChunkIlluminator GetOrCreate(ChunkIlluminator vanilla)
    {
        return _map.GetValue(vanilla, CreateLumos);
    }

    /// <summary>
    /// Пробует получить LumosChunkIlluminator без создания.
    /// Возвращает false, если инстанс ещё не был создан через GetOrCreate.
    /// </summary>
    public static bool TryGet(ChunkIlluminator vanilla, out LumosChunkIlluminator lumos)
    {
        return _map.TryGetValue(vanilla, out lumos);
    }


    /// <summary>
    /// Фабрика: создаёт LumosChunkIlluminator и копирует все поля из ванильного инстанса.
    /// Вызывается один раз на каждый ChunkIlluminator.
    /// </summary>
    private static LumosChunkIlluminator CreateLumos(ChunkIlluminator vanilla)
    {
        var lumos = new LumosChunkIlluminator();

        // 1. Читаем параметры конструктора
        var chunkProvider = (IChunkProvider)_fChunkProvider.GetValue(vanilla);
        var readBlockAccess = (IBlockAccessor)_fReadBlockAccess.GetValue(vanilla);
        var chunkSize = (int)_fChunkSize.GetValue(vanilla);

        lumos.InitFromVanillaConstructor(chunkProvider, readBlockAccess, chunkSize);

        // 2. Читаем параметры InitForWorld (могут быть ещё не установлены,
        //    если lazy init сработал очень рано — но на практике InitForWorld
        //    всегда вызывается до первого Sunlight/PlaceBlockLight)
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