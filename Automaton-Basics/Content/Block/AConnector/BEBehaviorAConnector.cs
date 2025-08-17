using Automaton.Interface;
using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Automaton.Content.Block.AConnector
{
    public class BEBehaviorAConnector : BlockEntityBehavior, IAutomaticConductor
    {
        public BEBehaviorAConnector(BlockEntity blockentity) : base(blockentity)
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

            if (this.Api.World.BlockAccessor.GetBlockEntity(this.Blockentity.Pos) is not BlockEntityAConnector entity)
                return;

           
        }
    }
}
