using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Config;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Api;
using TS3AudioBot.Web.Model;
using TS3AudioBot.Web.WebSocket;
using TSLib;
using TSLib.Full;
using TSLib.Helper;

namespace KDFCommands {
	public class UpdateWebSocket : IDisposable {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		
		private readonly WebSocketServer server;
		private readonly KDFCommandsPlugin kdf;
		private readonly Player player;
		private readonly PlayManager playManager;
		private readonly ResolveContext resolver;
		private readonly Ts3Client ts3Client;
		private readonly TsFullClient ts3FullClient;

		private bool running;

		public UpdateWebSocket(
			KDFCommandsPlugin kdf,
			Player player,
			PlayManager playManager,
			ResolveContext resolver,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			ConfWebSocket confWebSocket
		) {
			this.kdf = kdf;
			this.player = player;
			this.playManager = playManager;
			this.resolver = resolver;
			this.ts3Client = ts3Client;
			this.ts3FullClient = ts3FullClient;

			this.playManager.Queue.OnQueueChange += QueueChanged;
			this.playManager.Queue.OnQueueIndexChange += UpdateRecentlyPlayed;
			this.kdf.Autofill.OnStateChange += AutofillChanged; 
			this.kdf.Voting.OnSkipVoteChanged -= SkipVoteChanged;

			server = new WebSocketServer(IPAddress.Loopback, 2021, confWebSocket);
			server.OnClientConnected += ClientConnected;

			running = true;
			var thread = new Thread(() => {
				JsonValue<Dictionary<string, IList<(string, string)>>> listeners = null;
				JsonValue<SongInfo> song = null;
				bool frozen = false;
				long frozenSince = -1;
				
				while (running) {
					// Check for listener change
					var newListeners = KDFCommandsPlugin.CommandListeners(ts3Client, ts3FullClient, player);
					if (listeners == null || !ListenersEqual(listeners, newListeners)) {
						SendListenerUpdate(newListeners);
					}
					listeners = newListeners;
					
					// Check if song should be updated
					JsonValue<SongInfo> newSong = null;
					try {
						newSong = MainCommands.CommandSong(playManager, player, resolver);
					} catch (CommandException) {
						// Don't crash just because nothing is playing
					}
					
					if (newSong != null) {
						var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
						bool frozenStateChanged = ts - player.WebSocketPipe.LastDataSentTimestamp > 500 != frozen;
						if (frozenStateChanged) {
							frozen = !frozen;
							frozenSince = frozen ? DateTimeOffset.Now.ToUnixTimeSeconds() : -1;
						}

						if (
							song == null ||
							newSong.Value.Position < song.Value.Position ||
							newSong.Value.Length != song.Value.Length ||
							newSong.Value.Link != song.Value.Link ||
							newSong.Value.Paused != song.Value.Paused ||
							frozenStateChanged && !frozen
						) {
							if (Log.IsTraceEnabled) {
								string reason = "Unexpected reason.";
								if (song == null) reason = "First song started playing.";
								else if (newSong.Value.Position < song.Value.Position) reason = "Position < previous position.";
								else if (newSong.Value.Length != song.Value.Length) reason = "Length changed.";
								else if (newSong.Value.Link != song.Value.Link) reason = "Resource URL changed.";
								else if (newSong.Value.Paused != song.Value.Paused) reason = "Pause state changed.";
								else if (frozenStateChanged & !frozen) reason = "Song was frozen for over 500ms and now unfroze.";

								Log.Trace("Song update sent. Reason: " + reason);
							}

							SendSongUpdate(newSong);
						}

						// Send resync update if frozen every 5 seconds.
						if (frozen && (DateTimeOffset.Now.ToUnixTimeSeconds() - frozenSince) % 5 == 4) {
							Log.Trace("Song update sent. Reason: Update in frozen state.");
							SendSongUpdate(newSong);
						}
					} else if (song != null) {
						// newSong is null but previous song was not --> music stopped
						SendToAll("song", null);
						Log.Trace("Song update sent. Reason: Music stopped.");
					}

					song = newSong;

					Thread.Sleep(1000);
				}
			}) {
				IsBackground = true
			};
			thread.Start();
		}

		private void SendListenerUpdate(JsonValue<Dictionary<string, IList<(string, string)>>> newListeners, WebSocketConnection client = null) {
			if (client != null) {
				SendToClient(client, "listeners", JsonValue.Create(newListeners).Serialize());
			} else {
				SendToAll("listeners", JsonValue.Create(newListeners).Serialize());
			}
		}
		
		private void SendSongUpdate(JsonValue<SongInfo> newSong, WebSocketConnection client = null) {
			// Add Issuer Name
			newSong.Value.IssuerName = TryTranslateUidToName(newSong.Value.IssuerUid);
			
			if (client != null) {
				SendToClient(client, "song", newSong.Serialize());
			} else {
				SendToAll("song", newSong.Serialize());
			}
		}

		private string TryTranslateUidToName(string uid) {
			return Uid.IsValid(uid) ? ClientUtility.GetClientNameFromUid(ts3FullClient, Uid.To(uid)) : uid;
		}

		private void QueueChanged(object sender, EventArgs _) {
			foreach (var (_, value) in server.ConnectedClients) {
				SendToClient(value, "queue", kdf.CommandQueueInternal(Uid.To(value.Uid)).Serialize());
			}
		}

		private void UpdateRecentlyPlayed(object sender, EventArgs _) {
			SendToAll( "recentlyplayed", kdf.CommandRecentlyPlayed(50).Serialize());
		}

		private void SkipVoteChanged(object sender, Voting.SkipVoteEventArgs data) {
			SendToAll("voteskip",  new JsonValue<Voting.Result>(data.Data).Serialize());
		}

		private void ClientConnected(object sender, ClientConnectedEventArgs e) {
			// Send all initial info necessary
			try {
				SendSongUpdate(MainCommands.CommandSong(playManager, player, resolver), e.Client);
			} catch (CommandException) {
				// Nothing playing
				SendToClient(e.Client, "song", null);
			}
			SendListenerUpdate(KDFCommandsPlugin.CommandListeners(ts3Client, ts3FullClient, player), e.Client);
			SendToClient(e.Client, "queue", kdf.CommandQueueInternal(Uid.To(e.Client.Uid)).Serialize());
			SendToClient(e.Client, "recentlyplayed", kdf.CommandRecentlyPlayed(50).Serialize());
			SendToClient(e.Client, "autofill", kdf.Autofill.Status().Serialize());

			foreach (var (key, value) in kdf.Voting.CurrentVotes) {
				if (key == "skip") {
					SendToClient(e.Client, "voteskip", new JsonValue<Voting.Result>(new Voting.Result {
						VoteAdded = false,
						VoteComplete = false,
						VotesChanged = false,
						VoteCount = value.Voters.Count,
						VotesNeeded = kdf.Voting.Needed
					}).Serialize());
				}
			}
			
			var updater = kdf.TwitchInfoUpdater;
			if (
				updater != null &&
				updater.StreamerInfo != null &&
				updater.StreamInfo != null &&
				updater.StreamerInfo.Data != null &&
				updater.StreamInfo.Data != null && 
				updater.StreamerInfo.Data.Length != 0 &&
				updater.StreamInfo.Data.Length != 0
			) {
				var streamerInfo = updater.StreamerInfo.Data[0];
				var streamInfo = updater.StreamInfo.Data[0];
				SendToClient(e.Client, "twitchinfo", CreateTwitchInfoUpdate(streamerInfo, streamInfo).Serialize());
			}
		}
		
		private void AutofillChanged(object sender, Autofill.AutoFillEventArgs e) {
			SendToAll("autofill", e.status.Serialize());
		}

		public void TwitchInfoChanged(object sender, TwitchInfoEventArgs e) {
			if (
				e.StreamerInfo == null || e.StreamInfo == null ||
				e.StreamerInfo.Data == null || e.StreamInfo.Data == null || 
				e.StreamerInfo.Data.Length == 0 || e.StreamInfo.Data.Length == 0
			) {
				Log.Warn("Incomplete stream information, won't send an update.");
				return;
			}
			
			var streamerInfo = e.StreamerInfo.Data[0];
			var streamInfo = e.StreamInfo.Data[0];
			SendToAll("twitchinfo", CreateTwitchInfoUpdate(streamerInfo, streamInfo).Serialize());
		}

		private JsonValue<KDFCommandsPlugin.TwitchInfo> CreateTwitchInfoUpdate(StreamerData streamerInfo, StreamData streamInfo) {
			return JsonValue.Create(new KDFCommandsPlugin.TwitchInfo {
				ViewerCount = streamInfo.ViewerCount,
				Uptime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - streamInfo.StartedAt.ToUnix(),
				ThumbnailUrl = streamInfo.ThumbnailUrl,
				AvatarUrl = streamerInfo.ProfileImageUrl.ToString(),
				StreamerName = streamerInfo.DisplayName,
				StreamerLogin = streamerInfo.Login,
				StreamTitle = streamInfo.Title
			});
		}
		
		private void SendToAll(string type, string message) {
			foreach (var pair in server.ConnectedClients) {
				SendToClient(pair.Value, type, message);
			}
		}

		private static void SendToClient(WebSocketConnection client, string type, string message) {
			message ??= "null";
			client.SendString($"{{\"type\": \"{type}\", \"data\": {message}}}");
		}

		private static bool ListenersEqual(
			JsonValue<Dictionary<string, IList<(string, string)>>> left,
			JsonValue<Dictionary<string, IList<(string, string)>>> right
		) {
			return ListenersEqualDirectional(left, right) && ListenersEqualDirectional(right, left);
		}

		private static bool ListenersEqualDirectional(
			JsonValue<Dictionary<string, IList<(string, string)>>> left,
			JsonValue<Dictionary<string, IList<(string, string)>>> right
		) {
			foreach (var (key, value) in left.Value) {
				if (!right.Value.ContainsKey(key)) {
					return false;
				}

				foreach (var leftData in value) {
					var found = false;
					
					foreach (var rightData in right.Value[key]) {
						if (leftData.Item1 == rightData.Item1) {
							found = true;
						}
					}

					if (!found) {
						return false;
					}
				}
			}

			return true;
		}

		public void Dispose() {
			playManager.Queue.OnQueueChange -= QueueChanged;
			playManager.Queue.OnQueueIndexChange -= UpdateRecentlyPlayed;
			kdf.Autofill.OnStateChange -= AutofillChanged;
			kdf.Voting.OnSkipVoteChanged -= SkipVoteChanged;
			server.OnClientConnected -= ClientConnected;
			
			server.Dispose();
			running = false;
		}
	}
}
