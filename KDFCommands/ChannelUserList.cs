using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TSLib;
using TSLib.Full;
using TSLib.Messages;

namespace KDFCommands {
	public class ChannelUserListChangedEventArgs : EventArgs {
		public bool NewChannel { get; set; }
	}

	public class ChannelUserList : IDisposable {
		private readonly HashSet<ClientId> _ids;
		private readonly TsFullClient _ts3FullClient;

		public IReadOnlyCollection<ClientId> Ids => _ids;

		public event EventHandler<ChannelUserListChangedEventArgs> OnChannelChanged;

		public ClientId Id { get; }
		public ChannelId Channel { get; private set; }

		public ChannelUserList(ClientId id, TsFullClient ts3FullClient) {
			Id = id;
			_ids = new HashSet<ClientId>();
			_ts3FullClient = ts3FullClient;

			_ts3FullClient.OnClientMoved += OnClientMoved;
			_ts3FullClient.OnClientLeftView += OnClientLeftView;
			_ts3FullClient.OnClientEnterView += OnClientEnterView;
		}

		public void Dispose() {
			_ts3FullClient.OnClientMoved -= OnClientMoved;
			_ts3FullClient.OnClientLeftView -= OnClientLeftView;
			_ts3FullClient.OnClientEnterView -= OnClientEnterView;
		}

		private void OnEnterNewChannel(ChannelId channel) {
			Channel = channel;
			_ids.Clear();
			foreach (var (clId, client) in _ts3FullClient.Book.Clients) {
				if (client.Channel == Channel)
					_ids.Add(clId);
			}
		}

		private void OnUserEnterChannel(ClientId client) { _ids.Add(client); }

		private void OnUserLeaveChannel(ClientId client) { _ids.Remove(client); }

		private void OnClientMoved(object _, IEnumerable<ClientMoved> moveds) {
			bool changed = false;
			bool newChannel = false;
			foreach (var clientMoved in moveds) {
				if (clientMoved.ClientId == Id) {
					OnEnterNewChannel(clientMoved.TargetChannelId);
					changed = true;
					newChannel = true;
				}

				if (clientMoved.TargetChannelId == Channel) {
					OnUserEnterChannel(clientMoved.ClientId);
					changed = true;
				} else if(_ids.Contains(clientMoved.ClientId)) {
					OnUserLeaveChannel(clientMoved.ClientId);
					changed = true;
				}
			}

			if (changed)
				InvokeOnChannelChanged(newChannel);
		}

		private void OnClientEnterView(object sender, IEnumerable<ClientEnterView> e) {
			bool changed = false;
			bool newChannel = false;
			foreach (var enterView in e) {
				if (enterView.ClientId == Id) {
					OnEnterNewChannel(enterView.TargetChannelId);
					changed = true;
					newChannel = true;
				}

				if (enterView.TargetChannelId == Channel) {
					OnUserEnterChannel(enterView.ClientId);
					changed = true;
				}
			}

			if (changed)
				InvokeOnChannelChanged(newChannel);
		}

		private void OnClientLeftView(object sender, IEnumerable<ClientLeftView> e) {
			bool changed = false;
			bool newChannel = false;
			foreach (var leftView in e) {
				if (leftView.ClientId == Id) {
					Channel = leftView.TargetChannelId;
					_ids.Clear();
					changed = true;
					newChannel = true;
				} else if (leftView.SourceChannelId == Channel) {
					OnUserLeaveChannel(leftView.ClientId);
					changed = true;
				}
			}

			if (changed)
				InvokeOnChannelChanged(newChannel);
		}

		private void InvokeOnChannelChanged(bool newChannel) {
			OnChannelChanged?.Invoke(this, new ChannelUserListChangedEventArgs {NewChannel = newChannel});
		}
	}
}
