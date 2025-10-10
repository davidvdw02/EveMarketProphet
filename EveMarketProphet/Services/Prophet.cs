using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using EveMarketProphet.Models;
using EveMarketProphet.Properties;

namespace EveMarketProphet.Services
{
    public class ProphetSearchOptions
    {
        public int? StartSystemIdOverride { get; set; }
        public int? DestinationSystemIdFilter { get; set; }
        public bool IgnoreSettings { get; set; }
        public ISet<(int StartSystemId, int EndSystemId)> ExcludedSystemPairs { get; set; }
        public double? MinimumProfitPerJumpOverride { get; set; }
    }

    public static class Prophet
    {
        private const double OverrideProfitPerJumpFloor = 15000d;
        private static readonly ConcurrentDictionary<(int Start, int End, bool HighSec), List<int>> RouteCache = new ConcurrentDictionary<(int Start, int End, bool HighSec), List<int>>();
        private static readonly ConcurrentDictionary<(int Start, int End, bool HighSec), byte> UnreachableRoutes = new ConcurrentDictionary<(int Start, int End, bool HighSec), byte>();

        private static List<int> GetRoute(int startSystemId, int endSystemId, bool isHighSec)
        {
            var key = (startSystemId, endSystemId, isHighSec);

            if (UnreachableRoutes.ContainsKey(key))
            {
                return null;
            }

            if (RouteCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var route = Map.Instance.FindRoute(startSystemId, endSystemId, isHighSec);
            if (route == null)
            {
                UnreachableRoutes.TryAdd(key, 0);
                return null;
            }

            RouteCache.TryAdd(key, route);
            return route;
        }

        public static List<Trip> FindTradeRoutes()
        {
            return FindTradeRoutes(new ProphetSearchOptions());
        }

        public static List<Trip> FindTradeRoutes(ProphetSearchOptions options)
        {
            options ??= new ProphetSearchOptions();

            if (Market.Instance.OrdersByType == null) return null;
            if (Market.Instance.OrdersByType.Count == 0) return null;

            var db = Db.Instance;
            var typesById = db.TypesById;
            var stationsById = db.StationsById;
            var solarSystemsById = db.SolarSystemsById;

            var ignoreSettings = options.IgnoreSettings;
            var minBaseProfit = ignoreSettings ? 0 : Settings.Default.MinBaseProfit;
            var minProfitPerJump = options.MinimumProfitPerJumpOverride ?? (ignoreSettings ? OverrideProfitPerJumpFloor : Settings.Default.MinProfitPerJump);

            var startSystemOverride = options.StartSystemIdOverride;
            var destinationSystemFilter = options.DestinationSystemIdFilter;
            var excludedPairs = options.ExcludedSystemPairs;

            var hasStartFilter = startSystemOverride.HasValue;
            var hasDestinationFilter = destinationSystemFilter.HasValue;
            var hasExcludedPairs = excludedPairs != null && excludedPairs.Count > 0;
            var startSystemFilterId = startSystemOverride.GetValueOrDefault();
            var destinationSystemFilterId = destinationSystemFilter.GetValueOrDefault();

            if (ignoreSettings && minProfitPerJump < OverrideProfitPerJumpFloor)
            {
                minProfitPerJump = OverrideProfitPerJumpFloor;
            }

            var minFillerProfit = ignoreSettings ? 0 : Settings.Default.MinFillerProfit;
            var capital = ignoreSettings ? long.MaxValue : Settings.Default.Capital;
            var maxCargo = ignoreSettings ? double.MaxValue : Settings.Default.MaxCargo;
            var requireHighSec = !ignoreSettings && Settings.Default.IsHighSec;

            var profitableTx = new ConcurrentBag<Transaction>();

            Parallel.ForEach(Market.Instance.OrdersByType, orderGroup =>
            {
                var orders = orderGroup.ToList();

                if (!typesById.TryGetValue(orderGroup.Key, out var typeInfo))
                    return;

                foreach (var order in orders)
                {
                    order.TypeVolume = typeInfo.volume;
                    order.TypeName = typeInfo.typeName;
                }

                var sellOrders = orders.Where(o => !o.Bid && o.Price > 0).OrderBy(o => o.Price).ToList();
                var buyOrders = orders.Where(o => o.Bid && o.Price > 0).OrderByDescending(o => o.Price).ToList();

                if (sellOrders.Count == 0 || buyOrders.Count == 0)
                    return;

                foreach (var sellOrder in sellOrders)
                {
                    if (hasStartFilter && sellOrder.SystemId != startSystemFilterId)
                        continue;

                    foreach (var buyOrder in buyOrders)
                    {
                        if (hasDestinationFilter && buyOrder.SystemId != destinationSystemFilterId)
                            continue;

                        if (hasExcludedPairs && excludedPairs.Contains((sellOrder.SystemId, buyOrder.SystemId)))
                            continue;

                        if (sellOrder.Price >= buyOrder.Price)
                            break;

                        var tx = new Transaction(sellOrder, buyOrder);

                        if (requireHighSec && tx.SellOrder.StationSecurity < 0.5)
                            continue;

                        if (tx.Profit < minBaseProfit)
                            continue;

                        profitableTx.Add(tx);
                    }
                }
            });

            if (profitableTx.IsEmpty)
                return null;

            var playerSystemId = startSystemOverride ?? Auth.Instance.GetLocation();
            if (playerSystemId == 0)
                playerSystemId = Settings.Default.DefaultLocation;

            var stationPairGroups = profitableTx
                .GroupBy(x => new { x.StartStationId, x.EndStationId })
                .Select(group => group.OrderByDescending(x => x.Profit).ToList())
                .Where(group =>
                {
                    var firstTx = group.First();

                    if (hasStartFilter && firstTx.StartSystemId != startSystemFilterId)
                        return false;

                    if (hasDestinationFilter && firstTx.EndSystemId != destinationSystemFilterId)
                        return false;

                    if (hasExcludedPairs && excludedPairs.Contains((firstTx.StartSystemId, firstTx.EndSystemId)))
                        return false;

                    return true;
                });

            var trips = new ConcurrentBag<Trip>();

            Parallel.ForEach(stationPairGroups, txGroup =>
            {
                var selectedTx = new List<Transaction>();
                var firstTx = txGroup.First();
                var waypoints = GetRoute(firstTx.StartSystemId, firstTx.EndSystemId, requireHighSec);

                if (waypoints == null)
                    return;

                var isk = capital;
                var vol = maxCargo;

                foreach (var typeGroup in txGroup.GroupBy(x => x.TypeId))
                {
                    var buyOrders = typeGroup
                        .Select(x => x.BuyOrder)
                        .Where(o => !hasDestinationFilter || o.SystemId == destinationSystemFilterId)
                        .OrderByDescending(x => x.Price)
                        .GroupBy(x => x.OrderId)
                        .Select(x => x.First())
                        .ToList();

                    var sellOrders = typeGroup
                        .Select(x => x.SellOrder)
                        .Where(o => !hasStartFilter || o.SystemId == startSystemFilterId)
                        .OrderBy(x => x.Price)
                        .GroupBy(x => x.OrderId)
                        .Select(x => x.First())
                        .ToList();

                    if (buyOrders.Count == 0 || sellOrders.Count == 0)
                        continue;

                    var tracker = sellOrders.ToDictionary(x => x.OrderId, x => x.VolumeRemaining);

                    foreach (var buyOrder in buyOrders)
                    {
                        var quantityToFill = Math.Max(buyOrder.MinVolume, buyOrder.VolumeRemaining);
                        var temp = new List<Transaction>();

                        foreach (var sellOrder in sellOrders)
                        {
                            if (hasExcludedPairs && excludedPairs.Contains((sellOrder.SystemId, buyOrder.SystemId)))
                                continue;

                            if (tracker[sellOrder.OrderId] <= 0)
                                continue;

                            if (sellOrder.Price <= 0)
                                continue;

                            var quantity = Math.Min(tracker[sellOrder.OrderId], quantityToFill);
                            var partialTx = new Transaction(sellOrder, buyOrder, quantity)
                            {
                                Waypoints = waypoints,
                                Jumps = waypoints.Count
                            };
                            partialTx.ProfitPerJump = partialTx.Jumps > 0
                                ? partialTx.Profit / (double)partialTx.Jumps
                                : partialTx.Profit;

                            if (partialTx.ProfitPerJump < minProfitPerJump)
                                continue;

                            if (partialTx.Cost <= isk && partialTx.Weight <= vol)
                            {
                                quantityToFill -= partialTx.Quantity;
                                isk -= partialTx.Cost;
                                vol -= partialTx.Weight;
                                temp.Add(partialTx);
                            }
                            else
                            {
                                var quantityIsk = 0;
                                if (sellOrder.Price > 0)
                                {
                                    var maxAffordable = isk / sellOrder.Price;
                                    quantityIsk = (int)System.Math.Min(int.MaxValue, maxAffordable);
                                }
                                var quantityWeight = sellOrder.TypeVolume > 0 ? (int)(vol / sellOrder.TypeVolume) : 0;
                                var quantityPart = Math.Min(quantityIsk, quantityWeight);

                                if (quantityPart > 0)
                                {
                                    var partTx = new Transaction(sellOrder, buyOrder, quantityPart)
                                    {
                                        Waypoints = waypoints,
                                        Jumps = waypoints.Count
                                    };
                                    partTx.ProfitPerJump = partTx.Jumps > 0
                                        ? partTx.Profit / (double)partTx.Jumps
                                        : partTx.Profit;

                                    if (partTx.ProfitPerJump < minProfitPerJump)
                                        continue;

                                    if (partTx.Profit > minFillerProfit)
                                    {
                                        quantityToFill -= partTx.Quantity;
                                        isk -= partTx.Cost;
                                        vol -= partTx.Weight;
                                        temp.Add(partTx);
                                    }
                                }
                            }

                            if (quantityToFill == 0)
                                break;
                        }

                        if (quantityToFill == 0 || quantityToFill > buyOrder.MinVolume)
                        {
                            selectedTx.AddRange(temp);

                            foreach (var t in temp)
                            {
                                tracker[t.SellOrder.OrderId] -= t.Quantity;
                            }
                        }
                        else
                        {
                            foreach (var t in temp)
                            {
                                isk += t.Cost;
                                vol += t.Weight;
                            }
                        }
                    }
                }

                if (selectedTx.Count > 0)
                {
                    var trip = new Trip
                    {
                        Transactions = selectedTx,
                        Profit = selectedTx.Sum(x => x.Profit),
                        Cost = selectedTx.Sum(x => x.Cost),
                        Weight = selectedTx.Sum(x => x.Weight)
                    };

                    var approachRoute = GetRoute(playerSystemId, trip.Transactions.First().StartSystemId, requireHighSec);
                    if (approachRoute != null)
                    {
                        trip.Waypoints = approachRoute;
                        trip.Jumps = trip.Waypoints.Count + trip.Transactions.First().Jumps;
                        trip.ProfitPerJump = trip.Jumps > 0 ? trip.Profit / (double)trip.Jumps : trip.Profit;

                        if (stationsById.TryGetValue(trip.Transactions.First().StartStationId, out var startStation) &&
                            stationsById.TryGetValue(trip.Transactions.First().EndStationId, out var endStation))
                        {
                            foreach (var t in trip.Transactions)
                            {
                                t.SellOrder.StationName = startStation.stationName;
                                t.BuyOrder.StationName = endStation.stationName;
                            }
                        }

                        trip.SecurityWaypoints = new List<SolarSystem>();
                        foreach (var systemId in trip.Waypoints.Concat(trip.Transactions.First().Waypoints))
                        {
                            if (solarSystemsById.TryGetValue(systemId, out var system))
                            {
                                trip.SecurityWaypoints.Add(system);
                            }
                        }

                        if (trip.ProfitPerJump >= minProfitPerJump)
                        {
                            trips.Add(trip);
                        }
                    }
                }
            });

            var orderedTrips = trips.ToList();
            return orderedTrips.OrderByDescending(x => x.ProfitPerJump).ToList();
        }
    }
}
