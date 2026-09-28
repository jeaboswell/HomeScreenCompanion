using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Users;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace HomeScreenCompanion
{
    public class HomeSectionSyncTask : IScheduledTask
    {
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;

        public static string LastSyncTime { get; private set; } = "Never";
        public static bool IsRunning { get; private set; } = false;
        public static string LastSyncResult { get; private set; } = "";
        public static int LastSectionsCopied { get; private set; } = 0;
        public static List<string> ExecutionLog { get; } = new List<string>();
        public static DateTime? LastStartedUtc { get; private set; }
        private RunLog _log;

        private static void PersistLog() => LogStore.Save(LogStore.HomeScreen, ExecutionLog, LastSyncResult, LastStartedUtc,
            new Dictionary<string, string>
            {
                ["LastSyncTime"] = LastSyncTime,
                ["SectionsCopied"] = LastSectionsCopied.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

        // Brings back the last run's log and status after a server restart.
        internal static void RestoreLog()
        {
            var saved = LogStore.Load(LogStore.HomeScreen);
            if (saved == null || IsRunning) return;
            lock (ExecutionLog)
            {
                if (ExecutionLog.Count > 0) return;
                ExecutionLog.AddRange(saved.Lines ?? new List<string>());
            }
            LastSyncResult = LogStore.RestoredStatus(saved.Status);
            LastStartedUtc = saved.StartedUtc;
            var extra = saved.Extra ?? new Dictionary<string, string>();
            if (extra.TryGetValue("LastSyncTime", out var t) && !string.IsNullOrEmpty(t)) LastSyncTime = t;
            if (extra.TryGetValue("SectionsCopied", out var c) && int.TryParse(c, out var n)) LastSectionsCopied = n;
        }

        public HomeSectionSyncTask(IUserManager userManager, ILogManager logManager)
        {
            _userManager = userManager;
            _logger = logManager.GetLogger("HomeScreenCompanion_HSC");
            _log = new RunLog(ExecutionLog, _logger, "[Home Screen]", false);
        }

        public string Key => "HomeSectionSyncTask";
        public string Name => "Home Screen Sync";
        public string Description => "Syncs home screen sections from a source user to all selected target users.";
        public string Category => "Home Screen Companion";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            IsRunning = true;
            lock (ExecutionLog) { ExecutionLog.Clear(); }
            LastStartedUtc = DateTime.UtcNow;
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return Task.CompletedTask;

                bool debug = config.ExtendedConsoleOutput;
                _log = new RunLog(ExecutionLog, _logger, "[Home Screen]", debug);
                var startTime = DateTime.Now;
                _log.Rule();
                _log.Info($"Home Screen Sync  ·  {startTime:yyyy-MM-dd HH:mm}");
                _log.Rule();

                if (!config.HomeSyncEnabled)
                {
                    _log.Skip("Home Screen Sync is disabled in Settings — nothing to do");
                    return Task.CompletedTask;
                }

                if (string.IsNullOrWhiteSpace(config.HomeSyncSourceUserId))
                {
                    _log.Warn("No master user is selected in Settings — nothing to sync");
                    LastSyncResult = "No source user configured.";
                    return Task.CompletedTask;
                }

                if (config.HomeSyncTargetUserIds == null || config.HomeSyncTargetUserIds.Count == 0)
                {
                    _log.Warn("No target users are selected in Settings — nothing to sync");
                    LastSyncResult = "No target users configured.";
                    return Task.CompletedTask;
                }

                var sourceInternalId = _userManager.GetInternalId(config.HomeSyncSourceUserId);
                string sourceName = Guid.TryParse(config.HomeSyncSourceUserId, out var srcGuid)
                    ? (_userManager.GetUserById(srcGuid)?.Name ?? config.HomeSyncSourceUserId)
                    : config.HomeSyncSourceUserId;
                progress.Report(5);

                var sourceSections = _userManager.GetHomeSections(sourceInternalId, cancellationToken);

                if (sourceSections?.Sections == null || sourceSections.Sections.Length == 0)
                {
                    _log.Warn($"Master user {sourceName} has no home screen sections — nothing to copy");
                    LastSyncResult = "Source user has no home sections.";
                    return Task.CompletedTask;
                }

                _log.Info($"  Master: {sourceName}  ·  {RunLog.Plural(sourceSections.Sections.Length, "section")}");
                _log.Section("Master sections");
                foreach (var s in sourceSections.Sections)
                    _log.Debug($"  [{s.SectionType}] \"{s.CustomName ?? s.Name}\"");

                progress.Report(20);
                int totalCopied = 0, failedUsers = 0;
                int targetCount = config.HomeSyncTargetUserIds.Count;
                _log.Blank();
                _log.Info("» Copying home screen");

                for (int i = 0; i < targetCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var targetIdStr = config.HomeSyncTargetUserIds[i];
                    string targetName = Guid.TryParse(targetIdStr, out var tgtGuid)
                        ? (_userManager.GetUserById(tgtGuid)?.Name ?? targetIdStr)
                        : targetIdStr;
                    try
                    {
                        var targetInternalId = _userManager.GetInternalId(targetIdStr);

                        var existing = _userManager.GetHomeSections(targetInternalId, cancellationToken);
                        if (existing?.Sections?.Length > 0)
                        {
                            _log.Debug($"  {targetName}: replacing {existing.Sections.Length} existing sections");
                            var idsToDelete = existing.Sections
                                .Where(s => !string.IsNullOrEmpty(s.Id))
                                .Select(s => s.Id)
                                .ToArray();
                            if (idsToDelete.Length > 0)
                                _userManager.DeleteHomeSections(targetInternalId, idsToDelete, cancellationToken);
                        }

                        foreach (var section in sourceSections.Sections)
                        {
                            _log.Debug($"  {targetName}: + [{section.SectionType}] \"{section.CustomName ?? section.Name}\"");
                            _userManager.AddHomeSection(targetInternalId, CopySection(section), cancellationToken);
                            totalCopied++;
                        }

                        _log.Ok($"{targetName}: {RunLog.Plural(sourceSections.Sections.Length, "section")} copied");
                    }
                    catch (Exception ex)
                    {
                        failedUsers++;
                        _log.Error($"{targetName}: could not copy home screen — {ex.Message}");
                    }

                    progress.Report(20 + (int)(80.0 * (i + 1) / targetCount));
                }

                if (config.HomeSyncLibraryOrder)
                {
                    _log.Blank();
                    _log.Info("» Library order");
                    try
                    {
                        var sourceUser = _userManager.GetUserById(config.HomeSyncSourceUserId);
                        if (sourceUser != null)
                        {
                            var sourceConf = _userManager.GetUserConfiguration(sourceUser);
                            if (sourceConf?.OrderedViews != null && sourceConf.OrderedViews.Length > 0)
                            {
                                _log.Debug($"  Master order: [{string.Join(", ", sourceConf.OrderedViews)}]");
                                foreach (var targetIdStr in config.HomeSyncTargetUserIds)
                                {
                                    string targetName2 = Guid.TryParse(targetIdStr, out var tgtGuid2)
                                        ? (_userManager.GetUserById(tgtGuid2)?.Name ?? targetIdStr)
                                        : targetIdStr;
                                    try
                                    {
                                        var targetUser = _userManager.GetUserById(targetIdStr);
                                        if (targetUser == null) continue;
                                        var targetConf = _userManager.GetUserConfiguration(targetUser);
                                        if (targetConf == null) continue;
                                        targetConf.OrderedViews = sourceConf.OrderedViews;
                                        _userManager.UpdateConfiguration(_userManager.GetInternalId(targetIdStr), targetConf);
                                        _log.Ok($"{targetName2}: library order copied");
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.Warn($"{targetName2}: library order could not be copied — {ex.Message}");
                                    }
                                }
                            }
                            else
                            {
                                _log.Skip($"{sourceName} has no custom library order — nothing to copy");
                            }
                        }
                        else
                        {
                            _log.Warn("Master user could not be loaded — library order not copied");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"Library order sync failed: {ex.Message}");
                    }
                }

                LastSectionsCopied = totalCopied;
                LastSyncTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                LastSyncResult = $"OK — {totalCopied} section(s) copied to {targetCount} user(s).";
                _log.Rule();
                _log.Info("Summary");
                _log.Info($"  Sections:      {totalCopied} copied to {RunLog.Plural(targetCount - failedUsers, "user")}{(failedUsers > 0 ? $", {RunLog.Plural(failedUsers, "user")} failed" : "")}");
                _log.Info($"  Done in {RunLog.Elapsed(DateTime.Now - startTime)}  ·  {(failedUsers > 0 ? "✖ Completed with " + RunLog.Plural(failedUsers, "error") : "✔ Completed")}");
                _log.Rule();
                progress.Report(100);
            }
            catch (OperationCanceledException)
            {
                LastSyncResult = "Cancelled.";
                _log.Warn("Sync was cancelled");
            }
            catch (Exception ex)
            {
                LastSyncResult = $"Error: {ex.Message}";
                _log.Error($"Sync aborted: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
                PersistLog();
            }

            return Task.CompletedTask;
        }

        private static ContentSection CopySection(ContentSection source)
        {
            var copy = new ContentSection();
            foreach (var prop in typeof(ContentSection).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name == "Id") continue;
                if (prop.CanRead && prop.CanWrite)
                    prop.SetValue(copy, prop.GetValue(source));
            }
            return copy;
        }
    }
}
