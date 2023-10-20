using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Fusion;

namespace Foundry.Networking
{
    public class SyncedNetworkGraphId : NetworkBehaviour, INetworkedGraphId
    {
        
        private event Action<NetworkId> onIdAssigned;
        
        public void OnIdAssigned(Action<NetworkId> callback)
        {
            if (Value.IsValid())
                callback(Value);
            onIdAssigned += callback;
        }


        private uint cachedFusionId = 0xffffffff;
        private NetworkId cachedGraphId = NetworkId.Invalid;
        
        public NetworkId Value
        {
            get => cachedGraphId;
            set
            {
                if (Object?.Id.IsValid ?? false)
                {
                    if (cachedGraphId.IsValid() && !Object.HasStateAuthority)
                        tryTakeAuthority = true;
                        
                    FoundryRunnerManager.AddOrReplaceMappedId(Object.Id.Raw, value);
                    cachedGraphId = value;
                    onIdAssigned?.Invoke(value);
                }
                else
                    cachedGraphId = value;
            }
        }
        
        private bool tryTakeAuthority = false;

        public override void Spawned()
        {
            cachedFusionId = Object.Id.Raw;
            if (cachedGraphId.IsValid())
            {
                FoundryRunnerManager.AddOrReplaceMappedId(Object.Id.Raw, cachedGraphId);
                onIdAssigned?.Invoke(cachedGraphId);
            }
            else
            {
                FoundryRunnerManager.GetGraphIdAsync(Object.Id.Raw, id =>
                {
                    Value = id;
                });
            }
        }

        public void OnDestroy()
        {
            if(cachedFusionId != 0xffffffff)
                FoundryRunnerManager.RemoveMappedId(cachedFusionId, cachedGraphId);
        }

        public override void FixedUpdateNetwork()
        {
           
            if (tryTakeAuthority)
            {
                Object.RequestStateAuthority(); // For some reason photon doesn't like this being called anywhere but from in it's callbacks 
                tryTakeAuthority = false;
            }
        }
    }
}
