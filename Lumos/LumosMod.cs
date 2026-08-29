using HarmonyLib;
using Lumos.Core;
using Lumos.Patches;
using System;

using Vintagestory.API.Common;

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
    /// Применяем все Harmony-патчи при старте мода.
    /// </summary>
    public override void Start(ICoreAPI api)
    {
        // Защита от двойного патчинга: Start может вызваться дважды
        // (client + server в singleplayer) без Dispose между вызовами.
        if (_harmony != null || Harmony.HasAnyPatches("lumos"))
        {
            return;
        }

        _harmony = new Harmony("lumos");

        try
        {
            // PatchAll() находит все типы в сборке с атрибутом [HarmonyPatch]
            // и применяет их
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
        // 1. Очищаем кэши
        LumosChunkIlluminator.ClearCaches();

        // 2. Очищаем кэш профилей микроблоков
        MicroblockLightCache.Clear();

        // 3. Снимаем все Harmony-патчи, чтобы игра работала как раньше
        _harmony?.UnpatchAll("lumos");
        _harmony = null;

        base.Dispose();

    }
}
