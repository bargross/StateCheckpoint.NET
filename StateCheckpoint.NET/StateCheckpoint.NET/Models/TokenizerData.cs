namespace StateCheckpoint.NET.Models;

public class TokenizerData
{
    public string Type { get; set; } = "BPE";

    public Dictionary<string, int> TokenToId { get; set; } = new();

    public Dictionary<int, string> IdToToken { get; set; } = new();

    public List<MergeRule>? MergeRules { get; set; }

    public Dictionary<string, double>? TokenLogProbabilities { get; set; }

    public Dictionary<string, int> SpecialTokens { get; set; } = new()
    {
        { "bos", 1 }, { "eos", 2 }, { "pad", 0 }, { "unk", 3 } // sane defaults
    };

    public Dictionary<byte, int>? ByteToToken { get; set; }

    public int GetSpecialTokenId(string name, int defaultValue = -1)
    {
        return SpecialTokens.TryGetValue(name, out var id) ? id : defaultValue;
    }
}
