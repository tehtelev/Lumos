using Automaton.Content.Block.ACable;
using Automaton.Utils;
using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace Automaton.Content.Block.ABus
{
    public class BlockEntityABus : BlockEntityABase
    {
        

        public Facing Connection  //соединение этого провода
        {
            get => this.Automaton?.Connection ?? Facing.None;
            set
            {
                if (this.Automaton != null)
                {
                    this.Automaton.Connection = value;
                }
            }
        }



        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            try
            {
               // this.switches = SerializerUtil.Deserialize<Facing>(tree.GetBytes(SwitchesKey));
               // this.orientation = SerializerUtil.Deserialize<Facing>(tree.GetBytes(OrientationKey));
            }
            catch (Exception exception)
            {
                this.Api?.Logger.Error(exception.ToString());
            }
        }
    }
}
