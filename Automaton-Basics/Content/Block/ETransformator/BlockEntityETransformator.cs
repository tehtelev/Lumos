using Automaton.Utils;
using System.Linq;
using Vintagestory.API.Common;

namespace Automaton.Content.Block.ETransformator;

public class BlockEntityETransformator : BlockEntityABase
{
    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);

        if (this.Automaton == null || byItemStack == null)
            return;

        //задаем параметры блока/проводника
        var voltage = MyMiniLib.GetAttributeInt(byItemStack.Block, "voltage", 32);
        var lowVoltage = MyMiniLib.GetAttributeInt(byItemStack.Block, "lowVoltage", 32);
        var maxCurrent = MyMiniLib.GetAttributeFloat(byItemStack.Block, "maxCurrent", 5.0F);
        var isolated = MyMiniLib.GetAttributeBool(byItemStack.Block, "isolated", false);
        var isolatedEnvironment = MyMiniLib.GetAttributeBool(byItemStack.Block, "isolatedEnvironment", false);

        this.Automaton.Connection = Facing.DownAll;
        this.Automaton.Aparams = (
            new(BusConfigurator.None),
            FacingHelper.Faces(Facing.DownAll).First().Index);
    }
}
