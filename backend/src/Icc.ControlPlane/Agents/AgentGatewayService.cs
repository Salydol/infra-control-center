using Grpc.Core;
using Icc.Contracts.Agent.V1;

namespace Icc.ControlPlane.Agents;

/// <summary>
/// Точка входа агентов. Пока — каркас: принимает поток, отвечает HelloAck
/// и подтверждает сообщения. Регистрация с mTLS и сохранение данных —
/// недели 4–6 календарного плана.
/// </summary>
public sealed class AgentGatewayService(ILogger<AgentGatewayService> logger) : AgentService.AgentServiceBase
{
    public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "Регистрация агентов ещё не реализована"));

    public override async Task Connect(
        IAsyncStreamReader<ConnectRequest> requestStream,
        IServerStreamWriter<ConnectResponse> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken))
            return;

        var first = requestStream.Current;
        if (first.PayloadCase != ConnectRequest.PayloadOneofCase.Hello)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Первое сообщение должно быть Hello"));

        logger.LogInformation("Агент {AgentId} ({Hostname}) подключился",
            first.Hello.AgentId, first.Hello.Host?.Hostname);

        await responseStream.WriteAsync(new ConnectResponse { HelloAck = new HelloAck { Config = DefaultConfig() } });
        await responseStream.WriteAsync(new ConnectResponse { Ack = new Ack { Seq = first.Seq } });

        await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
        {
            logger.LogDebug("Агент {AgentId}: {Payload} seq={Seq}",
                first.Hello.AgentId, message.PayloadCase, message.Seq);
            await responseStream.WriteAsync(new ConnectResponse { Ack = new Ack { Seq = message.Seq } });
        }
    }

    internal static AgentConfig DefaultConfig() => new()
    {
        HeartbeatIntervalSeconds = 10,
        MetricsIntervalSeconds = 5,
        InventoryIntervalSeconds = 30,
        MaxLogLineBytes = 16 * 1024,
    };
}
