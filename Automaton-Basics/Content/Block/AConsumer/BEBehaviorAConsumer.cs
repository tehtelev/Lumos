using Automaton.Content.Block.EGenerator;
using Automaton.Content.Block.EMotor;
using Automaton.Interface;
using Automaton.Utils;
using System;
using System.Net;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Automaton.Content.Block.AConsumer
{
    public class BEBehaviorAConsumer : BlockEntityBehavior, IAutomaticConsumer
    {
        public BEBehaviorAConsumer(BlockEntity blockentity) : base(blockentity)
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

            if (this.Api.World.BlockAccessor.GetBlockEntity(this.Blockentity.Pos) is not BlockEntityAConsumer entity)
                return;


        }





        /// <summary>
        /// Нужно энергии (сохраняется)
        /// </summary>
        private float powerRequest;
        public const string PowerRequestKey = "automaton:powerRequest";

        /// <summary>
        /// Дали энергии  (сохраняется)
        /// </summary>
        private float powerReceive;
        public const string PowerReceiveKey = "automaton:powerReceive";



        /// <inheritdoc />
        public float Consume_request()
        {
            powerRequest = 1;
            return powerRequest;
        }

        /// <inheritdoc />
        public void Consume_receive(float amount)
        {
            var beh = this.Blockentity?.GetBehavior<BEBehaviorDoor>(); // ищем поведение двери, если есть

            if (amount >= 1)
            {
                if (beh != null && !beh.Opened)
                {
                    beh.ToggleDoorState(null, !beh.Opened); // открываем дверь, если она закрыта
                }
            }
            else if (beh != null && beh.Opened)
            {
                beh.ToggleDoorState(null, !beh.Opened); // закрываем дверь, если она открыта
            }

            powerReceive = amount;
        }


        protected BEBehaviorAutomaton? Automaton => Blockentity.GetBehavior<BEBehaviorAutomaton>();

        /// <summary>
        /// Передает значения из Block в BEBehaviorAutomaton
        /// </summary>
        public (AParams, int) Aparams
        {
            get => this.Automaton?.Aparams ?? (new(), 0);
            set => this.Automaton!.Aparams = value;
        }

        /// <summary>
        /// Передает значения из Block в BEBehaviorAutomaton
        /// </summary>
        public AParams[]? AllAparams
        {
            get => this.Automaton?.AllAparams ?? new AParams[]
            {
                new(),
                new(),
                new(),
                new(),
                new(),
                new()
            };
            set
            {
                if (this.Automaton != null)
                    this.Automaton.AllAparams = value!;
            }
        }


        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            var behaviorAutomaton = this.Automaton;
            if (behaviorAutomaton == null)
                return;

            behaviorAutomaton.Connection = Facing.AllAll;
            behaviorAutomaton.Aparams = (new(BusConfigurator.All), 0);
            behaviorAutomaton.Aparams = (new(BusConfigurator.All), 1);
            behaviorAutomaton.Aparams = (new(BusConfigurator.All), 2);
            behaviorAutomaton.Aparams = (new(BusConfigurator.All), 3);
            behaviorAutomaton.Aparams = (new(BusConfigurator.All), 4);
            behaviorAutomaton.Aparams = (new(BusConfigurator.All), 5);

            base.Initialize(api, properties);
        }


        public override void OnBlockPlaced(ItemStack byItemStack = null)
        {

            

            base.OnBlockPlaced(byItemStack);
        }

        /// <inheritdoc />
        public float getPowerReceive() => powerReceive;

        /// <inheritdoc />
        public float getPowerRequest() => powerRequest;

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat(PowerRequestKey, powerRequest);
            tree.SetFloat(PowerReceiveKey, powerReceive);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            powerRequest = tree.GetFloat(PowerRequestKey);
            powerReceive = tree.GetFloat(PowerReceiveKey);
        }




    }

}
