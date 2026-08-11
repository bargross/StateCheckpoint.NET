namespace StateCheckpoint.NET.Models;

public class SessionQuery
{
    public string? ModelFingerprint { get; set; }
    public DateTime? UpdatedAfter { get; set; }
    public DateTime? UpdatedBefore { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public int? Limit { get; set; }
    public SessionSortField OrderBy { get; set; } = SessionSortField.LastUpdated;
    public bool Descending { get; set; } = true;
}
