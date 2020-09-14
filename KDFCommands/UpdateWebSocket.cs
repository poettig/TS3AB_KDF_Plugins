using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Newtonsoft.Json;
using NLog;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Config;
using TS3AudioBot.Playlists;
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
		private readonly PlaylistManager playlistManager;
		private readonly Ts3Client ts3Client;
		private readonly TsFullClient ts3FullClient;

		private bool running;

		public UpdateWebSocket(
			KDFCommandsPlugin kdf,
			Player player,
			PlayManager playManager,
			PlaylistManager playlistManager,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			ConfWebSocket confWebSocket
		) {
			this.kdf = kdf;
			this.player = player;
			this.playManager = playManager;
			this.playlistManager = playlistManager;
			this.ts3Client = ts3Client;
			this.ts3FullClient = ts3FullClient;

			this.playManager.Queue.OnQueueChange += QueueChanged;
			this.kdf.Autofill.OnStateChange += AutofillChanged;

			server = new WebSocketServer(IPAddress.Loopback, 2021, confWebSocket);

			running = true;
			var thread = new Thread(() => {
				JsonValue<Dictionary<string, IList<string>>> listeners = null;
				JsonValue<SongInfo> song = null;
				uint songFrozen = 0;
				
				while (running) {
					// Check for listener change
					var newListeners = KDFCommandsPlugin.CommandListeners(ts3Client, ts3FullClient, player);
					if (listeners == null || !ListenersEqual(listeners, newListeners)) {
						var translated = new Dictionary<string, IList<string>>();
						foreach (var (key, value) in newListeners.Value) {
							var list = value.Select(entry => ClientUtility.GetClientNameFromUid(ts3FullClient, Uid.To(entry))).ToList();
							translated[key] = list;
						}
						SendToAll("listeners", JsonValue.Create(translated).Serialize());
					}
					listeners = newListeners;
					
					// Check if song should be updated
					JsonValue<SongInfo> newSong = null;
					try {
						newSong = MainCommands.CommandSong(playManager, player, ts3FullClient);
					} catch (CommandException) {
						// Don't crash just because nothing is playing
					}

					if (newSong != null) {
						if (
							song == null ||
							newSong.Value.Position < song.Value.Position ||
							newSong.Value.Length != song.Value.Length ||
							newSong.Value.Link != song.Value.Link ||
							newSong.Value.Paused != song.Value.Paused || 
							// Song not frozen anymore. Only sent update when frozen for at least 2 cycles.
							songFrozen > 1 && newSong.Value.Position - song.Value.Position > TimeSpan.FromMilliseconds(750)
						) {
							if (Log.IsTraceEnabled) {
								string reason = "Unexpected reason.";
								if (song == null) reason = "First song started playing.";
								else if (newSong.Value.Position < song.Value.Position) reason = "Position < previous position.";
								else if (newSong.Value.Length != song.Value.Length) reason = "Length changed.";
								else if (newSong.Value.Link != song.Value.Link) reason = "Resource URL changed.";
								else if (newSong.Value.Paused != song.Value.Paused) reason = "Pause state changed.";
								else if (songFrozen > 1 && newSong.Value.Position - song.Value.Position > TimeSpan.FromMilliseconds(750)) reason = "Song unfroze.";

								Log.Trace("Song update sent. Reason: " + reason);
							}

							SendToAll("song", newSong.Serialize());
							songFrozen = 0;
						}

						if (
							song != null &&
							songFrozen != 0 &&
							!newSong.Value.Paused &&
							newSong.Value.Position - song.Value.Position < TimeSpan.FromMilliseconds(250)
						) {
							// Song did not advance a second
							Log.Trace("Song frozen.");
							songFrozen++;
						}
					} else if (song != null) {
						// newSong is null but previous song was not --> music stopped
						SendToAll("song", null);
						Log.Trace("Song update sent. Reason: Music stopped");
					}
					song = newSong;

					Thread.Sleep(1000);
				}
			}) {
				IsBackground = true
			};
			thread.Start();
		}

		private void QueueChanged(object sender, EventArgs _) {
			foreach (var (_, value) in server.ConnectedClients) {
				SendToClient(value, "queue", kdf.CommandQueueInternal(Uid.To(value.Uid)).Serialize());
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
			var value = JsonValue.Create(new KDFCommandsPlugin.TwitchInfo {
				ViewerCount = streamInfo.ViewerCount,
				Uptime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - streamInfo.StartedAt.ToUnix(),
				ThumbnailUrl = streamInfo.ThumbnailUrl,
				AvatarUrl = streamerInfo.ProfileImageUrl.ToString(),
				StreamerName = streamerInfo.DisplayName,
				StreamerLogin = streamerInfo.Login,
				StreamTitle = streamInfo.Title
			});
			SendToAll("twitchinfo", value.Serialize());
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
			JsonValue<Dictionary<string, IList<string>>> left,
			JsonValue<Dictionary<string, IList<string>>> right
		) {
			return ListenersEqualDirectional(left, right) && ListenersEqualDirectional(right, left);
		}

		private static bool ListenersEqualDirectional(
			JsonValue<Dictionary<string, IList<string>>> left,
			JsonValue<Dictionary<string, IList<string>>> right
		) {
			foreach (var (key, value) in left.Value) {
				if (!right.Value.ContainsKey(key)) {
					return false;
				}
				
				if (value.Any(listener => !right.Value[key].Contains(listener))) {
					return false;
				}
			}

			return true;
		}

		public void Dispose() {
			playManager.Queue.OnQueueChange -= QueueChanged;
			kdf.Autofill.OnStateChange -= AutofillChanged;
			server.Dispose();
			running = false;
		}
	}
}
