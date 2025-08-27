using Vintagestory.API.MathTools;
using Automaton.Interface;

namespace Automaton.Utils
{
    /// <summary>
    /// Часть сети
    /// </summary>
    public class NetworkPart
    {
        public readonly Network?[] Networks = new Network?[6];
        public AParams[] aparams = new AParams[] { };
        public readonly BlockPos Position;
        public Facing Connection = Facing.None;
        public IAutomaticProcessor? Processor;
        public IAutomaticConsumer? Consumer;
        public IAutomaticConductor? Conductor;
        public IAutomaticProducer? Producer;
        public IAutomaticTransformator? Transformator;
        public bool IsLoaded = false;

        public NetworkPart(BlockPos position)
        {
            Position = position;
        }
    }
}