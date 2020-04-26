using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.CommandSystem.Text;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;
using TS3AudioBot.Plugins;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Sessions;
using TS3AudioBot.Web.Api;
using TSLib;
using TSLib.Full;
using TSLib.Full.Book;
using TSLib.Helper;

public class KDFCommands : IBotPlugin {
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger(); // TODO this does not get printed

	internal static ICommandBag Bag { get; } = new CommandsBag();

	internal class CommandsBag : ICommandBag {
		public IReadOnlyCollection<BotCommand> BagCommands { get; } = ImmutableList<BotCommand>.Empty;

		public IReadOnlyCollection<string> AdditionalRights { get; } = new[]
			{RightOverrideQueueCommandCheck, RightDeleteOther, RightSkipOther};
	}

	private const string YOUTUBE_URL_REGEX =
		"^(?:https?:\\/\\/)(?:www\\.)?(?:youtube\\.com\\/watch\\?v=(.*?)(?:&.*)*|youtu\\.be\\/(.*?)\\??.*)$";
	private const string TRUNCATED_MESSAGE =
		"\nThe number of songs to add was reduced compared to your request.\n" +
		"This can happen because the requested number of songs was not evenly divisible by the number of playlists " +
		"or at least one playlist had not enough songs (or both).";

	public const string RightDeleteOther = "del.other";
	public const string RightSkipOther = "skip.other";
	public const string RightOverrideQueueCommandCheck = "queue.view.full";

	private static readonly TimeSpan MinIdleTimeForVoteIgnore = TimeSpan.FromMinutes(10);

	private delegate void PartHandler(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		string query,
		string target
	);

	private Thread descThread;
	private bool running;

	private class DescriptionThreadData {
		public string Title { get; }
		public string PlaylistId { get; }
		public string Username { get; }
		public DescriptionThreadData(string title, string playlistId, string username) {
			Title = title;
			PlaylistId = playlistId;
			Username = username;
		}
	}

	private DescriptionThreadData descriptionThreadData;

	private Player player;
	private PlayManager playManager;
	private PlaylistManager playlistManager;
	private Ts3Client ts3Client;
	private TsFullClient ts3FullClient;

	private bool autofill = false;
	private List<string> autofillFrom;

	private VoteData voteData;

	public KDFCommands(
			Player player,
			PlayManager playManager,
			PlaylistManager playlistManager,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			CommandManager commandManager,
			BotInjector injector,
			ConfBot config) {
		
		this.player = player;
		this.playManager = playManager;
		this.playlistManager = playlistManager;
		this.ts3Client = ts3Client;
		this.ts3FullClient = ts3FullClient;
		this.running = false;
		playManager.OnPlaybackEnded();
		commandManager.RegisterCollection(Bag);

		if (!injector.TryGet(out voteData)) {
			voteData = new VoteData(ts3Client, config);
			injector.AddModule(voteData);
		}
	}

	public void Initialize() {
		playManager.AfterResourceStarted += ResourceStarted;
		playManager.PlaybackStopped += PlaybackStopped;
		playManager.ResourceStopped += OnResourceStopped;

		descThread = new Thread(DescriptionUpdater);
		descThread.IsBackground = true;
		running = true;
		descThread.Start();
	}

	private void OnResourceStopped(object sender, SongEndEventArgs e) { 
		voteData.OnSongEnd(sender, e);
	}

	private void ResourceStarted(object sender, PlayInfoEventArgs e) {
		descriptionThreadData = new DescriptionThreadData(
			e.ResourceData.ResourceTitle, 
			e.MetaData.ContainingPlaylistId, 
			GetClientNameFromUid(ts3FullClient, e.PlayResource.Meta.ResourceOwnerUid));
	}

	private void PlaybackStopped(object sender, EventArgs e) {
		if (playManager.Queue.Items.Count == playManager.Queue.Index && autofill) {
			if (!ts3Client.IsDefinitelyAlone()) {
				var result = PlayRandom();
				if (!result.Ok) {
					ts3Client.SendChannelMessage("Could not play a new autofill song: " + result.Error.Str);
				} else {
					return;
				}
			}

			DisableAutofill();
		}
	}

	private E<LocalStr> PlayRandom() {
		// Play random song from a random playlist currently in the selected set

		// Get total number of songs from all selected playlists
		var numSongs = 0;
		var playlists = new List<(string, IReadOnlyPlaylist)>();
		foreach (string playlistId in playlistManager.GetAvailablePlaylists().UnwrapThrow().Select(entry => entry.Id)) {
			if (autofillFrom != null && !autofillFrom.Contains(playlistId)) {
				continue;
			}

			var playlist = playlistManager.LoadPlaylist(playlistId).UnwrapThrow();
			playlists.Add((playlistId, playlist));
			numSongs += playlist.Items.Count;
		}
		Console.WriteLine("Found {0} songs across {1} playlists.", numSongs, playlists.Count);

		var plId = "";
		AudioResource resource = null;
		for (var i = 0; i < 5; i++) {
			// Draw random song number
			var songIndex = new Random().Next(0, numSongs);
			Console.WriteLine("Drawn song index: {0}", songIndex);

			// Find the randomized song
			foreach (var (playlistId, playlist) in playlists) {
				// Song is in this playlist
				if (songIndex < playlist.Items.Count) {
					Console.WriteLine("Found the song in playlist '{0}' at index {1}.", playlistId, songIndex);
					plId = playlistId;
					resource = playlist[songIndex].AudioResource;
					break;
				}

				// Song is in another playlist
				songIndex -= playlist.Items.Count;
			}

			// Check if the song was already played in the last 250 songs, if not take this one.
			// If items.count < 250, the subtraction is negative, meaning that j == 0 will be reached first
			var foundDuplicate = false;
			var items = playManager.Queue.Items;
			if (items.Count > 0) {
				for (var j = items.Count - 1; j != 0 && j >= items.Count - 250; j--) {
					if (items[j].AudioResource.Equals(resource)) {
						Console.WriteLine("The song was already played {0} songs ago. Searching another one...", items.Count - j - 1);
						foundDuplicate = true;
					}
				}
			}

			if (!foundDuplicate) {
				break;
			}
		}

		// Play song
		Console.WriteLine("Playing the song '{0}' from playlist '{1}'.", resource.ResourceTitle, plId);
		return playManager.Enqueue(resource, new MetaData(ts3FullClient.Identity.ClientUid, plId));
	}

	private void DescriptionUpdater() {
		while (running) {
			if (playManager.IsPlaying && descriptionThreadData != null) {
				var data = descriptionThreadData;
				int queueLength = playManager.Queue.Items.Count - playManager.Queue.Index - 1;
				TimeSpan timeLeftTS = player.Length.Subtract(player.Position);
				StringBuilder builder = new StringBuilder();
				builder.Append("[");
				if (data.Username != null)
					builder.Append(data.Username);
				builder.Append(" ").Append($"{timeLeftTS:mm\\:ss}");
				builder.Append(" Q").Append(queueLength).Append("] ").Append(data.Title);
				if (data.PlaylistId != null)
					builder.Append(" <Playlist: ").Append(data.PlaylistId).Append(">");
				ts3Client.ChangeDescription(builder.ToString()).UnwrapThrow();
			}
			Thread.Sleep(1000);
		}
	}

	private static string GetClientNameFromUid(TsFullClient ts3FullClient, Uid? id) {
		return id.HasValue ? ts3FullClient.GetClientNameFromUid(id.Value).Value.Name : null;
	}

	private static bool HasPlaylistId(PlayQueue queue, int index) {
		string listId = queue.Items[index].MetaData.ContainingPlaylistId;
		return listId != null && listId != "";
	}

	private static string GetPlaylistId(PlayQueue queue, int index) {
		return queue.Items[index].MetaData.ContainingPlaylistId;
	}

	private static string GetTitle(PlayQueue queue, int index) {
		return queue.Items[index].AudioResource.ResourceTitle;
	}

	private static string GetTitle(IReadOnlyPlaylist queue, int index) {
		return queue.Items[index].AudioResource.ResourceTitle;
	}

	private static string GetName(PlayQueue queue, int index, TsFullClient ts3FullClient) {
		return GetClientNameFromUid(ts3FullClient, queue.Items[index].MetaData.ResourceOwnerUid);
	}

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
		if (Int32.TryParse(parts[parts.Length - 1], out int countOutput)) {
			// The only element is a number --> playlist name missing
			if (parts.Length == 1) {
				throw new CommandException("No playlist to add from given.", CommandExceptionReason.CommandError);
			}

			count = countOutput;
			numPlaylists--;

			if (count < 0) {
				throw new CommandException("You can't add a negative number of songs.", CommandExceptionReason.CommandError);
			} else if (count == 0) {
				throw new CommandException("Adding no songs doesn't make any sense.", CommandExceptionReason.CommandError);
			}
		}

		// Calculate items per playlist
		int songsPerPlaylist = count / numPlaylists;
		if (count % numPlaylists != 0) {
			truncated = true;
		}

		if (songsPerPlaylist == 0) {
				throw new CommandException("You need to add least at one song per playlist.", CommandExceptionReason.CommandError);
		}

		int numSongsAdded = 0;
		var allItems = new List<QueueItem>();
		for (int i = 0; i < numPlaylists; i++) {
			var plistId = parts[i];
			var plist = playlistManager.LoadPlaylist(plistId).UnwrapThrow();
			var items = new List<PlaylistItem>(plist.Items);

			int numSongsToTake = Tools.Clamp(songsPerPlaylist, 0, plist.Items.Count);
			if (numSongsToTake != songsPerPlaylist) {
				truncated = true;
			}
			numSongsAdded += numSongsToTake;
			
			var meta = new MetaData(invoker.ClientUid, plistId);
			Shuffle(items, new Random());
			allItems.AddRange(items.Take(numSongsToTake).Select(item => new QueueItem(item.AudioResource, meta)));
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
			"." + (truncated ? TRUNCATED_MESSAGE : "");
	}

	public static void SendMessage(Ts3Client client, ClientCall cc, string message) {
		if(cc.ClientId.HasValue)
			client.SendMessage(message, cc.ClientId.Value);
	}

	[Command("youtube")]
	public static void CommandYoutube(
		PlayManager playManager,
		PlaylistManager playlistManager,
		ExecutionInformation execInfo,
		ResolveContext resolver,
		InvokerData invoker,
		ClientCall cc,
		Ts3Client ts3Client,
		string message) {
		string[] parts = Regex.Split(message, ";+");
		SendMessage(ts3Client, cc, "Received your request to add " + parts.Length + " songs, processing...");

		PartHandler urlHandler = AddUrl;
		PartHandler queryHandler = AddQuery;
		ParseMulti(urlHandler, queryHandler, playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client,
			parts, "");
	}

	private static void ParseMulti(
		PartHandler ifURL,
		PartHandler ifQuery,
		PlayManager playManager,
		PlaylistManager playlistManager,
		ExecutionInformation execInfo,
		ResolveContext resolver,
		InvokerData invoker,
		ClientCall cc,
		Ts3Client ts3Client,
		string[] parts,
		string target) {
		foreach (string part in parts) {
			// Skip if empty
			if (part == "") {
				return;
			}

			// Check if URL
			string query = part.Replace("[URL]", "").Replace("[/URL]", "").Trim(' ');
			if (Regex.Match(query, YOUTUBE_URL_REGEX).Success) {
				ifURL(playlistManager, playManager, execInfo, invoker, resolver, cc, ts3Client, query, target);
			} else {
				ifQuery(playlistManager, playManager, execInfo, invoker, resolver, cc, ts3Client, query, target);
			}
		}
	}

	private static void PrintAddMessage(Ts3Client ts3Client, ClientCall cc, PlayManager playManager) {
		PlayQueue queue = playManager.Queue;
		int realIndex = queue.Items.Count - 1;
		int index = realIndex - queue.Index;
		SendMessage(ts3Client, cc, "Added '" + queue.Items[realIndex].AudioResource.ResourceTitle + "' at queue position " + index); // This will fail if async
	}

	private static void AddUrl(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		string url,
		string target) {
		if (playManager.Enqueue(url, new MetaData(invoker.ClientUid)).UnwrapSendMessage(ts3Client, cc, url)) {
			PrintAddMessage(ts3Client, cc, playManager);
		}
	}

	private static void AddQuery(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		string query,
		string target) {
		var result = resolver.Search("youtube", query).UnwrapSendMessage(ts3Client, cc, query);
		if (result != null) {
			AudioResource audioResource = result[0];
			if (playManager.Enqueue(audioResource, new MetaData(invoker.ClientUid))
				.UnwrapSendMessage(ts3Client, cc, query)) {
				PrintAddMessage(ts3Client, cc, playManager);
			}
		}
	}

	[Command("list add")]
	public static void CommandListYoutube(
		PlayManager playManager,
		PlaylistManager playlistManager,
		ExecutionInformation execInfo,
		ResolveContext resolver,
		InvokerData invoker,
		ClientCall cc,
		Ts3Client ts3Client,
		string listId,
		string message) {
		string[] parts = Regex.Split(message, ";+");
		SendMessage(ts3Client, cc, "Received your request to add " + parts.Length + " songs to the playlist '" + listId + "', processing...");

		PartHandler urlHandler = ListAddUrl;
		PartHandler queryHandler = ListAddQuery;
		ParseMulti(urlHandler, queryHandler, playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client,
			parts, listId);
	}

	private static void ListAddUrl(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		string url,
		string target) {
		int index;
		string title;
		try {
			var playResource = resolver.Load(url).UnwrapThrow();
			title = playResource.BaseData.ResourceTitle;
			(_, index) = MainCommands.ListAddItem(playlistManager, info, target, playResource.BaseData);
		} catch (CommandException e) {
			SendMessage(ts3Client, cc, "Error occured for '" + url + "': " + e.Message);
			return;
		}

		SendMessage(ts3Client, cc,
			"Added '" + title +
			"' to playlist '" + target +
			"' at position " + index
		);
	}

	private static void ListAddQuery(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		string query,
		string target) {
		var result = resolver.Search("youtube", query).UnwrapSendMessage(ts3Client, cc, query);
		if (result != null) {
			AudioResource audioResource = result[0];
			int index;
			try {
				(_, index) = MainCommands.ListAddItem(playlistManager, info, target, audioResource);
			} catch (CommandException e) {
				SendMessage(ts3Client, cc, "Error occured for + '" + query + "': " + e.Message);
				return;
			}

			SendMessage(ts3Client, cc,
				"Added '" + audioResource.ResourceTitle +
				"' for your request '" + query +
				"' to playlist '" + target +
				"' at position " + index
			);
		}
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
				start = Int32.Parse(result.Groups[1].Value);
				end = Int32.Parse(result.Groups[2].Value);
			} else if (Int32.TryParse(part, out int index)) {
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

	private static SortedSet<int> parseAndMap(PlayQueue playQueue, string indicesString) {
		return new SortedSet<int>(ParseIndicesInBounds(indicesString, 1, playQueue.Items.Count - playQueue.Index - 1).Select(entry => entry + playQueue.Index));
	}

	[Command("del")]
	public static void CommandDelete(
		PlayManager playManager, ExecutionInformation info, InvokerData invoker, ClientCall cc,
		Ts3Client ts3Client, string idList) {
		var queue = playManager.Queue;

		// Parse id list and map to real ids. Throws CommandException when one does not exist or there is a syntax error.
		// Set because duplicates should be ignored

		List<(int, string)> succeded = new List<(int, string)>();
		List<(int, string)> failed = new List<(int, string)>();
		lock (playManager.Lock) {
			SortedSet<int> indices = parseAndMap(queue, idList);

			foreach (int index in indices.Reverse()) {
				QueueItem item = queue.Items[index];
				if (invoker.ClientUid == item.MetaData.ResourceOwnerUid || info.HasRights(RightDeleteOther)) {
					queue.Remove(index);
					succeded.Add((index, item.AudioResource.ResourceTitle));
				} else {
					failed.Add((index, item.AudioResource.ResourceTitle));
				}
			}
		}

		StringBuilder output = new StringBuilder();
		if (succeded.Count > 0) {
			output.Append("Removed the following songs:");
			foreach ((int index, string title) in succeded) {
				output.Append('\n');
				output.Append('[').Append(index).Append("] ").Append(title);
			}
		}

		if (failed.Count > 0) {
			if (succeded.Count > 0)
				output.Append('\n');
			output.Append("Failed to remove the following songs:");
			foreach ((int index, string title) in succeded) {
				output.Append('\n');
				output.Append('[').Append(index).Append("] ").Append(title);
			}
		}

		SendMessage(ts3Client, cc, output.ToString());
	}

	[Command("queue")]
	public static string CommandQueue(
		PlayManager playManager, PlaylistManager playlistManager, ExecutionInformation info, InvokerData invoker,
		TsFullClient ts3FullClient, string arg = null) {
		bool full = arg == "full";
		if (full && !info.HasRights(RightOverrideQueueCommandCheck))
			throw new CommandException("You have no permission to view the full queue.",
				CommandExceptionReason.CommandError);
		return CommandQueueInternal(playManager, playlistManager, invoker, ts3FullClient, full);
	}

	private static string CommandQueueInternal(
		PlayManager playManager, PlaylistManager playlistManager, InvokerData invoker, TsFullClient ts3FullClient,
		bool printAll) {
		var queue = playManager.Queue;

		if (queue.Items.Count == 0) {
			return "There is nothing on right now...";
		}

		string output = "";
		if (playManager.IsPlaying) {
			output +=
				"Current song: " + GetTitle(queue, queue.Index) +
				" - " + GetName(queue, queue.Index, ts3FullClient);
			if (HasPlaylistId(queue, queue.Index)) {
			   	output += " <Playlist: " + GetPlaylistId(queue, queue.Index) + ">";
			}
		}

		for (int i = queue.Index + 1; i < queue.Items.Count; i++) {
			if (printAll || queue.Items[i].MetaData.ResourceOwnerUid == invoker.ClientUid) {
				output +=
					"\n[" + (i - queue.Index) +
					"] " + GetTitle(queue, i) +
					" - " + GetName(queue, i, ts3FullClient);
				if (HasPlaylistId(queue, i)) {
					output += " <Playlist: " + GetPlaylistId(queue, i) + ">";
				}
			} else {
				output +=
					"\n[" + (i - queue.Index) +
					"] Hidden Song Name - " + GetName(queue, i, ts3FullClient);
				if (HasPlaylistId(queue, i)) {
					output += " <Playlist: Hidden>";
				}
			}
		}

		return output;
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
	public static string CommandYoutubeFront(
		PlayManager playManager,
		PlaylistManager playlistManager,
		ExecutionInformation execInfo,
		ResolveContext resolver,
		InvokerData invoker,
		ClientCall cc,
		Ts3Client ts3Client,
		string message) {
		// If the current song is the last in the queue, add normally
		var queue = playManager.Queue;

		if (queue.Index == queue.Items.Count || queue.Index == queue.Items.Count - 1) {
			CommandYoutube(playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client, message);
			return null;
		}

		// The index of front is outside of the queue, reject
		if (queue.Index + 1 >= queue.Items.Count) {
			// is that even possible?
			throw new CommandException("The index of the front would be outside of the queue!",
				CommandExceptionReason.CommandError);
		}

		AudioResource resource;

		// Check if URL
		string query = message.Replace("[URL]", "").Replace("[/URL]", "").Trim(' ');
		if (Regex.Match(query, YOUTUBE_URL_REGEX).Success) {
			resource = resolver.Load(query, "youtube").UnwrapThrow().BaseData;
		} else {
			var result = resolver.Search("youtube", query).UnwrapThrow();
			resource = result[0];
		}

		MetaData meta = new MetaData(invoker.ClientUid);
		playManager.EnqueueAsNextSong(new QueueItem(resource, meta));
		return "Added '" + resource.ResourceTitle + "' to the front of the queue.";
	}

	[Command("search list add")]
	public static string CommandSearchAdd(
		ExecutionInformation info, PlaylistManager playlistManager, UserSession session, string listId, int index) {
		AudioResource res = session.GetSearchResult(index);
		MainCommands.ListAddItem(playlistManager, info, listId, res);
		return "Ok";
	}

	public static bool Matches(string item, string query) {
		return !string.IsNullOrEmpty(item) && item.Contains(query);
	}

	private const string SessionKeyListSearchResults = "list-search-items";

	public class SearchListItemsResult {
		public string ListId { get; }
		public List<(PlaylistItem, int)> Items { get; }

		public SearchListItemsResult(string listId, List<(PlaylistItem, int)> items) {
			ListId = listId;
			Items = items;
		}
	}

	[Command("list search item")]
	public static JsonValue<SearchListItemsResult> CommandSearchItem(
		CallerInfo callerInfo, UserSession session, PlaylistManager playlistManager, string listId, string query) {
		query = query.ToLower();
		var plist = playlistManager.LoadPlaylist(listId).UnwrapThrow();
		var results = plist.Items.Select((item, idx) => (item, idx))
			.Where((item, idx) => Matches(item.item.AudioResource.ResourceTitle.ToLower(), query)).ToList();

		var searchResults = new SearchListItemsResult(listId, results);
		session.Set(SessionKeyListSearchResults, searchResults);
		return new JsonValue<SearchListItemsResult>(searchResults, res => {
			if (res.Items.Count == 0)
				return "No matching items.";
			var tmb = new TextModBuilder(callerInfo.IsColor);
			tmb.AppendFormat("Found {0} matching items:", res.Items.Count.ToString());
			foreach (var (item, idx) in res.Items) {
				tmb.AppendFormat("\n[{0}]: {1}", idx.ToString(), item.AudioResource.ResourceTitle);
			}

			return tmb.ToString();
		});
	}

	[Command("list search queue")]
	public static string CommandSearchAdd(InvokerData invoker, UserSession session, PlayManager playManager) {
		if (!session.Get<SearchListItemsResult>(SessionKeyListSearchResults, out var result)) {
			throw new CommandException("No search results found.", CommandExceptionReason.CommandError);
		}

		playManager.Enqueue(result.Items.Select(item => item.Item1.AudioResource),
			new MetaData(invoker.ClientUid, result.ListId)).UnwrapThrow();
		return $"Queued {result.Items.Count} items.";
	}

	[Command("list search add")]
	public static string CommandSearchAdd(
		ExecutionInformation info, UserSession session, PlaylistManager playlistManager, string listId) {
		if (!session.Get<List<(PlaylistItem, int)>>(SessionKeyListSearchResults, out var items)) {
			throw new CommandException("No search results found.", CommandExceptionReason.CommandError);
		}

		playlistManager.ModifyPlaylist(listId, list => {
			MainCommands.CheckPlaylistModifiable(list, info, "modify");
			list.AddRange(items.Select(item => item.Item1)).UnwrapThrow();
		}).UnwrapThrow();
		return $"Added {items.Count} items to {listId}.";
	}

	[Command("list item queue")]
	public static string CommandItemQueue(
		InvokerData invoker, PlaylistManager playlistManager, PlayManager playManager, string listId,
		string indicesString) {
		var plist = playlistManager.LoadPlaylist(listId).UnwrapThrow();
		var indices = ParseIndicesInBounds(indicesString, 0, plist.Items.Count - 1);
		var items = new List<PlaylistItem>(indices.Count);
		foreach (var index in indices) {
			items.Add(plist.Items[index]);
		}

		playManager.Enqueue(items.Select(item => item.AudioResource), new MetaData(invoker.ClientUid, listId)).UnwrapThrow();
		return $"Queued {items.Count} items from playlist {plist.Title}.";
	}

	private string AutofillStatus(string word) {
		string result = "Autofill is " + word + " ";
			
		if (autofill) {
			result += "enabled";
		} else {
			result += "disabled";
		}

		if (autofillFrom != null) {
			result += " using the playlists " + String.Join(", ", autofillFrom) + ".";
		} else if (autofill) {
			result += " using all playlists.";
		} else {
			result += ".";
		}

		return result;
	}

	private void DisableAutofill() {
		autofill = false;
		autofillFrom = null;
	}

	[Command("autofillstatus")]
	public string CommandAutofillStatus(string[] parts = null) {
		return AutofillStatus("currently");
	}
	
	[Command("autofilloff")]
	public void CommandAutofillOff(string[] parts = null) {
		// Explicitly requested to turn it off
		DisableAutofill();
		ts3Client.SendChannelMessage(AutofillStatus("now"));
	}

	[Command("autofill")]
	public void CommandAutofill(InvokerData invoker, string[] playlistIds = null) {
		// Check if all playlists exist, otherwise throw exception
		if (playlistIds != null) {
			var existingPlaylistIds = playlistManager.GetAvailablePlaylists().UnwrapThrow().Select(entry => entry.Id);
			foreach (var id in playlistIds) {
				if (!existingPlaylistIds.Contains(id)) {
					throw new CommandException("The playlist '" + id + "' does not exist.",
						CommandExceptionReason.CommandError);
				}
			}
		}

		if (autofill && autofillFrom == null) {
			// Currently enabled but without a selected set of playlists
			
			if (playlistIds != null && playlistIds.Length != 0) {
				// If a selected set of playlists is given, change to "set of playlists"
				autofillFrom = new List<string>(playlistIds);
			} else {
				// Else, disable autofill
				DisableAutofill();
			}
		} else if (autofill && autofillFrom != null) {
			// Currently enabled with a selected set of playlists
			
			if (playlistIds != null && playlistIds.Length != 0) {
				// If a selected set of playlists is given, update it
				autofillFrom = new List<string>(playlistIds);
			} else {
				// Else, switch to all
				autofillFrom = null;
			}
		} else {
			// Currently disabled, enable now (with set of playlists if given)
			autofill = true;
			if (playlistIds != null && playlistIds.Length != 0) {
				autofillFrom = new List<string>(playlistIds);
			} else {
				autofillFrom = null;
			}
		}
		
		// Only play a song if there is currently none playing
		if (autofill && !playManager.IsPlaying) {
			PlayRandom().UnwrapThrow();
		}

		ts3Client.SendChannelMessage("[" + GetClientNameFromUid(ts3FullClient, invoker.ClientUid) + "] " + AutofillStatus("now"));
	}

	public static class VotableCommands {
		public abstract class AVotableCommand {
			public static E<LocalStr> AreEmpty(string args) {
				if (string.IsNullOrEmpty(args))
					return R.Ok;
				return new LocalStr("This command can only be voted without arguments");
			}

			public static E<LocalStr> AreNotEmpty(string args) {
				if (!string.IsNullOrEmpty(args))
					return R.Ok;
				return new LocalStr("This command can't be voted without arguments");
			}

			public abstract (Func<string> action, bool removeOnResourceEnd) Create(ExecutionInformation info, string command, string args);
		}

		public static string ExecuteCommandWithArgs(ExecutionInformation info, string command, string args) {
			StringBuilder builder = new StringBuilder();
			builder.Append("!").Append(command);
			if (args != null)
				builder.Append(' ').Append(args);
			return CommandManager.ExecuteCommand(info, builder.ToString());
		}

		public static readonly Dictionary<string, AVotableCommand> Commands = new Dictionary<string, AVotableCommand>(
			new[] {
				new KeyValuePair<string, AVotableCommand>("pause", EmptyArgsCommand.Instance),
				new KeyValuePair<string, AVotableCommand>("previous", EmptyArgsCommand.ResetInstance),
				new KeyValuePair<string, AVotableCommand>("stop", EmptyArgsCommand.Instance),
				new KeyValuePair<string, AVotableCommand>("clear", EmptyArgsCommand.Instance),
				new KeyValuePair<string, AVotableCommand>("front", FrontCommand.Instance),
				new KeyValuePair<string, AVotableCommand>("skip", SkipCommand.Instance),
			});

		public class EmptyArgsCommand : AVotableCommand {
			public bool ResetOnResourceEnd { get; }

			public override (Func<string> action, bool removeOnResourceEnd) Create(ExecutionInformation info, string command, string args) {
				AreEmpty(args).UnwrapThrow();
				return (() => ExecuteCommandWithArgs(info, command, args), ResetOnResourceEnd);
			}

			private EmptyArgsCommand(bool reset) { ResetOnResourceEnd = reset; }
			public static AVotableCommand ResetInstance { get; } = new EmptyArgsCommand(true);
			public static AVotableCommand Instance { get; } = new EmptyArgsCommand(false);
		}

		public class SkipCommand : AVotableCommand {
			public override (Func<string> action, bool removeOnResourceEnd) Create(ExecutionInformation info, string command, string args) {
				if (!string.IsNullOrWhiteSpace(args) && !int.TryParse(args, out _))
					throw new CommandException("Skip expects no parameters or a number", CommandExceptionReason.CommandError);
				return (() => ExecuteCommandWithArgs(info, command, args), true);
			}
			private SkipCommand() {}
			public static AVotableCommand Instance { get; } = new SkipCommand();
		}
		public class FrontCommand : AVotableCommand {
			public override (Func<string> action, bool removeOnResourceEnd) Create(ExecutionInformation info, string command, string args) {
				AreNotEmpty(args).UnwrapThrow();
				return (() => ExecuteCommandWithArgs(info, command, args), false);
			}
			private FrontCommand() {}
			public static AVotableCommand Instance { get; } = new FrontCommand();
		}
	}

	public static string ExecuteTryCatch(ConfBot config, bool answer, Func<string> action, Action<string> errorHandler) {
		try {
			return action();
		} catch (CommandException ex) {
			NLog.LogLevel commandErrorLevel = answer ? NLog.LogLevel.Debug : NLog.LogLevel.Warn;
			Log.Log(commandErrorLevel, ex, "Command Error ({0})", ex.Message);
			if (answer) {
				errorHandler(TextMod.Format(config.Commands.Color,
					"Error: {0}".Mod().Color(Color.Red).Bold(),
					ex.Message));
			}
		} catch (Exception ex) {
			Log.Error(ex, "Unexpected command error: {0}", ex.UnrollException());
			if (answer) {
				errorHandler(TextMod.Format(config.Commands.Color,
						"An unexpected error occured: {0}".Mod().Color(Color.Red).Bold(), ex.Message));
			}
		}

		return null;
	}

	private class CurrentVoteData {
		public string Command { get; }
		public Func<string> Executor { get; }
		public int Needed { get; }
		public bool RemoveOnResourceEnd { get; }
		public HashSet<Uid> Voters { get; } = new HashSet<Uid>();
		public CurrentVoteData(string command, int clientCount, Func<string> executor, bool removeOnResourceEnd) {
			Command = command;
			Needed = Math.Max(clientCount / 2, 1);
			Executor = executor;
			RemoveOnResourceEnd = removeOnResourceEnd;
		}
	}

	private class VoteData {
		private readonly Dictionary<string, CurrentVoteData> currentVotes = new Dictionary<string, CurrentVoteData>();
		private readonly List<CurrentVoteData> removeOnResourceEnded = new List<CurrentVoteData>();

		public IReadOnlyDictionary<string, CurrentVoteData> CurrentVotes => currentVotes;

		private readonly Ts3Client client;
		private readonly ConfBot config;

		public VoteData(Ts3Client client, ConfBot config) {
			this.client = client;
			this.config = config;
		}

		public void OnSongEnd(object sender, EventArgs e) {
			foreach (var vote in removeOnResourceEnded) {
				currentVotes.Remove(vote.Command);
				client.SendChannelMessage($"Stopped vote for \"{vote.Command}\" due to end of resource.");
			}
			removeOnResourceEnded.Clear();
		}

		public void Add(CurrentVoteData vote) {
			currentVotes.Add(vote.Command, vote);
			if(vote.RemoveOnResourceEnd)
				removeOnResourceEnded.Add(vote);
		}

		public void Remove(CurrentVoteData vote) {
			currentVotes.Remove(vote.Command);
			if(vote.RemoveOnResourceEnd)
				removeOnResourceEnded.Remove(vote);
		}
		public bool CheckAndFire(CurrentVoteData vote) {
			if (vote.Needed <= vote.Voters.Count) {
				client.SendChannelMessage($"Enough votes, executing \"{vote.Command}\"...");
				
				Remove(vote);
				var res = ExecuteTryCatch(config, true, vote.Executor, err => client.SendChannelMessage(err).UnwrapToLog(Log));
				if (!string.IsNullOrEmpty(res))
					client.SendChannelMessage(res).UnwrapToLog(Log);

				return true;
			}

			return false;
		}
	}

	private static E<int> CountClientsInChannel(TsFullClient client, ChannelId channel, Func<Client, bool> predicate) {
		return client.Book.Clients.Values.Count(c => c.Channel == channel && predicate(c));
	}

	[Command("vote")]
	public static void CommandStartVote(TsFullClient ts3FullClient, Ts3Client ts3Client, BotInjector injector, ExecutionInformation info, ClientCall invoker, ConfBot config, string command, string? args = null) {
		
		var userChannel = invoker.ChannelId;
		if(!userChannel.HasValue)
			throw new CommandException("Could not get user channel", CommandExceptionReason.InternalError);
		var botChannel = ts3FullClient.Book.Clients[ts3FullClient.ClientId].Channel;

		if(botChannel != userChannel.Value)
			throw new CommandException("You have to be in the same channel as the bot to use votes", CommandExceptionReason.CommandError);

		command = command.ToLower();
		if (string.IsNullOrWhiteSpace(command))
			throw new CommandException("No command to vote for given", CommandExceptionReason.CommandError);
		
		if(!VotableCommands.Commands.TryGetValue(command, out var votableCommand)) 
			throw new CommandException($"The given command \"{command}\" can't be voted for", CommandExceptionReason.CommandError);

		if (!injector.TryGet<VoteData>(out var voteData))
			throw new CommandException("VoteData could not be found", CommandExceptionReason.InternalError);
;
		if (voteData.CurrentVotes.TryGetValue(command, out var currentVote)) {
			if(!string.IsNullOrWhiteSpace(args))
				throw new CommandException("There is already a vote going on for this command. You can't start another vote for the same command with other parameters right now.", CommandExceptionReason.CommandError);

			if (currentVote.Voters.Remove(invoker.ClientUid)) {
				int count = currentVote.Voters.Count;
				if (count == 0) {
					voteData.Remove(currentVote);
					ts3Client.SendChannelMessage($"Stopped vote for \"{command}\".");
				} else {
					ts3Client.SendChannelMessage($"Removed your vote for \"{command}\" ({currentVote.Voters.Count} votes of {currentVote.Needed})");
				}
			} else {
				currentVote.Voters.Add(invoker.ClientUid);
				voteData.CheckAndFire(currentVote);
				ts3Client.SendChannelMessage($"Added your vote for \"{command}\" ({currentVote.Voters.Count} votes of {currentVote.Needed})");
			}
		} else {
			var ci = new CallerInfo(false) {SkipRightsChecks = true, CommandComplexityMax = config.Commands.CommandComplexity};

			bool CheckClient(Client client) {
				if (ts3FullClient.ClientId == client.Id) // exclude bot
					return false;
				if (client.OutputMuted) // exclude muted
					return false;
				
				var data = ts3Client.GetClientInfoById(client.Id);
				return !data.Ok || data.Value.ClientIdleTime < MinIdleTimeForVoteIgnore; // include if data not ok or not long enough idle
			}

			int clientCount = CountClientsInChannel(ts3FullClient, botChannel, CheckClient);
			info.AddModule(ci);
			var (executor, removeOnResourceEnd) = votableCommand.Create(info, command, args);
			currentVote = new CurrentVoteData(command, clientCount, executor, removeOnResourceEnd);
			voteData.Add(currentVote);
			currentVote.Voters.Add(invoker.ClientUid);
			ts3Client.SendChannelMessage($"Started vote for \"{command}\" ({currentVote.Voters.Count} votes of {currentVote.Needed}).");
			voteData.CheckAndFire(currentVote);
		}
	}

	public void Dispose() {
		playManager.AfterResourceStarted -= ResourceStarted;
		playManager.PlaybackStopped -= PlaybackStopped;

		running = false;
		descThread.Join();
	}
}