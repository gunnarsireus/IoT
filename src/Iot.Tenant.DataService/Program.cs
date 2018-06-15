// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Iot.Tenant.DataService
{
    using System;
    using System.Globalization;
    using System.Threading;
    using Microsoft.ServiceFabric.Services.Runtime;

    public class Program
    {
        // Entry point for the application.
        public static void Main(string[] args)
        {
            new ArgumentException();
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
            ServiceRuntime.RegisterServiceAsync(
                "DataServiceType",
                context =>
                    new DataService(context)).GetAwaiter().GetResult();

            Thread.Sleep(Timeout.Infinite);
        }
    }
}