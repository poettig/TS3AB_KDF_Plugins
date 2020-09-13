using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Playlists;
using TS3AudioBot.Web.Api;
using TSLib;
using TSLib.Full;

namespace KDFCommands {
	internal class Autofill {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private class AutoFillData {
			public HashSet<string> Playlists { get; set; }
			public QueueItem Next { get; set; }
			public Uid IssuerUid { get; set; }

			public AutoFillData(Uid issuerUid) {
				IssuerUid = issuerUid;
			}
		}

		public class AutoFillEventArgs : EventArgs {
			public JsonValue<AutofillStatus> status;
			
			public AutoFillEventArgs(JsonValue<AutofillStatus> status) {
				this.status = status;
			}
		}

		private AutoFillData AutofillData { get; set; }

		private bool AutofillEnabled => AutofillData != null;

		private Ts3Client Ts3Client { get; }

		private Player Player { get; }
		
		private PlayManager PlayManager { get; }

		private PlaylistManager PlaylistManager { get; }
		public TsFullClient Ts3FullClient { get; }

		public event EventHandler<AutoFillEventArgs> OnStateChange;
		
		public Autofill(
			Ts3Client ts3Client,
			Player player,
			PlayManager playManager,
			PlaylistManager playlistManager,
			TsFullClient ts3FullClient) {
			
			Ts3Client = ts3Client;
			Player = player;
			PlayManager = playManager;
			PlaylistManager = playlistManager;
			Ts3FullClient = ts3FullClient;
		}

		public JsonValue<AutofillStatus> Status(string word = "") {
			return JsonValue.Create(new AutofillStatus {
				Enabled = AutofillEnabled,
				IssuerId = AutofillData?.IssuerUid.Value,
				IssuerName = AutofillData != null ? ClientUtility.GetClientNameFromUid(Ts3FullClient, AutofillData.IssuerUid) : null,
				Word = word,
				Playlists = AutofillData?.Playlists
			}, StatusToString);
		}

		private string StatusToString(AutofillStatus status) {
			string result = "Autofill is";
			result += status.Word != "" ? " " + status.Word + " " : " ";

			if (status.Enabled) {
				result += "enabled";

				if (status.Playlists != null) {
					result += " using the playlist";
					
					var playlists = status.Playlists.ToList();
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
				
				result += " Last change by " + status.IssuerName + ".";
			} else {
				result += "disabled";
				result += ".";
			}

			return result;
		}

		public void Disable() {
			Log.Trace("Autofill: Disabled.");
			AutofillData = null;
			OnStateChange?.Invoke(this, new AutoFillEventArgs(Status()));
		}

		public void DisableAndRemoveShadow() {
			if (AutofillData != null && AutofillData.Next == PlayManager.NextSongShadow) {
				PlayManager.NextSongShadow = null;
				Log.Trace("Autofill: Removed existing shadow.");
			}

			Disable();
		}

		private bool _playbackStoppedReentrantGuard = false;

		public void OnPlaybackStopped(PlaybackStoppedEventArgs eventArgs) {
			if (_playbackStoppedReentrantGuard)
				return;
			_playbackStoppedReentrantGuard = true;

			// Enqueue fires playback stopped event if the song failed to play
			UpdateAutofillOnPlaybackStopped(eventArgs);

			_playbackStoppedReentrantGuard = false;
		}

		public void Disable(Uid uid) {
			// Explicitly requested to turn it off
			DisableAndRemoveShadow();
			Ts3Client.SendChannelMessage("[" + ClientUtility.GetClientNameFromUid(Ts3FullClient, uid) + "] " +
			                             Status("now"));
		}

		private bool ShouldStayActive() {
			return !Ts3Client.IsDefinitelyAlone() || Player.WebSocketPipe.HasListeners; // not alone or anyone is listening via websocket
		}

		private bool ShouldFillSong() {
			var b = AutofillEnabled && // enabled
			        PlayManager.Queue.Items.Count == PlayManager.Queue.Index && // at end of queue
			        !PlayManager.IsPlaying; // not playing
			       
			if (!b) {
				string reason;
				if (!AutofillEnabled)
					reason = "Not enabled";
				else if (PlayManager.Queue.Items.Count != PlayManager.Queue.Index)
					reason = "Not at end of queue";
				else
					reason = "Play manager is playing";

				Log.Trace($"Autofill: ShouldFillSong returned false, first reason: {reason}");
			}

			return b;
		}

		private void UpdateAutofillOnPlaybackStopped(PlaybackStoppedEventArgs eventArgs) {
			if (!AutofillEnabled)
				return;

			if (!ShouldStayActive()) {
				DisableAndRemoveShadow();
				return;
			}

			if (!ShouldFillSong())
				return;

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

		private (QueueItem item, int offset) DrawRandom() {
			// Play random song from a random playlist currently in the selected set
			
			// Get 5 random songs
			SongRandomizerResult chosenSong = null;
			var randomSongs = SongRandomizer.GetRandomSongs(5, PlaylistManager, AutofillData.Playlists);
			foreach (var song in randomSongs) {
				// Check if the song was already played in the last 250 songs, if not take this one.
				// If items.count < 250, the subtraction is negative, meaning that j == 0 will be reached first
				var foundDuplicate = false;
				var items = PlayManager.Queue.Items;
				if (items.Count > 0) {
					for (var j = items.Count - 1; j != 0 && j >= items.Count - 250; j--) {
						if (!items[j].AudioResource.Equals(song.PlaylistItem)) {
							continue;
						}

						Log.Trace("Autofill: The song {0} was already played {1} songs ago. Searching another one...",
							items[j].AudioResource.ResourceTitle, items.Count - j - 1);
						foundDuplicate = true;
						break;
					}
				}

				if (!foundDuplicate) {
					chosenSong = song;
					break;
				}
			}

			if (chosenSong == null) {
				// Just play the last result anyway
				chosenSong = randomSongs[^1];
			}

			var resource = chosenSong.PlaylistItem;
			if (resource == null) {
				// Should not happen
				throw new CommandException(
					"Autofill: Missing resource for song at index " + chosenSong.IndexInPlaylist + " in playlist " + chosenSong.PlaylistId + ".",
					CommandExceptionReason.InternalError);
			}

			using (var statfile = new System.IO.StreamWriter("stats_autofill.txt", true)) {
				statfile.WriteLine(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ":::" + resource.ResourceTitle + ":::" + resource.ResourceId);
			}
			return (new QueueItem(resource, new MetaData(Ts3FullClient.Identity.ClientUid, chosenSong.PlaylistId)), chosenSong.IndexInPlaylist);
		}

		private void DrawNextSong() {
			var (item, index) = DrawRandom();
			Log.Info($"Autofill: Next song is '{item.AudioResource.ResourceTitle}' ({item.MetaData.ContainingPlaylistId}:{index})");
			AutofillData.Next = item;
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
						
						OnStateChange?.Invoke(this, new AutoFillEventArgs(Status()));
					} else {
						// Else, disable autofill
						Log.Info("Autofill: Disabled by command.");
						Disable(uid);
						return;
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

					OnStateChange?.Invoke(this, new AutoFillEventArgs(Status()));
					Log.Trace("Autofill: Changed active playlists.");
					DrawNextSong();
				}
			} else {
				// Check if the bot is alone. If yes, throw exception as autofill can't be enabled.
				if (!ShouldStayActive()) {
					throw new CommandException("Noone is here to listen to what whould be autofilled.", CommandExceptionReason.CommandError);
				}
				
				// Currently disabled, enable now (with set of playlists if given)
				AutofillData = new AutoFillData(uid);
				if (playlistIds != null && playlistIds.Length != 0) {
					AutofillData.Playlists = new HashSet<string>(playlistIds);
				} else {
					AutofillData.Playlists = null;
				}

				OnStateChange?.Invoke(this, new AutoFillEventArgs(Status()));
				Log.Info("Autofill: Enabled.");
				DrawNextSong();
			}
			
			if (!ShouldStayActive()) {
				Log.Info("Autofill: Disabled (should not stay active).");
				DisableAndRemoveShadow();
				return;
			}

			if (ShouldFillSong()) {
				var (item, next) = DoAutofill();
				PlayManager.Enqueue(item);
				PlayManager.NextSongShadow = next;
			}
			
			Ts3Client.SendChannelMessage("[" + ClientUtility.GetClientNameFromUid(Ts3FullClient, uid) + "] " +
			                             Status("now"));
		}
	}

	public class AutofillStatus {
		[JsonProperty("Enabled")]
		public bool Enabled { get; set; }
		
		[JsonProperty("IssuerId")]
		public string IssuerId { get; set; }
		
		[JsonProperty("IssuerName")]
		public string IssuerName { get; set; }
		
		[JsonProperty("Word")]
		public string Word { get; set; }
		
		[JsonProperty("Playlists")]
		public HashSet<string> Playlists { get; set; }
	}
}