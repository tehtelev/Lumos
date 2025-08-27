using System;
using System.Linq;
using System.Text;
using Automaton.Interface;
using Automaton.Utils;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Automaton.Content.Block.ETermoGenerator;

public class BEBehaviorTermoEGenerator : BlockEntityBehavior, IAutomaticProducer
{
    private int PowerOrder;           // Просят столько энергии (сохраняется)
    public const string PowerOrderKey = "automaton:powerOrder";

    private int PowerGive;           // Отдаем столько энергии (сохраняется)
    public const string PowerGiveKey = "automaton:powerGive";



    private bool IsBurned => false;

    

    public new BlockPos Pos => Blockentity.Pos;


    public BEBehaviorTermoEGenerator(BlockEntity blockEntity) : base(blockEntity)
    {

    }






    public void Update()
    {
        //смотрим надо ли обновить модельку когда сгорает прибор
        if (Api.World.BlockAccessor.GetBlockEntity(Blockentity.Pos) is BlockEntityETermoGenerator
            {
                AllAparams: not null
            } entity)
        {
          

        }

        //Blockentity.MarkDirty(true); //обновлять здесь уже лишнее
    }



    public int Produce_give()
    {
        BlockEntityETermoGenerator? entity = null;
        if (Blockentity is BlockEntityETermoGenerator temp)
        {
            entity = temp;
            if (temp.GenTemp > 20)
            {
                PowerGive = (int) temp.Power;
            }
            else
                PowerGive = 0;

        }

        return PowerGive;

    }



    public void Produce_order(float amount)
    {
        PowerOrder = amount;
    }



    public float getPowerGive() => PowerGive;


    public float getPowerOrder() => PowerOrder;



    /// <summary>
    /// Подсказка при наведении на блок
    /// </summary>
    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder stringBuilder)
    {
        base.GetBlockInfo(forPlayer, stringBuilder);

        if (Api.World.BlockAccessor.GetBlockEntity(Blockentity.Pos) is not BlockEntityETermoGenerator entity)
            return;

        if (IsBurned)
            return;

        stringBuilder.AppendLine(StringHelper.Progressbar(Math.Min(PowerGive, PowerOrder) / entity.Power * 100));
        stringBuilder.AppendLine("└ " + Lang.Get("Production") + ": " + ((int)Math.Min(PowerGive, PowerOrder)).ToString() + "/" + ((int)entity.Power).ToString() + " " + Lang.Get("W"));
        stringBuilder.AppendLine("└ " + Lang.Get("automatonbasics:block-termoplastini") + ": " + entity.heightTermoplastin);
        stringBuilder.AppendLine("└ " + Lang.Get("kpd") + ": " + (entity.kpd*100).ToString("F1")+ " %");
    }



    /// <summary>
    /// Сохранение параметров в дерево атрибутов
    /// </summary>
    /// <param name="tree"></param>
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetFloat(PowerOrderKey, PowerOrder);
        tree.SetFloat(PowerGiveKey, PowerGive);
    }



    /// <summary>
    /// Загрузка параметров из дерева атрибутов
    /// </summary>
    /// <param name="tree"></param>
    /// <param name="worldAccessForResolve"></param>
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        PowerOrder = tree.GetFloat(PowerOrderKey);
        PowerGive = tree.GetFloat(PowerGiveKey);
    }
}
