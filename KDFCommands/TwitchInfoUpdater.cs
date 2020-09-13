using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;

namespace KDFCommands {
	public class TwitchInfoUpdater : IDisposable {
		private const string TwitchConfigFileName = "KDFCommands.json";

		public StreamInfo StreamInfo { get; private set; }
		public StreamerInfo StreamerInfo { get; private set; }

		private CancellationTokenSource TokenSource { get; }
		private readonly string streamerLoginName;
		private readonly TwitchConfig twitchConfig;
		private string twitchOAuthToken;

		public event EventHandler<TwitchInfoEventArgs> OnTwitchInfoChanged;

		public TwitchInfoUpdater(ConfPlugins confPlugins, string streamerLoginName) {
			this.streamerLoginName = streamerLoginName;

			var twitchConfigPath = Path.Combine(confPlugins.Path, TwitchConfigFileName);
			twitchConfig = JsonConvert.DeserializeObject<TwitchConfig>(File.ReadAllText(twitchConfigPath));
			
			TokenSource = new CancellationTokenSource();
			var descUpdaterThread = new Thread(TwitchInfoCollector) {
				IsBackground = true
			};
			descUpdaterThread.Start();
		}
		
		private void TwitchInfoCollector() {
			while (true) {
				var newStreamerInfo = TwitchApiRequest<StreamerInfo>($"users?login={streamerLoginName}");
				var newStreamInfo = TwitchApiRequest<StreamInfo>($"streams?user_login={streamerLoginName}");

				if (StreamerInfo == null || !StreamerInfo.Equals(newStreamerInfo) || StreamInfo == null || !StreamInfo.Equals(newStreamInfo)) {
					OnTwitchInfoChanged?.Invoke(this, new TwitchInfoEventArgs(newStreamerInfo, newStreamInfo));
				}

				StreamerInfo = newStreamerInfo;
				StreamInfo = newStreamInfo;
				
				// Wait for 60s until next update, cancel thread when token was cancelled
				if (TokenSource.Token.WaitHandle.WaitOne(60000)) {
					return;
				}
			}
		}

		private T TwitchApiRequest<T>(string endpoint) {
			if (twitchOAuthToken == null && !FetchTwitchOauthToken()) {
				return default(T);
			}

			for (int i = 0; i < 2; i++) {
				var result = WebWrapper.DownloadStringReturnHttpStatusCode(
					out string dataStr,
					new Uri($"https://api.twitch.tv/helix/{endpoint}"),
					("Client-ID", twitchConfig.ClientId),
					("Authorization", $"Bearer {twitchOAuthToken}")
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
			             $"?client_id={twitchConfig.ClientId}" +
			             $"&client_secret={twitchConfig.ClientSecret}" +
			             $"&grant_type=client_credentials";
			if (!WebWrapper.PostRequest(out string oauthDataStr, new Uri(url))) {
				return false;
			}

			var oauthData = JsonConvert.DeserializeObject<Dictionary<string, string>>(oauthDataStr);
			twitchOAuthToken = oauthData["access_token"];
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

        protected bool Equals(StreamerInfo other) {
	        if (Data == null && other.Data == null) {
		        return true;
	        } 
	        
	        if (Data == null || other.Data == null) {
		        return false;
	        }

	        return Data.SequenceEqual(other.Data);
        }

        public override bool Equals(object obj) {
	        if (ReferenceEquals(null, obj)) {
		        return false;
	        }

	        if (ReferenceEquals(this, obj)) {
		        return true;
	        }

	        if (obj.GetType() != this.GetType()) {
		        return false;
	        }

	        return Equals((StreamerInfo) obj);
        }

        public override int GetHashCode() {
	        return (Data != null ? Data.GetHashCode() : 0);
        }
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

        protected bool Equals(StreamerData other) {
	        return Id == other.Id && Login == other.Login && DisplayName == other.DisplayName && Type == other.Type &&
	               BroadcasterType == other.BroadcasterType && Description == other.Description &&
	               Equals(ProfileImageUrl, other.ProfileImageUrl) && Equals(OfflineImageUrl, other.OfflineImageUrl) &&
	               ViewCount == other.ViewCount;
        }

        public override bool Equals(object obj) {
	        if (ReferenceEquals(null, obj)) {
		        return false;
	        }

	        if (ReferenceEquals(this, obj)) {
		        return true;
	        }

	        if (obj.GetType() != this.GetType()) {
		        return false;
	        }

	        return Equals((StreamerData) obj);
        }

        public override int GetHashCode() {
	        var hashCode = new HashCode();
	        hashCode.Add(Id);
	        hashCode.Add(Login);
	        hashCode.Add(DisplayName);
	        hashCode.Add(Type);
	        hashCode.Add(BroadcasterType);
	        hashCode.Add(Description);
	        hashCode.Add(ProfileImageUrl);
	        hashCode.Add(OfflineImageUrl);
	        hashCode.Add(ViewCount);
	        return hashCode.ToHashCode();
        }
    }

    public class StreamInfo
    {
    	[JsonProperty("data")]
    	public StreamData[] Data { get; set; }

        protected bool Equals(StreamInfo other) {
	        if (Data == null && other.Data == null) {
		        return true;
	        }
	        
	        if (Data == null || other.Data == null) {
		        return false;
	        }

	        return Data.SequenceEqual(other.Data);
        }

        public override bool Equals(object obj) {
	        if (ReferenceEquals(null, obj)) {
		        return false;
	        }

	        if (ReferenceEquals(this, obj)) {
		        return true;
	        }

	        if (obj.GetType() != this.GetType()) {
		        return false;
	        }

	        return Equals((StreamInfo) obj);
        }

        public override int GetHashCode() {
	        return (Data != null ? Data.GetHashCode() : 0);
        }
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

        private string[] tagIds;
    	[JsonProperty("tag_ids")]
    	public string[] TagIds {
	        get => tagIds;
	        set => tagIds = value.OrderBy(o => o).ToArray();
        }

        protected bool Equals(StreamData other) {
	        return Id == other.Id && UserId == other.UserId && UserName == other.UserName && GameId == other.GameId &&
	               Type == other.Type && Title == other.Title && ViewerCount == other.ViewerCount &&
	               StartedAt.Equals(other.StartedAt) && Language == other.Language &&
	               ThumbnailUrl == other.ThumbnailUrl && TagIds.SequenceEqual(other.TagIds);
        }

        public override bool Equals(object obj) {
	        if (ReferenceEquals(null, obj)) {
		        return false;
	        }

	        if (ReferenceEquals(this, obj)) {
		        return true;
	        }

	        if (obj.GetType() != this.GetType()) {
		        return false;
	        }

	        return Equals((StreamData) obj);
        }

        public override int GetHashCode() {
	        var hashCode = new HashCode();
	        hashCode.Add(Id);
	        hashCode.Add(UserId);
	        hashCode.Add(UserName);
	        hashCode.Add(GameId);
	        hashCode.Add(Type);
	        hashCode.Add(Title);
	        hashCode.Add(ViewerCount);
	        hashCode.Add(StartedAt);
	        hashCode.Add(Language);
	        hashCode.Add(ThumbnailUrl);
	        hashCode.Add(TagIds);
	        return hashCode.ToHashCode();
        }
    }
    
    public class TwitchInfoEventArgs : EventArgs {
	    public StreamerInfo StreamerInfo { get; }
	    public StreamInfo StreamInfo { get; }

	    public TwitchInfoEventArgs(StreamerInfo streamerInfo, StreamInfo streamInfo) {
		    StreamerInfo = streamerInfo;
		    StreamInfo = streamInfo;
	    }
    }
}