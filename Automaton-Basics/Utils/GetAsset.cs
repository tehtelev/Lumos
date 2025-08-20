using Automaton.Content.Block.ABus;
using Automaton.Content.Block.ACable;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Automaton.Utils
{ 
    class GetAsset
    {
        /// <summary>
        /// Извлекаем нужный вариант блока провода
        /// </summary>
        /// <param name="api"></param>
        /// <param name="baseBlock"></param>
        /// <param name="material"></param>
        /// <param name="indexType"></param>
        public Block CableAsset(ICoreAPI api, CollectibleObject baseBlock, BusConfigurator bits, int indexType)
        {
            string[] t = new string[2];
            string[] v = new string[2];

            t[0] = "bit";
            t[1] = "type";

            v[0] = BlockACable.bits2Types[(int)bits];
            v[1] = BlockACable.types[indexType];

            var assetLocation = baseBlock.CodeWithVariants(t, v);

            return api.World.GetBlock(assetLocation);

        }



        /// <summary>
        /// Извлекаем нужный вариант блока провода
        /// </summary>
        /// <param name="api"></param>
        /// <param name="baseBlock"></param>
        /// <param name="material"></param>
        /// <param name="indexType"></param>
        public Block BusAsset(ICoreAPI api, CollectibleObject baseBlock, int indexType)
        {
            string[] t = new string[1];
            string[] v = new string[1];


            t[0] = "type";


            v[0] = BlockABus.types[indexType];

            var assetLocation = baseBlock.CodeWithVariants(t, v);

            return api.World.GetBlock(assetLocation);

        }
    }
}
