using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using MhLabs.Logging;
using Microsoft.AspNetCore.Hosting;
using Newtonsoft.Json;
using Serilog;

namespace MhLabs.APIGatewayLambdaProxy
{
    public abstract class LambdaEntryPointBase : APIGatewayProxyFunction
    {
        protected const string CorrelationIdHeader = "mh-correlation-id";
        private static bool Warm { get; set; }
        private static string _correlationId;

        protected abstract IWebHostBuilder Initialize(IWebHostBuilder builder);

        protected virtual void UseCorrelationId(string correlationId) { }
        protected override void Init(IWebHostBuilder builder)
        {
            Initialize(builder)
                .UseSerilog((hostingContext, loggerConfiguration) =>
                {
                    loggerConfiguration
                    .Enrich.With<MemberIdEnrichment>()
                    .Enrich.With<ExceptionEnricher>()
                    .Enrich.With<CorrelationIdEnrichment>()
                    .Enrich.With<XRayEnrichment>()
                    .WriteTo.Console(outputTemplate: "[{Level:u3}] [{Properties:j}] {Message:lj}{NewLine}{Exception}");
                })
                .UseApiGateway();
        }

        public override async Task<APIGatewayProxyResponse> FunctionHandlerAsync(Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest request, Amazon.Lambda.Core.ILambdaContext lambdaContext)
        {
            if (string.IsNullOrEmpty(request.HttpMethod))
            {
                if (Warm)
                {
                    lambdaContext.Logger.Log("ping");
                    return new APIGatewayProxyResponse();
                }

                request.HttpMethod = "GET";
                request.Path = "/ping";
                request.Headers = new Dictionary<string, string> { { "Host", "localhost" } };
                request.RequestContext = new APIGatewayProxyRequest.ProxyRequestContext();
                lambdaContext.Logger.LogLine("Keep-alive invokation");
            }
            else
            {
                SetCorrelationId(request);
                UseCorrelationId(_correlationId);
                Console.WriteLine(JsonConvert.SerializeObject(request));
                LogValueResolver.Register<MemberIdEnrichment>(() => request.RequestContext.Authorizer.Claims["custom:memberid"]);
                LogValueResolver.Register<CorrelationIdEnrichment>(() => _correlationId);
            }
            Warm = true;
            return await base.FunctionHandlerAsync(request, lambdaContext);
        }

        private static void SetCorrelationId(APIGatewayProxyRequest request)
        {
            if (request.Headers.ContainsKey(CorrelationIdHeader) && !string.IsNullOrEmpty(request.Headers[CorrelationIdHeader]))
            {
                _correlationId = request.Headers[CorrelationIdHeader];
            }
            else
            {
                _correlationId = Guid.NewGuid().ToString();
            }
        }
    }
}