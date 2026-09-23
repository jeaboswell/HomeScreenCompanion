using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Users;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HomeScreenCompanion
{
    /// <summary>
    /// Moves a series the user had finished to the front of Continue Watching / Next Up when a
    /// new episode arrives.
    ///
    /// Emby orders Next Up by MAX(LastPlayedDate) over a series' played episodes and has no other
    /// sort key, so the only lever is that timestamp. Instead of toggling the episode unplayed and
    /// played again (which resets PlayCount and fires TogglePlayed events that scrobblers sync),
    /// we only rewrite LastPlayedDate on the user's last watched episode. Played, PlayCount and
    /// position stay untouched.
    /// </summary>
    internal static class ContinueWatchingBumper
    {
        public const string ModeAllEpisodes    = "AllEpisodes";
        public const string ModeNewSeasonsOnly = "NewSeasonsOnly";

        internal static List<User> ResolveUsers(PluginConfiguration config, IUserManager userManager)
        {
            // "All users" is resolved on every run so users created later are included automatically.
            var all = userManager.GetUserList(new UserQuery { IsDisabled = false });
            if (config.ContinueWatchingBumpAllUsers) return all.ToList();

            var ids = new HashSet<string>(config.ContinueWatchingBumpUserIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            return all.Where(u => ids.Contains(u.Id.ToString("N")) || ids.Contains(u.Id.ToString())).ToList();
        }

        /// <summary>
        /// Checks every given user against one series and bumps where the rules match.
        /// Idempotent: after a bump the last watched episode's LastPlayedDate is newer than the
        /// Next Up episode, so later runs leave it alone. Returns the number of users bumped.
        /// </summary>
        internal static int ProcessSeries(Series series, IReadOnlyList<User> users, PluginConfiguration config,
            ILibraryManager libraryManager, IUserDataManager userDataManager, RunLog log, CancellationToken cancellationToken)
        {
            if (users.Count == 0) return 0;

            // Only real, numbered episodes outside specials take part in the order.
            var episodes = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                AncestorIds = new[] { series.InternalId },
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber.HasValue && e.ParentIndexNumber.Value > 0 && e.IndexNumber.HasValue)
            .OrderBy(e => e.ParentIndexNumber!.Value)
            .ThenBy(e => e.IndexNumber!.Value)
            .ToList();

            if (episodes.Count < 2) return 0;

            bool newSeasonsOnly = string.Equals(config.ContinueWatchingBumpMode, ModeNewSeasonsOnly, StringComparison.OrdinalIgnoreCase);
            int bumped = 0;

            foreach (var user in users)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!series.IsVisible(user)) continue;

                    var data = episodes.Select(e => userDataManager.GetUserData(user, e)).ToList();

                    // Last watched episode in airing order — the one the user would toggle by hand.
                    int lastIdx = data.FindLastIndex(d => d != null && d.Played);
                    if (lastIdx < 0 || lastIdx == episodes.Count - 1) continue;

                    // The user must have been caught up: everything up to the last watched episode played.
                    bool caughtUp = true;
                    for (int i = 0; i < lastIdx; i++)
                        if (data[i] == null || !data[i].Played) { caughtUp = false; break; }
                    if (!caughtUp) continue;

                    var lastWatched = episodes[lastIdx];
                    var lastData    = data[lastIdx];
                    var nextUp      = episodes[lastIdx + 1];
                    var nextData    = data[lastIdx + 1];
                    if (lastData.LastPlayedDate == null) continue;

                    // Already started → it is in Resume with its own date; leave it be.
                    if (nextData != null && nextData.PlaybackPositionTicks > 0) continue;

                    // Only the Next Up episode decides. If it is older than the last watch (or than a
                    // previous bump), nothing new has arrived for this user — e.g. S02E02 landing
                    // while S02E01 is still unwatched does not bump again.
                    if (nextUp.DateCreated <= lastData.LastPlayedDate.Value) continue;

                    if (newSeasonsOnly && nextUp.ParentIndexNumber!.Value <= lastWatched.ParentIndexNumber!.Value) continue;

                    string label = $"'{series.Name}' for {user.Name} (new {EpisodeCode(nextUp)})";
                    if (config.DryRunMode)
                    {
                        log.Skip($"[Dry run] Would bump {label}");
                        continue;
                    }

                    lastData.LastPlayedDate = DateTimeOffset.UtcNow;
                    userDataManager.SaveUserData(user, lastWatched, lastData, UserDataSaveReason.Import, cancellationToken);
                    log.Ok($"Bumped {label}");
                    bumped++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.Error($"'{series.Name}' for {user.Name}: {ex.Message}");
                }
            }

            return bumped;
        }

        private static string EpisodeCode(Episode e)
            => $"S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00}";
    }
}
