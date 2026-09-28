using System.Net;

namespace Icc.ControlPlane.Tests;

public class HealthTests(ControlPlaneFactory factory) : IClassFixture<ControlPlaneFactory>
{
    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
