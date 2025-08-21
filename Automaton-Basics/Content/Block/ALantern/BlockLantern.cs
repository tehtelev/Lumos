using Automaton.Content.Block.ACable;
using Automaton.Content.Block.EGenerator;
using Automaton.Utils;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace Automaton.Content.Block.ALantern;

public class BlockLantern: BlockEBase
{
    private ICoreAPI? _coreApi;


    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        this._coreApi = api;
    }








    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (
            world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityALantern entity
        )
        {
            var variant= blockSel?.Block?.Variant["state"];
            if (variant == null)
                return false;

            var beh = entity.GetBehavior<BEBehaviorALantern>();
            if (variant == "on")
            {
                var assetLocation = this.CodeWithVariant("state","off");
                world.BlockAccessor.SetBlock(world.BlockAccessor.GetBlock(assetLocation).Id, blockSel.Position);
                if (beh != null)
                    beh.Working = false;
                entity.MarkDirty(true);
            }
            else
            {
                var assetLocation = this.CodeWithVariant("state", "on");
                world.BlockAccessor.SetBlock(world.BlockAccessor.GetBlock(assetLocation).Id, blockSel.Position);
                if (beh != null)
                    beh.Working = true;
                entity.MarkDirty(true);
            }

            return true;
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }


    public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ItemStack byItemStack = null)
    {
        base.OnBlockPlaced(world, blockPos, byItemStack);

        if (
            world.BlockAccessor.GetBlockEntity(blockPos) is BlockEntityALantern entity
        )
        {
            var variant = entity.Block.Variant["state"];
            var beh = entity.GetBehavior<BEBehaviorALantern>();
            if (variant == "on")
            {
                if (beh != null)
                    beh.Working = true;
            }
            else
            {
                if (beh != null)
                    beh.Working = false;
            }

        }

    }
}