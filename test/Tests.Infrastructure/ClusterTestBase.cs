﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Replication;
using Raven.Client.Exceptions;
using Raven.Client.Http;
using Raven.Client.Server;
using Raven.Client.Server.Commands;
using Raven.Client.Server.Operations;
using Raven.Client.Util;
using Raven.Server;
using Raven.Server.Documents;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Xunit;
using Constants = Raven.Client.Constants;

namespace Tests.Infrastructure
{
    [Trait("Category", "Cluster")]
    public abstract class ClusterTestBase : RavenTestBase
    {
        private const int PortRangeStart = 9000;
        private const int ElectionTimeoutInMs = 300;
        private static int numberOfPortRequests;

        internal static int GetPort()
        {
            var portRequest = Interlocked.Increment(ref numberOfPortRequests);
            return PortRangeStart - (portRequest % 500);
        }

        protected readonly ConcurrentBag<IDisposable> _toDispose = new ConcurrentBag<IDisposable>();

        protected List<RavenServer> Servers = new List<RavenServer>();
        private readonly Random _random = new Random();

        protected IDisposable NoTimeouts()
        {
            TimeoutEvent.Disable = true;
            return new DisposableAction(() => TimeoutEvent.Disable = false);
        }

        protected async Task CreateAndWaitForClusterDatabase(string databaseName, IDocumentStore store)
        {
            if (Servers.Count == 0)
                throw new InvalidOperationException("You cannot create a database on an empty cluster...");

            var databaseResult = CreateClusterDatabase(databaseName, store);

            Assert.True((databaseResult.ETag ?? 0) > 0); //sanity check                

            await WaitForEtagInCluster(databaseResult.ETag ?? 0, TimeSpan.FromSeconds(5));
        }

        protected static CreateDatabaseResult CreateClusterDatabase(string databaseName, IDocumentStore store, int replicationFactor = 2)
        {
            var doc = MultiDatabase.CreateDatabaseDocument(databaseName);
            var databaseResult = store.Admin.Server.Send(new CreateDatabaseOperation(doc, replicationFactor));
            return databaseResult;
        }


        protected async Task<bool> WaitUntilDatabaseHasState(DocumentStore store, TimeSpan timeout, bool isLoaded, string databaseName = null)
        {
            var requestExecutor = store.GetRequestExecuter();
            using (var context = JsonOperationContext.ShortTermSingleUse())
            {
                var shouldContinue = true;
                var timeoutTask = Task.Delay(timeout);
                while (shouldContinue && timeoutTask.IsCompleted == false)
                {
                    try
                    {
                        var databaseIsLoadedCommand = new IsDatabaseLoadedCommand();
                        await requestExecutor.ExecuteAsync(databaseIsLoadedCommand, context);
                        shouldContinue = databaseIsLoadedCommand.Result.IsLoaded != isLoaded;
                        await Task.Delay(100);
                    }
                    catch (OperationCanceledException)
                    {
                        //OperationCanceledException is thrown if the database is currently shutting down
                    }
                }

                return timeoutTask.IsCompleted == false;
            }
        }

        protected async Task<bool> WaitForDocumentInClusterAsync<T>(IReadOnlyList<ServerNode> topology,string docId, Func<T, bool> predicate, TimeSpan timeout)
        {
            var stores = GetStoresFromTopology(topology);
            var tasks = new List<Task<bool>>();

            foreach(var store in stores)
                tasks.Add(Task.Run(() => WaitForDocument(store, docId, predicate,(int)timeout.TotalMilliseconds)));

            var timeoutTask = Task.Delay(timeout);
            await Task.WhenAny(Task.WhenAll(tasks), timeoutTask);

            if(timeoutTask.IsCompleted)
                throw new TimeoutException();

            return tasks.All(x => x.Result);
        }

        protected bool WaitForDocument<T>(DocumentStore store,
            string docId,
            Func<T, bool> predicate,
            int timeout = 10000)
        {
            if (Debugger.IsAttached)
                timeout *= 100;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeout)
            {
                using (var session = store.OpenSession())
                {
                    try
                    {
                        var doc = session.Load<T>(docId);
                        if (doc != null && predicate(doc))
                        {
                            return true;
                        }
                    }
                    catch (ConflictException)
                    {
                        // expected that we might get conflict, ignore and wait
                    }
                }
            }
            using (var session = store.OpenSession())
            {
                //one last try, and throw if there is still a conflict
                var doc = session.Load<T>(docId);
                if (doc != null && predicate(doc))
                    return true;
            }
            return false;
        }


        protected static void DisposeServerAndWaitForFinishOfDisposal(RavenServer serverToDispose)
        {
            var mre = new ManualResetEventSlim();
            serverToDispose.AfterDisposal += () => mre.Set();
            serverToDispose.Dispose();

            mre.Wait();
        }

        protected List<DocumentStore> GetStoresFromTopology(IReadOnlyList<ServerNode> topologyNodes)
        {
            var stores = new List<DocumentStore>();
            var tokenToUrl = Servers.ToDictionary(x => x.ServerStore.NodeTag, x => x.WebUrls[0]);
            foreach (var node in topologyNodes)
            {
                string url;
                if(!tokenToUrl.TryGetValue(node.ClusterTag, out url)) //precaution
                    continue;

                var store = new DocumentStore
                {
                    DefaultDatabase = node.Database,
                    Url = url
                };

                _toDispose.Add(store);

                store.Initialize();
                stores.Add(store);
            }
            return stores;
        }

        protected async Task<RavenServer> SetupRaftClusterOnExistingServers(params RavenServer[] servers)
        {
            RavenServer leader = null;
            var numberOfNodes = servers.Length;
            var serversToPorts = new Dictionary<RavenServer, string>();
            var leaderIndex = _random.Next(0, numberOfNodes);
            for (var i = 0; i < numberOfNodes; i++)
            {
                var server = servers[i];
                serversToPorts.Add(server, server.WebUrls[0]);
                if (i == leaderIndex)
                {
                    server.ServerStore.EnsureNotPassive();
                    leader = server;
                    break;
                }
            }

            for (var i = 0; i < numberOfNodes; i++)
            {
                if (i == leaderIndex)
                {
                    continue;
                }
                var follower = Servers[i];
                // ReSharper disable once PossibleNullReferenceException
                await leader.ServerStore.AddNodeToClusterAsync(serversToPorts[follower]);
                await follower.ServerStore.WaitForTopology(Leader.TopologyModification.Voter);
            }
            // ReSharper disable once PossibleNullReferenceException
            Assert.True(await leader.ServerStore.WaitForState(RachisConsensus.State.Leader).WaitAsync(numberOfNodes * ElectionTimeoutInMs),
                "The leader has changed while waiting for cluster to become stable. Status: " + leader.ServerStore.ClusterStatus());
            return leader;
        }

        protected async Task<RavenServer> CreateRaftClusterAndGetLeader(int numberOfNodes, bool shouldRunInMemory = true)
        {
            var leaderIndex = _random.Next(0, numberOfNodes);
            RavenServer leader = null;
            var serversToPorts = new Dictionary<RavenServer,string>();
            for (var i = 0; i < numberOfNodes; i++)
            {
                var serverUrl = $"http://localhost:{GetPort()}";
                var server = GetNewServer(new Dictionary<string, string>()
                {                    
                    {"Raven/ServerUrl", serverUrl}
                },runInMemory:shouldRunInMemory);
                serversToPorts.Add(server, serverUrl);
                Servers.Add(server);
                if (i == leaderIndex)
                {
                    server.ServerStore.EnsureNotPassive();
                    leader = server;
                }
            }
            for (var i = 0; i < numberOfNodes; i++)
            {
                if (i == leaderIndex)
                {
                    continue;
                }
                var follower = Servers[i];
                // ReSharper disable once PossibleNullReferenceException
                await leader.ServerStore.AddNodeToClusterAsync(serversToPorts[follower]);
                await follower.ServerStore.WaitForTopology(Leader.TopologyModification.Voter);
            }
            // ReSharper disable once PossibleNullReferenceException
            var condition = await leader.ServerStore.WaitForState(RachisConsensus.State.Leader).WaitAsync(numberOfNodes* ElectionTimeoutInMs);
            Assert.True(condition,
                "The leader has changed while waiting for cluster to become stable. Status: " + leader.ServerStore.ClusterStatus() );
            return leader;
        }

        public async Task WaitForLeader(TimeSpan timeout)
        {
            var tasks = Servers
                .Select(server => server.ServerStore.WaitForState(RachisConsensus.State.Leader))
                .ToList();

            tasks.Add(Task.Delay(timeout));
            await Task.WhenAny(tasks);

            if (Task.Delay(timeout).IsCompleted)
                throw new TimeoutException();
        }

        protected override Task<DocumentDatabase> GetDocumentDatabaseInstanceFor(IDocumentStore store)
        {
            //var index = FindStoreIndex(store);
            //Assert.False(index == -1, "Didn't find store index, most likely it doesn't belong to the cluster. Did you setup Raft cluster properly?");
            //return Servers[index].ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);
            return Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);
        }

        private int FindStoreIndex(IDocumentStore store)
        {
            return Servers.FindIndex(srv => srv.WebUrls[0].Equals(store.Url, StringComparison.OrdinalIgnoreCase));
        }

        public async Task WaitForEtagInCluster(long etag,  TimeSpan timeout)
        {
            if (Servers.Count == 0)
                return;

            //maybe we are already at that etag, in this case nothing to do
            if(Servers.All(server => server.ServerStore.LastRaftCommitEtag >= etag))
                return;
            
            var tasks = 
                Servers
                .Select(server => server.ServerStore.Cluster.WaitForIndexNotification(etag))
                .ToList();
                        
            var timeoutTask = Task.Delay(timeout);
            
            await Task.WhenAny(timeoutTask, Task.WhenAll(tasks));

            if(timeoutTask.IsCompleted)
                throw new TimeoutException();
        }

        public override void Dispose()
        {
            foreach (var disposable in _toDispose)
                disposable.Dispose();

            foreach (var server in Servers)
            {
                server.Dispose();
            }

            base.Dispose();
        }
    }
}
