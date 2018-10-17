using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Amazon.CodeDeploy;
using Amazon.CodeDeploy.Model;
using Amazon.Lambda;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using Amazon.Lambda.Core;
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
        private const string PreTrafficInvocation = "__PRE_TRAFFIC_INVOCATION__";
        private readonly IAmazonLambda _lambda;

        private readonly IAmazonCodeDeploy _codeDeploy;

        private static bool Warm { get; set; }
        private static string _correlationId;

        public LambdaEntryPointBase() : base()
        {
            _lambda = new AmazonLambdaClient();
            _codeDeploy = new AmazonCodeDeployClient();
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
                            tasks.Add(_lambda.InvokeAsync(CreateInvokeRequest(KeepAliveInvocation)));
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


        [LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]
        public async Task PreTrafficFunction(PreTrafficHookEvent input, Amazon.Lambda.Core.ILambdaContext lambdaContext)
        {
            var lambdaArn = System.Environment.GetEnvironmentVariable("LambdaArn");
            var request = new PutLifecycleEventHookExecutionStatusRequest
            {
                DeploymentId = input.DeploymentId,
                LifecycleEventHookExecutionId = input.LifecycleEventHookExecutionId,
                Status = "Succeeded"
            };
            try
            {
                await _lambda.InvokeAsync(CreateInvokeRequest(PreTrafficInvocation));
            }
            catch(Exception ex) 
            {
                lambdaContext.Logger.Log(ex.Message + " "  + ex.StackTrace);
                request.Status = "Failed";
            }
            finally
            {
                await _codeDeploy.PutLifecycleEventHookExecutionStatusAsync(request);
            }
        }

        private static InvokeRequest CreateInvokeRequest(string invocationType)
        {
            return new InvokeRequest
            {
                FunctionName = System.Environment.GetEnvironmentVariable("LambdaToInvoke") ?? System.Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"),
                Payload = "{\"Headers\":{\"" + invocationType + "\": \"1\"}}"
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