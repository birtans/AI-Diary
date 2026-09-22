using AiDiary.Api.Configuration;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace AiDiary.Api.Storage;

public sealed class ObjectStorage
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonS3 _presignS3;
    private readonly DiaryOptions _options;

    public ObjectStorage(DiaryOptions options)
    {
        _options = options;
        var credentials = new BasicAWSCredentials(options.S3AccessKey, options.S3SecretKey);
        _s3 = new AmazonS3Client(credentials, BuildConfig(options, options.S3Endpoint));
        // Presigning is a local signature computation (no network call), so a
        // second client pointed at the public endpoint is enough to hand the
        // browser a URL it can actually reach — see S3PublicEndpointOrDefault.
        _presignS3 = options.S3PublicEndpointOrDefault == options.S3Endpoint
            ? _s3
            : new AmazonS3Client(credentials, BuildConfig(options, options.S3PublicEndpointOrDefault));
    }

    private static AmazonS3Config BuildConfig(DiaryOptions options, string endpoint) => new()
    {
        ServiceURL = endpoint,
        ForcePathStyle = options.S3ForcePathStyle,
        AuthenticationRegion = options.S3Region,
        // ServiceURL's own scheme isn't enough to drive presign URL protocol —
        // set it explicitly from the endpoint we were actually given.
        UseHttp = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase),
    };

    public string Bucket => _options.S3Bucket;

    /// <summary>
    /// Best-effort bucket creation at startup — convenient in dev (MinIO), a
    /// harmless no-op against an existing B2 bucket.
    /// </summary>
    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        try
        {
            var exists = await _s3.ListBucketsAsync(ct);
            if (exists.Buckets.All(b => b.BucketName != Bucket))
            {
                await _s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, ct);
            }
        }
        catch
        {
            // Bucket management may be restricted; the app will surface errors
            // on first actual use if the bucket is missing.
        }
    }

    public static string NoteObjectKey(Guid userId, Guid noteId, int version) =>
        $"u/{userId}/n/{noteId}/v{version}.md.enc";

    public async Task PutAsync(string key, byte[] data, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(data, writable: false);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
        }, ct);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct = default)
    {
        using var response = await _s3.GetObjectAsync(Bucket, key, ct);
        using var stream = new MemoryStream();
        await response.ResponseStream.CopyToAsync(stream, ct);
        return stream.ToArray();
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await _s3.DeleteObjectAsync(Bucket, key, ct);

    public async Task DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var batch = keys.Select(k => new KeyVersion { Key = k }).ToList();
        if (batch.Count == 0)
        {
            return;
        }
        await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = Bucket,
            Objects = batch,
        }, ct);
    }

    /// <summary>Short-lived presigned GET URL; the browser fetches the ciphertext directly.</summary>
    public string PresignGet(string key)
    {
        var url = _presignS3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Expires = DateTime.UtcNow.AddSeconds(_options.S3PresignTtlSeconds),
            Verb = HttpVerb.GET,
        });
        // The SDK's presigner defaults to https regardless of UseHttp on the
        // client config. SigV4 signs the Host header, not the scheme, so
        // rewriting it to match the intended endpoint doesn't break the
        // signature — and is required for a plain-http endpoint like MinIO.
        var wantsHttp = Uri.TryCreate(_options.S3PublicEndpointOrDefault, UriKind.Absolute, out var endpointUri) &&
            endpointUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase);
        return wantsHttp ? url.Replace("https://", "http://", StringComparison.Ordinal) : url;
    }

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket,
                MaxKeys = 1,
            }, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
