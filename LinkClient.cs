using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Stellar.Maestro;

// Maestro Link transport — one game client WebSocket (protocol.md §2) on a BACKGROUND task. It never touches the game:
// everything it learns is posted to Inbox and drained by the plugin's Update tick on the main thread (Maestro's SDK
// 2.3.0 has no IFramework.Post — same lock-free-queue pattern as Plugin-ThirdParty-NuGet-Native-Dependencies.md).
//
// The single exception is `ping`: answered with `pong` right here on the socket thread, so the server's RTT sample
// doesn't include up to a frame of main-thread latency (and survives a hitch/loading screen).
//
// Reconnect policy: before the FIRST welcome a failed connect is fatal (the UI shows "Can't reach the server"); after a
// welcome, drops reconnect forever with backoff 1, 2, 5, 10, 10… s, re-sending the same hello (same clientId → the
// server gives back the same playerId). A server `error` frame is fatal (no reconnect).
internal sealed class LinkClient : IDisposable
{
    internal enum Kind { Opened, Message, Dropped, Fatal }
    internal readonly struct Event
    {
        public readonly Kind Kind; public readonly string Text;
        public Event(Kind kind, string text) { Kind = kind; Text = text; }
    }

    private static readonly int[] BackoffSec = { 1, 2, 5, 10 };

    private readonly Uri _uri;
    private readonly string _hello;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<string> _out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _welcomed;
    private volatile ClientWebSocket? _ws;

    public readonly ConcurrentQueue<Event> Inbox = new();
    public bool Connected => _ws?.State == WebSocketState.Open;

    public LinkClient(Uri uri, string helloJson) { _uri = uri; _hello = helloJson; }

    public void Start() => Task.Run(RunAsync);

    // Main thread → socket. Dropped silently while disconnected (status is re-sent every second anyway).
    public void Send(string json) { if (Connected) _out.Writer.TryWrite(json); }

    private async Task RunAsync()
    {
        int attempt = 0;
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            string? fatal = null;
            using (var ws = new ClientWebSocket())
            {
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                try
                {
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                        await ws.ConnectAsync(_uri, connectCts.Token).ConfigureAwait(false);
                    }
                    while (_out.Reader.TryRead(out _)) { }   // stale frames from the previous connection: hello must go first
                    _ws = ws;
                    await SendRawAsync(ws, _hello, ct).ConfigureAwait(false);
                    Inbox.Enqueue(new Event(Kind.Opened, ""));
                    var sender = Task.Run(() => SendLoopAsync(ws, ct));
                    fatal = await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
                    attempt = 0;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (!_welcomed) fatal = "unreachable";
                    else Inbox.Enqueue(new Event(Kind.Dropped, ex.Message));
                }
                catch { /* cancelled */ }
                finally { _ws = null; }
            }
            if (ct.IsCancellationRequested) break;
            if (fatal != null) { Inbox.Enqueue(new Event(Kind.Fatal, fatal)); break; }
            if (!_welcomed) { Inbox.Enqueue(new Event(Kind.Fatal, "unreachable")); break; }   // closed before welcome
            Inbox.Enqueue(new Event(Kind.Dropped, "closed"));
            int wait = BackoffSec[Math.Min(attempt, BackoffSec.Length - 1)];
            attempt++;
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false); } catch { break; }
        }
    }

    // Returns a fatal error code (server `error` frame) or null for a plain drop.
    private async Task<string?> ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var sb = new StringBuilder();
        string? fatal = null;
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            if (!r.EndOfMessage) continue;
            var text = sb.ToString();
            sb.Clear();

            string t = "";
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("t", out var tp)) t = tp.GetString() ?? "";
                if (t == "ping")
                {
                    // Echo wallMs verbatim (raw JSON number) — the server measures RTT against its own clock.
                    string wall = root.TryGetProperty("wallMs", out var w) ? w.GetRawText() : "0";
                    await SendRawAsync(ws, "{\"t\":\"pong\",\"wallMs\":" + wall + "}", ct).ConfigureAwait(false);
                }
                else if (t == "welcome") _welcomed = true;
                else if (t == "error")
                    fatal = root.TryGetProperty("code", out var c) ? (c.GetString() ?? "error") : "error";
            }
            catch (JsonException) { continue; }   // not JSON → ignore the frame
            Inbox.Enqueue(new Event(Kind.Message, text));
            if (fatal != null) break;
        }
        return fatal;
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (await _out.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                if (ws.State != WebSocketState.Open) return;   // the next connection's loop takes over the channel
                while (_out.Reader.TryRead(out var msg))
                    await SendRawAsync(ws, msg, ct).ConfigureAwait(false);
            }
        }
        catch { /* socket dropped / cancelled — the receive loop reports it */ }
    }

    private async Task SendRawAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);   // ClientWebSocket allows ONE outstanding send
        try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    // Never blocks the main thread: in the background, a best-effort close handshake (so the server sees a clean leave
    // rather than a drop), THEN cancel the loops. Close first — cancelling a pending ReceiveAsync aborts the socket.
    public void Dispose()
    {
        var ws = _ws;
        Task.Run(async () =>
        {
            try
            {
                if (ws != null && ws.State == WebSocketState.Open)
                {
                    using var c = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _sendLock.WaitAsync(c.Token).ConfigureAwait(false);   // no close while a send is in flight
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", c.Token).ConfigureAwait(false); }
                    finally { _sendLock.Release(); }
                }
            }
            catch { }
            try { _cts.Cancel(); } catch { }
        });
    }
}
