// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierReportIntegrityTests(SupplierReportIntegrityScenarios scenarios) : IClassFixture<SupplierReportIntegrityScenarios>
{
    [Theory]
    [InlineData("attributionGroup")]
    [InlineData("journalPostingDate")]
    [InlineData("journalCurrency")]
    [InlineData("journalRecordedTime")]
    [InlineData("movementRecordedTime")]
    [InlineData("attributionAccount")]
    [InlineData("attributionVersion")]
    [InlineData("attributionPurpose")]
    [InlineData("historicalControlVersion")]
    public async Task RetainedApplicationEvidenceMustKeepItsExactHistoricalBindings(string corruption)
    {
        // GIVEN genuine payment, application and reversal history in a uniquely restored database.
        await using var scenario = await scenarios.OpenAsync("sourcesFalse");
        var context = scenario.Context;
        Assert.True((await SupplierReconciliationTests.Read(context)).IsComplete);
        var change = corruption switch
        {
            "attributionGroup" => "UPDATE Purchasing.SupplierControlAttributions SET GroupId=(SELECT TOP(1) Id FROM Purchasing.SupplierFinancialGroups WHERE Id<>@group) WHERE Id=@attribution;",
            "journalPostingDate" => "UPDATE Accounting.JournalEntries SET PostingDate=DATEADD(day,1,PostingDate) WHERE Id=@journal;",
            "journalCurrency" => "UPDATE Accounting.JournalEntries SET Currency='EUR' WHERE Id=@journal;",
            "journalRecordedTime" => "UPDATE Accounting.JournalEntries SET RecordedAtUtc=DATEADD(second,-1,RecordedAtUtc) WHERE Id=@journal;",
            "movementRecordedTime" => "UPDATE Purchasing.SupplierItemMovements SET RecordedAtUtc=DATEADD(second,-1,RecordedAtUtc) WHERE Id=@movement;",
            "attributionAccount" => $"UPDATE Purchasing.SupplierControlAttributions SET AccountId='{context.Bank}' WHERE Id=@attribution;",
            "attributionVersion" => "UPDATE Purchasing.SupplierControlAttributions SET AccountVersion=NEWID() WHERE Id=@attribution;",
            "attributionPurpose" => "UPDATE Purchasing.SupplierControlAttributions SET AccountPurpose='SupplierPayable' WHERE Id=@attribution;",
            "historicalControlVersion" => "DECLARE @changed uniqueidentifier=NEWID(); UPDATE Accounting.JournalLines SET AccountVersion=@changed WHERE JournalId=@journal AND Ordinal=@ordinal; UPDATE Purchasing.SupplierControlAttributions SET AccountVersion=@changed WHERE Id=@attribution;",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        // WHEN a privileged integrity fault detaches one retained binding without changing any monetary value.
        await context.Bills.AdminAsync("""
            DECLARE @attribution uniqueidentifier,@movement uniqueidentifier,@journal uniqueidentifier,@group uniqueidentifier,@ordinal int;
            SELECT TOP(1) @attribution=a.Id,@movement=m.Id,@journal=a.JournalId,@group=m.GroupId,@ordinal=a.Ordinal
            FROM Purchasing.SupplierControlAttributions a
            JOIN Purchasing.SupplierItemMovements m ON m.Id=a.MovementId
            JOIN Purchasing.SupplierOpenItems i ON i.Id=m.ItemId
            JOIN Accounting.SourceEvents s ON s.Id=m.SourceEventId
            WHERE s.SourceKind='SupplierApplication' AND s.EventKind='Apply' AND i.Kind='Advance';
            IF @attribution IS NULL THROW 51000,'Expected genuine standalone application evidence.',1;
            """ + change);
        var report = await SupplierReconciliationTests.Read(context);
        // THEN balanced arithmetic cannot conceal the lost historical ownership.
        Assert.False(report.IsComplete);
        Assert.Contains(report.Controls.Items, row => row.InvalidSourceEvidenceCount > 0);
    }
}

public sealed class SupplierReportIntegrityScenarios(SqlServerFixture server) : SupplierScenarioFixture(server, "sourcesFalse");
