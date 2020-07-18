using System;
using System.Collections.Generic;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Helper;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Model;

namespace KDFCommands {
	public class SongRandomizerResult {
		public readonly string PlaylistId;
		public readonly int IndexInPlaylist;
		public readonly AudioResource PlaylistItem;

		public SongRandomizerResult(string playlistId, int indexInPlaylist, AudioResource playlistItem) {
			PlaylistId = playlistId;
			IndexInPlaylist = indexInPlaylist;
			PlaylistItem = playlistItem;
		}

		protected bool Equals(SongRandomizerResult other) {
			return PlaylistId == other.PlaylistId && IndexInPlaylist == other.IndexInPlaylist;
		}

		public override bool Equals(object obj) {
			if (ReferenceEquals(null, obj)) {
				return false;
			}

			if (ReferenceEquals(this, obj)) {
				return true;
			}

			if (obj.GetType() != this.GetType()) {
				return false;
			}

			return Equals((SongRandomizerResult) obj);
		}

		public override int GetHashCode() {
			return HashCode.Combine(PlaylistId, IndexInPlaylist);
		}
	}
	
	public static class SongRandomizer {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private static readonly Random Random = new Random();
		
		public static IList<SongRandomizerResult> GetRandomSongs(int count, PlaylistManager playlistManager, HashSet<string> playlistSubset = null) {
			// Get total number of songs from all selected playlists
			var numSongs = 0;
			var playlistsUnfiltered = playlistManager.GetAvailablePlaylists();
			var playlists = new List<PlaylistInfo>();
			foreach (var playlist in playlistsUnfiltered) {
				if (playlistSubset != null && !playlistSubset.Contains(playlist.Id)) {
					continue;
				}

				playlists.Add(playlist);
				numSongs += playlist.SongCount;
			}

			var randomSongs = new List<SongRandomizerResult>();
			for (var i = 0; i < count; i++) {
				// Draw random song number
				var songIndex = Random.Next(0, numSongs);

				// Find the randomized song
				var infoOpt = FindSong(songIndex, playlists);
				if (!infoOpt.Ok) {
					throw new CommandException("SongRandomizer: Could not find the song at index " + songIndex + ".", CommandExceptionReason.InternalError);
				}

				var (list, index) = infoOpt.Value;
				var (playlist, _) = playlistManager.GetPlaylist(list.Id).UnwrapThrow();

				if (playlist.Count != list.SongCount) {
					Log.Warn("SongRandomizer: Playlist '{0}' is possibly corrupted!", list.Id);
				}

				var result = new SongRandomizerResult(list.Id, index, playlist[index]);
				if (randomSongs.Contains(result) && numSongs > count) {
					Log.Trace("SongRandomizer: Ignored duplicate song.");
					i--;
				} else {
					randomSongs.Add(result);
				}
			}

			return randomSongs;
		}
		
		private static R<(PlaylistInfo list, int offset)> FindSong(int index, IEnumerable<PlaylistInfo> playlists) {
			foreach (var list in playlists) {
				if (index < list.SongCount)
					return (list, index);
				index -= list.SongCount;
			}

			return R.Err;
		}
	}
}