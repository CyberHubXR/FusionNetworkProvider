using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CyberHub.Brane;
using Foundry.Core.Serialization;
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

        public FoundryRunnerManager runnerManager;
        
        
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
            runnerManager = FoundryRunnerManager.GetManagerForScene(gameObject.scene);
            GetNetworkStateIdAsync(id => onConnectedCallback?.Invoke());
            if (!HasStateAuthority)
                StartCoroutine(SendIDRequest());
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
            runnerManager.ChangeOwner(cachedGraphNetworkStateId, Object, newOwner, result =>
            {
                setOwnershipCallback?.Invoke(result);
                if(newOwner == BraneApp.GetService<INetworkProvider>().LocalPlayerId)
                    Object.RequestStateAuthority();
            });
        }

        public void StateAuthorityChanged()
        {
            onOwnershipChangedCallback?.Invoke(Object.StateAuthority);
        }

        IEnumerator SendIDRequest()
        {
            while (gameObject && !NetworkStateId.IsValid() && !Object.HasStateAuthority)
            {
                RPC_GetID(Object.StateAuthority);
                yield return new WaitForSecondsRealtime(0.25f);
            }
        }

        // Id request chain

        [Rpc(sources: RpcSources.All, targets: RpcTargets.StateAuthority, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_GetID([RpcTarget] PlayerRef stateAuthority, RpcInfo info = default)
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

        Action onRequestFullStateCallback;
        public void RequestFullState(Action callback)
        {
            onRequestFullStateCallback = callback;
            RPC_RequestFullNode();
        }

        [Rpc(sources: RpcSources.All, targets: RpcTargets.StateAuthority, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_RequestFullNode(RpcInfo info = default)
        {
            if (!NetworkManager.State.TryGetNode(cachedGraphNetworkStateId, out var node))
            {
                Debug.LogError("Request for nonexistent node: " + cachedGraphNetworkStateId);
                return;
            }

            MemoryStream stream = new();
            FoundrySerializer serializer = new(stream);

            NetworkManager.State.SerializeNode(node, serializer, true);

            serializer.Dispose();
            RPC_SendFullNode(info.Source, stream.ToArray());
        }
        
        [Rpc(sources: RpcSources.All, targets: RpcTargets.All, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_SendFullNode([RpcTarget] PlayerRef player, byte[] nodeData, RpcInfo info = default)
        {
            if (nodeData == null || nodeData.Length == 0)
            {
                Debug.LogError("Received empty node data from " + info.Source + " for " + gameObject.name);
                return;
            }
            try
            {
                var netId = new NetworkId();
                MemoryStream stream = new(nodeData);
                FoundryDeserializer deserializer = new(stream);
                deserializer.Deserialize(ref netId);
                NetworkStateId = netId;

                if (!NetworkManager.State.TryGetNode(netId, out var node))
                    node = NetworkManager.State.AddNode(netId, info.Source, false);

                node.Deserialize(deserializer);
                deserializer.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            
            onRequestFullStateCallback?.Invoke();
        }
    }
}
