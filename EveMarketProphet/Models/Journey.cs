using System.Collections.Generic;
using System.Linq;
using EveMarketProphet.Services;

namespace EveMarketProphet.Models
{
    public class Journey
    {
        public List<Trip> Legs { get; set; } = new List<Trip>();
        public List<int> Waypoints { get; set; } = new List<int>();
        public List<SolarSystem> SecurityWaypoints { get; set; } = new List<SolarSystem>();

        public double TotalProfit { get; set; }
        public double TotalCost { get; set; }
        public double TotalWeight { get; set; }
        public double MaxCost { get; set; }
        public double MaxWeight { get; set; }
        public int TotalJumps { get; set; }
        public double ProfitPerJump { get; set; }

        public int LegCount => Legs?.Count ?? 0;
        public Trip FirstLeg => Legs?.FirstOrDefault();
        public Trip LastLeg => Legs?.LastOrDefault();
        public string StartStationName => FirstLeg?.StartStationName;
        public string EndStationName => LastLeg?.EndStationName;
    }
}
