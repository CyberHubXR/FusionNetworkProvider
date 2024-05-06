using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CyberHub.Brane;
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
        
        private static FusionNetworkProvider provider = BraneApp.GetService<INetworkProvider>() as FusionNetworkProvider;
        private static Dictionary<Scene, FoundryRunnerManager> instances = new();
        
        private FoundryFusionSceneManager sceneManager;
        
        private HashSet<PlayerRef> stateUpdateSubscribers;
        
        INetworkProvider.StateDeltaCallback stateDeltaCallback;
        HashSet<PlayerRef> subscribedToStateFrom;

        private bool autoSubscribeToStateChanges = false;
        
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
        Scene currentScene;
        void Start()
        {
            Debug.Assert(runner, "FoundryRunnerManager requires a Photon NetworkRunner to be assigned.");
            currentScene = SceneManager.GetSceneByBuildIndex(BraneApp.GetService<ISceneNavigator>().CurrentScene.BuildIndex);
            instances.Add(currentScene, this);
        }
        
        void OnDestroy()
        {
            instances.Remove(currentScene);
        }
        
        public static FoundryRunnerManager GetManagerForScene(Scene scene)
        {
            if (instances.TryGetValue(scene, out var instance))
                return instance;
            Debug.LogError("No FoundryRunnerManager found for scene " + scene.name);
            return null;
        }

        public Task InitScene()
        {
            return InitSceneAsync();
        }

        async Task InitSceneAsync()
        {
            while(BraneApp.GetService<ISceneNavigator>().IsNavigating)
                await Task.Yield();
            sceneManager.InitScene();
            while (Object == null)
            {
                await Task.Delay(100);
            }
            while (!Object.IsValid)
            {
                await Task.Delay(100);
            }
        }

        async Task UpdateSharedModeMasterClientID()
        {
            if (runner.IsSharedModeMasterClient)
            {
                MasterClientId = runner.LocalPlayer.PlayerId;
                return;
            }
            
            //FOR SOME REASON they don't expose the master client ID, so we have to use reflection to get it.
            var cloudServicesProp = typeof(NetworkRunner).GetField("_cloudServices", BindingFlags.NonPublic | BindingFlags.Instance);
            Debug.Assert(cloudServicesProp != null, "Internal Fusion API has changed. _cloudServices cannot be accessed. Please report this message to the foundry team.");
            var cloudServices = cloudServicesProp.GetValue(runner);
            
            var communicatorProp = cloudServicesProp.FieldType.GetField("_communicator", BindingFlags.NonPublic | BindingFlags.Instance);
            Debug.Assert(communicatorProp != null, "Internal Fusion API has changed. _communicator cannot be accessed. Please report this message to the foundry team.");
            var comunicator = communicatorProp.GetValue(cloudServices);
            
            var clientProp = communicatorProp.FieldType.GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
            Debug.Assert(clientProp != null, "Internal Fusion API has changed. _client cannot be accessed. Please report this message to the foundry team.");
            var client = clientProp.GetValue(comunicator);
            
            var localPlayerProp = clientProp.FieldType.GetProperty("LocalPlayer");
            Debug.Assert(localPlayerProp != null, "Internal Fusion API has changed. LocalPlayer cannot be accessed. Please report this message to the foundry team.");
            object localPlayer = null;
            while (localPlayer == null)
            {
                localPlayer = localPlayerProp.GetValue(client);
                if(localPlayer == null)
                    await Task.Delay(20);
            }

            var roomRefProp = localPlayerProp.PropertyType.GetProperty("RoomReference",BindingFlags.Instance | BindingFlags.NonPublic);
            Debug.Assert(roomRefProp != null, "Internal Fusion API has changed. RoomReference cannot be accessed. Please report this message to the foundry team.");
            object roomRef = null;
            while (roomRef == null)
            {
                roomRef = roomRefProp.GetValue(localPlayer);
                if(roomRef == null)
                    await Task.Delay(20);
            }
            
            var masterClientIdProp = roomRefProp.PropertyType.GetProperty("MasterClientId");
            Debug.Assert(cloudServicesProp != null, "Internal Fusion API has changed. MasterClientId cannot be accessed. Please report this message to the foundry team.");
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

            
            
            stateUpdateSubscribers = new();
            subscribedToStateFrom = new();
            
            var navigator = BraneApp.GetService<ISceneNavigator>();
            
            sceneManager = new FoundryFusionSceneManager();
            sceneManager.runnerManager = GetComponent<Fusion.NetworkObject>();
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
                if (res.Ok == false) { throw new InvalidOperationException("Fusion runner did not start properly. Will now shutdown. Reason: " + res.ToString()); }
                await UpdateSharedModeMasterClientID();
                sessionStarted = true;
            });
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            provider.SendPlayerJoined(player);
            if (player == runner.LocalPlayer)
                return;
            
            // Subscribe to graph changes from the new player
            if(autoSubscribeToStateChanges)
                SubscribeToStateChanges(player);
        }

        public async void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            stateUpdateSubscribers.Remove(player);

            await UpdateSharedModeMasterClientID();
            provider.SendPlayerLeft(player);
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
            LoadingScreenManager.FailLoad("Fusion session failed to start. Reason: " + reason);
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
        
        public async Task SubscribeToStateChangesAsync(INetworkProvider.StateDeltaCallback onStateDelta)
        {
            stateDeltaCallback = onStateDelta;
            
            autoSubscribeToStateChanges = true;
            await SubscribeAll();
        }

        private async Task SubscribeAll()
        {
            // Wait until this list is populated, because for some reason it's not populated immediately even though we await the start game method
            // As the local player should be included, this should never be empty
            while (!runner.ActivePlayers.Any())
                await Task.Delay(50);
            
            int maxWait = 100000;

            bool unsubedPlayers = true;
            while(unsubedPlayers)
            {
                var playerSubTasks = runner.ActivePlayers.Where(p => p != LocalPlayerId && !subscribedToStateFrom.Contains(p)).Select(SubscribeToStateChanges).ToArray();
                await Task.WhenAll(playerSubTasks);
                unsubedPlayers = playerSubTasks.Length > 0;
            }
        }
        
        private async Task SubscribeToStateChanges(PlayerRef player)
        {
            if (subscribedToStateFrom.Contains(player))
                return;
            
            RPC_SubscribeToGraphChanges(player);
            while (!subscribedToStateFrom.Contains(player))
            {
                RPC_SubscribeToGraphChanges(player);
                await Task.Delay(250);
            }
        }

        public async void SendStateDelta(byte[] delta)
        {
            while (!Object)
                await Task.Yield();
            
            foreach (var player in stateUpdateSubscribers)
                RPC_SendStateDeltaReliable(player, delta);
        }

        private Func<byte[]> subscriberInitialState;
        public void SetSubscriberInitialStateCallback(Func<byte[]> callback)
        {
            subscriberInitialState = callback;
        }
        
        [Rpc(sources: RpcSources.All, targets: RpcTargets.All, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_SubscribeToGraphChanges([RpcTarget] PlayerRef player, RpcInfo info = default)
        {
            // Make sure we don't double subscribe 
            
            if (stateUpdateSubscribers.Contains(info.Source))
            {
                Debug.LogWarning("Ignoring duplicate subscribe to graph changes request from " + info.Source.PlayerId);
                return;
            }
            stateUpdateSubscribers.Add(info.Source);

            RPC_SendInitialStateReliable(info.Source, subscriberInitialState());
        }

        [Rpc(sources: RpcSources.All, targets: RpcTargets.All, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_SendInitialStateReliable([RpcTarget] PlayerRef player, byte[] graphData, RpcInfo info = default)
        {
            if (subscribedToStateFrom.Contains(info.Source))
                return;

            subscribedToStateFrom.Add(info.Source);
            stateDeltaCallback(info.Source, graphData);
        }
        
        [Rpc(sources: RpcSources.All, targets: RpcTargets.All, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_SendStateDeltaReliable([RpcTarget] PlayerRef player, byte[] graphData, RpcInfo info = default)
        {
            try
            {
                stateDeltaCallback(info.Source, graphData);
            }
            catch (Exception e)
            {
                Debug.LogError("Failed to apply network graph delta");
                Debug.LogException(e);
            }
        }
        
        public int ownerChangeCount = 0;
        public Dictionary<int, Action<bool>> ownershipChangeCallbacks = new();
        
        public void ChangeOwner(NetworkId id, Fusion.NetworkObject networkObject, int newOwner, Action<bool> callback)
        {
            if (!provider.IsSessionConnected)
                return;
            
            int callbackId = ownerChangeCount++;
            ownershipChangeCallbacks.Add(callbackId, callback);
            
            RPC_ChangeOwner(networkObject.StateAuthority, networkObject, id.Id, newOwner, callbackId);
        }
        
        [Rpc(sources: RpcSources.All, targets: RpcTargets.All, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_ChangeOwner([RpcTarget] PlayerRef currentObjectOwner, Fusion.NetworkObject netObject, uint objectId, int newOwner, int callbackId, RpcInfo info = default)
        {
            if (netObject.TryGetComponent(out Foundry.Networking.NetworkObject fno))
            {
                bool allowChange = fno.VerifyIDChangeRequest(newOwner);
                if(allowChange)
                    netObject.ReleaseStateAuthority();
                RPC_ReceiveOwnershipChange(info.Source, netObject, allowChange, callbackId);
            }
            else
            {
                bool changeAllowed = (bool)typeof(Fusion.NetworkObject)
                    .GetField("AllowStateAuthorityOverride", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(netObject);
                RPC_ReceiveOwnershipChange(info.Source, netObject, changeAllowed, callbackId);
            }
        }
        
        [Rpc(sources: RpcSources.All, targets: RpcTargets.All, Channel = RpcChannel.Reliable, InvokeLocal = false)]
        void RPC_ReceiveOwnershipChange([RpcTarget] PlayerRef listener, Fusion.NetworkObject netObject, bool result, int callbackId)
        {
            if(ownershipChangeCallbacks.TryGetValue(callbackId, out var callback))
            {
                if(result)
                    netObject.RequestStateAuthority();
                callback(result);
                ownershipChangeCallbacks.Remove(callbackId);
            }
        }
    }

    class FoundryFusionSceneManager : INetworkSceneManager
    {
        public Fusion.NetworkObject runnerManager;
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
            var currentScene = BraneApp.GetService<ISceneNavigator>().CurrentScene;
            var list = FindNetworkObjects(SceneManager.GetSceneByBuildIndex(currentScene.BuildIndex));
            list.Add(runnerManager);
            runner.RegisterSceneObjects(list);
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
