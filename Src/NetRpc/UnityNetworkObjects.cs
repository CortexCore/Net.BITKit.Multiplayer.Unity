using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using UnityEngine;
using CoreHandle = BITKit.Multiplayer.NetRpc.NetworkObjectHandle;
using CoreKind = BITKit.Multiplayer.NetRpc.NetworkObjectKind;
using CoreState = BITKit.Multiplayer.NetRpc.NetworkObjectState;

namespace BITKit.Multiplayer.Unity
{
    // Source-only compatibility DTOs. Core types own the new wire contract.
    [Obsolete("Use BITKit.Multiplayer.NetRpc.NetworkObjectKind.")]
    public enum NetworkObjectKind : byte
    {
        Prefab = 1,
        Scene = 2
    }

    [Obsolete("Use BITKit.Multiplayer.NetRpc.NetworkObjectState. This compatibility DTO is not the wire contract.")]
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

    [Obsolete("Use BITKit.Multiplayer.NetRpc.NetworkObjectSnapshot. This compatibility DTO is not the wire contract.")]
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

    /// <summary>Unity view of a Core-owned object lease; it does not own a second roster.</summary>
    public sealed class NetworkGameObjectHandle
    {
        private readonly CoreHandle _handle;
        internal NetworkGameObjectHandle(CoreHandle handle) => _handle = handle;
        public uint EntityId => _handle.EntityId;
        public uint OwnerPeerId => _handle.OwnerPeerId;
        public GameObject GameObject => (GameObject)_handle.Instance;
        public IUnityNetworkIdentity Identity => (IUnityNetworkIdentity)_handle.Identity;
        public NetEntity Entity => _handle.Entity;
        public bool IsAuthority => _handle.IsAuthority;
        public bool IsOwner(uint localPeerId) => _handle.IsOwner(localPeerId);
        public bool IsSceneObject => _handle.IsSceneObject;
        public string SceneKey => _handle.SceneKey;
        public string PrefabAddress => _handle.PrefabAddress;

        /// <summary>
        /// Call during Initializing to attach the application's entity scope. Core registers it after
        /// all callbacks finish, and unregisters it before releasing Unity. Do not also register it manually.
        /// </summary>
        public void AttachEntity(NetEntity entity) => _handle.AttachEntity(entity);
    }

    /// <summary>
    /// Main-thread facade over NetworkObjectService. Core owns the protocol, roster, IDs,
    /// generation/revision checks, authority, ownership and entity registration.
    /// </summary>
    public sealed class UnityNetworkObjects : IDisposable
    {
        private readonly UnityNetRpcDispatcher _dispatcher;
        private readonly UnityObjectAdapter _adapter;
        private readonly ConditionalWeakTable<CoreHandle, NetworkGameObjectHandle> _handles =
            new ConditionalWeakTable<CoreHandle, NetworkGameObjectHandle>();
        private bool _disposed;

        internal UnityNetworkObjects(RpcContextService runtime, INetworkPrefabLoader loader,
            IEntitiesService entities, uint worldGeneration, uint localPeerId,
            CancellationToken sessionLifetime, UnityNetRpcDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _dispatcher.VerifyMainThread();
            _adapter = new UnityObjectAdapter(loader, dispatcher);
            Core = new NetworkObjectService(runtime, _adapter, entities, worldGeneration, localPeerId, sessionLifetime);
            Core.Initializing += Initialize;
            Core.Spawned += OnSpawned;
            Core.Despawning += OnDespawning;
            Core.Faulted += Report;
        }

        public NetworkObjectService Core { get; }
        public bool IsAuthority => Core.IsAuthority;
        public uint WorldGeneration => Core.WorldGeneration;
        public uint LocalPeerId => Core.LocalPeerId;
        public IReadOnlyCollection<NetworkGameObjectHandle> Objects => Core.Objects.Select(Wrap).ToArray();
        public event Func<NetworkGameObjectHandle, CancellationToken, UniTask> Initializing;
        public event Action<NetworkGameObjectHandle> Spawned;
        public event Action<NetworkGameObjectHandle> Despawning;
        public event Action<Exception> Faulted;

        /// <summary>Starts registration. Await the async variant if Initializing callbacks can await.</summary>
        public void RegisterLoadedSceneObjects()
        {
            VerifyAccess();
            var instances = DiscoverSceneObjects();
            RegisterScenes(instances, CancellationToken.None).Forget(Report);
        }

        public UniTask RegisterLoadedSceneObjectsAsync(CancellationToken cancellationToken = default)
        {
            VerifyAccess();
            return RegisterScenes(DiscoverSceneObjects(), cancellationToken);
        }

        private GameObject[] DiscoverSceneObjects()
        {
            var instances = Resources.FindObjectsOfTypeAll<SceneIdentity>()
                .Where(identity => identity && identity.gameObject.scene.isLoaded &&
                    (identity.hideFlags & (HideFlags.NotEditable | HideFlags.HideAndDontSave)) == 0)
                .Select(identity => identity.gameObject).ToArray();
            foreach (var instance in instances) _adapter.RegisterScene(instance);
            return instances;
        }

        private async UniTask RegisterScenes(GameObject[] instances, CancellationToken cancellationToken)
        {
            foreach (var instance in instances)
                await RegisterSceneObjectAsync(instance, cancellationToken);
        }

        /// <summary>
        /// Returns null while async initialization or remote scene state is pending.
        /// Await RegisterSceneObjectAsync or observe Spawned when completion is required.
        /// </summary>
        public NetworkGameObjectHandle RegisterSceneObject(GameObject instance)
        {
            var pending = RegisterSceneObjectAsync(instance);
            if (pending.Status.IsCompleted()) return pending.GetAwaiter().GetResult();
            ObserveSceneRegistration(pending).Forget(Report);
            return null;
        }

        private static async UniTask ObserveSceneRegistration(UniTask<NetworkGameObjectHandle> pending)
        {
            await pending;
        }

        public async UniTask<NetworkGameObjectHandle> RegisterSceneObjectAsync(GameObject instance,
            CancellationToken cancellationToken = default)
        {
            VerifyAccess();
            cancellationToken.ThrowIfCancellationRequested();
            var key = _adapter.RegisterScene(instance);
            if (IsAuthority)
            {
                var state = CreateTemplate(CoreKind.Scene, null, key, instance.transform.position,
                    instance.transform.rotation, 0, instance.activeSelf);
                var lease = new NetworkObjectInstance(instance, instance.GetComponent<SceneIdentity>());
                return Wrap(await Core.SpawnAsync(state, lease, cancellationToken));
            }
            await Core.RetryPendingAsync(cancellationToken);
            var handle = Core.Objects.FirstOrDefault(value => value.IsSceneObject &&
                string.Equals(value.SceneKey, key, StringComparison.Ordinal));
            return handle == null ? null : Wrap(handle);
        }

        public async UniTask<NetworkGameObjectHandle> SpawnAsync(GameObject prefab, Vector3 position,
            Quaternion rotation, uint ownerPeerId = 0, CancellationToken cancellationToken = default)
        {
            VerifyAccess();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAuthority) throw new RpcException(RpcError.InvalidRole, "Only Host may spawn network objects.");
            if (!prefab) throw new ArgumentNullException(nameof(prefab));
            var metadata = prefab.GetComponent<NetworkIdentity>();
            if (!metadata || string.IsNullOrWhiteSpace(metadata.PrefabAddress))
                throw new InvalidOperationException("Network prefab requires NetworkIdentity.PrefabAddress.");
            var state = CreateTemplate(CoreKind.Prefab, metadata.PrefabAddress.Trim(), null,
                position, rotation, ownerPeerId, true);
            var lease = _adapter.InstantiatePrefab(prefab, position, rotation);
            // Core owns the supplied lease from this call, including failure and cancellation cleanup.
            return Wrap(await Core.SpawnAsync(state, lease, cancellationToken));
        }

        public UniTask SynchronizeAsync(CancellationToken cancellationToken = default)
        {
            VerifyAccess();
            return Core.SynchronizeAsync(cancellationToken);
        }

        public UniTask RetryPendingAsync(CancellationToken cancellationToken = default)
        {
            VerifyAccess();
            return Core.RetryPendingAsync(cancellationToken);
        }

        public void SetOwner(uint entityId, uint ownerPeerId)
        {
            VerifyAccess();
            Core.SetOwner(entityId, ownerPeerId);
        }

        public UniTask DespawnAsync(uint entityId, CancellationToken cancellationToken = default)
        {
            VerifyAccess();
            return Core.DespawnAsync(entityId, cancellationToken);
        }

        public bool TryGet(uint entityId, out NetworkGameObjectHandle handle)
        {
            if (Core.TryGet(entityId, out var coreHandle)) { handle = Wrap(coreHandle); return true; }
            handle = null;
            return false;
        }

        public bool Owns(uint entityId) => Core.Owns(entityId);
        private NetworkGameObjectHandle Wrap(CoreHandle handle) =>
            _handles.GetValue(handle, value => new NetworkGameObjectHandle(value));

        private async UniTask Initialize(CoreHandle handle, CancellationToken cancellationToken)
        {
            await _dispatcher.SwitchToMainThreadAsync();
            var callbacks = Initializing;
            if (callbacks == null) return;
            foreach (Func<NetworkGameObjectHandle, CancellationToken, UniTask> callback in callbacks.GetInvocationList())
            {
                try { await callback(Wrap(handle), cancellationToken); }
                finally { await _dispatcher.SwitchToMainThreadAsync(); }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private void OnSpawned(CoreHandle handle)
        {
            _dispatcher.VerifyMainThread();
            Spawned?.Invoke(Wrap(handle));
        }

        private void OnDespawning(CoreHandle handle)
        {
            _dispatcher.VerifyMainThread();
            Despawning?.Invoke(Wrap(handle));
        }

        private void Report(Exception error)
        {
            if (!_disposed) Faulted?.Invoke(error);
        }

        private void VerifyAccess()
        {
            _dispatcher.VerifyMainThread();
            if (_disposed) throw new ObjectDisposedException(nameof(UnityNetworkObjects));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _dispatcher.VerifyMainThread();
            try { Core.Dispose(); }
            finally
            {
                _disposed = true;
                Core.Initializing -= Initialize;
                Core.Spawned -= OnSpawned;
                Core.Despawning -= OnDespawning;
                Core.Faulted -= Report;
                _adapter.ClearSceneMap();
            }
        }

        private static CoreState CreateTemplate(CoreKind kind, string address, string sceneKey,
            Vector3 position, Quaternion rotation, uint ownerPeerId, bool active) => new CoreState
        {
            Kind = kind, Address = address, SceneKey = sceneKey, OwnerPeerId = ownerPeerId,
            PositionX = position.x, PositionY = position.y, PositionZ = position.z,
            RotationX = rotation.x, RotationY = rotation.y, RotationZ = rotation.z, RotationW = rotation.w,
            Active = active
        };

        private sealed class UnityObjectAdapter : INetworkObjectAdapter, INetworkObjectDispatcher
        {
            private readonly INetworkPrefabLoader _loader;
            private readonly UnityNetRpcDispatcher _dispatcher;
            private readonly Dictionary<string, GameObject> _scenes =
                new Dictionary<string, GameObject>(StringComparer.Ordinal);

            internal UnityObjectAdapter(INetworkPrefabLoader loader, UnityNetRpcDispatcher dispatcher)
            {
                _loader = loader ?? throw new ArgumentNullException(nameof(loader));
                _dispatcher = dispatcher;
            }

            public async UniTask SwitchToEngineThreadAsync(CancellationToken cancellationToken)
            {
                await _dispatcher.SwitchToMainThreadAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            internal string RegisterScene(GameObject instance)
            {
                _dispatcher.VerifyMainThread();
                if (!instance || !instance.scene.isLoaded)
                    throw new ArgumentException("A loaded scene object is required.", nameof(instance));
                if (!instance.GetComponent<SceneIdentity>())
                    throw new ArgumentException("Scene object requires SceneIdentity.", nameof(instance));
                var key = SceneIdentity.NameKey(instance.transform);
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException("SceneIdentity requires a saved, loaded scene.");
                if (_scenes.TryGetValue(key, out var existing) && existing && existing != instance)
                    throw new InvalidOperationException("Duplicate SceneIdentity key: " + key);
                _scenes[key] = instance;
                return key;
            }

            internal void ClearSceneMap() => _scenes.Clear();

            internal NetworkObjectInstance InstantiatePrefab(GameObject prefab, Vector3 position, Quaternion rotation)
            {
                _dispatcher.VerifyMainThread();
                var instance = UnityEngine.Object.Instantiate(prefab, position, rotation);
                try
                {
                    if (!instance) throw new InvalidOperationException("Unity did not create the network prefab.");
                    var identity = instance.GetComponent<NetworkIdentity>();
                    if (!identity) throw new InvalidOperationException("Instantiated network prefab lost its NetworkIdentity.");
                    return new NetworkObjectInstance(instance, identity);
                }
                catch
                {
                    if (instance) _loader.Release(instance);
                    throw;
                }
            }

            public async UniTask<NetworkObjectInstance> InstantiateAsync(CoreState state,
                CancellationToken cancellationToken)
            {
                await SwitchToEngineThreadAsync(cancellationToken);
                if (state.Kind == CoreKind.Scene)
                {
                    if (!_scenes.TryGetValue(state.SceneKey, out var scene) || !scene) return null;
                    var identity = scene.GetComponent<SceneIdentity>();
                    if (!identity) throw new InvalidOperationException("Scene object lost its SceneIdentity.");
                    return new NetworkObjectInstance(scene, identity);
                }
                GameObject instance = null;
                try
                {
                    try
                    {
                        instance = await _loader.InstantiateAsync(state.Address, Position(state),
                            Rotation(state), cancellationToken);
                    }
                    finally { await _dispatcher.SwitchToMainThreadAsync(); }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!instance)
                        throw new InvalidOperationException("Prefab loader returned no GameObject for " + state.Address);
                    var identity = instance.GetComponent<NetworkIdentity>();
                    if (!identity) throw new InvalidOperationException("Loaded network prefab requires NetworkIdentity.");
                    return new NetworkObjectInstance(instance, identity);
                }
                catch
                {
                    _dispatcher.VerifyMainThread();
                    if (instance) _loader.Release(instance);
                    throw;
                }
            }

            public void Bind(NetworkObjectInstance lease, CoreState state, bool authority)
            {
                _dispatcher.VerifyMainThread();
                var instance = (GameObject)lease.Instance;
                if (!instance) throw new InvalidOperationException("Network GameObject was destroyed before binding.");
                // Gameplay activation is deferred until Core has registered and applied initial entity state.
                instance.SetActive(false);
                if (lease.Identity is NetworkIdentity prefab) prefab.Bind(state.EntityId, state.OwnerPeerId, authority);
                else if (lease.Identity is SceneIdentity scene) scene.Bind(state.EntityId, state.OwnerPeerId, authority);
                else throw new InvalidOperationException("Unity lease requires a Unity network identity.");
                instance.transform.SetPositionAndRotation(Position(state), Rotation(state));
            }

            public void ApplyState(NetworkObjectInstance lease, CoreState state)
            {
                _dispatcher.VerifyMainThread();
                var instance = (GameObject)lease.Instance;
                if (!instance) throw new InvalidOperationException("Network GameObject was destroyed outside its object service.");
                if (lease.Identity.EntityId != state.EntityId)
                    throw new InvalidOperationException("Network identity no longer belongs to this object lease.");
                if (lease.Identity is NetworkIdentity prefab) prefab.SetOwner(state.OwnerPeerId);
                else if (lease.Identity is SceneIdentity scene) scene.SetOwner(state.OwnerPeerId);
                instance.SetActive(state.Active);
            }

            public void Release(NetworkObjectInstance lease, CoreState state)
            {
                _dispatcher.VerifyMainThread();
                var instance = (GameObject)lease.Instance;
                if (!instance) return;
                // An old, cancellation-ignoring initialization can finish after the authored scene
                // object has been bound by a new lease. Never clear or deactivate that newer binding.
                var boundId = lease.Identity.EntityId;
                if (boundId != 0 && boundId != state.EntityId) return;
                if (lease.Identity is NetworkIdentity prefab) prefab.Clear();
                else if (lease.Identity is SceneIdentity scene) scene.Clear();
                if (state.Kind == CoreKind.Scene) instance.SetActive(false);
                else _loader.Release(instance);
            }

            private static Vector3 Position(CoreState state) =>
                new Vector3(state.PositionX, state.PositionY, state.PositionZ);
            private static Quaternion Rotation(CoreState state) =>
                new Quaternion(state.RotationX, state.RotationY, state.RotationZ, state.RotationW);
        }
    }
}
