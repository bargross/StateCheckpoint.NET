namespace Checkpoint.NET.Stores;

/// <summary>
/// Low-level PostgreSQL Large Object SQL functions.
/// Used exclusively by PostgresModelStore (Training half) to store weights/optimizer > 2GB.
/// </summary>
internal static class PostgresLargeObjectQueries
{
    // --- Large Object Lifecycle ---
    public const string CreateLargeObject = "SELECT lo_create(0);";
    public const string UnlinkLargeObject = "SELECT lo_unlink(@oid);";
    public const string CloseLargeObject = "SELECT lo_close(@fd);";

    // --- Opening Modes ---
    public const string OpenWrite = "SELECT lo_open(@oid, 131072);"; // INV_WRITE = 0x20000
    public const string OpenRead = "SELECT lo_open(@oid, 262144);";  // INV_READ  = 0x40000

    // --- Read/Write Operations ---
    public const string WriteChunk = "SELECT lo_write(@fd, @data);";
    public const string ReadChunk = "SELECT lo_read(@fd, @length);";

    // --- Seeking / Sizing ---
    public const string GetSize = "SELECT lo_lseek(@fd, 0, 2);";     // SEEK_END
    public const string SeekStart = "SELECT lo_lseek(@fd, 0, 0);";   // SEEK_SET
}