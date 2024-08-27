﻿using ByteSizeLib;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosStaticFilesServer.Services;

public class MainFileCleanupService : IHostedService
{
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly ILogger<MainFileCleanupService> _logger;
    private readonly MareMetrics _metrics;
    private readonly IServiceProvider _services;
    private CancellationTokenSource _cleanupCts;

    public MainFileCleanupService(MareMetrics metrics, ILogger<MainFileCleanupService> logger,
        IServiceProvider services, IConfigurationService<StaticFilesServerConfiguration> configuration)
    {
        _metrics = metrics;
        _logger = logger;
        _services = services;
        _configuration = configuration;
    }

    public async Task CleanUpTask(CancellationToken ct)
    {
        _logger.LogInformation("Starting periodic cleanup task");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                using var dbContext = scope.ServiceProvider.GetService<MareDbContext>()!;

                bool useColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);

                if (useColdStorage)
                {
                    var coldStorageDir = _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory));

                    DirectoryInfo dirColdStorage = new(coldStorageDir);
                    var allFilesInColdStorageDir = dirColdStorage.GetFiles("*", SearchOption.AllDirectories).ToList();

                    var coldStorageRetention = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.ColdStorageUnusedFileRetentionPeriodInDays), 60);
                    var coldStorageSize = _configuration.GetValueOrDefault<double>(nameof(StaticFilesServerConfiguration.ColdStorageSizeHardLimitInGiB), -1);

                    // clean up cold storage
                    var remainingColdFiles = await CleanUpOutdatedFiles(coldStorageDir, allFilesInColdStorageDir, coldStorageRetention, forcedDeletionAfterHours: -1,
                        isColdStorage: true, deleteFromDb: true,
                        dbContext, ct).ConfigureAwait(false);
                    var finalRemainingColdFiles = CleanUpFilesBeyondSizeLimit(remainingColdFiles, coldStorageSize,
                        isColdStorage: true, deleteFromDb: true,
                        dbContext, ct);

                    _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalSizeColdStorage, finalRemainingColdFiles.Sum(f => f.Length));
                    _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalColdStorage, finalRemainingColdFiles.Count);
                }

                var hotStorageDir = _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
                DirectoryInfo dirHotStorage = new(hotStorageDir);
                var allFilesInHotStorage = dirHotStorage.GetFiles("*", SearchOption.AllDirectories).ToList();

                var unusedRetention = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UnusedFileRetentionPeriodInDays), 14);
                var forcedDeletionAfterHours = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.ForcedDeletionOfFilesAfterHours), -1);
                var sizeLimit = _configuration.GetValueOrDefault<double>(nameof(StaticFilesServerConfiguration.CacheSizeHardLimitInGiB), -1);

                var remainingHotFiles = await CleanUpOutdatedFiles(hotStorageDir, allFilesInHotStorage, unusedRetention, forcedDeletionAfterHours,
                    isColdStorage: false, deleteFromDb: !useColdStorage,
                    dbContext, ct).ConfigureAwait(false);

                var finalRemainingHotFiles = CleanUpFilesBeyondSizeLimit(remainingHotFiles, sizeLimit,
                    isColdStorage: false, deleteFromDb: !useColdStorage,
                    dbContext, ct);

                CleanUpStuckUploads(dbContext);

                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

                _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotalSize, finalRemainingHotFiles.Sum(f => { try { return f.Length; } catch { return 0; } }));
                _metrics.SetGaugeTo(MetricsAPI.GaugeFilesTotal, finalRemainingHotFiles.Count);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error during cleanup task");
            }

            var cleanupCheckMinutes = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.CleanupCheckInMinutes), 15);
            var now = DateTime.Now;
            TimeOnly currentTime = new(now.Hour, now.Minute, now.Second);
            TimeOnly futureTime = new(now.Hour, now.Minute - now.Minute % cleanupCheckMinutes, 0);
            var span = futureTime.AddMinutes(cleanupCheckMinutes) - currentTime;

            _logger.LogInformation("File Cleanup Complete, next run at {date}", now.Add(span));
            await Task.Delay(span, ct).ConfigureAwait(false);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup Service started");

        _cleanupCts = new();

        _ = CleanUpTask(_cleanupCts.Token);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts.Cancel();

        return Task.CompletedTask;
    }

    private List<FileInfo> CleanUpFilesBeyondSizeLimit(List<FileInfo> files, double sizeLimit, bool isColdStorage, bool deleteFromDb, MareDbContext dbContext, CancellationToken ct)
    {
        if (sizeLimit <= 0)
        {
            return [];
        }

        try
        {
            _logger.LogInformation("Cleaning up files beyond the cache size limit of {cacheSizeLimit} GiB", sizeLimit);
            var allLocalFiles = files
                .OrderBy(f => f.LastAccessTimeUtc).ToList();
            var totalCacheSizeInBytes = allLocalFiles.Sum(s => s.Length);
            long cacheSizeLimitInBytes = (long)ByteSize.FromGibiBytes(sizeLimit).Bytes;
            while (totalCacheSizeInBytes > cacheSizeLimitInBytes && allLocalFiles.Count != 0 && !ct.IsCancellationRequested)
            {
                var oldestFile = allLocalFiles[0];
                allLocalFiles.RemoveAt(0);
                totalCacheSizeInBytes -= oldestFile.Length;
                _logger.LogInformation("Deleting {oldestFile} with size {size}MiB", oldestFile.FullName, ByteSize.FromBytes(oldestFile.Length).MebiBytes);
                oldestFile.Delete();
                FileCache f = new() { Hash = oldestFile.Name.ToUpperInvariant() };
                if (deleteFromDb)
                    dbContext.Entry(f).State = EntityState.Deleted;
            }

            return allLocalFiles;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during cache size limit cleanup");
        }

        return [];
    }

    private List<FileInfo> CleanUpOrphanedFiles(List<FileCache> allFiles, List<FileInfo> allPhysicalFiles, CancellationToken ct)
    {
        var allFilesHashes = new HashSet<string>(allFiles.Select(a => a.Hash.ToUpperInvariant()), StringComparer.Ordinal);
        foreach (var file in allPhysicalFiles.ToList())
        {
            if (!allFilesHashes.Contains(file.Name.ToUpperInvariant()))
            {
                _metrics.DecGauge(MetricsAPI.GaugeFilesTotalSize, file.Length);
                _metrics.DecGauge(MetricsAPI.GaugeFilesTotal);
                file.Delete();
                _logger.LogInformation("File not in DB, deleting: {fileName}", file.Name);
                allPhysicalFiles.Remove(file);
            }

            ct.ThrowIfCancellationRequested();
        }

        return allPhysicalFiles;
    }

    private async Task<List<FileInfo>> CleanUpOutdatedFiles(string dir, List<FileInfo> allFilesInDir, int unusedRetention, int forcedDeletionAfterHours,
        bool isColdStorage, bool deleteFromDb, MareDbContext dbContext, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Cleaning up files older than {filesOlderThanDays} days", unusedRetention);
            if (forcedDeletionAfterHours > 0)
            {
                _logger.LogInformation("Cleaning up files written to longer than {hours}h ago", forcedDeletionAfterHours);
            }

            // clean up files in DB but not on disk or last access is expired
            var prevTime = DateTime.Now.Subtract(TimeSpan.FromDays(unusedRetention));
            var prevTimeForcedDeletion = DateTime.Now.Subtract(TimeSpan.FromHours(forcedDeletionAfterHours));
            List<FileCache> allDbFiles = await dbContext.Files.ToListAsync(ct).ConfigureAwait(false);
            List<string> removedFileHashes = new List<string>();
            int fileCounter = 0;

            foreach (var fileCache in allDbFiles.Where(f => f.Uploaded))
            {
                bool deleteCurrentFile = false;
                var file = FilePathUtil.GetFileInfoForHash(dir, fileCache.Hash);
                if (file == null)
                {
                    _logger.LogInformation("File does not exist anymore: {fileName}", fileCache.Hash);
                    deleteCurrentFile = true;
                }
                else if (file != null && file.LastAccessTime < prevTime)
                {
                    _logger.LogInformation("File outdated: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                    deleteCurrentFile = true;
                }
                else if (file != null && forcedDeletionAfterHours > 0 && file.LastWriteTime < prevTimeForcedDeletion)
                {
                    _logger.LogInformation("File forcefully deleted: {fileName}, {fileSize}MiB", file.Name, ByteSize.FromBytes(file.Length).MebiBytes);
                    deleteCurrentFile = true;
                }

                // do actual deletion of file and remove also from db if needed
                if (deleteCurrentFile)
                {
                    if (file != null) file.Delete();

                    removedFileHashes.Add(fileCache.Hash);

                    if (deleteFromDb)
                        dbContext.Files.Remove(fileCache);
                }

                // only used if file in db has no size for whatever reason
                if (!deleteCurrentFile && file != null && fileCache.Size == 0)
                {
                    _logger.LogInformation("Setting File Size of " + fileCache.Hash + " to " + file.Length);
                    fileCache.Size = file.Length;
                    // commit every 1000 files to db
                    if (fileCounter % 1000 == 0)
                        await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }

                fileCounter++;

                ct.ThrowIfCancellationRequested();
            }

            // clean up files that are on disk but not in DB for some reason
            return CleanUpOrphanedFiles(allDbFiles, allFilesInDir.Where(c => !removedFileHashes.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during file cleanup of old files");
        }

        return [];
    }

    private void CleanUpStuckUploads(MareDbContext dbContext)
    {
        var pastTime = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(20));
        var stuckUploads = dbContext.Files.Where(f => !f.Uploaded && f.UploadDate < pastTime);
        dbContext.Files.RemoveRange(stuckUploads);
    }
}