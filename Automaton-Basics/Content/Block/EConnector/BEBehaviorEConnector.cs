using Automaton.Interface;
using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Automaton.Content.Block.EConnector
{
    public class BEBehaviorEConnector : BlockEntityBehavior, IAutomaticConductor
    {
        public BEBehaviorEConnector(BlockEntity blockentity) : base(blockentity)
        {
        }

        public new BlockPos Pos => Blockentity.Pos;




        /// <summary>
        /// Обновление блока кабеля
        /// </summary>
        /// <exception cref="NotImplementedException"></exception>
        public void Update()
        {
            // Blockentity.MarkDirty();
        }


        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);

            if (this.Api.World.BlockAccessor.GetBlockEntity(this.Blockentity.Pos) is not BlockEntityEConnector entity)
                return;

           
        }
    }
}
