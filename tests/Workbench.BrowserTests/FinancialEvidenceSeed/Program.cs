// Copyright (c) 2026 The White Stag Collection.
// Test-only fixture for the isolated browser database. No application endpoint can invoke it.
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

if (args.Length != 5 || args[0] is not ("add" or "expire") ||
    !Guid.TryParse(args[2], out var orderId) || !Guid.TryParse(args[3], out var documentId) ||
    !Guid.TryParse(args[4], out var actorId))
    throw new InvalidOperationException("Expected mode, connection file, order ID, document ID and actor ID.");

var run = Environment.GetEnvironmentVariable("WORKBENCH_BROWSER_RUN");
if (run is null || !Regex.IsMatch(run, "^browser-[a-f0-9]{12}$", RegexOptions.CultureInvariant))
    throw new InvalidOperationException("An isolated browser run is required.");
var expectedPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), run, "setup.connection"));
if (!string.Equals(Path.GetFullPath(args[1]), expectedPath, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Only this browser run's protected connection file is accepted.");
if (File.GetAttributes(Path.GetDirectoryName(expectedPath)!).HasFlag(FileAttributes.ReparsePoint) ||
    File.GetAttributes(expectedPath).HasFlag(FileAttributes.ReparsePoint))
    throw new InvalidOperationException("Browser fixture paths cannot be reparse points.");
var connectionString = (await File.ReadAllTextAsync(expectedPath)).Trim();
var builder = new SqlConnectionStringBuilder(connectionString);
var expectedDatabase = "workbench_" + run.Replace('-', '_');
if (!string.Equals(builder.InitialCatalog, expectedDatabase, StringComparison.OrdinalIgnoreCase) ||
    !builder.DataSource.StartsWith("127.0.0.1,", StringComparison.Ordinal))
    throw new InvalidOperationException("Fixture connection must target this loopback browser database.");

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
await using (var check = new SqlCommand("SELECT DB_NAME()", connection))
    if (!string.Equals((string?)await check.ExecuteScalarAsync(), expectedDatabase, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Connected database does not match this browser run.");
await using (var marker = new SqlCommand("SELECT COUNT(*) FROM [Identity].Users u JOIN Tenancy.Tenants t ON t.Id=u.TenantId WHERE t.Name=N'Browser Tenant' AND u.Email=N'browser-live-0@example.test'", connection))
    if ((int)(await marker.ExecuteScalarAsync())! != 1)
        throw new InvalidOperationException("Browser tenant and account markers are absent.");

const string add = """
    SET XACT_ABORT ON;
    BEGIN TRAN;
    DECLARE @Tenant uniqueidentifier,@Supplier uniqueidentifier,@Attachment uniqueidentifier,@Revision uniqueidentifier,
      @Label nvarchar(200),@Media nvarchar(100),@Extension nvarchar(10),@Length bigint,@Digest char(64),@Config uniqueidentifier,
      @Now datetimeoffset=SYSUTCDATETIME(),@Set uniqueidentifier=NEWID();
    SELECT @Tenant=o.TenantId,@Supplier=o.SupplierId,@Attachment=d.AttachmentId,@Revision=d.RevisionId,
      @Label=d.Label,@Media=d.MediaType,@Extension=d.Extension,@Length=d.Length,@Digest=d.Sha256
      FROM Purchasing.DraftOrders o JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=o.TenantId AND d.OrderId=o.Id
      JOIN Storage.Revisions r ON r.TenantId=d.TenantId AND r.AttachmentId=d.AttachmentId AND r.Id=d.RevisionId AND r.State=1
      WHERE o.Id=@OrderId AND d.Id=@DocumentId AND o.State='Ordered' AND d.RemovedAtUtc IS NULL;
    SELECT @Config=Version FROM Accounting.Configurations WHERE TenantId=@Tenant;
    IF @Tenant IS NULL OR @Supplier IS NULL OR @Config IS NULL OR
      NOT EXISTS(SELECT 1 FROM [Identity].Users WHERE TenantId=@Tenant AND Id=@ActorId)
      THROW 51000,'Browser evidence fixture requires a current ordered uploaded document and actor.',1;
    IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@Tenant AND DocumentId=@DocumentId)
      THROW 51009,'Browser evidence fixture already exists.',1;
    INSERT Accounting.FinancialEvidenceSets(TenantId,Id,OwnerKind,OwnerId,OwnerRevisionId,PurchaseOrderId,SupplierId,ActorId,
      PostingDate,RecordedAtUtc,SourceSnapshotSha256,MissingEvidenceReason,LegacyEvidence,MutationPermission,InheritedEvidenceSetId)
      VALUES(@Tenant,@Set,'SupplierBill',NEWID(),NEWID(),@OrderId,@Supplier,@ActorId,
      CONVERT(date,@Now),@Now,HASHBYTES('SHA2_256',CONVERT(varbinary(max),N'Browser synthetic financial evidence')),NULL,0,NULL,NULL);
    INSERT Accounting.FinancialEvidenceLinks(TenantId,Id,EvidenceSetId,DocumentId,AttachmentId,RevisionId,Sha256,Length,Label,MediaType,Extension,
      ConfigurationVersion,RetentionYears,RetentionRationale,AnchorAtUtc,MinimumRetentionDeadlineUtc,RecordedAtUtc)
      VALUES(@Tenant,NEWID(),@Set,@DocumentId,@Attachment,@Revision,@Digest,@Length,@Label,@Media,@Extension,
      @Config,1,N'Isolated browser retention fixture',@Now,DATEADD(year,1,@Now),@Now);
    UPDATE Storage.Attachments SET Held=1 WHERE TenantId=@Tenant AND Id=@Attachment;
    INSERT Storage.FinancialEvidenceAttachmentStates(TenantId,AttachmentId) VALUES(@Tenant,@Attachment);
    COMMIT;
    """;
const string expire = """
    SET XACT_ABORT ON;
    BEGIN TRAN;
    IF NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks l JOIN Purchasing.PurchaseOrderDocuments d
      ON d.TenantId=l.TenantId AND d.Id=l.DocumentId
      WHERE d.OrderId=@OrderId AND d.Id=@DocumentId AND l.DocumentId=@DocumentId)
      THROW 51000,'Browser evidence fixture is absent.',1;
    DISABLE TRIGGER Accounting.PreserveFinancialEvidenceLinks ON Accounting.FinancialEvidenceLinks;
    UPDATE l SET AnchorAtUtc='2020-01-01',MinimumRetentionDeadlineUtc='2021-01-01'
      FROM Accounting.FinancialEvidenceLinks l JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=l.TenantId AND d.Id=l.DocumentId
      WHERE d.OrderId=@OrderId AND d.Id=@DocumentId;
    ENABLE TRIGGER Accounting.PreserveFinancialEvidenceLinks ON Accounting.FinancialEvidenceLinks;
    COMMIT;
    """;
await using var command = new SqlCommand(args[0] == "add" ? add : expire, connection) { CommandTimeout = 30 };
command.Parameters.AddWithValue("@OrderId", orderId);
command.Parameters.AddWithValue("@DocumentId", documentId);
command.Parameters.AddWithValue("@ActorId", actorId);
await command.ExecuteNonQueryAsync();
Console.WriteLine("Browser evidence fixture updated.");
