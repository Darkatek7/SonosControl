using System.Net;

namespace SonosControl.DAL.Repos;

public sealed class SonosCommandException : HttpRequestException
{
    public string SpeakerIp { get; }
    public string Service { get; }
    public string Action { get; }
    public int? UpnpErrorCode { get; }
    public string ErrorDescription { get; }

    public SonosCommandException(string speakerIp, string service, string action, HttpStatusCode? status,
        int? upnpErrorCode, string description, Exception? inner = null)
        : base($"Sonos {service}.{action} on {speakerIp} failed"
            + (status.HasValue ? $" (HTTP {(int)status.Value}" + (upnpErrorCode.HasValue ? $", UPnP {upnpErrorCode}" : "") + ")" : "")
            + $": {description}", inner, status)
    {
        SpeakerIp = speakerIp;
        Service = service;
        Action = action;
        UpnpErrorCode = upnpErrorCode;
        ErrorDescription = description;
    }
}
