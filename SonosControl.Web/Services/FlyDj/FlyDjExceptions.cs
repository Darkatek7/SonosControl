namespace SonosControl.Web.Services.FlyDj;

/// <summary>Base type of all failures talking to the FlyDJ sidecar.</summary>
public class FlyDjException : Exception
{
    public FlyDjException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The sidecar could not be reached, timed out, or answered with a server error.</summary>
public sealed class FlyDjUnavailableException : FlyDjException
{
    public FlyDjUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The sidecar is not running its simulation (<c>409 not_running</c>).</summary>
public sealed class FlyDjNotRunningException : FlyDjException
{
    public FlyDjNotRunningException(string message = "The FlyDJ simulation is not running.") : base(message) { }
}

/// <summary>The sidecar rejected a request with another 4xx status.</summary>
public sealed class FlyDjApiException : FlyDjException
{
    public FlyDjApiException(int statusCode, string? code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string? Code { get; }
}
