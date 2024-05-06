using System;
using System.Collections.Generic;
using CyberHub.Brane;

namespace Foundry.Networking
{
    public class FusionModuleConfig : BraneModuleConfig
    {
#if UNITY_EDITOR
        public static FusionModuleConfig GetAsset()
        {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<FusionModuleConfig>(
                "Assets/Foundry/Settings/FusionModuleConfig.asset");  
            if (asset == null)
            {
                asset = CreateInstance<FusionModuleConfig>();
                System.IO.Directory.CreateDirectory("Assets/Foundry/Settings");
                UnityEditor.AssetDatabase.CreateAsset(asset, "Assets/Foundry/Settings/FusionModuleConfig.asset");
            }
            return asset;
        }
#endif
        public override void RegisterServices(Dictionary<Type, ServiceConstructor> constructors)
        {
            constructors.Add(typeof(INetworkProvider), () => new FusionNetworkProvider());
        }
    }
    
    
}
