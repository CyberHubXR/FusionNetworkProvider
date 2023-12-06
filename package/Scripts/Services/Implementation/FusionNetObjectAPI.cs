using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Fusion;

namespace Foundry.Networking
{
    public class FusionNetObjectAPI : NetworkBehaviour, INetworkObjectAPI, IStateAuthorityChanged
    {
        public NetworkId NetworkStateId
        {
            get => cachedGraphNetworkStateId;
            set
            {
                cachedGraphNetworkStateId = value;
                Debug.Assert(value.IsValid(), "Tried to set NetworkStateId to invalid value");
                onNetworkStateIdSet?.Invoke(value);
                onNetworkStateIdSet = null;
            }
        }

        public int Owner => Object?.StateAuthority ?? -1;
        public bool IsOwner => Object?.HasStateAuthority ?? true;
        
        
        Action<NetworkId> onNetworkStateIdSet;
        public void GetNetworkStateIdAsync(Action<NetworkId> callback)
        {
            if (NetworkStateId.IsValid())
                callback(NetworkStateId);
            else
                onNetworkStateIdSet += callback;
        }
        
        private NetworkId cachedGraphNetworkStateId = NetworkId.Invalid;
        
        public override void Spawned()
        {
            GetNetworkStateIdAsync(id => onConnectedCallback?.Invoke());
            if (!HasStateAuthority)
                SendIDRequest();
        }

        private Action onConnectedCallback;

        public void OnConnected(Action callback)
        {
            if(Object is { IsValid: true } && NetworkStateId.IsValid())
                callback();
            else
                onConnectedCallback += callback;
        }

        Func<int, bool> onValidateOwnershipChangeCallback;
        public void OnValidateOwnershipChange(Func<int, bool> callback)
        {
            onValidateOwnershipChangeCallback += callback;
        }
        
        private Action<int> onOwnershipChangedCallback;
        public void OnOwnershipChanged(Action<int> callback)
        {
            onOwnershipChangedCallback += callback;
        }

        private Action<bool> setOwnershipCallback;
        public void SetOwnership(int newOwner, Action<bool> callback)
        {
            setOwnershipCallback += callback;
            FoundryRunnerManager.ChangeOwner(cachedGraphNetworkStateId, Object, newOwner, result =>
            {
                setOwnershipCallback?.Invoke(result);
                if(newOwner == FoundryApp.GetService<INetworkProvider>().LocalPlayerId)
                    Object.RequestStateAuthority();
            });
        }

        public void StateAuthorityChanged()
        {
            onOwnershipChangedCallback?.Invoke(Object.StateAuthority);
        }

        public async void SendIDRequest()
        {
            int attempts = 0;
            int maxAttempts = 50;
            while (!NetworkStateId.IsValid() && attempts++ < maxAttempts)
            {
                RPC_GetID();
                await Task.Delay(250);
            }

            if (!NetworkStateId.IsValid())
                Debug.LogError($"Failed to get NetworkStateId for {gameObject.name} after {maxAttempts} attempts");
        }
        
        // Id request chain
        [Rpc(sources: RpcSources.All, targets: RpcTargets.StateAuthority, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_GetID(RpcInfo info = default)
        {
            GetNetworkStateIdAsync(id =>
            {
                RPC_SetId(info.Source, id.Id);
            });
        }
        
        [Rpc(sources: RpcSources.StateAuthority, targets: RpcTargets.All, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_SetId([RpcTarget] PlayerRef requestor, uint id)
        {
            NetworkStateId = new(id);
        }
    }
}
