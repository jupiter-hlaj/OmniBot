using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SQS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Common.Telemetry;
using RecordingBot.Services.Bot;
using RecordingBot.Services.Contract;
using RecordingBot.Services.Util;
using SottoTeamsBot.Audio;
using SottoTeamsBot.Aws;
using SottoTeamsBot.Bot;
using System;

namespace RecordingBot.Services.ServiceSetup
{
    public class ServiceHost : IServiceHost
    {
        public IServiceCollection Services { get; private set; }
        public IServiceProvider ServiceProvider { get; private set; }

        public ServiceHost Configure(IServiceCollection services, IConfiguration configuration)
        {
            Services = services;
            Services.AddSingleton<IGraphLogger, GraphLogger>(sp =>
            {
                var logger = new GraphLogger("RecordingBot", redirectToTrace: false);
                logger.BindToILoggerFactory(sp.GetRequiredService<ILoggerFactory>());
                return logger;
            });
            Services.AddSingleton<IAzureSettings>(_ => _.GetRequiredService<AzureSettings>());
            Services.AddSingleton<IEventPublisher, EventGridPublisher>(_ => new EventGridPublisher(_.GetRequiredService<IOptions<AzureSettings>>().Value));
            Services.AddSingleton<IBotService, BotService>();

            // Sotto integration: AWS SDK clients + upload/resolve services.
            // AWS SDK picks up credentials from env vars (AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY)
            // and region from AWS_REGION. Set via K8s secret + Helm values.
            Services.Configure<BotOptions>(_ => { });
            Services.Configure<AudioFormatOptions>(configuration.GetSection("Sotto:AudioFormat"));
            Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
            Services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient());
            Services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
            Services.AddSingleton<AwsUploader>();
            Services.AddSingleton<DynamoResolver>();
            Services.AddSingleton<AudioEncoder>();

            return this;
        }

        public IServiceProvider Build()
        {
            ServiceProvider = Services.BuildServiceProvider();
            return ServiceProvider;
        }
    }
}
