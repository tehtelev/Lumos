using Automaton.Utils;
using Vintagestory.API.Common;

namespace Automaton.Content.Block;

public abstract class BlockEntityABase : BlockEntity
{
    protected BEBehaviorAutomaton? Automaton => GetBehavior<BEBehaviorAutomaton>();

    /// <summary>
    /// Передает значения из Block в BEBehaviorAutomaton
    /// </summary>
    public (AParams, int) Aparams
    {
        get => this.Automaton?.Aparams ?? (new(), 0);
        set => this.Automaton!.Aparams = value;
    }

    /// <summary>
    /// Передает значения из Block в BEBehaviorAutomaton
    /// </summary>
    public AParams[]? AllAparams
    {
        get => this.Automaton?.AllAparams ?? new AParams[]
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
                this.Automaton.AllAparams = value!;
        }
    }

    public const string AllAparamsKey = "automaton:allaparams";


    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        this.Automaton?.OnBlockUnloaded(); // вызываем метод OnBlockUnloaded у BEBehaviorAutomaton
    }


 
}