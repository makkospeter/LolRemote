using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace LolRemote.MockLcu;

public class WebSocketHub
{
    readonly List<Channel<string>> _clients = new();
    readonly object _lock = new();

    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<string>();
        lock (_lock) _clients.Add(channel);
        var sendTask = SendLoopAsync(socket, channel.Reader, ct);
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            lock (_lock) _clients.Remove(channel);
            channel.Writer.TryComplete();
            await sendTask;
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch (Exception) { }
            }
        }
    }

    static async Task SendLoopAsync(WebSocket socket, ChannelReader<string> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(ct))
            {
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception)
        {
        }
    }

    public void Publish(string uri, string eventType, object? data)
    {
        var payload = JsonSerializer.Serialize(
            new object[] { 8, "OnJsonApiEvent", new { data, eventType, uri } },
            MockEngine.JsonOptions);
        lock (_lock)
        {
            foreach (var client in _clients) client.Writer.TryWrite(payload);
        }
    }
}
