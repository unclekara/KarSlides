using System.Net;
using System.Net.Sockets;
using System.Text;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Vmix;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

public class VmixProtocolTests
{
    internal const string Xml = """
        <vmix><version>29.0.0.49</version><edition>Trial</edition><inputs>
        <input key="a1" number="1" type="Blank" title="Blank" state="Paused">Blank</input>
        <input key="b2" number="2" type="NDI" title="NDI STUDIO-PC (KarSlides - Slot 1)">NDI STUDIO-PC (KarSlides - Slot 1)</input>
        <input key="c3" number="3" type="NDI" title="NDI STUDIO-PC (KarSlides - Slot 10)">x</input>
        </inputs></vmix>
        """;

    [Theory]
    [InlineData("TALLY OK 0121", "0121")]
    [InlineData("TALLY OK 0", "0")]
    [InlineData("SUBSCRIBE OK TALLY", null)]
    [InlineData("TALLY OK 01x", null)]
    [InlineData("ACTS OK Input 1 1", null)]
    public void ParseTally(string line, string? expected) => Assert.Equal(expected, VmixProtocol.ParseTally(line));

    [Fact]
    public void TallyOf_MapsDigitsToProgramAndPreview()
    {
        Assert.Equal(new Tally(true, false), VmixProtocol.TallyOf("0121", 2));
        Assert.Equal(new Tally(false, true), VmixProtocol.TallyOf("0121", 3));
        Assert.Equal(Tally.None, VmixProtocol.TallyOf("0121", 1));
        Assert.Equal(Tally.None, VmixProtocol.TallyOf("0121", 9));
    }

    [Fact]
    public void ParseState_ReadsVersionAndInputs()
    {
        var s = VmixProtocol.ParseState(Xml);
        Assert.Equal("29.0.0.49", s.Version);
        Assert.Equal(3, s.Inputs.Count);
        Assert.Equal(("NDI", 2), (s.Inputs[1].Type, s.Inputs[1].Number));
    }

    [Theory]
    [InlineData("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\"><html><head><title>301 Moved Permanently</title></head></html>")]
    [InlineData("<html><body>Apache</body></html>")]
    [InlineData("not xml at all")]
    public void ParseState_RejectsSomethingElse(string answer) =>
        Assert.Throws<FormatException>(() => VmixProtocol.ParseState(answer));

    [Fact]
    public void FindNdiInput_MatchesExactSourceName_NotPrefix()
    {
        var s = VmixProtocol.ParseState(Xml);
        Assert.Equal(2, VmixProtocol.FindNdiInput(s, "KarSlides - Slot 1")?.Number);
        Assert.Equal(3, VmixProtocol.FindNdiInput(s, "KarSlides - Slot 10")?.Number);
        Assert.Null(VmixProtocol.FindNdiInput(s, "KarSlides - Slot 2"));
    }
}

public sealed class VmixClientTests
{
    private sealed class StubHttp(string body) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task SubscribesTally_AndPushesItToTheMappedSlot()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<Tally>();
        var gotProgram = new TaskCompletionSource();
        var http = new StubHttp(VmixProtocolTests.Xml);
        var client = new VmixClient(new VmixOptions { Host = "127.0.0.1", TcpPort = port, HttpPort = 8188 },
            new HttpClient(http), NullLogger<VmixClient>.Instance);
        client.AddSlot(1, "KarSlides - Slot 1", t =>
        {
            lock (received) received.Add(t);
            if (t.OnProgram) gotProgram.TrySetResult();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = client.RunAsync(cts.Token);

        using var server = await listener.AcceptTcpClientAsync(cts.Token);
        var stream = server.GetStream();
        var buf = new byte[256];
        var n = await stream.ReadAsync(buf, cts.Token);
        Assert.Contains("SUBSCRIBE TALLY", Encoding.UTF8.GetString(buf, 0, n));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("SUBSCRIBE OK TALLY\r\nTALLY OK 02\r\n"), cts.Token);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("TALLY OK 21\r\n"), cts.Token);

        await gotProgram.Task.WaitAsync(cts.Token);
        lock (received) Assert.Equal(new Tally(false, true), received[0]); // input 2 on preview first

        var status = client.Status;
        Assert.True(status.Connected);
        Assert.Equal("29.0.0.49", status.Version);
        Assert.Equal(2, status.SlotInputs[1]);
        Assert.StartsWith("http://127.0.0.1:8188/api", http.Urls[0]);

        cts.Cancel();
        await run;
        listener.Stop();
    }

    [Fact]
    public async Task WithoutVmix_ReportsDisconnected_AndClearsTally()
    {
        var received = new List<Tally>();
        var client = new VmixClient(new VmixOptions { Host = "127.0.0.1", TcpPort = 1, HttpPort = 1 },
            new HttpClient(), NullLogger<VmixClient>.Instance);
        client.AddSlot(1, "KarSlides - Slot 1", received.Add);

        // A refused connection to a closed port takes ~2 s on Windows: leave time for one full attempt.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.RunAsync(cts.Token);

        Assert.False(client.Status.Connected);
        Assert.Contains("not reachable", client.Status.Error);
        Assert.All(received, t => Assert.Equal(Tally.None, t));
    }

    private sealed class PortStub : HttpMessageHandler
    {
        public Dictionary<int, string> Answers { get; } = new();
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Urls.Add(r.RequestUri!.ToString());
            return Answers.TryGetValue(r.RequestUri!.Port, out var body)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })
                : throw new HttpRequestException("connection refused");
        }
    }

    [Fact]
    public async Task AutoPort_FindsTheVmixWebControllerAmongVmixPorts()
    {
        var stub = new PortStub();
        stub.Answers[8088] = "<html>Apache from WSL</html>";
        stub.Answers[8188] = VmixProtocolTests.Xml;
        var client = new VmixClient(new VmixOptions { HttpPort = 0 }, new HttpClient(stub), NullLogger<VmixClient>.Instance,
            () => [8088, 8188, 8200]);
        client.AddSlot(1, "KarSlides - Slot 1", _ => { });

        await client.RefreshInputsAsync(default);

        Assert.Equal(8188, client.Status.HttpPort);
        Assert.Equal(2, client.Status.SlotInputs[1]);

        // Found once, then reused: no probing of other ports on the next refresh.
        stub.Urls.Clear();
        await client.RefreshInputsAsync(default);
        Assert.All(stub.Urls, u => Assert.Contains(":8188/", u));
    }

    [Fact]
    public async Task AutoPort_VmixNotRunning_IsReportedClearly()
    {
        var client = new VmixClient(new VmixOptions { HttpPort = 0 }, new HttpClient(new PortStub()),
            NullLogger<VmixClient>.Instance, () => []);
        var ex = await Assert.ThrowsAsync<IOException>(() => client.RefreshInputsAsync(default));
        Assert.Contains("not running", ex.Message);
    }

    [Fact]
    public async Task AutoPort_NoWebController_IsReportedClearly()
    {
        var client = new VmixClient(new VmixOptions { HttpPort = 0 }, new HttpClient(new PortStub()),
            NullLogger<VmixClient>.Instance, () => [8200, 8300]);
        var ex = await Assert.ThrowsAsync<IOException>(() => client.RefreshInputsAsync(default));
        Assert.Contains("Web Controller", ex.Message);
    }

    [Fact]
    public async Task ExplicitPort_IsUsedWithoutDiscovery()
    {
        var stub = new PortStub();
        stub.Answers[9999] = VmixProtocolTests.Xml;
        var client = new VmixClient(new VmixOptions { HttpPort = 9999 }, new HttpClient(stub), NullLogger<VmixClient>.Instance,
            () => throw new InvalidOperationException("must not discover"));
        await client.RefreshInputsAsync(default);
        Assert.Equal(9999, client.Status.HttpPort);
    }

    [Fact]
    public async Task SomethingElseOnThePort_IsReportedClearly()
    {
        var client = new VmixClient(new VmixOptions { Host = "127.0.0.1", HttpPort = 8088 },
            new HttpClient(new StubHttp("<html><body>Apache</body></html>")), NullLogger<VmixClient>.Instance);

        var ex = await Assert.ThrowsAsync<FormatException>(() => client.RefreshInputsAsync(default));
        Assert.Contains("something other than vMix", ex.Message);
    }
}
