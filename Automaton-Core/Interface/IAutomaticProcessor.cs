using Automaton.Utils;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace Automaton.Interface;

public interface IAutomaticProcessor
{
    /// <summary>
    /// Координата
    /// </summary>
    public BlockPos Pos { get; }

    /// <summary>
    /// Система запрашивает у потребителя сколько ей нужно в данный момент энергии
    /// </summary>
    public int Consume_request();

    /// <summary>
    /// Система выдает энергию потребителю 
    /// </summary>
    /// <param name="amount"></param>
    public void Consume_receive(List<BusConfigurator> amount);


    /// <summary>
    /// Сколько получает в данный момент потребитель
    /// </summary>
    /// <returns></returns>
    public int GetPowerReceive();

    /// <summary>
    /// Сколько требует в данный момент потребитель
    /// </summary>
    /// <returns></returns>
    public int GetPowerRequest();

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