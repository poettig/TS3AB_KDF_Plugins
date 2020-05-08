using System;
using TS3AudioBot;
using TS3AudioBot.Localization;

public static class Helper {
	public static bool UnwrapSendMessage(this E<LocalStr> r, Ts3Client ts3Client, ClientCall cc, string query) {
		if (!r.Ok) {
			ts3Client.SendMessage("Error occured for + '" + query + "': " + r.Error.Str, cc.ClientId.Value);
		}
		return r.Ok;
	}

	public static T UnwrapSendMessage<T>(this R<T, LocalStr> r, Ts3Client ts3Client, ClientCall cc, string query) {
		if (r.Ok) {
			return r.Value;
		}

		ts3Client.SendMessage("Error occured for + '" + query + "': " + r.Error.Str, cc.ClientId.Value);
		return default(T);
	}
}