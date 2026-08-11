namespace StateCheckpoint.NET.Tests.Integration
{
    internal static class DbHelper
    {
        public static string BuildConnectionString()
        {
            var host = Environment.GetEnvironmentVariable("PG_HOST") ?? "localhost";
            var port = Environment.GetEnvironmentVariable("PG_PORT") ?? "5432";
            var db = Environment.GetEnvironmentVariable("PG_DATABASE") ?? "postgres";
            var user = Environment.GetEnvironmentVariable("PG_USER") ?? "postgres";
            var password = Environment.GetEnvironmentVariable("PG_PASSWORD") ?? "postgres";

            return $"Host={host};Port={port};Database={db};Username={user};Password={password}";
        }
    }
}
