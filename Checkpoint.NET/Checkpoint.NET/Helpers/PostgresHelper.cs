using Npgsql;
using System.Text.Json;

namespace Checkpoint.NET.Stores;

internal static class PostgresHelper
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public static void AddJsonParameter<T>(this NpgsqlCommand cmd, string name, T value)
        where T : class
    {
        cmd.Parameters.AddWithValue(name, JsonSerializer.Serialize(value, _jsonOpts));
    }
}