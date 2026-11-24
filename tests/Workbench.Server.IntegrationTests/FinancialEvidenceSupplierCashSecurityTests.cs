// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceSupplierCashSecurityTests(SupplierCreditRecoveryFixture fixture) : IClassFixture<SupplierCreditRecoveryFixture>
{
    [Theory]
    [InlineData("SupplierCredit", "SupplierCreditsPost")]
    [InlineData("SupplierRefund", "SupplierRefundsRecord")]
    [InlineData("SupplierRefundRepayment", "SupplierRefundRepaymentsRecord")]
    public async Task SupplementRequiresTheCurrentOriginalSourceAuthority(string kind, string permission)
    {
        // GIVEN a genuine source, its stored evidence owner, and newly uploaded supporting bytes.
        var prepared = await fixture.OpenAsync();
        await using var supplier = prepared.Context;
        var context = supplier.Bills;
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var owner = await context.ScalarAsync<Guid>($"""
            SELECT OwnerId FROM Accounting.FinancialEvidenceSets WHERE OwnerKind='{kind}' AND InheritedEvidenceSetId IS NULL
            """);
        await FinancialEvidenceSecurityTests.InstallAdapterAsync(context);
        var command = await FinancialEvidenceSecurityTests.CommandAsync(context, owner, document);
        var request = Guid.NewGuid();
        var original = await FinancialEvidencePostingTests.FinancialHashesAsync(context);
        // WHEN the authorized source operator appends evidence THEN the link is held and replays exactly.
        var receipt = await FinancialEvidenceSecurityTests.AppendAsync(context, command, request);
        Assert.Equal(receipt, await FinancialEvidenceSecurityTests.AppendAsync(context, command, request));
        Assert.True(await context.ScalarAsync<bool>($"""
            SELECT a.Held FROM Storage.Attachments a
            JOIN Accounting.FinancialEvidenceLinks link ON link.TenantId=a.TenantId AND link.AttachmentId=a.Id
            WHERE link.RevisionId='{document.Revision}'
            """));
        // AND a revoked source permission invalidates the very same replay without altering money.
        await context.AdminAsync($"DELETE FROM [Identity].RoleClaims WHERE ClaimValue='{permission}'");
        var error = await Assert.ThrowsAsync<SqlException>(() => FinancialEvidenceSecurityTests.AppendAsync(context, command, request));
        Assert.Equal(51003, error.Number);
        Assert.Equal(original, await FinancialEvidencePostingTests.FinancialHashesAsync(context));
    }
}
