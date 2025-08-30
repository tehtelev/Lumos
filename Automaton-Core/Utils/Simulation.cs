using System;
using System.Collections.Generic;

namespace Automaton.Utils
{
    public class Simulation
    {


        /// Массив для хранения всех расстояний между клиентами и магазинами
        /// </summary>
        public int[] Distances = new int[1];


        /// <summary>
        /// Буффер для хранения расстояний между клиентами и магазинами.
        /// </summary>
        public int[] DistBuffer = new int[0];
        /// <summary>
        /// Список клиентов, участвующих в симуляции.
        /// </summary>
        public Customer[] Customers { get; set; }

        /// <summary>
        /// Список магазинов, участвующих в симуляции.
        /// </summary>
        public Store[] Stores { get; set; }

        /// <summary>
        /// Запускает симуляцию распределения товара между клиентами и магазинами.
        /// </summary>
        public void Run()
        {
            // Проверяем, что списки клиентов и магазинов инициализированы
            if (Customers == null || Stores == null)
                return;

            for (int i = 0; i < Stores.Length; i++)
            {
                Stores[i].totalRequest = 0;
            }

            bool hasActiveStores;
            bool hasPendingCustomers;

            do
            {
                for (int i = 0; i < Stores.Length; i++)
                {
                    Stores[i].ResetRequests();
                }

                for (int c = 0; c < Customers.Length; c++)
                {
                    var customer = Customers[c];
                    customer.ResetStoreIndex();
                    if (customer.Remaining <= 0.001f)
                        continue;

                    int remaining = customer.Remaining;
                    int[] availableStoreIds = customer.GetAvailableStoreIds();
                    ProcessStoresArray(customer, remaining, availableStoreIds);
                }

                for (int i = 0; i < Stores.Length; i++)
                {
                    Stores[i].ProcessRequests(Customers);
                }

                hasActiveStores = false;
                for (int i = 0; i < Stores.Length; i++)
                {
                    if (!Stores[i].ImNull)
                    {
                        hasActiveStores = true;
                        break;
                    }
                }

                hasPendingCustomers = false;
                for (int i = 0; i < Customers.Length; i++)
                {
                    if (Customers[i].Remaining > 0.001f)
                    {
                        hasPendingCustomers = true;
                        break;
                    }
                }

            }
            while (hasActiveStores && hasPendingCustomers);
        }

        /// <summary>
        /// Обрабатывает массив идентификаторов магазинов для клиента, распределяя оставшееся количество товара между магазинами.
        /// </summary>
        /// <param name="customer"></param>
        /// <param name="remaining"></param>
        /// <param name="storeIds"></param>
        private void ProcessStoresArray(Customer customer, int remaining, int[] storeIds)
        {
            while (customer.HasMoreStores() && remaining > 0.001f)
            {
                int s = customer.GetNextStoreIndex();
                var store = Stores![storeIds[s]];
                if (store.Stock <= 0.001f && store.ImNull)
                    continue;

                int requested = remaining;
                store.CurrentRequests[customer.Id] = requested;
                remaining -= requested;
            }
        }

        /// <summary>
        /// Сбрасывает состояние симуляции, очищая списки клиентов и магазинов.
        /// </summary>
        public void Reset()
        {
            Stores=Array.Empty<Store>();
            Customers=Array.Empty<Customer>();
        }
    }
}