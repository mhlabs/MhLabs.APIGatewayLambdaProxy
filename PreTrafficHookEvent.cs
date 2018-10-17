namespace MhLabs.APIGatewayLambdaProxy
{
    public class PreTrafficHookEvent
    {
        public string DeploymentId { get; set; }
        public string LifecycleEventHookExecutionId { get; set; }
    }
}