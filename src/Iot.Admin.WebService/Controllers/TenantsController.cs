﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Iot.Admin.WebService.Controllers
{
    using Iot.Admin.WebService.Models;
    using Iot.Admin.WebService.ViewModels;
    using Iot.Common;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Mvc;
    using System;
    using System.Fabric;
    using System.Fabric.Description;
    using System.Fabric.Query;
    using System.Linq;
    using System.Threading.Tasks;

    [Route("api/[Controller]")]
    public class TenantsController : Controller
    {
        private readonly TimeSpan operationTimeout = TimeSpan.FromSeconds(20);
        private readonly FabricClient fabricClient;
        private readonly IApplicationLifetime appLifetime;

        public TenantsController(FabricClient fabricClient, IApplicationLifetime appLifetime)
        {
            this.fabricClient = fabricClient;
            this.appLifetime = appLifetime;
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            ApplicationList applications = await this.fabricClient.QueryManager.GetApplicationListAsync();

            return this.Ok(
                applications
                    .Where(x => x.ApplicationTypeName == Names.TenantApplicationTypeName)
                    .Select(
                        x =>
                            new ApplicationViewModel(
                                x.ApplicationName.ToString(),
                                x.ApplicationStatus.ToString(),
                                x.ApplicationTypeVersion,
                                x.ApplicationParameters)));
        }

        [HttpPost]
        [Route("{tenantName}")]
        public async Task<IActionResult> Post([FromRoute] string tenantName, [FromBody] TenantApplicationParams parameters)
        {
            // First create the application instance.
            // This won't actually create the services yet.
            ApplicationDescription application = new ApplicationDescription(
                new Uri($"{Names.TenantApplicationNamePrefix}/{tenantName}"),
                Names.TenantApplicationTypeName,
                parameters.Version);

            await this.fabricClient.ApplicationManager.CreateApplicationAsync(application, this.operationTimeout, this.appLifetime.ApplicationStopping);

            // Now create the data service in the new application instance.
            ServiceUriBuilder dataServiceNameUriBuilder = new ServiceUriBuilder(application.ApplicationName.ToString(), Names.TenantDataServiceName);
            StatefulServiceDescription dataServiceDescription = new StatefulServiceDescription()
            {
                ApplicationName = application.ApplicationName,
                HasPersistedState = true,
                MinReplicaSetSize = 3,
                TargetReplicaSetSize = 3,
                PartitionSchemeDescription = new UniformInt64RangePartitionSchemeDescription(parameters.DataPartitionCount, Int64.MinValue, Int64.MaxValue),
                ServiceName = dataServiceNameUriBuilder.Build(),
                ServiceTypeName = Names.TenantDataServiceTypeName
            };

            await this.fabricClient.ServiceManager.CreateServiceAsync(dataServiceDescription, this.operationTimeout, this.appLifetime.ApplicationStopping);

            // And finally, create the web service in the new application instance.
            ServiceUriBuilder webServiceNameUriBuilder = new ServiceUriBuilder(application.ApplicationName.ToString(), Names.TenantWebServiceName);
            StatelessServiceDescription webServiceDescription = new StatelessServiceDescription()
            {
                ApplicationName = application.ApplicationName,
                InstanceCount = parameters.WebInstanceCount,
                PartitionSchemeDescription = new SingletonPartitionSchemeDescription(),
                ServiceName = webServiceNameUriBuilder.Build(),
                ServiceTypeName = Names.TenantWebServiceTypeName
            };

            await this.fabricClient.ServiceManager.CreateServiceAsync(webServiceDescription, this.operationTimeout, this.appLifetime.ApplicationStopping);


            return this.Ok();
        }

        [HttpDelete]
        [Route("{tenantName}")]
        public async Task<IActionResult> Delete(string tenantName)
        {
            try
            {
                await this.fabricClient.ApplicationManager.DeleteApplicationAsync(
                    new DeleteApplicationDescription(new Uri($"{Names.TenantApplicationNamePrefix}/{tenantName}")),
                    this.operationTimeout,
                    this.appLifetime.ApplicationStopping);
            }
            catch (FabricElementNotFoundException)
            {
                // service doesn't exist; nothing to delete
            }

            return this.Ok();
        }
    }
}