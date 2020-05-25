using System;
using System.Text;
using System.Threading;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.Helper;

namespace KDFCommands {
	public class DescriptionData {
		public string Title { get; }
		public string PlaylistId { get; }
		public string Username { get; }

		public DescriptionData(string title, string playlistId, string username) {
			Title = title;
			PlaylistId = playlistId;
			Username = username;
		}

		private static void AppendPadded(StringBuilder builder, int i) {
			if (i < 10)
				builder.Append(0);
			builder.Append(i);
		}

		private static void AppendTimeSpan(StringBuilder builder, TimeSpan timeSpan) {
			if (timeSpan.Hours > 0) {
				AppendPadded(builder, timeSpan.Hours);
				builder.Append(':');
			}
			AppendPadded(builder, timeSpan.Minutes);
			builder.Append(':');
			AppendPadded(builder, timeSpan.Seconds);
		}

		public string MakeDescription(int queueLength, TimeSpan timeLeft) {
			StringBuilder builder = new StringBuilder();
			builder.Append("[");
			if (Username != null)
				builder.Append(Username);
			builder.Append(" ");
			AppendTimeSpan(builder, timeLeft);
			builder.Append(" Q").Append(queueLength).Append("] ").Append(Title);
			if (PlaylistId != null)
				builder.Append(" <Playlist: ").Append(PlaylistId).Append(">");
			return builder.ToString();
		}
	}

	public class Description : IDisposable {
		private class DescriptionThread {
			public DescriptionData Data { get; set; }

			private Player Player { get; }

			private Ts3Client Ts3Client { get; }

			private PlayManager PlayManager { get; }

			public DescriptionThread(
				Player player, Ts3Client ts3Client, PlayManager playManager, CancellationToken token) {
				Player = player;
				Ts3Client = ts3Client;
				PlayManager = playManager;

				var descUpdaterThread = new Thread(() => DescriptionUpdater(token)) {
					IsBackground = true
				};
				descUpdaterThread.Start();
			}

			private void DescriptionUpdater(CancellationToken token) {
				while (true) {
					var data = Data;
					if (PlayManager.IsPlaying && data != null) {
						int queueLength = PlayManager.Queue.Items.Count - PlayManager.Queue.Index - 1;
						TimeSpan timeLeft = Player.Length.Subtract(Player.Position);

						Ts3Client.ChangeDescription(Data.MakeDescription(queueLength, timeLeft)).UnwrapThrow();
					}

					if (token.WaitHandle.WaitOne(1000))
						return;
				}
			}
		}

		private DescriptionThread Thread { get; }
		private CancellationTokenSource TokenSource { get; }

		public DescriptionData Data {
			get => Thread.Data;
			set => Thread.Data = value;
		}

		public Description(Player player, Ts3Client ts3Client, PlayManager playManager) {
			TokenSource = new CancellationTokenSource();
			Thread = new DescriptionThread(player, ts3Client, playManager, TokenSource.Token);
		}

		public void Dispose() { TokenSource.Cancel(); }
	}
}