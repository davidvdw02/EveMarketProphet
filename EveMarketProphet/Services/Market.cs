using EveMarketProphet.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveMarketProphet.Properties;
using Flurl;
using Flurl.Http;

namespace EveMarketProphet.Services
{
    public class Market
    {
        public ILookup<int, MarketOrder> OrdersByType { get; private set; }

        public static Market Instance { get; } = new Market();

        public async Task FetchOrders(List<int> regions)
        {
            if (regions == null || regions.Count == 0)
            {
                OrdersByType = Enumerable.Empty<MarketOrder>().ToLookup(o => o.TypeId);
                return;
            }

            var pageRequests = await BuildPageRequestsAsync(regions).ConfigureAwait(false);
            if (pageRequests.Count == 0)
            {
                OrdersByType = Enumerable.Empty<MarketOrder>().ToLookup(o => o.TypeId);
                return;
            }

            var stationsById = Db.Instance.StationsById;
            var ignoreNullSecStations = Settings.Default.IgnoreNullSecStations;
            var contrabandLookup = Settings.Default.IgnoreContraband
                ? new HashSet<int>(Db.Instance.ContrabandTypes.Select(x => x.typeID))
                : null;
            var targetParallelism = Math.Max(Environment.ProcessorCount * 4, 8);
            var maxDegreeOfParallelism = Math.Max(1, Math.Min(targetParallelism, pageRequests.Count));
            using (var throttler = new SemaphoreSlim(maxDegreeOfParallelism))
            {
                var fetchTasks = new Task<IReadOnlyList<MarketOrder>>[pageRequests.Count];
                for (var i = 0; i < pageRequests.Count; i++)
                {
                    fetchTasks[i] = FetchAndFilterPageAsync(
                        pageRequests[i],
                        throttler,
                        stationsById,
                        ignoreNullSecStations,
                        contrabandLookup);
                }

                var filteredBatches = await Task.WhenAll(fetchTasks).ConfigureAwait(false);

                var totalOrders = 0;
                for (var i = 0; i < filteredBatches.Length; i++)
                {
                    totalOrders += filteredBatches[i].Count;
                }

                if (totalOrders == 0)
                {
                    OrdersByType = Enumerable.Empty<MarketOrder>().ToLookup(o => o.TypeId);
                    return;
                }

                var flattened = new List<MarketOrder>(totalOrders);
                for (var i = 0; i < filteredBatches.Length; i++)
                {
                    var batch = filteredBatches[i];
                    if (batch.Count == 0)
                    {
                        continue;
                    }

                    flattened.AddRange(batch);
                }

                OrdersByType = flattened.ToLookup(o => o.TypeId); // create subsets for each item type
            }



            /*await Task.Run(() =>
            {
                var groupedByStation = Orders.ToLookup(x => x.StationID);

                foreach (var stationGroup in groupedByStation)
                {
                    var station = DB.Instance.Stations.FirstOrDefault(s => s.stationID == stationGroup.Key);
                    if (station == null) continue;

                    foreach (var order in stationGroup)
                    {
                        order.SolarSystemID = station.solarSystemID;
                        order.RegionID = station.regionID;
                        order.StationSecurity = station.security;
                    }
                }

                if(Settings.Default.IgnoreNullSecStations)
                    Orders.RemoveAll(x => x.StationSecurity <= 0.0);

                if (Settings.Default.IgnoreContraband)
                {
                    var contraband = DB.Instance.ContrabandTypes.GroupBy(x => x.typeID).Select(x => x.Key);
                    Orders.RemoveAll(x => contraband.Contains(x.TypeID));
                }
            });*/

            //OrdersByType = orders.ToLookup(o => o.TypeID); // create subsets for each item type
            //Orders.Clear();
        }


        public static double GetTaxRate()
        {
            var baseTax = 0.02; // 2% base Tax
            var reduction = 0.1; // 10% reduction per skill level
            var accounting = Settings.Default.AccountingSkill;

            return baseTax - (baseTax * (accounting*reduction));
        }

        private static async Task<List<string>> BuildPageRequestsAsync(IReadOnlyList<int> regions)
        {
            if (regions == null || regions.Count == 0)
            {
                return new List<string>();
            }

            var regionTasks = regions.Select(async regionId =>
            {
                var orderUrl = $"https://esi.evetech.net/latest/markets/{regionId}/orders/";

                try
                {
                    var head = await orderUrl.HeadAsync().ConfigureAwait(false);
                    var pagesStr = head.Headers.GetValues("x-pages").FirstOrDefault();
                    if (!int.TryParse(pagesStr, out var pages) || pages < 1)
                    {
                        pages = 1;
                    }

                    var requests = new List<string>(pages);
                    for (int i = 1; i <= pages; i++)
                    {
                        requests.Add(orderUrl.SetQueryParam("page", i));
                    }

                    return requests;
                }
                catch
                {
                    return new List<string>();
                }
            });

            var regionRequests = await Task.WhenAll(regionTasks).ConfigureAwait(false);
            return regionRequests.SelectMany(x => x).ToList();
        }

        private static async Task<IReadOnlyList<MarketOrder>> FetchAndFilterPageAsync(
            string request,
            SemaphoreSlim throttler,
            IReadOnlyDictionary<long, Station> stationsById,
            bool ignoreNullSecStations,
            HashSet<int> contrabandLookup)
        {
            await throttler.WaitAsync().ConfigureAwait(false);

            try
            {
                var result = await request.GetJsonAsync<List<MarketOrder>>().ConfigureAwait(false);
                if (result == null || result.Count == 0)
                {
                    return Array.Empty<MarketOrder>();
                }

                var filtered = new List<MarketOrder>(result.Count);
                var enforceNullSec = ignoreNullSecStations;
                var enforceContraband = contrabandLookup != null;

                foreach (var order in result)
                {
                    if (order.SystemId == 0)
                    {
                        continue;
                    }

                    if (enforceContraband && contrabandLookup.Contains(order.TypeId))
                    {
                        continue;
                    }

                    if (!stationsById.TryGetValue(order.StationId, out var station))
                    {
                        continue;
                    }

                    var security = station.security;
                    if (enforceNullSec && security <= 0.0)
                    {
                        continue;
                    }

                    order.RegionId = station.regionID;
                    order.StationSecurity = security;
                    filtered.Add(order);
                }

                return filtered;
            }
            catch
            {
                // ignored - failed pages are skipped so the remaining data can still be processed
                return Array.Empty<MarketOrder>();
            }
            finally
            {
                throttler.Release();
            }
        }
    }
}
