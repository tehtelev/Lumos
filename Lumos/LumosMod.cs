using HarmonyLib;
using Lumos.Core;
using Lumos.Patches;
using System;

using Vintagestory.API.Common;


[assembly: ModDependency("game", "1.22.0")]
[assembly: ModInfo(
    "Lumos",
    "lumos",
    Website = "https://github.com/tehtelev/Lumos",
    Description = "Reworking the game's lighting system and fixing related bugs",
    Version = "1.0.5",
    Authors = new[] { "Tehtelev"}
)]



namespace Lumos;

/// <summary>
/// Точка входа мода Lumos.
/// Регистрирует все Harmony-патчи, которые подменяют логику ванильного ChunkIlluminator
/// на наш LumosChunkIlluminator.
/// </summary>
public class LumosMod : ModSystem
{
    private Harmony? _harmony;

    /// <summary>
    /// Мод должен загружаться и на сервере, и на клиенте — ChunkIlluminator
    /// существует на обеих сторонах (6 экземпляров: 3 серверных + 3 клиентских).
    /// </summary>
    public override bool ShouldLoad(EnumAppSide forSide) => true;

    /// <summary>
    /// Применяем все Harmony-патчи при старте мода.
    /// </summary>
    public override void Start(ICoreAPI api)
    {
        _harmony = new Harmony("lumos");

        try
        {
            // PatchAll() находит все типы в сборке с атрибутом [HarmonyPatch]
            // и применяет их. Это включает:
            //   - ChunkIlluminatorPatches (14 патчей на методы ChunkIlluminator)
            //   - BlockAccessorWorldGenPatch (1 патч на RunScheduledBlockLightUpdates)
            _harmony.PatchAll();

            api.Logger.Notification("[Lumos] All patches applied successfully.");
        }
        catch (Exception ex)
        {
            api.Logger.Error("[Lumos] Failed to apply patches: {0}", ex.ToString());
        }
    }

    /// <summary>
    /// При выгрузке мода — снимаем все патчи, чтобы игра работала как раньше.
    /// </summary>
    public override void Dispose()
    {
        // 1. Очищаем кэши)
        LumosChunkIlluminator.ClearCaches();

        // 2. Очищаем кэш профилей микроблоков
        MicroblockLightCache.Clear();

        // 3. Снимаем все Harmony-патчи, чтобы игра работала как раньше
        _harmony?.UnpatchAll("lumos");
        _harmony = null;

        base.Dispose();

    }
}
