using System;
using System.Collections.Generic;
using EveMarketProphet.Services;

namespace EveMarketProphet.Models
{
    public class Transaction
    {
        public MarketOrder BuyOrder { get; private set; }
        public MarketOrder SellOrder { get; private set; }
        public double Profit { get; private set; }
        public int Quantity { get; private set; }

        public long StartStationId => SellOrder?.StationId ?? 0;
        public long EndStationId => BuyOrder?.StationId ?? 0;
        public int StartSystemId => SellOrder?.SystemId ?? 0;
        public int EndSystemId => BuyOrder?.SystemId ?? 0;
        public int TypeId => SellOrder?.TypeId ?? BuyOrder?.TypeId ?? 0;
        public string TypeName => SellOrder?.TypeName ?? BuyOrder?.TypeName;
        public double TypeVolume => SellOrder?.TypeVolume ?? BuyOrder?.TypeVolume ?? 0;
        public string Icon => TypeId > 0 ? $"https://image.eveonline.com/Type/{TypeId}_64.png" : string.Empty;

        public long Cost => SellOrder != null ? Quantity * SellOrder.Price : 0;
        public double Weight => SellOrder != null ? Quantity * SellOrder.TypeVolume : 0;

        public List<int> Waypoints { get; set; }
        public double ProfitPerJump { get; set; } //{ get { return Waypoints.Count > 0 ? Profit / Waypoints.Count : Profit; } }
        public int Jumps { get; set; } //=> ( Waypoints != null ? Waypoints.Count : 0 );

        public Transaction(MarketOrder sell, MarketOrder buy)
        {
            SellOrder = sell;
            BuyOrder = buy;

            Quantity = Math.Min(buy.VolumeRemaining, sell.VolumeRemaining);
            //Quantity = sell.VolumeRemaining > buy.VolumeRemaining ? buy.VolumeRemaining : sell.VolumeRemaining;
            var buyPrice = sell.Price * Quantity;
            var sellPrice = buy.Price * Quantity;
            var tax = Market.GetTaxRate();

            Profit = sellPrice - (sellPrice * tax) - buyPrice;
        }

        public Transaction(MarketOrder sell, MarketOrder buy, int quantity)
        {
            SellOrder = sell;
            BuyOrder = buy;
            Quantity = quantity;

            var buyPrice = sell.Price * quantity;
            var sellPrice = buy.Price * quantity;
            var tax = Market.GetTaxRate();

            Profit = sellPrice - (sellPrice * tax) - buyPrice;
        }
    }
}