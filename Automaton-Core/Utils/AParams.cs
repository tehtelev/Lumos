using System;

namespace Automaton.Utils
{
    /// <summary>
    /// Параметры проводов/приборов как участников электрической цепи
    /// </summary>
    public struct AParams : IEquatable<AParams>
    {
        public BusConfigurator configurator;     // конфигуратор шины, определяет какие биты могут быть задействованы в линии
        public BusConfigurator signal;           // какие сигналы активны на данной линии (битовое поле)

        /// <summary>
        /// Конструктор для создания параметров проводника/приборов
        /// </summary>
        public AParams(
            BusConfigurator configurator,
            BusConfigurator signal = BusConfigurator.None
            )
        {
            this.configurator = configurator;

            this.signal = signal;
            
        }


        /// <summary>
        /// Конструктор по умолчанию для создания параметров проводника/приборов
        /// </summary>
        public AParams()
        {
            configurator = BusConfigurator.None;
            signal = BusConfigurator.None;
        }

        

        /// <summary>
        /// Проверка на равенство двух экземпляров AParams
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool Equals(AParams other)
        {
            return configurator == other.configurator;

        }

        /// <summary>
        /// Переопределение метода Equals для сравнения объектов AParams
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public override bool Equals(object? obj)
        {
            return obj is AParams other && Equals(other);
        }


        /// <summary>
        /// Переопределение метода GetHashCode для получения хэш-кода объекта AParams
        /// </summary>
        /// <returns></returns>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + configurator.GetHashCode();
                return hash;
            }
        }

    }
}