using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using VestedAI.ConnectorSdk.Runtime;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

/// <summary>
/// Channel liveness options — the 2026-11-24 failure class.
///
/// A connector whose hub stream dies silently (graceful FIN, half-open socket
/// after a relay restart, WireGuard rekey, network partition) sits
/// "unavailable" until its pod is restarted unless the transport detects the
/// dead peer. HTTP/2 keepalive PINGs are that detector: on a dead socket the
/// PING times out, the stream errors, and the supervisor redials. These tests
/// pin the PING cadence on the channel options both transports build.
///
/// (PHP/Swoole had no transport-level equivalent and enforces liveness at the
/// application layer instead; see the GrpcClient::recv() docblock there.)
/// </summary>
public class GrpcClientKeepAliveTests
{
    private static readonly TimeSpan PingDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void InsecureOptions_CarryKeepAlivePings()
    {
        var options = GrpcClient.BuildChannelOptions(insecure: true);

        Assert.Equal(ChannelCredentials.Insecure, options.Credentials);
        var handler = Assert.IsType<SocketsHttpHandler>(options.HttpHandler);
        Assert.Equal(PingDelay, handler.KeepAlivePingDelay);
        Assert.Equal(PingTimeout, handler.KeepAlivePingTimeout);
    }

    [Fact]
    public void SecureOptions_CarryKeepAlivePings()
    {
        var options = GrpcClient.BuildChannelOptions(insecure: false);

        Assert.Null(options.Credentials);
        var handler = Assert.IsType<SocketsHttpHandler>(options.HttpHandler);
        Assert.Equal(PingDelay, handler.KeepAlivePingDelay);
        Assert.Equal(PingTimeout, handler.KeepAlivePingTimeout);
    }
}
