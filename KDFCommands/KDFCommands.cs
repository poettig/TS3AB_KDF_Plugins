using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

	private Thread descThread;
	private bool running;
	private string title;
	private string username;
	private int lastStartedIndex;
	
	private Player player;
	private PlayManager playManager;
	private PlaylistManager playlistManager;
	private Ts3Client ts3Client;
	private TsFullClient ts3FullClient;
	
	// Your dependencies will be injected into the constructor of your class.
	public KDFCommands(
		Player player, PlayManager playManager, PlaylistManager playlistManager, Ts3Client ts3Client,
		TsFullClient ts3FullClient, CommandManager commandManager) {
		this.player = player;
		this.playManager = playManager;
		this.playlistManager = playlistManager;
		this.ts3Client = ts3Client;
		this.ts3FullClient = ts3FullClient;
		commandManager.RegisterCollection(Bag);
		
		this.running = false;
	}
	
	// The Initialize method will be called when all modules were successfully injected.
	public void Initialize() {
		playManager.AfterResourceStarted += Start;
		playManager.ResourceStopped += Stop;
	
		descThread = new Thread(new ThreadStart(DescriptionUpdater));
		descThread.IsBackground = true;
		running = true;
		descThread.Start();
	}
	
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

	private string GetClientNameFromUid(Uid id) {
		return ts3FullClient.GetClientNameFromUid(id).Value.Name;
	}

	private string GetTitleAtIndex(IReadOnlyPlaylist queue, int index) {
		return queue[index].AudioResource.ResourceTitle;
	}

	private string GetNameAtIndex(IReadOnlyPlaylist queue, int index) {
		return GetClientNameFromUid(queue[index].Meta.ResourceOwnerUid);
	}

	private void Start(object sender, PlayInfoEventArgs e) {
		title = e.ResourceData.ResourceTitle;
		username = GetClientNameFromUid(e.PlayResource.Meta.ResourceOwnerUid);
	}
	
	private void Stop(object sender, SongEndEventArgs e) { }
	
	public static void Shuffle<T>(IList<T> list, Random rng) {
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
	
		int count = countOpt.HasValue ? Tools.Clamp(countOpt.Value, 0, items.Count) : items.Count;
	
		Shuffle(items, new Random());
		playManager.Enqueue(invoker, items.Take(count)).UnwrapThrow();
	}
	
	// You should prefer static methods which get the modules injected via parameter unless
	// you actually need objects from your plugin in your method.
	[Command("youtube")]
	public void CommandYoutube(ResolveContext resolver, InvokerData invoker, ClientCall cc, string message) {
		string[] parts = Regex.Split(message, ";+");
		ts3Client.SendMessage("Received your request to add " + parts.Length + " songs, processing...",
			cc.ClientId.Value);
	
		foreach (string part in parts) {
			// Check if URL
			string query = part.Replace("[URL]", "").Replace("[/URL]", "");
			var match = Regex.Match(query, YOUTUBE_URL_REGEX);
			if (match.Success) {
				playManager.Enqueue(invoker, query);
				ts3Client.SendMessage("Added " + query, cc.ClientId.Value);
			} else {
				var result = resolver.Search("youtube", query).UnwrapThrow();
				var audioResource = result[0];
				playManager.Enqueue(invoker, audioResource);
				ts3Client.SendMessage(
					"Added '" + audioResource.ResourceTitle + "' for your request '" +
					query.Replace("[URL]", "").Replace("[/URL]", "") + "'", cc.ClientId.Value);
			}
		}
	}

	[Command("del")]
	public void CommandDelete(ExecutionInformation info, InvokerData invoker, ClientCall cc, int id) {
		var queue = playlistManager.CurrentList;
		if (id >= queue.Items.Count) {
			throw new CommandException("There is no song with that index in the queue.",
				CommandExceptionReason.CommandError);
		}
	
		PlaylistItem item = queue[id];
		if (invoker.ClientUid == item.Meta.ResourceOwnerUid || info.HasRights(RightDeleteOther)) {
			playlistManager.ModifyPlaylist(".mix", mix => mix.RemoveAt(id));
			ts3Client.SendMessage(
				"Removed " + item.AudioResource.ResourceTitle + " (position " + id + ") from the queue.",
				cc.ClientId.Value);
		} else {
			throw new CommandException("You have no permission to delete this song from the queue.",
				CommandExceptionReason.CommandError);
		}
	}

	[Command("queue")]
	public string CommandQueue(ExecutionInformation info, InvokerData invoker, string arg = null) {
		bool full = arg == "full";
		if (full && !info.HasRights(RightOverrideQueueCommandCheck))
			throw new CommandException("You have no permission to view the full queue.",
				CommandExceptionReason.CommandError);
		return CommandQueueInternal(invoker, full);
	}
	
	private string CommandQueueInternal(InvokerData invoker, bool printAll) {
		IReadOnlyPlaylist queue = playlistManager.CurrentList;

		if (queue.Items.Count == 0) {
			return "There is nothing on right now...";
		}
	
		string output = "";
		if (playManager.IsPlaying) {
			output += "Current song: " + GetTitleAtIndex(queue, playlistManager.Index) + " - " +
			          GetNameAtIndex(queue, playlistManager.Index);
		}
	
		for (int i = playlistManager.Index + 1; i < queue.Items.Count; i++) {
			if (printAll || queue[i].Meta.ResourceOwnerUid == invoker.ClientUid) {
				output += "\n[" + (i - playlistManager.Index) + "] " + GetTitleAtIndex(queue, i) + " - " +
				          GetNameAtIndex(queue, i);
			} else {
				output += "\n[" + (i - playlistManager.Index) + "] Hidden Song Name - " + GetNameAtIndex(queue, i);
			}
		}
	
		return output;
	}
	
	[Command("skip")]
	public string CommandSkip(ExecutionInformation info, InvokerData invoker, ClientCall cc) {
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
