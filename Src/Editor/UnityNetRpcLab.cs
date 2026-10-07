using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.NetRpc.Probes;
using BITKit.Multiplayer.Unity;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using UnityEditor;
using UnityEngine;

namespace BITKit.Multiplayer.EditorNetRpc
{
    public sealed class UnityNetRpcLab : IDisposable
    {
        public readonly UnityNetRpcDispatcher Dispatcher = new UnityNetRpcDispatcher();
        public UnityArenaWorld HostWorld { get; private set; }
        public UnityArenaWorld ClientWorld { get; private set; }
        public UnityArenaCommands HostCommands { get; private set; }
        public UnityArenaCommands ClientCommands { get; private set; }
        public IUnityArena Remote { get; private set; }
        public bool Ready { get; private set; }
        public string Error { get; private set; }
        public bool Woven => typeof(UnityArenaCommands).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Any(m => m.Name.StartsWith("__netrpc_recv_"));
        public int Notifications { get; private set; }
        public int Pending => Dispatcher.PendingCount + (_hostSession?.Dispatcher.PendingCount ?? 0) + (_clientSession?.Dispatcher.PendingCount ?? 0);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private ServiceProvider _host, _client;
        private UnityNetRpcAdapter _hostSession, _clientSession;
        private RpcContextService _hostRuntime;
        private TcpTransport _hostSocket, _clientSocket;
        private bool _disposed;
        private bool _hostAttached, _clientAttached;
        private Task _initialization = Task.CompletedTask;
        public Task Disposal { get; private set; } = Task.CompletedTask;
        private double _lastTick;
        private static UnityNetRpcLab _active;
        public static UnityNetRpcLab Active => _active;
        public static Task<string> LastSmoke { get; private set; }
        public static Task<string> LastLifecycle { get; private set; }
        public static void BeginSmokeReadback() { LastSmoke = Smoke(); }
        public static void BeginLifecycleReadback() { LastLifecycle = Lifecycle(); }
        public static string AdapterLifecycleReadback()
        {
            var dispatcher = new UnityNetRpcDispatcher(); var inner = new LoanProbeTransport();
            var wire = new UnityNetRpcTransport(inner, inner, dispatcher); int deliveries = 0, closed = 0, faults = 0; int value = 0;
            wire.OnReceived += bytes => { if (Thread.CurrentThread.ManagedThreadId != dispatcher.MainThreadId) throw new Exception("Ingress thread violation."); deliveries++; value = bytes.Span[0]; };
            wire.Closed += () => closed++; wire.Faulted += _ => faults++;
            Task.Run(() => { var bytes = new byte[] { 42 }; inner.Push(bytes); bytes[0] = 0; }).GetAwaiter().GetResult();
            if (deliveries != 0 || dispatcher.PendingCount != 1) throw new Exception("Borrowed ingress was not queued.");
            dispatcher.Pump(); if (deliveries != 1 || value != 42 || dispatcher.PendingBytes != 0) throw new Exception("Borrowed buffer ownership failed.");
            var outbound = new byte[] { 42 }; wire.Send(outbound).GetAwaiter().GetResult(); outbound[0] = 0;
            // Fake backend continuation uses only worker threads; it never requires Editor pumping.
            if (!wire.OutboundCompletion.AsTask().Wait(2000) || inner.SentValue != 42) throw new Exception("Outbound copied-loan/async lifetime failed.");
            Task.Run(() => { for (int i = 0; i < 257; i++) inner.Push(new byte[] { 1 }); }).GetAwaiter().GetResult();
            if (dispatcher.PendingCount > 288) throw new Exception("Queue bounds failed.");
            dispatcher.Pump(int.MaxValue);
            if (closed != 1 || faults != 1 || deliveries != 1 || dispatcher.PendingCount != 0 || dispatcher.PendingBytes != 0) throw new Exception("Overflow failed close/quiescence.");
            int late = 0; wire.Closed += () => late++; dispatcher.Pump(); if (late != 1) throw new Exception("Late Closed subscription was lost.");
            wire.Dispose(); dispatcher.Dispose(); inner.Push(new byte[] { 99 }); if (deliveries != 1) throw new Exception("Post-disposal callback escaped.");
            var sendDispatcher = new UnityNetRpcDispatcher(); var slow = new LoanProbeTransport { DelayMilliseconds = 5000 };
            var sending = new UnityNetRpcTransport(slow, slow, sendDispatcher);
            sending.Send(new byte[] { 1 }).GetAwaiter().GetResult();
            if (!slow.Started.Task.Wait(2000)) throw new Exception("Async send did not begin.");
            sending.Send(new byte[] { 2 }).GetAwaiter().GetResult(); sending.Dispose(); sendDispatcher.Dispose();
            if (!sending.OutboundCompletion.AsTask().Wait(2000) || sending.PendingOutboundBytes != 0 || slow.SentValue != 0) throw new Exception("Disposal did not cancel in-flight/drain queued sends.");
            var bounded = new UnityNetRpcDispatcher(); var peers = new List<UnityNetRpcTransport>(); var sources = new List<LoanProbeTransport>(); int closes = 0;
            for (int i = 0; i < 32; i++) { var source = new LoanProbeTransport(); var peer = new UnityNetRpcTransport(source, source, bounded); peer.Closed += () => { if (Thread.CurrentThread.ManagedThreadId != bounded.MainThreadId) throw new Exception("Close thread violation."); closes++; }; peers.Add(peer); sources.Add(source); }
            bool capped = false; try { var extra = new LoanProbeTransport(); new UnityNetRpcTransport(extra, extra, bounded); } catch (InvalidOperationException) { capped = true; }
            Task.Run(() => { foreach (var source in sources) source.Close(); }).GetAwaiter().GetResult();
            if (!capped || bounded.PendingCount != 32) throw new Exception("Control admission/reservation failed.");
            bounded.Pump(int.MaxValue); if (closes != 32) throw new Exception("A saturated close notification was lost.");
            foreach (var peer in peers) peer.Closed += () => closes++;
            bounded.Pump(int.MaxValue); if (closes != 64) throw new Exception("Saturated late Closed was lost.");
            foreach (var peer in peers) peer.Dispose(); bounded.Dispose();
            var byteDispatcher = new UnityNetRpcDispatcher(); var byteSource = new LoanProbeTransport(); var byteWire = new UnityNetRpcTransport(byteSource, byteSource, byteDispatcher); int byteClosed = 0;
            byteWire.Closed += () => byteClosed++; byteSource.Push(new byte[byteDispatcher.MaximumBytes + 1]); byteDispatcher.Pump();
            if (byteClosed != 1 || byteDispatcher.PendingBytes != 0) throw new Exception("Ingress byte bound failed.");
            byteWire.Dispose(); byteDispatcher.Dispose();
            var stoppedDispatcher = new UnityNetRpcDispatcher(); var stoppedSource = new LoanProbeTransport(); var stoppedWire = new UnityNetRpcTransport(stoppedSource, stoppedSource, stoppedDispatcher); int stale = 0;
            stoppedWire.OnReceived += _ => stale++; stoppedSource.Push(new byte[] { 1 }); stoppedDispatcher.Dispose(); stoppedWire.Dispose();
            if (stale != 0 || stoppedDispatcher.PendingBytes != 0) throw new Exception("Disposed dispatcher executed stale business.");
            return "PASS adapter: ingress/outbound borrowed bytes survive poison; main-thread delivery; item/byte bounds; overflow closes once; 32 reserved close slots and late subscribers delivered; async in-flight send canceled; queued sends returned; disposed callbacks suppressed; pending=0; bytes=0";
        }
        private sealed class LoanProbeTransport : BITKit.Multiplayer.NetRpc.ITransport, ITransportLifetime
        {
            public event Action<ReadOnlyMemory<byte>> OnReceived;
            public event Action Closed;
            public void Close() => Closed?.Invoke();
            public int SentValue;
            public int DelayMilliseconds = 10;
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Push(byte[] bytes) => OnReceived?.Invoke(bytes);
            public async UniTask Send(ReadOnlyMemory<byte> bytes, CancellationToken token = default) { Started.TrySetResult(true); await Task.Delay(DelayMilliseconds, token).ConfigureAwait(false); SentValue = bytes.Span[0]; }
            public UniTask SendFast(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => default;
        }

        [MenuItem("Tools/BITKit/NetRpc/Open Unity Sync Lab")]
        public static void Open() => UnityNetRpcLabWindow.Open();
        public static UnityNetRpcLab Start()
        {
            _active?.Dispose();
            var lab = new UnityNetRpcLab(); _active = lab;
            EditorApplication.update += lab.Update; AssemblyReloadEvents.beforeAssemblyReload += lab.BeforeReload; EditorApplication.quitting += lab.Dispose;
            lab._initialization = lab.Initialize(); return lab;
        }
        private async Task Initialize()
        {
            try
            {
                using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
                var accepting = listener.AcceptAsync(_lifetime.Token);
                _clientSocket = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port, cancellationToken: _lifetime.Token);
                _hostSocket = await accepting;
                if (_disposed) return;
                await UniTask.SwitchToMainThread(_lifetime.Token);
                if (_disposed) return;
                HostWorld = new UnityArenaWorld(Dispatcher.MainThreadId, _lifetime.Token); ClientWorld = new UnityArenaWorld(Dispatcher.MainThreadId, _lifetime.Token);
                _hostSession = CreateSession(HostWorld, true, out _host); _clientSession = CreateSession(ClientWorld, false, out _client);
                _hostSession.Faulted += e => Error = e.Message; _clientSession.Faulted += e => Error = e.Message;
                _hostSession.AttachPeer(2, _hostSocket, _hostSocket, _hostSocket); _hostAttached = true;
                _clientSession.AttachPeer(1, _clientSocket, _clientSocket, _clientSocket); _clientAttached = true;
                _hostRuntime = _host.GetRequiredService<RpcContextService>(); _ = _client.GetRequiredService<RpcContextService>();
                RegisterEntities(_host, HostWorld); RegisterEntities(_client, ClientWorld);
                HostCommands = _host.GetRequiredService<UnityArenaCommands>(); ClientCommands = _client.GetRequiredService<UnityArenaCommands>();
                Remote = _client.GetRequiredService<IUnityArena>();
                ClientWorld.Positions[0].Changed += (_, __) => Notification(); ClientWorld.Health[1].Changed += (_, __) => Notification();
                ((NetworkCollection)Remote.Items).Changed += _ => Notification();
                _hostRuntime.Faulted += e => Error = e.Message; _client.GetRequiredService<RpcContextService>().Faulted += e => Error = e.Message;
                Ready = true;
            }
            catch (Exception e) { if (!_disposed) Error = e.ToString(); }
            finally
            {
                if (!Ready)
                {
                    if (!_hostAttached && _hostSocket != null) await _hostSocket.DisposeAsync().ConfigureAwait(false);
                    if (!_clientAttached && _clientSocket != null) await _clientSocket.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        private void Notification() { ClientWorld.CheckThread(); Notifications++; }
        private static UnityNetRpcAdapter CreateSession(UnityArenaWorld world, bool host, out ServiceProvider provider)
        {
            var services = new ServiceCollection().AddSingleton(world).AddSingleton<IEntitiesService, EntitiesService>();
            services.AddSingleton<UnityArenaCommands>();
            if (host) services.AddNetRpcService<IUnityArena, UnityArenaService>(); else services.AddRemoteInterface<IUnityArena>();
            services.AddSingleton<IRemoteInterfaceFactory, PrecompiledRemoteInterfaceFactory>();
            services.AddNetRpcRuntime(host, 0x554E495459UL);
            provider = services.BuildServiceProvider();
            var runtime = provider.GetRequiredService<RpcContextService>();
            runtime.AllowContract(typeof(IUnityArena)); runtime.AllowContract(typeof(UnityArenaCommands));
            if (host) _ = provider.GetRequiredService<UnityArenaService>();
            return new UnityNetRpcAdapter(runtime);
        }
        private void RegisterEntities(ServiceProvider provider, UnityArenaWorld world)
        {
            for (int i = 0; i < 2; i++)
            {
                var scope = new ServiceCollection().AddSingleton<INetworkIdentity>(
                        new BITKit.Multiplayer.NetRpc.NetworkIdentity((uint)(i + 1)))
                    .AddSingleton<INetComponent>(world.Positions[i]).AddSingleton<INetComponent>(world.Health[i]).BuildServiceProvider();
                _owned.Add(scope); provider.GetRequiredService<IEntitiesService>().Register(new NetEntity(scope));
            }
        }
        private void Update()
        {
            if (_disposed) return;
            try
            {
                Dispatcher.Pump(); if (!Ready) return;
                double now = EditorApplication.timeSinceStartup;
                // Public Unity composition owner is the actual path used by this lab.
                _hostSession.Pump(now); _clientSession.Pump(now);
                if (now - _lastTick < 0.05) return; _lastTick = now; HostWorld.Advance(0.05f);
            }
            catch (Exception e) { Error = e.ToString(); }
        }
        public static void Stop() => _active?.Dispose();
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; Ready = false;
            EditorApplication.update -= Update; AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload; EditorApplication.quitting -= Dispose;
            _lifetime.Cancel(); _hostSession?.Dispose(); _clientSession?.Dispose(); _host?.Dispose(); _client?.Dispose();
            foreach (var owned in _owned) owned.Dispose(); _owned.Clear(); Dispatcher.Dispose();
            Disposal = FinishDisposal();
            if (ReferenceEquals(_active, this)) _active = null;
        }
        private async Task FinishDisposal()
        {
            try
            {
                await Task.WhenAll(_initialization, (_hostSession?.Disposal ?? UniTask.CompletedTask).AsTask(), (_clientSession?.Disposal ?? UniTask.CompletedTask).AsTask()).ConfigureAwait(false);
            }
            finally { _lifetime.Dispose(); }
        }
        private void BeforeReload()
        {
            bool ready = Ready; Dispose();
            // Ready-session shutdown consists only of socket/send worker tasks, never Editor continuations.
            bool complete = ready && Disposal.Wait(2000);
            SessionState.SetString("BITKit.NetRpc.ReloadReadback", $"ready={ready}; disposal-complete={complete}; pending={Pending}; bytes={Dispatcher.PendingBytes + (_hostSession?.Dispatcher.PendingBytes ?? 0) + (_clientSession?.Dispatcher.PendingBytes ?? 0)}; active={Active != null}");
        }
        public static string ReloadReadback() => SessionState.GetString("BITKit.NetRpc.ReloadReadback", "No active lab reload captured.");
        public static async Task<string> Lifecycle()
        {
            UnityNetRpcLabWindow.Open(); var lab = Start();
            try
            {
                await Wait(() => lab.Ready || lab.Error != null, lab);
                if (lab.Error != null) throw new Exception(lab.Error);
                var pending = lab.ClientCommands.LifetimeThread(1000);
                await Wait(() => lab.HostWorld.AsyncStarted == 1, lab);
                var world = lab.HostWorld; int tick = world.Tick, notifications = lab.Notifications;
                UnityEngine.Resources.FindObjectsOfTypeAll<UnityNetRpcLabWindow>().Single().Close();
                await lab.Disposal; await Task.Delay(75);
                if (Active != null || lab.Pending != 0 || world.Tick != tick || lab.Notifications != notifications || world.AsyncCompleted != 0) throw new Exception("Closed-window stale callback/queue violation.");
                bool ended = false;
                try { await pending; } catch (Exception) { ended = true; }
                if (!ended || !lab.Disposal.IsCompletedSuccessfully) throw new Exception("Session shutdown left a request/socket worker alive.");
                AssertSocketWorkersStopped(lab._hostSocket); AssertSocketWorkersStopped(lab._clientSocket);
                string smoke = await Smoke();
                var connecting = Start(); connecting.Dispose(); await connecting.Disposal;
                if (Active != null || connecting.Pending != 0) throw new Exception("Stop-during-connect left stale work.");
                return "PASS lifecycle: window-close cancels in-flight business; pending request ended; TCP/UDP/proof workers completed; queues=0; ticks/notifications frozen; fresh session smoke passed; stop-during-connect completed. " + smoke;
            }
            finally { lab.Dispose(); await lab.Disposal; }
        }
        private static void AssertSocketWorkersStopped(TcpTransport socket)
        {
            foreach (var name in new[] { "_receiveLoop", "_udpLoop", "_udpProofLoop" })
                if (((UniTask)typeof(TcpTransport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(socket)).Status == UniTaskStatus.Pending) throw new Exception("Socket worker remains: " + name);
        }
        public static async Task<string> Smoke()
        {
            var lab = Start();
            try
            {
                await Wait(() => lab.Ready || lab.Error != null, lab); if (lab.Error != null) throw new Exception(lab.Error);
                if (!lab.Woven) throw new Exception("Unity ILPP did not emit NetRpc receivers.");
                if (await lab.Remote.Plus(20, 22) != 42) throw new Exception("Remote native proxy result failed.");
                int before = lab.HostWorld.BusinessCalls;
                lab.ClientCommands.Move(1, 1, 0);
                int hp = await lab.ClientCommands.Attack(2);
                if (hp != 80 || await lab.ClientCommands.AsyncThread(42) != 42 || !await lab.Remote.Pickup(1)) throw new Exception("RPC result failed.");
                lab.HostCommands.Signal();
                await Wait(() => lab.ClientWorld.Positions[0].Value.X == 128 && lab.ClientWorld.Health[1].Value == 80 && lab.Remote.Items.Count == 1 && lab.Remote.Counts.ContainsKey(1) && lab.ClientWorld.Broadcasts == 1, lab);
                bool denied = false; try { lab.ClientWorld.Health[1].Value = 999; } catch (RpcException) { denied = true; }
                if (!denied || lab.HostWorld.Positions[0].Value.X != 128 || lab.HostWorld.BusinessCalls - before != 3 || lab.HostWorld.Broadcasts != 1 || lab.ClientWorld.ThreadViolations != 0 || lab.HostWorld.ThreadViolations != 0) throw new Exception("Authority/thread/once-only failed.");
                return $"PASS Unity {Application.unityVersion}: proxy={lab.Remote.GetType().Name}; woven={lab.Woven}; Plus=42; HP=80; X=128; List=1; Dictionary[1]=1; All=1/1; async-main-thread=true; Changed-main-thread=true; authority-denied=true; notifications={lab.Notifications}";
            }
            finally { lab.Dispose(); await lab.Disposal; }
        }
        private static async Task Wait(Func<bool> condition, UnityNetRpcLab lab)
        { var deadline = DateTime.UtcNow.AddSeconds(8); while (!condition()) { if (lab.Error != null) throw new Exception(lab.Error); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Unity NetRpc smoke timed out."); await Task.Delay(15); } }
    }

    public sealed class UnityNetRpcLabWindow : EditorWindow
    {
        private string _action;
        public static void Open() { var window = GetWindow<UnityNetRpcLabWindow>(); window.titleContent = new GUIContent("NetRpc Unity Lab"); window.minSize = new Vector2(620, 500); window.Show(); }
        private void OnEnable() => EditorApplication.update += Repaint;
        private void OnDisable() { EditorApplication.update -= Repaint; UnityNetRpcLab.Stop(); }
        private void OnGUI()
        {
            GUILayout.Label("NETRPC / UNITY MAIN-THREAD SYNC LAB", EditorStyles.boldLabel);
            GUILayout.Label("New MessagePack backend · actual Unity ILPP · TCP + UDP · Edit Mode");
            using (new EditorGUILayout.HorizontalScope()) { if (GUILayout.Button("START HOST + CLIENT")) UnityNetRpcLab.Start(); if (GUILayout.Button("STOP")) UnityNetRpcLab.Stop(); if (GUILayout.Button("RUN SMOKE")) RunSmoke(); }
            var lab = UnityNetRpcLab.Active;
            if (lab == null) { EditorGUILayout.HelpBox(_action ?? "Start to test the new backend. This lab owns its sessions and closes them on window/reload/quit.", MessageType.Info); return; }
            if (lab.Error != null) EditorGUILayout.HelpBox(lab.Error, MessageType.Error);
            if (!lab.Ready) { GUILayout.Label("Connecting..."); return; }
            GUILayout.Label($"Ready · woven {lab.Woven} · tick {lab.HostWorld.Tick} · main thread {lab.Dispatcher.MainThreadId} · queue {lab.Pending}");
            using (new EditorGUILayout.HorizontalScope()) { if (GUILayout.Button("MOVE P1 RIGHT")) lab.ClientCommands.Move(1, 1, 0); if (GUILayout.Button("ATTACK P2")) Attack(lab); if (GUILayout.Button("PICKUP")) Pickup(lab); if (GUILayout.Button("UDP ALL SIGNAL")) lab.HostCommands.Signal(); }
            var area = GUILayoutUtility.GetRect(440, 280, GUILayout.ExpandWidth(true)); EditorGUI.DrawRect(area, new Color(.05f, .07f, .08f));
            for (int i = 0; i < 2; i++) { var p = lab.ClientWorld.Positions[i].Value; var color = i == 0 ? new Color(.2f, .85f, .9f) : new Color(1, .65f, .2f); var at = new Vector2(area.x + p.X, area.y + p.Y); EditorGUI.DrawRect(new Rect(at.x - 12, at.y - 12, 24, 24), color); GUI.Label(new Rect(at.x - 30, at.y - 45, 120, 30), $"P{i + 1}: {lab.ClientWorld.Health[i].Value} HP"); }
            GUILayout.Label($"Items: [{string.Join(",", lab.Remote.Items)}]  Counts: {lab.Remote.Counts.Count}  All: {lab.HostWorld.Broadcasts}/{lab.ClientWorld.Broadcasts}  Changed: {lab.Notifications}");
            GUILayout.Label(_action ?? "");
        }
        private async void RunSmoke() { try { _action = await UnityNetRpcLab.Smoke(); Debug.Log(_action); } catch (Exception e) { _action = e.ToString(); Debug.LogError(e); } }
        private async void Attack(UnityNetRpcLab lab) { try { _action = "RPC HP=" + await lab.ClientCommands.Attack(2); } catch (Exception e) { _action = e.Message; } }
        private async void Pickup(UnityNetRpcLab lab) { try { _action = "Pickup=" + await lab.Remote.Pickup(1); } catch (Exception e) { _action = e.Message; } }
    }
}
