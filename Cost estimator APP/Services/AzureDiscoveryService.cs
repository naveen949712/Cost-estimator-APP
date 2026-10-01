using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Resources;
using Cost_Estimator_App.Models;

namespace Cost_Estimator_App.Services
{
    public class AzureTenantInfo
    {
        public string TenantId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    public class AzureSubscriptionInfo
    {
        public string SubscriptionId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    public class AzureDiscoveryService
    {
        private ArmClient? _armClient;
        private InteractiveBrowserCredential? _credential;

        public async Task<List<AzureTenantInfo>> LoginAndListTenantsAsync(string? clientId = null)
        {
            var options = new InteractiveBrowserCredentialOptions
            {
                // Default Azure CLI Client ID works for multi-tenant interactive login
                ClientId = string.IsNullOrWhiteSpace(clientId) ? "04b07795-8ddb-461a-bbee-02f9e1bf7b46" : clientId,
                RedirectUri = new Uri("http://localhost")
            };

            _credential = new InteractiveBrowserCredential(options);
            _armClient = new ArmClient(_credential);

            var tenants = new List<AzureTenantInfo>();
            await foreach (var tenant in _armClient.GetTenants().GetAllAsync())
            {
                tenants.Add(new AzureTenantInfo
                {
                    TenantId = tenant.Data.TenantId.ToString(),
                    DisplayName = tenant.Data.TenantId.ToString()
                });
            }

            return tenants;
        }

        public async Task<List<AzureSubscriptionInfo>> GetSubscriptionsForTenantAsync(string tenantId)
        {
            if (_credential == null) throw new InvalidOperationException("User is not authenticated with Azure.");

            var tenantClient = new ArmClient(_credential, tenantId);
            var subs = new List<AzureSubscriptionInfo>();

            await foreach (var sub in tenantClient.GetSubscriptions().GetAllAsync())
            {
                subs.Add(new AzureSubscriptionInfo
                {
                    SubscriptionId = sub.Data.SubscriptionId,
                    DisplayName = sub.Data.DisplayName
                });
            }

            return subs;
        }

        public async Task<List<InventoryItem>> ScanSubscriptionResourcesAsync(string tenantId, string subscriptionId)
        {
            if (_credential == null) throw new InvalidOperationException("User is not authenticated with Azure.");

            var tenantClient = new ArmClient(_credential, tenantId);
            var subResource = tenantClient.GetSubscriptionResource(
                SubscriptionResource.CreateResourceIdentifier(subscriptionId));

            var discoveredItems = new List<InventoryItem>();

            await foreach (var vm in subResource.GetVirtualMachinesAsync())
            {
                string vmName = vm.Data.Name;
                string region = vm.Data.Location.Name;
                string sku = vm.Data.HardwareProfile?.VmSize?.ToString() ?? "Standard_D2s_v5";
                string osType = vm.Data.StorageProfile?.OSDisk?.OSType?.ToString() ?? "Linux";

                int osDiskGb = vm.Data.StorageProfile?.OSDisk?.DiskSizeGB ?? 128;
                int totalDataDiskGb = 0;
                if (vm.Data.StorageProfile?.DataDisks != null)
                {
                    totalDataDiskGb = vm.Data.StorageProfile.DataDisks.Sum(d => d.DiskSizeGB ?? 0);
                }

                discoveredItems.Add(new InventoryItem
                {
                    VmName = vmName,
                    SkuName = sku,
                    Region = region,
                    RawOs = osType,
                    Environment = (vmName.Contains("dev", StringComparison.OrdinalIgnoreCase) ||
                                   vmName.Contains("uat", StringComparison.OrdinalIgnoreCase) ||
                                   vmName.Contains("test", StringComparison.OrdinalIgnoreCase)) ? "Dev" : "Prod",
                    OsDiskGb = osDiskGb,
                    DataDiskGb = totalDataDiskGb,
                    Quantity = 1
                });
            }

            return discoveredItems;
        }
    }
}