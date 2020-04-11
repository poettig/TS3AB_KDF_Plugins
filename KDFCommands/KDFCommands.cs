using System;
//using System.Timers;
using System.Threading;
using System.Text.RegularExpressions;

using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Plugins;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Helper;

using TSLib;
using TSLib.Full;

public class KDFCommands : IBotPlugin {
    private const string YOUTUBE_URL_REGEX = "^(?:https?:\\/\\/)(?:www\\.)?(?:youtube\\.com\\/watch\\?v=(.*?)(?:&.*)*|youtu\\.be\\/(.*?)\\??.*)$";

//    private Timer descTimer;
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
    public KDFCommands(Player player, PlayManager playManager, PlaylistManager playlistManager, Ts3Client ts3Client, TsFullClient ts3FullClient) {
        this.player = player;
        this.playManager = playManager;
        this.playlistManager = playlistManager;
        this.ts3Client = ts3Client;
        this.ts3FullClient = ts3FullClient;

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

    private string getInvokerName(InvokerData invoker) {
        return ts3FullClient.GetClientNameFromUid(invoker.ClientUid).Value.Name;
    }

    private string getTitleAtIndex(IReadOnlyPlaylist queue, int index) {
        return queue[index].AudioResource.ResourceTitle;
    }

    private string getNameAtIndex(IReadOnlyPlaylist queue, int index) {
        return ts3FullClient.GetClientNameFromUid(queue[index].Meta.ResourceOwnerUid).Value.Name;
    }

    private bool inServerGroup(ClientCall cc, ulong groupId) {
        return Array.Exists(cc.ServerGroups, elem => elem.Value == groupId);
    }

    private void Start(object sender, PlayInfoEventArgs e) {
        title = e.ResourceData.ResourceTitle;
        username = getInvokerName(e.Invoker);
    }

    private void Stop(object sender, SongEndEventArgs e) {
    }

	public static void Shuffle<T>(IList<T> list, Random rng) {
	   int n = list.Count;
	   while (n > 1)
	   {
		  n--;
		  int k = rng.Next(n + 1);
		  T value = list[k];
		  list[k] = list[n];
		  list[n] = value;
	   }
	}

	[Command("list rqueue")]
	public static void CommandListRQueue(PlaylistManager playlistManager, PlayManager playManager, InvokerData invoker, string listId, int? countOpt = null)
	{
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
        ts3Client.SendMessage("Received your request to add " + parts.Length + " songs, processing...", cc.ClientId.Value);

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
                ts3Client.SendMessage("Added '" + audioResource.ResourceTitle + "' for your request '" + query.Replace("[URL]", "").Replace("[/URL]", "") + "'", cc.ClientId.Value);
            }
        }
    }

    [Command("del")]
    public void CommandDelete(ResolveContext resolver, InvokerData invoker, ClientCall cc, int id) {
        var queue = playlistManager.CurrentList;
        if (id >= queue.Items.Count) {
            throw new CommandException("There is no song with that index in the queue.", CommandExceptionReason.CommandError);
        }

        PlaylistItem item = queue[id];
        if (inServerGroup(cc, 49) || inServerGroup(cc, 73) || invoker.ClientUid == item.Meta.ResourceOwnerUid) {
            playlistManager.ModifyPlaylist(".mix", mix => mix.RemoveAt(id));
            ts3Client.SendMessage("Removed " + item.AudioResource.ResourceTitle + " (position " + id + ") from the queue.", cc.ClientId.Value);
        } else {
            throw new CommandException("You have no permission to delete this song from the queue.", CommandExceptionReason.CommandError);
        }
    }

    [Command("queue")]
    public string CommandQueue(ResolveContext resolver, InvokerData invoker, ClientCall cc) {
        return CommandQueueInternal(resolver, invoker, cc, false);
    }

    [Command("queue")]
    public string CommandQueue(ResolveContext resolver, InvokerData invoker, ClientCall cc, string arg) {
        return CommandQueueInternal(resolver, invoker, cc, arg == "full" && (inServerGroup(cc, 49) || inServerGroup(cc, 73)));
    }

    private string CommandQueueInternal(ResolveContext resolver, InvokerData invoker, ClientCall cc, bool printAll) {
        IReadOnlyPlaylist queue = playlistManager.CurrentList;

        string output = "";
        if (playManager.IsPlaying) {
            output += "Current song: " + getTitleAtIndex(queue, playlistManager.Index) + " - " + getNameAtIndex(queue, playlistManager.Index);
        }

        for (int i = playlistManager.Index + 1; i < queue.Items.Count; i++) {
            if (printAll || queue[i].Meta.ResourceOwnerUid == invoker.ClientUid) {
                output += "\n[" + (i - playlistManager.Index) + "] " + getTitleAtIndex(queue, i) + " - " + getNameAtIndex(queue, i);
            } else {
                output += "\n[" + (i - playlistManager.Index) + "] Hidden Song Name - " + getNameAtIndex(queue, i);
            }
        }

        return output;
    }

    [Command("skip")]
    public string CommandSkip(ResolveContext resolver, InvokerData invoker, ClientCall cc) {
        var queue = playlistManager.CurrentList;

        if (playManager.IsPlaying) {
            if (inServerGroup(cc, 49) || inServerGroup(cc, 73) || invoker.ClientUid == queue[playlistManager.Index].Meta.ResourceOwnerUid) {
                playManager.Next(invoker).UnwrapThrow();
                return "Skipped current song.";
            } else {
                return "You have no permission to skip this song.";
            }
        } else {
            return "There is not song currently playing.";
        }
    }

    public void Dispose() {
        playManager.AfterResourceStarted += Start;
        playManager.ResourceStopped += Stop;

        running = false;
        descThread.Join();
    }
}