using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ShardForge.Server.Auth;
using ShardForge.Server.Maps;

namespace ShardForge.Server.Live;

/// <summary>
/// One room per map. Everyone in a room sees who else is there and where they stand, and gets every
/// statics change the moment it is saved. Clients send only their position; edits go through the HTTP API.
/// </summary>
public sealed class LiveHub(ILogger<LiveHub> log)
{
    private static readonly string[] Palette = ["#e07a5f", "#81b29a", "#f2cc8f", "#7aa2d6", "#c08bd6", "#6cc4c4", "#d6a36c", "#9bd66c"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class Peer(string cid, User user, string color, WebSocket socket)
    {
        public string Cid { get; } = cid;
        public User User { get; } = user;
        public string Color { get; } = color;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int X, Y, Z, Dir;
        public object Public => new { cid = Cid, name = User.Username, role = User.Role.ToString(), color = Color, x = X, y = Y, z = Z, dir = Dir };
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Peer>> _rooms = new();

    public int Count(string mapId) => _rooms.TryGetValue(mapId, out var r) ? r.Count : 0;

    public async Task Run(string mapId, User user, WebSocket socket, CancellationToken ct)
    {
        var room = _rooms.GetOrAdd(mapId, _ => new());
        string cid = Guid.NewGuid().ToString("N")[..10];
        string color = Palette[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(user.Username) % Palette.Length)];
        var me = new Peer(cid, user, color, socket);
        room[cid] = me;
        log.LogInformation("{User} joined {Map} ({Count} here)", user.Username, mapId, room.Count);
        try
        {
            await Send(me, new { type = "welcome", you = me.Public, peers = room.Values.Where(p => p != me).Select(p => p.Public) });
            await Broadcast(room, new { type = "join", peer = me.Public }, except: me);

            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text) continue; // ignore oversized or binary input
                Handle(room, me, buffer.AsSpan(0, result.Count));
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        finally
        {
            room.TryRemove(cid, out _);
            if (room.IsEmpty) _rooms.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, Peer>>(mapId, room));
            await Broadcast(room, new { type = "leave", cid });
            // Say goodbye without waiting long for the browser's answer (a stop must not hang on a sleeping tab).
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try { using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "server stopping", t.Token); }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            log.LogInformation("{User} left {Map}", user.Username, mapId);
        }
    }

    private void Handle(ConcurrentDictionary<string, Peer> room, Peer me, ReadOnlySpan<byte> message)
    {
        try
        {
            var doc = JsonDocument.Parse(message.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return;
            if (type.GetString() == "pos")
            {
                me.X = Clamp(root, "x", 0, 65535); me.Y = Clamp(root, "y", 0, 65535);
                me.Z = Clamp(root, "z", -128, 127); me.Dir = Clamp(root, "dir", 0, 7);
                _ = Broadcast(room, new { type = "pos", cid = me.Cid, x = me.X, y = me.Y, z = me.Z, dir = me.Dir }, except: me);
            }
        }
        catch (JsonException) { /* ignore bad input */ }
    }

    private static int Clamp(JsonElement root, string name, int min, int max) =>
        root.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? Math.Clamp(i, min, max) : 0;

    /// <summary>Tells everyone on the map about saved statics (including the saver's other tabs).</summary>
    public Task PublishBlocks(BlockChange change) =>
        _rooms.TryGetValue(change.MapId, out var room)
            ? Broadcast(room, new
            {
                type = "blocks", rev = change.Rev, by = change.Username, userId = change.UserId, cid = change.Cid,
                blocks = change.Blocks.ToDictionary(b => b.Block.ToString(), b => Convert.ToBase64String(b.Data)),
                land = change.Land.ToDictionary(b => b.Block.ToString(), b => Convert.ToBase64String(b.Data)),
            })
            : Task.CompletedTask;

    /// <summary>Tells everyone the map's base files changed (rebuilt): they should reload.</summary>
    public Task PublishRebuilt(string mapId, string by) =>
        _rooms.TryGetValue(mapId, out var room) ? Broadcast(room, new { type = "rebuilt", by }) : Task.CompletedTask;

    /// <summary>Server items changed on a map.</summary>
    public Task PublishItems(string mapId, string by, string? cid, IReadOnlyList<System.Text.Json.Nodes.JsonObject> put, IReadOnlyList<string> remove) =>
        _rooms.TryGetValue(mapId, out var room) ? Broadcast(room, new { type = "items", by, cid, put, remove }) : Task.CompletedTask;

    /// <summary>A backup was restored: everyone should reload what they see.</summary>
    public Task PublishRestored(string by, string summary) =>
        Task.WhenAll(_rooms.Values.Select(room => Broadcast(room, new { type = "restored", by, summary })));

    /// <summary>Tells everyone on every map that the house library changed.</summary>
    public Task PublishPrefabs(string by, string action, long id, string name) =>
        Task.WhenAll(_rooms.Values.Select(room => Broadcast(room, new { type = "prefabs", by, action, id, name })));

    private static async Task Broadcast(ConcurrentDictionary<string, Peer> room, object message, Peer? except = null)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await Task.WhenAll(room.Values.Where(p => p != except).Select(p => SendRaw(p, bytes)));
    }

    private static Task Send(Peer p, object message) => SendRaw(p, JsonSerializer.SerializeToUtf8Bytes(message, Json));

    private static async Task SendRaw(Peer p, byte[] bytes)
    {
        if (p.Socket.State != WebSocketState.Open) return;
        await p.SendLock.WaitAsync();
        try { await p.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
        finally { p.SendLock.Release(); }
    }
}
