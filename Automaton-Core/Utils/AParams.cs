using System;

namespace Automaton.Utils
{
    /// <summary>
    /// Параметры проводов/приборов как участников электрической цепи
    /// </summary>
    public struct AParams : IEquatable<AParams>
    {
        public BusConfigurator configurator;     // конфигуратор шины, определяет какие биты могут быть задействованы
        public bool[] signal;                    // ток проходящий тут

        /// <summary>
        /// Конструктор для создания параметров проводника/приборов
        /// </summary>
        public AParams(
            BusConfigurator configurator,
            bool[] signal = null!
            )
        {
            this.configurator = configurator;
            if (signal == null)
            {
                this.signal = new bool[8] { false, false, false, false, false, false, false, false };
            }
            else
            {
                this.signal = signal;
            }
        }


        /// <summary>
        /// Конструктор по умолчанию для создания параметров проводника/приборов
        /// </summary>
        public AParams()
        {
            configurator = BusConfigurator.None;
            signal = new bool[8] { false, false, false, false, false, false, false, false };
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