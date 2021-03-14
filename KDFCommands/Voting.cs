using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.CommandSystem.Text;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TSLib;
using TSLib.Full;
using TSLib.Full.Book;

namespace KDFCommands {
	public static class VotableCommands {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
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

			public abstract (Func<string> action, bool removeOnResourceEnd) Create(
				ExecutionInformation info, string command, string args);
		}

		public static string ExecuteCommandWithArgs(ExecutionInformation info, string command, string args) {
			StringBuilder builder = new StringBuilder();
			builder.Append("!").Append(command);
			if (args != null)
				builder.Append(' ').Append(args);
			string cmd = builder.ToString();
			Log.Info($"Vote succeeded, executing command: {cmd}");
			return CommandManager.ExecuteCommand(info, cmd);
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

			public override (Func<string> action, bool removeOnResourceEnd) Create(
				ExecutionInformation info, string command, string args) {
				AreEmpty(args).UnwrapThrow();
				return (() => ExecuteCommandWithArgs(info, command, args), ResetOnResourceEnd);
			}

			private EmptyArgsCommand(bool reset) { ResetOnResourceEnd = reset; }
			public static AVotableCommand ResetInstance { get; } = new EmptyArgsCommand(true);
			public static AVotableCommand Instance { get; } = new EmptyArgsCommand(false);
		}

		public class SkipCommand : AVotableCommand {
			public override (Func<string> action, bool removeOnResourceEnd) Create(
				ExecutionInformation info, string command, string args) {
				if (!string.IsNullOrWhiteSpace(args) && !int.TryParse(args, out _))
					throw new CommandException("Skip expects no parameters or a number.",
						CommandExceptionReason.CommandError);
				return (() => ExecuteCommandWithArgs(info, command, args), true);
			}

			private SkipCommand() { }
			public static AVotableCommand Instance { get; } = new SkipCommand();
		}

		public class FrontCommand : AVotableCommand {
			public override (Func<string> action, bool removeOnResourceEnd) Create(
				ExecutionInformation info, string command, string args) {
				AreNotEmpty(args).UnwrapThrow();
				return (() => ExecuteCommandWithArgs(info, command, args), false);
			}

			private FrontCommand() { }
			public static AVotableCommand Instance { get; } = new FrontCommand();
		}
	}

	public class Voting {
		private static readonly TimeSpan MinIdleTimeForVoteIgnore = TimeSpan.FromMinutes(10);
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public class SkipVoteEventArgs : EventArgs {
			public readonly Result Data;
			
			public SkipVoteEventArgs(Result data) {
				Data = data;
			}
		}
		
		public class CurrentVoteData {
			public string Command { get; }
			public Func<string> Executor { get; }
			public bool RemoveOnResourceEnd { get; }
			public HashSet<Uid> Voters { get; } = new HashSet<Uid>();

			public CurrentVoteData(string command, Func<string> executor, bool removeOnResourceEnd) {
				Command = command;
				Executor = executor;
				RemoveOnResourceEnd = removeOnResourceEnd;
			}
		}

		private readonly Dictionary<string, CurrentVoteData> currentVotes =
			new Dictionary<string, CurrentVoteData>();

		public int Needed { get; private set; }

		private readonly List<CurrentVoteData> removeOnResourceEnded = new List<CurrentVoteData>();

		public IReadOnlyDictionary<string, CurrentVoteData> CurrentVotes => currentVotes;
		
		public event EventHandler<SkipVoteEventArgs> OnSkipVoteChanged;

		private readonly Player player;
		private readonly Ts3Client client;
		private readonly ConfBot config;
		private readonly TsFullClient ts3FullClient;

		public Voting(Player player, Ts3Client client, TsFullClient ts3FullClient, ConfBot config) {
			this.player = player;
			this.client = client;
			this.config = config;
			this.ts3FullClient = ts3FullClient;
		}

		public static string ExecuteTryCatch(
			ConfBot config, bool answer, Func<string> action, Action<string> errorHandler) {
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

		private void UpdateNeededForUsers(IReadOnlyCollection<Uid> users) {
			{
				var n = Math.Max(users.Count / 2 + 1, 1);
				var old = Needed;
				Needed = n;
				if (old <= n)
					return;
			}

			foreach (var (_, vote) in CurrentVotes) {
				vote.Voters.IntersectWith(users);
				CheckAndFire(vote);
			}
		}

		public void OnSongEnd() {
			foreach (var vote in removeOnResourceEnded) {
				currentVotes.Remove(vote.Command);
				client.SendChannelMessage($"Stopped vote for \"{vote.Command}\" due to end of resource.");
			}

			removeOnResourceEnded.Clear();
		}

		public void OnBotChannelChanged() {
			var channel = ts3FullClient.Book.CurrentChannel();
			if (channel == null) {
				Log.Warn("OnBotChannelChanged: Could not get bot channel");
				return;
			}

			var clients = ClientUtility.GetClientsInChannel(ts3FullClient, channel.Id)
				.Where(CheckClientIncludedInVote)
				.Select(c => c.Uid);
			clients = clients.Concat(player.WebSocketPipe.Listeners.Select(Uid.To)).Distinct();
			var clientList = clients.ToList();
			Log.Trace("Relevant for voting: " + string.Join(", ", clientList.Select(uid => ClientUtility.GetClientNameFromUid(ts3FullClient, uid))));
			UpdateNeededForUsers(clientList.ToHashSet());
		}

		public void CancelAll() {
			currentVotes.Clear();
		}

		private void Add(CurrentVoteData vote) {
			currentVotes.Add(vote.Command, vote);
			if (vote.RemoveOnResourceEnd)
				removeOnResourceEnded.Add(vote);
		}

		private void Remove(CurrentVoteData vote) {
			currentVotes.Remove(vote.Command);
			if (vote.RemoveOnResourceEnd)
				removeOnResourceEnded.Remove(vote);
		}

		private bool CheckAndFire(CurrentVoteData vote) {
			// Vote is not finished if the needed vote count is not reached
			if (Needed > vote.Voters.Count) {
				return false;
			}

			client.SendChannelMessage($"Enough votes, executing \"{vote.Command}\"...");

			Remove(vote);
			var res = ExecuteTryCatch(config, true, vote.Executor,
				err => client.SendChannelMessage(err).UnwrapToLog(Log));
			if (!string.IsNullOrEmpty(res))
				client.SendChannelMessage(res).UnwrapToLog(Log);

			return true;
		}

		public class Result {
			// true = added user vote, false = removed user vote
			[JsonProperty(PropertyName = "VoteAdded")]
			public bool VoteAdded { get; set; }

			// this action resulted in a completed vote
			[JsonProperty(PropertyName = "VoteComplete")]
			public bool VoteComplete { get; set; }

			// this action resulted in a change in the vote set (created new or removed last vote)
			[JsonProperty(PropertyName = "VotesChanged")]
			public bool VotesChanged { get; set; }
			
			// The current vote standing
			[JsonProperty(PropertyName = "VoteCount")]
			public int VoteCount { get; set; }
			
			// The number of votes needed for success
			[JsonProperty(PropertyName = "VotesNeeded")]
			public int VotesNeeded { get; set; }
		}
		
		private bool CheckClientIncludedInVote(Client clientToCheck) {
			if (ts3FullClient.ClientId == clientToCheck.Id) // exclude bot
				return false;

			var data = this.client.GetClientInfoById(clientToCheck.Id);
			return data.Ok && !data.Value.OutputMuted && MinIdleTimeForVoteIgnore > data.Value.ClientIdleTime;
		}

		private CallerInfo CreateCallerInfo() {
			return new CallerInfo(false)
				{SkipRightsChecks = true, CommandComplexityMax = config.Commands.CommandComplexity};
		}

		public Result CommandVote(ExecutionInformation info, Uid invoker, string command, string args = null) {
			command = command.ToLower();
			if (string.IsNullOrWhiteSpace(command))
				throw new CommandException("No command to vote for given.", CommandExceptionReason.CommandError);

			if (!VotableCommands.Commands.TryGetValue(command, out var votableCommand))
				throw new CommandException($"The given command \"{command}\" can't be put up to vote.",
					CommandExceptionReason.CommandError);

			OnBotChannelChanged();

			bool voteAdded;
			bool votesChanged;
			bool voteCompleted;
			if (CurrentVotes.TryGetValue(command, out var currentVote)) {
				if (!string.IsNullOrWhiteSpace(args))
					throw new CommandException(
						"There is already a vote going on for this command. You can't start another vote for the same command with other parameters right now.",
						CommandExceptionReason.CommandError);

				if (currentVote.Voters.Remove(invoker)) {
					int count = currentVote.Voters.Count;
					voteAdded = false;
					if (count == 0) {
						Remove(currentVote);
						votesChanged = true;
						
						client.SendChannelMessage($"Removed vote of {ClientUtility.GetClientNameFromUid(ts3FullClient, invoker)} and stopped vote for \"{command}\".");
					} else {
						votesChanged = false;
						client.SendChannelMessage($"Removed vote of {ClientUtility.GetClientNameFromUid(ts3FullClient, invoker)} for \"{command}\" ({currentVote.Voters.Count} votes of {Needed})");
					}

					voteCompleted = false;
				} else {
					currentVote.Voters.Add(invoker);
					voteAdded = true;
					client.SendChannelMessage($"Added vote of {ClientUtility.GetClientNameFromUid(ts3FullClient, invoker)} for \"{command}\" ({currentVote.Voters.Count} votes of {Needed})");
					votesChanged = false;
					voteCompleted = CheckAndFire(currentVote);
				}
			} else {
				info.AddModule(CreateCallerInfo());
				var (executor, removeOnResourceEnd) = votableCommand.Create(info, command, args);

				currentVote = new CurrentVoteData(command, executor, removeOnResourceEnd);
				Add(currentVote);
				voteAdded = true;
				currentVote.Voters.Add(invoker);
				votesChanged = true;
				client.SendChannelMessage($"{ClientUtility.GetClientNameFromUid(ts3FullClient, invoker)} started vote for \"{command}\" ({currentVote.Voters.Count} votes of {Needed})");
				voteCompleted = CheckAndFire(currentVote);
			}

			var result = new Result {
				VoteAdded = voteAdded,
				VoteComplete = voteCompleted,
				VotesChanged = votesChanged,
				VoteCount = currentVote.Voters.Count,
				VotesNeeded = Needed
			};

			if (currentVote.Command == "skip" && (voteAdded || votesChanged || voteCompleted)) {
				OnSkipVoteChanged?.Invoke(this, new SkipVoteEventArgs(result));
			}

			return result;
		}
	}
}