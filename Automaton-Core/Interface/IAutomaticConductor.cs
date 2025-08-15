using Vintagestory.API.MathTools;

namespace Automaton.Interface;

public interface IAutomaticConductor
{
    /// <summary>
    /// Координата проводника
    /// </summary>
    public BlockPos Pos { get; }

   

    /// <summary>
    /// Обновляем Entity
    /// </summary>
    public void Update();
}
