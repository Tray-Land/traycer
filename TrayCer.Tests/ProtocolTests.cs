using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TrayCer.Core.Discovery;
using TrayCer.Core.Json;
using TrayCer.Core.Models;
using TrayCer.Core.Net;
using TrayCer.Core.Net.Mqtt;

namespace TrayCer.Tests;

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    public void Mqtt_ConnectPacketMatchesTheSpec()
    {
        byte[] packet = MqttCodec.Connect("c", "bblp", "1234", 60);

        // Fixed header, remaining length, "MQTT", level 4, flags user+password+clean, keepalive 60.
        byte[] expectedStart = [0x10, (byte)(packet.Length - 2), 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0xC2, 0, 60];
        CollectionAssert.AreEqual(expectedStart, packet[..12]);
        StringAssert.EndsWith(Encoding.ASCII.GetString(packet), "bblp\0\u00041234");
    }

    [TestMethod]
    public void Mqtt_RemainingLengthUsesVarint()
    {
        byte[] packet = MqttCodec.Frame(MqttPacketType.Publish, 0, [.. new byte[200]]);
        Assert.AreEqual(0xC8, packet[1], "200 = 0x48 with the continuation bit...");
        Assert.AreEqual(0x01, packet[2], "...then 1.");
        Assert.HasCount(203, packet);
    }

    [TestMethod]
    public async Task Mqtt_PublishRoundTripsThroughTheReader()
    {
        byte[] wire = MqttCodec.Publish("device/X/report", """{"print":{}}"""u8);
        using MemoryStream stream = new(wire);

        MqttPacket packet = await MqttCodec.ReadAsync(stream, 1024, CancellationToken.None);
        (string topic, ReadOnlyMemory<byte> payload) = MqttCodec.ParsePublish(packet);

        Assert.AreEqual(MqttPacketType.Publish, packet.Type);
        Assert.AreEqual("device/X/report", topic);
        Assert.AreEqual("""{"print":{}}""", Encoding.UTF8.GetString(payload.Span));
    }

    [TestMethod]
    public void Mqtt_QoS1PublishSkipsThePacketId()
    {
        byte[] body = [0, 1, (byte)'t', 0, 7, (byte)'h', (byte)'i'];
        MqttPacket packet = new(MqttPacketType.Publish, 0b0010, body);

        Assert.AreEqual((ushort)7, MqttCodec.QoS1PacketId(packet));
        Assert.AreEqual("hi", Encoding.ASCII.GetString(MqttCodec.ParsePublish(packet).Payload.Span));
    }

    [TestMethod]
    [DataRow("192.168.1.40", 7125, "http", "192.168.1.40", 7125, "/")]
    [DataRow("voron.local:80", 7125, "http", "voron.local", 80, "/")]
    [DataRow("http://octopi.local/octoprint", 80, "http", "octopi.local", 80, "/octoprint/")]
    [DataRow("https://printer.lan", 80, "https", "printer.lan", 443, "/")]
    [DataRow("[fe80::1]:8080", 80, "http", "fe80::1", 8080, "/")]
    public void Endpoint_ParsesWhatUsersType(string input, int defaultPort, string scheme, string host, int port, string path)
    {
        PrinterEndpoint endpoint = PrinterEndpoint.Parse(input, defaultPort);
        Assert.AreEqual(scheme, endpoint.Scheme);
        Assert.AreEqual(host, endpoint.Host);
        Assert.AreEqual(port, endpoint.Port);
        Assert.AreEqual(path, endpoint.BasePath);
    }

    [TestMethod]
    public void Endpoint_RejectsGarbage()
    {
        Assert.IsFalse(PrinterEndpoint.TryParse("", 80, out _));
        Assert.IsFalse(PrinterEndpoint.TryParse("ftp://x", 80, out _));
        Assert.IsFalse(PrinterEndpoint.TryParse("http://", 80, out _));
    }

    [TestMethod]
    public void Endpoint_ResolvesUnderTheBasePath()
    {
        PrinterEndpoint endpoint = PrinterEndpoint.Parse("http://host/octoprint/", 80);
        Assert.AreEqual("http://host/octoprint/api/job", endpoint.Resolve("/api/job").AbsoluteUri);
        PrinterEndpoint web = PrinterEndpoint.Parse("voron.local", 7125).WithPort(80);
        Assert.AreEqual("http://voron.local/", web.BaseUri.AbsoluteUri);
    }

    [TestMethod]
    [DataRow("10.0.0.5", true)]
    [DataRow("172.20.1.1", true)]
    [DataRow("192.168.0.9", true)]
    [DataRow("100.101.1.2", true)]
    [DataRow("8.8.8.8", false)]
    [DataRow("printer.local", true)]
    [DataRow("octopi", true)]
    [DataRow("example.com", false)]
    public void Http_AcceptsSelfSignedOnlyOnTheLan(string host, bool local) => Assert.AreEqual(local, PrinterHttp.IsLocalHost(host));

    [TestMethod]
    public void Json_ReadsNumbersWhicheverWayTheyArrive()
    {
        JsonNode node = JsonNode.Parse("""{"a":42,"b":"42.50","c":"45%","d":true,"e":"n/a","f":null,"g":[1,{"h":2}]}""")!;
        Assert.AreEqual(42, node.Number("a"));
        Assert.AreEqual(42.5, node.Number("b"));
        Assert.AreEqual(45, node.Number("c"));
        Assert.AreEqual(1, node.Number("d"));
        Assert.IsNull(node.Number("e"));
        Assert.IsNull(node.Number("f"));
        Assert.AreEqual(2, node.Int("g", "1", "h"));
        Assert.AreEqual("42", node.Text("a"));
        Assert.IsNull(node.NonEmpty("missing"));
    }

    [TestMethod]
    public void Json_MergeKeepsFieldsTheUpdateLeavesOut()
    {
        JsonObject target = JsonNode.Parse("""{"a":1,"o":{"x":1,"y":2},"list":[1,2]}""")!.AsObject();
        JsonRead.MergeInto(target, JsonNode.Parse("""{"o":{"y":3},"list":[9],"b":2}""")!.AsObject());

        Assert.AreEqual("""{"a":1,"o":{"x":1,"y":3},"list":[9],"b":2}""", target.ToJsonString());
    }

    [TestMethod]
    public void Json_FileNameDropsFolders()
    {
        Assert.AreEqual("a.gcode", JsonRead.FileName("/usr/data/gcodes/a.gcode"));
        Assert.AreEqual("b.gcode", JsonRead.FileName(@"0:\gcodes\b.gcode"));
        Assert.IsNull(JsonRead.FileName("  "));
    }

    [TestMethod]
    public void Discovery_ParsesBambuNotify()
    {
        const string notify = "NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1990\r\nServer: UPnP/1.0\r\nLocation: 192.168.1.77\r\n"
            + "NT: urn:bambulab-com:device:3dprinter:1\r\nUSN: 01P00A391800123\r\nCache-Control: max-age=1800\r\n"
            + "DevModel.bambu.com: C12\r\nDevName.bambu.com: Garage P1S\r\nDevSignal.bambu.com: -52\r\nDevConnect.bambu.com: lan\r\n\r\n";

        DiscoveredPrinter printer = BambuDiscovery.ParseNotify(notify, "192.168.1.1")!;

        Assert.AreEqual(PrinterKind.BambuLab, printer.Kind);
        Assert.AreEqual("192.168.1.77", printer.Host);
        Assert.AreEqual("01P00A391800123", printer.Serial);
        Assert.AreEqual("P1S", printer.Model);
        Assert.AreEqual("Garage P1S", printer.Name);
        Assert.IsNull(BambuDiscovery.ParseNotify("NOTIFY * HTTP/1.1\r\nUSN: x\r\n", "1.2.3.4"), "Other SSDP devices are ignored.");
    }

    [TestMethod]
    public void Discovery_ParsesSdcpReply()
    {
        byte[] reply = Encoding.UTF8.GetBytes("""
            {"Id":"979d4C788A4a78bC777A870F1A02867A","Data":{"Name":"Centauri Carbon","MachineName":"Elegoo Centauri Carbon","BrandName":"ELEGOO",
             "MainboardIP":"192.168.1.88","MainboardID":"8e3e4f0d0a090f0a","ProtocolVersion":"V3.0.0","FirmwareVersion":"V1.1.29"}}
            """);

        SdcpDevice device = SdcpDiscovery.ParseReply(reply, "192.168.1.1")!;

        Assert.AreEqual("192.168.1.88", device.Host);
        Assert.AreEqual("8e3e4f0d0a090f0a", device.MainboardId);
        Assert.AreEqual("Elegoo Centauri Carbon", device.MachineName);
        Assert.IsNull(SdcpDiscovery.ParseReply("pong"u8, "x"));
    }

    [TestMethod]
    public void Discovery_ParsesFlashForgeReplies()
    {
        byte[] modern = new byte[FlashForgeDiscovery.ModernReplySize];
        Encoding.ASCII.GetBytes("Shop 5M").CopyTo(modern, 0);
        modern[0x88] = 0x00;
        modern[0x89] = 0x24;
        Encoding.ASCII.GetBytes("SNMOMC9900123").CopyTo(modern, 0x92);

        DiscoveredPrinter printer = FlashForgeDiscovery.ParseReply(modern, "192.168.1.61")!;
        Assert.AreEqual(PrinterKind.FlashForge, printer.Kind);
        Assert.AreEqual("Shop 5M", printer.Name);
        Assert.AreEqual("SNMOMC9900123", printer.Serial);
        Assert.AreEqual("Adventurer 5M Pro", printer.Model);

        byte[] legacy = new byte[FlashForgeDiscovery.LegacyReplySize];
        Encoding.ASCII.GetBytes("Finder").CopyTo(legacy, 0);
        Assert.AreEqual(PrinterKind.FlashForgeLegacy, FlashForgeDiscovery.ParseReply(legacy, "10.0.0.2")!.Kind);
        Assert.IsNull(FlashForgeDiscovery.ParseReply(new byte[12], "10.0.0.3"));
    }

    [TestMethod]
    public void Lan_BroadcastAddressFromPrefix()
    {
        Assert.AreEqual(IPAddress.Parse("192.168.1.255"), LanAddress.Broadcast(IPAddress.Parse("192.168.1.37"), 24));
        Assert.AreEqual(IPAddress.Parse("10.0.63.255"), LanAddress.Broadcast(IPAddress.Parse("10.0.12.1"), 18));
    }
}
