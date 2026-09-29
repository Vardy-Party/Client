namespace VardyParty.Kernel;

/// <summary>
/// The games catalog rejected the call because the signed-in account lacks
/// permission (HTTP 403). This is not an empty night of fixtures.
/// </summary>
public class ApiForbiddenException : Exception
{
    public ApiForbiddenException() : base("The Games API rejected the account's permission")
    {
    }

    public ApiForbiddenException(string message) : base(message)
    {
    }

    public ApiForbiddenException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
