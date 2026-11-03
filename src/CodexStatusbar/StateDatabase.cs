using System.Runtime.InteropServices;

namespace CodexStatusbar;

/// <summary>One row of <c>~/.codex/state_5.sqlite</c>'s <c>threads</c> table.</summary>
internal sealed record StateThreadRecord(
    string Id,
    string? Title,
    string? RolloutPath,
    long TokensUsed,
    long RecencyAtMs);

/// <summary>
/// Read-only access to <c>state_5.sqlite</c> through the SQLite library that ships with Windows
/// (<c>winsqlite3.dll</c>), so the overlay needs no extra package and never writes to Codex state.
/// Used only for restart recovery / thread titles and as a fallback when the IPC route is down.
/// </summary>
internal static class StateDatabase
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x00000001;

    // Titles are intentionally not loaded. The panel uses short local identifiers.
    private const string ThreadColumns = "id, NULL, rollout_path, tokens_used, recency_at_ms";

    public static string ResolvePath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return ResolveLocalDatabasePath(Path.Combine(codexHome, "state_5.sqlite"));
    }

    /// <summary>
    /// Resolves the database that belongs to a given sessions root, so an overridden
    /// <c>--sessions</c> path (an isolated test home, a second Codex profile) never falls back to the
    /// default home's database and reports another profile's conversation.
    /// </summary>
    public static string ResolvePathForSessionsRoot(string? sessionsRoot)
    {
        if (!string.IsNullOrWhiteSpace(sessionsRoot))
        {
            // A missing isolated database must remain missing: never consult another profile.
            // A filesystem root has no sibling database, so disable the fallback with an empty path.
            if (!LocalPathPolicy.IsAllowedDirectory(sessionsRoot, allowMissing: true)) return string.Empty;
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsRoot));
            var home = Path.GetDirectoryName(normalized);
            return string.IsNullOrWhiteSpace(home)
                ? string.Empty
                : ResolveLocalDatabasePath(Path.Combine(home, "state_5.sqlite"));
        }

        return ResolvePath();
    }

    private static string ResolveLocalDatabasePath(string path)
    {
        try { return LocalPathPolicy.NormalizeLocalPath(path); }
        catch (ArgumentException) { return string.Empty; }
    }

    public static StateThreadRecord? TryReadThread(string databasePath, string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return QuerySingle(
            databasePath,
            $"SELECT {ThreadColumns} FROM threads WHERE id = $id LIMIT 1",
            threadId);
    }

    /// <summary>
    /// The most recently viewed non-archived thread. This is the documented fallback when the
    /// Codex IPC pipe is unavailable — never an aggregate across all of ~/.codex.
    /// </summary>
    public static StateThreadRecord? TryReadMostRecentThread(string databasePath) =>
        QuerySingle(
            databasePath,
            $"SELECT {ThreadColumns} FROM threads WHERE archived = 0 " +
            "ORDER BY recency_at_ms DESC LIMIT 1",
            null);

    public static IReadOnlyList<StateThreadRecord> TryReadRecentThreads(string databasePath) =>
        QueryRows(databasePath, $"SELECT {ThreadColumns} FROM threads WHERE archived = 0 "
            + "ORDER BY recency_at_ms DESC LIMIT 8", null, 8);

    private static StateThreadRecord? QuerySingle(string databasePath, string sql, string? parameter)
        => QueryRows(databasePath, sql, parameter, 1).FirstOrDefault();

    private static IReadOnlyList<StateThreadRecord> QueryRows(string databasePath, string sql, string? parameter, int maximum)
    {
        var rows = new List<StateThreadRecord>();
        // Validate before any File.Exists/SQLite call: a UNC path or mapped network drive can
        // initiate outbound authentication even when the application has no networking API.
        if (!LocalPathPolicy.IsAllowedFile(databasePath))
        {
            return rows;
        }

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        var databasePathPointer = IntPtr.Zero;
        var sqlPointer = IntPtr.Zero;
        var parameterPointer = IntPtr.Zero;
        var started = Environment.TickCount64;
        SqliteProgress progress = _ => Environment.TickCount64 - started > 75 ? 1 : 0;
        try
        {
            databasePathPointer = Marshal.StringToCoTaskMemUTF8(databasePath);
            if (sqlite3_open_v2(databasePathPointer, out database, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk)
            {
                return rows;
            }

            _ = sqlite3_busy_timeout(database, 50);
            sqlite3_progress_handler(database, 1000, progress, IntPtr.Zero);

            sqlPointer = Marshal.StringToCoTaskMemUTF8(sql);
            if (sqlite3_prepare_v2(database, sqlPointer, -1, out statement, IntPtr.Zero) != SqliteOk)
            {
                return rows;
            }

            if (parameter is not null)
            {
                parameterPointer = Marshal.StringToCoTaskMemUTF8(parameter);
                if (sqlite3_bind_text(statement, 1, parameterPointer, -1, IntPtr.Zero) != SqliteOk)
                {
                    return rows;
                }
            }

            while (rows.Count < maximum && sqlite3_step(statement) == SqliteRow)
            {
                rows.Add(new StateThreadRecord(
                    ReadText(statement, 0) ?? string.Empty,
                    null,
                    ReadText(statement, 2),
                    sqlite3_column_int64(statement, 3),
                    sqlite3_column_int64(statement, 4)));
            }
            return rows;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or AccessViolationException)
        {
            // No usable SQLite on this machine: the overlay simply runs without the state-db fallback.
            return rows;
        }
        finally
        {
            if (statement != IntPtr.Zero)
            {
                _ = sqlite3_finalize(statement);
            }

            if (database != IntPtr.Zero)
            {
                _ = sqlite3_close_v2(database);
            }
            GC.KeepAlive(progress);

            FreeString(databasePathPointer);
            FreeString(sqlPointer);
            FreeString(parameterPointer);
        }
    }

    private static void FreeString(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static string? ReadText(IntPtr statement, int column)
    {
        var pointer = sqlite3_column_text(statement, column);
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_open_v2(IntPtr filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_prepare_v2(IntPtr database, IntPtr sql, int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_bind_text(IntPtr statement, int index, IntPtr value, int byteCount, IntPtr destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern long sqlite3_column_int64(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_close_v2(IntPtr database);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteProgress(IntPtr argument);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void sqlite3_progress_handler(IntPtr database, int instructions, SqliteProgress callback, IntPtr argument);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
}
