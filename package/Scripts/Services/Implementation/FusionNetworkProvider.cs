using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Foundry.Services;
using Fusion;
using Photon.Voice.Fusion;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Foundry.Networking
{
    /// <summary>
    /// Implementation of INetworkProvider for Fusion.
    /// </summary>
    public class FusionNetworkProvider : INetworkProvider
    {
        private GameObject networkContextHolder;
        private FoundryRunnerManager runnerManager;

        private Dictionary<GameObject, RegisteredPrefab> registeredPrefabs = new();

        #region Interface Implementations

        public bool IsSessionConnected => FoundryRunnerManager.runner && FoundryRunnerManager.runner.IsRunning;
        public bool IsServer => runnerManager is { IsServer: true };
        public bool IsClient => runnerManager is { IsClient: true };

        public bool IsGraphAuthority => IsServer || (FoundryRunnerManager.runner && FoundryRunnerManager.runner.IsSharedModeMasterClient);
        public int GraphAuthorityId => runnerManager.MasterClientId;

        public int LocalPlayerId => runnerManager.LocalPlayerId;

        public Task StartSessionAsync(SessionInfo info)
        {
            sessionType = info.sessionType;
            networkContextHolder = new GameObject("Network Context");
            UnityEngine.Object.DontDestroyOnLoad(networkContextHolder);
            
            var runner = networkContextHolder.AddComponent<Fusion.NetworkRunner>();
            var photonVoiceNetwork = networkContextHolder.AddComponent<FusionVoiceClient>();
            var recorder = networkContextHolder.AddComponent<Photon.Voice.Unity.Recorder>();
            photonVoiceNetwork.PrimaryRecorder = recorder;
            photonVoiceNetwork.UseFusionAppSettings = true;
            photonVoiceNetwork.UseFusionAuthValues = true;
            
            runnerManager = networkContextHolder.AddComponent<FoundryRunnerManager>();
            FoundryRunnerManager.runner = runner;
            runnerManager.voiceClient = photonVoiceNetwork;
            runnerManager.recorder = recorder;

            return runnerManager.StartSession(info);
        }

        public Task StopSessionAsync()
        {
            GameObject.Destroy(networkContextHolder);
            return Task.CompletedTask;
        }

        private class ConstructionMetadata
        {
            public bool IsRoot = false;
            public Foundry.Networking.NetworkObject foundryNetObject;
            public List<Fusion.SimulationBehaviour> SimBehaviours = new();
            public List<Fusion.NetworkBehaviour> NetworkBehaviours = new();

            public void SearchObject(GameObject gameObject)
            {
                List<Fusion.SimulationBehaviour> sb = new(gameObject.GetComponents<SimulationBehaviour>());
                List<Fusion.NetworkBehaviour> nb = new();
        
                //Since network behaviours inherit from simulation behaviours we need to separate them out
                sb.RemoveAll(e =>
                {
                    if (e is NetworkBehaviour netB)
                    {
                        nb.Add(netB);
                        return true;
                    }
                    return false;
                });
                SimBehaviours.AddRange(sb);
                NetworkBehaviours.AddRange(nb);
            }
        }

        public void BindNetworkObject(GameObject gameObject, bool isPrefab)
        {
            BindNetworkObjectInternal(gameObject, null, isPrefab);
        }

        private List<Fusion.NetworkObject> BindNetworkObjectInternal(GameObject gameObject, ConstructionMetadata metadata, bool isPrefab)
        {
            // We need to search the entire tree for network behaviours and simulation behaviours, and add them to the correct network object -_-
            var foundryNetObject = gameObject.GetComponent<NetworkObject>();

            if (foundryNetObject)
            {
                var isRoot = metadata == null;
                metadata = new ConstructionMetadata();
                metadata.IsRoot = isRoot;
                metadata.foundryNetObject = foundryNetObject;
            }
            // If we have a network object, wait until later to search for behaviours so we have space to add some.
            if(metadata != null && !foundryNetObject)
                metadata.SearchObject(gameObject);
            
            List<Fusion.NetworkObject> childNetObjects = new List<Fusion.NetworkObject>();
            for (int i = 0; i < gameObject.transform.childCount; i++)
            {
                var child = gameObject.transform.GetChild(i);
                childNetObjects.AddRange(BindNetworkObjectInternal(child.gameObject, metadata, isPrefab));
            }
            

            if (metadata != null)
            {
                #region Component Mapping
                if(gameObject.TryGetComponent(out Foundry.Networking.NetworkTransform foundryNetTransform))
                {
                    FusionNetTransformProps props = foundryNetTransform.props.GetProps<FusionNetTransformProps>("Fusion");

                    Fusion.NetworkTransform transform;
                    if (props.isRigidbody)
                    {
                        if(!gameObject.TryGetComponent(out NetworkRigidbody rb))
                            rb = gameObject.AddComponent<Fusion.NetworkRigidbody>();
                        transform = rb;
                    }
                    else
                    {
                        if(!gameObject.TryGetComponent(out transform))
                            transform = gameObject.AddComponent<Fusion.NetworkTransform>();
                    }
                
                    transform.InterpolationDataSource = props.interpolationDataSource;
                    transform.InterpolationSpace = props.interpolationSpace;
                    transform.InterpolationTarget = foundryNetTransform.lerpObject;

                    transform.InterpolatedErrorCorrectionSettings = new();
                
                    foundryNetTransform.nativeScript = transform;

                    var api = gameObject.AddComponent<FusionNetworkTransformAPI>();
                    api.netTransform = transform;
                    foundryNetTransform.api = api;
                }
                
                if (gameObject.TryGetComponent(out NetworkVoiceOutput voiceOutput))
                {
                    var speaker = gameObject.AddComponent<Photon.Voice.Unity.Speaker>();
                    var voiceNetworkObject = gameObject.AddComponent<Photon.Voice.Fusion.VoiceNetworkObject>();
                    voiceOutput.nativeScript = voiceNetworkObject;
                }
                #endregion Component Mapping
            }


            if (!foundryNetObject)
            {
                metadata.SearchObject(gameObject);
                return childNetObjects;
            }

            
            Fusion.NetworkObject netObject;
            if(!gameObject.TryGetComponent(out netObject))
                netObject = gameObject.AddComponent<Fusion.NetworkObject>();
            foundryNetObject.nativeScript = netObject;
            
            var objectAPI = gameObject.AddComponent<FusionNetObjectAPI>();
            
            metadata.SearchObject(gameObject);

            foundryNetObject.nativeScript = netObject;
            if (foundryNetObject.disconnectBehaviour == DisconnectBehaviour.Destroy)
            {
                typeof(Fusion.NetworkObject)
                    .GetField("DestroyWhenStateAuthorityLeaves", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(netObject, true);
            }
            
            typeof(Fusion.NetworkObject)
                .GetField("AllowStateAuthorityOverride", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(netObject, foundryNetObject.allowOwnershipTransfer);

            // Set flags as if this object is a prefab
            netObject.Flags = NetworkObjectFlags.V1;
            if(!isPrefab)
                netObject.Flags |= NetworkObjectFlags.TypeSceneObject;
            else
                netObject.Flags |= metadata.IsRoot ? NetworkObjectFlags.TypePrefab : NetworkObjectFlags.TypePrefabChild;
            
            // Set guid
            netObject.NetworkGuid = Guid.Parse(foundryNetObject.guid);
            
            // Set behaviours
            netObject.SimulationBehaviours = metadata.SimBehaviours.ToArray();
            netObject.NetworkedBehaviours = metadata.NetworkBehaviours.ToArray();
            
            // Only add nested objects to the root object
            if (!metadata.IsRoot)
            {
                childNetObjects.Add(netObject);
                netObject.NestedObjects = new Fusion.NetworkObject[0];
            }
            else
                netObject.NestedObjects = childNetObjects.ToArray();
                

            return childNetObjects;
        }
        
        class RegisteredPrefab : Fusion.INetworkPrefabSource
        {
            public Fusion.NetworkPrefabId ID;
            public Fusion.NetworkObject Prefab;

            public void Load(in Fusion.NetworkPrefabLoadContext context)
            {
                context.Loaded(Prefab);
            }

            public void Unload()
            {
                
            }

            public string EditorSummary => "Prefab registered by Foundry.";
        }

        public void RegisterPrefab(GameObject prefab)
        {
            if(!prefab.TryGetComponent(out Fusion.NetworkObject netObject))
            {
                Debug.LogError(
                    $"Prefab {prefab.name} does not have a NetworkObject component. Make sure you call BindNetworkObject on the prefab before attempting to register it.");
                return;
            }
            
            if (!prefab.TryGetComponent(out Foundry.Networking.NetworkObject networkObject))
            {
                Debug.LogError("Prefab does not have a Foundry NetworkObject component.");
                return;
            }

            var guid = Guid.Parse(networkObject.guid);
            var prefabEntry = new RegisteredPrefab
            {
                Prefab = prefab.GetComponent<Fusion.NetworkObject>()
            };
            bool success = NetworkProjectConfig.Global.PrefabTable.TryAdd(guid, prefabEntry, out NetworkPrefabId prefabId);
            Debug.Assert(success, "Failed to add prefab to table.");
            Debug.Log($"Registered prefab {prefab.name} with guid {guid} and id {prefabId}");
            prefabEntry.ID = prefabId;
            registeredPrefabs.Add(prefab, prefabEntry);
        }

        public Task CompleteSceneSetup(ISceneNavigationEntry scene)
        {
           return runnerManager.InitScene();
        }

        public Task SubscribeToStateChangesAsync(INetworkProvider.StateDeltaCallback onStateDelta)
        {
            return runnerManager.SubscribeToStateChangesAsync(onStateDelta);
        }

        public void SetSubscriberInitialStateCallback(Func<byte[]> callback)
        {
            runnerManager.SetSubscriberInitialStateCallback(callback);
        }

        public void SendStateDelta(byte[] delta)
        {
            runnerManager.SendStateDelta(delta);
        }

        public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (registeredPrefabs.TryGetValue(prefab, out var registeredPrefab))
                return FoundryRunnerManager.runner.Spawn(registeredPrefab.ID, position, rotation, FoundryRunnerManager.runner.LocalPlayer).gameObject;
            return FoundryRunnerManager.runner.Spawn(prefab, position, rotation, FoundryRunnerManager.runner.LocalPlayer).gameObject;
        }

        public void Despawn(GameObject gameObject)
        {
            runnerManager.Despawn(gameObject);
        }

        public SessionType sessionType { get; private set;  }

        #endregion
        
        #region Runner callbacks and events
        
        public event NetworkEventHandler SessionConnected;
        public event NetworkErrorEventHandler StartSessionFailed;
        public event NetworkErrorEventHandler SessionDisconnected;
        public event NetworkPlayerEventHandler PlayerJoined;
        public event NetworkPlayerEventHandler PlayerLeft;
        
        public event Func<int, byte[]> OnNewStateSubscriber;

        public void SendSessionConnected()
        {
            SessionConnected?.Invoke();
        }
        
        public void SendSessionDisconnected(string reason)
        {
            SessionDisconnected?.Invoke(reason);
        }
        
        public void SendStartSessionFailed(string reason)
        {
            StartSessionFailed?.Invoke(reason);
        }
        
        public void SendPlayerJoined(int playerId)
        {
            // TODO assign players to the state graph
            PlayerJoined?.Invoke(playerId);
        }
        
        public void SendPlayerLeft(int playerId)
        {
            PlayerLeft?.Invoke(playerId);
        }

        #endregion
        
    }
}
