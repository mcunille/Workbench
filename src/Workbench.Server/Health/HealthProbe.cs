// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Health;

public static class HealthProbe
{
    public static async Task<int> RunAsync(
        HttpMessageInvoker client,
        Uri readinessUri,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, readinessUri);
            using var response = await client.SendAsync(request, cancellationToken);

            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
