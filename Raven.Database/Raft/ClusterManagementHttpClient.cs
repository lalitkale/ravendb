﻿// -----------------------------------------------------------------------
//  <copyright file="ClusterManagementHttpClient.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Rachis;
using Rachis.Commands;
using Rachis.Storage;
using Rachis.Transport;
using Rachis.Utils;

using Raven.Abstractions.Connection;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.OAuth;
using Raven.Database.Raft.Commands;
using Raven.Database.Raft.Dto;
using Raven.Database.Raft.Util;
using Raven.Imports.Newtonsoft.Json;
using Raven.Json.Linq;

namespace Raven.Database.Raft
{
	public class ClusterManagementHttpClient : IDisposable
	{
		private const int WaitForLeaderTimeoutInSeconds = 15;

		private readonly RaftEngine raftEngine;

		private readonly ConcurrentDictionary<string, ConcurrentQueue<HttpClient>> _httpClientsCache = new ConcurrentDictionary<string, ConcurrentQueue<HttpClient>>();

		private readonly ConcurrentDictionary<string, SecuredAuthenticator> _securedAuthenticatorCache = new ConcurrentDictionary<string, SecuredAuthenticator>();

		public ClusterManagementHttpClient(RaftEngine raftEngine)
		{
			this.raftEngine = raftEngine;
		}

		private HttpRaftRequest CreateRequest(NodeConnectionInfo node, string url, string method)
		{
			var request = new HttpRaftRequest(node, url, method, info =>
			{
				HttpClient client;
				var dispose = (IDisposable) GetConnection(info, out client);
				return Tuple.Create(dispose, client);
			},
			CancellationToken.None)
			{
				UnauthorizedResponseAsyncHandler = HandleUnauthorizedResponseAsync,
				ForbiddenResponseAsyncHandler = HandleForbiddenResponseAsync
			};
			GetAuthenticator(node).ConfigureRequest(this, new WebRequestEventArgs
			{
				Client = request.HttpClient,
				Credentials = node.ToOperationCredentials()
			});
			return request;
		}

		internal Task<Action<HttpClient>> HandleUnauthorizedResponseAsync(HttpResponseMessage unauthorizedResponse, NodeConnectionInfo nodeConnectionInfo)
		{
			var oauthSource = unauthorizedResponse.Headers.GetFirstValue("OAuth-Source");

			if (nodeConnectionInfo.ApiKey == null)
			{
				AssertUnauthorizedCredentialSupportWindowsAuth(unauthorizedResponse, nodeConnectionInfo);
				return null;
			}

			if (string.IsNullOrEmpty(oauthSource))
				oauthSource = nodeConnectionInfo.Uri.AbsoluteUri + "/OAuth/API-Key";

			return GetAuthenticator(nodeConnectionInfo).DoOAuthRequestAsync(nodeConnectionInfo.Uri.AbsoluteUri, oauthSource, nodeConnectionInfo.ApiKey);
		}

		private void AssertUnauthorizedCredentialSupportWindowsAuth(HttpResponseMessage response, NodeConnectionInfo nodeConnectionInfo)
		{
			if (nodeConnectionInfo.Username == null)
				return;

			var authHeaders = response.Headers.WwwAuthenticate.FirstOrDefault();
			if (authHeaders == null || (authHeaders.ToString().Contains("NTLM") == false && authHeaders.ToString().Contains("Negotiate") == false))
			{
				// we are trying to do windows auth, but we didn't get the windows auth headers
				throw new SecurityException(
					"Attempted to connect to a RavenDB Server that requires authentication using Windows credentials," + Environment.NewLine
					+ " but either wrong credentials where entered or the specified server does not support Windows authentication." +
					Environment.NewLine +
					"If you are running inside IIS, make sure to enable Windows authentication.");
			}
		}


		internal Task<Action<HttpClient>> HandleForbiddenResponseAsync(HttpResponseMessage forbiddenResponse, NodeConnectionInfo nodeConnection)
		{
			if (nodeConnection.ApiKey == null)
			{
				AssertForbiddenCredentialSupportWindowsAuth(forbiddenResponse, nodeConnection);
				return null;
			}

			return null;
		}

		private void AssertForbiddenCredentialSupportWindowsAuth(HttpResponseMessage response, NodeConnectionInfo nodeConnection)
		{
			if (nodeConnection.ToOperationCredentials().Credentials == null)
				return;

			var requiredAuth = response.Headers.GetFirstValue("Raven-Required-Auth");
			if (requiredAuth == "Windows")
			{
				// we are trying to do windows auth, but we didn't get the windows auth headers
				throw new SecurityException(
					"Attempted to connect to a RavenDB Server that requires authentication using Windows credentials, but the specified server does not support Windows authentication." +
					Environment.NewLine +
					"If you are running inside IIS, make sure to enable Windows authentication.");
			}
		}

		public async Task SendJoinServerAsync(NodeConnectionInfo nodeConnectionInfo)
		{
			try
			{
				await raftEngine.AddToClusterAsync(nodeConnectionInfo).ConfigureAwait(false);
				return;
			}
			catch (NotLeadingException)
			{
			}
			await SendJoinServerInternalAsync(raftEngine.GetLeaderNode(WaitForLeaderTimeoutInSeconds), nodeConnectionInfo).ConfigureAwait(false);
		}

		public async Task<CanJoinResult> SendJoinServerInternalAsync(NodeConnectionInfo leaderNode, NodeConnectionInfo newNode)
		{
			var url = leaderNode.Uri.AbsoluteUri + "admin/cluster/join";

			using (var request = CreateRequest(leaderNode, url, "POST"))
			{
				var response = await request.WriteAsync(() => new JsonContent(RavenJToken.FromObject(newNode))).ConfigureAwait(false);

				if (response.IsSuccessStatusCode)
					return CanJoinResult.CanJoin;

				switch (response.StatusCode)
				{
					case HttpStatusCode.NotModified:
						return CanJoinResult.AlreadyJoined;
					case HttpStatusCode.NotAcceptable:
						return CanJoinResult.InAnotherCluster;
					default:
						throw await CreateErrorResponseExceptionAsync(response).ConfigureAwait(false);
				}
			}
		}

		public Task SendClusterConfigurationAsync(ClusterConfiguration configuration)
		{
			try
			{
				var command = ClusterConfigurationUpdateCommand.Create(configuration);
				raftEngine.AppendCommand(command);
				return command.Completion.Task;
			}
			catch (NotLeadingException)
			{
				return SendClusterConfigurationInternalAsync(raftEngine.GetLeaderNode(WaitForLeaderTimeoutInSeconds), configuration);
			}
		}

		public Task SendDatabaseUpdateAsync(string databaseName, DatabaseDocument document)
		{
			try
			{
				var command = DatabaseUpdateCommand.Create(databaseName, document);
				raftEngine.AppendCommand(command);
				return command.Completion.Task;
			}
			catch (NotLeadingException)
			{
				return SendDatabaseUpdateInternalAsync(raftEngine.GetLeaderNode(WaitForLeaderTimeoutInSeconds), databaseName, document);
			}
		}

		public Task SendDatabaseDeleteAsync(string databaseName, bool hardDelete)
		{
			try
			{
				var command = DatabaseDeletedCommand.Create(databaseName, hardDelete);
				raftEngine.AppendCommand(command);
				return command.Completion.Task;
			}
			catch (NotLeadingException)
			{
				return SendDatabaseDeleteInternalAsync(raftEngine.GetLeaderNode(WaitForLeaderTimeoutInSeconds), databaseName, hardDelete);
			}
		}

		private async Task SendDatabaseDeleteInternalAsync(NodeConnectionInfo node, string databaseName, bool hardDelete)
		{
			var url = node.Uri.AbsoluteUri + "admin/cluster/commands/database/" + Uri.EscapeDataString(databaseName) + "?hardDelete=" + hardDelete;
			using (var request = CreateRequest(node, url, "DELETE"))
			{
				var response = await request.ExecuteAsync().ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
					return;

				throw await CreateErrorResponseExceptionAsync(response).ConfigureAwait(false);
			}
		}

		private Task SendClusterConfigurationInternalAsync(NodeConnectionInfo leaderNode, ClusterConfiguration configuration)
		{
			return PutAsync(leaderNode, "admin/cluster/commands/configuration", configuration);
		}

		private Task SendDatabaseUpdateInternalAsync(NodeConnectionInfo leaderNode, string databaseName, DatabaseDocument document)
		{
			return PutAsync(leaderNode, "admin/cluster/commands/database/" + Uri.EscapeDataString(databaseName), document);
		}

		private async Task PutAsync(NodeConnectionInfo node, string action, object content)
		{
			var url = node.Uri.AbsoluteUri + action;
			using (var request = CreateRequest(node, url, "PUT"))
			{
				var response = await request.WriteAsync(() => new JsonContent(RavenJObject.FromObject(content))).ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
					return;

				throw await CreateErrorResponseExceptionAsync(response).ConfigureAwait(false);
			}
		}

		public async Task<CanJoinResult> SendCanJoinAsync(NodeConnectionInfo nodeConnectionInfo)
		{
			var url = nodeConnectionInfo.Uri.AbsoluteUri + "admin/cluster/canJoin?topologyId=" + raftEngine.CurrentTopology.TopologyId;

			using (var request = CreateRequest(nodeConnectionInfo, url, "GET"))
			{
				var response = await request.ExecuteAsync().ConfigureAwait(false);

				if (response.IsSuccessStatusCode)
					return CanJoinResult.CanJoin;

				switch (response.StatusCode)
				{
					case HttpStatusCode.NotModified:
						return CanJoinResult.AlreadyJoined;
					case HttpStatusCode.NotAcceptable:
						return CanJoinResult.InAnotherCluster;
					default:
						throw await CreateErrorResponseExceptionAsync(response).ConfigureAwait(false);
				}
			}
		}

		public Task SendNodeUpdateAsync(NodeConnectionInfo node)
		{
			try
			{
				var currentTopology = raftEngine.CurrentTopology;
				var requestedTopology = new Topology(
					currentTopology.TopologyId,
					currentTopology.AllVotingNodes.Select(n => n.Uri.AbsoluteUri == node.Uri.AbsoluteUri ? node : n).ToList(),
					currentTopology.NonVotingNodes.Select(n => n.Uri.AbsoluteUri == node.Uri.AbsoluteUri ? node : n).ToList(),
					currentTopology.PromotableNodes.Select(n => n.Uri.AbsoluteUri == node.Uri.AbsoluteUri ? node : n).ToList());

				var command = new TopologyChangeCommand
				{
					Completion = new TaskCompletionSource<object>(),
					Requested = requestedTopology,
					Previous = currentTopology
				};

				raftEngine.AppendCommand(command);
				return command.Completion.Task;
			}
			catch (NotLeadingException)
			{
				return SendNodeUpdateInternalAsync(raftEngine.GetLeaderNode(WaitForLeaderTimeoutInSeconds), node);
			}
		}

		public async Task SendNodeUpdateInternalAsync(NodeConnectionInfo leaderNode, NodeConnectionInfo nodeToUpdate)
		{
			var url = leaderNode.Uri.AbsoluteUri + "admin/cluster/update";
			using (var request = CreateRequest(leaderNode, url, "POST"))
			{
				var response = await request.WriteAsync(() => new JsonContent(RavenJToken.FromObject(nodeToUpdate))).ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
					return;

				throw await CreateErrorResponseExceptionAsync(response).ConfigureAwait(false);
			}
		}

		public async Task SendLeaveAsync(NodeConnectionInfo node)
		{
			try
			{
				if (raftEngine.Options.SelfConnection == node)
				{
					await raftEngine.StepDownAsync().ConfigureAwait(false);
					raftEngine.WaitForLeader();
				}
				else
				{
					await raftEngine.RemoveFromClusterAsync(node).ConfigureAwait(false);
				}
			}
			catch (NotLeadingException)
			{
			}

			await SendLeaveClusterInternalAsync(raftEngine.GetLeaderNode(WaitForLeaderTimeoutInSeconds), node).ConfigureAwait(false);
		}

		public async Task SendLeaveClusterInternalAsync(NodeConnectionInfo leaderNode, NodeConnectionInfo leavingNode)
		{
			var url = leavingNode.Uri.AbsoluteUri + "admin/cluster/leave?name=" + leavingNode.Name;
			using (var request = CreateRequest(leavingNode, url, "GET"))
			{
				var response = await request.ExecuteAsync().ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
					return;

				throw await CreateErrorResponseExceptionAsync(response).ConfigureAwait(false);
			}
		}

		private static async Task<ErrorResponseException> CreateErrorResponseExceptionAsync(HttpResponseMessage response)
		{
			using (var sr = new StreamReader(await response.GetResponseStreamWithHttpDecompression().ConfigureAwait(false)))
			{
				var readToEnd = sr.ReadToEnd();

				if (string.IsNullOrWhiteSpace(readToEnd))
					throw ErrorResponseException.FromResponseMessage(response);

				RavenJObject ravenJObject;
				try
				{
					ravenJObject = RavenJObject.Parse(readToEnd);
				}
				catch (Exception e)
				{
					throw new ErrorResponseException(response, readToEnd, e);
				}

				if (response.StatusCode == HttpStatusCode.BadRequest && ravenJObject.ContainsKey("Message"))
				{
					throw new BadRequestException(ravenJObject.Value<string>("Message"), ErrorResponseException.FromResponseMessage(response));
				}

				if (ravenJObject.ContainsKey("Error"))
				{
					var sb = new StringBuilder();
					foreach (var prop in ravenJObject)
					{
						if (prop.Key == "Error")
							continue;

						sb.Append(prop.Key).Append(": ").AppendLine(prop.Value.ToString(Formatting.Indented));
					}

					if (sb.Length > 0)
						sb.AppendLine();
					sb.Append(ravenJObject.Value<string>("Error"));

					throw new ErrorResponseException(response, sb.ToString(), readToEnd);
				}

				throw new ErrorResponseException(response, readToEnd);
			}
		}

		public async Task<Guid> GetDatabaseId(NodeConnectionInfo nodeConnectionInfo)
		{
			var url = nodeConnectionInfo.Uri + "/stats";
			using (var request = CreateRequest(nodeConnectionInfo, url, "GET"))
			{
				var response = await request.ExecuteAsync().ConfigureAwait(false);
				if (!response.IsSuccessStatusCode)
					throw new InvalidOperationException("Unable to fetch database statictics for: " + nodeConnectionInfo.Uri);

				using (var responseStream = await response.GetResponseStreamWithHttpDecompression().ConfigureAwait(false))
				{
					var json = RavenJToken.TryLoad(responseStream);
					var stats = json.JsonDeserialization<DatabaseStatistics>();
					return stats.DatabaseId;
				}
			}
		}

		public void Dispose()
		{
			foreach (var q in _httpClientsCache.Select(x => x.Value))
			{
				HttpClient result;
				while (q.TryDequeue(out result))
				{
					result.Dispose();
				}
			}
			_httpClientsCache.Clear();

			_securedAuthenticatorCache.Clear();
		}

		internal SecuredAuthenticator GetAuthenticator(NodeConnectionInfo info)
		{
			return _securedAuthenticatorCache.GetOrAdd(info.Uri.AbsoluteUri, _ => new SecuredAuthenticator());
		}

		internal ReturnToQueue GetConnection(NodeConnectionInfo nodeConnection, out HttpClient result)
		{
			var connectionQueue = _httpClientsCache.GetOrAdd(nodeConnection.Uri.AbsoluteUri, _ => new ConcurrentQueue<HttpClient>());

			if (connectionQueue.TryDequeue(out result) == false)
			{
				var webRequestHandler = new WebRequestHandler
				{
					UseDefaultCredentials = nodeConnection.HasCredentials() == false,
					Credentials = nodeConnection.ToOperationCredentials().Credentials
				};

				result = new HttpClient(webRequestHandler)
				{
					BaseAddress = nodeConnection.Uri
				};
			}

			return new ReturnToQueue(result, connectionQueue);
		}

		internal struct ReturnToQueue : IDisposable
		{
			private readonly HttpClient client;
			private readonly ConcurrentQueue<HttpClient> queue;

			public ReturnToQueue(HttpClient client, ConcurrentQueue<HttpClient> queue)
			{
				this.client = client;
				this.queue = queue;
			}

			public void Dispose()
			{
				queue.Enqueue(client);
			}
		}
	}

	public enum CanJoinResult
	{
		CanJoin,

		AlreadyJoined,

		InAnotherCluster
	}
}