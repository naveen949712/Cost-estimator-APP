using System;
using System.Collections.Generic;
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

        public AzurePricingService(HttpClient http, IMemoryCache cache)
        {
            _http = http;
            _cache = cache;
        }

        public string DetectOperatingSystem(string rawOs)
        {
            if (string.IsNullOrWhiteSpace(rawOs)) return "Linux";
            string lower = rawOs.ToLowerInvariant();
            return (lower.Contains("win") || lower.Contains("server 20") || lower.Contains("microsoft"))
                ? "Windows"
                : "Linux";
        }

        public async Task<CostEstimateResult> CalculateItemCostAsync(
            InventoryItem item,
            string processorPreference,
            bool enableAhb)
        {
            string detectedOs = DetectOperatingSystem(item.RawOs);
            string effectiveSku = ApplyProcessorPreference(item.SkuName, processorPreference);

            var prices = await FetchPricesAsync(effectiveSku, item.Region);
            bool isWindowsWithoutAhb = (detectedOs == "Windows" && !enableAhb);

            decimal baseLinuxHourly = 0;
            decimal windowsHourly = 0;
            decimal res1YrMonthlyRate = 0;
            decimal res3YrMonthlyRate = 0;

            foreach (var p in prices)
            {
                bool isWindowsProduct = p.ProductName.Contains("Windows", StringComparison.OrdinalIgnoreCase);
                decimal price = p.RetailPrice > 0 ? p.RetailPrice : p.UnitPrice;
                if (price <= 0) continue;

                // 1. PAYG Consumption
                if (p.Type.Equals("Consumption", StringComparison.OrdinalIgnoreCase))
                {
                    if (isWindowsProduct && windowsHourly == 0)
                    {
                        windowsHourly = price;
                    }
                    else if (!isWindowsProduct && baseLinuxHourly == 0)
                    {
                        baseLinuxHourly = price;
                    }
                }
                // 2. Reservations (Always base compute in Azure)
                else if (p.Type.Equals("Reservation", StringComparison.OrdinalIgnoreCase))
                {
                    string term = p.ReservationTerm ?? "";

                    // Match 1 Year (e.g. "1 Year", "1 Years", "12 Months")
                    if (term.StartsWith("1", StringComparison.OrdinalIgnoreCase) && res1YrMonthlyRate == 0)
                    {
                        // If rate is > $100, it's the full 1-year lump sum -> divide by 12.
                        // Otherwise, it is already an hourly or monthly amortized rate.
                        res1YrMonthlyRate = price > 100m ? (price / 12m) : (price * 730m);
                    }
                    // Match 3 Year (e.g. "3 Year", "3 Years", "36 Months")
                    else if (term.StartsWith("3", StringComparison.OrdinalIgnoreCase) && res3YrMonthlyRate == 0)
                    {
                        // If rate is > $100, it's the full 3-year lump sum -> divide by 36.
                        // Otherwise, it is already an hourly or monthly amortized rate.
                        res3YrMonthlyRate = price > 100m ? (price / 36m) : (price * 730m);
                    }
                }
            }

            // Fallback for Windows rate if missing
            if (windowsHourly == 0) windowsHourly = baseLinuxHourly;

            // Determine effective PAYG
            decimal effectiveHourly = isWindowsWithoutAhb ? windowsHourly : baseLinuxHourly;
            decimal paygMonthly = Math.Round(item.Quantity * (effectiveHourly * 730m), 2);

            // Windows licensing surcharge per month (if AHB is disabled)
            decimal monthlyWinLicenseSurcharge = isWindowsWithoutAhb ? Math.Max(0, (windowsHourly - baseLinuxHourly) * 730m) : 0;

            // Calculate final monthly reservation costs
            decimal res1YrMonthly = res1YrMonthlyRate > 0
                ? Math.Round(item.Quantity * (res1YrMonthlyRate + monthlyWinLicenseSurcharge), 2)
                : 0;

            decimal res3YrMonthly = res3YrMonthlyRate > 0
                ? Math.Round(item.Quantity * (res3YrMonthlyRate + monthlyWinLicenseSurcharge), 2)
                : 0;

            // Accurate Savings percentages
            decimal savings1Yr = (paygMonthly > 0 && res1YrMonthly > 0 && res1YrMonthly < paygMonthly)
                ? Math.Round((1m - (res1YrMonthly / paygMonthly)) * 100m, 1)
                : 0;

            decimal savings3Yr = (paygMonthly > 0 && res3YrMonthly > 0 && res3YrMonthly < paygMonthly)
                ? Math.Round((1m - (res3YrMonthly / paygMonthly)) * 100m, 1)
                : 0;

            return new CostEstimateResult
            {
                VmName = string.IsNullOrWhiteSpace(item.VmName) ? effectiveSku : item.VmName,
                SkuName = effectiveSku,
                Region = item.Region,
                DetectedOs = detectedOs,
                Quantity = item.Quantity,
                PaygMonthly = paygMonthly,
                Reserved1YrMonthly = res1YrMonthly,
                Reserved3YrMonthly = res3YrMonthly,
                Savings1YrPercent = savings1Yr,
                Savings3YrPercent = savings3Yr
            };
        }

        private async Task<List<AzurePriceItem>> FetchPricesAsync(string sku, string region)
        {
            string cacheKey = $"{sku}_{region}";
            if (_cache.TryGetValue(cacheKey, out List<AzurePriceItem>? cached) && cached != null)
                return cached;

            var allItems = new List<AzurePriceItem>();
            string query = $"$filter=serviceName eq 'Virtual Machines' and armRegionName eq '{region}' and armSkuName eq '{sku}'";
            string? nextUrl = $"https://prices.azure.com/api/retail/prices?{query}";

            // Loop through pages (Azure paginates reservation records across multiple pages)
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
                catch
                {
                    break;
                }
            }

            _cache.Set(cacheKey, allItems, TimeSpan.FromHours(12));
            return allItems;
        }

        private string ApplyProcessorPreference(string sku, string preference)
        {
            if (string.IsNullOrWhiteSpace(sku)) return sku;

            if (preference == "AMD")
            {
                return Regex.Replace(sku, @"(?<=[A-Za-z]\d+)(s?_v\d+)", "a$1", RegexOptions.IgnoreCase);
            }
            else if (preference == "Intel")
            {
                return Regex.Replace(sku, @"(?<=[A-Za-z]\d+)a(?=s?_v\d+)", "", RegexOptions.IgnoreCase);
            }

            return sku;
        }
    }
}