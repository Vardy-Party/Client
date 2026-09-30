namespace VardyParty.Kernel;

/// <summary>
/// The games catalog rejected the call because no usable access token was
/// available (HTTP 401). This is not an empty night of fixtures.
/// </summary>
public class ApiUnauthorizedException : Exception
{
    public ApiUnauthorizedException() : base("The Games API rejected the access token")
    {
    }

    public ApiUnauthorizedException(string message) : base(message)
    {
    }

    public ApiUnauthorizedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
