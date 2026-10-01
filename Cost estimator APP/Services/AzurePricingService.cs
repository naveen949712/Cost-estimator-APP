using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cost_Estimator_App.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Cost_Estimator_App.Services
{
    public class AzurePricingService
    {
        private readonly HttpClient _http;
        private readonly IMemoryCache _cache;

        private record VmSkuSpec(string Series, string SkuPattern, int Cpus, decimal RamGb, bool SupportsAmd);

        // Production catalog across D, E, and M series
        private static readonly List<VmSkuSpec> ProductionCatalog = new()
        {
            // D-series v5 (General Purpose: ~4 GB / core)
            new("D", "Standard_D2s_v5", 2, 8, true),
            new("D", "Standard_D4s_v5", 4, 16, true),
            new("D", "Standard_D8s_v5", 8, 32, true),
            new("D", "Standard_D16s_v5", 16, 64, true),
            new("D", "Standard_D32s_v5", 32, 128, true),
            new("D", "Standard_D48s_v5", 48, 192, true),
            new("D", "Standard_D64s_v5", 64, 256, true),
            new("D", "Standard_D96s_v5", 96, 384, true),

            // E-series v5 (Memory Optimized: ~8 GB / core)
            new("E", "Standard_E2s_v5", 2, 16, true),
            new("E", "Standard_E4s_v5", 4, 32, true),
            new("E", "Standard_E8s_v5", 8, 64, true),
            new("E", "Standard_E16s_v5", 16, 128, true),
            new("E", "Standard_E20s_v5", 20, 160, true),
            new("E", "Standard_E32s_v5", 32, 256, true),
            new("E", "Standard_E48s_v5", 48, 384, true),
            new("E", "Standard_E64s_v5", 64, 512, true),
            new("E", "Standard_E96s_v5", 96, 672, true),
            new("E", "Standard_E104is_v5", 104, 672, false),

            // M-series (High-Memory/Large Scale)
            new("M", "Standard_M8ms", 8, 218.75m, false),
            new("M", "Standard_M16ms", 16, 437.5m, false),
            new("M", "Standard_M32ts", 32, 192, false),
            new("M", "Standard_M32ls", 32, 256, false),
            new("M", "Standard_M32ms", 32, 875, false),
            new("M", "Standard_M64ls", 64, 512, false),
            new("M", "Standard_M64s", 64, 1024, false),
            new("M", "Standard_M64ms", 64, 1792, false),
            new("M", "Standard_M128s", 128, 2048, false),
            new("M", "Standard_M128ms", 128, 3892, false)
        };

        public AzurePricingService(HttpClient http, IMemoryCache cache)
        {
            _http = http;
            _cache = cache;
        }

        public (string DisplayOs, string LicenseCategory) ClassifyOs(string rawOs)
        {
            if (string.IsNullOrWhiteSpace(rawOs)) return ("Linux", "None");
            string lower = rawOs.ToLowerInvariant();

            if (lower.Contains("win") || lower.Contains("server 20") || lower.Contains("microsoft"))
                return ("Windows", "Windows");
            if (lower.Contains("rhel") || lower.Contains("red hat") || lower.Contains("redhat"))
                return ("RHEL", "RHEL");
            if (lower.Contains("suse") || lower.Contains("sles"))
                return ("SUSE 24/7", "SUSE_247");

            return ("Linux", "None");
        }

        public string NormalizeSqlEdition(string rawSql)
        {
            if (string.IsNullOrWhiteSpace(rawSql)) return "None";
            string lower = rawSql.ToLowerInvariant();

            if (lower.Contains("dev")) return "Developer";
            if (lower.Contains("express") || lower.Contains("exp")) return "Express";
            if (lower.Contains("web")) return "Web";
            if (lower.Contains("standard") || lower.Contains("std")) return "Standard";
            if (lower.Contains("enterprise") || lower.Contains("ent")) return "Enterprise";

            return "None";
        }

        public bool IsNonProduction(string env, string vmName)
        {
            string combined = $"{env} {vmName}".ToLowerInvariant();
            return combined.Contains("dev") ||
                   combined.Contains("uat") ||
                   combined.Contains("test") ||
                   combined.Contains("qa") ||
                   combined.Contains("stage");
        }

        public (string DiskTier, decimal MonthlyCost) MapDiskTier(decimal storageGb, bool isDevOrUat)
        {
            if (storageGb <= 0) return ("-", 0m);

            if (isDevOrUat)
            {
                return storageGb switch
                {
                    <= 64 => ("E6 Standard SSD (64 GB)", 4.80m),
                    <= 128 => ("E10 Standard SSD (128 GB)", 9.60m),
                    <= 256 => ("E15 Standard SSD (256 GB)", 19.20m),
                    <= 512 => ("E20 Standard SSD (512 GB)", 38.40m),
                    <= 1024 => ("E30 Standard SSD (1 TB)", 76.80m),
                    <= 2048 => ("E40 Standard SSD (2 TB)", 153.60m),
                    _ => ("E50 Standard SSD (4 TB)", 307.20m)
                };
            }
            else
            {
                return storageGb switch
                {
                    <= 64 => ("P6 Premium SSD (64 GB)", 10.24m),
                    <= 128 => ("P10 Premium SSD (128 GB)", 19.71m),
                    <= 256 => ("P15 Premium SSD (256 GB)", 38.02m),
                    <= 512 => ("P20 Premium SSD (512 GB)", 73.22m),
                    <= 1024 => ("P30 Premium SSD (1 TB)", 135.17m),
                    <= 2048 => ("P40 Premium SSD (2 TB)", 258.87m),
                    _ => ("P50 Premium SSD (4 TB)", 493.56m)
                };
            }
        }

        public async Task<CostEstimateResult> CalculateItemCostAsync(
            InventoryItem item,
            string processorPreference,
            bool enableOsAhb,
            bool enableSqlAhb,
            int monthlyHours)
        {
            if (monthlyHours <= 0) monthlyHours = 730;

            var (displayOs, licenseCategory) = ClassifyOs(item.RawOs);
            string sqlEdition = NormalizeSqlEdition(item.SqlEdition);
            bool isDevOrUat = IsNonProduction(item.Environment, item.VmName);

            // Storage Sizing
            decimal effectiveOsDiskGb = item.OsDiskGb > 0 ? item.OsDiskGb : (item.StorageGb > 0 && item.DataDiskGb == 0 ? item.StorageGb : 0);
            decimal effectiveDataDiskGb = item.DataDiskGb;

            var osDiskRec = MapDiskTier(effectiveOsDiskGb, isDevOrUat);
            var dataDiskRec = MapDiskTier(effectiveDataDiskGb, isDevOrUat);

            decimal osDiskMonthly = Math.Round(item.Quantity * osDiskRec.MonthlyCost, 2);
            decimal dataDiskMonthly = Math.Round(item.Quantity * dataDiskRec.MonthlyCost, 2);
            decimal totalStorageMonthly = osDiskMonthly + dataDiskMonthly;

            decimal sqlHourlyRate = 0;
            if (!enableSqlAhb && sqlEdition != "None" && sqlEdition != "Developer" && sqlEdition != "Express")
            {
                sqlHourlyRate = await FetchSqlHourlyRatePerCoreAsync(sqlEdition, item.Region);
            }

            string chosenSku;
            int targetCpu;
            decimal targetRam;
            decimal baseLinuxHourly = 0;
            decimal res1YrMonthlyRate = 0;
            decimal res3YrMonthlyRate = 0;
            decimal monthlyOsLicensePerUnit = 0;
            decimal monthlySqlLicensePerUnit = 0;

            if (!string.IsNullOrWhiteSpace(item.SkuName))
            {
                chosenSku = ApplyProcessorPreference(item.SkuName, processorPreference);
                targetCpu = ExtractVcpuCount(chosenSku);
                targetRam = InferRamFromSku(chosenSku, targetCpu);

                var pricing = await EvaluateSkuPricingAsync(chosenSku, item.Region, targetCpu, licenseCategory, enableOsAhb, sqlHourlyRate, monthlyHours);
                baseLinuxHourly = pricing.BaseLinuxHourly;
                res1YrMonthlyRate = pricing.Res1YrMonthlyRate;
                res3YrMonthlyRate = pricing.Res3YrMonthlyRate;
                monthlyOsLicensePerUnit = pricing.MonthlyOsLicense;
                monthlySqlLicensePerUnit = pricing.MonthlySqlLicense;
            }
            else if (isDevOrUat)
            {
                // Non-production: prioritize burstable B-series (Basv2)
                int minCores = Math.Max(2, item.CpuCores);
                int targetCores = minCores <= 2 ? 2 : minCores <= 4 ? 4 : minCores <= 8 ? 8 : minCores <= 16 ? 16 : 32;
                while ((targetCores * 4) < item.RamGb && targetCores < 32)
                {
                    targetCores *= 2;
                }
                chosenSku = ApplyProcessorPreference($"Standard_B{targetCores}as_v2", processorPreference);
                targetCpu = targetCores;
                targetRam = targetCores * 4m;

                var pricing = await EvaluateSkuPricingAsync(chosenSku, item.Region, targetCpu, licenseCategory, enableOsAhb, sqlHourlyRate, monthlyHours);
                baseLinuxHourly = pricing.BaseLinuxHourly;
                res1YrMonthlyRate = pricing.Res1YrMonthlyRate;
                res3YrMonthlyRate = pricing.Res3YrMonthlyRate;
                monthlyOsLicensePerUnit = pricing.MonthlyOsLicense;
                monthlySqlLicensePerUnit = pricing.MonthlySqlLicense;
            }
            else
            {
                // Production: Evaluate eligible D, E, and M series SKUs and pick the LOWEST overall monthly cost
                int reqCpu = Math.Max(1, item.CpuCores);
                decimal reqRam = Math.Max(1m, item.RamGb);

                var eligibleCandidates = ProductionCatalog
                    .Where(s => s.Cpus >= reqCpu && s.RamGb >= reqRam)
                    .ToList();

                if (!eligibleCandidates.Any())
                {
                    eligibleCandidates.Add(ProductionCatalog.OrderByDescending(s => s.RamGb).First());
                }

                var bestCandidatesPerSeries = eligibleCandidates
                    .GroupBy(c => c.Series)
                    .Select(g => g.OrderBy(c => c.Cpus).ThenBy(c => c.RamGb).First())
                    .ToList();

                string bestSku = "";
                int bestCpu = 0;
                decimal bestRam = 0;
                decimal lowestTotalCost = decimal.MaxValue;
                (decimal BaseLinuxHourly, decimal Res1YrMonthlyRate, decimal Res3YrMonthlyRate, decimal MonthlyOsLicense, decimal MonthlySqlLicense) bestPricing = default;

                foreach (var candidate in bestCandidatesPerSeries)
                {
                    string candidateSku = candidate.SkuPattern;
                    if (candidate.SupportsAmd)
                    {
                        candidateSku = ApplyProcessorPreference(candidateSku, processorPreference);
                    }

                    var pricing = await EvaluateSkuPricingAsync(candidateSku, item.Region, candidate.Cpus, licenseCategory, enableOsAhb, sqlHourlyRate, monthlyHours);
                    decimal candidatePaygCompute = pricing.BaseLinuxHourly * monthlyHours;
                    decimal candidateTotal = candidatePaygCompute + pricing.MonthlyOsLicense + pricing.MonthlySqlLicense + totalStorageMonthly;

                    if (candidateTotal < lowestTotalCost && pricing.BaseLinuxHourly > 0)
                    {
                        lowestTotalCost = candidateTotal;
                        bestSku = candidateSku;
                        bestCpu = candidate.Cpus;
                        bestRam = candidate.RamGb;
                        bestPricing = pricing;
                    }
                }

                if (string.IsNullOrEmpty(bestSku))
                {
                    var fallback = eligibleCandidates.OrderBy(c => c.Cpus).ThenBy(c => c.RamGb).First();
                    bestSku = fallback.SupportsAmd ? ApplyProcessorPreference(fallback.SkuPattern, processorPreference) : fallback.SkuPattern;
                    bestCpu = fallback.Cpus;
                    bestRam = fallback.RamGb;
                    bestPricing = await EvaluateSkuPricingAsync(bestSku, item.Region, bestCpu, licenseCategory, enableOsAhb, sqlHourlyRate, monthlyHours);
                }

                chosenSku = bestSku;
                targetCpu = bestCpu;
                targetRam = bestRam;
                baseLinuxHourly = bestPricing.BaseLinuxHourly;
                res1YrMonthlyRate = bestPricing.Res1YrMonthlyRate;
                res3YrMonthlyRate = bestPricing.Res3YrMonthlyRate;
                monthlyOsLicensePerUnit = bestPricing.MonthlyOsLicense;
                monthlySqlLicensePerUnit = bestPricing.MonthlySqlLicense;
            }

            // Calculations
            decimal computePayg = Math.Round(item.Quantity * (baseLinuxHourly * monthlyHours), 2);
            decimal compute1Yr = res1YrMonthlyRate > 0 ? Math.Round(item.Quantity * res1YrMonthlyRate, 2) : Math.Round(item.Quantity * (baseLinuxHourly * 730m), 2);
            decimal compute3Yr = res3YrMonthlyRate > 0 ? Math.Round(item.Quantity * res3YrMonthlyRate, 2) : Math.Round(item.Quantity * (baseLinuxHourly * 730m), 2);

            if (compute3Yr > compute1Yr) compute3Yr = compute1Yr;

            decimal totalOsLicense = Math.Round(item.Quantity * monthlyOsLicensePerUnit, 2);
            decimal totalSqlLicense = Math.Round(item.Quantity * monthlySqlLicensePerUnit, 2);

            return new CostEstimateResult
            {
                VmName = item.VmName,
                Environment = item.Environment,
                SourceCpu = item.CpuCores,
                SourceRamGb = item.RamGb,
                SourceOsDiskGb = effectiveOsDiskGb,
                SourceDataDiskGb = effectiveDataDiskGb,
                SkuName = chosenSku,
                TargetCpu = targetCpu,
                TargetRamGb = targetRam,
                RecommendedOsDisk = osDiskRec.DiskTier,
                RecommendedDataDisk = dataDiskRec.DiskTier,
                Region = item.Region,
                DetectedOs = displayOs,
                LicenseType = licenseCategory == "None"
                    ? "Free / Open Source"
                    : (enableOsAhb ? $"{displayOs} (AHB)" : $"{displayOs}"),
                SqlEdition = sqlEdition == "None"
                    ? "-"
                    : (enableSqlAhb ? $"{sqlEdition} (AHB)" : sqlEdition),
                ComputePaygMonthly = computePayg,
                ComputeReserved1YrMonthly = compute1Yr,
                ComputeReserved3YrMonthly = compute3Yr,
                OsDiskMonthlyCost = osDiskMonthly,
                DataDiskMonthlyCost = dataDiskMonthly,
                OsLicenseMonthlyCost = totalOsLicense,
                SqlLicenseMonthlyCost = totalSqlLicense,
                Quantity = item.Quantity
            };
        }

        // Landing Zone Baseline Pricing Calculations
        public (decimal MonthlyCost, string Remarks) CalculateFirewallCost(string tier) => tier switch
        {
            "Basic" => (292.00m, "Azure Firewall Basic Deployment (730 hrs)"),
            "Premium" => (1277.50m, "Azure Firewall Premium Deployment (730 hrs)"),
            _ => (912.50m, "Azure Firewall Standard Deployment (730 hrs)")
        };

        public (decimal MonthlyCost, string Remarks) CalculateVpnGatewayCost(string sku) => sku switch
        {
            "VpnGw2AZ" => (394.20m, "VpnGw2AZ Zone-Redundant (up to 1.25 Gbps, 730 hrs)"),
            "VpnGw3AZ" => (1007.40m, "VpnGw3AZ Zone-Redundant (up to 2.5 Gbps, 730 hrs)"),
            _ => (153.30m, "VpnGw1AZ Zone-Redundant (up to 650 Mbps, 730 hrs)")
        };

        public (decimal MonthlyCost, string Remarks) CalculateAppGatewayCost(string tier, int capacityUnits)
        {
            int units = Math.Max(1, capacityUnits);
            if (tier.Equals("Standard_v2", StringComparison.OrdinalIgnoreCase))
            {
                decimal fixedMonthly = 0.246m * 730m; // ~$179.58
                decimal capacityMonthly = (units - 1) * (0.008m * 730m);
                return (Math.Round(fixedMonthly + capacityMonthly, 2), $"Standard_v2 Base + {units} Capacity Unit(s)");
            }
            else
            {
                decimal fixedMonthly = 0.443m * 730m; // ~$323.39
                decimal capacityMonthly = (units - 1) * (0.0144m * 730m);
                return (Math.Round(fixedMonthly + capacityMonthly, 2), $"WAF_v2 Base + {units} Capacity Unit(s)");
            }
        }

        public (decimal MonthlyCost, string Remarks) CalculateBastionCost(string tier) => tier switch
        {
            "Basic" => (138.70m, "Azure Bastion Basic (2 instances, 730 hrs)"),
            "Developer" => (35.00m, "Azure Bastion Developer Plan"),
            _ => (211.70m, "Azure Bastion Standard (Host scaling, 730 hrs)")
        };

        public (decimal MonthlyCost, string Remarks) CalculateBackupCost(int vmCount, decimal totalStorageGb)
        {
            if (vmCount <= 0) return (0m, "No protected VMs detected");
            decimal instanceFees = vmCount * 10.00m;
            decimal storageFees = totalStorageGb * 0.022m;
            return (Math.Round(instanceFees + storageFees, 2), $"{vmCount} Protected VM(s) + {totalStorageGb:N0} GB Storage");
        }

        public (decimal MonthlyCost, string Remarks) CalculateDefenderCost(int vmCount, string plan)
        {
            if (vmCount <= 0) return (0m, "No target VMs detected");
            decimal perServerRate = plan.Contains("1") ? 5.00m : 15.00m;
            return (Math.Round(vmCount * perServerRate, 2), $"Defender for Cloud Servers ({plan}) for {vmCount} VM(s)");
        }

        public (decimal MonthlyCost, string Remarks) CalculateKeyVaultCost(string tier) => tier switch
        {
            "Premium" => (150.00m, "Key Vault HSM Managed Pool / Keys"),
            _ => (6.00m, "Standard Key Vault (Secrets, Certs, Keys)")
        };

        public (decimal MonthlyCost, string Remarks) CalculateDdosCost() =>
            (2944.00m, "Azure DDoS Network Protection Plan (Up to 100 IPs)");

        public (decimal MonthlyCost, string Remarks) CalculatePublicIpCost(int count)
        {
            int qty = Math.Max(0, count);
            decimal cost = qty * (0.005m * 730m); // ~$3.65 / IP / month
            return (Math.Round(cost, 2), $"{qty} Standard Static Public IP(s)");
        }

        public (decimal MonthlyCost, string Remarks) CalculatePrivateEndpointCost(int count)
        {
            int qty = Math.Max(0, count);
            decimal cost = qty * (0.01m * 730m); // ~$7.30 / endpoint / month
            return (Math.Round(cost, 2), $"{qty} Private Endpoint(s) (730 hrs)");
        }

        public (decimal MonthlyCost, string Remarks) CalculateNatGatewayCost(int count)
        {
            int qty = Math.Max(0, count);
            decimal cost = qty * (0.045m * 730m); // ~$32.85 / gateway / month
            return (Math.Round(cost, 2), $"{qty} NAT Gateway(s) (730 hrs)");
        }

        public (decimal MonthlyCost, string Remarks) CalculateDnsResolverCost() =>
            (160.60m, "Azure DNS Private Resolver (1 Inbound + 1 Outbound Endpoint)");

        public (decimal MonthlyCost, string Remarks) CalculateAsrCost(int vmCount)
        {
            if (vmCount <= 0) return (0m, "No target VMs detected");
            decimal cost = vmCount * 25.00m; // $25.00 per replicated instance
            return (Math.Round(cost, 2), $"Azure Site Recovery for {vmCount} Replicated VM(s)");
        }

        public (decimal MonthlyCost, string Remarks) CalculateMonitorCost(decimal dataIngestionGb)
        {
            decimal billableGb = Math.Max(0m, dataIngestionGb - 5.0m); // First 5 GB free per billing month
            decimal cost = billableGb * 2.30m; // $2.30 per GB over 5 GB
            return (Math.Round(cost, 2), $"Log Analytics Ingestion ({dataIngestionGb:N0} GB/mo, 5 GB Free)");
        }

        private async Task<(decimal BaseLinuxHourly, decimal Res1YrMonthlyRate, decimal Res3YrMonthlyRate, decimal MonthlyOsLicense, decimal MonthlySqlLicense)>
            EvaluateSkuPricingAsync(
                string sku,
                string region,
                int vCpus,
                string licenseCategory,
                bool enableOsAhb,
                decimal sqlHourlyRate,
                int monthlyHours)
        {
            var vmPrices = await FetchPricesAsync(sku, region, "Virtual Machines");

            decimal baseLinuxHourly = 0;
            decimal windowsHourly = 0;
            decimal res1YrMonthlyRate = 0;
            decimal res3YrMonthlyRate = 0;

            foreach (var p in vmPrices)
            {
                decimal price = p.RetailPrice > 0 ? p.RetailPrice : p.UnitPrice;
                if (price <= 0) continue;

                bool isSpotOrPromo =
                    p.MeterName.Contains("Spot", StringComparison.OrdinalIgnoreCase) ||
                    p.MeterName.Contains("Low Priority", StringComparison.OrdinalIgnoreCase) ||
                    p.MeterName.Contains("Promo", StringComparison.OrdinalIgnoreCase) ||
                    p.SkuName.Contains("Spot", StringComparison.OrdinalIgnoreCase) ||
                    p.ProductName.Contains("Spot", StringComparison.OrdinalIgnoreCase);

                if (isSpotOrPromo) continue;

                bool isWindows = p.ProductName.Contains("Windows", StringComparison.OrdinalIgnoreCase);

                if (p.Type.Equals("Consumption", StringComparison.OrdinalIgnoreCase))
                {
                    if (isWindows && windowsHourly == 0) windowsHourly = price;
                    else if (!isWindows && baseLinuxHourly == 0) baseLinuxHourly = price;
                }
                else if (p.Type.Equals("Reservation", StringComparison.OrdinalIgnoreCase))
                {
                    string term = p.ReservationTerm ?? "";
                    if (term.StartsWith("1", StringComparison.OrdinalIgnoreCase) && res1YrMonthlyRate == 0)
                        res1YrMonthlyRate = price > 100m ? (price / 12m) : (price * 730m);
                    else if (term.StartsWith("3", StringComparison.OrdinalIgnoreCase) && res3YrMonthlyRate == 0)
                        res3YrMonthlyRate = price > 100m ? (price / 36m) : (price * 730m);
                }
            }

            if (baseLinuxHourly == 0 && windowsHourly > 0)
                baseLinuxHourly = Math.Max(0.01m, windowsHourly - (vCpus * 0.046m));
            else if (windowsHourly == 0 && baseLinuxHourly > 0)
                windowsHourly = baseLinuxHourly + (vCpus * 0.046m);

            // OS Licensing
            decimal monthlyOsLicense = 0;
            if (!enableOsAhb)
            {
                if (licenseCategory == "Windows")
                    monthlyOsLicense = Math.Max(vCpus * 0.046m * monthlyHours, (windowsHourly - baseLinuxHourly) * monthlyHours);
                else if (licenseCategory == "RHEL")
                    monthlyOsLicense = (await FetchRhelLicenseRateAsync(sku, region)) * monthlyHours;
                else if (licenseCategory == "SUSE_247")
                    monthlyOsLicense = (await FetchSuseLicenseRateAsync(sku, region)) * monthlyHours;
            }

            // SQL Licensing
            decimal monthlySqlLicense = 0;
            if (sqlHourlyRate > 0)
            {
                int billingCores = Math.Max(4, vCpus);
                monthlySqlLicense = billingCores * sqlHourlyRate * monthlyHours;
            }

            return (baseLinuxHourly, res1YrMonthlyRate, res3YrMonthlyRate, monthlyOsLicense, monthlySqlLicense);
        }

        private decimal InferRamFromSku(string sku, int cpus)
        {
            if (sku.Contains("_E", StringComparison.OrdinalIgnoreCase)) return cpus * 8m;
            if (sku.Contains("_M", StringComparison.OrdinalIgnoreCase))
            {
                var match = ProductionCatalog.FirstOrDefault(m => m.SkuPattern.Equals(sku, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.RamGb;
                return cpus * 28m;
            }
            if (sku.Contains("_F", StringComparison.OrdinalIgnoreCase)) return cpus * 2m;
            return cpus * 4m;
        }

        private async Task<decimal> FetchSqlHourlyRatePerCoreAsync(string edition, string region)
        {
            string cacheKey = $"sql_rate_{edition}_{region}";
            if (_cache.TryGetValue(cacheKey, out decimal cachedRate))
                return cachedRate;

            string query = $"$filter=serviceName eq 'SQL Server' and armRegionName eq '{region}' and priceType eq 'Consumption'";
            string url = $"https://prices.azure.com/api/retail/prices?{query}";

            try
            {
                var response = await _http.GetFromJsonAsync<AzurePricePagedResponse>(url);
                if (response?.Items != null)
                {
                    foreach (var item in response.Items)
                    {
                        if (item.ProductName.Contains(edition, StringComparison.OrdinalIgnoreCase))
                        {
                            decimal rate = item.RetailPrice > 0 ? item.RetailPrice : item.UnitPrice;
                            if (rate > 0)
                            {
                                _cache.Set(cacheKey, rate, TimeSpan.FromHours(12));
                                return rate;
                            }
                        }
                    }
                }
            }
            catch { }

            decimal fallback = edition switch
            {
                "Web" => 0.015m,
                "Standard" => 0.146m,
                "Enterprise" => 0.55m,
                _ => 0m
            };

            _cache.Set(cacheKey, fallback, TimeSpan.FromHours(12));
            return fallback;
        }

        private int ExtractVcpuCount(string sku)
        {
            var match = Regex.Match(sku, @"[A-Za-z]+(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int cpus))
            {
                return cpus;
            }
            return 2;
        }

        private async Task<decimal> FetchSuseLicenseRateAsync(string sku, string region)
        {
            string cacheKey = $"suse_247_rate_{sku}_{region}";
            if (_cache.TryGetValue(cacheKey, out decimal cachedRate)) return cachedRate;

            string query = $"$filter=serviceName eq 'SUSE Linux Enterprise Server Priority' and armRegionName eq '{region}' and armSkuName eq '{sku}' and priceType eq 'Consumption'";
            string url = $"https://prices.azure.com/api/retail/prices?{query}";

            try
            {
                var response = await _http.GetFromJsonAsync<AzurePricePagedResponse>(url);
                if (response?.Items != null && response.Items.Count > 0)
                {
                    decimal rate = response.Items[0].RetailPrice > 0 ? response.Items[0].RetailPrice : response.Items[0].UnitPrice;
                    if (rate > 0) { _cache.Set(cacheKey, rate, TimeSpan.FromHours(12)); return rate; }
                }
            }
            catch { }

            decimal fallbackRate = (sku.Contains("8") || sku.Contains("16") || sku.Contains("32") || sku.Contains("64") || sku.Contains("128")) ? 0.20m : 0.10m;
            _cache.Set(cacheKey, fallbackRate, TimeSpan.FromHours(12));
            return fallbackRate;
        }

        private async Task<decimal> FetchRhelLicenseRateAsync(string sku, string region)
        {
            string cacheKey = $"rhel_rate_{sku}_{region}";
            if (_cache.TryGetValue(cacheKey, out decimal cachedRate)) return cachedRate;

            string query = $"$filter=serviceName eq 'Red Hat Enterprise Linux' and armRegionName eq '{region}' and armSkuName eq '{sku}' and priceType eq 'Consumption'";
            string url = $"https://prices.azure.com/api/retail/prices?{query}";

            try
            {
                var response = await _http.GetFromJsonAsync<AzurePricePagedResponse>(url);
                if (response?.Items != null && response.Items.Count > 0)
                {
                    decimal rate = response.Items[0].RetailPrice > 0 ? response.Items[0].RetailPrice : response.Items[0].UnitPrice;
                    if (rate > 0) { _cache.Set(cacheKey, rate, TimeSpan.FromHours(12)); return rate; }
                }
            }
            catch { }

            decimal fallbackRate = 0.06m;
            _cache.Set(cacheKey, fallbackRate, TimeSpan.FromHours(12));
            return fallbackRate;
        }

        private async Task<List<AzurePriceItem>> FetchPricesAsync(string sku, string region, string serviceName)
        {
            string cacheKey = $"{sku}_{region}_{serviceName}";
            if (_cache.TryGetValue(cacheKey, out List<AzurePriceItem>? cached) && cached != null)
                return cached;

            var allItems = new List<AzurePriceItem>();
            string query = $"$filter=serviceName eq '{serviceName}' and armRegionName eq '{region}' and armSkuName eq '{sku}'";
            string? nextUrl = $"https://prices.azure.com/api/retail/prices?{query}";

            while (!string.IsNullOrEmpty(nextUrl) && allItems.Count < 500)
            {
                try
                {
                    var response = await _http.GetFromJsonAsync<AzurePricePagedResponse>(nextUrl);
                    if (response?.Items != null)
                    {
                        allItems.AddRange(response.Items);
                    }
                    nextUrl = response?.NextPageLink;
                }
                catch { break; }
            }

            _cache.Set(cacheKey, allItems, TimeSpan.FromHours(12));
            return allItems;
        }

        private string ApplyProcessorPreference(string sku, string preference)
        {
            if (string.IsNullOrWhiteSpace(sku)) return sku;
            if (sku.StartsWith("Standard_M", StringComparison.OrdinalIgnoreCase)) return sku;

            if (preference == "AMD")
                return Regex.Replace(sku, @"(?<=[A-Za-z]\d+)(s?_v\d+)", "a$1", RegexOptions.IgnoreCase);
            if (preference == "Intel")
                return Regex.Replace(sku, @"(?<=[A-Za-z]\d+)a(?=s?_v\d+)", "", RegexOptions.IgnoreCase);
            return sku;
        }
    }
}