using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EveMarketProphet.Models;
using EveMarketProphet.Properties;

namespace EveMarketProphet.Services
{
    public static class Prophet
    {
        private static readonly ConcurrentDictionary<(int Start, int End, bool HighSec), List<int>> RouteCache = new ConcurrentDictionary<(int Start, int End, bool HighSec), List<int>>();
        private static readonly ConcurrentDictionary<(int Start, int End, bool HighSec), byte> UnreachableRoutes = new ConcurrentDictionary<(int Start, int End, bool HighSec), byte>();

        private static List<int> GetRoute(int startSystemId, int endSystemId)
        {
            var isHighSec = Settings.Default.IsHighSec;
            var key = (startSystemId, endSystemId, isHighSec);

            if (UnreachableRoutes.ContainsKey(key))
            {
                return null;
            }

            if (RouteCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var route = Map.Instance.FindRoute(startSystemId, endSystemId);
            if (route == null)
            {
                UnreachableRoutes.TryAdd(key, 0);
                return null;
            }

            RouteCache.TryAdd(key, route);
            return route;
        }

        public static RouteSearchResult FindTradeRoutes()
        {
            if (Market.Instance.OrdersByType == null) return null;
            if (Market.Instance.OrdersByType.Count == 0) return null;

            var db = Db.Instance;
            var typesById = db.TypesById;
            var stationsById = db.StationsById;
            var solarSystemsById = db.SolarSystemsById;

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
                    foreach (var buyOrder in buyOrders)
                    {
                        if (sellOrder.Price >= buyOrder.Price)
                            break;

                        var tx = new Transaction(sellOrder, buyOrder);

                        if (Settings.Default.IsHighSec && tx.SellOrder.StationSecurity < 0.5)
                            continue;

                        if (tx.Profit < Settings.Default.MinBaseProfit)
                            continue;

                        profitableTx.Add(tx);
                    }
                }
            });

            if (profitableTx.IsEmpty)
                return null;

            var playerSystemId = Auth.Instance.GetLocation();
            if (playerSystemId == 0)
                playerSystemId = Settings.Default.DefaultLocation;

            var stationPairGroups = profitableTx
                .GroupBy(x => new { x.StartStationId, x.EndStationId })
                .Select(group => group.OrderByDescending(x => x.Profit).ToList());

            var trips = new ConcurrentBag<Trip>();

            Parallel.ForEach(stationPairGroups, txGroup =>
            {
                var selectedTx = new List<Transaction>();
                var firstTx = txGroup.First();
                var waypoints = GetRoute(firstTx.StartSystemId, firstTx.EndSystemId);

                if (waypoints == null)
                    return;

                var isk = Settings.Default.Capital;
                var vol = Settings.Default.MaxCargo;

                var types = txGroup.GroupBy(x => x.TypeId).Select(x => x.Key);

                foreach (var typeId in types)
                {
                    var buyOrders = txGroup.Where(x => x.TypeId == typeId)
                        .Select(x => x.BuyOrder)
                        .OrderByDescending(x => x.Price)
                        .GroupBy(x => x.OrderId)
                        .Select(x => x.First())
                        .ToList();

                    var sellOrders = txGroup.Where(x => x.TypeId == typeId)
                        .Select(x => x.SellOrder)
                        .OrderBy(x => x.Price)
                        .GroupBy(x => x.OrderId)
                        .Select(x => x.First())
                        .ToList();

                    var tracker = sellOrders
                        .GroupBy(x => x.OrderId)
                        .Select(x => x.First())
                        .ToDictionary(x => x.OrderId, x => x.VolumeRemaining);

                    foreach (var buyOrder in buyOrders)
                    {
                        var quantityToFill = Math.Max(buyOrder.MinVolume, buyOrder.VolumeRemaining);
                        var temp = new List<Transaction>();

                        foreach (var sellOrder in sellOrders)
                        {
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

                            if (partialTx.ProfitPerJump < Settings.Default.MinProfitPerJump)
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
                                var quantityIsk = sellOrder.Price > 0 ? (int)(isk / sellOrder.Price) : 0;
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

                                    if (partTx.ProfitPerJump < Settings.Default.MinProfitPerJump)
                                        continue;

                                    if (partTx.Profit > Settings.Default.MinFillerProfit)
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

                    var approachRoute = GetRoute(playerSystemId, trip.Transactions.First().StartSystemId);
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

                        if (trip.ProfitPerJump >= Settings.Default.MinProfitPerJump)
                        {
                            trips.Add(trip);
                        }
                    }
                }
            });

            var orderedTrips = trips.ToList();
            orderedTrips = orderedTrips.OrderByDescending(x => x.ProfitPerJump).ToList();

            var journeys = BuildJourneys(orderedTrips, solarSystemsById);

            return new RouteSearchResult
            {
                Trips = orderedTrips,
                Journeys = journeys
            };
        }

        private static List<Journey> BuildJourneys(List<Trip> trips, IDictionary<int, SolarSystem> solarSystemsById)
        {
            const int maxLegs = 3;
            var journeys = new List<Journey>();

            if (trips == null || trips.Count == 0)
                return journeys;

            var tripsByStart = trips
                .Where(t => t != null)
                .GroupBy(t => t.StartSystemId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var seen = new HashSet<string>();

            foreach (var trip in trips)
            {
                if (trip == null)
                    continue;

                var path = new List<Trip> { trip };
                ExploreJourneys(path, tripsByStart, journeys, seen, solarSystemsById, maxLegs);
            }

            return journeys
                .OrderByDescending(j => j.ProfitPerJump)
                .ThenByDescending(j => j.TotalProfit)
                .ToList();
        }

        private static void ExploreJourneys(List<Trip> path,
            IDictionary<int, List<Trip>> tripsByStart,
            List<Journey> journeys,
            HashSet<string> seen,
            IDictionary<int, SolarSystem> solarSystemsById,
            int maxLegs)
        {
            if (path.Count >= 2)
            {
                var key = string.Join("|", path.Select(p => RuntimeHelpers.GetHashCode(p)));
                if (!seen.Contains(key))
                {
                    var journey = CreateJourney(path, solarSystemsById);
                    if (journey != null)
                    {
                        journeys.Add(journey);
                        seen.Add(key);
                    }
                }
            }

            if (path.Count >= maxLegs)
                return;

            var current = path[path.Count - 1];
            if (!tripsByStart.TryGetValue(current.EndSystemId, out var nextOptions))
                return;

            foreach (var next in nextOptions)
            {
                if (path.Contains(next))
                    continue;

                var transition = GetRoute(current.EndSystemId, next.StartSystemId);
                if (transition == null)
                    continue;

                path.Add(next);
                ExploreJourneys(path, tripsByStart, journeys, seen, solarSystemsById, maxLegs);
                path.RemoveAt(path.Count - 1);
            }
        }

        private static Journey CreateJourney(IEnumerable<Trip> legs, IDictionary<int, SolarSystem> solarSystemsById)
        {
            var legList = legs?.ToList();
            if (legList == null || legList.Count < 2)
                return null;

            var firstLeg = legList[0];

            if (firstLeg.Waypoints == null || firstLeg.TradeWaypoints == null)
                return null;

            var aggregatedSystemIds = new List<int>();
            var aggregatedSecurity = new List<SolarSystem>();
            var totalJumps = 0;

            AppendRoute(firstLeg.Waypoints, aggregatedSystemIds, aggregatedSecurity, solarSystemsById, ref totalJumps);
            AppendRoute(firstLeg.TradeWaypoints, aggregatedSystemIds, aggregatedSecurity, solarSystemsById, ref totalJumps, firstLeg.TradeJumps);

            for (var i = 1; i < legList.Count; i++)
            {
                var previous = legList[i - 1];
                var current = legList[i];

                var transition = GetRoute(previous.EndSystemId, current.StartSystemId);
                if (transition == null)
                    return null;

                AppendRoute(transition, aggregatedSystemIds, aggregatedSecurity, solarSystemsById, ref totalJumps);
                AppendRoute(current.TradeWaypoints, aggregatedSystemIds, aggregatedSecurity, solarSystemsById, ref totalJumps, current.TradeJumps);
            }

            var totalProfit = legList.Sum(l => l.Profit);
            var totalCost = legList.Sum(l => l.Cost);
            var totalWeight = legList.Sum(l => l.Weight);
            var maxCost = legList.Max(l => l.Cost);
            var maxWeight = legList.Max(l => l.Weight);
            var profitPerJump = totalJumps > 0 ? totalProfit / totalJumps : totalProfit;

            return new Journey
            {
                Legs = new List<Trip>(legList),
                Waypoints = aggregatedSystemIds,
                SecurityWaypoints = aggregatedSecurity,
                TotalProfit = totalProfit,
                TotalCost = totalCost,
                TotalWeight = totalWeight,
                MaxCost = maxCost,
                MaxWeight = maxWeight,
                TotalJumps = totalJumps,
                ProfitPerJump = profitPerJump
            };
        }

        private static void AppendRoute(IEnumerable<int> systemIds,
            ICollection<int> aggregatedSystemIds,
            ICollection<SolarSystem> aggregatedSecurity,
            IDictionary<int, SolarSystem> solarSystemsById,
            ref int totalJumps,
            int? explicitJumpCount = null)
        {
            if (systemIds == null)
            {
                if (explicitJumpCount.HasValue)
                {
                    totalJumps += explicitJumpCount.Value;
                }
                return;
            }

            var ids = systemIds.ToList();
            if (ids.Count == 0)
            {
                if (explicitJumpCount.HasValue)
                {
                    totalJumps += explicitJumpCount.Value;
                }
                return;
            }

            foreach (var id in ids)
            {
                aggregatedSystemIds.Add(id);
                if (solarSystemsById.TryGetValue(id, out var system))
                {
                    aggregatedSecurity.Add(system);
                }
            }

            totalJumps += explicitJumpCount ?? ids.Count;
        }
    }
}
