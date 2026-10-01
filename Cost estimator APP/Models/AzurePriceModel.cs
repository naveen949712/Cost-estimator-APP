using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Cost_Estimator_App.Models
{
    public class AzurePricePagedResponse
    {
        [JsonPropertyName("Items")]
        public List<AzurePriceItem> Items { get; set; } = new();

        [JsonPropertyName("NextPageLink")]
        public string? NextPageLink { get; set; }
    }

    public class AzurePriceItem
    {
        [JsonPropertyName("armSkuName")]
        public string ArmSkuName { get; set; } = string.Empty;

        [JsonPropertyName("armRegionName")]
        public string ArmRegionName { get; set; } = string.Empty;

        [JsonPropertyName("productName")]
        public string ProductName { get; set; } = string.Empty;

        [JsonPropertyName("skuName")]
        public string SkuName { get; set; } = string.Empty;

        [JsonPropertyName("meterName")]
        public string MeterName { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("reservationTerm")]
        public string? ReservationTerm { get; set; }

        [JsonPropertyName("retailPrice")]
        public decimal RetailPrice { get; set; }

        [JsonPropertyName("unitPrice")]
        public decimal UnitPrice { get; set; }
    }
}