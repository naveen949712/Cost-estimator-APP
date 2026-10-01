namespace Cost_Estimator_App.Models
{
    public class InventoryItem
    {
        public string VmName { get; set; } = string.Empty;
        public string Environment { get; set; } = "Prod";      // Prod, Production, UAT, Dev, Test
        public string SkuName { get; set; } = string.Empty;     // Optional if on-prem specs provided
        public int CpuCores { get; set; } = 0;                  // On-prem vCPU
        public decimal RamGb { get; set; } = 0;                 // On-prem RAM (GB)
        public decimal OsDiskGb { get; set; } = 0;              // On-prem OS Disk (GB)
        public decimal DataDiskGb { get; set; } = 0;            // On-prem Data Disk (GB)
        public decimal StorageGb { get; set; } = 0;             // Fallback general storage (GB)
        public string Region { get; set; } = "eastus";
        public string RawOs { get; set; } = string.Empty;       // Windows, RHEL, SUSE, Ubuntu/Linux
        public string SqlEdition { get; set; } = string.Empty;  // Developer, Express, Web, Standard, Enterprise
        public int Quantity { get; set; } = 1;
    }

    public class LandingZoneConfig
    {
        public bool MasterEnable { get; set; } = false;

        // 1. Azure Firewall
        public bool EnableFirewall { get; set; } = false;
        public string FirewallTier { get; set; } = "Standard"; // Basic ($292/mo), Standard ($912.50/mo), Premium ($1277.50/mo)

        // 2. Azure VPN Gateway (Availability Zone Resilient)
        public bool EnableVpnGateway { get; set; } = false;
        public string VpnSku { get; set; } = "VpnGw1AZ"; // VpnGw1AZ, VpnGw2AZ, VpnGw3AZ

        // 3. Application Gateway v2 (Standard_v2 or WAF_v2)
        public bool EnableAppGateway { get; set; } = false;
        public string AppGwTier { get; set; } = "WAF_v2"; // Standard_v2 or WAF_v2
        public int AppGwCapacityUnits { get; set; } = 1;

        // 4. Azure Bastion
        public bool EnableBastion { get; set; } = false;
        public string BastionTier { get; set; } = "Standard"; // Basic, Standard, Developer

        // 5. Azure Backup
        public bool EnableBackup { get; set; } = false;

        // 6. Microsoft Defender for Cloud
        public bool EnableDefender { get; set; } = false;
        public string DefenderPlan { get; set; } = "Plan 2"; // Plan 1 ($5/server/mo), Plan 2 ($15/server/mo)

        // 7. Azure Key Vault
        public bool EnableKeyVault { get; set; } = false;
        public string KeyVaultTier { get; set; } = "Standard"; // Standard ($6/mo), Premium/Managed HSM ($150/mo)

        // 8. Azure DDoS Protection
        public bool EnableDdos { get; set; } = false; // DDoS Network Protection ($2,944/mo)

        // 9. Public IP Addresses
        public bool EnablePublicIps { get; set; } = false;
        public int PublicIpCount { get; set; } = 2; // ~$3.65/month each

        // 10. Private Endpoints
        public bool EnablePrivateEndpoints { get; set; } = false;
        public int PrivateEndpointCount { get; set; } = 4; // ~$7.30/month each

        // 11. NAT Gateway
        public bool EnableNatGateway { get; set; } = false;
        public int NatGatewayCount { get; set; } = 1; // ~$32.85/month each

        // 12. DNS Private Resolver
        public bool EnableDnsResolver { get; set; } = false; // ~$160.60/month baseline

        // 13. Azure Site Recovery (ASR)
        public bool EnableAsr { get; set; } = false; // ~$25.00/instance/month

        // 14. Azure Monitor (Log Analytics)
        public bool EnableMonitor { get; set; } = false;
        public decimal MonitorDataIngestionGb { get; set; } = 10m; // 5 GB free + $2.30/GB
    }

    public class CostEstimateResult
    {
        public string VmName { get; set; } = string.Empty;
        public string Environment { get; set; } = "Prod";

        // Source On-Prem Specs
        public int SourceCpu { get; set; }
        public decimal SourceRamGb { get; set; }
        public decimal SourceOsDiskGb { get; set; }
        public decimal SourceDataDiskGb { get; set; }

        // Recommended Target Specs
        public string SkuName { get; set; } = string.Empty;
        public int TargetCpu { get; set; }
        public decimal TargetRamGb { get; set; }
        public string RecommendedOsDisk { get; set; } = string.Empty;
        public string RecommendedDataDisk { get; set; } = string.Empty;

        public string Region { get; set; } = string.Empty;
        public string DetectedOs { get; set; } = string.Empty;
        public string LicenseType { get; set; } = string.Empty;
        public string SqlEdition { get; set; } = string.Empty;
        public int Quantity { get; set; }

        // Compute Only Costs
        public decimal ComputePaygMonthly { get; set; }
        public decimal ComputeReserved1YrMonthly { get; set; }
        public decimal ComputeReserved3YrMonthly { get; set; }

        // Discrete Storage Costs
        public decimal OsDiskMonthlyCost { get; set; }
        public decimal DataDiskMonthlyCost { get; set; }
        public decimal TotalStorageMonthlyCost => OsDiskMonthlyCost + DataDiskMonthlyCost;

        // Licensing Surcharges
        public decimal OsLicenseMonthlyCost { get; set; }
        public decimal SqlLicenseMonthlyCost { get; set; }
        public decimal TotalLicenseMonthlyCost => OsLicenseMonthlyCost + SqlLicenseMonthlyCost;

        // VM Totals
        public decimal TotalPaygMonthly => ComputePaygMonthly + TotalStorageMonthlyCost + TotalLicenseMonthlyCost;
        public decimal TotalReserved1YrMonthly => ComputeReserved1YrMonthly + TotalStorageMonthlyCost + TotalLicenseMonthlyCost;
        public decimal TotalReserved3YrMonthly => ComputeReserved3YrMonthly + TotalStorageMonthlyCost + TotalLicenseMonthlyCost;
    }
}