using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RosterControl.Integrations;

/// <summary>Turns network exceptions into short messages people can act on ("check your connection").</summary>
public static class NetworkErrors
{
    public static string Describe(Exception ex, string service)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }:
                    return $"{service} is limiting requests right now; it will be retried later.";
                case HttpRequestException { StatusCode: HttpStatusCode.NotFound }:
                    return $"{service} doesn't have that file (404).";
                case HttpRequestException { StatusCode: { } code } when (int)code >= 500:
                    return $"{service} is having problems ({(int)code}); try again later.";
                case HttpRequestException { StatusCode: { } code }:
                    return $"{service} returned an error ({(int)code}).";
                case SocketException or HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError }:
                    return $"Can't reach {service}. Check your internet connection.";
                case TaskCanceledException or TimeoutException or OperationCanceledException:
                    return $"{service} didn't respond in time. Check your internet connection.";
                case JsonException:
                    return $"{service} sent data that couldn't be read; try again later.";
            }
        }
        return ex is HttpRequestException ? $"Can't reach {service}. Check your internet connection." : ex.Message;
    }
}
