using System.Net;
using System.Text;

namespace AdobeDownloader.Core;

/// <summary>Official Adobe HTTPS only. Redirects are checked before making each request.</summary>
public sealed class AdobeTransport(HttpClient client)
{
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri ValidateUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.IdnHost.Equals("adobe.com", StringComparison.OrdinalIgnoreCase) ||
              uri.IdnHost.EndsWith(".adobe.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Expected an official Adobe HTTPS URL.");
        return uri;
    }

    public async Task<HttpResponseMessage> GetAsync(Uri uri, string? buildGuid, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            ValidateUrl(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "Creative Cloud/6.8.1.856/Win-10.0");
            request.Headers.Add("x-adobe-app-id", "accc-hdcore-desktop");
            request.Headers.TryAddWithoutValidation("x-api-key", "Creative Cloud_v6_4");
            request.Headers.Add("x-adobe-app-version", "6.8.1.856");
            if (!string.IsNullOrWhiteSpace(buildGuid)) request.Headers.Add("x-adobe-build-guid", buildGuid);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Adobe redirect had no Location header.");
                uri = new Uri(uri, location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Adobe returned HTTP {(int)status} ({status}).", null, status);
            }
            return response;
        }
        throw new HttpRequestException("Adobe redirect limit exceeded.");
    }

    public async Task<string> GetTextAsync(Uri uri, string? buildGuid, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var response = await GetAsync(uri, buildGuid, timeout.Token);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > 32 * 1024 * 1024)
                throw new InvalidDataException("Adobe metadata exceeded the 32 MiB limit.");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
