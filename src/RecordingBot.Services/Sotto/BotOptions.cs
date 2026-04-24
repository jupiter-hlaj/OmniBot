namespace SottoTeamsBot.Bot;

public sealed class BotOptions
{
    public string AadAppId { get; set; } = string.Empty;
    public string AadAppSecret { get; set; } = string.Empty;
    public string TenantId { get; set; } = "common";
    public string NotificationUrl { get; set; } = string.Empty;
    public string BotName { get; set; } = "SottoTeamsBot";
    public int MediaInternalPort { get; set; } = 8445;
    public int CallSignalingPort { get; set; } = 9442;
    public string ServiceFqdn { get; set; } = string.Empty;

    // AWS fields come from environment variables, not from appsettings.json.
    public string S3Bucket => Environment.GetEnvironmentVariable("SOTTO_S3_BUCKET") ?? string.Empty;
    public string SqsUrl => Environment.GetEnvironmentVariable("SOTTO_SQS_URL") ?? string.Empty;
    public string DynamoTenantsTable => Environment.GetEnvironmentVariable("SOTTO_DYNAMO_TENANTS_TABLE") ?? string.Empty;
    public string DynamoAgentsTable => Environment.GetEnvironmentVariable("SOTTO_DYNAMO_AGENTS_TABLE") ?? string.Empty;
    public string AwsRegion => Environment.GetEnvironmentVariable("SOTTO_AWS_REGION") ?? "us-east-1";
}
