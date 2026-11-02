using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.C411Torbox.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.C411Torbox.Data;

/// <summary>Stockage SQLite des demandes et des notifications.</summary>
public class RequestRepository
{
    private const int SchemaVersion = 1;

    private static readonly RequestStatus[] ActiveStatuses =
    [
        RequestStatus.Pending,
        RequestStatus.Approved,
        RequestStatus.AutoApproved,
        RequestStatus.SentToTorbox,
        RequestStatus.DownloadingOnTorbox,
        RequestStatus.CompletedOnTorbox,
    ];

    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public RequestRepository(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        Migrate();
    }

    public MediaRequest Insert(MediaRequest r)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Requests (UserId, UserName, CreatedAt, UpdatedAt, MediaKind, Scope, MediaTitle, Year, TmdbId, ImdbId, TvdbId,
                    PosterUrl, Season, Episode, TorrentTitle, SizeBytes, Seeders, Leechers, InfoHash, ProwlarrGuid, MagnetUrl, DownloadUrl,
                    WasCachedOnTorbox, AutoApprovalReason, Status, ReviewedBy, ReviewedAt, RejectReason, TorboxTorrentId, TorboxProgress, LastError, EpisodesInLibraryAtRequest)
                VALUES ($UserId, $UserName, $CreatedAt, $UpdatedAt, $MediaKind, $Scope, $MediaTitle, $Year, $TmdbId, $ImdbId, $TvdbId,
                    $PosterUrl, $Season, $Episode, $TorrentTitle, $SizeBytes, $Seeders, $Leechers, $InfoHash, $ProwlarrGuid, $MagnetUrl, $DownloadUrl,
                    $WasCachedOnTorbox, $AutoApprovalReason, $Status, $ReviewedBy, $ReviewedAt, $RejectReason, $TorboxTorrentId, $TorboxProgress, $LastError, $EpisodesInLibraryAtRequest);
                SELECT last_insert_rowid();
                """;
            r.CreatedAt = r.CreatedAt == default ? DateTime.UtcNow : r.CreatedAt;
            r.UpdatedAt = r.CreatedAt;
            BindRequest(cmd, r);
            r.Id = (long)cmd.ExecuteScalar()!;
            return r;
        }
    }

    public void Update(MediaRequest r)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE Requests SET UpdatedAt=$UpdatedAt, TorrentTitle=$TorrentTitle, InfoHash=$InfoHash, MagnetUrl=$MagnetUrl, DownloadUrl=$DownloadUrl,
                    AutoApprovalReason=$AutoApprovalReason, Status=$Status, ReviewedBy=$ReviewedBy, ReviewedAt=$ReviewedAt, RejectReason=$RejectReason,
                    TorboxTorrentId=$TorboxTorrentId, TorboxProgress=$TorboxProgress, LastError=$LastError
                WHERE Id=$Id;
                """;
            r.UpdatedAt = DateTime.UtcNow;
            BindRequest(cmd, r);
            cmd.Parameters.AddWithValue("$Id", r.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public MediaRequest? Get(long id)
        => QueryRequests("WHERE Id = $p0", id).FirstOrDefault();

    public IReadOnlyList<MediaRequest> GetByUser(Guid userId, int limit = 200)
        => QueryRequests("WHERE UserId = $p0 ORDER BY CreatedAt DESC LIMIT $p1", userId.ToString("N"), limit);

    public IReadOnlyList<MediaRequest> GetAll(RequestStatus? status, int limit = 500)
        => status is null
            ? QueryRequests("ORDER BY CreatedAt DESC LIMIT $p0", limit)
            : QueryRequests("WHERE Status = $p0 ORDER BY CreatedAt DESC LIMIT $p1", status.Value.ToString(), limit);

    /// <summary>Demandes en cours (ni refusées, ni échouées, ni disponibles).</summary>
    public IReadOnlyList<MediaRequest> GetActive()
        => QueryRequests("WHERE Status IN (" + string.Join(",", ActiveStatuses.Select(s => "'" + s + "'")) + ") ORDER BY CreatedAt");

    public IReadOnlyList<MediaRequest> GetActiveByHash(string infoHash)
        => GetActive().Where(r => string.Equals(r.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase)).ToList();

    public Notification AddNotification(Notification n)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Notifications (UserId, RequestId, Message, CreatedAt) VALUES ($UserId, $RequestId, $Message, $CreatedAt);
                SELECT last_insert_rowid();
                """;
            n.CreatedAt = n.CreatedAt == default ? DateTime.UtcNow : n.CreatedAt;
            cmd.Parameters.AddWithValue("$UserId", (object?)n.UserId?.ToString("N") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$RequestId", (object?)n.RequestId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$Message", n.Message);
            cmd.Parameters.AddWithValue("$CreatedAt", ToDb(n.CreatedAt));
            n.Id = (long)cmd.ExecuteScalar()!;
            return n;
        }
    }

    /// <summary>Notifications de l'utilisateur, plus celles destinées aux admins s'il l'est.</summary>
    public IReadOnlyList<Notification> GetNotifications(Guid userId, bool isAdmin, int limit = 100)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, UserId, RequestId, Message, CreatedAt, ReadAt FROM Notifications WHERE UserId = $u"
            + (isAdmin ? " OR UserId IS NULL" : string.Empty)
            + " ORDER BY CreatedAt DESC, Id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$u", userId.ToString("N"));
        cmd.Parameters.AddWithValue("$l", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<Notification>();
        while (reader.Read())
        {
            list.Add(new Notification
            {
                Id = reader.GetInt64(0),
                UserId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                RequestId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                Message = reader.GetString(3),
                CreatedAt = FromDb(reader.GetString(4)),
                ReadAt = reader.IsDBNull(5) ? null : FromDb(reader.GetString(5)),
            });
        }

        return list;
    }

    /// <summary>
    /// Marque comme lues les notifications visibles par l'utilisateur.
    /// Les notifications « admins » (UserId nul) sont partagées : un admin qui les lit les marque pour tous.
    /// </summary>
    public void MarkNotificationsRead(Guid userId, bool isAdmin)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Notifications SET ReadAt = $now WHERE ReadAt IS NULL AND (UserId = $u"
                + (isAdmin ? " OR UserId IS NULL" : string.Empty) + ")";
            cmd.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("$u", userId.ToString("N"));
            cmd.ExecuteNonQuery();
        }
    }

    private static void BindRequest(SqliteCommand cmd, MediaRequest r)
    {
        void Add(string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        Add("$UserId", r.UserId.ToString("N"));
        Add("$UserName", r.UserName);
        Add("$CreatedAt", ToDb(r.CreatedAt));
        Add("$UpdatedAt", ToDb(r.UpdatedAt));
        Add("$MediaKind", r.MediaKind.ToString());
        Add("$Scope", r.Scope.ToString());
        Add("$MediaTitle", r.MediaTitle);
        Add("$Year", r.Year);
        Add("$TmdbId", r.TmdbId);
        Add("$ImdbId", r.ImdbId);
        Add("$TvdbId", r.TvdbId);
        Add("$PosterUrl", r.PosterUrl);
        Add("$Season", r.Season);
        Add("$Episode", r.Episode);
        Add("$TorrentTitle", r.TorrentTitle);
        Add("$SizeBytes", r.SizeBytes);
        Add("$Seeders", r.Seeders);
        Add("$Leechers", r.Leechers);
        Add("$InfoHash", r.InfoHash);
        Add("$ProwlarrGuid", r.ProwlarrGuid);
        Add("$MagnetUrl", r.MagnetUrl);
        Add("$DownloadUrl", r.DownloadUrl);
        Add("$WasCachedOnTorbox", r.WasCachedOnTorbox ? 1 : 0);
        Add("$AutoApprovalReason", r.AutoApprovalReason.ToString());
        Add("$Status", r.Status.ToString());
        Add("$ReviewedBy", r.ReviewedBy);
        Add("$ReviewedAt", r.ReviewedAt is null ? null : ToDb(r.ReviewedAt.Value));
        Add("$RejectReason", r.RejectReason);
        Add("$TorboxTorrentId", r.TorboxTorrentId);
        Add("$TorboxProgress", r.TorboxProgress);
        Add("$LastError", r.LastError);
        Add("$EpisodesInLibraryAtRequest", r.EpisodesInLibraryAtRequest);
    }

    private static string ToDb(DateTime d) => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime FromDb(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private IReadOnlyList<MediaRequest> QueryRequests(string whereAndOrder, params object[] args)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Requests " + whereAndOrder;
        for (int i = 0; i < args.Length; i++)
        {
            cmd.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), args[i]);
        }

        using var reader = cmd.ExecuteReader();
        var list = new List<MediaRequest>();
        while (reader.Read())
        {
            string? S(string col) => reader[col] is string v ? v : null;
            long? L(string col) => reader[col] is long v ? v : null;
            list.Add(new MediaRequest
            {
                Id = (long)reader["Id"],
                UserId = Guid.Parse((string)reader["UserId"]),
                UserName = S("UserName") ?? string.Empty,
                CreatedAt = FromDb((string)reader["CreatedAt"]),
                UpdatedAt = FromDb((string)reader["UpdatedAt"]),
                MediaKind = Enum.Parse<MediaKind>((string)reader["MediaKind"]),
                Scope = Enum.Parse<RequestScope>((string)reader["Scope"]),
                MediaTitle = S("MediaTitle") ?? string.Empty,
                Year = (int?)L("Year"),
                TmdbId = S("TmdbId"),
                ImdbId = S("ImdbId"),
                TvdbId = S("TvdbId"),
                PosterUrl = S("PosterUrl"),
                Season = (int?)L("Season"),
                Episode = (int?)L("Episode"),
                TorrentTitle = S("TorrentTitle") ?? string.Empty,
                SizeBytes = L("SizeBytes") ?? 0,
                Seeders = (int)(L("Seeders") ?? 0),
                Leechers = (int)(L("Leechers") ?? 0),
                InfoHash = S("InfoHash"),
                ProwlarrGuid = S("ProwlarrGuid"),
                MagnetUrl = S("MagnetUrl"),
                DownloadUrl = S("DownloadUrl"),
                WasCachedOnTorbox = L("WasCachedOnTorbox") == 1,
                AutoApprovalReason = Enum.Parse<AutoApprovalReason>(S("AutoApprovalReason") ?? nameof(AutoApprovalReason.None)),
                Status = Enum.Parse<RequestStatus>((string)reader["Status"]),
                ReviewedBy = S("ReviewedBy"),
                ReviewedAt = S("ReviewedAt") is { } ra ? FromDb(ra) : null,
                RejectReason = S("RejectReason"),
                TorboxTorrentId = L("TorboxTorrentId"),
                TorboxProgress = reader["TorboxProgress"] is double p ? p : null,
                LastError = S("LastError"),
                EpisodesInLibraryAtRequest = (int)(L("EpisodesInLibraryAtRequest") ?? 0),
            });
        }

        return list;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Migrate()
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version >= SchemaVersion)
            {
                return;
            }

            using var tx = conn.BeginTransaction();
            using var create = conn.CreateCommand();
            create.Transaction = tx;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS Requests (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId TEXT NOT NULL,
                    UserName TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    MediaKind TEXT NOT NULL,
                    Scope TEXT NOT NULL,
                    MediaTitle TEXT NOT NULL,
                    Year INTEGER NULL,
                    TmdbId TEXT NULL,
                    ImdbId TEXT NULL,
                    TvdbId TEXT NULL,
                    PosterUrl TEXT NULL,
                    Season INTEGER NULL,
                    Episode INTEGER NULL,
                    TorrentTitle TEXT NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    Seeders INTEGER NOT NULL,
                    Leechers INTEGER NOT NULL,
                    InfoHash TEXT NULL,
                    ProwlarrGuid TEXT NULL,
                    MagnetUrl TEXT NULL,
                    DownloadUrl TEXT NULL,
                    WasCachedOnTorbox INTEGER NOT NULL,
                    AutoApprovalReason TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    ReviewedBy TEXT NULL,
                    ReviewedAt TEXT NULL,
                    RejectReason TEXT NULL,
                    TorboxTorrentId INTEGER NULL,
                    TorboxProgress REAL NULL,
                    LastError TEXT NULL,
                    EpisodesInLibraryAtRequest INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS IX_Requests_UserId ON Requests (UserId);
                CREATE INDEX IF NOT EXISTS IX_Requests_Status ON Requests (Status);
                CREATE INDEX IF NOT EXISTS IX_Requests_InfoHash ON Requests (InfoHash);
                CREATE TABLE IF NOT EXISTS Notifications (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId TEXT NULL,
                    RequestId INTEGER NULL,
                    Message TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    ReadAt TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Notifications_UserId ON Notifications (UserId);
                PRAGMA user_version = 1;
                """;
            create.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
