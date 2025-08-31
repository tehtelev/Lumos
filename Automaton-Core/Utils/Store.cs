using System;
using System.Collections.Generic;

namespace Automaton.Utils
{
    public class Store
    {
        /// <summary>
        /// Уникальный идентификатор магазина.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Текущее количество товара в магазине.
        /// </summary>
        public int Stock { get; set; }

        /// <summary>
        /// Словарь текущих запросов от клиентов, ключ - Id клиента.
        /// </summary>
        public Dictionary<int, int> CurrentRequests { get; } = new Dictionary<int, int>();

        /// <summary>
        /// Флаг, указывающий, что магазин больше не имеет товара.
        /// </summary>
        public bool ImNull { get; private set; }

        /// <summary>
        /// Общее количество товара, запрошенного от магазина за все время.
        /// </summary>
        public int totalRequest;

        /// <summary>
        /// Инициализирует новый экземпляр класса Store.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="stock"></param>
        public Store(int id, int stock)
        {
            Id = id;
            Stock = stock;
            totalRequest = 0;
        }

        /// <summary>
        /// Сбрасывает текущие запросы от клиентов.
        /// </summary>
        public void ResetRequests()
        {
            CurrentRequests.Clear();
        }

        /// <summary>
        /// Обрабатывает запросы от клиентов и распределяет товар по запросам.
        /// </summary>
        /// <param name="customers"></param>
        public void ProcessRequests(Customer[] customers)
        {
            int totalRequested = 0;
            foreach (var req in CurrentRequests.Values)
            {
                totalRequested += req;
            }

            totalRequest += totalRequested;

            if (Stock <= 0.001f)
            {
                Stock = 0;
                ImNull = true;
                ResetRequests();
                return;
            }

            if (totalRequested == 0) return;

            if (Stock >= totalRequested)
            {
                foreach (var kvp in CurrentRequests)
                {
                    int customerId = kvp.Key;
                    int requested = kvp.Value;
                    customers[customerId].AddReceived(Id, requested);
                    Stock -= requested;
                }
            }
            else
            {
                int ratio = Stock / totalRequested; 
                foreach (var kvp in CurrentRequests)
                {
                    int customerId = kvp.Key;
                    int requested = kvp.Value;
                    int allocated = requested * ratio;
                    customers[customerId].AddReceived(Id, allocated);
                    Stock -= allocated;
                }
            }

            if (Stock <= 0)
            {
                Stock = 0;
                ImNull = true;
            }

            ResetRequests();
        }

        internal void Update(int id, int stock)
        {
            Id = id;
            Stock = stock;
            totalRequest = 0;
            ImNull = false;
        }
    }
}