using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using TS3AudioBot;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;

namespace KDFCommands {
	public class TwitchInfoUpdater : IDisposable {
		private const string TwitchConfigFileName = "KDFCommands.json";

		public StreamInfo StreamInfo { get; private set; }
		public StreamerInfo StreamerInfo { get; private set; }

		private CancellationTokenSource TokenSource { get; }
		private readonly string _streamerLoginName;
		private readonly TwitchConfig _twitchConfig;
		private string _twitchOAuthToken;

		public TwitchInfoUpdater(ConfPlugins confPlugins, string streamerLoginName) {
			_streamerLoginName = streamerLoginName;

			var twitchConfigPath = Path.Combine(confPlugins.Path, TwitchConfigFileName);
			_twitchConfig = JsonConvert.DeserializeObject<TwitchConfig>(File.ReadAllText(twitchConfigPath));
			
			TokenSource = new CancellationTokenSource();
			var descUpdaterThread = new Thread(TwitchInfoCollector) {
				IsBackground = true
			};
			descUpdaterThread.Start();
		}
		
		private void TwitchInfoCollector() {
			while (true) {
				StreamerInfo = TwitchApiRequest<StreamerInfo>($"users?login={_streamerLoginName}");
				StreamInfo = TwitchApiRequest<StreamInfo>($"streams?user_login={_streamerLoginName}");
				
				// Wait for 60s until next update, cancel thread when token was cancelled
				if (TokenSource.Token.WaitHandle.WaitOne(60000)) {
					return;
				}
			}
		}

		private T TwitchApiRequest<T>(string endpoint) {
			if (_twitchOAuthToken == null && !FetchTwitchOauthToken()) {
				return default(T);
			}

			for (int i = 0; i < 2; i++) {
				var result = WebWrapper.DownloadStringReturnHttpStatusCode(
					out string dataStr,
					new Uri($"https://api.twitch.tv/helix/{endpoint}"),
					("Client-ID", _twitchConfig.ClientId),
					("Authorization", $"Bearer {_twitchOAuthToken}")
				);

				if (!result.Ok) {
					return default(T);
				}

				if (result.Unwrap() == 200) {
					return JsonConvert.DeserializeObject<T>(dataStr);
				}

				// Get new oauth token and retry
				if (i == 0 && !FetchTwitchOauthToken()) {
					return default(T);
				}
			}

			return default(T);
		}

		private bool FetchTwitchOauthToken() {
			string url = $"https://id.twitch.tv/oauth2/token" +
			             $"?client_id={_twitchConfig.ClientId}" +
			             $"&client_secret={_twitchConfig.ClientSecret}" +
			             $"&grant_type=client_credentials";
			if (!WebWrapper.PostRequest(out string oauthDataStr, new Uri(url))) {
				return false;
			}

			var oauthData = JsonConvert.DeserializeObject<Dictionary<string, string>>(oauthDataStr);
			_twitchOAuthToken = oauthData["access_token"];
			return true;
		}
		
		public void Dispose() {
			TokenSource.Cancel();
		}
		
		private class TwitchConfig {
			[JsonProperty("ClientId")]
			public string ClientId { get; set; }

			[JsonProperty("ClientSecret")]
			public string ClientSecret { get; set; }
		}
	}
	
	public class StreamerInfo {
    	[JsonProperty("data")]
    	public StreamerData[] Data { get; set; }
    }
    
    public class StreamerData
    {
    	[JsonProperty("id")]
    	public long Id { get; set; }

    	[JsonProperty("login")]
    	public string Login { get; set; }

    	[JsonProperty("display_name")]
    	public string DisplayName { get; set; }

    	[JsonProperty("type")]
    	public string Type { get; set; }

    	[JsonProperty("broadcaster_type")]
    	public string BroadcasterType { get; set; }

    	[JsonProperty("description")]
    	public string Description { get; set; }

    	[JsonProperty("profile_image_url")]
    	public Uri ProfileImageUrl { get; set; }

    	[JsonProperty("offline_image_url")]
    	public Uri OfflineImageUrl { get; set; }

    	[JsonProperty("view_count")]
    	public long ViewCount { get; set; }
    }

    public class StreamInfo
    {
    	[JsonProperty("data")]
    	public StreamData[] Data { get; set; }
    }

    public class StreamData
    {
    	[JsonProperty("id")]
    	public string Id { get; set; }

    	[JsonProperty("user_id")]
    	public long UserId { get; set; }

    	[JsonProperty("user_name")]
    	public string UserName { get; set; }

    	[JsonProperty("game_id")]
    	public long GameId { get; set; }

    	[JsonProperty("type")]
    	public string Type { get; set; }

    	[JsonProperty("title")]
    	public string Title { get; set; }

    	[JsonProperty("viewer_count")]
    	public long ViewerCount { get; set; }

    	[JsonProperty("started_at")]
    	public DateTime StartedAt { get; set; }

    	[JsonProperty("language")]
    	public string Language { get; set; }

    	[JsonProperty("thumbnail_url")]
    	public string ThumbnailUrl { get; set; }

    	[JsonProperty("tag_ids")]
    	public Guid[] TagIds { get; set; }
    }
}