using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon.CodeDeploy;
using Amazon.CodeDeploy.Model;
using Amazon.Lambda;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using Amazon.Lambda.Core;
using Amazon.Lambda.Model;
using MhLabs.AwsSignedHttpClient;
using MhLabs.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Newtonsoft.Json;

namespace MhLabs.APIGatewayLambdaProxy
{
    public abstract class LambdaEntryPointBase : APIGatewayProxyFunction
    {
        private const string Concurrency = "__CONCURRENCY__";
        private const string KeepAliveInvocation = "__KEEP_ALIVE_INVOCATION__";
        private const string PreTrafficInvocation = "__PRE_TRAFFIC_INVOCATION__";
        private IAmazonLambda _lambda;

        private IAmazonCodeDeploy _codeDeploy;
        private HttpClient _httpClient;

        private static bool Warm { get; set; }

        protected virtual int PreTrafficConcurrency { get; set; } = 1;
        protected LambdaEntryPointBase() : base(string.IsNullOrEmpty(
            System.Environment.GetEnvironmentVariable("PRE_HOOK_TRIGGER"))
            ? AspNetCoreStartupMode.Constructor
            : AspNetCoreStartupMode.FirstRequest)
        {
        }

        // For backward compatibility
        [Obsolete("Use Init()")]
        protected virtual IWebHostBuilder Initialize(IWebHostBuilder builder)
        {
            return builder;
        }
        protected virtual void UseCorrelationId(string correlationId) { }
        protected override void Init(IWebHostBuilder builder)
        {
            Initialize(builder);
        }

        protected override void PostCreateContext(Microsoft.AspNetCore.Hosting.Internal.HostingApplication.Context context, APIGatewayProxyRequest apiGatewayRequest, ILambdaContext lambdaContext)
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter((int)(lambdaContext.RemainingTime.TotalMilliseconds * 0.75));
            context.HttpContext.RequestAborted = cts.Token;
        }

        public override async Task<APIGatewayProxyResponse> FunctionHandlerAsync(APIGatewayProxyRequest request, ILambdaContext lambdaContext)
        {
            if (string.IsNullOrEmpty(request.HttpMethod)) // For backward compatibility
            {
                if (request.Headers != null)
                {
                    if (request.Headers.ContainsKey(Concurrency))
                    {
                        var concurrency = int.Parse(request.Headers[Concurrency]);
                        await KeepAlive(concurrency - 1, KeepAliveInvocation);
                    }
                    if (request.Headers.ContainsKey(KeepAliveInvocation))
                    {
                        Thread.Sleep(75); // To mitigate lambda reuse
                    }
                    if (!Warm && !request.Headers.Keys.Any(key => key == Concurrency || key == KeepAliveInvocation))
                    {
                        LambdaLogger.Log($"[Info] Customer affected by coldstart");
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
                lambdaContext.Logger.LogLine("Keep-alive invocation");
            }

            Warm = true;
            return await base.FunctionHandlerAsync(request, lambdaContext);
        }

        private async Task KeepAlive(int concurrency, string invocationType)
        {
            var tasks = new List<Task<InvokeResponse>>();
            LambdaLogger.Log("Concurrency " + concurrency);
            for (var i = 0; i < concurrency; i++)
            {
                LambdaLogger.Log("Invoke " + i);
                tasks.Add(_lambda.InvokeAsync(CreateInvokeRequest(invocationType)));
            }

            await Task.WhenAll(tasks);
        }

        private void InitHooks()
        {
            _lambda = _lambda ?? new AmazonLambdaClient();
            _codeDeploy = _codeDeploy ?? new AmazonCodeDeployClient();
            _httpClient = _httpClient ?? new HttpClient(new AwsSignedHttpMessageHandler { InnerHandler = new HttpClientHandler() }) { BaseAddress = Env.Get("ApiBaseUrl").ToUri() };
        }

        [LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]
        public async Task PreTrafficHook(CodeDeployEvent deployment)
        {
            var status = LifecycleEventStatus.Succeeded;
            InitHooks();
            try
            {
                Console.WriteLine("PreHook. Version " + Env.Get("AWS_LAMBDA_FUNCTION_VERSION"));
                var tests = File.ReadAllText("smoketests.json").To<List<SmokeTest>>();
                foreach (var test in tests)
                {
                    if (!test.NoProxy)
                    {
                        var splitPath = test.Path.Trim('/').Split('/');
                        test.Path = string.Join("/", splitPath.Skip(1));
                        Console.WriteLine("Path " + test.Path);
                    }
                    var apiRequest = new APIGatewayProxyRequest
                    {
                        Path = test.Path,
                        HttpMethod = test.Method,
                        Headers = new Dictionary<string, string> { { "Host", "localhost" }, { "__PRE_TRAFFIC_HOOK__", "true" } }
                    };
                    var lambdaRequest = new InvokeRequest
                    {
                        FunctionName = Env.Get("VersionToTest"),
                        Payload = apiRequest.ToJson()
                    };
                    var response = await _lambda.InvokeAsync(lambdaRequest);

                    using (var reader = new StreamReader(response.Payload))
                    {
                        var responseStr = reader.ReadToEnd();
                        Console.WriteLine("Response: " + responseStr);
                        var regex = new Regex(test.ResponsePattern);
                        if (!regex.IsMatch(responseStr))
                        {
                            status = LifecycleEventStatus.Failed; ;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                status = LifecycleEventStatus.Failed;
                Console.WriteLine(ex.Message + " " + ex.StackTrace);
            }

            var request = new PutLifecycleEventHookExecutionStatusRequest
            {
                DeploymentId = deployment.DeploymentId,
                LifecycleEventHookExecutionId = deployment.LifecycleEventHookExecutionId,
                Status = status
            };

            await _codeDeploy.PutLifecycleEventHookExecutionStatusAsync(request);
        }

        [LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]
        public async Task PostTrafficHook(CodeDeployEvent deployment)
        {
            var status = LifecycleEventStatus.Succeeded;

            try
            {
                InitHooks();
                Console.WriteLine("PostHook. Version " + Env.Get("AWS_LAMBDA_FUNCTION_VERSION"));
                var tests = File.ReadAllText("smoketests.json").To<List<SmokeTest>>();
                foreach (var test in tests)
                {
                    var httpRequest = new HttpRequestMessage(new HttpMethod(test.Method), test.Path);
                    if (!string.IsNullOrEmpty(test.Body))
                    {
                        httpRequest.Content = new StringContent(test.Body);
                    }
                    var response = await _httpClient.SendAsync(httpRequest);
                    var responseStr = await response.Content.ReadAsStringAsync();
                    Console.WriteLine("Response: " + responseStr);
                    var regex = new Regex(test.ResponsePattern);
                    if (!regex.IsMatch(responseStr))
                    {
                        status = LifecycleEventStatus.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                status = LifecycleEventStatus.Failed;
                Console.WriteLine(ex.Message + " " + ex.StackTrace);
            }
            var request = new PutLifecycleEventHookExecutionStatusRequest
            {
                DeploymentId = deployment.DeploymentId,
                LifecycleEventHookExecutionId = deployment.LifecycleEventHookExecutionId,
                Status = status
            };

            await _codeDeploy.PutLifecycleEventHookExecutionStatusAsync(request);
        }

        private static InvokeRequest CreateInvokeRequest(string invocationType)
        {
            return new InvokeRequest
            {
                FunctionName = System.Environment.GetEnvironmentVariable("LambdaToInvoke") ?? System.Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"),
                Payload = "{\"Headers\":{\"" + invocationType + "\": \"1\"}}"
            };
        }
    }

}