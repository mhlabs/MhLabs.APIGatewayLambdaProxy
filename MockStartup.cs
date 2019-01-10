using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MhLabs.APIGatewayLambdaProxy
{
    public class MockStartup
    {
        private readonly IHostingEnvironment _hostingEnvironment;

        public MockStartup(IHostingEnvironment env)
        {
        }

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
        }
    }
}
