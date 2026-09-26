// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.Tenancy;

namespace Workbench.Server.IntegrationTests.Infrastructure;

// Installs its adapter only in AuthTestApplication's disposable SQL clone.
internal sealed class JournalTestContext : IAsyncDisposable
{
    public static readonly Guid TenantId = AuthTestApplication.TenantId;
    public static readonly Guid ActorId = AuthTestApplication.AdminUserId;
    public static readonly Guid OtherTenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public AuthTestApplication Application { get; }
    public SqlConnection Connection { get; }
    public Guid SessionId { get; }
    public byte[] ProofKey { get; }
    public Guid ConfigurationVersion { get; private set; }
    public Guid DebitAccountId { get; private set; }
    public Guid CreditAccountId { get; private set; }
    public Guid DebitAccountVersion { get; private set; }
    public Guid CreditAccountVersion { get; private set; }

    private JournalTestContext(AuthTestApplication application, SqlConnection connection, Guid sessionId, byte[] proofKey)
    {
        Application = application;
        Connection = connection;
        SessionId = sessionId;
        ProofKey = proofKey;
    }

    public static async Task<JournalTestContext> OpenAsync(SqlServerFixture fixture, string? priorMigration = null)
    {
        var application = await AuthTestApplication.CreateAsync(fixture, priorMigration: priorMigration);
        try
        {
            await using var admin = new SqlConnection(application.AdminConnectionString);
            await admin.OpenAsync();
            await using (var install = new SqlCommand(AdapterSql, admin))
                await install.ExecuteNonQueryAsync();
            var sessionId = Guid.NewGuid();
            await using (var grant = new SqlCommand("""
                INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
                    SELECT @tenant,@actor,RoleId FROM Administration.AccountingRoles
                    WHERE TenantId=@tenant AND Kind='Administrator';
                INSERT [Identity].[Sessions](Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
                    SELECT @session,@tenant,@actor,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),
                        DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME())
                    FROM [Identity].[Users] WHERE Id=@actor;
                """, admin))
            {
                grant.Parameters.AddWithValue("@tenant", TenantId);
                grant.Parameters.AddWithValue("@actor", ActorId);
                grant.Parameters.AddWithValue("@session", sessionId);
                await grant.ExecuteNonQueryAsync();
            }
            var proofKey = await ReadProofKeyAsync(admin);
            var connection = await OpenRestrictedConnectionAsync(application.WebConnectionString, proofKey, TenantId);
            return new JournalTestContext(application, connection, sessionId, proofKey);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public Task<SqlConnection> OpenSiblingAsync() => OpenRestrictedConnectionAsync(Application.WebConnectionString, ProofKey, TenantId);

    public async Task<SqlConnection> OpenOtherTenantAsync()
        => await OpenRestrictedConnectionAsync(Application.WebConnectionString, ProofKey, OtherTenantId);

    private static async Task<SqlConnection> OpenRestrictedConnectionAsync(string connectionString, byte[] proofKey, Guid tenantId)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            await new TenantContextProof(proofKey).ApplyAsync(connection, tenantId, CancellationToken.None);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<byte[]> ReadProofKeyAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT ProofKey FROM Security.TenantContextKeys WHERE Id=1", connection);
        return (byte[])(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing tenant proof key."));
    }

    public async Task ConfigureAsync(int scale = 2, string currency = "USD", string startDate = "2026-01-01", int fiscalStartMonth = 1)
    {
        var payload = JsonSerializer.Serialize(new
        {
            policies = new
            {
                country = "US",
                region = "CA",
                currency,
                scale,
                fiscalStartMonth,
                startApproach = "OpeningBalances",
                plannedStartDate = startDate
            },
            mappings = Array.Empty<object>(),
            coverage = Array.Empty<object>()
        });
        var result = await SaveAsync(Guid.NewGuid(), "Configure", payload, expectedVersion: ConfigurationVersion);
        ConfigurationVersion = result.Version;
    }

    public async Task CreateGeneralAccountsAsync()
    {
        var result = await SaveAsync(Guid.NewGuid(), "CreateAccounts", """
            [{"code":"1910","name":"Synthetic debit","type":"Asset","purpose":"General"},
             {"code":"2910","name":"Synthetic credit","type":"Liability","purpose":"General"}]
            """);
        var ids = JsonSerializer.Deserialize<Guid[]>(result.Ids)!;
        DebitAccountId = ids[0];
        CreditAccountId = ids[1];
        DebitAccountVersion = CreditAccountVersion = result.Version;
    }

    public async Task<(Guid Version, string Ids)> SaveAsync(Guid requestId, string operation, string payload, Guid? id = null, Guid? expectedVersion = null, SqlConnection? connection = null)
    {
        await using var command = new SqlCommand("EXEC [Accounting].[Save] @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Operation=@operation,@Id=@id,@ExpectedVersion=@version,@Payload=@payload", connection ?? Connection);
        command.Parameters.AddWithValue("@actor", ActorId);
        command.Parameters.AddWithValue("@session", SessionId);
        command.Parameters.AddWithValue("@request", requestId);
        command.Parameters.AddWithValue("@operation", operation);
        command.Parameters.AddWithValue("@id", (object?)id ?? DBNull.Value);
        command.Parameters.AddWithValue("@version", (object?)expectedVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("@payload", payload);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Accounting.Save returned no row.");
        return (reader.GetGuid(0), reader.GetString(1));
    }

    public async Task<SyntheticSource> CreateSourceAsync(string amount = "12.34", string currency = "USD", Guid? tenantId = null)
    {
        var id = Guid.NewGuid();
        var revision = Guid.NewGuid();
        await using var admin = new SqlConnection(Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT Accounting.SyntheticSources(TenantId,Id,Revision,Amount,Currency,DebitAccountId,CreditAccountId)
            VALUES(@tenant,@id,@revision,@amount,@currency,@debit,@credit);
            """, admin);
        command.Parameters.AddWithValue("@tenant", tenantId ?? TenantId);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@revision", revision);
        command.Parameters.Add(new SqlParameter("@amount", SqlDbType.Decimal) { Precision = 28, Scale = 4, Value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture) });
        command.Parameters.AddWithValue("@currency", currency);
        command.Parameters.AddWithValue("@debit", DebitAccountId);
        command.Parameters.AddWithValue("@credit", CreditAccountId);
        await command.ExecuteNonQueryAsync();
        return new SyntheticSource(id, revision, amount, currency);
    }

    public async Task<PostingResult> PostAsync(SyntheticSource source, Guid? requestId = null,
        SqlConnection? connection = null, Guid? actorId = null, Guid? sessionId = null,
        Guid? expectedSourceRevision = null, Guid? expectedConfigurationVersion = null,
        Guid? expectedDebitVersion = null, Guid? expectedCreditVersion = null,
        DateTime? postingDate = null, string? reference = null, int failpoint = 0)
    {
        await using var command = NewPostCommand(source, requestId ?? Guid.NewGuid(), connection ?? Connection,
            actorId ?? ActorId, sessionId ?? SessionId, expectedSourceRevision ?? source.Revision,
            expectedConfigurationVersion ?? ConfigurationVersion, expectedDebitVersion ?? DebitAccountVersion,
            expectedCreditVersion ?? CreditAccountVersion, postingDate ?? new DateTime(2026, 2, 1), reference, failpoint);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Synthetic journal returned no row.");
        return new PostingResult(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetDateTimeOffset(3));
    }

    public static SqlCommand NewPostCommand(SyntheticSource source, Guid requestId, SqlConnection connection,
        Guid actorId, Guid sessionId, Guid expectedSourceRevision, Guid expectedConfigurationVersion,
        Guid expectedDebitVersion, Guid expectedCreditVersion, DateTime postingDate, string? reference = null,
        int failpoint = 0)
    {
        var command = new SqlCommand("""
            EXEC Accounting.PostSyntheticJournal @ActorId=@actor,@SessionId=@session,@RequestId=@request,
                @SourceId=@source,@ExpectedSourceRevision=@sourceVersion,
                @ExpectedConfigurationVersion=@configVersion,
                @ExpectedDebitAccountVersion=@debitVersion,@ExpectedCreditAccountVersion=@creditVersion,
                @DocumentDate=@date,@EffectiveDate=@date,@PostingDate=@date,@Reference=@reference,@Failpoint=@failpoint;
            """, connection) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("@actor", actorId);
        command.Parameters.AddWithValue("@session", sessionId);
        command.Parameters.AddWithValue("@request", requestId);
        command.Parameters.AddWithValue("@source", source.Id);
        command.Parameters.AddWithValue("@sourceVersion", expectedSourceRevision);
        command.Parameters.AddWithValue("@configVersion", expectedConfigurationVersion);
        command.Parameters.AddWithValue("@debitVersion", expectedDebitVersion);
        command.Parameters.AddWithValue("@creditVersion", expectedCreditVersion);
        command.Parameters.Add(new SqlParameter("@date", SqlDbType.Date) { Value = postingDate });
        command.Parameters.AddWithValue("@reference", (object?)reference ?? DBNull.Value);
        command.Parameters.AddWithValue("@failpoint", failpoint);
        return command;
    }

    public async Task<int> CountAsync(string table, SqlConnection? connection = null)
    {
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM Accounting.[{table}]", connection ?? Connection);
        return (int)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Count returned null."));
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await Application.DisposeAsync();
    }

    public sealed record SyntheticSource(Guid Id, Guid Revision, string Amount, string Currency);
    public sealed record PostingResult(Guid SourceEventId, Guid JournalId, long Sequence, DateTimeOffset RecordedAtUtc);

    private const string AdapterSql = """
        CREATE TABLE Accounting.SyntheticSources(
            TenantId uniqueidentifier NOT NULL,
            Id uniqueidentifier NOT NULL,
            Revision uniqueidentifier NOT NULL,
            Amount decimal(28,4) NOT NULL,
            Currency nvarchar(3) NOT NULL,
            DebitAccountId uniqueidentifier NOT NULL,
            CreditAccountId uniqueidentifier NOT NULL,
            FrozenAtUtc datetimeoffset NULL,
            CONSTRAINT PK_SyntheticSources PRIMARY KEY(TenantId,Id),
            CONSTRAINT CK_SyntheticSources_Positive CHECK(Amount>0)
        );
        EXEC(N'ALTER SECURITY POLICY Security.TenantIsolationPolicy
            ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Accounting.SyntheticSources,
            ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Accounting.SyntheticSources AFTER INSERT,
            ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Accounting.SyntheticSources AFTER UPDATE');
        EXEC(N'GRANT SELECT ON Accounting.SyntheticSources TO workbench_web;
            DENY INSERT,UPDATE,DELETE ON Accounting.SyntheticSources TO workbench_web;');
        EXEC(N'
        CREATE PROCEDURE Accounting.PostSyntheticJournal
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
          @SourceId uniqueidentifier,@ExpectedSourceRevision uniqueidentifier,
          @ExpectedConfigurationVersion uniqueidentifier,
          @ExpectedDebitAccountVersion uniqueidentifier,@ExpectedCreditAccountVersion uniqueidentifier,
          @DocumentDate date,@EffectiveDate date,@PostingDate date,@Reference nvarchar(200)=NULL,
          @Failpoint int=0
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N''AccountingConfigurationManage'';
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N''TenantId''));
            IF @TenantId IS NULL OR @RequestId IS NULL OR @SourceId IS NULL OR @ExpectedSourceRevision IS NULL
              OR @ExpectedConfigurationVersion IS NULL OR @ExpectedDebitAccountVersion IS NULL OR @ExpectedCreditAccountVersion IS NULL
              OR @Failpoint NOT IN (0,1,2,3) THROW 51000,''Invalid synthetic source command.'',1;
            DECLARE @LockResult int,@Resource nvarchar(255)=N''Accounting:''+CONVERT(nvarchar(36),@TenantId);
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
            IF @LockResult<0
            BEGIN
              DECLARE @LockFailure nvarchar(2048)=CONCAT(N''Synthetic accounting lock failed: result='',@LockResult,N'', session='',@@SPID);
              THROW 51009,@LockFailure,1;
            END;
            -- Authorization and replay must precede all mutable source, configuration and account checks.
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N''AccountingConfigurationManage'';
            DECLARE @CanonicalInput nvarchar(max)=(SELECT @SourceId sourceId,@ExpectedSourceRevision sourceRevision,
                @ExpectedConfigurationVersion configurationVersion,@ExpectedDebitAccountVersion debitAccountVersion,
                @ExpectedCreditAccountVersion creditAccountVersion,@DocumentDate documentDate,@EffectiveDate effectiveDate,
                @PostingDate postingDate,@Reference reference FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
            DECLARE @Replay bit=CASE WHEN EXISTS(SELECT 1 FROM Accounting.PostingReceipts
                WHERE TenantId=@TenantId AND RequestId=@RequestId) THEN 1 ELSE 0 END;
            DECLARE @Result TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
            IF @Replay=0
            BEGIN
              IF @Failpoint=1 THROW 51000,''Injected before source validation.'',1;
              DECLARE @Amount decimal(28,4),@Currency nvarchar(3),@DebitId uniqueidentifier,@CreditId uniqueidentifier;
              SELECT @Amount=Amount,@Currency=Currency,@DebitId=DebitAccountId,@CreditId=CreditAccountId
                FROM Accounting.SyntheticSources WITH(UPDLOCK,HOLDLOCK)
                WHERE TenantId=@TenantId AND Id=@SourceId AND Revision=@ExpectedSourceRevision AND FrozenAtUtc IS NULL;
              IF @Amount IS NULL
              BEGIN
                DECLARE @ExistingSourceEventId uniqueidentifier;
                SELECT @ExistingSourceEventId=Id FROM Accounting.SourceEvents
                  WHERE TenantId=@TenantId AND SourceKind=N''Synthetic'' AND SourceId=@SourceId
                    AND SourceRevision=@ExpectedSourceRevision AND EventKind=N''Posted'';
                IF @ExistingSourceEventId IS NOT NULL
                BEGIN
                  DECLARE @ConflictMessage nvarchar(2048)=N''Synthetic source event already posted: ''
                    +CONVERT(nvarchar(36),@ExistingSourceEventId)+N''.'';
                  THROW 51009,@ConflictMessage,1;
                END;
                THROW 51009,''Synthetic source changed or was already frozen.'',1;
              END;
              IF @DebitId=@CreditId THROW 51000,''Synthetic source requires two accounts.'',1;
              IF NOT EXISTS(SELECT 1 FROM Accounting.Configurations WHERE TenantId=@TenantId
                AND Version=@ExpectedConfigurationVersion
                AND JSON_VALUE(Payload,''$.policies.currency'')=@Currency)
                THROW 51009,''Synthetic source configuration changed.'',1;
              IF NOT EXISTS(SELECT 1 FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@DebitId
                AND Version=@ExpectedDebitAccountVersion AND Purpose=''General'' AND ArchivedAtUtc IS NULL)
                OR NOT EXISTS(SELECT 1 FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@CreditId
                AND Version=@ExpectedCreditAccountVersion AND Purpose=''General'' AND ArchivedAtUtc IS NULL)
                THROW 51009,''Synthetic source account changed.'',1;
              DECLARE @AmountText nvarchar(40)=CONVERT(nvarchar(40),@Amount);
              DECLARE @Lines nvarchar(max)=(SELECT v.Ordinal ordinal,v.AccountId accountId,v.AccountVersion accountVersion,
                    v.Debit debit,v.Credit credit
                FROM (VALUES(1,@DebitId,@ExpectedDebitAccountVersion,@AmountText,N''0.0000''),
                            (2,@CreditId,@ExpectedCreditAccountVersion,N''0.0000'',@AmountText))
                    v(Ordinal,AccountId,AccountVersion,Debit,Credit)
                ORDER BY v.Ordinal FOR JSON PATH);
              DECLARE @Snapshot nvarchar(max)=(SELECT @SourceId sourceId,@ExpectedSourceRevision sourceRevision,
                    @AmountText amount,@Currency currency,@DebitId debitAccountId,@CreditId creditAccountId
                    FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              IF @Failpoint=2 THROW 51000,''Injected before kernel.'',1;
              INSERT @Result EXEC Accounting.PostJournal @ActorId=@ActorId,@SessionId=@SessionId,@RequestId=@RequestId,
                @RequiredPermission=N''AccountingConfigurationManage'',@SourceCommandKind=N''SyntheticPost'',
                @SourceCommandVersion=1,@CanonicalInput=@CanonicalInput,@SourceKind=N''Synthetic'',
                @SourceId=@SourceId,@SourceRevision=@ExpectedSourceRevision,@EventKind=N''Posted'',@RuleVersion=1,
                @ExpectedConfigurationVersion=@ExpectedConfigurationVersion,@Currency=@Currency,@DocumentDate=@DocumentDate,
                @EffectiveDate=@EffectiveDate,@PostingDate=@PostingDate,@Reference=@Reference,@Reason=NULL,
                @SourceSnapshot=@Snapshot,@Lines=@Lines;
              UPDATE Accounting.SyntheticSources SET FrozenAtUtc=SYSUTCDATETIME()
                WHERE TenantId=@TenantId AND Id=@SourceId AND Revision=@ExpectedSourceRevision AND FrozenAtUtc IS NULL;
              IF @@ROWCOUNT<>1 THROW 51009,''Synthetic source changed during posting.'',1;
              IF @Failpoint=3 THROW 51000,''Injected after journal and source freeze.'',1;
            END
            ELSE
            BEGIN
              DECLARE @ReplayCurrency nvarchar(3);
              SELECT @ReplayCurrency=j.Currency FROM Accounting.PostingReceipts r
                JOIN Accounting.JournalEntries j ON j.TenantId=r.TenantId AND j.Id=r.JournalId
                WHERE r.TenantId=@TenantId AND r.RequestId=@RequestId;
              INSERT @Result EXEC Accounting.PostJournal @ActorId=@ActorId,@SessionId=@SessionId,@RequestId=@RequestId,
                @RequiredPermission=N''AccountingConfigurationManage'',@SourceCommandKind=N''SyntheticPost'',
                @SourceCommandVersion=1,@CanonicalInput=@CanonicalInput,@SourceKind=N''Synthetic'',
                @SourceId=@SourceId,@SourceRevision=@ExpectedSourceRevision,@EventKind=N''Posted'',@RuleVersion=1,
                @ExpectedConfigurationVersion=@ExpectedConfigurationVersion,@Currency=@ReplayCurrency,@DocumentDate=@DocumentDate,
                @EffectiveDate=@EffectiveDate,@PostingDate=@PostingDate,@Reference=@Reference,@Reason=NULL,
                @SourceSnapshot=N''{}'',@Lines=N''[]'';
            END;
            COMMIT;
            SELECT SourceEventId,JournalId,Sequence,RecordedAtUtc FROM @Result;
          END TRY
          BEGIN CATCH
            IF @@TRANCOUNT>0 ROLLBACK;
            THROW;
          END CATCH;
        END');
        EXEC(N'GRANT EXECUTE ON Accounting.PostSyntheticJournal TO workbench_web;');
        """;
}
