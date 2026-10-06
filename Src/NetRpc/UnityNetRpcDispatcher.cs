using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using NetRpcTransport = BITKit.Multiplayer.NetRpc.ITransport;

namespace BITKit.Multiplayer.Unity
{
    /// <summary>Per-session bounded ingress. Call Pump on Unity's main thread; no static current runtime.</summary>
    public sealed class UnityNetRpcDispatcher : IDisposable
    {
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private readonly Queue<Action> _queue = new Queue<Action>();
        private readonly Queue<(object Owner, Action Deliver)> _control = new Queue<(object, Action)>();
        private readonly HashSet<object> _controlOwners = new HashSet<object>();
        private readonly HashSet<object> _queuedControl = new HashSet<object>();
        private readonly object _gate = new object();
        private int _bytes;
        private bool _disposed;
        public int MaximumItems { get; } = 256;
        public int MaximumBytes { get; } = 4 * 1024 * 1024;
        public int MainThreadId => _thread;
        internal bool IsDisposed { get { lock (_gate) return _disposed; } }
        public int PendingCount { get { lock (_gate) return _queue.Count + _control.Count; } }
        public int PendingBytes { get { lock (_gate) return _bytes; } }
        internal void RegisterControl(object owner)
        { lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(UnityNetRpcDispatcher)); if (_controlOwners.Count >= 32) throw new InvalidOperationException("Unity NetRpc dispatcher supports at most 32 transports."); _controlOwners.Add(owner); } }
        internal void UnregisterControl(object owner)
        {
            lock (_gate)
            {
                _controlOwners.Remove(owner); _queuedControl.Remove(owner);
                int count = _control.Count;
                for (int i = 0; i < count; i++) { var next = _control.Dequeue(); if (!ReferenceEquals(next.Owner, owner)) _control.Enqueue(next); }
            }
        }
        internal bool PostControl(object owner, Action action)
        { lock (_gate) { if (_disposed || !_controlOwners.Contains(owner)) return false; if (_queuedControl.Add(owner)) _control.Enqueue((owner, action)); return true; } }
        internal bool Post(Action action, int bytes)
        {
            lock (_gate)
            {
                if (_disposed || _queue.Count >= MaximumItems || bytes > MaximumBytes - _bytes) return false;
                _bytes += bytes; _queue.Enqueue(() => { try { action(); } finally { lock (_gate) _bytes -= bytes; } }); return true;
            }
        }
        public void Pump(int maximum = 128)
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("NetRpc dispatcher requires Unity main thread.");
            for (int i = 0; i < maximum; i++)
            {
                Action next; lock (_gate) { if (_control.Count > 0) { var control = _control.Dequeue(); _queuedControl.Remove(control.Owner); next = control.Deliver; } else { if (_queue.Count == 0) return; next = _queue.Dequeue(); } }
                next();
            }
        }
        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("Dispose NetRpc dispatcher on Unity main thread.");
            lock (_gate) _disposed = true;
            // Drain transport callbacks in disposed/generation-checked mode so every retained array is returned.
            Pump(int.MaxValue);
        }
    }

    public sealed class UnityNetRpcTransport : NetRpcTransport, ITransportLifetime, IDisposable
    {
        private readonly NetRpcTransport _inner;
        private readonly ITransportLifetime _life;
        private readonly UnityNetRpcDispatcher _dispatcher;
        private int _disposed, _lost;
        private readonly object _sendGate = new object();
        private readonly CancellationTokenSource _sendLifetime = new CancellationTokenSource();
        private readonly Queue<(byte[] Bytes, int Length, bool Fast, CancellationToken Token)> _outbound = new Queue<(byte[], int, bool, CancellationToken)>();
        private int _outboundBytes;
        private bool _sending;
        public UniTask OutboundCompletion { get; private set; } = UniTask.CompletedTask;
        public int PendingOutboundBytes { get { lock (_sendGate) return _outboundBytes; } }
        public event Action<ReadOnlyMemory<byte>> OnReceived;
        private readonly object _closeGate = new object();
        private Action _closed;
        private bool _closeDelivered;
        private Exception _failure;
        public event Action Closed
        {
            add
            {
                bool notify; lock (_closeGate) { if (Volatile.Read(ref _disposed) != 0) return; notify = _closeDelivered; _closed += value; }
                if (notify) _dispatcher.PostControl(this, DeliverControl);
            }
            remove { lock (_closeGate) _closed -= value; }
        }
        public event Action<Exception> Faulted;
        public UnityNetRpcTransport(NetRpcTransport inner, ITransportLifetime life, UnityNetRpcDispatcher dispatcher)
        { _inner = inner; _life = life; _dispatcher = dispatcher; dispatcher.RegisterControl(this); inner.OnReceived += Receive; life.Closed += Lost; }
        public UniTask Send(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => EnqueueSend(bytes, false, token);
        public UniTask SendFast(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => EnqueueSend(bytes, true, token);
        private UniTask EnqueueSend(ReadOnlyMemory<byte> bytes, bool fast, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_sendGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _lost) != 0) throw new ObjectDisposedException(nameof(UnityNetRpcTransport));
                if (_outbound.Count >= 128 || bytes.Length > 4 * 1024 * 1024 - _outboundBytes) throw new InvalidOperationException("Unity NetRpc outbound queue exceeded bounds.");
                var owned = ArrayPool<byte>.Shared.Rent(Math.Max(1, bytes.Length)); bytes.Span.CopyTo(owned);
                _outbound.Enqueue((owned, bytes.Length, fast, token)); _outboundBytes += bytes.Length;
                if (!_sending) { _sending = true; OutboundCompletion = DrainSends().Preserve(); }
            }
            // Payload loan is over after the owned copy. Like a packet queue, completion is not a remote ACK.
            // Synchronous submission also keeps Runtime's sequential snapshot property reads on main thread.
            return default;
        }
        private async UniTask DrainSends()
        {
            await UniTask.SwitchToThreadPool();
            while (true)
            {
                (byte[] Bytes, int Length, bool Fast, CancellationToken Token) send;
                lock (_sendGate) { if (_outbound.Count == 0) { _sending = false; if (Volatile.Read(ref _disposed) != 0) _sendLifetime.Dispose(); return; } send = _outbound.Dequeue(); }
                try
                {
                    if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _lost) == 0 && !send.Token.IsCancellationRequested)
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(send.Token, _sendLifetime.Token);
                        if (send.Fast) await _inner.SendFast(send.Bytes.AsMemory(0, send.Length), linked.Token);
                        else await _inner.Send(send.Bytes.AsMemory(0, send.Length), linked.Token);
                    }
                }
                catch (OperationCanceledException) when (send.Token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _lost) != 0) { }
                catch (Exception error) { Fail(error); }
                finally { ArrayPool<byte>.Shared.Return(send.Bytes); lock (_sendGate) _outboundBytes -= send.Length; }
            }
        }
        private void Receive(ReadOnlyMemory<byte> bytes)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _lost) != 0 || _dispatcher.IsDisposed) return;
            if (bytes.Length > _dispatcher.MaximumBytes) { Fail(new InvalidOperationException("Unity NetRpc ingress queue exceeded bounds.")); return; }
            var owned = ArrayPool<byte>.Shared.Rent(Math.Max(1, bytes.Length)); bytes.Span.CopyTo(owned); int length = bytes.Length;
            if (!_dispatcher.Post(() =>
            {
                try { if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _lost) == 0 && !_dispatcher.IsDisposed) OnReceived?.Invoke(owned.AsMemory(0, length)); }
                catch (Exception error) { Faulted?.Invoke(error); }
                finally { ArrayPool<byte>.Shared.Return(owned); }
            }, length))
            {
                ArrayPool<byte>.Shared.Return(owned);
                Fail(new InvalidOperationException("Unity NetRpc ingress queue exceeded bounds."));
            }
        }
        private void Lost()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _lost, 1) != 0) return;
            lock (_sendGate) { if (Volatile.Read(ref _disposed) == 0) _sendLifetime.Cancel(); }
            _dispatcher.PostControl(this, DeliverControl);
        }
        private void Fail(Exception error)
        { if (Volatile.Read(ref _disposed) != 0) return; Interlocked.CompareExchange(ref _failure, error, null); Lost(); _dispatcher.PostControl(this, DeliverControl); }
        private void DeliverControl()
        {
            if (Volatile.Read(ref _disposed) != 0 || _dispatcher.IsDisposed) return;
            var error = Interlocked.Exchange(ref _failure, null);
            Action handlers; lock (_closeGate) { _closeDelivered = Volatile.Read(ref _lost) != 0; handlers = _closeDelivered ? _closed : null; if (_closeDelivered) _closed = null; }
            try { if (error != null) Faulted?.Invoke(error); } finally { handlers?.Invoke(); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _inner.OnReceived -= Receive; _life.Closed -= Lost;
            _dispatcher.UnregisterControl(this); lock (_closeGate) _closed = null;
            lock (_sendGate)
            {
                _sendLifetime.Cancel();
                while (_outbound.Count > 0) { var send = _outbound.Dequeue(); ArrayPool<byte>.Shared.Return(send.Bytes); _outboundBytes -= send.Length; }
                if (!_sending) _sendLifetime.Dispose();
            }
        }
    }
}
