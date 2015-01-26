﻿using MediaBrowser.Common.IO;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Trakt.Api;
using Trakt.Api.DataContracts.Sync;
using Trakt.Helpers;
using Trakt.Model;

namespace Trakt.ScheduledTasks
{
    /// <summary>
    /// Task that will Sync each users local library with their respective trakt.tv profiles. This task will only include 
    /// titles, watched states will be synced in other tasks.
    /// </summary>
    public class SyncLibraryTask : IScheduledTask
    {
        //private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly TraktApi _traktApi;
        private readonly IUserDataManager _userDataManager;

        public SyncLibraryTask(ILogManager logger, IJsonSerializer jsonSerializer, IUserManager userManager, IUserDataManager userDataManager, IHttpClient httpClient, IFileSystem fileSystem, IServerApplicationHost appHost)
        {
            _jsonSerializer = jsonSerializer;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _logger = logger.GetLogger("Trakt");
            _fileSystem = fileSystem;
            _traktApi = new TraktApi(jsonSerializer, _logger, httpClient, appHost);
        }

        public IEnumerable<ITaskTrigger> GetDefaultTriggers()
        {
            return new List<ITaskTrigger>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var users = _userManager.Users.Where(u =>
            {
                var traktUser = UserHelper.GetTraktUser(u);

                return traktUser != null;

            }).ToList();

            // No point going further if we don't have users.
            if (users.Count == 0)
            {
                _logger.Info("No Users returned");
                return;
            }

            // purely for progress reporting
            var progPercent = 0.0;
            var percentPerUser = 100 / users.Count;

            foreach (var user in users)
            {
                var traktUser = UserHelper.GetTraktUser(user);

                // I'll leave this in here for now, but in reality this continue should never be reached.
                if (traktUser == null || String.IsNullOrEmpty(traktUser.LinkedMbUserId))
                {
                    _logger.Error("traktUser is either null or has no linked MB account");
                    continue;
                }

                await SyncUserLibrary(user, traktUser, progPercent, percentPerUser, progress, cancellationToken)
                        .ConfigureAwait(false);

                progPercent += percentPerUser;
            }
        }

        private async Task SyncUserLibrary(User user, 
            TraktUser traktUser, 
            double progPercent,
            double percentPerUser, 
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var libraryRoot = user.RootFolder;

            /*
             * In order to sync watched status to trakt.tv we need to know what's been watched on Trakt already. This
             * will stop us from endlessly incrementing the watched values on the site.
             */
            var traktWatchedMovies = await _traktApi.SendGetAllWatchedMoviesRequest(traktUser).ConfigureAwait(false);
            var traktCollectedMovies = await _traktApi.SendGetAllCollectedMoviesRequest(traktUser).ConfigureAwait(false);
            var traktWatchedShows = await _traktApi.SendGetWatchedShowsRequest(traktUser).ConfigureAwait(false);
            var traktCollectedShows = await _traktApi.SendGetCollectedShowsRequest(traktUser).ConfigureAwait(false);

            var movies = new List<Movie>();
            var episodes = new List<Episode>();
            var playedMovies = new List<Movie>();
            var playedEpisodes = new List<Episode>();
            var unPlayedMovies = new List<Movie>();
            var unPlayedEpisodes = new List<Episode>();

            var mediaItems = libraryRoot.GetRecursiveChildren(user)
                .Where(SyncFromTraktTask.CanSync)
                .OrderBy(x => x is Movie)
                .ThenBy(x => x is Episode ? (x as Episode).SeriesName : null)
                .ToList();

            if (mediaItems.Count == 0)
            {
                _logger.Info("No trakt media found for '" + user.Name + "'. Have trakt locations been configured?");
                return;
            }

            // purely for progress reporting
            var percentPerItem = (float)percentPerUser / mediaItems.Count / 2.0;

            foreach (var child in mediaItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Path == null || child.LocationType == LocationType.Virtual) continue;

                if (child is Movie)
                {
                    var movie = child as Movie;
                    var collectedMovie = SyncFromTraktTask.FindMatch(movie, traktCollectedMovies);
                    if (collectedMovie == null || collectedMovie.Metadata.IsEmpty())
                    {
                        movies.Add(movie);
                    }

                    var userData = _userDataManager.GetUserData(user.Id, child.GetUserDataKey());

                    var movieWatched = SyncFromTraktTask.FindMatch(movie, traktWatchedMovies);
                    if (userData.Played)
                    {
                        if (movieWatched == null || movieWatched.Plays < userData.PlayCount)
                        {
                            playedMovies.Add(movie);
                        }
                    }
                    else
                    {
                        if (movieWatched != null)
                        {
                            unPlayedMovies.Add(movie);
                        }
                    }
                }
                else if (child is Episode)
                {
                    var episode = child as Episode;
                    var userData = _userDataManager.GetUserData(user.Id, episode.GetUserDataKey());
                    var isPlayedTraktTv = false;
                    var traktWatchedShow = SyncFromTraktTask.FindMatch(episode.Series, traktWatchedShows);

                    if (traktWatchedShow != null && traktWatchedShow.Seasons != null && traktWatchedShow.Seasons.Count > 0)
                    {
                        isPlayedTraktTv =
                            traktWatchedShow.Seasons.Any(
                                season =>
                                    season.Number == episode.GetSeasonNumber() &&
                                    season.Episodes != null &&
                                    season.Episodes.Any(te => te.Number == episode.IndexNumber && te.Plays > 0));
                    }

                    // if the show has been played locally and is unplayed on trakt.tv then add it to the list
                    if (userData != null && userData.Played && !isPlayedTraktTv)
                    {
                        playedEpisodes.Add(episode);
                    }
                    // If the show has not been played locally but is played on trakt.tv then add it to the unplayed list
                    else if (userData != null && !userData.Played && isPlayedTraktTv)
                    {
                        unPlayedEpisodes.Add(episode);
                    }
                    var traktCollectedShow = SyncFromTraktTask.FindMatch(episode.Series, traktCollectedShows);
                    if (traktCollectedShow == null ||
                        traktCollectedShow.Seasons == null ||
                        traktCollectedShow.Seasons.All(x => x.Number != episode.ParentIndexNumber) ||
                        traktCollectedShow.Seasons.First(x => x.Number == episode.ParentIndexNumber)
                            .Episodes.All(e => e.Number != episode.IndexNumber))
                    {
                        episodes.Add(episode);
                    }
                }

                // purely for progress reporting
                progPercent += percentPerItem;
                progress.Report(progPercent);
            }
            _logger.Debug("Movies to add to Collection: " + movies.Count);
            // send any remaining entries
            if (movies.Count > 0)
            {
                try
                {
                    var dataContracts = await _traktApi.SendLibraryUpdateAsync(movies, traktUser, cancellationToken, EventType.Add).ConfigureAwait(false);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (ArgumentNullException argNullEx)
                {
                    _logger.ErrorException("ArgumentNullException handled sending movies to trakt.tv", argNullEx);
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Exception handled sending movies to trakt.tv", e);
                }
                // purely for progress reporting
                progPercent += (percentPerItem * movies.Count);
                progress.Report(progPercent);
            }
            _logger.Debug("Movies to set watched: " + playedMovies.Count);
            if (playedMovies.Count > 0)
            {
                try
                {
                    var dataContracts = await _traktApi.SendMoviePlaystateUpdates(playedMovies, traktUser, true, cancellationToken);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Error updating movie play states", e);
                }
                // purely for progress reporting
                progPercent += (percentPerItem * playedMovies.Count);
                progress.Report(progPercent);
            }

            _logger.Debug("Movies to set unwatched: " + unPlayedMovies.Count);
            if (unPlayedMovies.Count > 0)
            {
                try
                {
                    var dataContracts = await _traktApi.SendMoviePlaystateUpdates(playedMovies, traktUser, true, cancellationToken);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Error updating movie play states", e);
                }
                // purely for progress reporting
                progPercent += (percentPerItem * unPlayedMovies.Count);
                progress.Report(progPercent);
            }

            _logger.Debug("Episodes to add to Collection: " + episodes.Count);
            if (episodes.Count > 0)
            {
                try
                {
                    var dataContracts = await _traktApi.SendLibraryUpdateAsync(episodes, traktUser, cancellationToken, EventType.Add).ConfigureAwait(false);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (ArgumentNullException argNullEx)
                {
                    _logger.ErrorException("ArgumentNullException handled sending episodes to trakt.tv", argNullEx);
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Exception handled sending episodes to trakt.tv", e);
                }
                // purely for progress reporting
                progPercent += (percentPerItem * episodes.Count);
                progress.Report(progPercent);
            }

            _logger.Debug("Episodes to set watched: " + playedEpisodes.Count);
            if (playedEpisodes.Count > 0)
            {
                try
                {
                    var dataContracts = await _traktApi.SendEpisodePlaystateUpdates(playedEpisodes, traktUser, true, cancellationToken);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                            LogTraktResponseDataContract(traktSyncResponse);
                    }
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Error updating episode play states", e);
                }
                // purely for progress reporting
                progPercent += (percentPerItem * playedEpisodes.Count);
                progress.Report(progPercent);
            }
            _logger.Debug("Episodes to set unwatched: " + unPlayedEpisodes.Count);
            if (unPlayedEpisodes.Count > 0)
            {
                try
                {
                    var dataContracts = await _traktApi.SendEpisodePlaystateUpdates(unPlayedEpisodes, traktUser, false, cancellationToken);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Error updating episode play states", e);
                }
                // purely for progress reporting
                progPercent += (percentPerItem * unPlayedEpisodes.Count);
                progress.Report(progPercent);
            }
        }

        public string Name
        {
            get { return "Sync library to trakt.tv"; }
        }

        public string Category
        {
            get
            {
                return "Trakt";
            }
        }

        public string Description
        {
            get
            {
                return
                    "Adds any media that is in each users trakt monitored locations to their trakt.tv profile";
            }
        }

        private void LogTraktResponseDataContract(TraktSyncResponse dataContract)
        {
            _logger.Debug("TraktResponse Added Movies: " + dataContract.Added.Movies);
            _logger.Debug("TraktResponse Added Shows: " + dataContract.Added.Shows);
            _logger.Debug("TraktResponse Added Seasons: " + dataContract.Added.Seasons);
            _logger.Debug("TraktResponse Added Episodes: " + dataContract.Added.Episodes);
            foreach (var traktMovie in dataContract.NotFound.Movies)
            {
                _logger.Error("TraktResponse not Found:" + _jsonSerializer.SerializeToString(traktMovie));
            }
            foreach (var traktShow in dataContract.NotFound.Shows)
            {
                _logger.Error("TraktResponse not Found:" + _jsonSerializer.SerializeToString(traktShow));
            }
            foreach (var traktSeason in dataContract.NotFound.Seasons)
            {
                _logger.Error("TraktResponse not Found:" + _jsonSerializer.SerializeToString(traktSeason));
            }
            foreach (var traktEpisode in dataContract.NotFound.Episodes)
            {
                _logger.Error("TraktResponse not Found:" + _jsonSerializer.SerializeToString(traktEpisode));
            }
        }
    }
}
