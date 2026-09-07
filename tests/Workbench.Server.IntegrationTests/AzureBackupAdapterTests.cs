// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class AzureBackupAdapterTests
{
    [Theory]
    [InlineData(404, "BlobNotFound", true)]
    [InlineData(403, "AuthorizationPermissionMismatch", false)]
    [InlineData(404, "ContainerNotFound", false)]
    public async Task ReadsTheExactVersionAndOnlyClassifiesBlobAbsenceAsMissing(int status, string code, bool missing)
    {
        // GIVEN an Azure response from an enumerated immutable version, not a mutable current read.
        using var handler = new Handler((HttpStatusCode)status, code);
        using var http = new HttpClient(handler);
        var options = new BlobClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        var source = new AzureBackupSource(new BlobContainerClient(new Uri("https://source.blob.core.windows.net/workbench"), options), Guid.NewGuid());
        var version = new BackupVersion("published", "version-one", "\"etag-one\"", 3, Guid.NewGuid(), Guid.NewGuid());
        // WHEN downloading the named version with its original ETag.
        if (missing) await Assert.ThrowsAsync<FileNotFoundException>(() => source.OpenAsync(version, default));
        else await Assert.ThrowsAsync<RequestFailedException>(() => source.OpenAsync(version, default));
        // THEN the request is version-pinned and authorization/container failures cannot become accepted file loss.
        Assert.Contains("versionid=version-one", handler.RequestUri!.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("\"etag-one\"", handler.IfMatch);
    }

    private sealed class Handler(HttpStatusCode status, string code) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? IfMatch { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            IfMatch = request.Headers.IfMatch.ToString();
            var response = new HttpResponseMessage(status) { Content = new StringContent($"<Error><Code>{code}</Code><Message>Test response</Message></Error>", System.Text.Encoding.UTF8, "application/xml") };
            response.Headers.Add("x-ms-error-code", code);
            return Task.FromResult(response);
        }
    }
}
