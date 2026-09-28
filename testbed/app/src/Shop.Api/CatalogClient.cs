using System.Net;
using Microsoft.Extensions.Options;
using Shop.Common;

namespace Shop.Api;

public sealed class CatalogClientOptions
{
    public string BaseUrl { get; set; } = "http://catalog:8080";
    public int TimeoutMs { get; set; } = 2000;
    public int Retries { get; set; } = 2;
    public int RetryDelayMs { get; set; } = 100;
}

public sealed record Reservation(long ProductId, int Quantity, decimal UnitPrice);

public enum ReserveOutcome { Reserved, NotFound, InsufficientStock }

public sealed class CatalogUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// HTTP-клиент к catalog. Таймаут и число повторов читаются на каждый запрос,
/// поэтому их изменение в настройках действует без рестарта.
/// </summary>
public sealed class CatalogClient(HttpClient http, IOptionsMonitor<CatalogClientOptions> options, ILogger<CatalogClient> logger)
{
    public async Task<(ReserveOutcome Outcome, Reservation? Reservation)> ReserveAsync(
        long productId, int quantity, CancellationToken ct)
    {
        using var response = await SendWithRetriesAsync("reserve",
            () => new HttpRequestMessage(HttpMethod.Post, $"/products/{productId}/reserve")
            {
                Content = JsonContent.Create(new { quantity }),
            }, ct);

        return response.StatusCode switch
        {
            HttpStatusCode.OK => (ReserveOutcome.Reserved, await response.Content.ReadFromJsonAsync<Reservation>(ct)),
            HttpStatusCode.NotFound => (ReserveOutcome.NotFound, null),
            HttpStatusCode.Conflict => (ReserveOutcome.InsufficientStock, null),
            _ => throw new CatalogUnavailableException($"catalog returned {(int)response.StatusCode}"),
        };
    }

    public async Task<(HttpStatusCode Status, string Body)> ForwardGetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var response = await SendWithRetriesAsync("get",
            () => new HttpRequestMessage(HttpMethod.Get, pathAndQuery), ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        string operation, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var o = options.CurrentValue;
        http.BaseAddress ??= new Uri(o.BaseUrl);

        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.TimeoutMs);
            try
            {
                var response = await DependencyMetrics.TrackAsync("catalog", operation, async () =>
                {
                    var r = await http.SendAsync(createRequest(), timeout.Token);
                    if ((int)r.StatusCode >= 500)
                    {
                        r.Dispose();
                        throw new HttpRequestException($"catalog returned {(int)r.StatusCode}", null, r.StatusCode);
                    }
                    return r;
                });
                return response;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
                var reason = ex is OperationCanceledException ? $"timeout after {o.TimeoutMs}ms" : ex.Message;
                if (attempt >= o.Retries)
                {
                    logger.LogError("Catalog {Operation} failed after {Attempts} attempts: {Reason}",
                        operation, attempt + 1, reason);
                    throw new CatalogUnavailableException(reason, ex);
                }

                logger.LogWarning("Catalog {Operation} attempt {Attempt} failed: {Reason}", operation, attempt + 1, reason);
                await Task.Delay(o.RetryDelayMs * (attempt + 1), ct);
            }
        }
    }
}
