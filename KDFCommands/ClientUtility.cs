﻿using System;
using System.Linq;
using TS3AudioBot;
using TS3AudioBot.CommandSystem;
using TSLib;
using TSLib.Full;
using TSLib.Full.Book;

namespace KDFCommands {
	public static class ClientUtility {
		public static E<int> CountClientsInChannel(
			TsFullClient client, ChannelId channel, Func<Client, bool> predicate) {
			return client.Book.Clients.Values.Count(c => c.Channel == channel && predicate(c));
		}

		public static Client ClientByUid(TsFullClient ts3FullClient, Uid uid) {
			foreach (var (_, value) in ts3FullClient.Book.Clients) {
				if (value.Uid == uid)
					return value;
			}

			return null;
		}

		public static bool ClientIsOnline(TsFullClient ts3FullClient, Uid uid) {
			var result = ts3FullClient.ClientList(ClientListOptions.uid);
			if (!result) {
				return false;
			}

			return result.Value.Count(item => item.Uid == uid) != 0;
		}

		public static void SendMessage(Ts3Client client, ClientCall cc, string message) {
			if(cc.ClientId.HasValue)
				client.SendMessage(message, cc.ClientId.Value);
		}
	
		public static void CheckOnlineThrow(TsFullClient ts3FullClient, Uid uidStr) {
			if (!ClientIsOnline(ts3FullClient, uidStr)) {
				throw new CommandException(
					$"The user with UID '{uidStr}' is not online and therefore not allowed to use this command.",
					CommandExceptionReason.Unauthorized
				);
			}
		}

		public static string GetClientNameFromUid(TsFullClient ts3FullClient, Uid id) {
			Uid uid = id.Value == "Anonymous" ? ts3FullClient.Identity.ClientUid : id;

			var result = ts3FullClient.GetClientNameFromUid(uid);
			if (!result.Ok) {
				throw new CommandException($"The UID '{id}' does not exist on this server.", CommandExceptionReason.CommandError);
			}
			return result.Value.Name;
		}

		public static string GetUserNameOrBotName(string userId, TsFullClient ts3FullClient) {
			if (userId == null) {
				return GetClientNameFromUid(ts3FullClient, ts3FullClient.Identity.ClientUid);
			}

			return GetClientNameFromUid(ts3FullClient, new Uid(userId));
		}
	}
}
