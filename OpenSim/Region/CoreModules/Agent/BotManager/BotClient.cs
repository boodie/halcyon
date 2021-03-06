/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 * 
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 * 
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenMetaverse;
using TransactionInfoBlock = OpenMetaverse.Packets.MoneyBalanceReplyPacket.TransactionInfoBlock;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using System.Net;
using System.Xml;
using OpenSim.Region.Framework.Interfaces;
using log4net;
using System.Reflection;
using OpenMetaverse.StructuredData;

namespace OpenSim.Region.CoreModules.Agent.BotManager
{
    public class BotClient : IBot, IClientAPI
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        #region Declares

        private UUID m_UUID, m_sessionId;
        public string m_firstName, m_lastName;
        private int m_animationSequenceNumber = 1;
        private uint m_circuitCode;
        private Scene m_scene;
        private Dictionary<string, UUID> m_defaultAnimations = new Dictionary<string, UUID>();
        private bool m_frozenUser = false;
        private bool m_closing = false;

        #endregion

        #region Constructor
        public BotClient(string firstName, string lastName, Scene scene, Vector3 startPos, UUID ownerID)
        {
            m_circuitCode = (uint)Util.RandomClass.Next(0, int.MaxValue);
            m_UUID = UUID.Random();
            m_sessionId = UUID.Random();
            m_firstName = firstName;
            m_lastName = lastName;
            m_scene = scene;
            StartPos = startPos;
            OwnerID = ownerID;
            MovementController = new BotMovementController(this);
            RegisteredScriptsForPathUpdateEvents = new List<UUID>();
            TimeCreated = DateTime.Now;

            InitDefaultAnimations();
        }
        #endregion

        #region IBot Properties

        public UUID OwnerID { get; private set; }

        public OpenMetaverse.UUID AgentID { get { return m_UUID; } }

        public BotMovementController MovementController { get; private set; }

        public List<UUID> RegisteredScriptsForPathUpdateEvents { get; private set; }

        public DateTime TimeCreated { get; private set; }

        public bool Frozen { get { return m_frozenUser; } }

        #endregion

        #region IBot Methods

        #region Bot Property Methods

        public void Close()
        {
            if (!m_closing)
            {
                m_closing = true;
                MovementController.StopMovement();
                m_scene.RemoveClient(AgentID);

                // Fire the callback for this connection closing
                if (OnConnectionClosed != null)
                    OnConnectionClosed(this);
            }
        }

        #endregion

        #region Region Interaction Methods

        public bool SitOnObject(UUID objectID)
        {
            if (m_frozenUser)
                return false;

            ScenePresence sp = m_scene.GetScenePresence(m_UUID);
            if (sp == null)
                return false;
            SceneObjectPart child = m_scene.GetSceneObjectPart(objectID);
            if (child == null)
                return false;

            sp.HandleAgentRequestSit(sp.ControllingClient, AgentID, objectID, Vector3.Zero);
            return true;
        }

        public bool StandUp()
        {
            if (m_frozenUser)
                return false;

            ScenePresence sp = m_scene.GetScenePresence(m_UUID);
            if (sp == null)
                return false;

            sp.StandUp(false, true);
            return true;
        }

        public bool TouchObject(UUID objectID)
        {
            if (m_frozenUser)
                return false;

            ScenePresence sp = m_scene.GetScenePresence(AgentID);
            if (sp == null)
                return false;

            SceneObjectPart part = m_scene.GetSceneObjectPart(objectID);
            if (part == null)
                return false;

            m_scene.ProcessObjectGrab(part.LocalId, Vector3.Zero, this, null);
            m_scene.ProcessObjectDeGrab(part.LocalId, this, null);
            return true;
        }

        #endregion

        #region Bot Animation Methods

        public bool StartAnimation(UUID animID, string anim, UUID objectID)
        {
            if (m_frozenUser)
                return false;

            ScenePresence sp = m_scene.GetScenePresence(AgentID);
            if (sp == null)
                return false;

            if (animID == UUID.Zero)
                sp.AddAnimation(anim, objectID);
            else
                sp.AddAnimation(animID, objectID);
            return true;
        }

        public bool StopAnimation(UUID animID, string anim)
        {
            if (m_frozenUser)
                return false;

            ScenePresence sp = m_scene.GetScenePresence(AgentID);
            if (sp == null)
                return false;

            if (animID == UUID.Zero)
                sp.RemoveAnimation(anim);
            else
                sp.RemoveAnimation(animID);
            return true;
        }

        #endregion

        #region Bot Movement Methods

        public bool SetSpeed(float speed)
        {
            if (m_frozenUser)
                return false;

            ScenePresence sp = m_scene.GetScenePresence(AgentID);
            if (sp == null)
                return false;

            sp.SpeedModifier = speed;
            return true;
        }

        #endregion

        #region Chat Methods

        public void Say(int channel, string message, ChatTypeEnum sourceType)
        {
            if (m_frozenUser)
                return;

            if (channel == 0 && sourceType != ChatTypeEnum.StartTyping && sourceType != ChatTypeEnum.StopTyping)
            {
                message = message.Trim();
                if (string.IsNullOrEmpty(message))
                {
                    return;
                }
            }

            if (sourceType == ChatTypeEnum.StartTyping)
            {
                StartAnimation(UUID.Zero, "TYPE", UUID.Zero);
            }
            else if (sourceType == ChatTypeEnum.StopTyping)
            {
                StopAnimation(UUID.Zero, "TYPE");
            }

            OSChatMessage chatFromClient = new OSChatMessage();
            chatFromClient.Channel = channel;
            chatFromClient.From = Name;
            chatFromClient.Message = message;
            chatFromClient.Position = StartPos;
            chatFromClient.Scene = m_scene;
            chatFromClient.SenderUUID = AgentId;
            chatFromClient.Type = sourceType;

            // Force avatar position to be server-known avatar position. (Former contents of FixPositionOfChatMessage.)
            ScenePresence avatar;
            if (m_scene.TryGetAvatar(m_UUID, out avatar))
                chatFromClient.Position = avatar.AbsolutePosition;

            OnChatFromClient(this, chatFromClient);
        }

        public void SendInstantMessage(UUID agentId, string message)
        {
            if (m_frozenUser)
                return;

            // We may be able to use ClientView.SendInstantMessage here, but we need a client instance.
            // InstantMessageModule.OnInstantMessage searches through a list of scenes for a client matching the toAgent,
            // but I don't think we have a list of scenes available from here.
            // (We also don't want to duplicate the code in OnInstantMessage if we can avoid it.)

            // user is a UUID

            // client.SendInstantMessage(m_host.UUID, fromSession, message, user, imSessionID, m_host.Name, AgentManager.InstantMessageDialog.MessageFromAgent, (uint)Util.UnixTimeSinceEpoch());
            UUID friendTransactionID = UUID.Random();

            //m_pendingFriendRequests.Add(friendTransactionID, fromAgentID);

            GridInstantMessage msg = new GridInstantMessage();
            msg.fromAgentID = new Guid(AgentID.ToString());
            msg.toAgentID = agentId.Guid;
            msg.imSessionID = new Guid(friendTransactionID.ToString()); // This is the item we're mucking with here
            msg.timestamp = (uint)Util.UnixTimeSinceEpoch();// timestamp;
            msg.fromAgentName = Name;
            // Cap the message length at 1024.
            if (message != null && message.Length > 1024)
                msg.message = message.Substring(0, 1024);
            else
                msg.message = message;
            msg.dialog = (byte)InstantMessageDialog.MessageFromAgent;
            msg.fromGroup = false;// fromGroup;
            msg.offline = (byte)0; //offline;
            msg.ParentEstateID = 0; //ParentEstateID;
            ScenePresence sp = m_scene.GetScenePresence(AgentID);
            msg.Position = sp.AbsolutePosition;
            msg.RegionID = m_scene.RegionInfo.RegionID.Guid;//RegionID.Guid;
            // binaryBucket is the SL URL without the prefix, e.g. "Region/x/y/z"
            string url = EncodeURL(false, m_scene.RegionInfo.RegionName, msg.Position.X, msg.Position.Y, msg.Position.Z);
            byte[] bucket = Utils.StringToBytes(url);
            msg.binaryBucket = new byte[bucket.Length];// binaryBucket;
            bucket.CopyTo(msg.binaryBucket, 0);

            IMessageTransferModule transferModule = m_scene.RequestModuleInterface<IMessageTransferModule>();
            if (transferModule != null)
            {
                transferModule.SendInstantMessage(msg, delegate(bool success) { });
            }
        }

        private string EncodeURL(bool includePrefix, string region, float posx, float posy, float posz)
        {
            int x = (int)Math.Floor(posx);
            int y = (int)Math.Floor(posy);
            int z = (int)Math.Floor(posz);
            string prefix = includePrefix ? "http://places.inworldz.com/" : String.Empty;

            return prefix + Util.EscapeUriDataStringRfc3986(m_scene.RegionInfo.RegionName) + "/" + x.ToString() + "/" + y.ToString() + "/" + z.ToString();
        }

        public void SendChatMessage(string message, byte type, OpenMetaverse.Vector3 fromPos, string fromName, OpenMetaverse.UUID fromAgentID, OpenMetaverse.UUID ownerID, byte source, byte audible)
        {
        }

        #endregion

        #region Give Inventory

        public void GiveInventoryObject(SceneObjectPart part, string objName, UUID objId, byte assetType, UUID destId)
        {
            if (m_frozenUser)
                return;

            // check if destination is an avatar
            if (!String.IsNullOrEmpty(m_scene.CommsManager.UserService.Key2Name(destId, false)))
            {
                if (m_scene.GetScenePresence(destId) == null || m_scene.GetScenePresence(destId).IsChildAgent)
                    return;//Only allow giving items to users in the sim

                // destination is an avatar
                InventoryItemBase agentItem =
                        m_scene.MoveTaskInventoryItem(destId, UUID.Zero, part, objId);

                if (agentItem == null)
                    return;

                byte dialog = (byte)InstantMessageDialog.InventoryOffered;
                byte[] bucket = new byte[17];
                bucket[0] = assetType;
                agentItem.ID.ToBytes(bucket, 1);
                ScenePresence sp = m_scene.GetScenePresence(AgentID);
                string URL = EncodeURL(true, m_scene.RegionInfo.RegionName, sp.AbsolutePosition.X, sp.AbsolutePosition.Y, sp.AbsolutePosition.Z);

                GridInstantMessage msg = new GridInstantMessage(m_scene,
                        AgentID, Name, destId,
                        dialog, false, "'" + objName + "'  ( " + URL + " )",
                        agentItem.ID, true, sp.AbsolutePosition,
                        bucket);

                IMessageTransferModule transferModule = m_scene.RequestModuleInterface<IMessageTransferModule>();
                if (transferModule != null)
                {
                    transferModule.SendInstantMessage(msg, delegate(bool success) { });
                }
            }
        }

        #endregion

        #endregion

        #region IClientAPI Properties / Methods

        public OpenMetaverse.Vector3 StartPos
        {
            get;
            set;
        }

        public OpenMetaverse.UUID AgentId
        {
            get { return m_UUID; }
        }

        public OpenMetaverse.UUID SessionId
        {
            get { return m_sessionId; }
        }

        public OpenMetaverse.UUID SecureSessionId
        {
            get { return UUID.Zero; }
        }

        public OpenMetaverse.UUID ActiveGroupId
        {
            get { return UUID.Zero; }
        }

        public string ActiveGroupName
        {
            get { return String.Empty; }
        }

        public ulong ActiveGroupPowers
        {
            get { return 0; }
        }

        public ulong GetGroupPowers(OpenMetaverse.UUID groupID) { return 0; }

        public bool IsGroupMember(OpenMetaverse.UUID GroupID) { return false; }

        public string FirstName
        {
            get { return m_firstName; }
        }

        public string LastName
        {
            get { return m_lastName; }
        }

        public IScene Scene
        {
            get { return m_scene; }
        }

        public int NextAnimationSequenceNumber
        {
            get { return m_animationSequenceNumber++; }
        }

        public string Name
        {
            get { return FirstName + " " + LastName; }
        }

        public bool IsActive
        {
            get;
            set;
        }

        public bool SendLogoutPacketWhenClosing
        {
            set { }
        }

        public bool DebugCrossings
        {
            get { return false; }
            set { }
        }

        public uint CircuitCode
        {
            get { return m_circuitCode; }
        }

#pragma warning disable 0067 // disable "X is never used"
        public event Action<int> OnSetThrottles;

        public event GenericMessage OnGenericMessage;

        public event ImprovedInstantMessage OnInstantMessage;

        public event ChatMessage OnChatFromClient;

        public event TextureRequest OnRequestTexture;

        public event RezObject OnRezObject;
        public event RestoreObject OnRestoreObject;

        public event ModifyTerrain OnModifyTerrain;

        public event BakeTerrain OnBakeTerrain;

        public event EstateChangeInfo OnEstateChangeInfo;

        public event SimWideDeletesDelegate OnSimWideDeletes;

        public event SetAppearance OnSetAppearance;

        public event AvatarNowWearing OnAvatarNowWearing;

        public event RezSingleAttachmentFromInv OnRezSingleAttachmentFromInv;

        public event RezMultipleAttachmentsFromInv OnRezMultipleAttachmentsFromInv;

        public event UUIDNameRequest OnDetachAttachmentIntoInv;

        public event ObjectAttach OnObjectAttach;

        public event ObjectDeselect OnObjectDetach;

        public event ObjectDrop OnObjectDrop;

        public event StartAnim OnStartAnim;

        public event StopAnim OnStopAnim;

        public event LinkObjects OnLinkObjects;

        public event DelinkObjects OnDelinkObjects;

        public event RequestMapBlocks OnRequestMapBlocks;

        public event RequestMapName OnMapNameRequest;

        public event TeleportLocationRequest OnTeleportLocationRequest;

        public event DisconnectUser OnDisconnectUser;

        public event RequestAvatarProperties OnRequestAvatarProperties;

        public event RequestAvatarInterests OnRequestAvatarInterests;

        public event SetAlwaysRun OnSetAlwaysRun;

        public event TeleportLandmarkRequest OnTeleportLandmarkRequest;

        public event DeRezObjects OnDeRezObjects;

        public event Action<IClientAPI> OnRegionHandShakeReply;

        public event GenericCall2 OnRequestWearables;

        public event GenericCall2 OnCompleteMovementToRegion;

        public event UpdateAgent OnAgentUpdate;

        public event AgentRequestSit OnAgentRequestSit;

        public event AgentSit OnAgentSit;

        public event AvatarPickerRequest OnAvatarPickerRequest;

        public event Action<IClientAPI> OnRequestAvatarsData;

        public event AddNewPrim OnAddPrim;

        public event FetchInventory OnAgentDataUpdateRequest;

        public event TeleportLocationRequest OnSetStartLocationRequest;

        public event RequestGodlikePowers OnRequestGodlikePowers;

        public event GodKickUser OnGodKickUser;

        public event ObjectDuplicate OnObjectDuplicate;

        public event ObjectDuplicateOnRay OnObjectDuplicateOnRay;

        public event GrabObject OnGrabObject;

        public event DeGrabObject OnDeGrabObject;

        public event MoveObject OnGrabUpdate;

        public event SpinStart OnSpinStart;

        public event SpinObject OnSpinUpdate;

        public event SpinStop OnSpinStop;

        public event UpdateShape OnUpdatePrimShape;

        public event ObjectExtraParams OnUpdateExtraParams;

        public event ObjectRequest OnObjectRequest;

        public event ObjectSelect OnObjectSelect;

        public event ObjectDeselect OnObjectDeselect;

        public event GenericCall7 OnObjectDescription;

        public event GenericCall7 OnObjectName;

        public event GenericCall7 OnObjectClickAction;

        public event GenericCall7 OnObjectMaterial;

        public event RequestObjectPropertiesFamily OnRequestObjectPropertiesFamily;

        public event UpdatePrimFlags OnUpdatePrimFlags;

        public event UpdatePrimTexture OnUpdatePrimTexture;

        public event UpdateVectorWithUndoSupport OnUpdatePrimGroupPosition;

        public event UpdateVectorWithUndoSupport OnUpdatePrimSinglePosition;

        public event UpdatePrimRotation OnUpdatePrimGroupRotation;

        public event UpdatePrimSingleRotation OnUpdatePrimSingleRotation;

        public event UpdatePrimSingleRotationPosition OnUpdatePrimSingleRotationPosition;

        public event UpdatePrimGroupRotation OnUpdatePrimGroupMouseRotation;

        public event UpdateVector OnUpdatePrimScale;

        public event UpdateVector OnUpdatePrimGroupScale;

        public event StatusChange OnChildAgentStatus;

        public event GenericCall2 OnStopMovement;

        public event Action<OpenMetaverse.UUID> OnRemoveAvatar;

        public event ObjectPermissions OnObjectPermissions;

        public event CreateNewInventoryItem OnCreateNewInventoryItem;

        public event LinkInventoryItem OnLinkInventoryItem;

        public event CreateInventoryFolder OnCreateNewInventoryFolder;

        public event UpdateInventoryFolder OnUpdateInventoryFolder;

        public event MoveInventoryFolder OnMoveInventoryFolder;

        public event FetchInventoryDescendents OnFetchInventoryDescendents;

        public event PurgeInventoryDescendents OnPurgeInventoryDescendents;

        public event FetchInventory OnFetchInventory;

        public event RequestTaskInventory OnRequestTaskInventory;

        public event UpdateInventoryItem OnUpdateInventoryItem;

        public event CopyInventoryItem OnCopyInventoryItem;

        public event MoveInventoryItem OnMoveInventoryItem;

        public event RemoveInventoryFolder OnRemoveInventoryFolder;

        public event RemoveInventoryItem OnRemoveInventoryItem;

        public event RemoveInventoryItem OnPreRemoveInventoryItem;

        public event UDPAssetUploadRequest OnAssetUploadRequest;

        public event XferReceive OnXferReceive;

        public event RequestXfer OnRequestXfer;

        public event ConfirmXfer OnConfirmXfer;

        public event AbortXfer OnAbortXfer;

        public event RezScript OnRezScript;

        public event UpdateTaskInventory OnUpdateTaskInventory;

        public event MoveTaskInventory OnMoveTaskItem;

        public event RemoveTaskInventory OnRemoveTaskItem;

        public event RequestAsset OnRequestAsset;

        public event UUIDNameRequest OnNameFromUUIDRequest;

        public event ParcelAccessListRequest OnParcelAccessListRequest;

        public event ParcelAccessListUpdateRequest OnParcelAccessListUpdateRequest;

        public event ParcelPropertiesRequest OnParcelPropertiesRequest;

        public event ParcelDivideRequest OnParcelDivideRequest;

        public event ParcelJoinRequest OnParcelJoinRequest;

        public event ParcelPropertiesUpdateRequest OnParcelPropertiesUpdateRequest;

        public event ParcelSelectObjects OnParcelSelectObjects;

        public event ParcelObjectOwnerRequest OnParcelObjectOwnerRequest;

        public event ParcelAbandonRequest OnParcelAbandonRequest;

        public event ParcelGodForceOwner OnParcelGodForceOwner;

        public event ParcelReclaim OnParcelReclaim;

        public event ParcelReturnObjectsRequest OnParcelReturnObjectsRequest;

        public event ParcelDeedToGroup OnParcelDeedToGroup;

        public event RegionInfoRequest OnRegionInfoRequest;

        public event EstateCovenantRequest OnEstateCovenantRequest;

        public event FriendActionDelegate OnApproveFriendRequest;

        public event FriendActionDelegate OnDenyFriendRequest;

        public event FriendshipTermination OnTerminateFriendship;

        public event MoneyTransferRequest OnMoneyTransferRequest;

        public event EconomyDataRequest OnEconomyDataRequest;

        public event MoneyBalanceRequest OnMoneyBalanceRequest;

        public event UpdateAvatarProperties OnUpdateAvatarProperties;

        public event AvatarInterestsUpdate OnAvatarInterestsUpdate;

        public event ParcelBuy OnParcelBuy;

        public event RequestPayPrice OnRequestPayPrice;

        public event ObjectSaleInfo OnObjectSaleInfo;

        public event ObjectBuy OnObjectBuy;

        public event BuyObjectInventory OnBuyObjectInventory;

        public event RequestTerrain OnRequestTerrain;

        public event RequestTerrain OnUploadTerrain;

        public event ObjectIncludeInSearch OnObjectIncludeInSearch;

        public event UUIDNameRequest OnTeleportHomeRequest;

        public event ScriptAnswer OnScriptAnswer;

        public event AgentSit OnUndo;

        public event AgentSit OnRedo;

        public event LandUndo OnLandUndo;

        public event ForceReleaseControls OnForceReleaseControls;

        public event GodLandStatRequest OnLandStatRequest;

        public event DetailedEstateDataRequest OnDetailedEstateDataRequest;

        public event SetEstateFlagsRequest OnSetEstateFlagsRequest;

        public event SetEstateTerrainBaseTexture OnSetEstateTerrainBaseTexture;

        public event SetEstateTerrainDetailTexture OnSetEstateTerrainDetailTexture;

        public event SetEstateTerrainTextureHeights OnSetEstateTerrainTextureHeights;

        public event CommitEstateTerrainTextureRequest OnCommitEstateTerrainTextureRequest;

        public event SetRegionTerrainSettings OnSetRegionTerrainSettings;

        public event EstateRestartSimRequest OnEstateRestartSimRequest;

        public event EstateChangeCovenantRequest OnEstateChangeCovenantRequest;

        public event UpdateEstateAccessDeltaRequest OnUpdateEstateAccessDeltaRequest;

        public event SimulatorBlueBoxMessageRequest OnSimulatorBlueBoxMessageRequest;

        public event EstateBlueBoxMessageRequest OnEstateBlueBoxMessageRequest;

        public event EstateDebugRegionRequest OnEstateDebugRegionRequest;

        public event EstateTeleportOneUserHomeRequest OnEstateTeleportOneUserHomeRequest;

        public event EstateTeleportAllUsersHomeRequest OnEstateTeleportAllUsersHomeRequest;

        public event UUIDNameRequest OnUUIDGroupNameRequest;

        public event RegionHandleRequest OnRegionHandleRequest;

        public event ParcelInfoRequest OnParcelInfoRequest;

        public event RequestObjectPropertiesFamily OnObjectGroupRequest;

        public event ScriptReset OnScriptReset;

        public event GetScriptRunning OnGetScriptRunning;

        public event SetScriptRunning OnSetScriptRunning;

        public event UpdateVector OnAutoPilotGo;

        public event TerrainUnacked OnUnackedTerrain;

        public event ActivateGestures OnActivateGestures;

        public event DeactivateGestures OnDeactivateGestures;

        public event ObjectOwner OnObjectOwner;

        public event DirPlacesQuery OnDirPlacesQuery;

        public event DirFindQuery OnDirFindQuery;

        public event DirLandQuery OnDirLandQuery;

        public event DirPopularQuery OnDirPopularQuery;

        public event DirClassifiedQuery OnDirClassifiedQuery;

        public event EventInfoRequest OnEventInfoRequest;

        public event ParcelSetOtherCleanTime OnParcelSetOtherCleanTime;

        public event MapItemRequest OnMapItemRequest;

        public event OfferCallingCard OnOfferCallingCard;

        public event AcceptCallingCard OnAcceptCallingCard;

        public event DeclineCallingCard OnDeclineCallingCard;

        public event SoundTrigger OnSoundTrigger;

        public event StartLure OnStartLure;

        public event TeleportLureRequest OnTeleportLureRequest;

        public event NetworkStats OnNetworkStatsUpdate;

        public event ClassifiedInfoRequest OnClassifiedInfoRequest;

        public event ClassifiedInfoUpdate OnClassifiedInfoUpdate;

        public event ClassifiedDelete OnClassifiedDelete;

        public event ClassifiedDelete OnClassifiedGodDelete;

        public event EventNotificationAddRequest OnEventNotificationAddRequest;

        public event EventNotificationRemoveRequest OnEventNotificationRemoveRequest;

        public event EventGodDelete OnEventGodDelete;

        public event ParcelDwellRequest OnParcelDwellRequest;

        public event UserInfoRequest OnUserInfoRequest;

        public event UpdateUserInfo OnUpdateUserInfo;

        public event RetrieveInstantMessages OnRetrieveInstantMessages;

        public event PickDelete OnPickDelete;

        public event PickGodDelete OnPickGodDelete;

        public event PickInfoUpdate OnPickInfoUpdate;

        public event AvatarNotesUpdate OnAvatarNotesUpdate;

        public event MuteListRequest OnMuteListRequest;

        public event MuteListEntryUpdate OnUpdateMuteListEntry;

        public event MuteListEntryRemove OnRemoveMuteListEntry;

        public event PlacesQuery OnPlacesQuery;

        public event GrantUserRights OnGrantUserRights;

        public event FreezeUserUpdate OnParcelFreezeUser;

        public event EjectUserUpdate OnParcelEjectUser;

        public event GroupVoteHistoryRequest OnGroupVoteHistoryRequest;

        public event GroupAccountDetailsRequest OnGroupAccountDetailsRequest;

        public event GroupAccountSummaryRequest OnGroupAccountSummaryRequest;

        public event GroupAccountTransactionsRequest OnGroupAccountTransactionsRequest;

        public event AgentCachedTextureRequest OnAgentCachedTextureRequest;

        public event ActivateGroup OnActivateGroup;

        public event GodlikeMessage OnGodlikeMessage;

        public event GodlikeMessage OnEstateTelehubRequest;

#pragma warning restore 0067

        public System.Net.IPEndPoint RemoteEndPoint
        {
            get { return new IPEndPoint(IPAddress.Loopback, 0); }
        }

        public bool IsLoggingOut
        {
            get;
            set;
        }

        public void SetDebugPacketLevel(int newDebug)
        {
        }

        public void ProcessInPacket(OpenMetaverse.Packets.Packet NewPack)
        {
        }

        public void Kick(string message)
        {
        }

        public void Start()
        {
        }

        public void SendWearables(AvatarWearable[] wearables, int serial)
        {
        }

        public void SendAppearance(AvatarAppearance app, Vector3 hover)
        {
        }

        public void SendStartPingCheck(byte seq)
        {
        }

        public void SendKillObject(ulong regionHandle, uint localID)
        {
        }

        public void SendKillObjects(ulong regionHandle, uint[] localID)
        {
        }

        public void SendAnimations(OpenMetaverse.UUID[] animID, int[] seqs, OpenMetaverse.UUID sourceAgentId, OpenMetaverse.UUID[] objectIDs)
        {
        }

        public void SendRegionHandshake(RegionInfo regionInfo, RegionHandshakeArgs args)
        {
        }

        public void SendInstantMessage(GridInstantMessage im)
        {
        }

        public void SendGenericMessage(string method, List<string> message)
        {
        }

        public void SendLayerData(float[] map)
        {
        }

        public void SendLayerData(int px, int py, float[] map)
        {
        }

        public void SendWindData(OpenMetaverse.Vector2[] windSpeeds)
        {
        }

        public void SendCloudData(float[] cloudCover)
        {
        }

        public void MoveAgentIntoRegion(RegionInfo regInfo, OpenMetaverse.Vector3 pos, OpenMetaverse.Vector3 look)
        {
        }

        public void InformClientOfNeighbour(ulong neighbourHandle, System.Net.IPEndPoint neighbourExternalEndPoint)
        {
        }

        public AgentCircuitData RequestClientInfo()
        {
            AgentCircuitData agentData = new AgentCircuitData();

            agentData.AgentID = AgentId;
            // agentData.Appearance
            // agentData.BaseFolder
            agentData.CapsPath = String.Empty;
            agentData.child = false;
            agentData.CircuitCode = m_circuitCode;
            agentData.ClientVersion = "Bot";
            agentData.FirstName = m_firstName;
            // agentData.InventoryFolder
            agentData.LastName = m_lastName;
            agentData.SecureSessionID = SecureSessionId;
            agentData.SessionID = m_sessionId;
            // agentData.startpos

            return agentData;
        }

        public void SendMapBlock(List<MapBlockData> mapBlocks, uint flag)
        {
        }

        public void SendLocalTeleport(OpenMetaverse.Vector3 position, OpenMetaverse.Vector3 lookAt, uint flags)
        {
        }

        public void SendTeleportFailed(string reason)
        {
        }

        public void SendTeleportLocationStart()
        {
        }

        public void SendMoneyBalance(UUID transaction, bool success, string description, int balance, TransactionInfoBlock transInfo)
        {
        }

        public void SendPayPrice(OpenMetaverse.UUID objectID, int[] payPrice)
        {
        }

        public void SendAvatarData(ulong regionHandle, string firstName, string lastName, string grouptitle, OpenMetaverse.UUID avatarID, uint avatarLocalID, 
            OpenMetaverse.Vector3 Pos, byte[] textureEntry, uint parentID, OpenMetaverse.Quaternion rotation, OpenMetaverse.Vector4 collisionPlane, 
            OpenMetaverse.Vector3 velocity, bool immediate)
        {
        }

        public void SendAvatarTerseUpdate(ulong regionHandle, ushort timeDilation, uint localID, OpenMetaverse.Vector3 position, OpenMetaverse.Vector3 velocity, OpenMetaverse.Vector3 acceleration, OpenMetaverse.Quaternion rotation, OpenMetaverse.UUID agentid, OpenMetaverse.Vector4 collisionPlane)
        {
        }

        public void SendCoarseLocationUpdate(List<OpenMetaverse.UUID> users, List<OpenMetaverse.Vector3> CoarseLocations)
        {
        }

        public void AttachObject(uint localID, OpenMetaverse.Quaternion rotation, byte attachPoint, OpenMetaverse.UUID ownerID)
        {
        }

        public void SetChildAgentThrottle(byte[] throttle)
        {
        }

        public void SendPrimitiveToClient(object sop, uint clientFlags, OpenMetaverse.Vector3 lpos, PrimUpdateFlags updateFlags)
        {
        }

        public void SendPrimitiveToClientImmediate(object sop, uint clientFlags, OpenMetaverse.Vector3 lpos)
        {
        }

        public void SendPrimTerseUpdate(object sop)
        {
        }

        public void SendInventoryFolderDetails(OpenMetaverse.UUID ownerID, InventoryFolderBase folder, List<InventoryItemBase> items, List<InventoryFolderBase> folders, bool fetchFolders, bool fetchItems)
        {
        }

        public void FlushPrimUpdates()
        {
        }

        public void SendInventoryItemDetails(OpenMetaverse.UUID ownerID, InventoryItemBase item)
        {
        }

        public void SendInventoryItemCreateUpdate(InventoryItemBase Item, uint callbackId)
        {
        }

        public void SendRemoveInventoryItem(OpenMetaverse.UUID itemID)
        {
        }

        public void SendTakeControls(int controls, bool TakeControls, bool passToAgent)
        {
        }
        public void SendTakeControls2(int controls1, bool takeControls1, bool passToAgent1,
                                      int controls2, bool takeControls2, bool passToAgent2)
        {
        }

        public void SendTaskInventory(OpenMetaverse.UUID taskID, short serial, byte[] fileName)
        {
        }

        public void SendBulkUpdateInventory(InventoryNodeBase node)
        {
        }

        public void SendXferPacket(ulong xferID, uint packet, byte[] data)
        {
        }

        public void SendEconomyData(float EnergyEfficiency, int ObjectCapacity, int ObjectCount, int PriceEnergyUnit, int PriceGroupCreate, int PriceObjectClaim, float PriceObjectRent, float PriceObjectScaleFactor, int PriceParcelClaim, float PriceParcelClaimFactor, int PriceParcelRent, int PricePublicObjectDecay, int PricePublicObjectDelete, int PriceRentLight, int PriceUpload, int TeleportMinPrice, float TeleportPriceExponent)
        {
        }

        public void SendAvatarPickerReply(AvatarPickerReplyAgentDataArgs AgentData, List<AvatarPickerReplyDataArgs> Data)
        {
        }

        public void SendAgentDataUpdate(OpenMetaverse.UUID agentid, OpenMetaverse.UUID activegroupid, string firstname, string lastname, ulong grouppowers, string groupname, string grouptitle)
        {
        }

        public void SendPreLoadSound(OpenMetaverse.UUID objectID, OpenMetaverse.UUID ownerID, OpenMetaverse.UUID soundID)
        {
        }

        public void SendPlayAttachedSound(OpenMetaverse.UUID soundID, OpenMetaverse.UUID objectID, OpenMetaverse.UUID ownerID, float gain, byte flags)
        {
        }

        public void SendTriggeredSound(OpenMetaverse.UUID soundID, OpenMetaverse.UUID ownerID, OpenMetaverse.UUID objectID, OpenMetaverse.UUID parentID, ulong handle, OpenMetaverse.Vector3 position, float gain)
        {
        }

        public void SendAttachedSoundGainChange(OpenMetaverse.UUID objectID, float gain)
        {
        }

        public void SendNameReply(OpenMetaverse.UUID profileId, string firstname, string lastname)
        {
        }

        public void SendAlertMessage(string message)
        {
        }

        public void SendAlertMessage(string message, string infoMessage, OSD extraParams)
        {
            /* no op */
        }

        public void SendAgentAlertMessage(string message, bool modal)
        {
        }

        public void SendLoadURL(string objectname, OpenMetaverse.UUID objectID, OpenMetaverse.UUID ownerID, bool groupOwned, string message, string url)
        {
        }

        public void SendDialog(string objectname, OpenMetaverse.UUID objectID, OpenMetaverse.UUID ownerID, string ownerFirstname, string ownerLastname, string msg, OpenMetaverse.UUID textureID, int ch, string[] buttonlabels)
        {
        }

        public void SendSunPos(OpenMetaverse.Vector3 sunPos, OpenMetaverse.Vector3 sunVel, ulong CurrentTime, uint SecondsPerSunCycle, uint SecondsPerYear, float OrbitalPosition)
        {
        }

        public void SendViewerEffect(OpenMetaverse.Packets.ViewerEffectPacket.EffectBlock[] effectBlocks)
        {
        }

        public void SendViewerTime(int phase)
        {
        }

        private void InitDefaultAnimations()
        {
            try
            {

                using (XmlTextReader reader = new XmlTextReader("data/avataranimations.xml"))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(reader);
                    if (doc.DocumentElement != null)
                        foreach (XmlNode nod in doc.DocumentElement.ChildNodes)
                        {
                            if (nod.Attributes["name"] != null)
                            {
                                string name = nod.Attributes["name"].Value.ToLower();
                                string id = nod.InnerText;
                                m_defaultAnimations.Add(name, (UUID)id);
                            }
                        }
                }
            }
            catch (Exception)
            {
            }
        }

        public OpenMetaverse.UUID GetDefaultAnimation(string name)
        {
            if (m_defaultAnimations.ContainsKey(name.ToLower()))
                return m_defaultAnimations[name.ToLower()];
            return UUID.Zero;
        }

        public void SendAvatarProperties(OpenMetaverse.UUID avatarID, string aboutText, string bornOn, byte[] charterMember, string flAbout, uint flags, OpenMetaverse.UUID flImageID, OpenMetaverse.UUID imageID, string profileURL, OpenMetaverse.UUID partnerID)
        {
        }

        public void SendAvatarInterests(OpenMetaverse.UUID avatarID, uint skillsMask, string skillsText, uint wantToMask, string wantToText, string languagesText)
        {
        }

        public void SendScriptQuestion(OpenMetaverse.UUID taskID, string taskName, string ownerName, OpenMetaverse.UUID itemID, int question)
        {
        }

        public void SendHealth(float health)
        {
        }

        public void SendEstateUUIDList(OpenMetaverse.UUID invoice, int whichList, OpenMetaverse.UUID[] UUIDList, uint estateID)
        {
        }

        public void SendBannedUserList(OpenMetaverse.UUID invoice, EstateBan[] banlist, uint estateID)
        {
        }

        public void SendRegionInfoToEstateMenu(RegionInfoForEstateMenuArgs args)
        {
        }

        public void SendEstateCovenantInformation(OpenMetaverse.UUID covenant, uint lastUpdated)
        {
        }

        public void SendDetailedEstateData(OpenMetaverse.UUID invoice, string estateName, uint estateID, uint parentEstate, uint estateFlags, uint sunPosition, OpenMetaverse.UUID covenant, uint covenantLastUpdated, string abuseEmail, OpenMetaverse.UUID estateOwner)
        {
        }

        public void SendLandProperties(int sequence_id, bool snap_selection, int request_result, LandData landData, float simObjectBonusFactor, int parcelObjectCapacity, int simObjectCapacity, uint regionFlags)
        {
        }

        public void SendLandAccessListData(List<OpenMetaverse.UUID> avatars, uint accessFlag, int localLandID)
        {
        }

        public void SendForceClientSelectObjects(List<uint> objectIDs)
        {
        }

        public void SendLandObjectOwners(LandData land, List<OpenMetaverse.UUID> groups, Dictionary<OpenMetaverse.UUID, int> ownersAndCount)
        {
        }

        public void SendLandParcelOverlay(byte[] data, int sequence_id)
        {
        }

        public void SendParcelMediaCommand(uint flags, ParcelMediaCommandEnum command, float time)
        {
        }

        public void SendParcelMediaUpdate(string mediaUrl, OpenMetaverse.UUID mediaTextureID, byte autoScale, string mediaType, string mediaDesc, int mediaWidth, int mediaHeight, byte mediaLoop)
        {
        }

        public void SendAssetUploadCompleteMessage(sbyte AssetType, bool Success, OpenMetaverse.UUID AssetFullID)
        {
        }

        public void SendConfirmXfer(ulong xferID, uint PacketID)
        {
        }

        public void SendXferRequest(ulong XferID, short AssetType, OpenMetaverse.UUID vFileID, byte FilePath, byte[] FileName)
        {
        }

        public void SendInitiateDownload(string simFileName, string clientFileName)
        {
        }

        public void SendImageFirstPart(ushort numParts, OpenMetaverse.UUID ImageUUID, uint ImageSize, byte[] ImageData, byte imageCodec)
        {
        }

        public void SendImageNextPart(ushort partNumber, OpenMetaverse.UUID imageUuid, byte[] imageData)
        {
        }

        public void SendImageNotFound(OpenMetaverse.UUID imageid)
        {
        }

        public void SendDisableSimulator()
        {
        }

        public void SendSimStats(SimStats stats)
        {
        }

        public void SendObjectPropertiesFamilyData(uint RequestFlags, OpenMetaverse.UUID ObjectUUID, OpenMetaverse.UUID OwnerID, OpenMetaverse.UUID GroupID, uint BaseMask, uint OwnerMask, uint GroupMask, uint EveryoneMask, uint NextOwnerMask, int OwnershipCost, byte SaleType, int SalePrice, uint Category, OpenMetaverse.UUID LastOwnerID, string ObjectName, string Description)
        {
        }

        public void SendObjectPropertiesReply(OpenMetaverse.UUID ItemID, ulong CreationDate, OpenMetaverse.UUID CreatorUUID, OpenMetaverse.UUID FolderUUID, OpenMetaverse.UUID FromTaskUUID, OpenMetaverse.UUID GroupUUID, short InventorySerial, OpenMetaverse.UUID LastOwnerUUID, OpenMetaverse.UUID ObjectUUID, OpenMetaverse.UUID OwnerUUID, string TouchTitle, byte[] TextureID, string SitTitle, string ItemName, string ItemDescription, uint OwnerMask, uint NextOwnerMask, uint GroupMask, uint EveryoneMask, uint BaseMask, uint FoldedOwnerMask, uint FoldedNextOwnerMask, byte saleType, int salePrice)
        {
        }

        public void SendAgentOffline(OpenMetaverse.UUID[] agentIDs)
        {
        }

        public void SendAgentOnline(OpenMetaverse.UUID[] agentIDs)
        {
        }

        public void SendSitResponse(OpenMetaverse.UUID TargetID, OpenMetaverse.Vector3 OffsetPos, OpenMetaverse.Quaternion SitOrientation, bool autopilot, OpenMetaverse.Vector3 CameraAtOffset, OpenMetaverse.Vector3 CameraEyeOffset, bool ForceMouseLook)
        {
        }

        public void SendAdminResponse(OpenMetaverse.UUID Token, uint AdminLevel)
        {
        }

        public void SendGroupMembership(GroupMembershipData[] GroupMembership)
        {
        }

        public void SendGroupNameReply(OpenMetaverse.UUID groupLLUID, string GroupName)
        {
        }

        public void SendJoinGroupReply(OpenMetaverse.UUID groupID, bool success)
        {
        }

        public void SendEjectGroupMemberReply(OpenMetaverse.UUID agentID, OpenMetaverse.UUID groupID, bool success)
        {
        }

        public void SendLeaveGroupReply(OpenMetaverse.UUID groupID, bool success)
        {
        }

        public void SendCreateGroupReply(OpenMetaverse.UUID groupID, bool success, string message)
        {
        }

        public void SendLandStatReply(uint reportType, uint requestFlags, uint resultCount, List<LandStatReportItem> lsrpl)
        {
        }

        public void SendScriptRunningReply(OpenMetaverse.UUID objectID, OpenMetaverse.UUID itemID, bool running)
        {
        }

        public void SendAsset(AssetBase asset, AssetRequestInfo req)
        {
        }

        public void SendTexture(AssetBase TextureAsset)
        {
        }

        public byte[] GetThrottlesPacked(float multiplier)
        {
            return new byte[0];
        }

        public event ViewerEffectEventHandler OnViewerEffect;

        public event Action<IClientAPI> OnLogout;

        public event Action<IClientAPI> OnConnectionClosed;

        public void SendBlueBoxMessage(OpenMetaverse.UUID FromAvatarID, string FromAvatarName, string Message)
        {
        }

        public void SendLogoutPacket()
        {
        }

        public void SetClientInfo(ClientInfo info)
        {
        }

        public void SetClientOption(string option, string value)
        {
        }

        public string GetClientOption(string option)
        {
            return String.Empty;
        }

        public void SendSetFollowCamProperties(OpenMetaverse.UUID objectID, Dictionary<int, float> parameters)
        {
        }

        public void SendClearFollowCamProperties(OpenMetaverse.UUID objectID)
        {
        }

        public void SendRegionHandle(OpenMetaverse.UUID regoinID, ulong handle)
        {
        }

        public void SendParcelInfo(RegionInfo info, LandData land, OpenMetaverse.UUID parcelID, uint x, uint y)
        {
        }

        public void SendScriptTeleportRequest(string objName, string simName, OpenMetaverse.Vector3 pos, OpenMetaverse.Vector3 lookAt)
        {
        }

        public void SendDirPlacesReply(OpenMetaverse.UUID queryID, DirPlacesReplyData[] data)
        {
        }

        public void SendDirPeopleReply(OpenMetaverse.UUID queryID, DirPeopleReplyData[] data)
        {
        }

        public void SendDirEventsReply(OpenMetaverse.UUID queryID, DirEventsReplyData[] data)
        {
        }

        public void SendDirGroupsReply(OpenMetaverse.UUID queryID, DirGroupsReplyData[] data)
        {
        }

        public void SendDirClassifiedReply(OpenMetaverse.UUID queryID, DirClassifiedReplyData[] data)
        {
        }

        public void SendDirLandReply(OpenMetaverse.UUID queryID, DirLandReplyData[] data)
        {
        }

        public void SendDirPopularReply(OpenMetaverse.UUID queryID, DirPopularReplyData[] data)
        {
        }

        public void SendEventInfoReply(EventData info)
        {
        }

        public void SendMapItemReply(mapItemReply[] replies, uint mapitemtype, uint flags)
        {
        }

        public void SendAvatarGroupsReply(OpenMetaverse.UUID avatarID, GroupMembershipData[] data)
        {
        }

        public void SendOfferCallingCard(OpenMetaverse.UUID srcID, OpenMetaverse.UUID transactionID)
        {
        }

        public void SendAcceptCallingCard(OpenMetaverse.UUID transactionID)
        {
        }

        public void SendDeclineCallingCard(OpenMetaverse.UUID transactionID)
        {
        }

        public void SendTerminateFriend(OpenMetaverse.UUID exFriendID)
        {
        }

        public void SendAvatarClassifiedReply(OpenMetaverse.UUID targetID, OpenMetaverse.UUID[] classifiedID, string[] name)
        {
        }

        public void SendAvatarInterestsReply(OpenMetaverse.UUID avatarID, uint skillsMask, string skillsText, uint wantToMask, string wantToTask, string languagesText)
        {
        }

        public void SendClassifiedInfoReply(OpenMetaverse.UUID classifiedID, OpenMetaverse.UUID creatorID, uint creationDate, uint expirationDate, uint category, string name, string description, OpenMetaverse.UUID parcelID, uint parentEstate, OpenMetaverse.UUID snapshotID, string simName, OpenMetaverse.Vector3 globalPos, string parcelName, byte classifiedFlags, int price)
        {
        }

        public void SendAgentDropGroup(OpenMetaverse.UUID groupID)
        {
        }

        public void RefreshGroupMembership()
        {
        }

        public void SendAvatarNotesReply(OpenMetaverse.UUID targetID, string text)
        {
        }

        public void SendAvatarPicksReply(OpenMetaverse.UUID targetID, Dictionary<OpenMetaverse.UUID, string> picks)
        {
        }

        public void SendPickInfoReply(OpenMetaverse.UUID pickID, OpenMetaverse.UUID creatorID, bool topPick, OpenMetaverse.UUID parcelID, string name, string desc, OpenMetaverse.UUID snapshotID, string user, string originalName, string simName, OpenMetaverse.Vector3 posGlobal, int sortOrder, bool enabled)
        {
        }

        public void SendAvatarClassifiedReply(OpenMetaverse.UUID targetID, Dictionary<OpenMetaverse.UUID, string> classifieds)
        {
        }

        public void SendParcelDwellReply(int localID, OpenMetaverse.UUID parcelID, float dwell)
        {
        }

        public void SendUserInfoReply(bool imViaEmail, bool visible, string email)
        {
        }

        public void SendUseCachedMuteList()
        {
        }

        public void SendMuteListUpdate(string filename)
        {
        }

        public void KillEndDone()
        {
        }

        public bool AddGenericPacketHandler(string MethodName, GenericMessage handler)
        {
            return false;
        }

        public void SendChangeUserRights(OpenMetaverse.UUID agent, OpenMetaverse.UUID agentRelated, int relatedRights)
        {
        }

        public void SendTextBoxRequest(string message, int chatChannel, string objectname, OpenMetaverse.UUID ownerID, string firstName, string lastName, OpenMetaverse.UUID objectId)
        {
        }

        public void FreezeMe(uint flags, OpenMetaverse.UUID whoKey, string whoName)
        {
            bool freeze = ((flags & 1) == 0);
            if (freeze != m_frozenUser)
            {
                m_frozenUser = freeze;
                if (m_frozenUser)
                {
                    SendAgentAlertMessage(whoName + " has frozen you in place. You will be unable to move or interact until you log off and start a new session, or until " + whoName + " unfreezes you.", true);
                    m_log.WarnFormat("{0} has frozen {1} [{2}].", this.Name, whoName, whoKey);
                }
                else
                {
                    SendAgentAlertMessage(whoName + " has unfrozen you. You are free to move and interact again.", true);
                    m_log.WarnFormat("{0} has unfrozen {1} [{2}].", this.Name, whoName, whoKey);
                }
            }
        }

        public void SendAbortXfer(ulong id, int result)
        {
        }

        public void RunAttachmentOperation(Action action)
        {
            action();
        }

        public void SendAgentCachedTexture(List<CachedAgentArgs> args)
        {
        }

        public void SendTelehubInfo(OpenMetaverse.Vector3 TelehubPos, OpenMetaverse.Quaternion TelehubRot, List<OpenMetaverse.Vector3> SpawnPoint, OpenMetaverse.UUID ObjectID, string nameT)
        {
        }

        #endregion


        public void HandleWithInventoryWriteThread(Action toHandle)
        {

        }


        public System.Threading.Tasks.Task PauseUpdatesAndFlush()
        {
            return null;
        }

        public void ResumeUpdates(IEnumerable<uint> excludeObjectIds)
        {
        }

        public void WaitForClose()
        {
        }

        public void AfterAttachedToConnection(OpenSim.Framework.AgentCircuitData c)
        {
        }


        public List<AgentGroupData> GetAllGroupPowers()
        {
            return new List<AgentGroupData>();
        }

        public void SetGroupPowers(IEnumerable<AgentGroupData> groupPowers)
        {

        }

        public int GetThrottleTotal()
        {
            return 0;
        }


        public void SetActiveGroupInfo(AgentGroupData activeGroup)
        {
        }
    }
}
