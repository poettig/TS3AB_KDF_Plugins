using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Config;
using TS3AudioBot.Plugins;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Web.Api;
using TSLib;
using TSLib.Full;
using TSLib.Helper;

namespace KDFCommands {
	public class KDFCommandsPlugin : IBotPlugin {
		internal static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		internal static ICommandBag Bag { get; } = new CommandsBag();

		internal class CommandsBag : ICommandBag {
			public IReadOnlyCollection<BotCommand> BagCommands { get; } = ImmutableList<BotCommand>.Empty;

			public IReadOnlyCollection<string> AdditionalRights { get; } = new[]
				{RightOverrideQueueCommandCheck, RightDeleteOther, RightSkipOther};
		}

		private const string TruncatedMessage =
			"\nThe number of songs to add was reduced compared to your request.\n" +
			"This can happen because the requested number of songs was not evenly divisible by the number of playlists " +
			"or at least one playlist had not enough songs (or both).";

		public const string RightDeleteOther = "del.other";
		public const string RightSkipOther = "skip.other";
		public const string RightOverrideQueueCommandCheck = "queue.view.full";

		private readonly Player player;
		private readonly PlayManager playManager;
		private readonly PlaylistManager playlistManager;
		
		private readonly ResolveContext resolver;

		private readonly TsFullClient ts3FullClient;
		private readonly Ts3Client ts3Client;

		private readonly ConfBot confBot;
		private readonly ConfPlugins confPlugins;

		internal Voting Voting { get; set; }
		internal Autofill Autofill { get; set; }
		private Description Description { get; set; }
		internal TwitchInfoUpdater TwitchInfoUpdater { get; set; }
		private UpdateWebSocket UpdateWebSocket { get; set; }
		private RecalcGainInfo recalcGainInfo;

		public KDFCommandsPlugin(
			Player player,
			PlayManager playManager,
			PlaylistManager playlistManager,
			ResolveContext resolver,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			CommandManager commandManager,
			ConfBot confBot,
			ConfPlugins confPlugins,
			Bot bot) {
			
			this.player = player;
			this.playManager = playManager;
			this.playlistManager = playlistManager;

			this.resolver = resolver;

			this.ts3FullClient = ts3FullClient;
			bot.RegenerateStatusImage();
			this.ts3Client = ts3Client;
			this.confBot = confBot;
			this.confPlugins = confPlugins;

			commandManager.RegisterCollection(Bag);
		}

		public void Initialize() {
			playManager.AfterResourceStarted += ResourceStarted;
			playManager.PlaybackStopped += PlaybackStopped;
			playManager.ResourceStopped += OnResourceStopped;

			Voting = new Voting(player, ts3Client, ts3FullClient, confBot);
			Autofill = new Autofill(ts3Client, player, playManager, playlistManager, ts3FullClient);
			Description = new Description(player, ts3Client, playManager);
			UpdateWebSocket = new UpdateWebSocket(this, player, playManager, resolver, ts3Client, ts3FullClient, confBot.WebSocket);
		}

		public void Dispose() {
			playManager.AfterResourceStarted -= ResourceStarted;
			playManager.PlaybackStopped -= PlaybackStopped;
			playManager.ResourceStopped -= OnResourceStopped;

			Description.Dispose();
			Autofill.DisableAndRemoveShadow();
			UpdateWebSocket.Dispose();

			Voting.CancelAll();
		}

		private void OnResourceStopped(object sender, SongEndEventArgs e) {
			Voting.OnSongEnd();
			if (TwitchInfoUpdater != null) {
				TwitchInfoUpdater.Dispose();
				TwitchInfoUpdater = null;
			}
		}

		private void ResourceStarted(object sender, PlayInfoEventArgs e) {
			using (System.IO.StreamWriter statfile = new System.IO.StreamWriter("stats_play.txt", true)) {
				statfile.WriteLine(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ":::" + e.ResourceData.ResourceTitle + ":::" + e.ResourceData.ResourceId + ":::" + e.PlayResource.Meta.ContainingPlaylistId + ":::" + e.PlayResource.Meta.ResourceOwnerUid);
			}

			if (e.PlayResource.BaseData.AudioType == "twitch") {
				// Start twitch info collector
				Log.Info("Start twitch update collector.");
				TwitchInfoUpdater = new TwitchInfoUpdater(confPlugins, e.PlayResource.BaseData.ResourceId);
				TwitchInfoUpdater.OnTwitchInfoChanged += UpdateWebSocket.TwitchInfoChanged;
			} else if (TwitchInfoUpdater != null) {
				TwitchInfoUpdater.Dispose();
				TwitchInfoUpdater = null;
			}
			
			var owner = e.PlayResource.Meta.ResourceOwnerUid;
			Description.Data = new DescriptionData(
				e.ResourceData.ResourceTitle,
				e.MetaData.ContainingPlaylistId,
				owner.HasValue ? ClientUtility.GetClientNameFromUid(ts3FullClient, owner.Value) : null);
		}

		private void PlaybackStopped(object sender, PlaybackStoppedEventArgs eventArgs) { Autofill.OnPlaybackStopped(eventArgs); }

		private static void Shuffle<T>(IList<T> list, Random rng) {
			int n = list.Count;
			while (n > 1) {
				n--;
				int k = rng.Next(n + 1);
				T value = list[k];
				list[k] = list[n];
				list[n] = value;
			}
		}

		public static HashSet<AudioResource> ListInteresection(IPlaylist listA, IPlaylist listB) {
			var itemsInB = new HashSet<AudioResource>(listB);
			var itemsInA = new HashSet<AudioResource>(listA);
			itemsInA.IntersectWith(itemsInB);
			return itemsInA;
		}

		public static HashSet<AudioResource> ListDifference(IPlaylist listA, IPlaylist listB) {
			var itemsInB = new HashSet<AudioResource>(listB);
			var itemsInA = new HashSet<AudioResource>(listA);
			itemsInA.ExceptWith(itemsInB);
			return itemsInA;
		}

		public static void AppendAudioResource(StringBuilder builder, int index, AudioResource item) {
			builder.Append(index).Append(": ").Append(item.ResourceTitle);
		}

		public static void AppendItemsIndexed(StringBuilder builder, IEnumerable<AudioResource> items) {
			var index = 0;
			foreach (var item in items) {
				builder.AppendLine();
				AppendAudioResource(builder, index++, item);
			}
		}

		private static void ListRemoveAll(StringBuilder builder,
			PlaylistManager playlistManager, ExecutionInformation info, string listId, HashSet<AudioResource> items) {
			MainCommands.ModifyPlaylist(playlistManager, listId, info, editor => {
				var indices = new List<int>(items.Count);
				for (var i = editor.Playlist.Count - 1; i >= 0; i--) {
					var res = editor.Playlist[i];
					if (!items.Contains(res)) 
						continue;
					indices.Add(i);

					builder.AppendLine().Append("Removed ");
					AppendAudioResource(builder, i, res);
				}

				editor.RemoveIndices(indices);
			}).UnwrapThrow();
		}

		[Command("list intersect")]
		public static string CommandListIntersect(PlaylistManager playlistManager, ExecutionInformation info, string listId, string listTo, string[] args = null) {
			var additionalArgs = args ?? Array.Empty<string>();

			var (listA, aId) = playlistManager.GetPlaylist(listId).UnwrapThrow();
			var (listB, bId) = playlistManager.GetPlaylist(listTo).UnwrapThrow();

			// Return songs in both lists
			var inBoth = ListInteresection(listA, listB);

			var builder = new StringBuilder();
			builder.Append($"{inBoth.Count} songs in the intersection of \"{aId}\" and \"{bId}\". ");

			if (!additionalArgs.Contains("--remove")) {
				builder.Append($"Append --remove to remove them from \"{aId}\".");

				if (inBoth.Count > 0)
					AppendItemsIndexed(builder, inBoth);
			} else {
				builder.Append($"Removed them from \"{aId}\".");
				ListRemoveAll(builder, playlistManager, info, aId, inBoth);
			}

			return builder.ToString();
		}

		[Command("list difference")]
		public static string CommandListDifference(PlaylistManager playlistManager, ExecutionInformation info, string listId, string listTo, string[] args = null) {
			var additionalArgs = args ?? Array.Empty<string>();

			var (listA, aId) = playlistManager.GetPlaylist(listId).UnwrapThrow();
			var (listB, bId) = playlistManager.GetPlaylist(listTo).UnwrapThrow();

			var difference = ListDifference(listA, listB);

			var builder = new StringBuilder();
			builder.Append($"{difference.Count} songs in the difference of \"{aId}\" and \"{bId}\". ");

			if (!additionalArgs.Contains("--remove")) {
				builder.Append($"Append --remove to remove them from \"{aId}\".");

				if (difference.Count > 0) {
					AppendItemsIndexed(builder, difference);
				}
			} else {
				builder.Append($"Removed them from \"{aId}\".");
				ListRemoveAll(builder, playlistManager, info, aId, difference);
			}

			return builder.ToString();
		}

		[Command("list rqueue")]
		public static string CommandListRQueue(
			PlaylistManager playlistManager,
			PlayManager playManager,
			InvokerData invoker,
			string[] parts) {

			bool truncated = false;

			// Check if last element is a number.
			// If yes, remember it as number as songs to randomly queue and the last array entry when iterating playlists.
			int count = 1;
			int numPlaylists = parts.Length;
			if (int.TryParse(parts[^1], out var countOutput)) {
				// The only element is a number --> playlist name missing
				if (parts.Length == 1) {
					throw new CommandException("No playlist to add from given.", CommandExceptionReason.CommandError);
				}

				count = countOutput;
				numPlaylists--;

				if (count < 0) {
					throw new CommandException("You can't add a negative number of songs.",
						CommandExceptionReason.CommandError);
				}

				if (count == 0) {
					throw new CommandException("Adding no songs doesn't make any sense.",
						CommandExceptionReason.CommandError);
				}
			}

			// Calculate items per playlist
			int songsPerPlaylist = count / numPlaylists;
			if (count % numPlaylists != 0) {
				truncated = true;
			}

			if (songsPerPlaylist == 0) {
				throw new CommandException("You need to add least at one song per playlist.",
					CommandExceptionReason.CommandError);
			}

			var numSongsAdded = 0;
			var allItems = new List<QueueItem>();
			for (var i = 0; i < numPlaylists; i++) {
				var userProvidedId = parts[i];
				var (plist, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
				var items = new List<AudioResource>(plist);

				int numSongsToTake = Tools.Clamp(songsPerPlaylist, 0, items.Count);
				if (numSongsToTake != songsPerPlaylist) {
					truncated = true;
				}

				numSongsAdded += numSongsToTake;

				var meta = new MetaData(invoker.ClientUid, id);
				Shuffle(items, new Random());
				allItems.AddRange(items.Take(numSongsToTake).Select(item => new QueueItem(item, meta)));
			}

			// Shuffle again across all added songs from all playlists
			Shuffle(allItems, new Random());
			int startPos;
			lock (playManager.Lock) {
				startPos = playManager.Queue.Items.Count - playManager.Queue.Index;
				playManager.Enqueue(allItems).UnwrapThrow();
			}

			return
				"Added a total of " + numSongsAdded +
				" songs across " + numPlaylists +
				" playlists to the queue at positions " + startPos +
				"-" + (startPos + numSongsAdded) +
				"." + (truncated ? TruncatedMessage : "");
		}
		
		[Command("list add")]
		public JsonArray<QueryResult> CommandListAdd(
			ExecutionInformation execInfo,
			InvokerData invoker,
			string listId,
			string message,
			ClientCall cc = null
		) {
			return ProcessQueries(execInfo, GetValidUid(invoker, null), message, cc, listId);
		}
		
		[Command("list addwithuid")]
		public JsonArray<QueryResult> CommandListAdd(
			ExecutionInformation execInfo,
			InvokerData invoker,
			string uidStr,
			string listId,
			string message,
			ClientCall cc = null
		) {
			return ProcessQueries(execInfo, GetValidUid(invoker, uidStr), message, cc, listId);
		}
		
		[Command("queuesongs")]
		public JsonArray<QueryResult> CommandQueueSong(
			ExecutionInformation execInfo,
			InvokerData invoker,
			string message,
			bool skipsearch = false,
			ClientCall cc = null
		) {
			return ProcessQueries(execInfo, GetValidUid(invoker, null), message, cc, skipsearch: skipsearch);
		}
		
		[Command("queuesongswithuid")]
		public JsonArray<QueryResult> CommandQueueSong(
			ExecutionInformation execInfo,
			InvokerData invoker,
			string uidStr,
			string message,
			bool skipsearch = false,
			ClientCall cc = null
		) {
			return ProcessQueries(execInfo, GetValidUid(invoker, uidStr), message, cc, skipsearch: skipsearch);
		}

		private JsonArray<QueryResult> ProcessQueries(
			ExecutionInformation execInfo,
			Uid uid,
			string message,
			ClientCall cc = null,
			string listId = null,
			bool front = false,
			bool skipsearch = false
		) {
			var parts = Regex.Split(message, ";+");
			if (parts.Length == 0) {
				throw new CommandException("No queries specified.", CommandExceptionReason.CommandError);
			}
			
			if (cc != null) {
				// Send update.

				var pluralS = parts.Length == 1 ? "" : "s";
				var msg = $"Received your request to add {parts.Length} song{pluralS} to ";
				if (listId != null) {
					msg += $"the playlist '{listId}'";
				} else {
					msg += $"the queue";
				}
				msg += ", processing...";
				
				ClientUtility.SendMessage(ts3Client, cc, msg);
			}
			
			var results = new List<QueryResult>();
			foreach (var part in parts) {
				// Skip if empty
				if (part == "") {
					continue;
				}

				var result = DispatchQuery(uid, execInfo, part, listId, front, skipsearch);
				if (!result.Ok) {
					if (cc != null) {
						// Send update.
						ClientUtility.SendMessage(ts3Client, cc, result.Error.ToString());
					}
					
					results.Add(new QueryResult(false, result.Error.ToString()));
				} else {
					if (cc != null) {
						// Send update.
						ClientUtility.SendMessage(ts3Client, cc, result.Value);
					}
					
					results.Add(new QueryResult(true, result.Value));	
				}
			}

			return new JsonArray<QueryResult>(results, elements => {
				if (cc != null) {
					return "Finished processing your query.";
				}

				var successes = new List<string>();
				var fails = new List<string>();

				foreach (var entry in elements) {
					if (entry.Success) {
						successes.Add(entry.Message);
					} else {
						fails.Add(entry.Message);
					}
				}

				var messageBuilder = new StringBuilder();
			
				messageBuilder.Append($"Processed {successes.Count} queries successfully:\n");
				messageBuilder.Append(string.Join("\n", successes));
				messageBuilder.Append("\n");
				messageBuilder.Append($"Failed to process {fails.Count} queries:\n");
				messageBuilder.Append(string.Join("\n", fails));

				return messageBuilder.ToString();
			});
		}

		private R<string, LocalStr> DispatchQuery(
			Uid uid,
			ExecutionInformation execInfo,
			string query,
			string listId = null,
			bool front = false,
			bool skipsearch = false
		) {
			var trimmedQuery = query.Trim();
			
			// Try to interpret it as URL.
			var resource = resolver.Load(trimmedQuery);
			if (resource.Ok) {
				return AddResource(resource.Value.BaseData, uid, execInfo, listId, front);
			}
			
			// Skip searching if requested.
			if (skipsearch) {
				return resource.Error;
			}
			
			// Was not interpretable as URL, try search.
			string type;
			string actualQuery;
			var elements = trimmedQuery.Split(":", 2);
			
			if (elements.Length < 2) {
				// No search type specified. Use youtube by default.
				type = "youtube";
				actualQuery = trimmedQuery;
			} else {
				type = elements[0];
				actualQuery = elements[1];
			}
			
			var searchResult = AddBySearch(uid, execInfo, type, actualQuery, listId, front);
			if (searchResult.Ok) {
				return searchResult.Value;
			}

			return new LocalStr(
				$"Neither adding via URL ({resource.Error}) nor search ({searchResult.Error}) was successful."
			);
		}

		private static string ComposeAddMessage(PlayManager playManager) {
			var queue = playManager.Queue;
			var realIndex = queue.Items.Count - 1;
			var index = realIndex - queue.Index;

			return $"Added '{queue.Items[realIndex].AudioResource.ResourceTitle}' at queue position {index}.";
		}

		private R<string, LocalStr> AddBySearch(
			Uid uid,
			ExecutionInformation execInfo,
			string type,
			string query,
			string listId = null,
			bool front = false
		) {
			var searchResult = resolver.Search(type, query);
			if (!searchResult.Ok) {
				return searchResult.Error;
			}

			if (searchResult.Value.Count == 0) {
				return new LocalStr($"No Results returned from '{type}' for search '{query}'");
			}

			return AddResource(searchResult.Value[0], uid, execInfo, listId, front);
		}

		private R<string, LocalStr> AddResource(
			AudioResource resource,
			Uid uid,
			ExecutionInformation execInfo,
			string listId = null,
			bool front = false
		) {
			if (front && listId != null) {
				throw new CommandException(
					"Fronting into a playlist is not supported.",
					CommandExceptionReason.CommandError
				);
			}
			
			// Add to front of queue if requested, but only if there is something playing right now.
			// Thats because EnqueueAsNextSong() is programmed to break if used when nothing is playing.
			if (front) {
				if (playManager.IsPlaying) {
					playManager.EnqueueAsNextSong(new QueueItem(resource, new MetaData(uid)));
				} else {
					playManager.Enqueue(resource, new MetaData(uid));
				}

				return $"Added '{resource.ResourceTitle}' to the front of the queue.";
			} 
			
			// Add to queue if no list id given.
			if (listId == null) {
				var result = playManager.Enqueue(resource, new MetaData(uid));
				if (!result.Ok) {
					return result.Error;
				}

				return ComposeAddMessage(playManager);
			}
			
			// Otherwise, add to list.
			if (execInfo == null) {
				return new LocalStr(
					$"Tried to add '{resource.ResourceTitle}' to playlist '{listId}' without execution information."
				);
			}
			
			R<int> addResult;
			try {
				addResult = MainCommands.ListAddItem(playlistManager, execInfo, listId, resource);
			} catch (CommandException e) {
				return new LocalStr(e.Message);
			}

			if (!addResult.Ok) {
				return $"Error occured for '{resource.ResourceTitle}': Already contained in the playlist";
			}

			return $"Added '{resource.ResourceTitle}' to playlist '{listId}' at position {addResult.Value}.";
		}
		
		public class LoggingHandler : DelegatingHandler {
			public LoggingHandler(HttpMessageHandler innerHandler)
				: base(innerHandler) { }

			protected override async Task<HttpResponseMessage> SendAsync(
				HttpRequestMessage request, CancellationToken cancellationToken) {
				Console.WriteLine("Request:");
				Console.WriteLine(request.ToString());
				if (request.Content != null) {
					Console.WriteLine(await request.Content.ReadAsStringAsync());
				}

				Console.WriteLine();

				HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

				Console.WriteLine("Response:");
				Console.WriteLine(response.ToString());
				if (response.Content != null) {
					Console.WriteLine(await response.Content.ReadAsStringAsync());
				}

				Console.WriteLine();

				return response;
			}
		}

		[Command("list createwithuid")]
		public static void CommandListCreateWithUid(PlaylistManager playlistManager, TsFullClient ts3FullClient, string uidStr, string listId) {
			MainCommands.CommandListCreate(playlistManager, new InvokerData(GetValidUid(ts3FullClient, null, uidStr)), listId);
		}
		
		private static SortedSet<int> ParseIndicesInBounds(string indicesString, int lower, int upper) {
			SortedSet<int> indices = new SortedSet<int>();
			if (upper < lower)
				return indices;
			string[] parts = Regex.Split(indicesString, ",+");
			foreach (string part in parts) {
				if (part.Length == 0) {
					continue;
				}

				var result = Regex.Match(part, "^(\\d+)-(\\d+)$");
				int start;
				int end;
				if (result.Success) {
					// Range, parse it
					start = int.Parse(result.Groups[1].Value);
					end = int.Parse(result.Groups[2].Value);
				} else if (int.TryParse(part, out int index)) {
					start = end = index;
				} else {
					throw new CommandException("Invalid index: " + part, CommandExceptionReason.CommandError);
				}

				if (end < start) {
					throw new CommandException("Given range is invalid: " + start + "-" + end,
						CommandExceptionReason.CommandError);
				}

				if (upper < end) {
					throw new CommandException("The given index is too big: " + end + " (max " + upper + ")",
						CommandExceptionReason.CommandError);
				}

				if (start < lower) {
					throw new CommandException("The given index is too small: " + start + " (min " + lower + ")",
						CommandExceptionReason.CommandError);
				}

				for (int i = start; i <= end; i++) {
					indices.Add(i);
				}
			}

			return indices;
		}

		private static SortedSet<int> ParseAndMap(PlayQueue playQueue, string indicesString) {
			return new SortedSet<int>(ParseIndicesInBounds(indicesString, 1, playQueue.Items.Count - playQueue.Index - 1).Select(entry => entry + playQueue.Index));
		}
		
		[Command("del")]
		public string CommandDelete(
			CallerInfo ci,
			ExecutionInformation info, 
			InvokerData invoker,
			string idList, 
			string uidStr = null
		) {
			return CommandDeleteInternal(playManager, ci, info, GetValidUid(invoker, uidStr), idList);
		}
	
		private static string CommandDeleteInternal(PlayManager playManager, CallerInfo ci, ExecutionInformation info, Uid uid, string idList) {
			var queue = playManager.Queue;
			var currentSongIndex = queue.Index;

			// Parse id list and map to real ids. Throws CommandException when one does not exist or there is a syntax error.
			// Set because duplicates should be ignored

			List<(int, string)> succeeded = new List<(int, string)>();
			List<(int, string)> failed = new List<(int, string)>();
			lock (playManager.Lock) {
				SortedSet<int> indices = ParseAndMap(queue, idList);
				
				foreach (int index in indices.Reverse()) {
					QueueItem item = queue.Items[index];
					if (uid == item.MetaData.ResourceOwnerUid || (!ci.ApiCall && info.HasRights(RightDeleteOther))) {
						queue.Remove(index);
						succeeded.Add((index - currentSongIndex, item.AudioResource.ResourceTitle));
					} else {
						failed.Add((index - currentSongIndex, item.AudioResource.ResourceTitle));
					}
				}

				playManager.OnQueueChanged(indices.Min);
			}

			succeeded.Reverse();
			failed.Reverse();

			StringBuilder output = new StringBuilder();
			if (succeeded.Count > 0) {
				output.Append("Removed the following songs:");
				foreach ((int index, string title) in succeeded) {
					output.Append('\n');
					output.Append('[').Append(index).Append("] ").Append(title);
				}
			}

			if (failed.Count > 0) {
				if (succeeded.Count > 0)
					output.Append('\n');
				output.Append("Failed to remove the following songs:");
				foreach ((int index, string title) in failed) {
					output.Append('\n');
					output.Append('[').Append(index).Append("] ").Append(title);
				}
			}

			return output.ToString();
		}

		public class QueueItemInfo {
			[JsonProperty(PropertyName = "Title")] public string Title { get; set; }

			[JsonProperty(PropertyName = "UserId")]
			public string UserId { get; set; }
			
			[JsonProperty(PropertyName = "UserName")]
			public string UserName { get; set; }

			[JsonProperty(PropertyName = "ContainingListId")]
			public string ContainingListId { get; set; }
			
			[JsonProperty(PropertyName = "ResourceId")]
			public string ResourceId { get; set; }
			
			[JsonProperty(PropertyName = "ResourceType")]
			public string ResourceType { get; set; }
		}

		public class CurrentQueueInfo {
			[JsonProperty(PropertyName = "Current")]
			public QueueItemInfo Current { get; set; }

			[JsonProperty(PropertyName = "Items")] public List<QueueItemInfo> Items { get; set; }
		}

		private void AppendSong(StringBuilder target, QueueItemInfo qi, bool restrict) {
			target.Append(restrict ? "Hidden Song Name" : qi.Title);
			target.Append(" - ").Append(ClientUtility.GetClientNameFromUid(ts3FullClient, Uid.To(qi.UserId)));

			if (qi.ContainingListId != null)
				target.Append(" <Playlist: ").Append(qi.ContainingListId).Append(">");
		}

		private string QueueInfoToString(CurrentQueueInfo queueInfo) {
			if (queueInfo.Current == null) {
				return "There is nothing on right now...";
			}

			var output = new StringBuilder();
			if (queueInfo.Current != null) {
				output.Append("Current song: ");
				AppendSong(output, queueInfo.Current, false);
			}

			for (var index = 0; index < queueInfo.Items.Count; index++) {
				var item = queueInfo.Items[index];
				output.AppendLine();
				output.Append('[').Append(index + 1).Append("] ");
				AppendSong(output, item, item.Title == null);
			}

			return output.ToString();
		}

		private QueueItemInfo ToQueueItemInfo(QueueItem qi, bool restrict) {
			return new QueueItemInfo {
				ContainingListId = qi.MetaData.ContainingPlaylistId,
				Title = restrict ? null : qi.AudioResource.ResourceTitle,
				UserId = qi.MetaData.ResourceOwnerUid.GetValueOrDefault(Uid.Null).Value,
				UserName = ClientUtility.GetClientNameFromUid(ts3FullClient, qi.MetaData.ResourceOwnerUid.GetValueOrDefault(Uid.Null)),
				ResourceId = qi.AudioResource.ResourceId,
				ResourceType = qi.AudioResource.AudioType
			};
		}

		[Command("showqueue")]
		public JsonValue<CurrentQueueInfo> CommandShowQueue(
			InvokerData invoker,
			ExecutionInformation info,
			string uidStr = null
		) {
			return CommandQueueInternal(GetValidUid(invoker, uidStr), info);
		}
		
		[Command("showfullqueue")]
		public JsonValue<CurrentQueueInfo> CommandShowFullQueue(
			InvokerData invoker,
			ExecutionInformation info,
			string uidStr = null
		) {
			return CommandQueueInternal(GetValidUid(invoker, uidStr), info, false);
		}

		public JsonValue<CurrentQueueInfo> CommandQueueInternal(Uid uid, ExecutionInformation info = null, bool hideOthers = true) {
			if (!hideOthers && (info == null || !info.HasRights(RightOverrideQueueCommandCheck)))
				throw new CommandException("You have no permission to view the full queue.",
					CommandExceptionReason.CommandError);
			var queueInfo = new CurrentQueueInfo();
			lock (playManager) {
				bool ShouldRestrict(QueueItem qi) => hideOthers && qi.MetaData.ResourceOwnerUid != uid;
				queueInfo.Items = playManager.Queue.Items.Skip(playManager.Queue.Index + 1)
					.Select(qi => ToQueueItemInfo(qi, ShouldRestrict(qi))).ToList();
				if (playManager.IsPlaying)
					queueInfo.Current = ToQueueItemInfo(playManager.Queue.Current, false);
			}

			return new JsonValue<CurrentQueueInfo>(queueInfo, QueueInfoToString);
		}

		[Command("recentlyplayed")]
		public JsonArray<QueueItemInfo> CommandRecentlyPlayed(int? count) {
			List<QueueItemInfo> items;
			lock (playManager) {
				int start = Math.Max(0, playManager.Queue.Index - count ?? 5);
				var take = playManager.Queue.Index - start;
				items = playManager.Queue.Items.Skip(start).Take(take).Select(qi => ToQueueItemInfo(qi, false))
					.ToList();
			}

			return new JsonArray<QueueItemInfo>(items, infos => {
				var builder = new StringBuilder();
				for (int i = 0; i < infos.Count; ++i) {
					if (i != 0)
						builder.AppendLine();
					builder.Append("[-").Append(infos.Count - i).Append("] ");
					AppendSong(builder, infos[i], false);
				}

				return builder.ToString();
			});
		}

		[Command("skip")]
		public static string CommandSkip(
			PlayManager playManager,
			ExecutionInformation info,
			InvokerData invoker,
			int? countOpt = null) {
			var queue = playManager.Queue;

			int count;
			lock (playManager.Lock) {
				if (!playManager.IsPlaying)
					throw new CommandException("There is no song currently playing.",
						CommandExceptionReason.CommandError);
				count = Tools.Clamp(countOpt.GetValueOrDefault(1), 0, queue.Items.Count - queue.Index);
				for (int i = 0; i < count; i++) {
					if (invoker.ClientUid != queue.Items[queue.Index].MetaData.ResourceOwnerUid &&
					    !info.HasRights(RightSkipOther)) {
						throw new CommandException($"You have no permission to skip song {i}.",
							CommandExceptionReason.CommandError);
					}
				}

				playManager.Next(count).UnwrapThrow();
			}

			return $"Skipped {count} songs.";
		}

		[Command("front")]
		public JsonArray<QueryResult> CommandFront(
			ExecutionInformation execInfo,
			InvokerData invoker,
			string message,
			ClientCall cc = null
		) {
			return ProcessQueries(execInfo, GetValidUid(invoker, null), message, cc, front: true);
		}
		
		[Command("frontwithuid")]
		public JsonArray<QueryResult> CommandFront(
			ExecutionInformation execInfo,
			InvokerData invoker,
			string uidStr,
			string message,
			ClientCall cc = null
		) {
			return ProcessQueries(execInfo, GetValidUid(invoker, uidStr), message, cc, front: true);
		}

		[Command("list item queue")]
		public string CommandItemQueue(
			InvokerData invoker, 
			string userProvidedId,
			string indicesString, string uidStr = null
		) {
			var (plist, listId) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			var indices = ParseIndicesInBounds(indicesString, 0, plist.Count - 1);
			var items = new List<AudioResource>(indices.Count);
			items.AddRange(indices.Select(index => plist[index]));

			playManager.Enqueue(items, new MetaData(GetValidUid(invoker, uidStr), listId)).UnwrapThrow();

			if (indices.Count == 1) {
				return $"Queued '{items[0].ResourceTitle}' from playlist {listId}.";
			}

			return $"Queued {items.Count} items from playlist {listId}.";
		}
		
		[Command("list item front")]
		public string CommandItemFront(
			InvokerData invoker,
			string userProvidedId,
			string indicesString, 
			string uidStr = null
		) {
			var (plist, _) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			var indices = ParseIndicesInBounds(indicesString, 0, plist.Count - 1);

			if (indices.Count != 1) {
				throw new CommandException("Only single index is allowed for fronting.", CommandExceptionReason.CommandError);
			}

			var resource = plist[indices.First()];
			playManager.EnqueueAsNextSong(new QueueItem(resource, new MetaData(GetValidUid(invoker, uidStr))));
			return "Added '" + resource.ResourceTitle + "' to the front of the queue.";
		}
		
		[Command("list item replaceurl")]
		public static string ListItemReplaceUrlCommand(
			PlaylistManager playlistManager,
			ExecutionInformation info,
			ResolveContext resolver,
			string listId,
			int index,
			string url,
			string[] args = null) {
			var additionalArgs = args ?? Array.Empty<string>();
			
			string id;
			AudioResource resource;

			{
				var (list, s) = playlistManager.GetPlaylist(listId).UnwrapThrow();
				MainCommands.DoBoundsCheck(list, index);
				id = s;
				resource = list[index];
				if (resource.AudioType != "youtube")
					throw new CommandException("You can't replace the URL of a non-youtube entry.",
						CommandExceptionReason.CommandError);
			}

			AudioResource newResource;
			{
				var newRes = resolver.Load(url).UnwrapThrow().BaseData;
				newResource = new AudioResource(
					newRes.ResourceId,
					resource.TitleIsUserSet != null && resource.TitleIsUserSet.Value
						? resource.ResourceTitle
						: newRes.ResourceTitle,
					newRes.AudioType,
					resource.AdditionalData,
					resource.TitleIsUserSet
				);
			}

			if (additionalArgs.Contains("--all")) {
				if (!playlistManager.TryGetUniqueResourceInfo(resource, out var resourceInfo))
					throw new CommandException("Could not find the list item", CommandExceptionReason.InternalError);

				var builder = new StringBuilder();
				builder.AppendJoin(", ", resourceInfo.ContainingLists.Select(kv => '\'' + kv.Key + '\''));

				// Check all lists for modifiable
				foreach (var occKv in resourceInfo.ContainingLists) {
					var (list, lid) = playlistManager.GetPlaylist(occKv.Key).UnwrapThrow();
					MainCommands.CheckPlaylistModifiable(lid, list, info);
				}

				playlistManager.ChangeItemAtDeep(id, index, newResource).UnwrapThrow();
				return $"Successfully replaced the youtube URL of '{resource.ResourceTitle}' in playlists {builder}.";
			} else {
				bool replaced = false;
				MainCommands.ModifyPlaylist(playlistManager, id, info, editor => {
					replaced = editor.ChangeItemAt(index, newResource);
					if(!replaced)
						editor.RemoveItemAt(index);
				}).UnwrapThrow();
				if(!replaced)
					return $"This song already existed in the playlist '{id}', removed the entry to replace.";
				return $"Successfully replaced the youtube URL of '{resource.ResourceTitle}' in playlist '{id}' at index {index}.";
			}
		}

		[Command("checkuser online byuid")]
		public static bool CommandCheckUserOnlineByUid(TsFullClient ts3FullClient, string uid) {
			return ClientUtility.ClientIsOnline(ts3FullClient, Uid.To(uid));
		}

		[Command("autofillstatus")]
		public JsonValue<AutofillStatus> CommandAutofillStatus() {
			return Autofill.Status();
		}

		[Command("autofilloff")]
		public void CommandAutofillOff(InvokerData invoker, string uidStr = null) {
			Autofill.Disable(GetValidUid(invoker, uidStr));
		}

		[Command("autofill")]
		public void CommandAutofill(InvokerData invoker, string[] playlistIds = null) {
			Autofill.CommandAutofill(GetValidUid(invoker, null), playlistIds);
		}
		
		[Command("autofillwithuid")]
		public void CommandAutofillWithUid(InvokerData invoker, string uidStr, string[] playlistIds = null) {
			Autofill.CommandAutofill(GetValidUid(invoker, uidStr), playlistIds);
		}

		[Command("skiporvoteskip")]
		public JsonValue<Voting.Result> CommandSkipOrVoteSkip(
			ExecutionInformation info, 
			InvokerData invoker,
			string uidStr = null
		) {
			var uid = GetValidUid(invoker, uidStr);

			lock (playManager.Lock) {
				var current = playManager.Queue.Current;
				if (current != null && current.MetaData.ResourceOwnerUid == uid) {
					playManager.Next();
					ts3Client.SendChannelMessage($"{ClientUtility.GetClientNameFromUid(ts3FullClient, uid)} skipped the current song.");
					return new JsonValue<Voting.Result>(new Voting.Result {
						VoteAdded = true,
						VoteComplete = true,
						VoteCount = 1,
						VotesChanged = true,
						VotesNeeded = 1
					});
				}
			}
			
			return StartVote(info, uid, "skip", null);
		}

		[Command("votewithuid")]
		public JsonValue<Voting.Result> CommandStartVoteWithUid(
			ExecutionInformation info,
			InvokerData invoker,
			string uidStr, 
			string command,
			string args = null
		) {
			return StartVote(info, GetValidUid(invoker, uidStr), command, args);
		}

		[Command("vote")]
		public JsonValue<Voting.Result> CommandVote(
			ExecutionInformation info, 
			InvokerData invoker, 
			string command,
			string args = null
		) {
			return StartVote(info, invoker.ClientUid, command, args);
		}

		private JsonValue<Voting.Result> StartVote(ExecutionInformation info, Uid client, string command, string args) {
			var res = Voting.CommandVote(info, client, command, args);
			return new JsonValue<Voting.Result>(res, r => null);
		}

		[Command("twitchinfo")]
		public JsonValue<TwitchInfo> CommandTwitchInfo() {
			if (TwitchInfoUpdater == null) {
				throw new CommandException("No twitch stream currently running.", CommandExceptionReason.MissingContext);
			}

			if (playManager == null) {
				throw new CommandException("Missing playManager for some reason.", CommandExceptionReason.MissingContext);
			}

			string issuerName;
			Uid? issuerUid;
			lock (playManager) {
				if (playManager.CurrentPlayData == null) {
					throw new CommandException("Missing CurrentPlayData for some reason.",
						CommandExceptionReason.MissingContext);
				}

				if (playManager.CurrentPlayData.MetaData == null) {
					throw new CommandException("Missing MetaData for some reason.",
						CommandExceptionReason.MissingContext);
				}

				issuerName = null;
				issuerUid = playManager.CurrentPlayData.MetaData.ResourceOwnerUid;
				if (issuerUid != null) {
					issuerName = ClientUtility.GetClientNameFromUid(ts3FullClient, issuerUid.Value);
				}
			}

			var streamInfo = TwitchInfoUpdater?.StreamInfo?.Data[0];
			var streamerInfo = TwitchInfoUpdater?.StreamerInfo?.Data[0];

			if (streamInfo == null) {
				throw new CommandException("Missing StreamInfo for some reason.", CommandExceptionReason.MissingContext);
			}
			
			if (streamerInfo == null) {
				throw new CommandException("Missing StreamerInfo for some reason.", CommandExceptionReason.MissingContext);
			}
			
			return JsonValue.Create(new TwitchInfo {
				ViewerCount = streamInfo.ViewerCount,
				Uptime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - streamInfo.StartedAt.ToUnix(),
				ThumbnailUrl = streamInfo.ThumbnailUrl,
				AvatarUrl = streamerInfo.ProfileImageUrl.ToString(),
				StreamerName = streamerInfo.DisplayName,
				StreamerLogin = streamerInfo.Login,
				StreamTitle = streamInfo.Title,
				IssuerUid = issuerUid.ToString(),
				IssuerName = issuerName,
			}, info => "[" + info.IssuerName + " " + info.Uptime + "] - " + info.StreamTitle + " (" + info.ViewerCount + " viewers) - https://twitch.tv/" + info.StreamerLogin);
		}
		
		[Command("listeners")]
		public static JsonValue<Dictionary<string, IList<(string, string)>>> CommandListeners(Ts3Client ts3Client, TsFullClient ts3FullClient, Player player) {
			var channelListeners = ClientUtility.GetListeningClients(ts3Client, ts3FullClient)
				.Where(client => !player.WebSocketPipe.Listeners.Contains(client.Uid.ToString()))
				.Select(client => (client.Uid.ToString(), ClientUtility.GetClientNameFromUid(ts3FullClient, client.Uid)))
				.ToList();

			var websocketListeners = player.WebSocketPipe.Listeners.Select(
				listener => (listener, ClientUtility.GetClientNameFromUid(ts3FullClient, Uid.To(listener)))
			).ToList();

			return JsonValue.Create(new Dictionary<string, IList<(string, string)>> {
				{ "channel", channelListeners }	,
				{ "websocket", websocketListeners }
			}, data => $"Via Website: {string.Join(", ", data["websocket"].Select(value => value.Item2).ToList())}\nIn Channel: {string.Join(", ", data["channel"].Select(value => value.Item2).ToList())}");
		}

		[Command("randomsongs")]
		public static JsonArray<SongRandomizerResult> CommandRandomSongs(PlaylistManager playlistManager, int count, string[] playlists = null) {
			if (count <= 0) {
				throw new CommandException("You cannot randomize 0 or less songs.", CommandExceptionReason.CommandError);
			}

			var playlistsHashMap = playlists != null && playlists.Length != 0 ? new HashSet<string>(playlists) : null;
			var randomSongs = SongRandomizer.GetRandomSongs(count, playlistManager, playlistsHashMap);
			return new JsonArray<SongRandomizerResult>(randomSongs);
		}
		
		[Command("youtubestream")]
		public static string CommandYoutubeStream(PlayManager playManager, ResolveContext resolver, ClientCall cc, string url) {
			var resource = resolver.Load(url).UnwrapThrow();
			var newResource = resource.BaseData.WithGain(0);
			playManager.Enqueue(newResource, new MetaData(cc.ClientUid)).UnwrapThrow();
			return ComposeAddMessage(playManager);
		}

		[Command("songcount")]
		public static string CommandSongCount(PlaylistManager playlistManager) {
			return $"There are currently {playlistManager.Count} items"
			       + $" across {playlistManager.GetAvailablePlaylists().Length} playlists.\n"
			       + $" {playlistManager.UniqueCount} or"
			       + $" {(double) playlistManager.UniqueCount / playlistManager.Count * 100:0.00}% of them are unique.";
		}

		private bool isGainRecalculationCandidate(AudioResource resource, string flagKey) {
			// Gain calculation only for "youtube" tracks
			return resource.AudioType == "youtube" && (
				resource.AdditionalData == null
				|| resource.Gain == null
				|| !resource.AdditionalData.ContainsKey(flagKey)
				|| resource.AdditionalData[flagKey] != "true"
			);
		}

		[Command("recalcgain")]
		public string CommandRecalculateGain(ClientCall cc) {
			const string flagKey = "fully_analysed";

			// Already running.
			if (recalcGainInfo != null) {
				var analyzed = recalcGainInfo.Successful + recalcGainInfo.Failed;
				var elapsed = recalcGainInfo.Watch.Elapsed;
				var ppm = elapsed.TotalMinutes < 1 ? analyzed : analyzed / elapsed.TotalMinutes;
				var etaString = "N/A";
				try {
					var eta = TimeSpan.FromMinutes((recalcGainInfo.ToProcessEstimate - analyzed) / ppm);
					etaString = $"{eta.Days}d, {eta.Hours:00}h {eta.Minutes:00}m {eta.Seconds:00}s";
				} catch (OverflowException) {
					// Its fine, we have a default string for that.
				}

				return "Gain recalculation already running.\n"
				       + $"{recalcGainInfo.Progress}/{recalcGainInfo.DbSize}"
				       + $" ({(double) recalcGainInfo.Progress / recalcGainInfo.DbSize * 100:0.00}%) scanned,"
				       + $" {analyzed} analysed: {recalcGainInfo.Successful} successful, {recalcGainInfo.Failed} failed.\n"
				       + $" Running since {elapsed.Days}d, {elapsed.Hours:00}h {elapsed.Minutes:00}m {elapsed.Seconds:00}s,"
				       + $" {ppm:0.0} items/m,"
				       + $" ETA {etaString}.";
			}

			// Estimate how many items there are to process (estimation because that can change if an item in a playlist
			// is removed or added).
			var toProcess = 0;
			foreach (var info in playlistManager.GetAvailablePlaylists()) {
				foreach (var entry in playlistManager.GetPlaylist(info.Id).UnwrapThrow().list) {
					if (isGainRecalculationCandidate(entry, flagKey)) {
						toProcess++;
					}
				}
			}

			recalcGainInfo = new RecalcGainInfo(playlistManager.Count, toProcess);
			var thread = new Thread(() => {
				var playlists = playlistManager.GetAvailablePlaylists();
				for (var plIdx = 0; plIdx < playlists.Length; plIdx++) {
					// Get playlist.
					var playlistOption = playlistManager.GetPlaylist(playlists[plIdx].Id);

					if (!playlistOption.Ok) {
						Log.Error($"Failed to fetch playlist {playlists[plIdx].Id}.");
						return;
					}

					// Remember analyze stats from before starting the playlist in order to remove results
					// in case the playlist disappears.
					var toProcessEstimate = playlistOption.Value.list.Count(r => isGainRecalculationCandidate(r, flagKey));
					var previousSuccessfuls = recalcGainInfo.Successful;
					var previousFails = recalcGainInfo.Failed;
					var previousProgress = recalcGainInfo.Progress;
					
					// Find candidate that is not fully analyzed yet.
					for (var resIdx = 0; resIdx < playlistOption.Value.list.Count; resIdx++) {
						var resource = playlistOption.Value.list[resIdx];
						var identifier = $"{playlistOption.Value.id}:{resIdx}:{resource.UniqueId}";

						recalcGainInfo.Progress++;
						
						if (!isGainRecalculationCandidate(resource, flagKey)) {
							continue;
						}

						var newResource = resource.DeepCopy();

						// Add additional data if it does not exist yet.
						if (newResource.AdditionalData == null) {
							newResource = newResource.WithNewAdditionalData();
						}

						// Resolve the resource to an analyzable URL.
						var playResourceOption = resolver.Load(newResource);
						if (!playResourceOption.Ok) {
							Log.Error($"Problem resolving '{identifier}': {playResourceOption.Error}");
							recalcGainInfo.Failed++;
							continue;
						}

						// Analyse the resource fully.
						var gain = player.FfmpegProducer.VolumeDetect(
							playResourceOption.Value.PlayUri,
							new CancellationToken(),
							true
						);
						newResource = newResource.WithGain(gain);
						newResource.AdditionalData[flagKey] = "true";

						// Lock the playlistManager
						lock (playlistManager.Lock) {
							// Re-fetch the playlist in order to check if it still exists.
							var plOpt = playlistManager.GetPlaylist(playlists[plIdx].Id);
							if (!plOpt.Ok) {
								Log.Error($"Playlist {playlists[plIdx].Id} disappeared during analysis. Skipping playlist.");
								
								// Update the DbSize to ignore the disappeared playlist for stats.
								recalcGainInfo.DbSize -= playlists[plIdx].SongCount;
								
								// Update process estimate to ignore the disappeared playlist for stats.
								// Only subtract as many items as there where candidates in the playlist.
								recalcGainInfo.ToProcessEstimate -= toProcessEstimate;
								
								// Update stats by resetting them to the value bofore starting to scan the disappeared playlist.
								recalcGainInfo.Successful = previousSuccessfuls;
								recalcGainInfo.Failed = previousFails;
								recalcGainInfo.Progress = previousProgress;
								
								break;
							}
							
							// Check if there is still the same resource at this index in the playlist.
							if (!plOpt.Value.list[resIdx].Equals(newResource)) {
								Log.Error($"Failed replacing '{identifier}': Resource at index {resIdx} changed during analysis.");
								
								// Trigger that playlist is completely scanned again.
								plIdx--;
								break;
							}
							
							if (!playlistManager.TryGetUniqueResourceInfo(newResource, out var resourceInfo)) {
								Log.Error($"Failed replacing '{identifier}': Could not find unique resource info.");
								recalcGainInfo.Failed++;
								continue;
							}
							
							// Replace the old entry with the new one.
							// Do that for all occurences of this audio resource across all playlists.
							var result = playlistManager.ChangeItemAtDeep(playlists[plIdx].Id, resIdx, newResource);
							if (!result.Ok) {
								Log.Error($"Failed replacing '{identifier}': {result.Error}");
								recalcGainInfo.Failed++;
								return;
							}

							Log.Info(
								$"Finished full analysis of '{identifier}': " +
								$"Changed gain from {resource.Gain ?? 0} dB to {gain} dB, affected the playlists" +
								$" {string.Join(", ", resourceInfo.ContainingLists.Select(kv => '\'' + kv.Key + '\''))}."
							);
							recalcGainInfo.Successful += resourceInfo.ContainingLists.Count();
						}
					}
				}

				recalcGainInfo.Watch.Stop();
				var analyzed = recalcGainInfo.Successful + recalcGainInfo.Failed;
				var elapsed = recalcGainInfo.Watch.Elapsed;
				var msg = "Recalculation of gain values done.\n"
				          + $"{analyzed}/{recalcGainInfo.DbSize}"
				          + $" ({(double) analyzed / recalcGainInfo.DbSize * 100:0.00}%) analysed"
				          + $" (estimation was {recalcGainInfo.ToProcessEstimate}/{recalcGainInfo.DbSize}):"
				          + $" {recalcGainInfo.Successful} successful, {recalcGainInfo.Failed} failed."
				          + $" Recalucation took {elapsed.Days}d, {elapsed.Hours:00}h {elapsed.Minutes:00}m {elapsed.Seconds:00}s.";
				Log.Info(msg);
				ClientUtility.SendMessage(ts3Client, cc, msg);
				recalcGainInfo = null;
			}) {
				IsBackground = true
			};
			thread.Start();

			return $"Started gain recalculation for {recalcGainInfo.ToProcessEstimate}/{recalcGainInfo.DbSize} elements.";
		}

		private Uid GetValidUid(InvokerData invoker, string uidStr) {
			return GetValidUid(ts3FullClient, invoker, uidStr);
		}
		
		private static Uid GetValidUid(TsFullClient ts3FullClient, InvokerData invoker, string uidStr) {
			Uid uid;
			if (uidStr != null) {
				uid = Uid.To(uidStr);

				// Check if user exists, throws exception if not.
				ClientUtility.GetClientNameFromUid(ts3FullClient, uid);	
			} else if (invoker != null && !invoker.IsAnonymous) {
				uid = invoker.ClientUid;
			} else {
				throw new CommandException("No valid uid given.", CommandExceptionReason.CommandError);
			}
			
			return uid;
		}

		public class TwitchInfo {
			[JsonProperty("ViewerCount")]
			public long ViewerCount { get; set; }
			
			[JsonProperty("Uptime")]
			public long Uptime { get; set; }

			[JsonProperty("ThumbnailUrl")]
			public string ThumbnailUrl { get; set; }
			
			[JsonProperty("AvatarUrl")]
			public string AvatarUrl { get; set; }

			[JsonProperty("StreamerName")]
			public string StreamerName { get; set; }
			
			[JsonProperty("StreamerLogin")]
			public string StreamerLogin { get; set; }

			[JsonProperty("StreamTitle")]
			public string StreamTitle { get; set; }
			
			[JsonProperty("IssuerUid")]
			public string IssuerUid { get; set; }
			
			[JsonProperty("IssuerName")]
			public string IssuerName { get; set; }
		}
		
		public class QueryResult {
			public bool Success { get; }
			public string Message { get; }
			
			public QueryResult(bool success, string message) {
				Success = success;
				Message = message;
			}
		}

		private class RecalcGainInfo {
			public int DbSize { get; set; }
			public int ToProcessEstimate { get; set; }
			public int Progress { get; set; }
			public int Successful { get; set; }
			public int Failed { get; set; }
			public Stopwatch Watch { get; }

			public RecalcGainInfo(int dbSize, int toProcessEstimate) {
				DbSize = dbSize;
				ToProcessEstimate = toProcessEstimate;
				Watch = Stopwatch.StartNew();
			} 
		}
	}
}