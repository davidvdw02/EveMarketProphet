using System.Collections.Generic;
using System.Linq;
using EveMarketProphet.Services;

namespace EveMarketProphet.Models
{
    public class Trip
    {
        public List<Transaction> Transactions { get; set; }
        public List<int> Waypoints { get; set; }
        public List<SolarSystem> SecurityWaypoints { get; set; }
        public int Jumps { get; set; }
        public double Profit { get; set; }
        public double ProfitPerJump { get; set; }
        public double Cost { get; set; }
        public double Weight { get; set; }

        public Transaction PrimaryTransaction => Transactions?.FirstOrDefault();

        public int StartSystemId => PrimaryTransaction?.StartSystemId ?? 0;
        public int EndSystemId => PrimaryTransaction?.EndSystemId ?? 0;
        public long StartStationId => PrimaryTransaction?.StartStationId ?? 0;
        public long EndStationId => PrimaryTransaction?.EndStationId ?? 0;
        public string StartStationName => PrimaryTransaction?.SellOrder?.StationName;
        public string EndStationName => PrimaryTransaction?.BuyOrder?.StationName;

        public IReadOnlyList<int> TradeWaypoints => PrimaryTransaction?.Waypoints;
        public int TradeJumps => PrimaryTransaction?.Jumps ?? 0;
    }
}
