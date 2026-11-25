using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RosterControl.Integrations;

namespace RosterControl.Tests;

public class NetworkErrorTests
{
    [Test]
    public void No_connection_suggests_checking_the_internet()
    {
        var ex = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.", new SocketException(11001));
        Assert.That(NetworkErrors.Describe(ex, "Data Dragon"), Is.EqualTo("Can't reach Data Dragon. Check your internet connection."));
    }

    [TestCase(HttpStatusCode.TooManyRequests, "limiting requests")]
    [TestCase(HttpStatusCode.Forbidden, "limiting requests")]
    [TestCase(HttpStatusCode.NotFound, "404")]
    [TestCase(HttpStatusCode.BadGateway, "having problems (502)")]
    [TestCase(HttpStatusCode.BadRequest, "error (400)")]
    public void Http_status_codes_get_a_short_explanation(HttpStatusCode code, string expected)
    {
        var ex = new HttpRequestException("Response status code does not indicate success.", null, code);
        Assert.That(NetworkErrors.Describe(ex, "GitHub"), Does.StartWith("GitHub").And.Contain(expected));
    }

    [Test]
    public void Timeouts_and_bad_data_are_explained()
    {
        Assert.That(NetworkErrors.Describe(new TaskCanceledException("timeout", new TimeoutException()), "Meraki"), Does.Contain("didn't respond in time"));
        Assert.That(NetworkErrors.Describe(new InvalidOperationException("wrapper", new JsonException("bad")), "Community Dragon"), Does.Contain("couldn't be read"));
    }

    [Test]
    public void Other_errors_keep_their_own_message()
    {
        Assert.That(NetworkErrors.Describe(new InvalidOperationException("Disk is full"), "Data Dragon"), Is.EqualTo("Disk is full"));
    }
}
