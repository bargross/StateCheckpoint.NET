namespace StateCheckpoint.NET;

internal static class TagsHelper
{
    public static bool TagsMatch(Dictionary<string, string>? actual, Dictionary<string, string>? filter)
    {
        if (filter == null) 
            return true;

        if (actual == null) 
            return false;

        foreach (var pair in filter)
            if (!actual.TryGetValue(pair.Key, out var val) || val != pair.Value)
                return false;

        return true;
    }
}
