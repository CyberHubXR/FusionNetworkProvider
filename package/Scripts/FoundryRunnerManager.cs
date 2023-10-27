using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Foundry.Core.Serialization;
using Foundry.Services;
using Fusion;
using Fusion.Sockets;
using Photon.Voice.Fusion;
using Photon.Voice.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace Foundry.Networking
{
    /// <summary>
    /// A monobehavior that manages Core Fusion scripts and provides an interface for them to interact with the FusionNetworkProvider.
    /// </summary>
    /// <see cref="FusionNetworkProvider"/>
    public class FoundryRunnerManager : NetworkBehaviour, INetworkRunnerCallbacks
    {
        public NetworkRunner runner;
        
        public FusionVoiceClient voiceClient;
        public Recorder recorder;
        public bool IsClient => runner.IsClient;
        public bool IsServer => runner.IsServer;

        public int LocalPlayerId => runner.LocalPlayer.PlayerId;
        
        public int MasterClientId { get; private set; }
        
        private static FusionNetworkProvider provider = FoundryApp.GetService<INetworkProvider>() as FusionNetworkProvider;
        
        private FoundryFusionSceneManager sceneManager;
        
        private static HashSet<PlayerRef> graphUpdateSubscribers;

        private static bool graphInitalized = false;
        
        /// <summary>
        /// This implementations method of linking a fusion network id to a foundry network id, we can't rely on
        /// [Networked] properties because they only seem to be sync on a change and dont receive an initial value,
        /// and RPC calls take a few ticks to arrive so we can't rely on them either. So we use this dictionary and send
        /// changes to it along with our network graph data.
        /// </summary>
        /// 
        private static Dictionary<uint, NetworkId> fusionIdToGraphId = new(); 
        private static Dictionary<uint, Action<NetworkId>> fusionIdToGraphCallbacks = new();
        private static Queue<IdAddEvent> idMapChanges = new();
        
        /// <summary>
        /// We only need an add event as we can clean up this list using OnDestroy() of the accessor scripts.
        /// </summary>
        private struct IdAddEvent : IFoundrySerializable
        {
            public uint fusionId;
            public NetworkId graphId;
            
            public void Serialize(FoundrySerializer serializer)
            {
                serializer.Serialize(in fusionId);
                serializer.Serialize(in graphId);
            }

            public void Deserialize(FoundryDeserializer deserializer)
            {
                deserializer.Deserialize(ref fusionId);
                deserializer.Deserialize(ref graphId);
            }
        }

        public static void AddOrReplaceMappedId(uint fusionId, NetworkId graphId, bool recordEvent = true)
        {
            fusionIdToGraphId[fusionId] = graphId;
            if(!recordEvent)
                return;
            idMapChanges.Enqueue(new IdAddEvent
            {
                fusionId = fusionId,
                graphId = graphId
            });
        }

        public static void RemoveMappedId(uint fusionId, NetworkId graphId)
        {
            if (!provider.IsSessionConnected)
                return;
            // If the id doesn't match, that probbably means fusion reassigned the id to a new object, and we don't want to remove the new mapping.
            if (!fusionIdToGraphId[fusionId].Equals(graphId))
                return;
            fusionIdToGraphId.Remove(fusionId);
        }

        /// <summary>
        /// Get the graph id associated with a fusion id, if it exists.
        /// </summary>
        /// <param name="fusionId"></param>
        /// <returns></returns>
        public static NetworkId GetGraphId(uint fusionId)
        {
            if(fusionIdToGraphId.TryGetValue(fusionId, out var graphId))
                return graphId;
            return NetworkId.Invalid;
        }
        
        /// <summary>
        /// Wait for the graph id associated with a fusion id to be assigned, then call the callback with the graph id.
        /// </summary>
        /// <param name="fusionId"></param>
        /// <param name="callback"></param>
        /// <exception cref="NotImplementedException"></exception>
        public static void GetGraphIdAsync(uint fusionId, Action<NetworkId> callback)
        {
            if(fusionIdToGraphId.TryGetValue(fusionId, out var graphId))
            {
                callback(graphId);
                return;
            }
            
            if(!fusionIdToGraphCallbacks.TryAdd(fusionId, callback))
                fusionIdToGraphCallbacks[fusionId] += callback;
        }

        private static byte[] SerializeIdMapFull()
        {
            MemoryStream stream = new MemoryStream();
            FoundrySerializer serializer = new(stream);
            
            int changeCount = fusionIdToGraphId.Count;
            serializer.Serialize(in changeCount);
            foreach(KeyValuePair<uint, NetworkId> pair in fusionIdToGraphId)
            {
                var change = new IdAddEvent
                {
                    fusionId = pair.Key,
                    graphId = pair.Value
                };
                serializer.Serialize(in change);
            }

            return stream.ToArray();
        }
        
        // When joining a session we need to report our local references that not everyone may have caught due to the lag in rpc subscription calls.
        private static byte[] SerializeIdMapLocal()
        {
            MemoryStream stream = new MemoryStream();
            FoundrySerializer serializer = new(stream);
            
            int changeCount = fusionIdToGraphId.Count;
            serializer.Serialize(in changeCount);
            foreach(KeyValuePair<uint, NetworkId> pair in fusionIdToGraphId)
            {
                if(pair.Value.Owner != provider.LocalPlayerId)
                    continue;
                
                var change = new IdAddEvent
                {
                    fusionId = pair.Key,
                    graphId = pair.Value
                };
                serializer.Serialize(in change);
            }

            return stream.ToArray();
        }

        private static byte[] SerializeIdMapDelta()
        {
            MemoryStream stream = new MemoryStream();
            FoundrySerializer serializer = new(stream);
            
            int changeCount = idMapChanges.Count;
            serializer.Serialize(in changeCount);
            while (idMapChanges.Count > 0)
            {
                var change = idMapChanges.Dequeue();
                serializer.Serialize(in change);
            }

            return stream.ToArray();
        }

        private static void ApplyIdMapDelta(byte[] mapDelta)
        {
            MemoryStream stream = new MemoryStream(mapDelta);
            FoundryDeserializer deserializer = new(stream);
            int changeCount = 0;
            deserializer.Deserialize(ref changeCount);
            while (changeCount > 0)
            {
                IdAddEvent change = new();
                deserializer.Deserialize(ref change);
                AddOrReplaceMappedId(change.fusionId, change.graphId, false);
                
                // Call any callbacks waiting on this id
                if(fusionIdToGraphCallbacks.TryGetValue(change.fusionId, out var callback))
                {
                    callback(change.graphId);
                    fusionIdToGraphCallbacks.Remove(change.fusionId);
                }
                --changeCount;
            }
        }
        
        void Start()
        {
            Debug.Assert(runner, "FoundryRunnerManager requires a Photon NetworkRunner to be assigned.");
            
            graphUpdateSubscribers = new();
            graphInitalized = runner.IsSharedModeMasterClient || runner.IsServer;
            
            
                
        }

        void Update()
        {
            //Dirty work-around to make sure this gets called on the main thread.
            if (sessionStarted)
            {
                sceneManager.InitScene();
                sessionStarted = false;
                StartCoroutine(ReportGraphChanges());
            }
        }

        void UpdateSharedModeMasterClientID()
        {
            if (runner.IsSharedModeMasterClient)
            {
                MasterClientId = runner.LocalPlayer.PlayerId;
                return;
            }
            
            //FOR SOME REASON they don't expose the master client ID, so we have to use reflection to get it.
            var cloudServicesProp = typeof(NetworkRunner).GetField("_cloudServices", BindingFlags.NonPublic | BindingFlags.Instance);
            var cloudServices = cloudServicesProp.GetValue(runner);
            
            var communicatorProp = cloudServicesProp.FieldType.GetField("_communicator", BindingFlags.NonPublic | BindingFlags.Instance);
            var comunicator = communicatorProp.GetValue(cloudServices);
            
            var clientProp = communicatorProp.FieldType.GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
            var client = clientProp.GetValue(comunicator);
            
            var localPlayerProp = clientProp.FieldType.GetProperty("LocalPlayer");
            var localPlayer = localPlayerProp.GetValue(client);

            var roomRefProp = localPlayerProp.PropertyType.GetProperty("RoomReference",BindingFlags.Instance | BindingFlags.NonPublic);
            var roomRef = roomRefProp.GetValue(localPlayer);
            
            var masterClientIdProp = roomRefProp.PropertyType.GetProperty("MasterClientId");
            var masterClientActorID = (int)masterClientIdProp.GetValue(roomRef);

            // We've just given up on any semblance of performance here
            foreach(var player in runner.ActivePlayers)
            {
                if (runner.GetPlayerActorId(player) == masterClientActorID)
                {
                    MasterClientId = player.PlayerId;
                    break;
                }
            }
        }

        IEnumerator ReportGraphChanges()
        {
            if (runner.IsServer)
                throw new InvalidOperationException("Foundry's Network Graph does not support Fusion's server-client model at this time. Please tell us if you want this feature!");
            while (runner.IsRunning)
            {
                //We yield at the beginning of the loop so that continue statements don't crash the editor.
                yield return new WaitForSeconds(1f / runner.Simulation.Config.TickRate);
                if(graphUpdateSubscribers.Count == 0)
                    continue;
                var graphDelta = provider.Graph.GenerateDelta();
                var idMapDelta = SerializeIdMapDelta();
                if (graphDelta.data.Length == 0 && idMapDelta.Length == 0)
                    continue;
                // Report our changes to all the other players
                foreach (var player in graphUpdateSubscribers)
                {
                    if(player == runner.LocalPlayer)
                        continue;
                    RPC_SendGraphDeltaReliable(runner, player, graphDelta.data, idMapDelta);
                }

            }
        }


        private bool sessionStarted = false;
        public Task StartSession(SessionInfo info)
        {
            GameMode fusionGameMode;
            switch (info.sessionType)
            {
                case SessionType.Shared:
                    fusionGameMode = GameMode.Shared;
                    break;
                case SessionType.Client:
                    fusionGameMode = GameMode.Client;
                    break;
                case SessionType.Host:
                    fusionGameMode = GameMode.Host;
                    break;
                case SessionType.Server:
                    fusionGameMode = GameMode.Server;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            
            var navigator = FoundryApp.GetService<ISceneNavigator>();
            
            sceneManager = new FoundryFusionSceneManager();
            var sgr = runner.StartGame(new StartGameArgs
            {
                GameMode = fusionGameMode,
                SessionName = info.sessionName,
                Scene = navigator.CurrentScene.BuildIndex,
                SceneManager = sceneManager
            });
            return Task.Run(async () =>
            {
                var res = await sgr;
                if (res.Ok == false) { throw new InvalidOperationException("Fusion runner did not start properly. Will now shutdown. Reason: " + res.ShutdownReason); }
                sessionStarted = true;
                UpdateSharedModeMasterClientID();
            });
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            provider.SendPlayerJoined(player.PlayerId);
            if (player == runner.LocalPlayer)
                return;
            
            // Subscribe to graph changes from the new player
            RPC_SubscribeToGraphChanges(runner, player);
            
            // When a player joins, we need to send them the current graph state.
            if (runner.IsServer || runner.IsSharedModeMasterClient)
            {
                // First serialize our current delta and send it out to all players, we do this to make sure we don't resend construction events to the player that just joined, as they would already have them.
                var graphDelta = provider.Graph.GenerateDelta();
                var idMapDelta = SerializeIdMapDelta();
                if (graphDelta.data.Length != 0)
                {
                    // Report our changes to all players but the one that just joined
                    foreach (var p in graphUpdateSubscribers)
                    {
                        if(player == runner.LocalPlayer || p == player)
                            continue;
                        RPC_SendGraphDeltaReliable(runner, player, graphDelta.data, idMapDelta);
                    }
                }
                
                // Send the full graph to the new joiner
                graphDelta = provider.Graph.SerializeFull();
                idMapDelta = SerializeIdMapFull();
                RPC_SendGraphDeltaReliable(runner, player, graphDelta.data, idMapDelta);
                
                // Subscribe to graph changes for the new player, that way they at least don't miss any from the graph authority.
                graphUpdateSubscribers.Add(player);
            }
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (graphUpdateSubscribers.Contains(player))
            {
                graphUpdateSubscribers.Remove(player);
            }
            provider.SendPlayerLeft(player.PlayerId);

            if (!runner.IsSharedModeMasterClient)
                return;

            var id = player.PlayerId;
            var orphanedNodes = provider.Graph.idToNode.Select(pair => pair).Where(node => node.Key.Owner == id).ToList();
            foreach (var node in orphanedNodes)
            {
                // Dirty work around for not re-deleting objects that were already deleted recursively.
                if (!provider.Graph.idToNode.ContainsKey(node.Key))
                    continue;
                if (node.Value.AssociatedObject)
                {
                    // We could keep the old id, but it's probably better to just reassign it since we can to avoid collisions.
                    provider.Graph.ChangeId(node.Key, provider.Graph.NewId(runner.LocalPlayer.PlayerId));
                }
                else
                {
                    // If the node is not associated with an object, we can just remove it, as it was probably orphaned.
                    provider.Graph.RemoveNode(node.Key);
                }
            }
            
            UpdateSharedModeMasterClientID();
        }

        public void OnInput(NetworkRunner runner, NetworkInput input)
        {
            
        }

        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input)
        {
            
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            string reason;
            switch (shutdownReason)
            {
                case ShutdownReason.Ok:
                    reason = "Ok";
                    break;
                case ShutdownReason.Error:
                    reason = "A Fusion error occurred.";
                    break;
                case ShutdownReason.IncompatibleConfiguration:
                    reason = "Incompatible Fusion configuration.";
                    break;
                case ShutdownReason.ServerInRoom:
                    reason = "Server is in room.";
                    break;
                case ShutdownReason.DisconnectedByPluginLogic:
                    reason = "Disconnected by plugin logic.";
                    break;
                case ShutdownReason.GameClosed:
                    reason = "Game closed.";
                    break;
                case ShutdownReason.GameNotFound:
                    reason = "Game not found.";
                    break;
                case ShutdownReason.MaxCcuReached:
                    reason = "Max CCU reached.";
                    break;
                case ShutdownReason.InvalidRegion:
                    reason = "Invalid region.";
                    break;
                case ShutdownReason.GameIdAlreadyExists:
                    reason = "Game ID already exists.";
                    break;
                case ShutdownReason.GameIsFull:
                    reason = "Game is full.";
                    break;
                case ShutdownReason.InvalidAuthentication:
                    reason = "Invalid authentication.";
                    break;
                case ShutdownReason.CustomAuthenticationFailed:
                    reason = "Custom authentication failed.";
                    break;
                case ShutdownReason.AuthenticationTicketExpired:
                    reason = "Authentication ticket expired.";
                    break;
                case ShutdownReason.PhotonCloudTimeout:
                    reason = "Photon Cloud timeout.";
                    break;
                case ShutdownReason.AlreadyRunning:
                    reason = "Already running.";
                    break;
                case ShutdownReason.InvalidArguments:
                    reason = "Invalid arguments.";
                    break;
                case ShutdownReason.HostMigration:
                    reason = "Host migration.";
                    break;
                case ShutdownReason.ConnectionTimeout:
                    reason = "Connection timeout.";
                    break;
                case ShutdownReason.ConnectionRefused:
                    reason = "Connection refused.";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shutdownReason), shutdownReason, null);
            }
            provider.SendSessionDisconnected(reason);
        }

        public void OnConnectedToServer(NetworkRunner runner)
        {
            provider.SendSessionConnected();
        }

        public void OnDisconnectedFromServer(NetworkRunner runner)
        {
            provider.SendSessionDisconnected("Disconnected from server.");
        }

        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
        {
            // TODO - handle connect request, this is probably where we'll want to do our authentication
        }

        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
            string reasonText;
            switch (reason)
            {
                case NetConnectFailedReason.Timeout:
                    reasonText = "Connection timed out.";
                    break;
                case NetConnectFailedReason.ServerFull:
                    reasonText = "Server is full.";
                    break;
                case NetConnectFailedReason.ServerRefused:
                    reasonText = "Server refused connection.";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
            }
            provider.SendStartSessionFailed(reasonText);
        }

        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message)
        {
            
        }

        public void OnSessionListUpdated(NetworkRunner runner, List<global::Fusion.SessionInfo> sessionList)
        {
            
        }

        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data)
        {
            
        }

        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken)
        {
            
        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ArraySegment<byte> data)
        {
            
        }

        public void OnSceneLoadDone(NetworkRunner runner)
        {
            
        }

        public void OnSceneLoadStart(NetworkRunner runner)
        {
            
        }

        public void Despawn(GameObject o)
        {
            runner.Despawn(o.GetComponent<Fusion.NetworkObject>());
        }
        
        [Rpc(sources: RpcSources.All, targets: RpcTargets.All, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        static void RPC_SubscribeToGraphChanges(NetworkRunner runner, [RpcTarget] PlayerRef player, RpcInfo info = default)
        {
            // Make sure we don't double subscribe 
            if (graphUpdateSubscribers.Contains(info.Source))
                return;
            
            graphUpdateSubscribers.Add(info.Source);
            
            //Update the new subscriber with the current map state
            RPC_SendGraphDeltaReliable(runner, info.Source, new byte[0], SerializeIdMapFull());
        }
        
        [Rpc(sources: RpcSources.All, targets: RpcTargets.All, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        static void RPC_SendGraphDeltaReliable(NetworkRunner runner, [RpcTarget] PlayerRef player, byte[] graphData, byte[] idMapData, RpcInfo info = default)
        {
            NetworkGraphDelta delta = new NetworkGraphDelta
            {
                data = graphData
            };
            try
            {
                if(idMapData.Length > 0)
                    ApplyIdMapDelta(idMapData);
                if(graphData.Length > 0)
                    provider.Graph.ApplyDelta(ref delta, info.Source.PlayerId);
            }
            catch (Exception e)
            {
                Debug.LogError("Failed to apply network graph delta: \"" + e.Message + "\"\n Source: " + e.Source + "\n Stack Trace: " + e.StackTrace);
                #if UNITY_EDITOR
                throw;
                #endif
            }

            // If this was the first time we received an update to the graph (Only could be a full graph from the graph authority), we need to subscribe to updates from the other players.
            if (!graphInitalized)
            {
                foreach (var p in runner.ActivePlayers)
                {
                    if (p == runner.LocalPlayer)
                        continue;
                    RPC_SubscribeToGraphChanges(runner, p);
                }

                graphInitalized = true;
            }
        }
    }

    class FoundryFusionSceneManager : INetworkSceneManager
    {
        bool loadInProgress = true;
        private NetworkRunner runner;
        
        public void Initialize(NetworkRunner runner)
        {
            this.runner = runner;
        }

        public void Shutdown(NetworkRunner runner)
        {
            
        }

        public bool IsReady(NetworkRunner runner)
        {
            return !loadInProgress;
        }

        public void InitScene()
        {
            runner.InvokeSceneLoadStart();
            var currentScene = FoundryApp.GetService<ISceneNavigator>().CurrentScene;
            runner.RegisterSceneObjects(FindNetworkObjects(SceneManager.GetSceneByBuildIndex(currentScene.BuildIndex)));
            runner.InvokeSceneLoadDone();
            loadInProgress = false;
        }

        private List<Fusion.NetworkObject> FindNetworkObjects(Scene scene) {

            var networkObjects = new List<Fusion.NetworkObject>();
            var gameObjects = scene.GetRootGameObjects();
            var result = new List<Fusion.NetworkObject>();

            // get all root gameobjects and move them to this runners scene
            foreach (var go in gameObjects) {
                networkObjects.Clear();
                go.GetComponentsInChildren(true, networkObjects);

                foreach (var sceneObject in networkObjects) {
                    if (sceneObject.Flags.IsSceneObject()) {
                        if (sceneObject.gameObject.activeInHierarchy || sceneObject.Flags.IsActivatedByUser()) {
                            result.Add(sceneObject);
                        }
                    }
                }
            }

            return result;
        }
    }
}
