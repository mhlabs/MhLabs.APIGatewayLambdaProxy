using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using Amazon.Lambda.Model;
using MhLabs.SerilogExtensions;
using Microsoft.AspNetCore.Hosting;
using Newtonsoft.Json;
using Serilog;

namespace MhLabs.APIGatewayLambdaProxy
{
    public abstract class LambdaEntryPointBase : APIGatewayProxyFunction
    {
        protected const string CorrelationIdHeader = "mh-correlation-id";
        private const string Concurrency = "__CONCURRENCY__";
        private const string KeepAliveInvocation = "__KEEP_ALIVE_INVOCATION__";
        private readonly IAmazonLambda _lambda;

        private static bool Warm { get; set; }
        private static string _correlationId;

        public LambdaEntryPointBase() : base()
        {
            _lambda = new AmazonLambdaClient();
        }

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
                    .WriteTo.Console(outputTemplate: Constants.OutputTemplateFormat);
                })
                .UseApiGateway();
        }

        public override async Task<APIGatewayProxyResponse> FunctionHandlerAsync(APIGatewayProxyRequest request, Amazon.Lambda.Core.ILambdaContext lambdaContext)
        {
            if (string.IsNullOrEmpty(request.HttpMethod)) // For backward compability
            {
                if (request.Headers != null)
                {
                    if (request.Headers.ContainsKey(Concurrency))
                    {
                        var concurrency = int.Parse(request.Headers[Concurrency]);
                        var tasks = new List<Task<InvokeResponse>>();

                        for (var i = 0; i < concurrency - 1; i++)
                        {
                            tasks.Add(_lambda.InvokeAsync(CreateInvokeRequest()));
                        }

                        await Task.WhenAll(tasks);
                    }
                    if (request.Headers.ContainsKey(KeepAliveInvocation)) 
                    {
                        Thread.Sleep(75); // To mitigate lambda reuse
                    }
                }

                lambdaContext.Logger.Log(JsonConvert.SerializeObject(request));
                
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
                LogValueResolver.Register<RequestBodyEnrichment>(() => request.Body);
            }

            Warm = true;

            return await base.FunctionHandlerAsync(request, lambdaContext);
        }

        private static InvokeRequest CreateInvokeRequest()
        {
            return new InvokeRequest
            {
                FunctionName = System.Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"),
                Payload = "{\"Headers\":{\"" + KeepAliveInvocation + "\": \"1\"}}"
            };
        }

        private static void SetCorrelationId(APIGatewayProxyRequest request)
        {
            _correlationId = HasCorrelationIdHeader(request)
                ? request.Headers[CorrelationIdHeader]
                : Guid.NewGuid().ToString();
        }

        private static bool HasCorrelationIdHeader(APIGatewayProxyRequest request)
        {
            return request.Headers.ContainsKey(CorrelationIdHeader) && !string.IsNullOrEmpty(request.Headers[CorrelationIdHeader]);
        }
    }
}