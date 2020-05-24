﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NLog.Fluent;
using TS3AudioBot;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.CommandSystem.Text;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TSLib;
using TSLib.Full;
using TSLib.Full.Book;

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

		public abstract (Func<string> action, bool removeOnResourceEnd) Create(
			ExecutionInformation info, string command, string args);
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
				throw new CommandException("Skip expects no parameters or a number",
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

	public class CurrentVoteData {
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

	private readonly Dictionary<string, CurrentVoteData> _currentVotes =
		new Dictionary<string, CurrentVoteData>();

	private readonly List<CurrentVoteData> _removeOnResourceEnded = new List<CurrentVoteData>();

	public IReadOnlyDictionary<string, CurrentVoteData> CurrentVotes => _currentVotes;

	private readonly Ts3Client _client;
	private readonly ConfBot _config;
	private TsFullClient _ts3FullClient;

	public Voting(Ts3Client client, TsFullClient ts3FullClient, ConfBot config) {
		_client = client;
		_config = config;
		_ts3FullClient = ts3FullClient;
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

	public void OnSongEnd() {
		foreach (var vote in _removeOnResourceEnded) {
			_currentVotes.Remove(vote.Command);
			_client.SendChannelMessage($"Stopped vote for \"{vote.Command}\" due to end of resource.");
		}

		_removeOnResourceEnded.Clear();
	}

	private void Add(CurrentVoteData vote) {
		_currentVotes.Add(vote.Command, vote);
		if (vote.RemoveOnResourceEnd)
			_removeOnResourceEnded.Add(vote);
	}

	private void Remove(CurrentVoteData vote) {
		_currentVotes.Remove(vote.Command);
		if (vote.RemoveOnResourceEnd)
			_removeOnResourceEnded.Remove(vote);
	}

	private bool CheckAndFire(CurrentVoteData vote) {
		if (vote.Needed > vote.Voters.Count) {
			return false;
		}

		_client.SendChannelMessage($"Enough votes, executing \"{vote.Command}\"...");

		Remove(vote);
		var res = ExecuteTryCatch(_config, true, vote.Executor,
			err => _client.SendChannelMessage(err).UnwrapToLog(Log));
		if (!string.IsNullOrEmpty(res))
			_client.SendChannelMessage(res).UnwrapToLog(Log);

		return true;
	}

	private static E<int> CountClientsInChannel(
		TsFullClient client, ChannelId channel, Func<Client, bool> predicate) {
		return client.Book.Clients.Values.Count(c => c.Channel == channel && predicate(c));
	}

	public void CommandVote(ExecutionInformation info,
		Uid invoker, ChannelId botChannel, string command, string? args = null) {

		command = command.ToLower();
		if (string.IsNullOrWhiteSpace(command))
			throw new CommandException("No command to vote for given", CommandExceptionReason.CommandError);

		if (!VotableCommands.Commands.TryGetValue(command, out var votableCommand))
			throw new CommandException($"The given command \"{command}\" can't be voted for",
				CommandExceptionReason.CommandError);

		if (CurrentVotes.TryGetValue(command, out var currentVote)) {
			if (!string.IsNullOrWhiteSpace(args))
				throw new CommandException(
					"There is already a vote going on for this command. You can't start another vote for the same command with other parameters right now.",
					CommandExceptionReason.CommandError);

			if (currentVote.Voters.Remove(invoker)) {
				int count = currentVote.Voters.Count;
				if (count == 0) {
					Remove(currentVote);
					_client.SendChannelMessage($"Stopped vote for \"{command}\".");
				} else {
					_client.SendChannelMessage(
						$"Removed your vote for \"{command}\" ({currentVote.Voters.Count} votes of {currentVote.Needed})");
				}
			} else {
				currentVote.Voters.Add(invoker);
				_client.SendChannelMessage(
					$"Added your vote for \"{command}\" ({currentVote.Voters.Count} votes of {currentVote.Needed})");
				CheckAndFire(currentVote);
			}
		} else {
			var ci = new CallerInfo(false)
				{SkipRightsChecks = true, CommandComplexityMax = _config.Commands.CommandComplexity};

			bool CheckClient(Client client) {
				if (_ts3FullClient.ClientId == client.Id) // exclude bot
					return false;
				if (client.OutputMuted) // exclude muted
					return false;

				var data = _client.GetClientInfoById(client.Id);
				return !data.Ok || data.Value.ClientIdleTime <
				       MinIdleTimeForVoteIgnore; // include if data not ok or not long enough idle
			}

			int clientCount = CountClientsInChannel(_ts3FullClient, botChannel, CheckClient);
			info.AddModule(ci);
			var (executor, removeOnResourceEnd) = votableCommand.Create(info, command, args);
			currentVote = new CurrentVoteData(command, clientCount, executor, removeOnResourceEnd);
			Add(currentVote);
			currentVote.Voters.Add(invoker);
			_client.SendChannelMessage(
				$"Started vote for \"{command}\" ({currentVote.Voters.Count} votes of {currentVote.Needed}).");
			CheckAndFire(currentVote);
		}
	}
}