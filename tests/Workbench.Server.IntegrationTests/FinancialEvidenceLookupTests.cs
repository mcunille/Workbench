// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceLookupTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private SupplierPaymentTestContext _context = null!;
    // Each instance owns fresh database and authority; cold setup remains part of process timing.
    public async Task InitializeAsync() => _context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    [Fact]
    public async Task PaymentReportsKeepEvidenceSeparateFromZeroValueBillWithIdenticalIds()
    {
        // GIVEN an authentic zero-value bill and a payment sharing owner and revision GUIDs.
        var context = _context;
        var draft = context.Bills.CompleteDraft();
        draft["revision"]!["total"] = "0";
        draft["revision"]!["missingEvidenceReason"] = "Zero-value invoice has no file";
        foreach (var component in draft["revision"]!["units"]![0]!["components"]!.AsArray()) component!["amount"] = "0";
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context.Bills, draft);
        await context.Bills.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.Bills.PostCommand(reviewed));
        var payment = await context.CommandAsync();
        payment["paymentId"] = reviewed["billId"]!.DeepClone();
        payment["paymentRevisionId"] = reviewed["revisionId"]!.DeepClone();
        payment["evidence"]!["missingEvidenceReason"] = "Payment receipt has no file";
        await context.RecordAsync(payment);
        var owner = Guid.Parse(payment["paymentId"]!.GetValue<string>());
        var paymentSet = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Accounting.FinancialEvidenceSets WHERE OwnerKind='SupplierPayment' AND OwnerId='{owner}'");
        var journal = await context.Bills.ScalarAsync<Guid>($"SELECT j.Id FROM Accounting.JournalEntries j JOIN Accounting.SourceEvents s ON s.Id=j.SourceEventId WHERE s.SourceKind='SupplierPayment' AND s.SourceId='{owner}'");
        var item = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierOpenItems WHERE SourceKind='SupplierPayment' AND SourceId='{owner}'");
        using var client = context.Bills.Journal.Application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);

        // WHEN each payment report reads evidence THEN only the payment's typed evidence is returned.
        foreach (var route in new[] { $"/api/beta/accounting/journals/{journal}", $"/api/beta/accounting/supplier-open-items/{item}" })
        {
            var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal(paymentSet, result["financialEvidence"]!["id"]!.GetValue<Guid>());
            Assert.Equal("Payment receipt has no file", result["financialEvidence"]!["missingEvidenceReason"]!.GetValue<string>());
        }
        // AND reading the original bill still returns its distinct explanation.
        var bill = await context.Bills.ReadAsync("ReadSupplierBill", ("@BillId", owner));
        Assert.Equal("Zero-value invoice has no file", bill["financialEvidence"]!["missingEvidenceReason"]!.GetValue<string>());
        Assert.NotEqual(paymentSet, bill["financialEvidence"]!["id"]!.GetValue<Guid>());

    }
}

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceLookupReversalTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private SupplierPaymentTestContext _context = null!;
    public async Task InitializeAsync() => _context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    [Fact]
    public async Task PaymentReversalJournalUsesOriginalEvidenceOwnerKind()
    {
        // GIVEN an authentic posted payment with its own evidence explanation.
        var context = _context;
        var payment = await context.CommandAsync();
        payment["evidence"]!["missingEvidenceReason"] = "Payment receipt has no file";
        await context.RecordAsync(payment);
        var owner = Guid.Parse(payment["paymentId"]!.GetValue<string>());
        var paymentSet = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Accounting.FinancialEvidenceSets WHERE OwnerKind='SupplierPayment' AND OwnerId='{owner}'");
        var journal = await context.Bills.ScalarAsync<Guid>($"SELECT j.Id FROM Accounting.JournalEntries j JOIN Accounting.SourceEvents s ON s.Id=j.SourceEventId WHERE s.SourceKind='SupplierPayment' AND s.SourceId='{owner}'");
        // WHEN the payment is reversed THEN the reversal resolves the original payment's kind and evidence.
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, owner);
        await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), correction);
        var reversal = await context.Bills.ScalarAsync<Guid>($"SELECT ReversalJournalId FROM Accounting.CorrectionGroups WHERE OriginalJournalId='{journal}'");
        using var client = context.Bills.Journal.Application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        var reversed = await client.GetAsync($"/api/beta/accounting/journals/{reversal}");
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        var reversalBody = JsonNode.Parse(await reversed.Content.ReadAsStringAsync())!;
        Assert.Equal(paymentSet, reversalBody["financialEvidence"]!["id"]!.GetValue<Guid>());
        Assert.Equal("Payment receipt has no file", reversalBody["financialEvidence"]!["missingEvidenceReason"]!.GetValue<string>());
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceLookupRecognitionTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private SupplierBillTestContext _context = null!;
    public async Task InitializeAsync() => _context = await SupplierBillPostingTests.OpenAsync(sqlServer);
    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecognitionJournalUsesItsTypedEvidenceOwner(bool billBacked)
    {
        // GIVEN bill-backed or standalone recognition with its distinct immutable evidence owner.
        var context = _context;
        Guid journal;
        Guid owner;
        if (billBacked)
        {
            var reviewed = await SupplierBillPostingTests.ReviewedAsync(context);
            var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
            journal = Guid.Parse(posted["journalIds"]![0]!.GetValue<string>());
            owner = Guid.Parse(reviewed["billId"]!.GetValue<string>());
        }
        else
        {
            var command = await context.Recognition.CommandAsync();
            var posted = await context.Recognition.PostAsync(command.ToJsonString());
            journal = posted.JournalIds[0];
            owner = posted.EventIds[0];
        }
        var kind = billBacked ? "SupplierBill" : "PurchaseRecognition";
        var expected = await context.ScalarAsync<Guid>($"SELECT Id FROM Accounting.FinancialEvidenceSets WHERE OwnerKind='{kind}' AND OwnerId='{owner}'");
        using var client = context.Journal.Application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        // WHEN its journal is read THEN a bill uses bill identity and a standalone side uses posted event identity.
        var response = await client.GetAsync($"/api/beta/accounting/journals/{journal}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(expected, result["financialEvidence"]!["id"]!.GetValue<Guid>());
        Assert.Equal(billBacked ? "Reviewed invoice supplied without a file" : "Reviewed source evidence",
            result["financialEvidence"]!["missingEvidenceReason"]!.GetValue<string>());
    }
}
