using Automaton.Utils;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Automaton.Content.Block;

public abstract class BlockEntityEBase : BlockEntity
{
    protected BEBehaviorAutomaton? Automaton => GetBehavior<BEBehaviorAutomaton>();

    /// <summary>
    /// Передает значения из Block в BEBehaviorAutomaton
    /// </summary>
    public (EParams, int) Eparams
    {
        get => this.Automaton?.Eparams ?? (new(), 0);
        set => this.Automaton!.Eparams = value;
    }

    /// <summary>
    /// Передает значения из Block в BEBehaviorAutomaton
    /// </summary>
    public EParams[]? AllEparams
    {
        get => this.Automaton?.AllEparams ?? new EParams[]
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
                this.Automaton.AllEparams = value!;
        }
    }

    public const string AllEparamsKey = "automaton:allEparams";


    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        this.Automaton?.OnBlockUnloaded(); // вызываем метод OnBlockUnloaded у BEBehaviorAutomaton
    }


 
}