using System;
using System.Collections.Generic;
using System.Linq;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Model;
using TSLib;
using TSLib.Full;

namespace KDFCommands {
	class Autofill {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private class AutoFillData {
			public Random Random { get; } = new Random();
			public HashSet<string> Playlists { get; set; } = null;
			public QueueItem Next { get; set; }
			public Uid IssuerUid { get; set; }
		}

		private AutoFillData AutofillData { get; set; }

		private bool AutofillEnabled => AutofillData != null;

		private Ts3Client Ts3Client { get; }

		private PlayManager PlayManager { get; }

		private PlaylistManager PlaylistManager { get; }
		public TsFullClient Ts3FullClient { get; }

		public Autofill(
			Ts3Client ts3Client,
			PlayManager playManager,
			PlaylistManager playlistManager,
			TsFullClient ts3FullClient) {
			
			Ts3Client = ts3Client;
			PlayManager = playManager;
			PlaylistManager = playlistManager;
			Ts3FullClient = ts3FullClient;
		}

		public string Status(string word = "") {
			string result = "Autofill is";
			result += word != "" ? " " + word + " " : " ";

			if (AutofillEnabled) {
				result += "enabled";

				if (AutofillData.Playlists != null) {
					result += " using the playlist";
					
					var playlists = AutofillData.Playlists.ToList();
					if (playlists.Count == 1) {
						result += " " + playlists[0] + ".";
					} else {
						result += "s ";
						result += string.Join(", ", playlists.Take(playlists.Count - 1));
						result += " and " + playlists[^1] + ".";
					}
				} else {
					result += " using all playlists.";
				}
				
				result += " Last change by " + ClientUtility.GetClientNameFromUid(Ts3FullClient, AutofillData.IssuerUid) + ".";
			} else {
				result += "disabled";
				result += ".";
			}

			return result;
		}

		public void Disable() { AutofillData = null; }

		public void DisableAndRemoveShadow() {
			if (AutofillData != null && AutofillData.Next == PlayManager.NextSongShadow)
				PlayManager.NextSongShadow = null;
			Disable();
		}

		private bool playbackStoppedReentrantGuard = false;

		public void OnPlaybackStopped(PlaybackStoppedEventArgs eventArgs) {
			if (playbackStoppedReentrantGuard)
				return;
			playbackStoppedReentrantGuard = true;

			// Enqueue fires playback stopped event if the song failed to play
			UpdateAutofillOnPlaybackStopped(eventArgs);

			playbackStoppedReentrantGuard = false;
		}

		public void Disable(Uid uid) {
			// Explicitly requested to turn it off
			DisableAndRemoveShadow();
			Ts3Client.SendChannelMessage("[" + ClientUtility.GetClientNameFromUid(Ts3FullClient, uid) + "] " +
			                             Status("now"));
		}

		private bool CheckPreconditions() {
			return PlayManager.Queue.Items.Count == PlayManager.Queue.Index && // at end of queue
			       AutofillEnabled && // enabled
			       !PlayManager.IsPlaying && // not playing
			       !Ts3Client.IsDefinitelyAlone(); // not alone
		}

		private void UpdateAutofillOnPlaybackStopped(PlaybackStoppedEventArgs eventArgs) {
			if (!CheckPreconditions()) {
				if (AutofillData != null && AutofillData.Next == eventArgs.NextShadow)
					eventArgs.NextShadow = null;
				Disable();
				return;
			}

			var (item, next) = DoAutofill();
			eventArgs.Item = item;
			eventArgs.NextShadow = next;
		}

		private (QueueItem item, QueueItem next) DoAutofill() {
			var item = AutofillData.Next;
			Log.Info("Autofilling the song '{0}' from playlist '{1}'.", item.AudioResource.ResourceTitle,
				item.MetaData.ContainingPlaylistId);
			DrawNextSong();
			var next = AutofillData.Next;
			return (item, next);
		}

		private static R<(PlaylistInfo list, int offset)> FindSong(int index, IEnumerable<PlaylistInfo> playlists) {
			foreach (var list in playlists) {
				if (index < list.SongCount)
					return (list, index);
				index -= list.SongCount;
			}

			return R.Err;
		}

		private QueueItem DrawRandom() {
			// Play random song from a random playlist currently in the selected set
			
			// Get total number of songs from all selected playlists
			var numSongs = 0;
			var playlistsUnfiltered = PlaylistManager.GetAvailablePlaylists().UnwrapThrow();
			List<PlaylistInfo> playlists = new List<PlaylistInfo>();
			foreach (var playlist in playlistsUnfiltered) {
				if (AutofillData.Playlists != null && !AutofillData.Playlists.Contains(playlist.Id)) {
					continue;
				}

				playlists.Add(playlist);
				numSongs += playlist.SongCount;
			}

			Log.Debug("Found {0} songs across {1} playlists.", numSongs, playlists.Count);

			var sIdx = 0;
			string plId = null;
			AudioResource resource = null;
			for (var i = 0; i < 5; i++) {
				// Draw random song number
				var songIndex = AutofillData.Random.Next(0, numSongs);

				// Find the randomized song
				var infoOpt = FindSong(songIndex, playlists);
				if (!infoOpt.Ok)
					throw new CommandException("Autofill: Could not find the song at index " + songIndex + ".",
						CommandExceptionReason.InternalError);

				var (list, index) = infoOpt.Value;
				var (playlist, _) = PlaylistManager.LoadPlaylist(list.Id).UnwrapThrow();

				if (playlist.Items.Count != list.SongCount)
					Log.Warn("Playlist '{0}' is possibly corrupted!", list.Id);

				sIdx = index;
				plId = list.Id;
				resource = playlist.Items[index].AudioResource;
				Log.Info("Found song index {0}: Song '{1}' in playlist '{2}' at index {3}.", songIndex,
					resource.ResourceTitle, list.Id, index);

				// Check if the song was already played in the last 250 songs, if not take this one.
				// If items.count < 250, the subtraction is negative, meaning that j == 0 will be reached first
				var foundDuplicate = false;
				var items = PlayManager.Queue.Items;
				if (items.Count > 0) {
					for (var j = items.Count - 1; j != 0 && j >= items.Count - 250; j--) {
						if (!items[j].AudioResource.Equals(resource)) {
							continue;
						}

						Log.Info("The song {0} was already played {1} songs ago. Searching another one...",
							items[j].AudioResource.ResourceTitle, items.Count - j - 1);
						foundDuplicate = true;
						break;
					}
				}

				if (!foundDuplicate) {
					break;
				}
			}

			if (resource == null) {
				// Should not happen
				throw new CommandException(
					"Autofill: Missing resource for song at index " + sIdx + " in playlist " + plId + ".",
					CommandExceptionReason.InternalError);
			}

			using (System.IO.StreamWriter statfile = new System.IO.StreamWriter("stats_autofill.txt", true)) {
				statfile.WriteLine(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ":::" + resource.ResourceTitle + ":::" + resource.ResourceId);
			}
			return new QueueItem(resource, new MetaData(Ts3FullClient.Identity.ClientUid, plId));
		}

		private void DrawNextSong() {
			AutofillData.Next = DrawRandom();
		}

		private E<LocalStr> PlayRandom() {
			if (AutofillData.Next == null)
				throw new InvalidOperationException();

			var item = AutofillData.Next;
			Log.Info("Autofilling the song '{0}' from playlist '{1}'.", item.AudioResource.ResourceTitle,
				item.MetaData.ContainingPlaylistId);
			var res = PlayManager.Enqueue(item);
			
			// Only draw next song if autofill was not disabled in the meantime
			if (AutofillEnabled) {
				DrawNextSong();
			} 
			return res;
		}

		public void CommandAutofill(Uid uid, string[] playlistIds = null) {
			// Check if all playlists exist, otherwise throw exception
			if (playlistIds != null) {
				for (int i = 0; i < playlistIds.Length; ++i) {
					if (PlaylistManager.TryGetPlaylistId(playlistIds[i], out var realId)) {
						playlistIds[i] = realId; // Apply possible correction
					} else {
						throw new CommandException("The playlist '" + playlistIds[i] + "' does not exist.",
							CommandExceptionReason.CommandError);
					}
				}
			}

			if (AutofillEnabled) {
				if (AutofillData.Playlists == null) {
					// Currently enabled but without a selected set of playlists

					if (playlistIds != null && playlistIds.Length != 0) {
						// If a selected set of playlists is given, change to "set of playlists"
						AutofillData.Playlists = new HashSet<string>(playlistIds);
						AutofillData.IssuerUid = uid;
						DrawNextSong();
					} else {
						// Else, disable autofill
						DisableAndRemoveShadow();
					}
				} else {
					// Currently enabled with a selected set of playlists

					if (playlistIds != null && playlistIds.Length != 0) {
						// If a selected set of playlists is given, update it
						AutofillData.Playlists = new HashSet<string>(playlistIds);
						AutofillData.IssuerUid = uid;
					} else {
						// Else, switch to all
						AutofillData.Playlists = null;
						AutofillData.IssuerUid = uid;
					}

					DrawNextSong();
				}
			} else {
				// Currently disabled, enable now (with set of playlists if given)
				AutofillData = new AutoFillData {IssuerUid = uid};
				if (playlistIds != null && playlistIds.Length != 0) {
					AutofillData.Playlists = new HashSet<string>(playlistIds);
				} else {
					AutofillData.Playlists = null;
				}

				AutofillData.Next = DrawRandom();
			}

			if (CheckPreconditions()) {
				var (item, next) = DoAutofill();
				PlayManager.Enqueue(item);
				PlayManager.NextSongShadow = next;
			}
			
			Ts3Client.SendChannelMessage("[" + ClientUtility.GetClientNameFromUid(Ts3FullClient, uid) + "] " +
			                             Status("now"));
		}

	}
}