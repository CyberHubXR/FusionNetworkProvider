using System.Collections;
using System.Collections.Generic;
using Foundry.Core.Editor;
using UnityEngine;

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

        public List<UsedService> GetUsedService()
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

        public FoundryModuleConfig GetModuleConfig()
        {
            return FusionModuleConfig.GetAsset();
        }
    }
}
