using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace BITKit.Multiplayer.Unity
{
    /// <summary>Stable authored identity for an object that already exists in a shared scene.</summary>
    [DisallowMultipleComponent]
    public sealed class SceneIdentity : MonoBehaviour, IUnityNetworkIdentity
    {
        [SerializeField] private string localId;
        private uint _entityId;
        private uint _ownerPeerId;
        private bool _isAuthority;

        public string LocalId => localId;
        public uint EntityId => _entityId;
        public uint OwnerPeerId => _ownerPeerId;
        public bool IsAssigned => _entityId != 0;
        public bool IsAuthority => _isAuthority;

        public void Bind(uint entityId, uint ownerPeerId = 0, bool authority = false)
        {
            if (entityId == 0) throw new ArgumentOutOfRangeException(nameof(entityId));
            _entityId = entityId;
            _ownerPeerId = ownerPeerId;
            _isAuthority = authority;
        }

        internal void SetOwner(uint ownerPeerId) => _ownerPeerId = ownerPeerId;

        public void Clear()
        {
            _entityId = 0;
            _ownerPeerId = 0;
            _isAuthority = false;
        }

        public static string Key(Transform target)
        {
            if (!target || !target.gameObject.scene.isLoaded ||
                string.IsNullOrEmpty(target.gameObject.scene.path)) return null;
            return BuildKey(target, target.gameObject.scene.path);
        }

        public static string BuildKey(Transform target, string scenePath)
        {
            if (!target || string.IsNullOrWhiteSpace(scenePath)) return null;
            var parts = new Stack<string>();
            for (var current = target; current; current = current.parent)
            {
                var identity = current.GetComponent<SceneIdentity>();
                parts.Push(identity && !string.IsNullOrWhiteSpace(identity.localId)
                    ? "i" + Uri.EscapeDataString(identity.localId.Trim()) + "@s" + current.GetSiblingIndex()
                    : "s" + current.GetSiblingIndex());
            }
            return scenePath + "|" + string.Join("/", parts);
        }

        public static string NameKey(Transform target)
        {
            if (!target || !target.gameObject.scene.isLoaded ||
                string.IsNullOrWhiteSpace(target.gameObject.scene.path)) return null;
            return BuildNameKey(target, target.gameObject.scene.path);
        }

        public static string BuildNameKey(Transform target, string scenePath)
        {
            if (!target || string.IsNullOrWhiteSpace(scenePath)) return null;
            Transform namedRoot = null;
            for (var node = target; node; node = node.parent)
                if (node.GetComponent<SceneIdentity>()) namedRoot = node;
            if (!namedRoot) return null;
            var parts = new Stack<string>();
            for (var node = target; node; node = node.parent)
            {
                if (string.IsNullOrWhiteSpace(node.name)) return null;
                var identity = node == namedRoot ? node.GetComponent<SceneIdentity>() : null;
                parts.Push(identity && !string.IsNullOrWhiteSpace(identity.localId)
                    ? "i" + Uri.EscapeDataString(identity.localId.Trim())
                    : Uri.EscapeDataString(node.name));
                if (node == namedRoot) break;
            }
            return scenePath + "|" + string.Join("/", parts);
        }

        public static long HashNameKey(string nameKey)
        {
            if (string.IsNullOrWhiteSpace(nameKey))
                throw new ArgumentException("Scene name path required.", nameof(nameKey));
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var value = offset;
            foreach (var b in Encoding.UTF8.GetBytes(nameKey))
                value = unchecked((value ^ b) * prime);
            var id = (long)(value & long.MaxValue);
            return id == 0 ? 1 : id;
        }
    }
}
