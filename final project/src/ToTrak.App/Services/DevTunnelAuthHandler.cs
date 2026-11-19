using System.Net.Http;
using Microsoft.Maui.Storage;

namespace ToTrak.App.Services;

public sealed class DevTunnelAuthHandler : DelegatingHandler
{
    public const string TokenStorageKey = "DevTunnelConnectToken";

    public DevTunnelAuthHandler()
        : base(new HttpClientHandler())
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Add authentication only for Microsoft Dev Tunnels.
        if (request.RequestUri?.Host.EndsWith(
                ".devtunnels.ms",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            string? token =
                await SecureStorage.Default.GetAsync(TokenStorageKey);

            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.TryAddWithoutValidation(
                    "X-Tunnel-Authorization",
                    $"tunnel {token.Trim()}");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}