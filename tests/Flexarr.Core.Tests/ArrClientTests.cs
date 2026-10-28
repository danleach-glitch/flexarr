using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Flexarr.Integrations.Arr;

namespace Flexarr.Core.Tests;

public partial class ArrClientTests
{
    [Fact]
    public async Task TheQueue_IsReadPageByPage_PastTheFirst200()
    {
        var radarr = new QueueOf(450);
        var client = new ArrClient(new HttpClient(radarr));
        var instance = new ArrInstance { Name = "Radarr", Kind = ArrKind.Radarr, BaseUrl = new Uri("http://radarr.test"), ApiKey = "k" };

        var queue = await client.GetQueueAsync(instance);

        Assert.Equal(450, queue.Count);
        Assert.Equal(Enumerable.Range(1, 450), queue.Select(q => q.Id));
        Assert.Equal(3, radarr.Pages);
    }

    /// <summary>A Radarr whose queue holds <paramref name="total"/> downloads, served a page at a time.</summary>
    private sealed partial class QueueOf(int total) : HttpMessageHandler
    {
        public int Pages { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Pages++;
            var query = request.RequestUri!.Query;
            var page = int.Parse(PageNumber().Match(query).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var size = int.Parse(PageSize().Match(query).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var ids = Enumerable.Range((page - 1) * size + 1, Math.Max(0, Math.Min(size, total - (page - 1) * size)));
            var json = $"{{\"page\":{page},\"pageSize\":{size},\"totalRecords\":{total},\"records\":[{string.Join(',', ids.Select(i => $"{{\"id\":{i}}}"))}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        [GeneratedRegex(@"[?&]page=(\d+)")]
        private static partial Regex PageNumber();

        [GeneratedRegex(@"pageSize=(\d+)")]
        private static partial Regex PageSize();
    }
}
