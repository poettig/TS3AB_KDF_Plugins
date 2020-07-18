using System;
using System.Collections.Generic;
using System.Linq;
using NLog.Fluent;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Model;
using TSLib;
using TSLib.Full;
using TSLib.Full.Book;

namespace KDFCommands {
	public static class ClientUtility {
		public static IEnumerable<Client> GetClientsInChannel(TsFullClient client, ChannelId channel) {
			foreach (Client c in client.Book.Clients.Values) {
				if (c.Channel == channel) yield return c;
			}
		}
		
		public static IEnumerable<Client> GetListeningClients(Ts3Client ts3Client, TsFullClient ts3FullClient) {
			return GetClientsInChannel(ts3FullClient, ts3FullClient.Book.CurrentChannel().Id)
				.Where(currentClient => {
					if (ts3FullClient.ClientId == currentClient.Id) // exclude bot
						return false;

					var data = ts3Client.GetClientInfoById(currentClient.Id);
					return data.Ok && !data.Value.OutputMuted;
				});
		}

		public static IEnumerable<Client> GetClientsByUidOnline(TsFullClient client, Uid uid) {
			foreach (Client c in client.Book.Clients.Values) {
				if (c.Uid == uid) yield return c;
			}
		}

		public static R<Client> GetFirstClientByUidOnline(TsFullClient ts3FullClient, Uid uid) {
			foreach (var client in GetClientsByUidOnline(ts3FullClient, uid)) {
				return client;
			}

			return R.Err;
		}

		public static bool ClientIsOnline(TsFullClient ts3FullClient, Uid uid) {
			return GetFirstClientByUidOnline(ts3FullClient, uid).Ok;
		}

		public static void SendMessage(Ts3Client client, ClientCall cc, string message) {
			if (cc == null || !cc.ClientId.HasValue)
				return;
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

		public static string GetClientNameFromUid(TsFullClient ts3FullClient, Uid uid) {
			var onlineClient = GetFirstClientByUidOnline(ts3FullClient, uid);
			if (onlineClient.Ok) {
				return onlineClient.Value.Name;
			}
			
			var result = ts3FullClient.GetClientNameFromUid(uid);
			if (!result.Ok) {
				throw new CommandException($"The UID '{uid}' does not exist on this server.", CommandExceptionReason.CommandError);
			}
			return result.Value.Name;
		}
	}
}
