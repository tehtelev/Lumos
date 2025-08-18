using Automaton.Content.Block.ACable;
using Automaton.Content.Block.EGenerator;
using Automaton.Content.Block.ETermoGenerator;
using Automaton.Interface;
using Automaton.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Automaton.Content.Block.ABus
{
    public class BEBehaviorABus : BlockEntityBehavior, IAutomaticConductor
    {
        public BEBehaviorABus(BlockEntity blockentity) : base(blockentity)
        {
        }

        public new BlockPos Pos => Blockentity.Pos;


        /// <summary>
        /// Подсказка при наведении на блок
        /// </summary>
        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder stringBuilder)
        {
            base.GetBlockInfo(forPlayer, stringBuilder);

            
            if (Api.World.BlockAccessor.GetBlockEntity(Blockentity.Pos) is not BlockEntityABus entity)
                return;



            //stringBuilder.AppendLine("Заглушка");

        }

        /// <summary>
        /// Обновление блока кабеля
        /// </summary>
        /// <exception cref="NotImplementedException"></exception>
        public void Update()
        {
            //смотрим надо ли обновить модельку когда сгорает прибор
            if (Api.World.BlockAccessor.GetBlockEntity(Blockentity.Pos) is BlockEntityABus
                {
                    AllAparams: not null
                } entity)
            {


            }

            
        }


    }
}
