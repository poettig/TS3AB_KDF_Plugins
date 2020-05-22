using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
using TS3AudioBot.Web.Model;
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

	private delegate string PartHandler(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		TsFullClient tsFullClient,
		string query,
		string target = null,
		bool silent = false,
		string uidStr = null
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

	private class AutoFillData {
		public Random Random { get; } = new Random();
		public HashSet<string> Playlists { get; set; } = null;
		public QueueItem Next { get; set; }
	}

	private AutoFillData autofillData;

	private bool AutofillEnabled => autofillData != null;

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

		if (injector.TryGet(out voteData)) {
			return;
		}

		voteData = new VoteData(ts3Client, config);
		injector.AddModule(voteData);
	}

	public void Initialize() {
		playManager.AfterResourceStarted += ResourceStarted;
		playManager.PlaybackStopped += PlaybackStopped;
		playManager.ResourceStopped += OnResourceStopped;

		descThread = new Thread(DescriptionUpdater) {
			IsBackground = true
		};
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

	private bool playbackStoppedReentrantGuard = false;

	private void PlaybackStopped(object sender, EventArgs e) {
		if (playbackStoppedReentrantGuard)
			return;
		playbackStoppedReentrantGuard = true;

		// Enqueue fires playback stopped event if the song failed to play
		UpdateAutofillOnPlaybackStopped();

		playbackStoppedReentrantGuard = false;
	}

	private void UpdateAutofillOnPlaybackStopped() {
		if (playManager.Queue.Items.Count != playManager.Queue.Index || !AutofillEnabled) {
			return;
		}

		for (var i = 0; i < 10; i++) {
			if (ts3Client.IsDefinitelyAlone()) {
				DisableAutofill();
				return;
			}

			var result = PlayRandom();
			if (result.Ok) {
				return;
			}
		}

		ts3Client.SendChannelMessage("Could not play a new autofill song after 10 tries. Disabling autofill.");
		DisableAutofill();
	}

	private static R<(PlaylistInfo list, int offset)> FindSong(int index, IEnumerable<PlaylistInfo> playlists) {
		foreach(var list in playlists) {
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
		var playlistsUnfiltered = playlistManager.GetAvailablePlaylists().UnwrapThrow();
		List<PlaylistInfo> playlists = new List<PlaylistInfo>();
 		foreach (var playlist in playlistsUnfiltered) {
			if (autofillData.Playlists != null && !autofillData.Playlists.Contains(playlist.Id)) {
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
			var songIndex = autofillData.Random.Next(0, numSongs);

			// Find the randomized song
			var infoOpt = FindSong(songIndex, playlists);
			if (!infoOpt.Ok)
				throw new CommandException("Autofill: Could not find the song at index " + songIndex + ".", CommandExceptionReason.InternalError);

			var (list, index) = infoOpt.Value;
			var playlist = playlistManager.LoadPlaylist(list.Id).UnwrapThrow();

			if(playlist.Items.Count != list.SongCount)
				Log.Warn("Playlist '{0}' is possibly corrupted!", list.Id);

			sIdx = index;
			plId = list.Id;
			resource = playlist.Items[index].AudioResource;
			Log.Info("Found song index {0}: Song '{1}' in playlist '{2}' at index {3}.", songIndex, resource.ResourceTitle, list.Id, index);

			// Check if the song was already played in the last 250 songs, if not take this one.
			// If items.count < 250, the subtraction is negative, meaning that j == 0 will be reached first
			var foundDuplicate = false;
			var items = playManager.Queue.Items;
			if (items.Count > 0) {
				for (var j = items.Count - 1; j != 0 && j >= items.Count - 250; j--) {
					if (!items[j].AudioResource.Equals(resource)) {
						continue;
					}

					Log.Info("The song {0} was already played {1} songs ago. Searching another one...", items[j].AudioResource.ResourceTitle,items.Count - j - 1);
					foundDuplicate = true;
					break;
				}
			}

			if (!foundDuplicate) {
				break;
			}
		}

		if (resource == null) { // Should not happen
			throw new CommandException("Autofill: Missing resource for song at index " + sIdx + " in playlist " + plId + ".", CommandExceptionReason.InternalError);
		}

		return new QueueItem(resource, new MetaData(ts3FullClient.Identity.ClientUid, plId));
	}

	private void DrawNextSong() {
		autofillData.Next = DrawRandom();
		playManager.PrepareNextSong(autofillData.Next);
	}

	private E<LocalStr> PlayRandom() {
		if(autofillData.Next == null)
			throw new InvalidOperationException();

		var item = autofillData.Next;
		Log.Info("Autofilling the song '{0}' from playlist '{1}'.", item.AudioResource.ResourceTitle, item.MetaData.ContainingPlaylistId);
		var res = playManager.Enqueue(item);
		DrawNextSong();
		return res;
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
		if (!id.HasValue) {
			return null;
		}

		Uid uid;
		if (id.Value.ToString() == "Anonymous") {
			uid = ts3FullClient.Identity.ClientUid;
		} else {
			uid = id.Value;
		}

		var result = ts3FullClient.GetClientNameFromUid(uid);
		if (!result.Ok) {
			throw new CommandException($"The UID '{id}' does not exist on this server.", CommandExceptionReason.CommandError);
		}
		return result.Value.Name;

	}

	private static string GetUserNameOrBotName(string userId, TsFullClient ts3FullClient) {
		if (userId == null) {
			return GetClientNameFromUid(ts3FullClient, ts3FullClient.Identity.ClientUid);
		}

		return GetClientNameFromUid(ts3FullClient, new Uid(userId));
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
		if (int.TryParse(parts[^1], out var countOutput)) {
			// The only element is a number --> playlist name missing
			if (parts.Length == 1) {
				throw new CommandException("No playlist to add from given.", CommandExceptionReason.CommandError);
			}

			count = countOutput;
			numPlaylists--;

			if (count < 0) {
				throw new CommandException("You can't add a negative number of songs.", CommandExceptionReason.CommandError);
			}

			if (count == 0) {
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

		var numSongsAdded = 0;
		var allItems = new List<QueueItem>();
		for (var i = 0; i < numPlaylists; i++) {
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

	private static void SendMessage(Ts3Client client, ClientCall cc, string message) {
		if(cc.ClientId.HasValue)
			client.SendMessage(message, cc.ClientId.Value);
	}
	
	private static void checkOnlineThrow(TsFullClient ts3FullClient, string uidStr) {
		if (!CommandCheckUserOnlineByUid(ts3FullClient, uidStr)) {
			throw new CommandException(
				$"The user with UID '{uidStr}' is not online and therefore not allowed to use this command.",
				CommandExceptionReason.Unauthorized
			);
		}
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
		TsFullClient ts3FullClient,
		string message) {
		string[] parts = Regex.Split(message, ";+");
		
		SendMessage(ts3Client, cc, "Received your request to add " + parts.Length + " songs, processing...");

		PartHandler urlHandler = AddUrl;
		PartHandler queryHandler = AddQuery;
		ParseMulti(urlHandler, queryHandler, playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client, ts3FullClient, parts, "");
	}
	
	[Command("youtubebyuid")]
	public static string CommandYoutubeByUid(
		PlayManager playManager,
		PlaylistManager playlistManager,
		ExecutionInformation execInfo,
		ResolveContext resolver,
		InvokerData invoker,
		Ts3Client ts3Client,
		TsFullClient ts3FullClient,
		string uidStr,
		string message) {

		string query = message.Replace("[URL]", "").Replace("[/URL]", "").Trim(' ');
		if (Regex.Match(query, YOUTUBE_URL_REGEX).Success) {
			return AddUrl(playlistManager, playManager, execInfo, invoker, resolver, null, ts3Client, ts3FullClient, query, silent: true, uidStr: uidStr);
		} else {
			return AddQuery(playlistManager, playManager, execInfo, invoker, resolver, null, ts3Client, ts3FullClient, query, silent: true, uidStr: uidStr);
		}
	}

	private static void ParseMulti(
		PartHandler ifUrl,
		PartHandler ifQuery,
		PlayManager playManager,
		PlaylistManager playlistManager,
		ExecutionInformation execInfo,
		ResolveContext resolver,
		InvokerData invoker,
		ClientCall cc,
		Ts3Client ts3Client,
		TsFullClient ts3FullClient,
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
				ifUrl(playlistManager, playManager, execInfo, invoker, resolver, cc, ts3Client, ts3FullClient, query, target);
			} else {
				ifQuery(playlistManager, playManager, execInfo, invoker, resolver, cc, ts3Client, ts3FullClient, query, target);
			}
		}
	}

	private static string ComposeAddMessage(PlayManager playManager) {
		PlayQueue queue = playManager.Queue;
		int realIndex = queue.Items.Count - 1;
		int index = realIndex - queue.Index;
		return "Added '" + queue.Items[realIndex].AudioResource.ResourceTitle + "' at queue position " + index;
	}

	private static void PrintAddMessage(Ts3Client ts3Client, ClientCall cc, PlayManager playManager) {
		SendMessage(ts3Client, cc, ComposeAddMessage(playManager)); // This will fail if async
	}

	private static Uid GetRelevantUid(TsFullClient ts3FullClient, InvokerData invoker, string uidStr) {
		if (uidStr != null) {
			checkOnlineThrow(ts3FullClient, uidStr);
			return Uid.To(uidStr);
		} else {
			return invoker.ClientUid;
		}
	}

	private static string AddUrl(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		TsFullClient ts3FullClient,
		string url,
		string target = null,
		bool silent = false,
		string uidStr = null) {

		Uid uid = GetRelevantUid(ts3FullClient, invoker, uidStr);
		
		if (silent) {
			playManager.Enqueue(url, new MetaData(uid)).UnwrapThrow();
			return ComposeAddMessage(playManager);
        }

		if (cc == null) {
			throw new CommandException(
				"Tried to call AddURL with invalid parameter combination 'cc == null' and 'silent == false'.",
				CommandExceptionReason.CommandError
			);
		}

		if (playManager.Enqueue(url, new MetaData(uid)).UnwrapSendMessage(ts3Client, cc, url)) {
			PrintAddMessage(ts3Client, cc, playManager);
		}

		// Only reached of not silent.
		return null;
	}

	private static string AddQuery(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		TsFullClient ts3FullClient,
		string query,
		string target = null,
		bool silent = false,
		string uidStr = null) {

		Uid uid = GetRelevantUid(ts3FullClient, invoker, uidStr);

		IList<AudioResource> result = null;
		if (silent) {
			result = resolver.Search("youtube", query).UnwrapThrow();
		} else {
			if (cc == null) {
				throw new CommandException(
					"Tried to call AddQuery with invalid parameter combination 'cc == null' and 'silent == false'.",
					CommandExceptionReason.CommandError
				);
			}
			
			result = resolver.Search("youtube", query).UnwrapSendMessage(ts3Client, cc, query);
		}
		
		// Will not be reached if silent is marked and the search failed because of UnwrapThrow()
		if (result == null) {
			return null;
		}

		AudioResource audioResource = result[0];
		if (silent) {
			playManager.Enqueue(audioResource, new MetaData(uid)).UnwrapThrow();
			return ComposeAddMessage(playManager);
		}
		
		if (playManager.Enqueue(audioResource, new MetaData(uid)).UnwrapSendMessage(ts3Client, cc, query)) {
			PrintAddMessage(ts3Client, cc, playManager);
		}

		// Only reached if not silent
		return null;
	}
	
	public class LoggingHandler : DelegatingHandler
	{
		public LoggingHandler(HttpMessageHandler innerHandler)
			: base(innerHandler)
		{
		}

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Console.WriteLine("Request:");
			Console.WriteLine(request.ToString());
			if (request.Content != null)
			{
				Console.WriteLine(await request.Content.ReadAsStringAsync());
			}
			Console.WriteLine();

			HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

			Console.WriteLine("Response:");
			Console.WriteLine(response.ToString());
			if (response.Content != null)
			{
				Console.WriteLine(await response.Content.ReadAsStringAsync());
			}
			Console.WriteLine();

			return response;
		}
	}
	
	[Command("spotify")]
	public static void CommandSpotify(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		TsFullClient ts3FullClient,
		string username,
		string playlistName,
		string oauth) {
		
		var client = new HttpClient();
		client.DefaultRequestHeaders.Add("Authorization", "Bearer " + oauth);

		var resp = client.GetAsync("https://api.spotify.com/v1/users/" + username + "/playlists").Result;
		if (!resp.IsSuccessStatusCode) {
			throw new CommandException(
				"Getting the users playlists failed (" + resp.StatusCode +
				"). Maybe the username is wrong or the oauth token is invalid.", CommandExceptionReason.CommandError);
		}

		var pNameSpotify = playlistName.Replace("\"", "");
		var pNameInternal = pNameSpotify.Replace(" ", "");

		// Get playlist id
		JObject data = JObject.Parse(resp.Content.ReadAsStringAsync().Result);
		string playlistId = null;
		foreach (var playlist in data["items"]) {
			if (playlist["name"].ToString() == pNameSpotify) {
				playlistId = playlist["id"].ToString();
			}
		}

		if (playlistId == null) {
			throw new CommandException("No playlist with the name '" + pNameSpotify + "' found for spotify user '" + username +"'", CommandExceptionReason.CommandError);
		}
		
		// Create the playlist. Throws Exception if it does already exist.
		MainCommands.CommandListCreate(playlistManager, invoker, pNameInternal);
		
		// Get all playlist items and add to the playlist
		var nextUrl = "Init";
		while (nextUrl != null) {
			resp = nextUrl == "Init" ? client.GetAsync("https://api.spotify.com/v1/playlists/" + playlistId + "/tracks").Result : client.GetAsync(nextUrl).Result;

			if (resp == null || !resp.IsSuccessStatusCode) {
				throw new CommandException("Could not get the items from the playlist (" + resp?.StatusCode + ").", CommandExceptionReason.CommandError);
			}
			
			data = JObject.Parse(resp.Content.ReadAsStringAsync().Result);
			foreach (var item in data["items"]) {
				var title = item["track"]["name"];
				var artists = item["track"]["artists"].Select(artist => artist["name"].ToString()).ToList();
				ListAddQuery(
					playlistManager,
					playManager,
					info,
					invoker,
					resolver,
					cc,
					ts3Client,
					ts3FullClient,
					title + " " + string.Join(" ", artists),
					pNameInternal
				);
			}

			nextUrl = data.ContainsKey("next") && !string.IsNullOrEmpty(data["next"].ToString()) ? data["next"].ToString() : null;
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
		TsFullClient ts3FullClient,
		string listId,
		string message) {
		string[] parts = Regex.Split(message, ";+");
		SendMessage(ts3Client, cc, "Received your request to add " + parts.Length + " songs to the playlist '" + listId + "', processing...");

		PartHandler urlHandler = ListAddUrl;
		PartHandler queryHandler = ListAddQuery;
		ParseMulti(urlHandler, queryHandler, playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client, ts3FullClient, parts, listId);
	}

	private static string ListAddUrl(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		TsFullClient ts3FullClient,
		string url,
		string target = null,
		bool silent = false,
		string uidStr = null) {
		
		int index;
		string title;
		try {
			var playResource = resolver.Load(url).UnwrapThrow();
			title = playResource.BaseData.ResourceTitle;
			(_, index) = MainCommands.ListAddItem(playlistManager, info, target, playResource.BaseData);
		} catch (CommandException e) {
			SendMessage(ts3Client, cc, "Error occured for '" + url + "': " + e.Message);
			return null;
		}

		SendMessage(ts3Client, cc,
			"Added '" + title +
			"' to playlist '" + target +
			"' at position " + index
		);

		return null;
	}

	private static string ListAddQuery(
		PlaylistManager playlistManager,
		PlayManager playManager,
		ExecutionInformation info,
		InvokerData invoker,
		ResolveContext resolver,
		ClientCall cc,
		Ts3Client ts3Client,
		TsFullClient ts3FullClient,
		string query,
		string target = null,
		bool silent = false,
		string uidStr = null) {
		
		var result = resolver.Search("youtube", query).UnwrapSendMessage(ts3Client, cc, query);
		
		if (result == null) {
			return null;
		}

		AudioResource audioResource = result[0];
		int index;
		try {
			(_, index) = MainCommands.ListAddItem(playlistManager, info, target, audioResource);
		} catch (CommandException e) {
			SendMessage(ts3Client, cc, "Error occured for '" + query + "': " + e.Message);
			return null;
		}

		SendMessage(ts3Client, cc,
			"Added '" + audioResource.ResourceTitle +
			"' for your request '" + query +
			"' to playlist '" + target +
			"' at position " + index
		);

		return null;
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
	public static void CommandDelete(
		PlayManager playManager, ExecutionInformation info, InvokerData invoker, ClientCall cc,
		Ts3Client ts3Client, string idList) {
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
				if (invoker.ClientUid == item.MetaData.ResourceOwnerUid || info.HasRights(RightDeleteOther)) {
					queue.Remove(index);
					succeeded.Add((index - currentSongIndex, item.AudioResource.ResourceTitle));
				} else {
					failed.Add((index - currentSongIndex, item.AudioResource.ResourceTitle));
				}
			}
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

		SendMessage(ts3Client, cc, output.ToString());
	}

	public class QueueItemInfo {
		[JsonProperty(PropertyName = "Title")]
		public string Title { get; set; }
		[JsonProperty(PropertyName = "UserId")]
		public string UserId { get; set; }
		[JsonProperty(PropertyName = "ContainingListId")]
		public string ContainingListId { get; set; }
	}

	public class CurrentQueueInfo {
		[JsonProperty(PropertyName = "Current")]
		public QueueItemInfo Current { get; set; }
		[JsonProperty(PropertyName = "Items")]
		public List<QueueItemInfo> Items { get; set; }
	}

	private void AppendSong(StringBuilder target, QueueItemInfo qi, bool restrict) {
		target.Append(restrict ? "Hidden Song Name" : qi.Title);
		target.Append(" - ").Append(GetUserNameOrBotName(qi.UserId, ts3FullClient));

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

	private static QueueItemInfo ToQueueItemInfo(QueueItem qi, bool restrict) {
		return new QueueItemInfo {
			ContainingListId = qi.MetaData.ContainingPlaylistId,
			Title = restrict ? null : qi.AudioResource.ResourceTitle,
			UserId = qi.MetaData.ResourceOwnerUid.GetValueOrDefault(Uid.Null).Value
		};
	}

	[Command("queuewithuid")]
	public JsonValue<CurrentQueueInfo> CommandQueueWithUid(
		ExecutionInformation info,
		InvokerData invoker,
		string uidStr) { 
		
		// Check if user exists, throws exception if not.
		var uid = Uid.To(uidStr);
		GetClientNameFromUid(ts3FullClient, uid);
		return CommandQueueInternal(info, uid, uidStr);
	}

	[Command("queue")]
	public JsonValue<CurrentQueueInfo> CommandQueue(
		ExecutionInformation info,
		InvokerData invoker, 
		string arg = null) {
		return CommandQueueInternal(info, invoker.ClientUid, arg);
	}

	private JsonValue<CurrentQueueInfo> CommandQueueInternal(ExecutionInformation info, Uid uid, string arg = null) {
		bool restricted = arg != "full";
		if (!restricted && !info.HasRights(RightOverrideQueueCommandCheck))
			throw new CommandException("You have no permission to view the full queue.", CommandExceptionReason.CommandError);
		var queueInfo = new CurrentQueueInfo();
		lock (playManager) {
			bool ShouldRestrict(QueueItem qi) => restricted && qi.MetaData.ResourceOwnerUid != uid;
			queueInfo.Items = playManager.Queue.Items.Skip(playManager.Queue.Index + 1)
				.Select(qi => ToQueueItemInfo(qi, ShouldRestrict(qi))).ToList();
			if (playManager.IsPlaying)
				queueInfo.Current = ToQueueItemInfo(playManager.Queue.Current, false);
		}
		return new JsonValue<CurrentQueueInfo>(queueInfo, QueueInfoToString);
	}

	[Command("recentlyplayed")]
	public JsonArray<QueueItemInfo> CommandRecentlyPlayed(PlayManager playManager, ExecutionInformation info, InvokerData invoker, int? count) {
		List<QueueItemInfo> items;
		lock (playManager) {
			int start = Math.Max(0, playManager.Queue.Index - count ?? 5);
			var take = playManager.Queue.Index - start;
			items = playManager.Queue.Items.Skip(start).Take(take).Select(qi => ToQueueItemInfo(qi, false)).ToList();
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
	public static string CommandYoutubeFront(
		PlayManager playManager,
		PlaylistManager playlistManager,
		ExecutionInformation execInfo,
		ResolveContext resolver,
		InvokerData invoker,
		ClientCall cc,
		Ts3Client ts3Client,
		TsFullClient ts3FullClient,
		string message) {
		// If the current song is the last in the queue, add normally
		var queue = playManager.Queue;

		if (queue.Index == queue.Items.Count || queue.Index == queue.Items.Count - 1) {
			CommandYoutube(playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client, ts3FullClient, message);
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
		var query = message.Replace("[URL]", "").Replace("[/URL]", "").Trim(' ');
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

	private static bool Matches(string item, string query) {
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
		string indicesString, string uid = null) {
		var plist = playlistManager.LoadPlaylist(listId).UnwrapThrow();
		var indices = ParseIndicesInBounds(indicesString, 0, plist.Items.Count - 1);
		var items = new List<PlaylistItem>(indices.Count);
		items.AddRange(indices.Select(index => plist.Items[index]));

		playManager.Enqueue(items.Select(item => item.AudioResource), new MetaData(uid != null ? Uid.To(uid) : invoker.ClientUid, listId)).UnwrapThrow();

		if (indices.Count == 1) {
			return $"Queued '{items[0].AudioResource.ResourceTitle}' from playlist {plist.Title}.";
		}
		return $"Queued {items.Count} items from playlist {plist.Title}.";
	}

	[Command("checkuser online byuid")]
	public static bool CommandCheckUserOnlineByUid(TsFullClient ts3FullClient, string uid) {
		var result = ts3FullClient.ClientList(ClientListOptions.uid);
		if (!result) {
			return false;
		}

		return result.Value.Count(item => item.Uid.ToString() == uid) != 0;
	}

	private string AutofillStatus(string word) {
		string result = "Autofill is " + word + " ";
			
		if (AutofillEnabled) {
			result += "enabled";

			if(autofillData.Playlists != null) {
				result += " using the playlists " + string.Join(", ", autofillData.Playlists) + ".";
			} else {
				result += " using all playlists.";
			}
		} else {
			result += "disabled";
			result += ".";
		}

		return result;
	}

	private void DisableAutofill() {
		autofillData = null;
	}

	[Command("autofillstatus")]
	public string CommandAutofillStatus() {
		return AutofillStatus("currently");
	}

	[Command("autofilloff")]
	private void CommandAutofillOff(InvokerData invoker) {
		CommandAutofillOffInternal(invoker.ClientUid);
	}

	[Command("autofilloffbyuid")]
	private void CommandAutofillOffByUid(string uid) {
		CommandAutofillOffInternal(Uid.To(uid));
	}

	private void CommandAutofillOffInternal(Uid uid) {
		// Explicitly requested to turn it off
		DisableAutofill();
		ts3Client.SendChannelMessage("[" + GetClientNameFromUid(ts3FullClient, uid) + "] " + AutofillStatus("now"));
	}

	[Command("autofill")]
	public void CommandAutofill(InvokerData invoker, string[] playlistIds = null) {
		CommandAutofillInternal(invoker.ClientUid, playlistIds);
	}

	[Command("autofillbyuid")]
	public void CommandAutofillByUid(InvokerData invoker, string uidStr, string[] playlistIds = null) {
		checkOnlineThrow(ts3FullClient, uidStr);
		CommandAutofillInternal(Uid.To(uidStr), playlistIds);
	}
	
	private void CommandAutofillInternal(Uid uid, string[] playlistIds = null) {
		// Check if all playlists exist, otherwise throw exception
		if (playlistIds != null) {
			for (int i = 0; i < playlistIds.Length; ++i) {
				if (playlistManager.TryGetPlaylistId(playlistIds[i], out var realId)) {
					playlistIds[i] = realId; // Apply possible correction
				} else {
					throw new CommandException("The playlist '" + playlistIds[i] + "' does not exist.",
						CommandExceptionReason.CommandError);
				}
			}
		}

		if (AutofillEnabled) {
			if (autofillData.Playlists == null) {
				// Currently enabled but without a selected set of playlists
			
				if (playlistIds != null && playlistIds.Length != 0) {
					// If a selected set of playlists is given, change to "set of playlists"
					autofillData.Playlists = new HashSet<string>(playlistIds);
					DrawNextSong();
				} else {
					// Else, disable autofill
					DisableAutofill();
				}
			} else {
				// Currently enabled with a selected set of playlists
			
				if (playlistIds != null && playlistIds.Length != 0) {
					// If a selected set of playlists is given, update it
					autofillData.Playlists = new HashSet<string>(playlistIds);
				} else {
					// Else, switch to all
					autofillData.Playlists = null;
				}
				DrawNextSong();
			}
		} else {
			// Currently disabled, enable now (with set of playlists if given)
			autofillData = new AutoFillData();
			if (playlistIds != null && playlistIds.Length != 0) {
				autofillData.Playlists = new HashSet<string>(playlistIds);
			} else {
				autofillData.Playlists = null;
			}
			autofillData.Next = DrawRandom();
		}

		// Only play a song if there is currently none playing
		if (AutofillEnabled && !playManager.IsPlaying) {
			bool found = false;
			for (var i = 0; i < 10; i++) {
				var result = PlayRandom();
				if (result.Ok) {
					found = true;
					break;
				}
			}

			if (!found) {
				// Could not successfully play a song after 10 tries
				ts3Client.SendChannelMessage("Could not play a new autofill song after 10 tries. Disabling autofill.");
				DisableAutofill();
				return;
			}
		}

		ts3Client.SendChannelMessage("[" + GetClientNameFromUid(ts3FullClient, uid) + "] " + AutofillStatus("now"));
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
				new KeyValuePair<string, AVotableCommand>("skip", SkipCommand.Instance)
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
			if (vote.Needed > vote.Voters.Count) {
				return false;
			}

			client.SendChannelMessage($"Enough votes, executing \"{vote.Command}\"...");
				
			Remove(vote);
			var res = ExecuteTryCatch(config, true, vote.Executor, err => client.SendChannelMessage(err).UnwrapToLog(Log));
			if (!string.IsNullOrEmpty(res))
				client.SendChannelMessage(res).UnwrapToLog(Log);

			return true;

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