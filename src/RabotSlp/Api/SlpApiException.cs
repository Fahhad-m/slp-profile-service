using System.Net;

namespace RabotSlp.Api;

/// <summary>The SLP API could not deliver a usable answer.</summary>
public class SlpApiException : Exception
{
    public SlpApiException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>The API answered 404 because the profile id does not exist.</summary>
public sealed class ProfileNotFoundException : SlpApiException
{
    public ProfileNotFoundException(string profileId, string detail)
        : base($"Profile '{profileId}' was not found: {detail}", HttpStatusCode.NotFound)
    {
        ProfileId = profileId;
    }

    public string ProfileId { get; }
}
