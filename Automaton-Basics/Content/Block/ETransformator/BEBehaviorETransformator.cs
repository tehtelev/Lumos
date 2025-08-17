using Automaton.Interface;
using Automaton.Utils;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;


namespace Automaton.Content.Block.ETransformator;

public class BEBehaviorETransformator : BlockEntityBehavior, IAutomaticTransformator
{
    float maxCurrent; //максимальный ток
    float power;      //мощность


    public const string PowerKey = "automaton:power";


    public BEBehaviorETransformator(BlockEntity blockEntity) : base(blockEntity)
    {
        maxCurrent = MyMiniLib.GetAttributeFloat(this.Block, "maxCurrent", 5.0F);
    }

    public bool IsBurned => this.Block.Variant["state"] == "burned";
    public new BlockPos Pos => this.Blockentity.Pos;

    public int highVoltage => MyMiniLib.GetAttributeInt(this.Block, "voltage", 32);

    public int lowVoltage => MyMiniLib.GetAttributeInt(this.Block, "lowVoltage", 32);



    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder stringBuilder)
    {
        base.GetBlockInfo(forPlayer, stringBuilder);

        //проверяем не сгорел ли прибор
        if (this.Api.World.BlockAccessor.GetBlockEntity(this.Blockentity.Pos) is not BlockEntityETransformator entity)
            return;

      

        if (IsBurned)
            return;

        //stringBuilder.AppendLine(StringHelper.Progressbar(getPower() / (lowVoltage * maxCurrent) * 100));
        //stringBuilder.AppendLine("└ " + Lang.Get("Power") + ": " + getPower() + " / " + lowVoltage * maxCurrent + " " + Lang.Get("W"));
        stringBuilder.AppendLine("└ " + Lang.Get("Power") + ": " + ((int)getPower()).ToString() + " " + Lang.Get("W"));
        stringBuilder.AppendLine();

        
    }


    public void Update()
    {
        //смотрим надо ли обновить модельку когда сгорает трансформатор
        if (this.Api.World.BlockAccessor.GetBlockEntity(this.Blockentity.Pos) is BlockEntityETransformator
            {
                AllAparams: not null
            } entity)
        {
           

        }

        
    }


    public float getPower()
    {
        return this.power;
    }

    public void setPower(float power)
    {
        this.power = power;
    }



    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetFloat(PowerKey, power);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        power = tree.GetFloat(PowerKey);
    }
}
