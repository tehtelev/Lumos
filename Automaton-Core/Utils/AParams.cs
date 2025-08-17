using System;

namespace Automaton.Utils
{
    /// <summary>
    /// Параметры проводов/приборов как участников электрической цепи
    /// </summary>
    public struct AParams : IEquatable<AParams>
    {
        public string material;     //индекс материала
        public bool[] signal;            //ток проходящий тут

        /// <summary>
        /// Конструктор для создания параметров проводника/приборов
        /// </summary>
        public AParams(
            string material,
            bool[] signal = null!
            )
        {
            this.material = material;
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
            material = "";
            signal = new bool[8] { false, false, false, false, false, false, false, false };
        }

        

        /// <summary>
        /// Проверка на равенство двух экземпляров AParams
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool Equals(AParams other)
        {
            return material == other.material;

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
                hash = hash * 31 + material.GetHashCode();
                return hash;
            }
        }

    }
}