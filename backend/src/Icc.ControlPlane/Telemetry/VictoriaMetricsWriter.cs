using System.Text;
using System.Text.Json;
using Icc.Contracts.Agent.V1;

namespace Icc.ControlPlane.Telemetry;

public sealed class StorageOptions
{
    public string VictoriaMetricsUrl { get; set; } = "http://victoriametrics:8428";
    public string VictoriaLogsUrl { get; set; } = "http://victorialogs:9428";
}

/// <summary>
/// Пишет метрики агентов в VictoriaMetrics через /api/v1/import (JSON lines).
/// К меткам каждой серии добавляется agent_id: по нему серии связываются с агентом.
/// </summary>
public sealed class VictoriaMetricsWriter(HttpClient http, ILogger<VictoriaMetricsWriter> logger)
{
    public async Task WriteAsync(Guid agentId, MetricBatch batch, CancellationToken ct)
    {
        if (batch.Samples.Count == 0)
            return;

        var body = new StringBuilder();
        foreach (var sample in batch.Samples)
        {
            var labels = new Dictionary<string, string>(sample.Labels.Count + 2)
            {
                ["__name__"] = sample.Name,
                ["agent_id"] = agentId.ToString(),
            };
            foreach (var (key, value) in sample.Labels)
                labels[key] = value;

            var timestamp = sample.Timestamp?.ToDateTimeOffset().ToUnixTimeMilliseconds()
                            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            body.Append(JsonSerializer.Serialize(new
            {
                metric = labels,
                values = new[] { double.IsFinite(sample.Value) ? sample.Value : 0 },
                timestamps = new[] { timestamp },
            }));
            body.Append('\n');
        }

        using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/v1/import", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("VictoriaMetrics import failed: {Status} {Body}",
                (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
    }
}
