using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
        private GameObject _networkContextHolder;
        private FoundryRunnerManager _runnerManager;

        private Dictionary<GameObject, RegisteredPrefab> registeredPrefabs = new();

        #region Interface Implementations

        public bool IsSessionConnected => _runnerManager;
        public bool IsServer => _runnerManager?.IsServer ?? false;
        public bool IsClient => _runnerManager?.IsClient ?? false;

        public bool IsGraphAuthority => IsServer || (_runnerManager?.runner.IsSharedModeMasterClient ?? false);

        public int LocalPlayerId => _runnerManager.LocalPlayerId;
        public NetworkGraph Graph { get; private set; }

        public Task StartSessionAsync(SessionInfo info)
        {
            sessionType = info.sessionType;
            _networkContextHolder = new GameObject("Network Context");
            UnityEngine.Object.DontDestroyOnLoad(_networkContextHolder);
            
            var runner = _networkContextHolder.AddComponent<Fusion.NetworkRunner>();
            var photonVoiceNetwork = _networkContextHolder.AddComponent<FusionVoiceClient>();
            var recorder = _networkContextHolder.AddComponent<Photon.Voice.Unity.Recorder>();
            photonVoiceNetwork.PrimaryRecorder = recorder;
            photonVoiceNetwork.UseFusionAppSettings = true;
            photonVoiceNetwork.UseFusionAuthValues = true;
            
            _runnerManager = _networkContextHolder.AddComponent<FoundryRunnerManager>();
            _runnerManager.runner = runner;
            _runnerManager.voiceClient = photonVoiceNetwork;
            _runnerManager.recorder = recorder;

            return _runnerManager.StartSession(info);
        }

        public Task StopSessionAsync()
        {
            GameObject.Destroy(_networkContextHolder);
            return Task.CompletedTask;
        }

        private class ConstructionMetadata
        {
            public bool isRoot = false;
            public Foundry.Networking.NetworkObject foundryNetObject;
            public List<Fusion.SimulationBehaviour> simBehaviours = new();
            public List<Fusion.NetworkBehaviour> networkBehaviours = new();

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
                simBehaviours.AddRange(sb);
                networkBehaviours.AddRange(nb);
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
                metadata.isRoot = isRoot;
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
            
            gameObject.AddComponent<SyncedNetworkGraphId>();
            
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
                netObject.Flags |= metadata.isRoot ? NetworkObjectFlags.TypePrefab : NetworkObjectFlags.TypePrefabChild;
            
            // Set guid
            netObject.NetworkGuid = Guid.Parse(foundryNetObject.guid);
            
            // Set behaviours
            netObject.SimulationBehaviours = metadata.simBehaviours.ToArray();
            netObject.NetworkedBehaviours = metadata.networkBehaviours.ToArray();
            
            // Only add nested objects to the root object
            if (!metadata.isRoot)
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

        public void RegisterSceneObjects(IList<GameObject> networkObjects)
        {
            throw new NotImplementedException();
        }

        public GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (registeredPrefabs.TryGetValue(prefab, out var registeredPrefab))
                return _runnerManager.runner.Spawn(registeredPrefab.ID, position, rotation, _runnerManager.runner.LocalPlayer).gameObject;
            return _runnerManager.runner.Spawn(prefab, position, rotation, _runnerManager.runner.LocalPlayer).gameObject;
        }

        public void Destroy(GameObject gameObject)
        {
            _runnerManager.Despawn(gameObject);
        }

        public SessionType sessionType { get; private set;  }

        #endregion
        
        #region Runner callbacks and events
        
        public event NetworkEventHandler SessionConnected;
        public event NetworkErrorEventHandler StartSessionFailed;
        public event NetworkErrorEventHandler SessionDisconnected;
        public event NetworkPlayerEventHandler PlayerJoined;
        public event NetworkPlayerEventHandler PlayerLeft;

        public void SendSessionConnected()
        {
            Graph = new()
            {
                GetMasterID = () => _runnerManager.MasterClientId,
                GetLocalPlayerID = () => LocalPlayerId
            };
            SessionConnected?.Invoke();
        }
        
        public void SendSessionDisconnected(string reason)
        {
            SessionDisconnected?.Invoke(reason);
            Graph = null;
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
