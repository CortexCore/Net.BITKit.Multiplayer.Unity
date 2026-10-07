using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using NetRpcTransport = BITKit.Multiplayer.NetRpc.ITransport;

namespace BITKit.Multiplayer.Unity
{
    /// <summary>Main-thread adapter for a DI-owned RpcContextService. It never creates a container or discovers business targets.</summary>
    public sealed class UnityNetRpcAdapter : IDisposable
    {
        public UnityNetRpcDispatcher Dispatcher { get; } = new UnityNetRpcDispatcher();
        public RpcContextService Runtime { get; }
        private readonly List<(uint Peer, UnityNetRpcTransport Wire)> _wires = new List<(uint, UnityNetRpcTransport)>();
        private readonly List<IAsyncDisposable> _connections = new List<IAsyncDisposable>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private UnityNetworkObjects _networkObjects;
        private readonly bool _host;
        private bool _disposed, _publishing;
        private double _lastPublish, _lastSnapshot;
        public UniTask Disposal { get; private set; } = UniTask.CompletedTask;
        public event Action<Exception> Faulted;
        public UnityNetRpcAdapter(RpcContextService runtime)
        {
            Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _host = runtime.IsServer;
            Runtime.Faulted += Report;
        }
        public CancellationToken Lifetime => _lifetime.Token;

        public UnityNetworkObjects AttachNetworkObjects(uint worldGeneration, uint localPeerId,
            INetworkPrefabLoader prefabLoader)
        {
            return AttachNetworkObjects(worldGeneration, localPeerId, prefabLoader,
                Runtime.Entities ?? throw new InvalidOperationException(
                    "Attach the application's IEntitiesService to Runtime before attaching network objects."));
        }

        public UnityNetworkObjects AttachNetworkObjects(uint worldGeneration, uint localPeerId,
            INetworkPrefabLoader prefabLoader, IEntitiesService entities, bool registerLoadedSceneObjects = true)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UnityNetRpcAdapter));
            Dispatcher.VerifyMainThread();
            if (_networkObjects != null) throw new InvalidOperationException("A Unity network-object world is already attached.");
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            if (!ReferenceEquals(Runtime.Entities, entities))
                throw new InvalidOperationException("Network objects must use the IEntitiesService already attached to this Runtime.");
            var objects = new UnityNetworkObjects(Runtime, prefabLoader, entities,
                worldGeneration, localPeerId, Lifetime, Dispatcher);
            objects.Faulted += Report;
            try
            {
                if (registerLoadedSceneObjects) objects.RegisterLoadedSceneObjects();
                _networkObjects = objects;
                return objects;
            }
            catch
            {
                objects.Faulted -= Report;
                objects.Dispose();
                throw;
            }
        }

        public void DetachNetworkObjects()
        {
            Dispatcher.VerifyMainThread();
            var objects = _networkObjects;
            if (objects == null) return;
            _networkObjects = null;
            try { objects.Dispose(); }
            finally { objects.Faulted -= Report; }
        }

        public void AttachPeer(uint peer, NetRpcTransport transport, ITransportLifetime life, IAsyncDisposable connectionOwner = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UnityNetRpcAdapter));
            if (Thread.CurrentThread.ManagedThreadId != Dispatcher.MainThreadId) throw new InvalidOperationException("Attach Unity NetRpc peers on main thread.");
            var wire = new UnityNetRpcTransport(transport, life, Dispatcher); wire.Faulted += Report;
            try { Runtime.AttachPeer(peer, wire); _wires.Add((peer, wire)); if (connectionOwner != null) _connections.Add(connectionOwner); }
            catch { wire.Dispose(); throw; }
        }
        public void Pump(double unscaledSeconds)
        {
            if (_disposed) return; Dispatcher.Pump();
            if (_host && !_publishing && unscaledSeconds - _lastPublish >= 0.05)
            { _lastPublish = unscaledSeconds; Publish(unscaledSeconds).Forget(); }
        }
        private async UniTaskVoid Publish(double seconds)
        {
            _publishing = true;
            try { bool full = seconds - _lastSnapshot >= 1; if (full) _lastSnapshot = seconds; await Runtime.PublishStateAsync(full); }
            catch (Exception e) { if (!_disposed) Report(e); }
            finally { _publishing = false; }
        }
        private void Report(Exception e) => Faulted?.Invoke(e);
        public void Dispose()
        {
            if (_disposed) return;
            if (Thread.CurrentThread.ManagedThreadId != Dispatcher.MainThreadId) throw new InvalidOperationException("Dispose Unity NetRpc session on main thread.");
            DetachNetworkObjects();
            _disposed = true; _lifetime.Cancel(); Runtime.Faulted -= Report;
            var wires = _wires.ToArray(); var connections = _connections.ToArray(); _wires.Clear(); _connections.Clear();
            try
            {
                foreach (var entry in wires) Runtime.DetachPeer(entry.Peer);
            }
            finally
            {
                foreach (var entry in wires) { entry.Wire.Faulted -= Report; entry.Wire.Dispose(); }
                Dispatcher.Dispose(); Disposal = CloseConnections(connections, wires).Preserve();
            }
        }
        private async UniTask CloseConnections(IAsyncDisposable[] connections, (uint Peer, UnityNetRpcTransport Wire)[] wires)
        {
            var tasks = new List<UniTask>();
            foreach (var owner in connections) { try { tasks.Add(CloseOwner(owner)); } catch (Exception e) { tasks.Add(UniTask.FromException(e)); } }
            foreach (var entry in wires) tasks.Add(entry.Wire.OutboundCompletion);
            try { await UniTask.WhenAll(tasks); }
            finally { _lifetime.Dispose(); }
        }
        private static async UniTask CloseOwner(IAsyncDisposable owner) => await owner.DisposeAsync().ConfigureAwait(false);
    }
}
