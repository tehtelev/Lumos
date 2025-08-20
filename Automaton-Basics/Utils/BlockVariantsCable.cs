using Automaton.Content.Block.ACable;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Automaton.Utils;

public class BlockVariantsCable
{
    public readonly Cuboidf[] CollisionBoxes= Array.Empty<Cuboidf>();
    public readonly MeshData? MeshData;
    public readonly Cuboidf[] SelectionBoxes=Array.Empty<Cuboidf>();

    /// <summary>
    /// Извлекаем нужный вариант блока провода
    /// </summary>
    /// <param name="api"></param>
    /// <param name="baseBlock"></param>
    /// <param name="material"></param>
    /// <param name="indexType"></param>
    public BlockVariantsCable(ICoreAPI api, CollectibleObject baseBlock, BusConfigurator bits, int indexType)
    {

        string[] t = new string[2];
        string[] v = new string[2];

        t[0] = "bit";
        t[1] = "type";

        v[0] = BlockACable.bits2Types[(int)bits];
        v[1] = BlockACable.types[indexType];

        var assetLocation = baseBlock.CodeWithVariants(t, v);
        var block = api.World.GetBlock(assetLocation);

        if (block == null)
            return;

        this.CollisionBoxes = block.CollisionBoxes;
        this.SelectionBoxes = block.SelectionBoxes;

        // Используем полученный блок для тесселяции, а не baseBlock!
        if (api is ICoreClientAPI clientApi)
        {
            var cachedShape = clientApi.TesselatorManager.GetCachedShape(block.Shape.Base);
            clientApi.Tesselator.TesselateShape(block, cachedShape, out this.MeshData);
            clientApi.TesselatorManager.ThreadDispose();  //обязательно!!
        }
    }



}
