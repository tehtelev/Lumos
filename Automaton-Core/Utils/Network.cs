using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Automaton.Interface;

namespace Automaton.Utils
{
    /// <summary>
    /// Сеть
    /// </summary>
    public class Network
    {
        public readonly HashSet<IAutomaticProcessor> Processors = new();  //Аккумуляторы
        public readonly HashSet<IAutomaticConsumer> Consumers = new();       //Потребители
        public readonly HashSet<IAutomaticConductor> Conductors = new();       //Проводники
        public readonly HashSet<IAutomaticProducer> Producers = new();           //Генераторы
        public readonly HashSet<IAutomaticTransformator> Transformators = new();  //Трансформаторы
        public readonly HashSet<BlockPos> PartPositions = new();     //Координаты позиций сети
        public float Consumption; //Потребление
        public float Capacity;    //Емкость батарей
        public float MaxCapacity; //Максимальная емкость батарей
        public float Production;  //Генерация
        public float Request;     //Необходимость
        public int version;       //Версия сети, для отслеживания изменений

    }
}