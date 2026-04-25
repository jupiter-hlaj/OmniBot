using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;
using System.Text.Json;
using SottoTeamsBot.Bot;
using SottoTeamsBot.Calls;
using SottoTeamsBot.Models;

namespace SottoTeamsBot.Aws;

public sealed class AwsUploader
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonSQS _sqs;
    private readonly string _bucket;
    private readonly string _sqsUrl;

    public AwsUploader(IAmazonS3 s3, IAmazonSQS sqs, IOptions<BotOptions> options)
    {
        _s3 = s3;
        _sqs = sqs;
        _bucket = options.Value.S3Bucket;
        _sqsUrl = options.Value.SqsUrl;
    }

    public async Task UploadAsync(Stream stream, string s3Key, string contentType)
    {
        var initResponse = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = s3Key,
            ContentType = contentType
        });

        var uploadId = initResponse.UploadId;
        var partETags = new List<PartETag>();

        try
        {
            const int partSize = 5 * 1024 * 1024;
            var buffer = new byte[partSize];
            int partNumber = 1;
            int bytesRead;

            while ((bytesRead = await ReadExactAsync(stream, buffer, partSize)) > 0)
            {
                using var partStream = new MemoryStream(buffer, 0, bytesRead, writable: false);
                var partResponse = await _s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket,
                    Key = s3Key,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    InputStream = partStream,
                    PartSize = bytesRead
                });
                partETags.Add(new PartETag(partNumber, partResponse.ETag));
                partNumber++;
            }

            if (partETags.Count == 0)
                throw new InvalidOperationException("Cannot upload an empty audio stream.");

            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = s3Key,
                UploadId = uploadId,
                PartETags = partETags
            });
        }
        catch
        {
            await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = s3Key,
                UploadId = uploadId
            });
            throw;
        }
    }

    public async Task PublishSqsMessageAsync(CallSession session)
    {
        var evt = SqsCallEvent.FromSession(session);
        var json = JsonSerializer.Serialize(evt, SqsCallEvent.SerializerOptions);
        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _sqsUrl,
            MessageBody = json
        });
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
