using System.Net;
using System.Net.Sockets;
using System.Text;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class HttpTransportTests
{
    [Fact]
    public async Task RealHttpRequestUsesConfiguredOriginAndValidatesResponse()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var served = ServeAsync();
        using var api = new NpepApi(origin);
        var response = await api.SendAsync("info", "infoResponse", token: deadline.Token);
        Assert.True(NpepProtocol.Equal(Fixtures.Value("infoResponse")["data"], response));
        await served;

        async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            Assert.Equal("GET /api/v2/npep/info HTTP/1.1", await reader.ReadLineAsync(deadline.Token));
            string? id = null;
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line)
                if (line.StartsWith("X-Request-Id:", StringComparison.OrdinalIgnoreCase)) id = line[(line.IndexOf(':') + 1)..].Trim();
            Assert.NotNull(id);
            var body = Fixtures.Value("infoResponse");
            body["requestId"] = id;
            byte[] payload = Encoding.UTF8.GetBytes(body.ToJsonString());
            byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, deadline.Token);
            await stream.WriteAsync(payload, deadline.Token);
        }
    }
}
