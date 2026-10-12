// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierInputIsolationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task UnpostedInputsKeepPaymentAndCorrectionAuthoritySeparate()
    {
        // GIVEN independently prepared payment and correction contexts with their original authority.
        await using var correction = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        await using var payment = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        Assert.Equal(3, await CorrectionAuthorityAsync(correction));
        Assert.Equal(0, await CorrectionAuthorityAsync(payment));
        Assert.Equal(1, await correction.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM [Identity].RoleClaims WHERE TenantId='{JournalTestContext.TenantId}' AND ClaimValue='SupplierPaymentsCorrect'"));
        Assert.Equal(0, await payment.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM [Identity].RoleClaims WHERE ClaimValue='SupplierPaymentsCorrect'"));
        Assert.NotEqual(correction.Allocation.Journal.SessionId, payment.Allocation.Journal.SessionId);
        Assert.False(correction.Allocation.Journal.ProofKey.SequenceEqual(payment.Allocation.Journal.ProofKey));
        Assert.Equal(0, await payment.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalEntries"));

        // WHEN a correction context records a real payment and mutates its local account map.
        await correction.RecordAsync(await correction.CommandAsync());
        correction.Bills.Recognition.Accounts.Clear();

        // THEN payment inputs retain clean financial state and can post independently.
        Assert.Equal(6, payment.Bills.Recognition.Accounts.Count);
        Assert.Equal(0, await payment.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        await payment.RecordAsync(await payment.CommandAsync());
        Assert.Equal(1, await payment.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
    }

    private static async Task<int> CorrectionAuthorityAsync(SupplierPaymentTestContext context)
    {
        await using var command = new SqlCommand("""
            SELECT HAS_PERMS_BY_NAME('Purchasing.CorrectSupplierPayment','OBJECT','EXECUTE')
                 + HAS_PERMS_BY_NAME('Purchasing.PreviewSupplierPaymentCorrection','OBJECT','EXECUTE')
                 + HAS_PERMS_BY_NAME('Purchasing.ReverseSupplierApplication','OBJECT','EXECUTE')
            """, context.Allocation.Journal.Connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
