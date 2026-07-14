namespace StateCheckpoint.NET.Models;

public class SessionSummary
{
    public Guid SessionId { get; set; }
    public string ModelFingerprint { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
    public int TokenHistoryLength { get; set; }
    public SamplingData? SamplingConfig { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

