using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LolRemote.Companion;

public sealed class MdnsResponder : IDisposable
{
    public const string HostName = "lolremote.local";

    static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    const int MdnsPort = 5353;

    readonly Func<IPAddress?> _address;
    readonly List<Socket> _sockets = new();

    public bool Running { get; private set; }

    public MdnsResponder(Func<IPAddress?> address)
    {
        _address = address;
    }

    public void Start(CancellationToken ct)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            var local = _address();
            var option = local is null ? new MulticastOption(Group) : new MulticastOption(Group, local);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            if (local is not null)
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            _sockets.Add(socket);
            Running = true;
            _ = Task.Run(() => ListenAsync(socket, ct));
        }
        catch (Exception)
        {
            Running = false;
        }
    }

    async Task ListenAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, ct);
                var reply = BuildReply(buffer.AsSpan(0, received.ReceivedBytes));
                if (reply is null) continue;
                await socket.SendToAsync(reply, SocketFlags.None, new IPEndPoint(Group, MdnsPort), ct);
                if (received.RemoteEndPoint is IPEndPoint sender && sender.Port != MdnsPort)
                    await socket.SendToAsync(reply, SocketFlags.None, sender, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(200, CancellationToken.None);
            }
        }
    }

    byte[]? BuildReply(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12) return null;
        var flags = (packet[2] << 8) | packet[3];
        if ((flags & 0x8000) != 0) return null;
        var questions = (packet[4] << 8) | packet[5];
        var offset = 12;
        var wanted = false;
        for (var i = 0; i < questions; i++)
        {
            var name = ReadName(packet, ref offset);
            if (name is null || offset + 4 > packet.Length) return null;
            var type = (packet[offset] << 8) | packet[offset + 1];
            offset += 4;
            if (string.Equals(name, HostName, StringComparison.OrdinalIgnoreCase) && (type == 1 || type == 255)) wanted = true;
        }
        if (!wanted) return null;

        var address = _address();
        if (address is null) return null;

        var reply = new List<byte>
        {
            packet[0], packet[1], 0x84, 0x00,
            0, 0, 0, 1, 0, 0, 0, 0
        };
        foreach (var label in HostName.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            reply.Add((byte)bytes.Length);
            reply.AddRange(bytes);
        }
        reply.Add(0);
        reply.AddRange(new byte[] { 0, 1, 0x80, 1, 0, 0, 0, 120, 0, 4 });
        reply.AddRange(address.GetAddressBytes());
        return reply.ToArray();
    }

    static string? ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var guard = 0; guard < 64; guard++)
        {
            if (position >= packet.Length) return null;
            var length = packet[position];
            if (length == 0)
            {
                if (!jumped) offset = position + 1;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length) return null;
                var pointer = ((length & 0x3F) << 8) | packet[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            if (position + 1 + length > packet.Length) return null;
            labels.Add(Encoding.ASCII.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var socket in _sockets)
        {
            try { socket.Dispose(); } catch (Exception) { }
        }
    }
}
