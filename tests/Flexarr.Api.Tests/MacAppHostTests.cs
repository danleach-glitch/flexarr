using System.Net;
using System.Net.Sockets;
using System.Text;
using Flexarr.Api.Endpoints;
using Microsoft.Extensions.Configuration;

namespace Flexarr.Api.Tests;

public class MacAppHostTests
{
    [Fact]
    public void WithoutTheMacApp_IsNotAvailable()
    {
        Assert.False(new MacAppHost(new ConfigurationBuilder().Build()).IsAvailable);
    }

    [Theory]
    [InlineData("/Volumes/Media/Films", "/Volumes/Media/Films")]
    [InlineData("", null)]
    public async Task ChooseFolder_SendsTheToken_AndReadsTheAppsAnswer(string answer, string? expected)
    {
        using var app = new TcpListener(IPAddress.Loopback, 0);
        app.Start();
        var port = ((IPEndPoint)app.LocalEndpoint).Port;
        var host = new MacAppHost(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Flexarr:MacApp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Flexarr:MacApp:Token"] = "secret",
        }).Build());

        var choosing = host.ChooseFolderAsync(CancellationToken.None);
        using var connection = await app.AcceptTcpClientAsync(CancellationToken.None);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var request = await reader.ReadLineAsync(CancellationToken.None);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"), CancellationToken.None);

        Assert.Equal("secret choose", request);
        Assert.Equal(expected, await choosing);
    }
}
