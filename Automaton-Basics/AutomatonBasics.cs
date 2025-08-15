using Automaton.Content.Block.EConnector;
using Automaton.Content.Block.EGenerator;
using Automaton.Content.Block.EMotor;
using Vintagestory.API.Common;
using Automaton.Content.Block;
using Automaton.Content.Block.ESwitch;
using Vintagestory.API.Client;
using Automaton.Content.Block.ECable;
using Automaton.Content.Block.ETransformator;
using Automaton.Content.Block.ETermoGenerator;
using Automaton.Content.Block.Termoplastini;
using Vintagestory.API.Config;
using System.Collections.Generic;




[assembly: ModDependency("game", "1.21.0-rc.4")]
[assembly: ModDependency("automatoncore", "0.0.1")]
[assembly: ModInfo(
    "Automaton: Basics",
    "automatonbasics",
    Website = "https://github.com/tehtelev/Automaton",
    Description = "Basic automatics devices.",
    Version = "0.0.1",
    Authors = new[] {
        "Tehtelev",
        "Kotl"
    }
)]

namespace Automaton;


public class AutomatonBasics : ModSystem
{

    private ICoreAPI api = null!;
    private ICoreClientAPI capi = null!;

    /// <summary>
    /// Причины сгорания электрических блоков
    /// </summary>
    public static Dictionary<int, string> causeBurn = new Dictionary<int, string>
    {
        { 1, Lang.Get("causeCurrent") },
        { 2, Lang.Get("causeVoltage") },
        { 3, Lang.Get("causeEnvironment") }
    };



    /// <summary>
    /// Старт общего потока
    /// </summary>
    /// <param name="api"></param>
    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        this.api = api;

        api.RegisterBlockClass("BlockECable", typeof(BlockECable));
        api.RegisterBlockEntityClass("BlockEntityECable", typeof(BlockEntityECable));
        api.RegisterBlockEntityBehaviorClass("BEBehaviorECable", typeof(BEBehaviorECable));

        api.RegisterBlockClass("BlockESwitch", typeof(BlockESwitch));



        api.RegisterBlockClass("BlockConnector", typeof(BlockConnector));
        api.RegisterBlockEntityClass("BlockEntityEConnector", typeof(BlockEntityEConnector));
        api.RegisterBlockEntityBehaviorClass("BEBehaviorEConnector", typeof(BEBehaviorEConnector));


        api.RegisterBlockClass("BlockETransformator", typeof(BlockETransformator));
        api.RegisterBlockEntityClass("BlockEntityETransformator", typeof(BlockEntityETransformator));
        api.RegisterBlockEntityBehaviorClass("BEBehaviorETransformator", typeof(BEBehaviorETransformator));



        api.RegisterBlockClass("BlockEMotor", typeof(BlockEMotor));
        api.RegisterBlockEntityClass("BlockEntityEMotor", typeof(BlockEntityEMotor));
        api.RegisterBlockEntityBehaviorClass("BEBehaviorEMotor", typeof(BEBehaviorEMotor));


        api.RegisterBlockClass("BlockEGenerator", typeof(BlockEGenerator));
        api.RegisterBlockEntityClass("BlockEntityEGenerator", typeof(BlockEntityEGenerator));
        api.RegisterBlockEntityBehaviorClass("BEBehaviorEGenerator", typeof(BEBehaviorEGenerator));


        api.RegisterBlockEntityBehaviorClass("Automaton", typeof(BEBehaviorAutomaton));


        api.RegisterBlockClass("BlockETermoGenerator", typeof(BlockETermoGenerator));
        api.RegisterBlockEntityClass("BlockEntityETermoGenerator", typeof(BlockEntityETermoGenerator));
        api.RegisterBlockEntityBehaviorClass("BEBehaviorTermoEGenerator", typeof(BEBehaviorTermoEGenerator));

        api.RegisterBlockClass("BlockTermoplastini", typeof(BlockTermoplastini));


    }






    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        this.capi = api;
    }

}