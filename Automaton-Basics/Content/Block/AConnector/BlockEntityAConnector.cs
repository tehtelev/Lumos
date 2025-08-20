using Automaton.Content.Block.ACable;
using Automaton.Utils;
using Vintagestory.API.Common;

namespace Automaton.Content.Block.AConnector;

public class BlockEntityAConnector : BlockEntityACable
{
    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);

        var electricity = this.Automaton;
        if (electricity == null || byItemStack == null)
            return;

        electricity.Connection = Facing.AllAll;
        electricity.Aparams = (new(BusConfigurator.All), 0);
        electricity.Aparams = (new(BusConfigurator.All), 1);
        electricity.Aparams = (new(BusConfigurator.All), 2);
        electricity.Aparams = (new(BusConfigurator.All), 3);
        electricity.Aparams = (new(BusConfigurator.All), 4);
        electricity.Aparams = (new(BusConfigurator.All), 5);
    }
}