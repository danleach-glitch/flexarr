using System.Security.Claims;
using Flexarr.Api.Authentication;
using Flexarr.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>A long-lived stream checks, as it runs, that the sign-in it was opened with still holds.</summary>
public sealed class OpenStreamSignInTests : IDisposable
{
    private readonly FlexarrApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private DefaultHttpContext Request(string method, string? signInId = null, string? apiKey = null)
    {
        var claims = new List<Claim> { new(FlexarrClaims.Method, method) };
        if (signInId is not null)
        {
            claims.Add(new Claim(FlexarrClaims.SignIn, signInId));
        }

        var http = new DefaultHttpContext { RequestServices = _factory.Services, User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (apiKey is not null)
        {
            http.Request.Headers[ApiKeyAuthenticationHandler.HeaderName] = apiKey;
        }

        return http;
    }

    [Fact]
    public void TheServersLogs_StopWhenLocked()
    {
        var signIns = _factory.Services.GetRequiredService<SignIns>();
        var unlocks = _factory.Services.GetRequiredService<AdminUnlocks>();
        var (browser, _) = signIns.Issue(SignInKind.Browser, "Safari", NetworkZone.Local, "192.168.1.20");
        unlocks.Unlock(browser.Id);
        var logs = OpenStreamSignIn.Of(Request(ApiKeyAuthenticationHandler.Methods.Session, browser.Id), admin: true);
        Assert.True(logs.Holds());

        unlocks.Lock(browser.Id);

        Assert.False(logs.Holds());
    }

    [Fact]
    public void WhatsPlaying_StopsOnSigningOut_OrChoosingAnotherProfile()
    {
        var signIns = _factory.Services.GetRequiredService<SignIns>();
        var (phone, _) = signIns.Issue(SignInKind.Device, "Phone", NetworkZone.Local, "192.168.1.21");
        var (tv, _) = signIns.Issue(SignInKind.Device, "TV", NetworkZone.Local, "192.168.1.22");
        var onPhone = OpenStreamSignIn.Of(Request(ApiKeyAuthenticationHandler.Methods.Device, phone.Id));
        var onTv = OpenStreamSignIn.Of(Request(ApiKeyAuthenticationHandler.Methods.Device, tv.Id));
        Assert.True(onPhone.Holds());
        Assert.True(onTv.Holds());

        signIns.Revoke(phone.Id);
        signIns.ChooseProfile(tv.Id, "someone-else");

        Assert.False(onPhone.Holds());
        Assert.False(onTv.Holds());
    }

    [Fact]
    public void TheApiKey_HoldsWhileItsTheServersKey()
    {
        Assert.True(OpenStreamSignIn.Of(Request(ApiKeyAuthenticationHandler.Methods.ApiKey, apiKey: FlexarrApiFactory.ApiKey), admin: true).Holds());
        Assert.False(OpenStreamSignIn.Of(Request(ApiKeyAuthenticationHandler.Methods.ApiKey, apiKey: "an-old-key"), admin: true).Holds());
    }
}
