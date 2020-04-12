using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Plugins;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Helper;
using TS3AudioBot.Rights;
using TS3AudioBot.Sessions;
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
	public static void CommandListRQueue(
		PlaylistManager playlistManager, PlayManager playManager, InvokerData invoker, string listId,
		int? countOpt = null) {
		var plist = playlistManager.LoadPlaylist(listId).UnwrapThrow();
		var items = new List<PlaylistItem>(plist.Items);
	
		int count = Tools.Clamp(countOpt.HasValue ? countOpt.Value : 1, 0, items.Count);
	
		Shuffle(items, new Random());
		playManager.Enqueue(invoker, items.Take(count)).UnwrapThrow();
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
			string query = part.Trim(' ').Replace("[URL]", "").Replace("[/URL]", "");
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
	public static void CommandYoutube(
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

	private static SortedSet<int> parseAndMap(PlaylistManager playlistManager, string indicesString) {
		var queue = playlistManager.CurrentList;

		SortedSet<int> indices = new SortedSet<int>();
		string[] parts = Regex.Split(indicesString, ",+");
		foreach (string part in parts) {
			if (part == "") {
				continue;
			}

			var result = Regex.Match(part, "^(\\d+)-(\\d+)$");
			if (result.Success) {
				// Range, parse it
				int start = Int32.Parse(result.Groups[1].Value);
				int end = Int32.Parse(result.Groups[2].Value);

				for (int i = start; i <= end; i++) {
					if (i == 0) {
						throw new CommandException("You can't delete the currently running song (index 0).", CommandExceptionReason.CommandError);
					}

					int realIndex = playlistManager.Index + i;
					if (realIndex >= queue.Items.Count) {
						throw new CommandException("There is no song in the queue with the index " + i, CommandExceptionReason.CommandError);
					}
					indices.Add(realIndex);
				}
			} else {
				if (Int32.TryParse(part, out int index)) {
					if (index < 0) {
						throw new CommandException("Given index is negative: " + part, CommandExceptionReason.CommandError);
					}

					if (index == 0) {
						throw new CommandException("You can't delete the currently running song (index 0).", CommandExceptionReason.CommandError);
					}

					int realIndex = playlistManager.Index + index;
					if (realIndex >= queue.Items.Count) {
						throw new CommandException("There is no song in the queue with the index " + index, CommandExceptionReason.CommandError);
					}
					indices.Add(realIndex);
				} else {
					throw new CommandException("Invalid index: " + part, CommandExceptionReason.CommandError);
				}
			}
		}

		return indices;
	}

	[Command("del", "lol das is ne hilfe")]
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
	public static void CommandSkip(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation info,
			InvokerData invoker,
			Ts3Client ts3Client,
			ClientCall cc,
			int count) {
		lock (delLock) {
			var queue = playlistManager.CurrentList;
			for (int i = 0; i < count; i++) {
				if (playManager.IsPlaying) {
					if (invoker.ClientUid == queue[playlistManager.Index].Meta.ResourceOwnerUid || info.HasRights(RightSkipOther)) {
						playManager.Next(invoker).UnwrapThrow();
						ts3Client.SendMessage("Skipped the current song.", cc.ClientId.Value);
					} else {
						throw new CommandException("You have no permission to skip this song.", CommandExceptionReason.CommandError);
					}
				} else {
					throw new CommandException("There is not song currently playing.", CommandExceptionReason.CommandError);
				}
			}
			Thread.Sleep(100);
		}
	}

	[Command("search list add")]
	public static string CommandSearchAdd(ExecutionInformation info, PlaylistManager playlistManager, UserSession session, string listId, int index) {
		AudioResource res = session.GetSearchResult(index);
		MainCommands.ListAddItem(playlistManager, info, listId, res);
		return "Ok";
	}

	public void Dispose() {
		playManager.AfterResourceStarted -= Start;
		playManager.ResourceStopped -= Stop;
	
		running = false;
		descThread.Join();
	}
}
