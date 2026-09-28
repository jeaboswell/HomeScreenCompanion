using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Users;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomeScreenCompanion
{
    public class TopListSyncTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserViewManager _userViewManager;
        private readonly IUserManager _userManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;

        public static List<string> ExecutionLog { get; } = new List<string>();
        public static bool IsRunning { get; private set; } = false;
        public static DateTime? LastStartedUtc { get; private set; }
        private static RunLog _log = new RunLog(ExecutionLog, null, "", false);
        public static string LastRunStatus { get; private set; } = "Never";

        public TopListSyncTask(ILibraryManager libraryManager, IUserViewManager userViewManager, IUserManager userManager, IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _userViewManager = userViewManager;
            _userManager = userManager;
            _jsonSerializer = jsonSerializer;
            _logger = logManager.GetLogger("HomeScreenCompanion_Access");
        }

        public string Key => "TopListSyncTask";
        public string Name => "Top-list section sync";
        public string Description => "Run after manually creating a new library. Ensures each top-list home section only shows items from its own library, and that regular home sections never include top-list libraries.";
        public string Category => "Home Screen Companion";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            IsRunning = true;
            try
            {
                var (_, msg) = SyncAll(_libraryManager, _userViewManager, _userManager, _jsonSerializer, _logger, cancellationToken);
                LastRunStatus = msg;
            }
            catch (Exception ex)
            {
                LastRunStatus = $"Error: {ex.Message}";
                _log.Error($"Top-list sync aborted: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
            }
            return Task.CompletedTask;
        }

        private static string UserLabel(IUserManager userManager, string userId)
        {
            try
            {
                if (Guid.TryParse(userId, out var guid))
                    return userManager.GetUserById(guid)?.Name ?? userId;
            }
            catch { }
            return userId;
        }

        // The ids of every entry Emby's own section editor lists under "Libraries" for this user.
        // An items-section has no "include only" field — the selection is stored as ExcludedFolders
        // (every view NOT ticked) — so a view missing here stays ticked. The editor loads
        // Users/{id}/Views?IncludeHidden=true&AllowDynamicChildren=false, which also contains
        // libraries the user hid from My Media and channels; mirror that exactly. Both the GUID
        // and the internal id of each view are returned so either form matches.
        internal static List<string> GetSectionEditorViewIds(IUserViewManager userViewManager, IUserManager userManager, string userId)
        {
            var ids = new List<string>();
            try
            {
                var uid = userManager.GetInternalId(userId);
                Guid.TryParse(userId, out var userGuid);
                // Reflect through the interface type to handle explicit interface implementations.
                var ifMethod = typeof(IUserViewManager).GetMethod("GetUserViews");
                if (ifMethod == null) return ids;
                var queryParams = ifMethod.GetParameters();
                object queryArg = null;
                if (queryParams.Length > 0)
                {
                    try
                    {
                        var qt = queryParams[0].ParameterType;
                        queryArg = Activator.CreateInstance(qt);
                        var uidProp = qt.GetProperty("UserId");
                        if (uidProp?.PropertyType == typeof(long))
                            uidProp.SetValue(queryArg, uid);
                        else
                            uidProp?.SetValue(queryArg, userGuid);
                        SetBool(qt, queryArg, "IncludeHidden", true);
                        SetBool(qt, queryArg, "AllowDynamicChildren", false);
                        SetBool(qt, queryArg, "IncludeExternalContent", true);
                    }
                    catch { queryArg = null; }
                }
                var result = ifMethod.Invoke(userViewManager, new[] { queryArg });
                if (result is System.Collections.IEnumerable views)
                    foreach (var v in views)
                    {
                        if (v is BaseItem bi)
                        {
                            if (bi.Id != Guid.Empty) ids.Add(bi.Id.ToString("N"));
                            if (bi.InternalId > 0) ids.Add(bi.InternalId.ToString());
                            continue;
                        }
                        var idProp = v?.GetType().GetProperty("Id");
                        if (idProp?.GetValue(v) is Guid vid && vid != Guid.Empty)
                            ids.Add(vid.ToString("N"));
                    }
            }
            catch { }
            return ids.Select(s => s.ToLowerInvariant()).Distinct().ToList();
        }

        private static void SetBool(Type t, object target, string name, bool value)
        {
            var p = t.GetProperty(name);
            if (p == null || !p.CanWrite) return;
            if (p.PropertyType == typeof(bool) || p.PropertyType == typeof(bool?))
                p.SetValue(target, value);
        }

        // All id forms of a top-list's own library (as stored, GUID, internal id), lowercase, so
        // it is never put on its own exclusion list whichever form a view list reports.
        internal static HashSet<string> OwnLibraryIds(string libId, ILibraryManager libraryManager)
        {
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(libId)) return own;
            var raw = libId.Trim().ToLowerInvariant();
            own.Add(raw);
            own.Add(raw.Replace("-", ""));
            try
            {
                BaseItem lib = null;
                if (long.TryParse(raw, out var internalId))
                    lib = libraryManager.GetItemById(internalId);
                else if (Guid.TryParse(raw, out var g))
                    lib = libraryManager.GetItemById(g);
                if (lib != null)
                {
                    own.Add(lib.Id.ToString("N"));
                    own.Add(lib.InternalId.ToString());
                }
            }
            catch { }
            return own;
        }

        internal static (int updated, string message) SyncAll(
            ILibraryManager libraryManager,
            IUserViewManager userViewManager,
            IUserManager userManager,
            IJsonSerializer jsonSerializer,
            ILogger logger,
            CancellationToken cancellationToken,
            RunLog? log = null)
        {
            var config = Plugin.Instance?.Configuration;
            var startTime = DateTime.Now;
            // When called from the main sync, lines go into that run's log (under its "» Top-lists"
            // heading). On its own (scheduled task / UI button) this task keeps its own log.
            bool standalone = log == null;
            if (standalone)
            {
                lock (ExecutionLog) { ExecutionLog.Clear(); }
                LastStartedUtc = DateTime.UtcNow;
                _log = new RunLog(ExecutionLog, null, "", config?.ExtendedConsoleOutput ?? false);
                _log.Rule();
                _log.Info($"Top-list section sync  ·  {startTime:yyyy-MM-dd HH:mm}");
                _log.Rule();
            }
            else
            {
                _log = log!;
            }
            if (config == null) { _log.Error("Plugin configuration could not be loaded"); return (0, "No config."); }

            var topLists = config.TopLists ?? new List<TopListHomeSection>();
            if (topLists.Count == 0) { _log.Skip("No top-lists configured — nothing to do"); return (0, "No top-lists configured."); }

            if (standalone) { _log.Blank(); _log.Info($"» Top-list sections  ·  {RunLog.Plural(topLists.Count, "top-list")}"); }
            int totalUpdated = 0, totalErrors = 0;

            // Collect all configured top-list library IDs so each top-list always excludes
            // its siblings, even if their libraries haven't been discovered via GetVirtualFolders yet.
            var allTlLibIds = topLists
                .Where(t => !string.IsNullOrEmpty(t.HomeSectionLibraryId) && t.HomeSectionLibraryId != "auto")
                .Select(t => t.HomeSectionLibraryId.Trim().ToLowerInvariant())
                .Distinct().ToList();

            foreach (var tl in topLists)
            {
                string tlName = tl.TagName ?? "(unnamed)";
                _log.Section($"Top-list '{tlName}'");
                if (string.IsNullOrEmpty(tl.HomeSectionLibraryId) || tl.HomeSectionLibraryId == "auto") { _log.Skip($"Top-list '{tlName}': skipped — no library has been created for it yet"); continue; }
                int tlUpdated = 0, tlRemoved = 0;

                var ownIds = OwnLibraryIds(tl.HomeSectionLibraryId, libraryManager);
                var safeTag = new string((tl.TagName ?? "").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                var sectionMarker = "hsc__tl__" + safeTag;

                // Remove home sections for users no longer assigned to this top-list
                if (tl.HomeSectionTracked != null)
                {
                    var assignedNorm = new HashSet<string>(
                        (tl.HomeSectionUserIds ?? new List<string>()).Select(id => id.Replace("-", "").ToLowerInvariant()),
                        StringComparer.OrdinalIgnoreCase);

                    var unassignedTracked = tl.HomeSectionTracked
                        .Where(t => !string.IsNullOrEmpty(t.UserId) && !string.IsNullOrEmpty(t.SectionId))
                        .Where(t => !assignedNorm.Contains(t.UserId.Replace("-", "").ToLowerInvariant()))
                        .ToList();

                    foreach (var t in unassignedTracked)
                    {
                        try
                        {
                            var uid = userManager.GetInternalId(t.UserId);
                            userManager.DeleteHomeSections(uid, new[] { t.SectionId }, cancellationToken);
                            tl.HomeSectionTracked.Remove(t);
                            tlRemoved++;
                            _log.Debug($"  {UserLabel(userManager, t.UserId)}: section removed (user no longer selected)");
                        }
                        catch (Exception ex) { totalErrors++; _log.Warn($"Top-list '{tlName}': could not remove section for {UserLabel(userManager, t.UserId)} — {ex.Message}"); }
                    }
                }

                foreach (var tracking in tl.HomeSectionTracked ?? new List<HomeSectionTracking>())
                {
                    if (string.IsNullOrEmpty(tracking.UserId) || string.IsNullOrEmpty(tracking.SectionId)) continue;
                    try
                    {
                        var uid = userManager.GetInternalId(tracking.UserId);
                        var sections = userManager.GetHomeSections(uid, cancellationToken)?.Sections
                            ?? Array.Empty<ContentSection>();

                        var owned = sections.FirstOrDefault(s => s.Id == tracking.SectionId);
                        if (owned == null) continue;

                        // Every view this user's section editor lists (hidden libraries, channels
                        // and Live TV included).
                        var allViewIds = GetSectionEditorViewIds(userViewManager, userManager, tracking.UserId);

                        // Always also include virtual folder IDs (ensures regular libraries are covered)
                        foreach (var f in libraryManager.GetVirtualFolders())
                            if (!string.IsNullOrEmpty(f.ItemId))
                                allViewIds.Add(f.ItemId.Trim().ToLowerInvariant());

                        allViewIds = allViewIds.Distinct().ToList();

                        // Final fallback: only virtual folders if nothing else worked
                        if (allViewIds.Count == 0)
                        {
                            allViewIds = libraryManager.GetVirtualFolders()
                                .Where(f => !string.IsNullOrEmpty(f.ItemId))
                                .Select(f => f.ItemId.Trim().ToLowerInvariant())
                                .ToList();
                        }

                        var settingsDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            if (!string.IsNullOrEmpty(tl.HomeSectionSettings) && tl.HomeSectionSettings != "{}")
                                settingsDict = jsonSerializer.DeserializeFromString<Dictionary<string, string>>(tl.HomeSectionSettings) ?? settingsDict;
                        }
                        catch { }

                        // Merge: union of newly discovered IDs and any previously stored exclusions
                        // (preserves IDs like Live TV that are only captured at initial setup time).
                        var storedExclude = (settingsDict.TryGetValue("_queryExcludeViewIds", out var storedEv) ? storedEv : "")
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0);
                        var mergedExcludeIds = allViewIds
                            .Concat(storedExclude)
                            .Concat(allTlLibIds)
                            .Where(id => !ownIds.Contains(id))
                            .Distinct().ToList();
                        var excludeStr = string.Join(",", mergedExcludeIds);

                        settingsDict["_queryExcludeViewIds"] = excludeStr;
                        settingsDict["ExcludedFolders"] = excludeStr;
                        tl.HomeSectionSettings = jsonSerializer.SerializeToString(settingsDict);

                        var updated = HomeScreenCompanionTask.BuildContentSection(
                            jsonSerializer, settingsDict, tl.HomeSectionLibraryId, owned);
                        typeof(ContentSection).GetProperty("Id")?.SetValue(updated, owned.Id);
                        userManager.UpdateHomeSection(uid, updated, cancellationToken);
                        _log.Debug($"  {UserLabel(userManager, tracking.UserId)}: section updated");
                        totalUpdated++;
                        tlUpdated++;
                    }
                    catch (Exception ex) { totalErrors++; _log.Warn($"Top-list '{tlName}': section could not be updated for {UserLabel(userManager, tracking.UserId)} — {ex.Message}"); }
                }
                if (tlUpdated > 0 || tlRemoved > 0)
                    _log.Ok($"Top-list '{tlName}': {RunLog.Plural(tlUpdated, "section")} updated{(tlRemoved > 0 ? $", {tlRemoved} removed" : "")}");
                else
                    _log.Skip($"Top-list '{tlName}': no home sections to update");
            }

            // Ensure ALL top-list libraries are excluded from every TAG items-type section.
            // This makes the task a complete maintenance sweep, not just a top-list-section refresh.
            var allTopListLibIds = topLists
                .Where(t => !string.IsNullOrEmpty(t.HomeSectionLibraryId) && t.HomeSectionLibraryId != "auto")
                .Select(t => t.HomeSectionLibraryId.Trim().ToLowerInvariant())
                .Distinct().ToList();

            if (allTopListLibIds.Count > 0)
            {
                foreach (var tc in config.Tags ?? new List<TagConfig>())
                {
                    if (!tc.EnableHomeSection) continue;

                    var tcSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        if (!string.IsNullOrEmpty(tc.HomeSectionSettings) && tc.HomeSectionSettings != "{}")
                            tcSettings = jsonSerializer.DeserializeFromString<Dictionary<string, string>>(tc.HomeSectionSettings) ?? tcSettings;
                    }
                    catch { }

                    tcSettings.TryGetValue("SectionType", out var tcSt);
                    if (tcSt == "boxset") continue;

                    var existingExcluded = (tcSettings.TryGetValue("_queryExcludeViewIds", out var tcEv) ? tcEv : "")
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();

                    var missing = allTopListLibIds
                        .Where(id => !existingExcluded.Contains(id, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    if (missing.Count == 0) continue;

                    existingExcluded.AddRange(missing);
                    existingExcluded = existingExcluded.Distinct().ToList();
                    var tcExcStr = string.Join(",", existingExcluded);
                    tcSettings["_queryExcludeViewIds"] = tcExcStr;
                    tcSettings["ExcludedFolders"] = tcExcStr;
                    tc.HomeSectionSettings = jsonSerializer.SerializeToString(tcSettings);

                    if (!string.IsNullOrEmpty(tc.Tag))
                    {
                        try
                        {
                            var tagItem = libraryManager.GetItemList(new InternalItemsQuery
                            {
                                IncludeItemTypes = new[] { "Tag" },
                                Name = tc.Tag,
                                Recursive = true
                            }, cancellationToken).FirstOrDefault();
                            if (tagItem != null) tcSettings["_queryTagId"] = tagItem.InternalId.ToString();
                        }
                        catch { }
                    }

                    var tcSafeTag = new string((tc.Name ?? tc.Tag ?? "").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                    var tcMarker = "hsc__" + tcSafeTag;

                    var realTracked = (tc.HomeSectionTracked ?? new List<HomeSectionTracking>())
                        .Where(t => !string.IsNullOrEmpty(t.SectionId) && !t.SectionId.StartsWith("hsc__"))
                        .ToList();

                    foreach (var tracking in realTracked)
                    {
                        try
                        {
                            var uid = userManager.GetInternalId(tracking.UserId);
                            var secs = userManager.GetHomeSections(uid, cancellationToken)?.Sections ?? Array.Empty<ContentSection>();
                            var owned = secs.FirstOrDefault(s => s.Id == tracking.SectionId);
                            if (owned == null) continue;
                            var updatedSec = HomeScreenCompanionTask.BuildContentSection(jsonSerializer, tcSettings, string.Empty, owned);
                            typeof(ContentSection).GetProperty("Id")?.SetValue(updatedSec, owned.Id);
                            userManager.UpdateHomeSection(uid, updatedSec, cancellationToken);
                            totalUpdated++;
                        }
                        catch { }
                    }
                }
            }

            // Aggressive: also keep all untracked, non-library-scoped sections up to date.
            totalUpdated += HomeScreenCompanionTask.UpdateUntrackedSections(
                jsonSerializer, userManager, config, allTopListLibIds, cancellationToken);

            GrantTopListLibraryAccess(topLists, userManager, libraryManager, logger);

            Plugin.Instance?.SaveConfiguration();
            var summary = $"Updated {totalUpdated} section(s) across {topLists.Count} top-list(s).";
            _log.Info($"    {RunLog.Plural(totalUpdated, "section")} updated across {RunLog.Plural(topLists.Count, "top-list")}{(totalErrors > 0 ? $", {RunLog.Plural(totalErrors, "error")}" : "")}  ·  {RunLog.Elapsed(DateTime.Now - startTime)}");
            return (totalUpdated, summary);
        }

        internal static void GrantTopListLibraryAccess(List<TopListHomeSection> topLists, IUserManager userManager, ILibraryManager libraryManager, ILogger logger)
        {
            dynamic mgr = userManager;

            foreach (var tl in topLists)
            {
                if (string.IsNullOrEmpty(tl.HomeSectionLibraryId) || tl.HomeSectionLibraryId == "auto") continue;
                if (tl.HomeSectionUserIds == null || tl.HomeSectionUserIds.Count == 0) continue;

                var rawLibId = tl.HomeSectionLibraryId.Trim();

                // HomeSectionLibraryId may be stored as InternalId (numeric) rather than GUID.
                // EnabledFolders in Emby user policy requires the GUID (32 hex chars, no dashes).
                // If numeric, look up the GUID via the library manager.
                var libGuid = ResolveLibraryGuid(rawLibId, libraryManager);
                if (string.IsNullOrEmpty(libGuid))
                {
                    logger.Warn($"[Access] Could not resolve GUID for library '{rawLibId}' — skipping.");
                    continue;
                }

                var assignedNormIds = new HashSet<string>(
                    tl.HomeSectionUserIds.Select(id => id.Replace("-", "").ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);

                var allUsers = userManager.GetUserList(new UserQuery { IsDisabled = false }).ToList();
                var matchedUsers = allUsers
                    .Where(u => assignedNormIds.Contains(u.Id.ToString().Replace("-", "").ToLowerInvariant()))
                    .ToList();

                // Grant: ensure assigned users have access
                foreach (var user in matchedUsers)
                {
                    try
                    {
                        var uid = userManager.GetInternalId(user.Id.ToString());
                        dynamic policy = GetPolicy(mgr, user, uid);
                        if (policy == null) continue;

                        bool enableAll;
                        try { enableAll = (bool)policy.EnableAllFolders; } catch { enableAll = false; }
                        if (enableAll) continue;

                        string[] folders;
                        try { folders = (string[])policy.EnabledFolders ?? Array.Empty<string>(); } catch { folders = Array.Empty<string>(); }

                        if (folders.Any(f => string.Equals(f.Replace("-", ""), libGuid, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // Remove any stale InternalId entry and add the GUID
                        var cleanedFolders = folders
                            .Where(f => !string.Equals(f.Replace("-", ""), rawLibId.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        policy.EnabledFolders = cleanedFolders.Concat(new[] { libGuid }).ToArray();
                        UpdatePolicy(mgr, user, uid, policy);
                        logger.Info($"[Access] Granted library '{tl.TagName}' to '{user.Name}'.");
                    }
                    catch (Exception ex) { logger.Error($"[Access] Grant error for '{user.Name}': {ex.GetBaseException().Message}"); }
                }

                // Revoke: remove access from users NOT assigned to this top-list
                var rawNorm = rawLibId.Replace("-", "").ToLowerInvariant();
                foreach (var user in allUsers.Where(u => !assignedNormIds.Contains(u.Id.ToString().Replace("-", "").ToLowerInvariant())))
                {
                    try
                    {
                        var uid = userManager.GetInternalId(user.Id.ToString());
                        dynamic policy = GetPolicy(mgr, user, uid);
                        if (policy == null) continue;

                        bool enableAll;
                        try { enableAll = (bool)policy.EnableAllFolders; } catch { enableAll = false; }
                        if (enableAll) continue;

                        string[] folders;
                        try { folders = (string[])policy.EnabledFolders ?? Array.Empty<string>(); } catch { folders = Array.Empty<string>(); }

                        var hasEntry = folders.Any(f =>
                            string.Equals(f.Replace("-", ""), libGuid, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(f.Replace("-", ""), rawNorm, StringComparison.OrdinalIgnoreCase));
                        if (!hasEntry) continue;

                        policy.EnabledFolders = folders
                            .Where(f =>
                                !string.Equals(f.Replace("-", ""), libGuid, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(f.Replace("-", ""), rawNorm, StringComparison.OrdinalIgnoreCase))
                            .ToArray();

                        UpdatePolicy(mgr, user, uid, policy);
                        logger.Info($"[Access] Revoked library '{tl.TagName}' from '{user.Name}'.");
                    }
                    catch (Exception ex) { logger.Error($"[Access] Revoke error for '{user.Name}': {ex.GetBaseException().Message}"); }
                }
            }
        }

        private static string ResolveLibraryGuid(string libId, ILibraryManager libraryManager)
        {
            if (string.IsNullOrEmpty(libId)) return null;
            var norm = libId.Replace("-", "").ToLowerInvariant();

            // If it's already a 32-char hex string → it's a GUID
            if (norm.Length == 32 && norm.All(c => "0123456789abcdef".IndexOf(c) >= 0))
                return norm;

            // Otherwise treat as InternalId — find the matching CollectionFolder
            if (long.TryParse(libId.Trim(), out long internalId))
            {
                try
                {
                    var items = libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "CollectionFolder" }
                    });
                    var match = items.FirstOrDefault(it => it.InternalId == internalId);
                    if (match != null)
                        return match.Id.ToString("N").ToLowerInvariant();
                }
                catch { }
            }

            return null;
        }

        internal static object GetPolicy(dynamic mgr, BaseItem user, long uid)
        {
            dynamic policy = null;
            try { policy = mgr.GetUserPolicy(user); return policy; }
            catch { }
            try { policy = mgr.GetUserPolicy(user.Id); return policy; }
            catch { }
            try { policy = mgr.GetUserPolicy(uid); return policy; }
            catch { }
            return user.GetType().GetProperty("Policy")?.GetValue(user);
        }

        internal static void UpdatePolicy(dynamic mgr, BaseItem user, long uid, dynamic policy)
        {
            try { mgr.UpdateUserPolicy(uid, policy); return; }
            catch { }
            try { mgr.UpdateUserPolicy(user.Id, policy); return; }
            catch { }
            mgr.UpdateUserPolicy(user, policy);
        }

    }
}
