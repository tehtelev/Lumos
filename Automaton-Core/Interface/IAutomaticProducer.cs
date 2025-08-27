using Vintagestory.API.MathTools;

namespace Automaton.Interface;

public interface IAutomaticProducer
{
    /// <summary>
    /// Координата
    /// </summary>
    public BlockPos Pos { get; }

    /// <summary>
    /// Система запрашивает у генератора сколько ей нужно в данный момент выдать
    /// </summary>
    /// <param name="amount"></param>
    public void Produce_order(int amount);

    /// <summary>
    /// Сколько может выдать генератор сейчас максимум
    /// </summary>
    /// <returns></returns>
    public int GetPowerGive();

    /// <summary>
    /// Сколько в данный момент просят с генератора (нагрузка)
    /// </summary>
    /// <returns></returns>
    public int GetPowerOrder();

    /// <summary>
    /// Генератор выдает энергию в систему
    /// </summary>
    public int Produce_give();

    /// <summary>
    /// Обновляем Entity
    /// </summary>
    public void Update();
}
