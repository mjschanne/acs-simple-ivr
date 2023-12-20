namespace SimpleIVR.Models;


public class IncomingCallEvent
{
    public string CallerDisplayName { get; set; }
    public string CorrelationId { get; set; }
    public Target From { get; set; }
    public string IncomingCallContext { get; set; }
    public string ServerCallId { get; set; }
    public Target To { get; set; }
}
