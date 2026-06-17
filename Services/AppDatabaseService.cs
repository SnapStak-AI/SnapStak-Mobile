using SQLite;
using SnapStakMobile.Models;

namespace SnapStakMobile.Services;

public class AppDatabaseService
{
    private SQLiteAsyncConnection? _db;
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (_db != null) return _db;

        await _initLock.WaitAsync();
        try
        {
            if (_db != null) return _db;

            string dbPath = Path.Combine(
                FileSystem.AppDataDirectory, "snapstak.db3");

            _db = new SQLiteAsyncConnection(dbPath,
                SQLiteOpenFlags.ReadWrite |
                SQLiteOpenFlags.Create |
                SQLiteOpenFlags.SharedCache);

            await _db.CreateTableAsync<SavedApp>();
            await _db.CreateTableAsync<ScannedApp>();

            // Migrate older saved_apps schema if needed
            try { await _db.ExecuteAsync("ALTER TABLE saved_apps ADD COLUMN ApkLastModified TEXT"); }
            catch { }
            try { await _db.ExecuteAsync("ALTER TABLE saved_apps ADD COLUMN Color TEXT"); }
            catch { }
            try { await _db.ExecuteAsync("ALTER TABLE saved_apps ADD COLUMN ApkPath TEXT"); }
            catch { }
            try { await _db.ExecuteAsync("ALTER TABLE saved_apps ADD COLUMN LocalIndexPath TEXT"); }
            catch { }

            return _db;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ── SavedApp (WebView apps) ────────────────────────────────────────────────

    public async Task<List<SavedApp>> GetAllAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<SavedApp>()
                       .OrderByDescending(a => a.SavedAt)
                       .ToListAsync();
    }

    public async Task<SavedApp?> GetByPackageAsync(string packageName)
    {
        var db = await GetDbAsync();
        return await db.Table<SavedApp>()
                       .Where(a => a.PackageName == packageName)
                       .FirstOrDefaultAsync();
    }

    public async Task SaveAsync(SavedApp app)
    {
        var db = await GetDbAsync();
        var existing = await GetByPackageAsync(app.PackageName);
        if (existing != null)
        {
            app.Id = existing.Id;
            app.SavedAt = DateTime.UtcNow;
            await db.UpdateAsync(app);
        }
        else
        {
            app.SavedAt = DateTime.UtcNow;
            await db.InsertAsync(app);
        }
    }

    public async Task DeleteAsync(SavedApp app)
    {
        var db = await GetDbAsync();
        await db.DeleteAsync(app);
    }

    /// <summary>
    /// Clears all scan history so the next scan re-inspects every app.
    /// Does NOT touch saved_apps — detected WebView apps are preserved.
    /// </summary>
    public async Task ClearScannedAsync()
    {
        var db = await GetDbAsync();
        await db.DeleteAllAsync<ScannedApp>();
    }

    public async Task<int> CountAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<SavedApp>().CountAsync();
    }

    // ── ScannedApp (every inspected app) ─────────────────────────────────────

    /// <summary>
    /// Returns all previously scanned APK timestamps.
    /// Key = packageName, Value = APK last-modified UTC at time of last scan.
    /// Any app whose current APK timestamp matches this value is skipped.
    /// </summary>
    public async Task<Dictionary<string, DateTime>> GetScannedTimestampsAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<ScannedApp>().ToListAsync();
        return all.ToDictionary(
            s => s.PackageName,
            s => s.ApkLastModified,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records that an app was inspected at the given APK timestamp.
    /// Called for every non-system app regardless of whether it was
    /// detected as a WebView app — this is what makes repeat scans fast.
    /// Uses INSERT OR REPLACE for efficiency.
    /// </summary>
    public async Task MarkScannedAsync(string packageName, DateTime apkLastModified)
    {
        var db = await GetDbAsync();
        await db.InsertOrReplaceAsync(new ScannedApp
        {
            PackageName = packageName,
            ApkLastModified = apkLastModified,
            ScannedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Bulk-records scan results for all inspected apps in a single transaction.
    /// Much faster than calling MarkScannedAsync in a loop.
    /// </summary>
    public async Task MarkScannedBatchAsync(IEnumerable<(string packageName, DateTime apkLastModified)> items)
    {
        var db = await GetDbAsync();
        var records = items.Select(i => new ScannedApp
        {
            PackageName = i.packageName,
            ApkLastModified = i.apkLastModified,
            ScannedAt = DateTime.UtcNow
        }).ToList();

        await db.RunInTransactionAsync(conn =>
        {
            foreach (var r in records)
                conn.InsertOrReplace(r);
        });
    }
}