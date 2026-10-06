using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace BITKit.Multiplayer.Unity
{
    public enum NetworkObjectKind : byte
    {
        Prefab = 1,
        Scene = 2
    }

    public sealed class NetworkObjectState
    {
        public uint EntityId { get; set; }
        public uint OwnerPeerId { get; set; }
        public uint WorldGeneration { get; set; }
        public ulong Revision { get; set; }
        public NetworkObjectKind Kind { get; set; }
        public string Address { get; set; }
        public string SceneKey { get; set; }
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }
        public float RotationW { get; set; } = 1f;
        public bool Active { get; set; } = true;

        internal NetworkObjectState Copy() => (NetworkObjectState)MemberwiseClone();
    }

    public sealed class NetworkObjectSnapshot
    {
        public uint WorldGeneration { get; set; }
        public ulong Revision { get; set; }
        public NetworkObjectState[] Objects { get; set; } = Array.Empty<NetworkObjectState>();
    }

    /// <summary>Application asset-system boundary used to resolve NetworkIdentity.PrefabAddress in a Player.</summary>
    public interface INetworkPrefabLoader
    {
        UniTask<GameObject> InstantiateAsync(string address, Vector3 position, Quaternion rotation,
            CancellationToken cancellationToken);
        void Release(GameObject instance);
    }

    public sealed class NetworkGameObjectHandle
    {
        internal NetworkGameObjectHandle(NetworkObjectState state, GameObject gameObject,
            IUnityNetworkIdentity identity, bool authority)
        {
            State = state;
            GameObject = gameObject;
            Identity = identity;
            IsAuthority = authority;
        }

        internal NetworkObjectState State { get; set; }
        public uint EntityId => State.EntityId;
        public uint OwnerPeerId => State.OwnerPeerId;
        public GameObject GameObject { get; }
        public IUnityNetworkIdentity Identity { get; }
        public bool IsAuthority { get; }
        public bool IsOwner(uint localPeerId) => localPeerId != 0 && State.OwnerPeerId == localPeerId;
        public bool IsSceneObject => State.Kind == NetworkObjectKind.Scene;
        public string SceneKey => State.SceneKey;
        public string PrefabAddress => State.Address;
    }

    /// <summary>
    /// One world-generation roster. The Host owns IDs/lifecycle; Clients resolve NetworkIdentity prefab addresses
    /// and bind authored SceneIdentity objects before NetEntity component synchronization is exposed.
    /// </summary>
    [global::BITKit.Multiplayer.NetRpcBackend]
    public sealed class UnityNetworkObjects : IDisposable
    {
        private readonly RpcContextService _runtime;
        private readonly IRpcContext<UnityNetworkObjects> _rpcContext;
        private readonly INetworkPrefabLoader _loader;
        private readonly CancellationTokenSource _lifetime;
        private readonly Dictionary<uint, NetworkObjectState> _states = new Dictionary<uint, NetworkObjectState>();
        private readonly Dictionary<uint, NetworkGameObjectHandle> _objects = new Dictionary<uint, NetworkGameObjectHandle>();
        private readonly Dictionary<string, GameObject> _sceneObjects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly HashSet<uint> _loading = new HashSet<uint>();
        private uint _nextEntityId;
        private ulong _revision;
        private bool _disposed;

        internal UnityNetworkObjects(RpcContextService runtime, INetworkPrefabLoader loader,
            uint worldGeneration, uint localPeerId, CancellationToken sessionLifetime,
            IRpcContext<UnityNetworkObjects> rpcContext)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _rpcContext = rpcContext ?? throw new ArgumentNullException(nameof(rpcContext));
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            if (worldGeneration == 0) throw new ArgumentOutOfRangeException(nameof(worldGeneration));
            if (localPeerId == 0) throw new ArgumentOutOfRangeException(nameof(localPeerId));
            WorldGeneration = worldGeneration;
            LocalPeerId = localPeerId;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionLifetime);
        }

        public bool IsAuthority => _runtime.IsServer;
        public uint WorldGeneration { get; }
        public uint LocalPeerId { get; }
        public IReadOnlyCollection<NetworkGameObjectHandle> Objects => _objects.Values;
        public event Func<NetworkGameObjectHandle, CancellationToken, UniTask> Initializing;
        public event Action<NetworkGameObjectHandle> Spawned;
        public event Action<NetworkGameObjectHandle> Despawning;
        public event Action<Exception> Faulted;

        public void RegisterLoadedSceneObjects()
        {
            ThrowIfDisposed();
            foreach (var identity in Resources.FindObjectsOfTypeAll<SceneIdentity>())
                if (identity && identity.gameObject.scene.isLoaded &&
                    (identity.hideFlags & (HideFlags.NotEditable | HideFlags.HideAndDontSave)) == 0)
                    RegisterSceneObject(identity.gameObject, false);
        }

        public NetworkGameObjectHandle RegisterSceneObject(GameObject instance) =>
            RegisterSceneObject(instance, true);

        private NetworkGameObjectHandle RegisterSceneObject(GameObject instance, bool announce)
        {
            ThrowIfDisposed();
            if (!instance || !instance.scene.isLoaded)
                throw new ArgumentException("A loaded scene object is required.", nameof(instance));
            var identity = instance.GetComponent<SceneIdentity>() ??
                throw new ArgumentException("Scene object requires SceneIdentity.", nameof(instance));
            var key = SceneIdentity.NameKey(instance.transform);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("SceneIdentity requires a saved, loaded scene.");
            if (_sceneObjects.TryGetValue(key, out var existing) && existing != instance)
                throw new InvalidOperationException("Duplicate SceneIdentity key: " + key);
            _sceneObjects[key] = instance;

            var state = _states.Values.FirstOrDefault(value =>
                value.Kind == NetworkObjectKind.Scene && string.Equals(value.SceneKey, key, StringComparison.Ordinal));
            if (state == null && IsAuthority)
            {
                state = CreateState(NetworkObjectKind.Scene, null, key, instance.transform,
                    0, instance.activeSelf);
                _states.Add(state.EntityId, state);
            }
            if (state == null) return null;
            var handle = BindScene(state, instance, identity);
            if (announce && IsAuthority) RpcUpsert(state.Copy());
            return handle;
        }

        public async UniTask<NetworkGameObjectHandle> SpawnAsync(GameObject prefab, Vector3 position,
            Quaternion rotation, uint ownerPeerId = 0, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!IsAuthority) throw new RpcException(RpcError.InvalidRole, "Only Host may spawn network objects.");
            if (!prefab) throw new ArgumentNullException(nameof(prefab));
            var metadata = prefab.GetComponent<NetworkIdentity>();
            if (!metadata || string.IsNullOrWhiteSpace(metadata.PrefabAddress))
                throw new InvalidOperationException("Network prefab requires NetworkIdentity.PrefabAddress.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            var state = CreateState(NetworkObjectKind.Prefab, metadata.PrefabAddress.Trim(), null,
                position, rotation, ownerPeerId, true);
            var instance = UnityEngine.Object.Instantiate(prefab, position, rotation);
            NetworkGameObjectHandle handle = null;
            try
            {
                handle = await BindDynamic(state, instance, linked.Token);
                _states.Add(state.EntityId, state);
                RpcUpsert(state.Copy());
                return handle;
            }
            catch
            {
                if (handle != null) _objects.Remove(state.EntityId);
                if (instance) _loader.Release(instance);
                throw;
            }
        }

        public async UniTask SynchronizeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (IsAuthority) throw new RpcException(RpcError.InvalidRole, "Host already owns the object roster.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            var snapshot = await RpcReadSnapshot(WorldGeneration);
            linked.Token.ThrowIfCancellationRequested();
            if (snapshot == null || snapshot.WorldGeneration != WorldGeneration)
                throw new RpcException(RpcError.InvalidPayload, "Network object snapshot generation mismatch.");

            var included = new HashSet<uint>();
            foreach (var state in snapshot.Objects ?? Array.Empty<NetworkObjectState>())
            {
                included.Add(state.EntityId);
                await ApplyState(state, linked.Token);
            }
            foreach (var stale in _states.Values.Where(state => state.Revision <= snapshot.Revision &&
                         !included.Contains(state.EntityId)).Select(state => state.EntityId).ToArray())
                RemoveLocal(stale);
            _revision = Math.Max(_revision, snapshot.Revision);
        }

        public void SetOwner(uint entityId, uint ownerPeerId)
        {
            ThrowIfDisposed();
            if (!IsAuthority) throw new RpcException(RpcError.InvalidRole, "Only Host may change ownership.");
            if (!_states.TryGetValue(entityId, out var state))
                throw new KeyNotFoundException("Network object is not registered.");
            state.OwnerPeerId = ownerPeerId;
            state.Revision = NextRevision();
            ApplyOwner(state);
            RpcSetOwner(WorldGeneration, entityId, ownerPeerId, state.Revision);
        }

        public UniTask DespawnAsync(uint entityId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAuthority) throw new RpcException(RpcError.InvalidRole, "Only Host may despawn network objects.");
            if (!_states.ContainsKey(entityId)) return UniTask.CompletedTask;
            var revision = NextRevision();
            RemoveLocal(entityId);
            RpcRemove(WorldGeneration, entityId, revision);
            return UniTask.CompletedTask;
        }

        public bool TryGet(uint entityId, out NetworkGameObjectHandle handle) =>
            _objects.TryGetValue(entityId, out handle);

        public bool Owns(uint entityId) =>
            _states.TryGetValue(entityId, out var state) && state.OwnerPeerId == LocalPeerId;

        [global::BITKit.Multiplayer.Rpc(global::BITKit.Multiplayer.SendTo.Host)]
        private UniTask<NetworkObjectSnapshot> RpcReadSnapshot(uint worldGeneration)
        {
            if (!IsAuthority || worldGeneration != WorldGeneration)
                throw new RpcException(RpcError.InvalidPayload, "Network object snapshot generation mismatch.");
            return UniTask.FromResult(new NetworkObjectSnapshot
            {
                WorldGeneration = WorldGeneration,
                Revision = _revision,
                Objects = _states.Values.OrderBy(state => state.EntityId).Select(state => state.Copy()).ToArray()
            });
        }

        [global::BITKit.Multiplayer.Rpc(global::BITKit.Multiplayer.SendTo.All)]
        private void RpcUpsert(NetworkObjectState state)
        {
            if (_disposed || state == null || state.WorldGeneration != WorldGeneration ||
                state.EntityId == 0 || state.Revision == 0 || state.Revision <= _revision) return;
            ApplyState(state, _lifetime.Token).Forget(Report);
        }

        [global::BITKit.Multiplayer.Rpc(global::BITKit.Multiplayer.SendTo.All)]
        private void RpcSetOwner(uint worldGeneration, uint entityId, uint ownerPeerId, ulong revision)
        {
            if (_disposed || worldGeneration != WorldGeneration || revision <= _revision) return;
            _revision = revision;
            if (!_states.TryGetValue(entityId, out var state)) return;
            state.OwnerPeerId = ownerPeerId;
            state.Revision = revision;
            ApplyOwner(state);
        }

        [global::BITKit.Multiplayer.Rpc(global::BITKit.Multiplayer.SendTo.All)]
        private void RpcRemove(uint worldGeneration, uint entityId, ulong revision)
        {
            if (_disposed || worldGeneration != WorldGeneration || revision <= _revision) return;
            _revision = revision;
            RemoveLocal(entityId);
        }

        private async UniTask ApplyState(NetworkObjectState incoming, CancellationToken cancellationToken)
        {
            Validate(incoming);
            if (_states.TryGetValue(incoming.EntityId, out var current) && current.Revision >= incoming.Revision)
                return;
            var state = incoming.Copy();
            _states[state.EntityId] = state;
            _revision = Math.Max(_revision, state.Revision);
            if (_objects.TryGetValue(state.EntityId, out var existing))
            {
                existing.State = state;
                ApplyOwner(state);
                existing.GameObject.SetActive(state.Active);
                return;
            }
            if (state.Kind == NetworkObjectKind.Scene)
            {
                if (_sceneObjects.TryGetValue(state.SceneKey, out var sceneObject) && sceneObject)
                    BindScene(state, sceneObject, sceneObject.GetComponent<SceneIdentity>());
                return;
            }
            if (!_loading.Add(state.EntityId)) return;
            GameObject instance = null;
            try
            {
                instance = await _loader.InstantiateAsync(state.Address, Position(state), Rotation(state), cancellationToken);
                if (!instance) throw new InvalidOperationException("Prefab loader returned no GameObject for " + state.Address);
                if (!_states.TryGetValue(state.EntityId, out var latest) || latest.Revision != state.Revision)
                {
                    _loader.Release(instance);
                    return;
                }
                await BindDynamic(latest, instance, cancellationToken);
            }
            catch
            {
                if (instance && !_objects.ContainsKey(state.EntityId)) _loader.Release(instance);
                throw;
            }
            finally { _loading.Remove(state.EntityId); }
        }

        private NetworkGameObjectHandle BindScene(NetworkObjectState state, GameObject instance,
            SceneIdentity identity)
        {
            if (_objects.TryGetValue(state.EntityId, out var existing)) return existing;
            if (!identity) throw new InvalidOperationException("Scene object lost its SceneIdentity.");
            identity.Bind(state.EntityId, state.OwnerPeerId, IsAuthority);
            instance.transform.SetPositionAndRotation(Position(state), Rotation(state));
            instance.SetActive(state.Active);
            var handle = new NetworkGameObjectHandle(state, instance, identity, IsAuthority);
            _objects[state.EntityId] = handle;
            Spawned?.Invoke(handle);
            return handle;
        }

        private async UniTask<NetworkGameObjectHandle> BindDynamic(NetworkObjectState state,
            GameObject instance, CancellationToken cancellationToken)
        {
            var identity = instance.GetComponent<NetworkIdentity>() ??
                throw new InvalidOperationException("Loaded network prefab requires NetworkIdentity.");
            identity.Bind(state.EntityId, state.OwnerPeerId, IsAuthority);
            instance.transform.SetPositionAndRotation(Position(state), Rotation(state));
            instance.SetActive(state.Active);
            var handle = new NetworkGameObjectHandle(state, instance, identity, IsAuthority);
            if (Initializing != null)
                foreach (Func<NetworkGameObjectHandle, CancellationToken, UniTask> callback in Initializing.GetInvocationList())
                    await callback(handle, cancellationToken);
            _objects[state.EntityId] = handle;
            Spawned?.Invoke(handle);
            return handle;
        }

        private void ApplyOwner(NetworkObjectState state)
        {
            if (!_objects.TryGetValue(state.EntityId, out var handle)) return;
            handle.State = state;
            if (handle.Identity is NetworkIdentity dynamicIdentity) dynamicIdentity.SetOwner(state.OwnerPeerId);
            else if (handle.Identity is SceneIdentity sceneIdentity) sceneIdentity.SetOwner(state.OwnerPeerId);
        }

        private NetworkObjectState CreateState(NetworkObjectKind kind, string address, string sceneKey,
            Transform transform, uint ownerPeerId, bool active) =>
            CreateState(kind, address, sceneKey, transform.position, transform.rotation, ownerPeerId, active);

        private NetworkObjectState CreateState(NetworkObjectKind kind, string address, string sceneKey,
            Vector3 position, Quaternion rotation, uint ownerPeerId, bool active) => new NetworkObjectState
        {
            EntityId = NextEntityId(),
            OwnerPeerId = ownerPeerId,
            WorldGeneration = WorldGeneration,
            Revision = NextRevision(),
            Kind = kind,
            Address = address,
            SceneKey = sceneKey,
            PositionX = position.x,
            PositionY = position.y,
            PositionZ = position.z,
            RotationX = rotation.x,
            RotationY = rotation.y,
            RotationZ = rotation.z,
            RotationW = rotation.w,
            Active = active
        };

        private uint NextEntityId()
        {
            if (_nextEntityId == uint.MaxValue)
                throw new RpcException(RpcError.LimitExceeded, "Network entity ID space exhausted.");
            return ++_nextEntityId;
        }

        private ulong NextRevision()
        {
            if (_revision == ulong.MaxValue)
                throw new RpcException(RpcError.LimitExceeded, "Network object revision exhausted.");
            return ++_revision;
        }

        private static Vector3 Position(NetworkObjectState state) =>
            new Vector3(state.PositionX, state.PositionY, state.PositionZ);

        private static Quaternion Rotation(NetworkObjectState state) =>
            new Quaternion(state.RotationX, state.RotationY, state.RotationZ, state.RotationW);

        private void Validate(NetworkObjectState state)
        {
            if (state == null || state.EntityId == 0 || state.WorldGeneration != WorldGeneration || state.Revision == 0 ||
                !Enum.IsDefined(typeof(NetworkObjectKind), state.Kind) ||
                state.Kind == NetworkObjectKind.Prefab && string.IsNullOrWhiteSpace(state.Address) ||
                state.Kind == NetworkObjectKind.Scene && string.IsNullOrWhiteSpace(state.SceneKey) ||
                !float.IsFinite(state.PositionX) || !float.IsFinite(state.PositionY) || !float.IsFinite(state.PositionZ) ||
                !float.IsFinite(state.RotationX) || !float.IsFinite(state.RotationY) ||
                !float.IsFinite(state.RotationZ) || !float.IsFinite(state.RotationW))
                throw new RpcException(RpcError.InvalidPayload, "Invalid network object state.");
        }

        private void RemoveLocal(uint entityId)
        {
            _states.Remove(entityId);
            if (!_objects.Remove(entityId, out var handle)) return;
            Despawning?.Invoke(handle);
            if (handle.Identity is NetworkIdentity dynamicIdentity) dynamicIdentity.Clear();
            else if (handle.Identity is SceneIdentity sceneIdentity) sceneIdentity.Clear();
            if (handle.IsSceneObject)
            {
                if (handle.GameObject) handle.GameObject.SetActive(false);
            }
            else if (handle.GameObject) _loader.Release(handle.GameObject);
        }

        private void Report(Exception error)
        {
            if (!_disposed) Faulted?.Invoke(error);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UnityNetworkObjects));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _rpcContext.Dispose();
            _lifetime.Cancel();
            foreach (var entityId in _objects.Keys.ToArray()) RemoveLocal(entityId);
            _states.Clear();
            _sceneObjects.Clear();
            _loading.Clear();
            _lifetime.Dispose();
        }
    }
}
