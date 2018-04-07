﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Iot.Tenant.DataService
{
    using System;
    using System.Collections.Generic;
    using System.Fabric;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Iot.Tenant.DataService.Models;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Data.Collections;
    using Microsoft.ServiceFabric.Services.Communication.Runtime;
    using Microsoft.ServiceFabric.Services.Runtime;
    using Common;
    using Microsoft.ServiceFabric.Services.Communication.AspNetCore;

    internal sealed class DataService : StatefulService
    {
        internal const string EventDictionaryName = "store://events/dictionary";
        internal const string EventQueueName = "store://events/queue";
        private const int OffloadBatchSize = 100;
        private const int DrainIteration = 5;
        private readonly TimeSpan OffloadBatchInterval = TimeSpan.FromSeconds(10);


        public DataService(StatefulServiceContext context)
            : base(context)
        {
        }

        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new ServiceReplicaListener[1]
            {
                new ServiceReplicaListener(
                    context =>
                        new KestrelCommunicationListener(
                            context,
                            (url, listener) =>
                            {
                                ServiceEventSource.Current.Message($"Listening on {url}");

                                return new WebHostBuilder()
                                    .UseKestrel()
                                    .ConfigureServices(
                                        services => services
                                            .AddSingleton<StatefulServiceContext>(this.Context)
                                            .AddSingleton<IReliableStateManager>(this.StateManager))
                                    .UseContentRoot(Directory.GetCurrentDirectory())
                                    .UseServiceFabricIntegration(listener, ServiceFabricIntegrationOptions.UseUniqueServiceUrl)
                                    .UseStartup<Startup>()
                                    .UseUrls(url)
                                    .Build();
                            })
                    )
            };
        }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            IReliableQueue<DeviceEventSeries> queue = await this.StateManager.GetOrAddAsync<IReliableQueue<DeviceEventSeries>>(EventQueueName);

            int iteration = 0;
            while(true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using (ITransaction tx = this.StateManager.CreateTransaction())
                    {
                        // When the number of items in the queue reaches a certain size..
                        int count = (int) await queue.GetCountAsync(tx);

                        ServiceEventSource.Current.ServiceMessage(
                            this.Context, 
                            "Current queue size: {0} in iteration {1}",
                            count, iteration);

                        // if the queue size reaches the batch size, start draining the queue
                        // always drain the queue every nth iteration so that nothing sits in the queue indefinitely
                        if (count >= OffloadBatchSize || iteration == DrainIteration)
                        {
                            ServiceEventSource.Current.ServiceMessage(
                                this.Context, 
                                "Starting batch offload..");

                            // Dequeue the items into a batch
                            List<DeviceEventSeries> batch = new List<DeviceEventSeries>(count);

                            for (int i = 0; i < count; ++i)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                ConditionalValue<DeviceEventSeries> result = await queue.TryDequeueAsync(tx);

                                if (result.HasValue)
                                {
                                    batch.Add(result.Value);
                                }
                            }

                            //TODO: Process the data or send to a storage location.

                            // Commit the dequeue operations
                            await tx.CommitAsync();

                            ServiceEventSource.Current.ServiceMessage(
                                this.Context, 
                                "Batch offloaded {0} events.",
                                count);

                            // skip the delay and move on to the next batch.
                            iteration = 0;
                            continue;
                        }
                        else if (count > 0)
                        {
                            iteration++;
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // transient error. Retry.
                    ServiceEventSource.Current.ServiceMessage(this.Context, "TimeoutException in RunAsync.");
                }
                catch (FabricTransientException fte)
                {
                    // transient error. Retry.
                    ServiceEventSource.Current.ServiceMessage(this.Context, "FabricTransientException in RunAsync: {0}", fte.Message);
                }
                catch (FabricNotPrimaryException)
                {
                    // not primary any more, time to quit.
                    return;
                }

                await Task.Delay(this.OffloadBatchInterval, cancellationToken);
            }
        }
    }
}