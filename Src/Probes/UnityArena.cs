using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;


namespace BITKit.Multiplayer.NetRpc.Probes
{
    public struct UnityArenaPosition
    {
        public float X { get; set; }
        public float Y { get; set; }
        public UnityArenaPosition(float x, float y) { X = x; Y = y; }
    }
    [NetRpcRemoteContract("Packages/net.bitkit.multiplayer.unity/Probes/Generated/UnityArenaRemote.g.cs")]
    public interface IUnityArena
    {
        UniTask<int> Plus(int a, int b);
        UniTask<bool> Pickup(int slot);
        int Tick { get; }
        IList<int> Items { get; }
        IDictionary<int, int> Counts { get; }
    }
    public sealed class UnityArenaWorld
    {
        private readonly int _thread;
        public NetComponent<UnityArenaPosition>[] Positions { get; } = {
            new NetComponent<UnityArenaPosition>(1, new UnityArenaPosition(120, 150)),
            new NetComponent<UnityArenaPosition>(1, new UnityArenaPosition(240, 150)) };
        public NetComponent<int>[] Health { get; } = { new NetComponent<int>(2, 100), new NetComponent<int>(2, 100) };
        public NetworkList<int> Items { get; } = new NetworkList<int>();
        public NetworkDictionary<int, int> Counts { get; } = new NetworkDictionary<int, int>();
        public int Tick { get; private set; }
        public int ThreadViolations { get; private set; }
        public int BusinessCalls { get; private set; }
        public int Broadcasts { get; private set; }
        public int AsyncStarted { get; set; }
        public int AsyncCompleted { get; set; }
        public CancellationToken Lifetime { get; }
        public UnityArenaWorld(int thread, CancellationToken lifetime = default) { _thread = thread; Lifetime = lifetime; }
        public void CheckThread()
        { if (Thread.CurrentThread.ManagedThreadId != _thread) { ThreadViolations++; throw new InvalidOperationException("Unity arena business executed outside main thread."); } }
        public void Advance(float seconds) { CheckThread(); Tick++; }
        public void Move(int slot, float x, float y)
        {
            CheckThread(); BusinessCalls++;
            if (slot < 1 || slot > 2 || float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)) throw new ArgumentException("Invalid input.");
            var p = Positions[slot - 1].Value; p.X = Math.Max(16, Math.Min(400, p.X + Math.Max(-1, Math.Min(1, x)) * 8)); p.Y = Math.Max(16, Math.Min(270, p.Y + Math.Max(-1, Math.Min(1, y)) * 8)); Positions[slot - 1].Value = p;
        }
        public int Attack(int slot) { CheckThread(); BusinessCalls++; Health[slot - 1].Value = Math.Max(0, Health[slot - 1].Value - 20); return Health[slot - 1].Value; }
        public void Broadcast() { CheckThread(); Broadcasts++; }
        public bool Pickup(int slot) { CheckThread(); BusinessCalls++; Items.Add(slot); Counts[slot] = Counts.TryGetValue(slot, out var count) ? count + 1 : 1; return true; }
    }
    public sealed class UnityArenaService : IUnityArena
    {
        private readonly UnityArenaWorld _world;
        public UnityArenaService(UnityArenaWorld world) => _world = world;
        public UniTask<int> Plus(int a, int b) { _world.CheckThread(); return UniTask.FromResult(a + b); }
        public UniTask<bool> Pickup(int slot) => UniTask.FromResult(_world.Pickup(slot));
        public int Tick { get { _world.CheckThread(); return _world.Tick; } }
        public IList<int> Items => _world.Items;
        public IDictionary<int, int> Counts => _world.Counts;
    }
    public sealed class UnityArenaCommands : IDisposable
    {
        private readonly UnityArenaWorld _world;
        public UnityArenaCommands(UnityArenaWorld world, IRpcContext<UnityArenaCommands> rpcContext) => _world = world;
        [Rpc(SendTo.Host)] public void Move(int slot, float x, float y) => _world.Move(slot, x, y);
        [Rpc(SendTo.Host)] public UniTask<int> Attack(int slot) => UniTask.FromResult(_world.Attack(slot));
        [Rpc(SendTo.Host)] public async UniTask<int> AsyncThread(int value) { _world.CheckThread(); await Task.Delay(15, _world.Lifetime).ConfigureAwait(false); await UniTask.SwitchToMainThread(_world.Lifetime); _world.CheckThread(); return value; }
        [Rpc(SendTo.Host)] public async UniTask<int> LifetimeThread(int milliseconds) { _world.CheckThread(); _world.AsyncStarted++; await Task.Delay(milliseconds, _world.Lifetime).ConfigureAwait(false); await UniTask.SwitchToMainThread(_world.Lifetime); _world.CheckThread(); _world.AsyncCompleted++; return 42; }
        [Rpc(SendTo.All, RpcDelivery.Unreliable)] public void Signal() => _world.Broadcast();
        public void Dispose() { }
    }
}
