using System.Collections.Generic;

namespace EveMarketProphet.Models
{
    public class RouteSearchResult
    {
        public List<Trip> Trips { get; set; } = new List<Trip>();
        public List<Journey> Journeys { get; set; } = new List<Journey>();
    }
}
