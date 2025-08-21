using Automaton.Content.Block.ACable;
using Automaton.Utils;
using Vintagestory.API.Common;

namespace Automaton.Content.Block.AConsumer;

public class BlockEntityAConsumer : BlockEntityABase
{
    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);

        var behaviorAutomaton = this.Automaton;
        if (behaviorAutomaton == null || byItemStack == null)
            return;

        behaviorAutomaton.Connection = Facing.AllAll;
        behaviorAutomaton.Aparams = (new(BusConfigurator.All), 0);
        behaviorAutomaton.Aparams = (new(BusConfigurator.All), 1);
        behaviorAutomaton.Aparams = (new(BusConfigurator.All), 2);
        behaviorAutomaton.Aparams = (new(BusConfigurator.All), 3);
        behaviorAutomaton.Aparams = (new(BusConfigurator.All), 4);
        behaviorAutomaton.Aparams = (new(BusConfigurator.All), 5);
    }

    

}