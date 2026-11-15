using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

/// <summary>A server shows an app it holds the app's key before the app sends it there over plain HTTP.</summary>
public sealed class ServerProofTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private const string Challenge = "00112233445566778899aabbccddeeff";

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    /// <summary>The same numbers as the apps' own tests (FlexarrKit's ServerProofTests, Android core's), so all three agree.</summary>
    [Fact]
    public void WorksOutTheSameAnswersAsTheApps()
    {
        var hash = ServerProof.HashOf("test-key");

        Assert.Equal("62af8704764faf8ea82fc61ce9c4c3908b6cb97d463a634e9e587d7c885db0ef", hash);
        Assert.Equal("3de99eba31a42ce3d62608d3d0dc14263f03df662a95478c87e6f6bba7dcba2f", ServerProof.KeyId(hash));
        Assert.Equal("7991230abbbdddd2888885e97b6dad0651507d6da3f6f3467d198caaa5378e01", ServerProof.Answer(hash, "server-1", Challenge));
    }

    private async Task<(string? ServerId, string? Proof)> AskAsync(string key, string challenge = Challenge)
    {
        var keyId = ServerProof.KeyId(ServerProof.HashOf(key));
        var setup = await factory.CreateClient().GetFromJsonAsync<JsonElement>(Api($"/setup?challenge={challenge}&keyId={keyId}"));
        return (setup.GetProperty("serverId").GetString(),
            setup.TryGetProperty("proof", out var proof) && proof.ValueKind == JsonValueKind.String ? proof.GetString() : null);
    }

    [Fact]
    public async Task AnswersForTheApiKey_AndForADevicesToken()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        var serverId = factory.Services.GetRequiredService<IOptionsMonitor<Configuration.FlexarrOptions>>().CurrentValue.ServerId!;

        var (idForKey, forKey) = await AskAsync(FlexarrApiFactory.ApiKey);
        var (_, forToken) = await AskAsync(token);

        Assert.Equal(serverId, idForKey);
        Assert.Equal(ServerProof.Answer(ServerProof.HashOf(FlexarrApiFactory.ApiKey), serverId, Challenge), forKey);
        Assert.Equal(ServerProof.Answer(ServerProof.HashOf(token), serverId, Challenge), forToken);
    }

    [Fact]
    public async Task GivesNoAnswer_ForAKeyItDoesntKnow_OrAChallengeThatIsntOne()
    {
        var (_, unknown) = await AskAsync("not-a-key-this-server-has");
        var (_, badChallenge) = await AskAsync(FlexarrApiFactory.ApiKey, challenge: "short");

        Assert.Null(unknown);
        Assert.Null(badChallenge);
    }
}
