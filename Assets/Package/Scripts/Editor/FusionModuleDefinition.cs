using System.Collections.Generic;
using CyberHub.Brane;
using CyberHub.Brane.Editor;

namespace Foundry.Networking
{
    public class FusionModuleDefinition : IModuleDefinition
    {
        public string ModuleName()
        {
            return "Fusion Networking for Foundry";
        }

        public List<ProvidedService> GetProvidedServices()
        {
            return new List<ProvidedService>
            {
                new ProvidedService
                {
                    ImplementationName = "Photon Fusion + Voice",
                    ServiceInterface = typeof(INetworkProvider)
                }
            };
        }

        public List<UsedService> GetUsedServices()
        {
            return new List<UsedService>
            {
                new UsedService
                {
                    optional = false,
                    ServiceInterface = typeof(INetworkProvider)
                }
            };
        }

        public BraneModuleConfig GetModuleConfig()
        {
            return FusionModuleConfig.GetAsset();
        }
    }
}
