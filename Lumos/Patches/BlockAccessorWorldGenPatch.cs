using HarmonyLib;
using Lumos.Core;
using System;
using System.Reflection;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace Lumos.Patches;

/// <summary>
/// Патч на BlockAccessorWorldGen.RunScheduledBlockLightUpdates.
/// 
/// Контекст:
/// - Класс BlockAccessorWorldGen существует ТОЛЬКО на сервере (namespace Vintagestory.Server).
/// - На клиенте типа нет, поэтому используем AccessTools.TypeByName() вместо typeof() —
///   это безопасно вернёт null на клиенте, и Harmony пропустит патч без исключения.
/// 
/// Логика:
/// 1. Достаём server из BlockAccessorWorldGen через reflection.
/// 2. Из server достаём WorldMap → chunkIlluminatorWorldGen.
/// 3. Получаем ServerMapChunk для (chunkx, chunkz).
/// 4. Достаём список отложенных обновлений света (ScheduledBlockLightUpdates).
/// 5. Передаём список в наш ProcessScheduledBlockLightUpdates.
/// 6. Обнуляем список в ServerMapChunk, как это делает ванильный код.
/// </summary>
[HarmonyPatch]
public static class BlockAccessorWorldGen_RunScheduled_Patch
{
    // Кэшированные FieldInfo/MethodInfo для производительности.
    // Reflection медленный, но мы кэшируем результаты один раз — дальше просто GetValue/Invoke.
    private static FieldInfo? _serverField;
    private static MethodInfo _worldMapField;
    private static FieldInfo? _illuminatorField;
    private static FieldInfo? _scheduledUpdatesField;
    private static MethodInfo? _getMapChunkMethod;

    private static bool _reflectionResolved;
    private static bool _reflectionFailed;

    /// <summary>
    /// Harmony вызывает TargetMethod() чтобы узнать, какой метод патчить.
    /// Возвращаем null если тип не найден (клиент) — Harmony пропустит патч.
    /// </summary>
    static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("Vintagestory.Server.BlockAccessorWorldGen");
        if (type == null)
        {
            // Клиент — типа нет, патчить нечего
            return null;
        }
        return AccessTools.Method(type, "RunScheduledBlockLightUpdates",
            new[] { typeof(int), typeof(int) });
    }

    /// <summary>
    /// Ленивая инициализация reflection-полей. Вызывается один раз при первом
    /// срабатывании патча.
    /// </summary>
    private static bool EnsureReflectionResolved(object blockAccessorInstance)
    {
        if (_reflectionResolved) return true;
        if (_reflectionFailed) return false;

        try
        {
            var bawgType = blockAccessorInstance.GetType();

            // 1. private/internal поле "server" в BlockAccessorWorldGen
            _serverField = AccessTools.Field(bawgType, "server");
            if (_serverField == null)
                throw new MissingFieldException(bawgType.FullName, "server");

            // 2. Получаем тип ServerMain для дальнейшего reflection
            var serverType = _serverField.FieldType;

            // WorldMap — это property в ServerMain
            var worldMapProperty = AccessTools.Property(serverType, "WorldMap");
            if (worldMapProperty == null)
                throw new MissingMemberException(serverType.FullName, "WorldMap");
            _worldMapField = worldMapProperty.GetGetMethod(nonPublic: true) as MethodInfo
                ?? throw new MissingMethodException(serverType.FullName, "get_WorldMap");

            // 3. chunkIlluminatorWorldGen — public поле в ServerWorldMap
            // Получаем тип возвращаемого значения get_WorldMap
            var worldMapType = worldMapProperty.PropertyType;
            _illuminatorField = AccessTools.Field(worldMapType, "chunkIlluminatorWorldGen");
            if (_illuminatorField == null)
                throw new MissingFieldException(worldMapType.FullName, "chunkIlluminatorWorldGen");

            // 4. GetMapChunk(int, int) — override метод в BlockAccessorWorldGen
            _getMapChunkMethod = AccessTools.Method(bawgType, "GetMapChunk",
                new[] { typeof(int), typeof(int) });
            if (_getMapChunkMethod == null)
                throw new MissingMethodException(bawgType.FullName, "GetMapChunk");

            // 5. ScheduledBlockLightUpdates — публичное поле в ServerMapChunk
            // Получаем тип из IMapChunk (возвращаемый тип GetMapChunk)
            var mapChunkType = _getMapChunkMethod.ReturnType;
            // Тип IMapChunk — это интерфейс, реальное поле в ServerMapChunk.
            // Ищем в типе ServerMapChunk напрямую.
            var serverMapChunkType = AccessTools.TypeByName("Vintagestory.Server.ServerMapChunk")
                ?? AccessTools.TypeByName("Vintagestory.Common.ServerMapChunk");
            if (serverMapChunkType == null)
                throw new TypeLoadException("ServerMapChunk type not found");

            _scheduledUpdatesField = AccessTools.Field(serverMapChunkType, "ScheduledBlockLightUpdates");
            if (_scheduledUpdatesField == null)
                throw new MissingFieldException(serverMapChunkType.FullName, "ScheduledBlockLightUpdates");

            _reflectionResolved = true;
            return true;
        }
        catch (Exception ex)
        {
            _reflectionFailed = true;

            return false;
        }
    }

    /// <summary>
    /// Prefix — полная замена метода RunScheduledBlockLightUpdates.
    /// Возвращаем false, чтобы оригинальный метод НЕ вызывался.
    /// </summary>
    static bool Prefix(object __instance, int chunkx, int chunkz)
    {
        // Первая инициализация reflection
        if (!EnsureReflectionResolved(__instance))
        {
            // Reflection не удался — пропускаем, даём работать ванильному коду?
            // Нет, мы же уже отменили его вызов через return false ниже.
            // Поэтому возвращаем true, чтобы ванильный код выполнился как fallback.
            // Но тогда наш prefix должен вернуть true в этом случае.
            return true; // вызвать оригинал как fallback
        }

        // 1. Достаём map chunk
        var mapChunk = _getMapChunkMethod!.Invoke(__instance, new object[] { chunkx, chunkz });
        if (mapChunk == null)
            return false; // нет чанка — ничего не делаем, пропускаем оригинал

        // 2. Достаём список отложенных обновлений
        var scheduledUpdates = _scheduledUpdatesField!.GetValue(mapChunk) as System.Collections.Generic.List<Vec4i>;
        if (scheduledUpdates == null || scheduledUpdates.Count == 0)
        {
            // Пустой список — очищаем и выходим (как ванила)
            _scheduledUpdatesField.SetValue(mapChunk, null);
            return false;
        }

        // 3. Достаём наш LumosChunkIlluminator через цепочку:
        //    __instance.server → server.WorldMap → WorldMap.chunkIlluminatorWorldGen
        try
        {
            var server = _serverField!.GetValue(__instance);
            var worldMap = ((MethodInfo)_worldMapField!).Invoke(server, null);
            var illuminator = _illuminatorField!.GetValue(worldMap) as ChunkIlluminator;

            if (illuminator == null)
            {
                // Странная ситуация — illuminator не инициализирован. Fallback к ваниле.
                return true;
            }

            // 4. Получаем нашу "тень" из хранилища
            var lumosIlluminator = LumosStateHolder.GetOrCreate(illuminator);

            // 5. Вызываем наш метод обработки отложенных обновлений
            lumosIlluminator.ProcessScheduledBlockLightUpdates(scheduledUpdates);
        }
        catch (Exception ex)
        {

        }

        // 6. Очищаем список, как это делает ванильный код
        _scheduledUpdatesField.SetValue(mapChunk, null);

        return false; // не вызываем оригинальный метод
    }
}