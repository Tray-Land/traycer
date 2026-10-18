using System.Buffers.Binary;
using System.Text;

namespace TrayCer.Core.Net.Mqtt;

public enum MqttPacketType : byte
{
    Connect = 1,
    ConnAck = 2,
    Publish = 3,
    PubAck = 4,
    Subscribe = 8,
    SubAck = 9,
    PingReq = 12,
    PingResp = 13,
    Disconnect = 14,
}

public readonly record struct MqttPacket(MqttPacketType Type, byte Flags, byte[] Body);

/// <summary>
/// Just enough MQTT 3.1.1 to read a Bambu Lab printer's reports: connect, subscribe, QoS 0
/// publish, ping and disconnect. A full client library would run in the always-on process for
/// the sake of five packet types.
/// </summary>
public static class MqttCodec
{
    public const byte ConnAckAccepted = 0;
    public const byte ConnAckBadCredentials = 4;
    public const byte ConnAckNotAuthorized = 5;

    public static byte[] Connect(string clientId, string? username, string? password, ushort keepAliveSeconds)
    {
        List<byte> body = [];
        AppendString(body, "MQTT");
        body.Add(4); // protocol level 3.1.1
        byte flags = 0x02; // clean session
        if (username is not null)
        {
            flags |= 0x80;
        }

        if (password is not null)
        {
            flags |= 0x40;
        }

        body.Add(flags);
        body.Add((byte)(keepAliveSeconds >> 8));
        body.Add((byte)keepAliveSeconds);
        AppendString(body, clientId);
        if (username is not null)
        {
            AppendString(body, username);
        }

        if (password is not null)
        {
            AppendString(body, password);
        }

        return Frame(MqttPacketType.Connect, 0, body);
    }

    public static byte[] Subscribe(ushort packetId, string topic)
    {
        List<byte> body = [(byte)(packetId >> 8), (byte)packetId];
        AppendString(body, topic);
        body.Add(0); // QoS 0
        return Frame(MqttPacketType.Subscribe, 0x02, body);
    }

    public static byte[] Publish(string topic, ReadOnlySpan<byte> payload)
    {
        List<byte> body = [];
        AppendString(body, topic);
        body.AddRange(payload);
        return Frame(MqttPacketType.Publish, 0, body);
    }

    public static byte[] PingReq() => [(byte)MqttPacketType.PingReq << 4, 0];

    public static byte[] Disconnect() => [(byte)MqttPacketType.Disconnect << 4, 0];

    /// <summary>Topic and payload of a PUBLISH, skipping the packet id that QoS 1 and 2 add.</summary>
    public static (string Topic, ReadOnlyMemory<byte> Payload) ParsePublish(MqttPacket packet)
    {
        byte[] body = packet.Body;
        if (body.Length < 2)
        {
            throw new InvalidDataException("PUBLISH too short.");
        }

        int topicLength = BinaryPrimitives.ReadUInt16BigEndian(body);
        int offset = 2 + topicLength;
        if (offset > body.Length)
        {
            throw new InvalidDataException("PUBLISH topic overruns the packet.");
        }

        string topic = Encoding.UTF8.GetString(body, 2, topicLength);
        int qos = (packet.Flags >> 1) & 0x03;
        if (qos > 0)
        {
            offset += 2;
        }

        return offset > body.Length ? (topic, ReadOnlyMemory<byte>.Empty) : (topic, body.AsMemory(offset));
    }

    /// <summary>Packet id of a QoS 1 PUBLISH, which the receiver must PUBACK.</summary>
    public static ushort? QoS1PacketId(MqttPacket packet)
    {
        if (((packet.Flags >> 1) & 0x03) != 1 || packet.Body.Length < 2)
        {
            return null;
        }

        int topicLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Body);
        return packet.Body.Length >= topicLength + 4 ? BinaryPrimitives.ReadUInt16BigEndian(packet.Body.AsSpan(2 + topicLength)) : null;
    }

    public static byte[] PubAck(ushort packetId) => [(byte)MqttPacketType.PubAck << 4, 2, (byte)(packetId >> 8), (byte)packetId];

    public static async Task<MqttPacket> ReadAsync(Stream stream, int maxBodyBytes, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
        byte header = one[0];

        int length = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (shift > 21)
            {
                throw new InvalidDataException("Malformed MQTT remaining length.");
            }

            await stream.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0)
            {
                break;
            }
        }

        if (length > maxBodyBytes)
        {
            throw new InvalidDataException($"MQTT packet of {length} bytes is larger than allowed.");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return new MqttPacket((MqttPacketType)(header >> 4), (byte)(header & 0x0F), body);
    }

    internal static byte[] Frame(MqttPacketType type, byte flags, List<byte> body)
    {
        List<byte> packet = new(body.Count + 5) { (byte)(((byte)type << 4) | flags) };
        int remaining = body.Count;
        do
        {
            byte digit = (byte)(remaining % 128);
            remaining /= 128;
            if (remaining > 0)
            {
                digit |= 0x80;
            }

            packet.Add(digit);
        }
        while (remaining > 0);

        packet.AddRange(body);
        return [.. packet];
    }

    private static void AppendString(List<byte> body, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        body.Add((byte)(bytes.Length >> 8));
        body.Add((byte)bytes.Length);
        body.AddRange(bytes);
    }
}
