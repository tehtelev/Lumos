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

namespace Automaton.Content.Block.ECable
{
    public class BEBehaviorECable : BlockEntityBehavior, IAutomaticConductor
    {
        public BEBehaviorECable(BlockEntity blockentity) : base(blockentity)
        {
        }

        public new BlockPos Pos => Blockentity.Pos;


        /// <summary>
        /// Подсказка при наведении на блок
        /// </summary>
        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder stringBuilder)
        {
            base.GetBlockInfo(forPlayer, stringBuilder);

            
            if (Api.World.BlockAccessor.GetBlockEntity(Blockentity.Pos) is not BlockEntityECable entity)
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
            if (Api.World.BlockAccessor.GetBlockEntity(Blockentity.Pos) is BlockEntityECable
                {
                    AllEparams: not null
                } entity)
            {
                var hasBurnout = entity.AllEparams.Any(e => e.burnout);
                if (hasBurnout)
                    ParticleManager.SpawnBlackSmoke(Api.World, Pos.ToVec3d().Add(0.1, 0, 0.1));


                bool prepareBurnout = entity.AllEparams.Any(e => e.ticksBeforeBurnout > 0);
                if (prepareBurnout)
                {
                    ParticleManager.SpawnWhiteSlowSmoke(this.Api.World, Pos.ToVec3d().Add(0.1, 0, 0.1));
                }

            }

            
        }


    }
}
