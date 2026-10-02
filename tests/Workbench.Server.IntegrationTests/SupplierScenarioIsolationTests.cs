// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierScenarioIsolationTests(SupplierIsolationScenarios scenarios) : IClassFixture<SupplierIsolationScenarios>
{
    [Fact]
    public async Task PreparedHistoryIsRetainedWithoutSharingMutationOrAuthentication()
    {
        // GIVEN two independently restored copies of genuine payment/application/reversal history.
        await using var first = await scenarios.OpenAsync("sourcesFalse");
        await using var second = await scenarios.OpenAsync("sourcesFalse");
        var a = first.Context; var b = second.Context;
        Assert.Equal(1, await a.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        Assert.Equal(1, await a.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
        Assert.Equal(1, await a.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplicationReversals"));
        Assert.Equal(await SupplierOpenItemAtomicityTests.SnapshotAsync(a), await SupplierOpenItemAtomicityTests.SnapshotAsync(b));
        Assert.NotEqual(a.Allocation.Journal.SessionId, b.Allocation.Journal.SessionId);
        Assert.False(a.Allocation.Journal.ProofKey.SequenceEqual(b.Allocation.Journal.ProofKey));
        Assert.NotEqual(await a.Bills.ScalarAsync<string>("SELECT SecurityStamp FROM [Identity].Users WHERE Id='11111111-1111-1111-1111-111111111111'"),
            await b.Bills.ScalarAsync<string>("SELECT SecurityStamp FROM [Identity].Users WHERE Id='11111111-1111-1111-1111-111111111111'"));
        Assert.Equal(1, await a.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM sys.database_principals WHERE name LIKE 'workbench_web[_]%'"));
        Assert.Equal(1, await a.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM [Identity].Sessions"));
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(b);
        // WHEN one case corrupts its own evidence and tries the other database's contained credential.
        await a.Bills.AdminAsync("UPDATE Accounting.SourceEvents SET SourceRevision=NEWID() WHERE SourceKind='SupplierApplication'");
        var destination = new SqlConnectionStringBuilder(a.Allocation.Journal.Application.WebConnectionString);
        var other = new SqlConnectionStringBuilder(b.Allocation.Journal.Application.WebConnectionString);
        destination.UserID = other.UserID; destination.Password = other.Password;
        await using var rejected = new SqlConnection(destination.ConnectionString);
        // THEN mutation stays local, foreign credentials cannot connect, and the untouched history still reconciles.
        var error = await Assert.ThrowsAsync<SqlException>(() => rejected.OpenAsync());
        Assert.Contains(error.Number, new[] { 18456, 4060 });
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(b));
        Assert.False((await SupplierReconciliationTests.Read(a)).IsComplete);
        Assert.True((await SupplierReconciliationTests.Read(b)).IsComplete);
    }
}
