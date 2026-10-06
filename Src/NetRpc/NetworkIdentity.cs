using System;
using UnityEngine;

namespace BITKit.Multiplayer.Unity
{
    public interface IUnityNetworkIdentity : global::BITKit.Multiplayer.NetRpc.INetworkIdentity
    {
        uint OwnerPeerId { get; }
        bool IsAssigned { get; }
        bool IsAuthority { get; }
    }

    /// <summary>Runtime identity assigned by the authoritative Unity network-object roster.</summary>
    [DisallowMultipleComponent]
    public sealed class NetworkIdentity : MonoBehaviour, IUnityNetworkIdentity
    {
        [SerializeField] private uint entityId;
        [SerializeField] private uint ownerPeerId;
        [SerializeField] private string prefabAddress;
        [SerializeField] private bool isAssigned;
        [SerializeField] private bool isAuthority;

        public uint EntityId => entityId;
        public uint OwnerPeerId => ownerPeerId;
        public string PrefabAddress => prefabAddress;
        public bool IsAssigned => isAssigned;
        public bool IsAuthority => isAuthority;

        internal void Bind(uint id, uint owner, bool authority)
        {
            if (id == 0) throw new ArgumentOutOfRangeException(nameof(id));
            entityId = id;
            ownerPeerId = owner;
            isAssigned = true;
            isAuthority = authority;
        }

        internal void SetOwner(uint owner) => ownerPeerId = owner;

        internal void Clear()
        {
            entityId = 0;
            ownerPeerId = 0;
            isAssigned = false;
            isAuthority = false;
        }
    }
}
