using Grpc.Core;
using Grpc.Net.Client;
using Vested.V1;
using VestedAI.ConnectorSdk.Errors;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Bidi gRPC client wrapping ConnectorHub.ConnectorHubClient.Connect.
/// Port of vested_connect/runtime/grpc_client.py and node/runtime/grpc-client.ts.
///
/// Usage:
///   await using var client = new GrpcClient(host, port, token, insecure);
///   await client.ConnectAsync();
///   await client.SendAsync(msg);
///   var reply = await client.ReceiveAsync(ct);
/// </summary>
internal sealed class GrpcClient : IAsyncDisposable
{
    private readonly string _address;
    private readonly string _token;
    private readonly bool _insecure;

    private GrpcChannel? _channel;
    private AsyncDuplexStreamingCall<ConnectorMsg, HubMsg>? _call;

    public GrpcClient(string host, int port, string token, bool insecure)
    {
        _address = $"{(insecure ? "http" : "https")}://{host}:{port}";
        _token = token;
        _insecure = insecure;
    }

    /// <summary>Opens the channel and starts the bidi stream.</summary>
    public void Connect()
    {
        var options = _insecure
            ? new GrpcChannelOptions
            {
                // Required for plain-text HTTP/2 without TLS
                Credentials = ChannelCredentials.Insecure,
                HttpHandler = new HttpClientHandler(),
            }
            : new GrpcChannelOptions();

        _channel = GrpcChannel.ForAddress(_address, options);
        var stub = new ConnectorHub.ConnectorHubClient(_channel);
        var metadata = new Metadata { { "x-connector-token", _token } };
        _call = stub.Connect(metadata);
    }

    /// <summary>Sends a ConnectorMsg to the hub.</summary>
    public Task SendAsync(ConnectorMsg msg)
    {
        if (_call is null) throw new ConnectorException("stream not opened");
        return _call.RequestStream.WriteAsync(msg);
    }

    /// <summary>
    /// Reads the next HubMsg from the hub.
    /// Throws <see cref="ConnectorException"/> when the stream is closed.
    /// Throws <see cref="TokenException"/> on UNAUTHENTICATED status.
    /// </summary>
    public async Task<HubMsg> ReceiveAsync(CancellationToken ct = default)
    {
        if (_call is null) throw new ConnectorException("stream not opened");
        try
        {
            bool hasNext = await _call.ResponseStream.MoveNext(ct).ConfigureAwait(false);
            if (!hasNext) throw new ConnectorException("stream closed by hub");
            return _call.ResponseStream.Current;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            throw new TokenException(ex.Status.Detail.Length > 0
                ? ex.Status.Detail
                : "unauthenticated", ex);
        }
        catch (RpcException ex)
        {
            throw new ConnectorException($"stream error: {ex.Status.Detail}", ex);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_call is not null)
        {
            try { _call.Dispose(); } catch { /* best effort */ }
            _call = null;
        }
        if (_channel is not null)
        {
            await _channel.ShutdownAsync().ConfigureAwait(false);
            _channel.Dispose();
            _channel = null;
        }
    }
}
