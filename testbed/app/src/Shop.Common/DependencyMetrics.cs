using Prometheus;

namespace Shop.Common;

/// <summary>
/// Время и исход обращений к зависимостям (БД, Redis, соседние сервисы).
/// Для RCA это главный сигнал: какая зависимость начала тормозить или падать.
/// </summary>
public static class DependencyMetrics
{
    private static readonly Histogram Duration = Metrics.CreateHistogram(
        "shop_dependency_duration_seconds", "Время обращения к зависимости", new HistogramConfiguration
        {
            LabelNames = ["dependency", "operation", "result"],
            Buckets = Histogram.ExponentialBuckets(0.001, 2, 14),
        });

    public static async Task<T> TrackAsync<T>(string dependency, string operation, Func<Task<T>> action)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = "error";
        try
        {
            var value = await action();
            result = "ok";
            return value;
        }
        finally
        {
            Duration.WithLabels(dependency, operation, result)
                .Observe(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    public static Task TrackAsync(string dependency, string operation, Func<Task> action) =>
        TrackAsync(dependency, operation, async () =>
        {
            await action();
            return true;
        });
}
