﻿using FlatRedNetwork.Logging;
using FlatRedNetwork.Messaging;
using Lidgren.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace FlatRedNetwork
{
    public class NetworkManager
    {
        /// <summary>
        /// The role of this instance on the network
        /// </summary>
        public NetworkRole Role { get; private set; }

        /// <summary>
        /// The IPEndpoint for the Server
        /// </summary>
        public string ServerAddress { get; private set; }
        
        /// <summary>
        /// The unique identifier for this instance on the network.
        /// </summary>
        public long NetworkId
        {
            get
            {
                if(mNetwork == null)
                {
                    throw new FlatRedNetworkException("Attempted to get NetworkId before Network was initialized.");
                }
                return mNetwork.UniqueIdentifier;
            }
        }

        /// <summary>
        /// The configuration, including application name and port,
        /// used to set up the network
        /// </summary>
        public NetworkConfiguration Configuration { get; private set; }

        /// <summary>
        /// A numerical list of state types used to transfer type as a very small package.
        /// Defined in the constructor from the network configuration
        /// </summary>
        internal static List<Type> EntityStateTypes { get; private set; }
        
        /// <summary>
        /// The current time for the server.
        /// Useful as a consistent timeline for interpolating between client and server states.
        /// </summary>
        public double ServerTime
        {
            get
            {
                double netTime;
                if(Role == NetworkRole.Server)
                {
                    netTime = NetTime.Now;
                }
                else
                {
                    if(mNetwork != null && mNetwork.Connections != null && mNetwork.Connections.Count > 0)
                    {
                        netTime = mNetwork.Connections[0].GetRemoteTime(NetTime.Now);
                    }
                    else
                    {
                        netTime = -1;
                    }
                }
                return netTime;
            }
        }

        /// <summary>
        /// The game arena that controls all client game objects
        /// </summary>
        public INetworkArena GameArena { get; set; }



        /// <summary>
        /// The Lidgren NetPeer instance used to transmit and receive messages
        /// </summary>
        private NetPeer mNetwork;

        /// <summary>
        /// Master list of networked entities
        /// </summary>
        private List<INetworkEntity> mEntities;

        /// <summary>
        /// The logger instance that will be used to log messages.
        /// </summary>
        private ILogger mLog;

        /// <summary>
        /// A counter used to get new IDs for entities
        /// </summary>
        private long mEntityId;


        /// <summary>
        /// Instantiate the Network.
        /// WARNING: If no logger is provided, all messages will be swallowed.
        /// </summary>
        /// <param name="config">Configuration for networking</param>
        /// <param name="arena">The game arena</param>
        /// <param name="log">An ILogger to write messages to</param>
        public NetworkManager(NetworkConfiguration config, ILogger log = null)
        {
            Configuration = config;
            mLog = log ?? new NullLogger();
            EntityStateTypes = config.EntityStateTypes;
        }

        /// <summary>
        /// Reads any messages in the queue and updates Entities accordingly.
        /// Usually called in the game loop
        /// </summary>
        public void Update()
        {
            NetIncomingMessage msg;
            while((msg = mNetwork.ReadMessage()) != null)
            {
                switch(msg.MessageType)
                {
                    case NetIncomingMessageType.VerboseDebugMessage:
                    case NetIncomingMessageType.DebugMessage:
                        mLog.Debug(msg.ReadString());
                        break;
                    case NetIncomingMessageType.WarningMessage:
                        mLog.Warning(msg.ReadString());
                        break;
                    case NetIncomingMessageType.ErrorMessage:
                        mLog.Error(msg.ReadString());
                        break;
                    case NetIncomingMessageType.StatusChanged:
                        ProcessStatusChangedMessage(msg);
                        break;
                    case NetIncomingMessageType.Data:
                        ProcessDataMessage(msg);
                        break;
                }
                mNetwork.Recycle(msg);
            }
        }

        /// <summary>
        /// The dead reckoning cycle.
        /// Broadcasts the state of all entities and forces updates.
        /// Clients should always accept overrides from the server 
        /// during dead reckoning to keep the game in sync.
        /// </summary>
        public void DeadReckon()
        {
            // NOTE: not sure about this idea: Entity owner initiates dead reckoning instead of server
            for(int i = 0; i < mEntities.Count; i++)
            {
                if(mEntities[i].OwnerId == NetworkId)
                {
                    SendDataMessage(mEntities[i], NetworkMessageType.Reckoning);
                }
            }
        }

        /// <summary>
        /// Broadcasts a message to all clients to create the provided entity.
        /// Note that this is broadcast only. No instantiation should happen
        /// at this point.
        /// </summary>
        /// <param name="entity">The entity to create.</param>
        public void RequestCreateEntity(object initialState)
        {
            if(Role == NetworkRole.Server)
            {
                CreateEntity(NetworkId, GetUniqueEntityId(), initialState, ServerTime);
            }
            else
            {
                SendDataMessage(0, NetworkId, initialState, NetworkMessageType.Create);
            }
        }

        /// <summary>
        /// Broadcasts a message to all clients to destroy the provided entity.
        /// Note that this is broadcast only. No destruction should happen until
        /// a message is received.
        /// </summary>
        /// <param name="entity">The entity to destroy.</param>
        public void RequestDestroyEntity(INetworkEntity entity)
        {
            if(Role == NetworkRole.Server)
            {
                DestroyEntity(entity.EntityId);
            }
            else
            {
                SendDataMessage(entity, NetworkMessageType.Destroy);
            }
        }

        /// <summary>
        /// Broadcasts a message to all clients to update the provided entity.
        /// Note that this is broadcast only. No updating should happen until
        /// a message is received.
        /// </summary>
        /// <param name="entity">The entity to update.</param>
        public void RequestUpdateEntity(INetworkEntity entity)
        {
            if(Role == NetworkRole.Server)
            {
                UpdateEntity(entity.EntityId, entity.GetState(), true, ServerTime);
            }
            else
            {
                SendDataMessage(entity, NetworkMessageType.Update);
            }
        }

        /// <summary>
        /// Initializes the network according to the provided role
        /// </summary>
        /// <param name="role">The role to use</param>
        public void Initialize(NetworkRole role)
        {
            Role = role;

            var config = new NetPeerConfiguration(Configuration.ApplicationName);
            config.EnableMessageType(NetIncomingMessageType.WarningMessage);
            config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            config.EnableMessageType(NetIncomingMessageType.DebugMessage);
            config.EnableMessageType(NetIncomingMessageType.ErrorMessage);
            config.EnableMessageType(NetIncomingMessageType.Error);
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);

#if DEBUG
            // Note: these only exist on Lidgren in DEBUG mode
            config.SimulatedLoss = Configuration.SimulatedLoss;
            config.SimulatedMinimumLatency = Configuration.SimulatedMinimumLatency;
            config.SimulatedRandomLatency = Configuration.SimulatedRandomLatencySeconds;
            config.SimulatedDuplicatesChance = Configuration.SimulatedDuplicateChance;
#endif

            mEntities = new List<INetworkEntity>();

            switch(Role)
            {
                case NetworkRole.Client :
                    mNetwork = new NetClient(config);
                    mLog.Debug("Starting client.");
                    break;
                case NetworkRole.Server :
                    config.Port = Configuration.ApplicationPort;
                    mNetwork = new NetServer(config);
                    mLog.Debug("Starting server on port:" + Configuration.ApplicationPort);
                    break;
            }
            mNetwork.Start();
        }

        /// <summary>
        /// Connect to an endpoint
        /// </summary>
        /// <param name="ipaddress">The IP address to connect to</param>
        public void Connect(string address)
        {
            ServerAddress = address;
            mLog.Info("Connecting to: " + ServerAddress + ":" + Configuration.ApplicationPort);
            if(Role == NetworkRole.Client)
            {
                if(ServerAddress == null)
                {
                    string errorMessage = "Bad server address.";
                    mLog.Error(errorMessage);
                    throw new FlatRedNetworkException(errorMessage);
                }
                mNetwork.Connect(address, Configuration.ApplicationPort);
                // TODO: set specific server connection variable here?
            }
            else
            {
                string errorMessage = "Cannot connect while running as Server.";
                mLog.Error(errorMessage);
                throw new FlatRedNetworkException(errorMessage);
            }
        }

        /// <summary>
        /// Closes connections
        /// </summary>
        public void Disconnect()
        {
            mLog.Info("Disconnecting...");
            mNetwork.Shutdown("Disconnecting.");
        }


        /// <summary>
        /// Handles an incoming data message
        /// </summary>
        /// <param name="message">The incoming message</param>
        private void ProcessDataMessage(NetIncomingMessage message)
        {
            NetworkMessage netMsg = new NetworkMessage(message);
            switch(netMsg.Action)
            {
                case NetworkMessageType.Create :
                    // Server creates EntityId on new entities
                    if(Role == NetworkRole.Server)
                    {
                        netMsg.EntityId = GetUniqueEntityId();
                    }
                    CreateEntity(netMsg.OwnerId, netMsg.EntityId, netMsg.Payload, netMsg.MessageSentTime);
                    break;
                case NetworkMessageType.Destroy :
                    // TODO: do something with time here?
                    DestroyEntity(netMsg.EntityId);
                    break;
                case NetworkMessageType.Update :
                case NetworkMessageType.Reckoning :
                    bool isReckoning = netMsg.Action == NetworkMessageType.Reckoning;
                    UpdateEntity(netMsg.EntityId, netMsg.Payload, isReckoning, netMsg.MessageSentTime);
                    break;
                default:
                    throw new FlatRedNetworkException("Message type not implemented: " + netMsg.Action.ToString());
            }
        }

        /// <summary>
        /// Called when a Create message has arrived, gets an entity from the game screen.
        /// </summary>
        /// <param name="ownerId">The NetworkId of the peer that controls the new entity.</param>
        /// <param name="entityId">The unique identifier for the entity.</param>
        /// <param name="payload">The object that will be used to apply the entity's starting state.</param>
        /// <param name="time">The time the message was sent, used for projecting the state to current time.</param>
        private void CreateEntity(long ownerId, long entityId, object payload, double time)
        {
            // check if already exists.
            INetworkEntity targetEntity = mEntities.Where(e => e.EntityId == entityId).SingleOrDefault();

            if(targetEntity == null)
            {
                targetEntity = GameArena.RequestCreateEntity(ownerId, entityId, payload);
            }
            else
            {
                mLog.Warning("Attempted to create entity for ID that already exists: " + entityId);
            }
            
            targetEntity.OwnerId = ownerId;
            targetEntity.EntityId = entityId;
            targetEntity.UpdateState(payload, time);
            mEntities.Add(targetEntity);

            BroadcastIfServer(entityId, ownerId, payload, NetworkMessageType.Create);
        }

        /// <summary>
        /// Called when a Destroy message has arrived, destroys an entity.
        /// </summary>
        /// <param name="entityId">The unique ID of the entity to be destroyed</param>
        private void DestroyEntity(long entityId)
        {
            INetworkEntity targetEntity = mEntities.Where(e => e.EntityId == entityId).SingleOrDefault();
            if(targetEntity != null)
            {
                mEntities.Remove(targetEntity);
                GameArena.RequestDestroyEntity(targetEntity);
            }
            else
            {
                mLog.Warning("Couldn't find entity marked for destruction: " + entityId);
            }

            BroadcastIfServer(entityId, targetEntity.OwnerId, null, NetworkMessageType.Destroy);
        }

        /// <summary>
        /// Called when an Update message has arrived, applies the new state to the entity.
        /// </summary>
        /// <param name="entityId">The unique identifier for the entity.</param>
        /// <param name="payload">The object that will be used to apply the entity's starting state.</param>
        /// <param name="isReckoning">True if this is a reckoning update.</param>
        /// <param name="time">The time the message was sent, used for projecting the state to current time.</param>
        private void UpdateEntity(long entityId, object payload, bool isReckoning, double time)
        {
            INetworkEntity targetEntity = mEntities.Where(e => e.EntityId == entityId).SingleOrDefault();

            // TODO: automatically create entity?
            // ignore if null, entity creation message may not have arrived
            if(targetEntity != null)
            {
                targetEntity.UpdateState(payload, time, isReckoning);
                BroadcastIfServer(entityId, targetEntity.OwnerId, payload, NetworkMessageType.Update);
            }
            else
            {
                mLog.Warning("Couldn't find entity to update: " + entityId);
            }
        }

        /// <summary>
        /// When the server receives a message from a client, it needs to notify other clients. This is called in
        /// all of the Create, Destroy and Update methods but only performs actual logic if running as server.
        /// </summary>
        /// <param name="entityId">The ID of the affected entity.</param>
        /// <param name="ownerId">The owner of the affected entity</param>
        /// <param name="payload">The payload from the original message.</param>
        /// <param name="action">Type type of message, determining the action to be taken.</param>
        private void BroadcastIfServer(long entityId, long ownerId, object payload, NetworkMessageType action)
        {
            if(Role == NetworkRole.Server)
            {
                SendDataMessage(entityId, ownerId, payload, action); 
            }
        }

        /// <summary>
        /// Called by the Update method when status change messages are received.
        /// Connection, disconnection and approval requests, for example.
        /// </summary>
        /// <param name="message">The incoming message.</param>
        private void ProcessStatusChangedMessage(NetIncomingMessage message)
        {
            NetConnectionStatus newStatus = (NetConnectionStatus)message.ReadByte();

            switch(newStatus)
            {
                case NetConnectionStatus.Connected :
                    mLog.Info("Connected to: " + message.SenderEndPoint);

                    // send all game objects to new peer
                    if(Role == NetworkRole.Server)
                    {
                        SendCreateAllEntities(message.SenderConnection);
                    }
                    break;


                case NetConnectionStatus.Disconnected:
                    mLog.Info("Disconnected.");

                    // destroy all game objects owned by disconnected peer
                    if(Role == NetworkRole.Server)
                    {
                        DestroyAllOwnedById(message.SenderConnection.RemoteUniqueIdentifier);
                    }

                    break;


                case NetConnectionStatus.RespondedAwaitingApproval :
                    // TODO: set max connections and deny if full
                    message.SenderConnection.Approve();
                    break;
            }
            
        }

        /// <summary>
        /// Sends a Create message for all entities in the local collection.
        /// This should generally only be called in Server mode.
        /// </summary>
        /// <param name="recipient">An individual receipient.
        /// If not supplied, the message will be sent to all connections.</param>
        private void SendCreateAllEntities(NetConnection recipient = null)
        {
            foreach(INetworkEntity entity in mEntities)
            {
                SendDataMessage(entity, NetworkMessageType.Create, recipient: recipient);
            }
        }

        /// <summary>
        /// Sends a Destroy message for all entities owned by a specific NetworkId.
        /// Usually called in Server mode when a client disconnects.
        /// </summary>
        /// <param name="ownerId">The OwnerId of entities to destroy.</param>
        private void DestroyAllOwnedById(long ownerId)
        {
            for(int i = mEntities.Count - 1; i > -1; i--)
            {
                INetworkEntity entity = mEntities[i];

                if(entity.OwnerId == ownerId)
                {
                    SendDataMessage(entity, NetworkMessageType.Destroy);
                    if(Role == NetworkRole.Server)
                    {
                        DestroyEntity(entity.EntityId);
                    }
                }
            }
        }

        /// <summary>
        /// Uses the provided entity to compose a data message.
        /// ReliableSequenced method is suggested to balance performance with deliverability.
        /// </summary>
        /// <param name="entity">The entity to build a message from.</param>
        /// <param name="action">The type of message to send.</param>
        /// <param name="method">Delivery method.</param>
        /// <param name="recipient">The recipient connection. Will send to all if null.</param>
        private void SendDataMessage(INetworkEntity entity, NetworkMessageType action, NetDeliveryMethod method = NetDeliveryMethod.ReliableSequenced, NetConnection recipient = null)
        {
            if(Role != NetworkRole.Server && entity.OwnerId != NetworkId)
            {
                throw new FlatRedNetworkException("Cannot send an update for an entity that is not owned by this client!");
            }

            object payload = entity.GetState();
            SendDataMessage(entity.EntityId, entity.OwnerId, payload, action, method, recipient);
        }

        /// <summary>
        /// Composes and sends a data message.
        /// </summary>
        /// <param name="entityId">Unique ID of the affected entity.</param>
        /// <param name="ownerId">The entity owner's NetworkId</param>
        /// <param name="payload">The state describing changes to the entity.</param>
        /// <param name="action">The type of message that determines the ultimate action taken.</param>
        /// <param name="method">The delivery method.</param>
        /// <param name="recipient"></param>
        private void SendDataMessage(long entityId, long ownerId, object payload, NetworkMessageType action, NetDeliveryMethod method = NetDeliveryMethod.ReliableSequenced, NetConnection recipient = null)
        {
            int payloadTypeId = -1;
            Type type;

            if(payload != null)
            {
                try
                {
                    type = payload.GetType();
                    payloadTypeId = Configuration.EntityStateTypes.IndexOf(type);
                }
                catch(Exception ex)
                {
                    throw new FlatRedNetworkException("Failed to get entity state.", ex);
                }

                if(payloadTypeId == -1)
                {
                    throw new FlatRedNetworkException("Failed to find ID for type: " + type.ToString());
                }
            }

            NetworkMessage message = new NetworkMessage();
            message.SenderId = NetworkId;
            message.OwnerId = ownerId;
            message.EntityId = entityId;
            message.PayloadTypeId = payloadTypeId;
            message.Action = action;
            message.Payload = payload;

            NetOutgoingMessage outgoingMessage = mNetwork.CreateMessage();
            message.Encode(outgoingMessage, ServerTime);
            switch(Role)
            {
                case NetworkRole.Server:
                    if(recipient == null)
                    {
                        ((NetServer)mNetwork).SendToAll(outgoingMessage, method);
                    }
                    else
                    {
                        ((NetServer)mNetwork).SendMessage(outgoingMessage, recipient, method);
                    }
                    break;
                case NetworkRole.Client:
                    var server = ((NetClient)mNetwork).ServerConnection;
                    mNetwork.SendMessage(outgoingMessage, server, method);
                    break;
                default:
                    throw new FlatRedNetworkException("Attempted to send message as an unsupported role: " + Role.ToString());
            }
        }

        private long GetUniqueEntityId()
        {
            // TODO: handle max long ID? God forbid someone makes a game that big with this library...
            long id = mEntityId;
            mEntityId++;
            return id;
        }
    }
}
