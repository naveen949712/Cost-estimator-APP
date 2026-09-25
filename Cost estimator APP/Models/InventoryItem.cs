namespace Cost_Estimator_App.Models
{
    public class InventoryItem
    {
        public string VmName { get; set; } = string.Empty;
        public string SkuName { get; set; } = string.Empty;     // e.g., Standard_D4s_v5
        public string Region { get; set; } = string.Empty;      // e.g., eastus
        public string RawOs { get; set; } = string.Empty;       // e.g., Windows Server 2022 or Ubuntu
        public int Quantity { get; set; } = 1;
    }

    public class CostEstimateResult
    {
        public string VmName { get; set; } = string.Empty;
        public string SkuName { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string DetectedOs { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal PaygMonthly { get; set; }
        public decimal Reserved1YrMonthly { get; set; }
        public decimal Reserved3YrMonthly { get; set; }
        public decimal Savings1YrPercent { get; set; }
        public decimal Savings3YrPercent { get; set; }
    }
}