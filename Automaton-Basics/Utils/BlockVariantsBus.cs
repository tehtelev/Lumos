using Automaton.Content.Block.ABus;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Automaton.Utils;

public class BlockVariantsBus
{
    public readonly Cuboidf[] CollisionBoxes= Array.Empty<Cuboidf>();
    public readonly MeshData? MeshData;
    public readonly Cuboidf[] SelectionBoxes=Array.Empty<Cuboidf>();

    /// <summary>
    /// Извлекаем нужный вариант блока провода
    /// </summary>
    /// <param name="api"></param>
    /// <param name="baseBlock"></param>
    /// <param name="indexType"></param>
    public BlockVariantsBus(ICoreAPI api, CollectibleObject baseBlock, int indexType)
    {

        string[] t = new string[1];
        string[] v = new string[1];

        
        t[0] = "type";

        
        v[0] = BlockABus.types[indexType];

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
