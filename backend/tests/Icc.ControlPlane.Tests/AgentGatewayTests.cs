using Grpc.Core;
using Grpc.Net.Client;
using Icc.Contracts.Agent.V1;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Icc.ControlPlane.Tests;

public class AgentGatewayTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Connect_HelloFirst_ReturnsHelloAckAndAcks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var channel = CreateChannel();
        var client = new AgentService.AgentServiceClient(channel);

        using var call = client.Connect(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new ConnectRequest
        {
            Seq = 1,
            Hello = new Hello { AgentId = "test-agent", Host = new HostInfo { Hostname = "node1" } },
        }, ct);
        await call.RequestStream.WriteAsync(new ConnectRequest { Seq = 2, Heartbeat = new Heartbeat() }, ct);
        await call.RequestStream.CompleteAsync();

        var responses = await call.ResponseStream.ReadAllAsync(ct).ToListAsync(ct);

        Assert.Equal(ConnectResponse.PayloadOneofCase.HelloAck, responses[0].PayloadCase);
        Assert.True(responses[0].HelloAck.Config.HeartbeatIntervalSeconds > 0);
        Assert.Equal([1UL, 2UL], responses.Skip(1).Select(r => r.Ack.Seq));
    }

    [Fact]
    public async Task Connect_WithoutHello_FailsPrecondition()
    {
        var ct = TestContext.Current.CancellationToken;
        using var channel = CreateChannel();
        var client = new AgentService.AgentServiceClient(channel);

        using var call = client.Connect(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new ConnectRequest { Seq = 1, Heartbeat = new Heartbeat() }, ct);
        await call.RequestStream.CompleteAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(ct));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    private GrpcChannel CreateChannel() =>
        GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler(),
        });
}
