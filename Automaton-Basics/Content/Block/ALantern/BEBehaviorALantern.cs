using Automaton.Content.Block.EGenerator;
using Automaton.Interface;
using Automaton.Utils;
using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Automaton.Content.Block.ALantern
{
    public class BEBehaviorALantern : BlockEntityBehavior, IAutomaticProducer
    {
        public BEBehaviorALantern(BlockEntity blockentity) : base(blockentity)
        {
        }

        public new BlockPos Pos => Blockentity.Pos;

        public bool Working;
        public const string WorkingKey = "automaton:working";


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

            if (this.Api.World.BlockAccessor.GetBlockEntity(this.Blockentity.Pos) is not BlockEntityALantern entity)
                return;

           
        }


        private float PowerOrder;           // Просят столько энергии (сохраняется)
        public const string PowerOrderKey = "automaton:powerOrder";

        private float PowerGive;           // Отдаем столько энергии (сохраняется)
        public const string PowerGiveKey = "automaton:powerGive";




        /// <summary>
        /// Вызывается при выгрузке блока из мира
        /// </summary>
        public override void OnBlockUnloaded()
        {
            base.OnBlockUnloaded();

        }

        
        

        /// <inheritdoc />
        public float Produce_give()
        {
            float power = (Working) // Проверяем, включен ли блок
                ? 9999
                : 0; 

            PowerGive = power;
            return power;
        }

        /// <inheritdoc />
        public void Produce_order(float amount)
        {
            PowerOrder = amount;
        }

        /// <inheritdoc />
        public float getPowerGive() => PowerGive;

        /// <inheritdoc />
        public float getPowerOrder() => PowerOrder;

      



        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat(PowerOrderKey, PowerOrder);
            tree.SetFloat(PowerGiveKey, PowerGive);
            tree.SetBool(WorkingKey, Working);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            PowerOrder = tree.GetFloat(PowerOrderKey);
            PowerGive = tree.GetFloat(PowerGiveKey);
            Working= tree.GetBool(WorkingKey);
        }

 
    }
}
