// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionConcurrencyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("100", false)]
    [InlineData("0", false)]
    [InlineData("100", true)]
    [InlineData("0", true)]
    public async Task FinancialEvidencePreventsSupplierChange(string cost, bool reversed)
    {
        // GIVEN immutable financial evidence, including zero amounts and fully reversed history.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        await PreparePurchaseAsync(context);
        var input = await context.CommandAsync(cost: cost);
        await context.PostAsync(input.ToJsonString());
        if (reversed)
            await context.CorrectAsync((await PurchaseRecognitionCorrectionTests.CorrectionAsync(context, input, null)).ToJsonString());
        var replacementSupplier = Guid.NewGuid();
        await PurchaseRecognitionCorrectionTests.AdminAsync(context, "INSERT Purchasing.Suppliers(Id,TenantId,Name,IsArchived,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId) VALUES(@id,@tenant,'Replacement supplier',0,SYSUTCDATETIME(),SYSUTCDATETIME(),@actor,@actor)",
            ("@id", replacementSupplier), ("@tenant", JournalTestContext.TenantId), ("@actor", JournalTestContext.ActorId));
        // WHEN the current amendment path attempts to replace or remove the supplier THEN history freezes identity.
        Assert.Equal(50415, (await Assert.ThrowsAsync<SqlException>(() => AmendAsync(context, context.Connection, replacementSupplier))).Number);
        Assert.Equal(50415, (await Assert.ThrowsAsync<SqlException>(() => AmendAsync(context, context.Connection, null))).Number);
        // AND an ordinary notes amendment remains permitted without rewriting retained evidence.
        await AmendAsync(context, context.Connection, context.SupplierId);
        Assert.Equal(context.SupplierId, await PurchaseRecognitionCorrectionTests.ScalarAsync<Guid>(context, "SELECT SupplierId FROM Purchasing.RecognitionUnits"));
        Assert.Equal("Operational note", await PurchaseRecognitionCorrectionTests.ScalarAsync<string>(context, "SELECT Notes FROM Purchasing.DraftOrders"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupplierAmendmentAndFirstRecognitionSerialize(bool postingFirst)
    {
        // GIVEN an ordered purchase whose supplier may still change before its first financial event.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        await PreparePurchaseAsync(context);
        var input = await context.CommandAsync();
        await using var sibling = await context.Journal.OpenSiblingAsync();
        Task Post() => context.PostAsync(input.ToJsonString());
        Task Amend() => AmendAsync(context, sibling, null);
        // WHEN one writer retains the common lock while the independent competing writer arrives.
        var error = await InOrderAsync(context, postingFirst ? context.Connection : sibling, postingFirst ? sibling : context.Connection,
            postingFirst ? Post : Amend, postingFirst ? Amend : Post);
        // THEN only the serially permitted identity is durable.
        Assert.Equal(postingFirst ? 50415 : 51009, error?.Number);
        Assert.Equal(postingFirst ? 1 : 0, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(postingFirst ? context.SupplierId : (Guid?)null,
            await PurchaseRecognitionCorrectionTests.ScalarAsync<Guid?>(context, "SELECT SupplierId FROM Purchasing.DraftOrders WHERE SupplierId IS NOT NULL"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateSourceRequestsProduceOneFinancialEvent(bool sameRequest)
    {
        // GIVEN identical durable source input on independent restricted connections.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var input = await context.CommandAsync(); var request = Guid.NewGuid();
        await using var sibling = await context.Journal.OpenSiblingAsync();
        RecognitionResult? first = null, second = null;
        // WHEN the duplicate arrives before the first source transaction commits.
        var error = await InOrderAsync(context, context.Connection, sibling,
            async () => first = await context.PostAsync(input.ToJsonString(), request),
            async () => second = await context.PostAsync(input.ToJsonString(), sameRequest ? request : Guid.NewGuid(), sibling));
        // THEN an exact retry returns the same complete receipt; a new request cannot duplicate the source.
        if (sameRequest) { Assert.Null(error); Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second)); }
        else Assert.Equal(51009, error?.Number);
        Assert.Equal(1, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(1, await context.CountAsync("RecognitionGroupReceipts"));
        Assert.Equal(100m, await context.BalanceAsync("Inventory"));
    }

    [Fact]
    public async Task CompetingSecondSidesCreateOnlyOneMatch()
    {
        // GIVEN a receipt awaiting its invoice and two independently eligible invoice sources.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var receipt = await context.CommandAsync(); await context.PostAsync(receipt.ToJsonString());
        var first = await context.CommandAsync("Invoice"); var second = await context.CommandAsync("Invoice");
        foreach (var invoice in new[] { first, second })
        { invoice["units"]![0]!["unitId"] = receipt["units"]![0]!["unitId"]!.DeepClone(); invoice["units"]![0]!["expectedPriorEventRevision"] = 1; }
        await using var sibling = await context.Journal.OpenSiblingAsync();
        // WHEN both attempt the same next side before the first commits.
        var error = await InOrderAsync(context, context.Connection, sibling,
            () => context.PostAsync(first.ToJsonString()), () => context.PostAsync(second.ToJsonString(), connection: sibling));
        // THEN the loser cannot create another invoice, match or payable.
        Assert.Equal(51009, error?.Number);
        Assert.Equal(2, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(1, await context.CountAsync("RecognitionMatches"));
        Assert.Equal(-100m, await context.BalanceAsync("SupplierPayable"));
    }

    [Fact]
    public async Task ConcurrentSubdivisionsCannotOverdrawSourceCapacity()
    {
        // GIVEN two individually valid 60 percent subdivisions of one durable source.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var first = await context.CommandAsync(); var second = first.DeepClone().AsObject();
        second["units"]![0]!["unitId"] = Guid.NewGuid().ToString();
        foreach (var input in new[] { first, second })
        {
            input["units"]![0]!["quantity"] = "0.6";
            var side = input["units"]![0]!["sides"]![0]!;
            side["sourceQuantity"] = "0.6"; side["sourceAmount"] = "60"; side["components"]![0]!["amount"] = "60";
            side["subdivisionKey"] = ReferenceEquals(input, first) ? "A" : "B";
        }
        await using var sibling = await context.Journal.OpenSiblingAsync();
        // WHEN their transactions overlap THEN the second sees the committed allocation and rejects.
        var error = await InOrderAsync(context, context.Connection, sibling,
            () => context.PostAsync(first.ToJsonString()), () => context.PostAsync(second.ToJsonString(), connection: sibling));
        Assert.Equal(51009, error?.Number);
        Assert.Equal(1, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(60m, await context.BalanceAsync("Inventory"));
    }

    [Fact]
    public async Task MappingChangeInvalidatesAnOverlappingExpectedConfiguration()
    {
        // GIVEN source input authorized against the old configuration.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var input = await context.CommandAsync();
        var payload = JsonNode.Parse(await PurchaseRecognitionCorrectionTests.ScalarAsync<string>(context, "SELECT Payload FROM Accounting.Configurations"))!;
        payload["mappings"]!.AsArray().Single(x => x!["slot"]!.GetValue<string>() == "Inventory")!["accountId"] = context.Accounts["Prepayment"].ToString();
        payload["mappings"]!.AsArray().Single(x => x!["slot"]!.GetValue<string>() == "Prepayment")!["accountId"] = context.Accounts["Inventory"].ToString();
        await using var sibling = await context.Journal.OpenSiblingAsync();
        // WHEN the mapping change obtains the common lock first THEN posting rechecks the current version.
        var error = await InOrderAsync(context, context.Connection, sibling,
            () => context.Journal.SaveAsync(Guid.NewGuid(), "Configure", payload.ToJsonString(), expectedVersion: context.Journal.ConfigurationVersion),
            () => context.PostAsync(input.ToJsonString(), connection: sibling));
        Assert.Equal(51009, error?.Number);
        Assert.Equal(0, await context.CountAsync("RecognitionGroupReceipts"));
        Assert.Equal(0m, await context.BalanceAsync("Inventory"));
    }

    [Fact]
    public async Task CorrectionInvalidatesAnOverlappingSecondSide()
    {
        // GIVEN one recognition side whose reversal overlaps an invoice matching that original unit.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var receipt = await context.CommandAsync(); await context.PostAsync(receipt.ToJsonString());
        var correction = await PurchaseRecognitionCorrectionTests.CorrectionAsync(context, receipt, null);
        var invoice = await context.CommandAsync("Invoice");
        invoice["units"]![0]!["unitId"] = receipt["units"]![0]!["unitId"]!.DeepClone(); invoice["units"]![0]!["expectedPriorEventRevision"] = 1;
        await using var sibling = await context.Journal.OpenSiblingAsync();
        // WHEN correction commits first THEN the invoice cannot match superseded evidence.
        var error = await InOrderAsync(context, context.Connection, sibling,
            () => context.CorrectAsync(correction.ToJsonString()), () => context.PostAsync(invoice.ToJsonString(), connection: sibling));
        Assert.Equal(51009, error?.Number);
        Assert.Equal(0, await context.CountAsync("RecognitionMatches"));
        Assert.Equal(1, await context.CountAsync("RecognitionEventCorrections"));
        Assert.Equal(0m, await context.BalanceAsync("Inventory"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PeriodCloseAndRecognitionRespectBothSerialOrders(bool postingFirst)
    {
        // GIVEN a February source and a close command on independent restricted connections.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        await PurchaseRecognitionCorrectionTests.AdminAsync(context, JournalControlAdapterSql.Install);
        var input = await context.CommandAsync();
        await using var sibling = await context.Journal.OpenSiblingAsync();
        Task Post() => context.PostAsync(input.ToJsonString());
        async Task Close()
        {
            await using var command = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,@ExpectedConfigurationVersion=@version,@PeriodStart='2026-02-01',@Reason=N'Reconciled',@Evidence=NULL", sibling);
            command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); command.Parameters.AddWithValue("@session", context.Journal.SessionId);
            command.Parameters.AddWithValue("@request", Guid.NewGuid()); command.Parameters.AddWithValue("@version", context.Journal.ConfigurationVersion);
            await command.ExecuteNonQueryAsync();
        }
        // WHEN close and posting overlap THEN a first close prevents posting; a first post is retained by close.
        var error = await InOrderAsync(context, postingFirst ? context.Connection : sibling, postingFirst ? sibling : context.Connection, postingFirst ? Post : Close, postingFirst ? Close : Post);
        if (postingFirst) Assert.Null(error); else Assert.Equal(51009, error?.Number);
        Assert.Equal(postingFirst ? 1 : 0, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(postingFirst ? 100m : 0m, await context.BalanceAsync("Inventory"));
        Assert.Equal(1, await context.Journal.CountAsync("PeriodClosures"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceRevisionPublicationAndRecognitionRespectBothSerialOrders(bool postingFirst)
    {
        // GIVEN a current source revision and a prepared durable successor.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var input = await context.CommandAsync(); var successor = await context.CommandAsync();
        var originalSide = input["units"]![0]!["sides"]![0]!; var nextSide = successor["units"]![0]!["sides"]![0]!;
        await using var publisher = new SqlConnection(context.Journal.Application.AdminConnectionString); await publisher.OpenAsync();
        var request = Guid.NewGuid(); RecognitionResult? posted = null;
        async Task Post() => posted = await context.PostAsync(input.ToJsonString(), request);
        async Task Publish()
        {
            await using var command = new SqlCommand("EXEC Purchasing.PublishFixtureRecognitionRevision @TenantId=@tenant,@Side='Recognition',@SourceId=@source,@Revision=@revision,@PreparedSourceId=@prepared", publisher);
            command.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId); command.Parameters.AddWithValue("@source", originalSide["sourceId"]!.GetValue<string>());
            command.Parameters.AddWithValue("@revision", nextSide["sourceRevision"]!.GetValue<string>()); command.Parameters.AddWithValue("@prepared", nextSide["sourceId"]!.GetValue<string>());
            await command.ExecuteNonQueryAsync();
        }
        // WHEN publication and posting overlap THEN the locked current head governs new writes only.
        var error = await InOrderAsync(context, postingFirst ? context.Connection : publisher, postingFirst ? publisher : context.Connection, postingFirst ? Post : Publish, postingFirst ? Publish : Post);
        if (postingFirst)
        {
            Assert.Null(error);
            Assert.Equal(JsonSerializer.Serialize(posted), JsonSerializer.Serialize(await context.PostAsync(input.ToJsonString(), request)));
            await context.CorrectAsync((await PurchaseRecognitionCorrectionTests.CorrectionAsync(context, input, null)).ToJsonString());
            Assert.Equal(1, await context.CountAsync("RecognitionEventCorrections"));
        }
        else Assert.Equal(51004, error?.Number);
        Assert.Equal(postingFirst ? 1 : 0, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(0m, await context.BalanceAsync("Inventory"));
    }

    internal static async Task<SqlException?> InOrderAsync(PurchaseRecognitionTestContext context, SqlConnection firstConnection, SqlConnection secondConnection, Func<Task> first, Func<Task> second)
    {
        await using var gate = await JournalConcurrencyTests.AccountingLockGate.OpenAsync(context.Journal.Application.AdminConnectionString);
        {
            using var phaseCost = PhaseCostTrace.Measure("transaction-begin");
            await ExecuteAsync(firstConnection, "BEGIN TRANSACTION");
        }
        try
        {
            var firstTask = first();
            {
                using var phaseCost = PhaseCostTrace.Measure("first-lock-observation");
                await gate.WaitForBlockedAsync(firstConnection);
            }
            {
                using var phaseCost = PhaseCostTrace.Measure("release-barrier");
                await gate.ReleaseAsync();
            }
            {
                using var phaseCost = PhaseCostTrace.Measure("first-command-completion");
                await firstTask;
            }
            var secondTask = CaptureAsync(second);
            {
                using var phaseCost = PhaseCostTrace.Measure("second-lock-observation");
                await gate.WaitForBlockedByAsync(firstConnection, secondConnection);
            }
            {
                using var phaseCost = PhaseCostTrace.Measure("transaction-commit");
                await ExecuteAsync(firstConnection, "COMMIT TRANSACTION");
            }
            {
                using var phaseCost = PhaseCostTrace.Measure("second-command-completion");
                return await secondTask;
            }
        }
        finally
        {
            using var phaseCost = PhaseCostTrace.Measure("transaction-cleanup");
            await ExecuteAsync(firstConnection, "IF @@TRANCOUNT>0 ROLLBACK TRANSACTION");
        }
    }

    private static async Task<SqlException?> CaptureAsync(Func<Task> action)
    { try { await action(); return null; } catch (SqlException error) { return error; } }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    { await using var command = new SqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }

    private static async Task PreparePurchaseAsync(PurchaseRecognitionTestContext context)
    {
        await PurchaseRecognitionCorrectionTests.AdminAsync(context, "UPDATE Purchasing.DraftOrders SET PoNumber=1 WHERE Id=@id", ("@id", context.PurchaseOrderId));
        await using var read = new SqlCommand("SELECT RowVersion FROM Purchasing.DraftOrders WHERE Id=@id", context.Connection);
        read.Parameters.AddWithValue("@id", context.PurchaseOrderId);
        context.PurchaseOrderVersion = "0x" + Convert.ToHexString((byte[])(await read.ExecuteScalarAsync())!);
    }

    private static async Task AmendAsync(PurchaseRecognitionTestContext context, SqlConnection connection, Guid? supplier)
    {
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with
        {
            SupplierId = supplier,
            SupplierName = "Recognition supplier",
            Notes = "Operational note",
            Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }]
        });
        await using var command = new SqlCommand("Purchasing.SavePurchaseOrder", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", Guid.NewGuid());
        command.Parameters.AddWithValue("@ActorUserId", JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@TargetId", context.PurchaseOrderId);
        command.Parameters.AddWithValue("@ExpectedVersion", Convert.FromHexString(context.PurchaseOrderVersion[2..]));
        command.Parameters.AddWithValue("@Operation", "Amend"); command.Parameters.AddWithValue("@OrderDate", "2026-01-01");
        command.Parameters.AddWithValue("@Reason", "Operational amendment");
        command.Parameters.AddWithValue("@Draft", JsonSerializer.Serialize(draft, DraftOrderInput.JsonOptions));
        command.Parameters.AddWithValue("@Calculation", "{}");
        await command.ExecuteNonQueryAsync();
    }
}
