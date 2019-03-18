namespace MhLabs.APIGatewayLambdaProxy
{
    public class SmokeTest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public string ResponsePattern { get; set; }
        public string Body { get; set; }
        public bool NoProxy { get; set; }
    }

}