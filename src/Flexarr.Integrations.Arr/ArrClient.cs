using System.Net.Http.Json;

namespace Flexarr.Integrations.Arr;

public sealed record ArrSystemStatus(string AppName, string Version, string? InstanceName);

/// <summary>Minimal client for the shared *arr REST API surface.</summary>
public sealed class ArrClient(HttpClient httpClient)
{
    public async Task<ArrSystemStatus> GetSystemStatusAsync(ArrInstance instance, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(instance.BaseUrl, $"api/{instance.ApiVersion}/system/status"));
        request.Headers.Add("X-Api-Key", instance.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ArrSystemStatus>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{instance.Name} returned an empty system status.");
    }
}
