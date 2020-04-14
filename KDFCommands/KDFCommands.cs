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
using TS3AudioBot.Plugins;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Helper;
using TS3AudioBot.Rights;
using TS3AudioBot.Sessions;
using TS3AudioBot.Web.Api;
using TSLib;
using TSLib.Full;
using TSLib.Helper;

public class KDFCommands : IBotPlugin {
	internal static ICommandBag Bag { get; } = new CommandsBag();
	
	internal class CommandsBag : ICommandBag {
		public IReadOnlyCollection<BotCommand> BagCommands { get; } = ImmutableList<BotCommand>.Empty;
		public IReadOnlyCollection<string> AdditionalRights { get; } = new []{ RightOverrideQueueCommandCheck, RightDeleteOther, RightSkipOther };
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

	private static readonly object delLock = new object();

	private delegate void PartHandler(
		PlayManager playManager,
		PlaylistManager playlistManager,
		ExecutionInformation execInfo,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		string query,
		string target
	);

	private Thread descThread;
	private bool running;
	private string title;
	private string username;

	private Player player;
	private PlayManager playManager;
	private PlaylistManager playlistManager;
	private Ts3Client ts3Client;
	private TsFullClient ts3FullClient;

	public KDFCommands(
			Player player,
			PlayManager playManager,
			PlaylistManager playlistManager,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			CommandManager commandManager) {
		this.player = player;
		this.playManager = playManager;
		this.playlistManager = playlistManager;
		this.ts3Client = ts3Client;
		this.ts3FullClient = ts3FullClient;
		this.running = false;

		commandManager.RegisterCollection(Bag);
	}
	
	public void Initialize() {
		playManager.AfterResourceStarted += Start;
		playManager.ResourceStopped += Stop;
	
		descThread = new Thread(new ThreadStart(DescriptionUpdater));
		descThread.IsBackground = true;
		running = true;
		descThread.Start();
	}

	private void Start(object sender, PlayInfoEventArgs e) {
		title = e.ResourceData.ResourceTitle;
		username = GetClientNameFromUid(ts3FullClient, e.PlayResource.Meta.ResourceOwnerUid);
	}

	private void Stop(object sender, SongEndEventArgs e) { }

	private void DescriptionUpdater() {
		while (running) {
			if (playManager.IsPlaying) {
				int queuelength = playlistManager.CurrentList.Items.Count - playlistManager.Index - 1;
				TimeSpan timeLeftTS = player.Length.Subtract(player.Position);
				string desc = "[" + username + " " + $"{timeLeftTS:mm\\:ss}" + " Q" + queuelength + "] - " + title;
				ts3Client.ChangeDescription(desc).UnwrapThrow();
				Thread.Sleep(1000);
			}
		}
	}

	private static string GetClientNameFromUid(TsFullClient ts3FullClient, Uid id) {
		return ts3FullClient.GetClientNameFromUid(id).Value.Name;
	}

	private static string GetTitleAtIndex(IReadOnlyPlaylist queue, int index) {
		return queue[index].AudioResource.ResourceTitle;
	}

	private static string GetNameAtIndex(IReadOnlyPlaylist queue, int index, TsFullClient ts3FullClient) {
		return GetClientNameFromUid(ts3FullClient, queue[index].Meta.ResourceOwnerUid);
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
		int startPos = playlistManager.CurrentList.Items.Count - playlistManager.Index;

		// Check if last element is a number.
		// If yes, remember it as number as songs to randomly queue and the last array entry when iterating playlists.
		int count = 1;
		int numPlaylists = parts.Length;
		if (Int32.TryParse(parts[parts.Length - 1], out int countOutput)) {
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
		var allItems = new List<PlaylistItem>();
		for (int i = 0; i < numPlaylists; i++) {
			var plist = playlistManager.LoadPlaylist(parts[i]).UnwrapThrow();
			var items = new List<PlaylistItem>(plist.Items);

			int numSongsToTake = Tools.Clamp(songsPerPlaylist, 0, plist.Items.Count);
			if (numSongsToTake != songsPerPlaylist) {
				truncated = true;
			}
			numSongsAdded += numSongsToTake;

			Shuffle(items, new Random());
			allItems.AddRange(items.Take(numSongsToTake));
		}

		// Shuffle again across all added songs from all playlists
		Shuffle(allItems, new Random());
		playManager.Enqueue(invoker, allItems).UnwrapThrow();
		return
			"Added a total of " + numSongsAdded +
			" songs across " + numPlaylists +
			" playlists to the queue at positions " + startPos +
			"-" + (startPos + numSongsAdded) +
			"." + (truncated ? TRUNCATED_MESSAGE : "");
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
		ts3Client.SendMessage("Received your request to add " + parts.Length + " songs, processing...", cc.ClientId.Value);

		PartHandler urlHandler = AddUrl;
		PartHandler queryHandler = AddQuery;
		ParseMulti(urlHandler, queryHandler, playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client, parts);
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
			string target = "") {

		foreach (string part in parts) {
			// Skip if empty
			if (part == "") {
				return;
			}

			// Check if URL
			string query = part.Replace("[URL]", "").Replace("[/URL]", "").Trim(' ');
			if (Regex.Match(query, YOUTUBE_URL_REGEX).Success) {
				ifURL(playManager, playlistManager, execInfo, invoker, resolver, cc, ts3Client, query, target);
			} else {
				ifQuery(playManager, playlistManager, execInfo, invoker, resolver, cc, ts3Client, query, target);
			}
		}
	}

	private static void AddUrl(
			PlayManager playManager,
		 	PlaylistManager playlistManager,
		 	ExecutionInformation execInfo,
		 	InvokerData invoker,
		 	ResolveContext resolver,
		 	ClientCall cc,
		 	Ts3Client ts3Client,
		 	string url,
		 	string target) {

		if (playManager.Enqueue(invoker, url).UnwrapSendMessage(ts3Client, cc, url)) {
			IReadOnlyPlaylist queue = playlistManager.CurrentList;
			int realIndex = queue.Items.Count - 1;
			int index = realIndex - playlistManager.Index;
			ts3Client.SendMessage("Added '" + GetTitleAtIndex(queue, realIndex) + "' at queue position " + index, cc.ClientId.Value);
		}
	}

	private static void AddQuery(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation execInfo,
			InvokerData invoker,
			ResolveContext resolver,
			ClientCall cc,
			Ts3Client ts3Client,
			string query,
			string target) {

		var result = resolver.Search("youtube", query).UnwrapSendMessage(ts3Client, cc, query);
		if (result != null) {
			AudioResource audioResource = result[0];
			if (playManager.Enqueue(invoker, audioResource).UnwrapSendMessage(ts3Client, cc, query)) {
				IReadOnlyPlaylist queue = playlistManager.CurrentList;
				int index = queue.Items.Count - playlistManager.Index - 1;
				ts3Client.SendMessage("Added '" + audioResource.ResourceTitle + "' for your request '" + query + "' at queue position " + index, cc.ClientId.Value);
			}
		}
	}

	[Command("list youtube add")]
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
		ts3Client.SendMessage("Received your request to add " + parts.Length + " songs to the playlist '" + listId + "', processing...", cc.ClientId.Value);

		PartHandler urlHandler = ListAddUrl;
		PartHandler queryHandler = ListAddQuery;
		ParseMulti(urlHandler, queryHandler, playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client, parts, listId);
	}

	private static void ListAddUrl(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation execInfo,
			InvokerData invoker,
			ResolveContext resolver,
			ClientCall cc,
			Ts3Client ts3Client,
			string url,
			string target) {

		try {
			MainCommands.CommandListAddInternal(resolver, playlistManager, execInfo, target, url);
		} catch (CommandException e) {
			ts3Client.SendMessage("Error occured for '" + url + "': " + e.Message, cc.ClientId.Value);
			return;
		}

		IReadOnlyPlaylist playlist = playlistManager.LoadPlaylist(target).Value; // No unwrap needed, playlist exists if code got here
		int index = playlist.Items.Count - 1;
		ts3Client.SendMessage(
			"Added '" + GetTitleAtIndex(playlist, index) +
			"' to playlist '" + target +
			"' at position " + index, cc.ClientId.Value
		);
	}

	private static void ListAddQuery(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation execInfo,
			InvokerData invoker,
			ResolveContext resolver,
			ClientCall cc,
			Ts3Client ts3Client,
			string query,
			string target) {

		var result = resolver.Search("youtube", query).UnwrapSendMessage(ts3Client, cc, query);
		if (result != null) {
			AudioResource audioResource = result[0];
			try {
				MainCommands.ListAddItem(playlistManager, execInfo, target, audioResource);
			} catch (CommandException e) {
				ts3Client.SendMessage("Error occured for + '" + query + "': " + e.Message, cc.ClientId.Value);
				return;
			}

			IReadOnlyPlaylist playlist = playlistManager.LoadPlaylist(target).Value; // No unwrap needed, playlist exists if code got here
			int index = playlist.Items.Count - 1;
			ts3Client.SendMessage(
				"Added '" + audioResource.ResourceTitle +
				"' for your request '" + query +
				"' to playlist '" + target +
				"' at position " + index, cc.ClientId.Value
			);
		}
	}

	private static SortedSet<int> ParseIndicesInBounds(string indicesString, int lower, int upper)
	{	  
		SortedSet<int> indices = new SortedSet<int>();
		if (upper < lower)
			return indices;
		string[] parts = Regex.Split(indicesString, ",+");
		foreach (string part in parts)
		{
			if (part.Length == 0)
			{
				continue;
			}

			var result = Regex.Match(part, "^(\\d+)-(\\d+)$");
			int start;
			int end;
			if (result.Success)
			{
				// Range, parse it
				start = Int32.Parse(result.Groups[1].Value);
				end = Int32.Parse(result.Groups[2].Value);
			}
			else if (Int32.TryParse(part, out int index)) {
				start = end = index;
			}
			else
			{
				throw new CommandException("Invalid index: " + part, CommandExceptionReason.CommandError);
			}

			if (end < start)
			{
				throw new CommandException("Given range is invalid: " + start + "-" + end, CommandExceptionReason.CommandError);
			}

			if (upper < end) {
				throw new CommandException("The given index is too big: " + end + " (max " + upper + ")", CommandExceptionReason.CommandError);
			}

			if (start < lower) {
				throw new CommandException("The given index is too small: " + start + " (min " + lower + ")", CommandExceptionReason.CommandError);
			}
			for (int i = start; i <= end; i++)
			{
				indices.Add(i);
			}
		}

		return indices;
	}

	private static SortedSet<int> parseAndMap(PlaylistManager playlistManager, string indicesString) {
		var queue = playlistManager.CurrentList;
		return new SortedSet<int>(ParseIndicesInBounds(indicesString, 1, queue.Items.Count - playlistManager.Index - 1).Select(entry => entry + playlistManager.Index));
	}

	[Command("del")]
	public static void CommandDelete(PlaylistManager playlistManager, ExecutionInformation info, InvokerData invoker, ClientCall cc, Ts3Client ts3Client, string idList) {
		lock(delLock) {
			var queue = playlistManager.CurrentList;

			// Parse id list and map to real ids. Throws CommandException when one does not exist or there is a syntax error.
			// Set because duplicates should be ignored
			SortedSet<int> indices = parseAndMap(playlistManager, idList);
			List<(int, string)> succeded = new List<(int, string)>();
			List<(int, string)> failed = new List<(int, string)>();

			playlistManager.ModifyPlaylist(".mix", mix => {
				// Sort into reverse order, otherwise the indices would shift while removing
				foreach (int index in indices.Reverse()) {
					PlaylistItem item = queue[index];
					if (invoker.ClientUid == item.Meta.ResourceOwnerUid || info.HasRights(RightDeleteOther)) {
						mix.RemoveAt(index);
						succeded.Add((index, item.AudioResource.ResourceTitle));
					} else {
						failed.Add((index, item.AudioResource.ResourceTitle));
					}
				}
			});
			
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
			
			ts3Client.SendMessage(output.ToString(), cc.ClientId.Value);
		}
	}

	[Command("queue")]
	public static string CommandQueue(PlayManager playManager, PlaylistManager playlistManager, ExecutionInformation info, InvokerData invoker, TsFullClient ts3FullClient, string arg = null) {
		bool full = arg == "full";
		if (full && !info.HasRights(RightOverrideQueueCommandCheck))
			throw new CommandException("You have no permission to view the full queue.",
				CommandExceptionReason.CommandError);
		return CommandQueueInternal(playManager, playlistManager, invoker, ts3FullClient, full);
	}
	
	private static string CommandQueueInternal(PlayManager playManager, PlaylistManager playlistManager, InvokerData invoker, TsFullClient ts3FullClient, bool printAll) {
		IReadOnlyPlaylist queue = playlistManager.CurrentList;

		if (queue.Items.Count == 0) {
			return "There is nothing on right now...";
		}

		string output = "";
		if (playManager.IsPlaying) {
			output += "Current song: " + GetTitleAtIndex(queue, playlistManager.Index) + " - " +
			          GetNameAtIndex(queue, playlistManager.Index, ts3FullClient);
		}

		for (int i = playlistManager.Index + 1; i < queue.Items.Count; i++) {
			if (printAll || queue[i].Meta.ResourceOwnerUid == invoker.ClientUid) {
				output += "\n[" + (i - playlistManager.Index) + "] " + GetTitleAtIndex(queue, i) + " - " +
				          GetNameAtIndex(queue, i, ts3FullClient);
			} else {
				output += "\n[" + (i - playlistManager.Index) + "] Hidden Song Name - " + GetNameAtIndex(queue, i, ts3FullClient);
			}
		}
	
		return output;
	}

	[Command("skip")]
	public static string CommandSkip(PlayManager playManager, PlaylistManager playlistManager, ExecutionInformation info, InvokerData invoker, ClientCall cc) {
		lock (delLock) {
			var queue = playlistManager.CurrentList;

			if (playManager.IsPlaying) {
				if (invoker.ClientUid == queue[playlistManager.Index].Meta.ResourceOwnerUid || info.HasRights(RightSkipOther)) {
					playManager.Next(invoker).UnwrapThrow();
					return "Skipped current song.";
				} else {
					return "You have no permission to skip this song.";
				}
			} else {
				return "There is not song currently playing.";
			}
		}
	}
	
	[Command("skip")]
	public static string CommandSkip(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation info,
			InvokerData invoker,
			Ts3Client ts3Client,
			ClientCall cc,
			int count) {

		var queue = playlistManager.CurrentList;
		int songsToDelete = count - 1;
		int start = playlistManager.Index + 1;
		int end = start + songsToDelete;

		if (playManager.IsPlaying) {
			// Put the number of entries to delete in bounds
			if (queue.Items.Count >= end) {
				end = queue.Items.Count - 1;
			}

			// Check rights
			if (!info.HasRights(RightSkipOther)) {
				// Check if the current song belongs to this user
				if (invoker.ClientUid != queue[playlistManager.Index].Meta.ResourceOwnerUid) {
					return "You have no permission to skip the current song.";
				}

				// Check if the songs to delete all belong to the user
				for (int i = start; start <= end; i++) {
					if (invoker.ClientUid != queue[i].Meta.ResourceOwnerUid) {
						return "You have no permission to skip the song at queue position " + (i - playlistManager.Index);
					}
				}
			}

			// Delete the songs
			CommandDelete(playlistManager, info, invoker, cc, ts3Client, start + "-" + end);

			// Skip the current song
			playManager.Next(invoker).UnwrapThrow();
			return "Skipped this and the next " + songsToDelete + " songs.";
		} else {
			return "There is not song currently playing.";
		}
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
		IReadOnlyPlaylist queue = playlistManager.CurrentList;
		if (playlistManager.Index == queue.Items.Count || playlistManager.Index == queue.Items.Count - 1) {
			CommandYoutube(playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client, message);
			return null;
		}

		// The index of front is outside of the queue, reject
		if (playlistManager.Index + 1 >= queue.Items.Count) {
			throw new CommandException("The index of the front would be outside of the queue!", CommandExceptionReason.CommandError);
		}

		AudioResource resource = null;

		// Check if URL
		string query = message.Replace("[URL]", "").Replace("[/URL]", "").Trim(' ');
		if (Regex.Match(query, YOUTUBE_URL_REGEX).Success) {
			resource = resolver.Load(query, "youtube").UnwrapThrow().BaseData;
		} else {
			var result = resolver.Search("youtube", query).UnwrapThrow();
			resource = result[0];
		}

		MetaData meta = new MetaData();
		meta.ResourceOwnerUid = invoker.ClientUid;

		playlistManager.ModifyPlaylist(".mix", mix => mix.Insert(playlistManager.Index + 1, new PlaylistItem(resource, meta))).UnwrapThrow();
		return "Added '" + resource.ResourceTitle + "' to the front of the queue.";
	}

	[Command("search list add")]
	public static string CommandSearchAdd(ExecutionInformation info, PlaylistManager playlistManager, UserSession session, string listId, int index) {
		AudioResource res = session.GetSearchResult(index);
		MainCommands.ListAddItem(playlistManager, info, listId, res);
		return "Ok";
	}

	public static bool Matches(string item, string query) {
		return !string.IsNullOrEmpty(item) && item.Contains(query);
	}

	 private const string SessionKeyListSearchResults = "list-search-items";

	[Command("list search item")]
	public static JsonArray<(PlaylistItem, int)> CommandSearchItem(CallerInfo callerInfo, UserSession session, PlaylistManager playlistManager, string listId, string query) {
		query = query.ToLower();
		var plist = playlistManager.LoadPlaylist(listId).UnwrapThrow();
		var results = plist.Items.Select((item, idx) => (item, idx))
			.Where((item, idx) => Matches(item.item.AudioResource.ResourceTitle.ToLower(), query)).ToList();

		  session.Set(SessionKeyListSearchResults, results);
		return new JsonArray<(PlaylistItem, int)>(results, res => {
			if (res.Count == 0)
				return "No matching items.";
			var tmb = new TextModBuilder(callerInfo.IsColor);
			tmb.AppendFormat("Found {0} matching items:", res.Count.ToString());
			foreach (var (item, idx) in res) {
				tmb.AppendFormat("\n[{0}]: {1}", idx.ToString(), item.AudioResource.ResourceTitle);
			}
			return tmb.ToString();
		});
	}

	[Command("list search queue")]
	public static string CommandSearchAdd(InvokerData invoker, UserSession session, PlayManager playManager) {
		if (!session.Get<List<(PlaylistItem, int)>>(SessionKeyListSearchResults, out var items)) {
			throw new CommandException("No search results found.", CommandExceptionReason.CommandError);
		}

		playManager.Enqueue(invoker, items.Select(item => item.Item1)).UnwrapThrow();
		return $"Queued {items.Count} items.";
	}

	[Command("list search add")]
	public static string CommandSearchAdd(ExecutionInformation info, UserSession session, PlaylistManager playlistManager, string listId)
	{
		if (!session.Get<List<(PlaylistItem, int)>>(SessionKeyListSearchResults, out var items))
		{
			throw new CommandException("No search results found.", CommandExceptionReason.CommandError);
		}
		
		playlistManager.ModifyPlaylist(listId, list => {
			MainCommands.CheckPlaylistModifiable(list, info, "modify");
			list.AddRange(items.Select(item => item.Item1)).UnwrapThrow();
		});
		return $"Added {items.Count} items to {listId}.";
	}

	[Command("list item queue")]
	public static string CommandItemQueue(InvokerData invoker, PlaylistManager playlistManager, PlayManager playManager, string listId, string indicesString)
	{
		var plist = playlistManager.LoadPlaylist(listId).UnwrapThrow();
		var indices = ParseIndicesInBounds(indicesString, 0, plist.Items.Count - 1);
		var items = new List<PlaylistItem>(indices.Count);
		foreach(var index in indices)
		{
			items.Add(plist.Items[index]);
		}

		playManager.Enqueue(invoker, items).UnwrapThrow();
		return $"Queued {items.Count} items from playlist {plist.Title}.";
	}

	public void Dispose() {
		playManager.AfterResourceStarted -= Start;
		playManager.ResourceStopped -= Stop;
	
		running = false;
		descThread.Join();
	}
}
