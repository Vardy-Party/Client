namespace VardyParty.Kernel;

/// <summary>
/// Exception thrown when the Games API returns HTTP 500 after all retries
/// </summary>
public class ApiSystemDownException : Exception
{
    public ApiSystemDownException() : base("The Games API system is down")
    {
    }

    public ApiSystemDownException(string message) : base(message)
    {
    }

    public ApiSystemDownException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
