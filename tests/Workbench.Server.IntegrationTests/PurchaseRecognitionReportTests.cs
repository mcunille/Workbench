// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionReportTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DetailExposesBothImmutableSidesOnlyToAuthorizedTenantReaders()
    {
        // GIVEN a matched purchase with two journaled sides and one unrelated synthetic journal.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        await context.Journal.CreateGeneralAccountsAsync();
        var command = await PurchaseRecognitionCorrectionTests.MatchedAsync(context);
        var recognition = await context.Journal.PostAsync(await context.Journal.CreateSourceAsync("7.00"));
        var unitId = Guid.Parse(command["units"]![0]!["unitId"]!.GetValue<string>());
        using var reader = context.Journal.Application.CreateClient();
        await LoginAsync(reader, AuthTestApplication.AdminEmail);
        var journalIds = await JournalIdsAsync(context);
        Assert.Equal(2, journalIds.Count);
        var matchId = await MatchIdAsync(context);

        // WHEN each side's detail is read THEN persisted evidence, mappings, exact amounts and match identity are visible.
        foreach (var (journalId, eventId, side) in journalIds)
        {
            using var response = await reader.GetAsync($"/api/beta/accounting/journals/{journalId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var evidence = body.RootElement.GetProperty("recognition");
            Assert.Equal(unitId, evidence.GetProperty("unitId").GetGuid());
            Assert.Equal(eventId, evidence.GetProperty("eventId").GetGuid());
            Assert.Equal(side, evidence.GetProperty("side").GetString());
            Assert.Equal(journalId, body.RootElement.GetProperty("header").GetProperty("id").GetGuid());
            Assert.Equal("1.000000", evidence.GetProperty("quantity").GetString());
            Assert.Equal("1.000000", evidence.GetProperty("sourceQuantity").GetString());
            Assert.Equal(side == "Recognition" ? "100.00" : "105.00", evidence.GetProperty("sourceAmount").GetString());
            var evidenceJson = evidence.GetProperty("evidenceJson").GetString()!;
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(evidenceJson))),
                evidence.GetProperty("evidenceSha256").GetString());
            using var retained = JsonDocument.Parse(evidenceJson);
            Assert.True(retained.RootElement.GetProperty("accountMappings").GetArrayLength() > 0);
            Assert.Equal(2, evidence.GetProperty("components").GetArrayLength());
            Assert.Contains(evidence.GetProperty("components").EnumerateArray(),
                part => part.GetProperty("kind").GetString() == "BaseCost" &&
                    part.GetProperty("amount").GetString() == (side == "Recognition" ? "100.00" : "105.00"));
            Assert.Equal(matchId, Assert.Single(evidence.GetProperty("matchIds").EnumerateArray()).GetGuid());
            Assert.Empty(evidence.GetProperty("correctionGroups").EnumerateArray());
        }
        using (var old = await reader.GetAsync($"/api/beta/accounting/journals/{recognition.JournalId}"))
        {
            using var body = JsonDocument.Parse(await old.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("recognition").ValueKind);
        }

        // GIVEN a user without reporting permission THEN detail is denied before private evidence is read.
        using var unauthorized = context.Journal.Application.CreateClient();
        await LoginAsync(unauthorized, "member@example.com");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await unauthorized.GetAsync($"/api/beta/accounting/journals/{journalIds[0].JournalId}")).StatusCode);

        // GIVEN another tenant's authorized reporting reader THEN foreign journal IDs remain unavailable.
        await using (var admin = new SqlConnection(context.Journal.Application.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var grant = new SqlCommand("""
                INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
                    SELECT @tenant,@user,RoleId FROM Administration.AccountingRoles
                    WHERE TenantId=@tenant AND Kind='Reader'
                """, admin);
            grant.Parameters.AddWithValue("@tenant", JournalTestContext.OtherTenantId);
            grant.Parameters.AddWithValue("@user", AuthTestApplication.OtherTenantUserId);
            await grant.ExecuteNonQueryAsync();
        }
        using var foreign = context.Journal.Application.CreateClient();
        await LoginAsync(foreign, "other@example.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await foreign.GetAsync($"/api/beta/accounting/journals/{journalIds[0].JournalId}")).StatusCode);
    }

    [Fact]
    public async Task CorrectionDetailLinksAllJournalsWhileEarlierCutoffsContainOnlyOriginalActivity()
    {
        // GIVEN a matched unit and a later replacement with fresh side events and journals.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var original = await MatchedInTwoCommandsAsync(context);
        var originalJournals = await JournalIdsAsync(context);
        var initialMatchId = await MatchIdAsync(context);
        var matchedAt = await MatchRecordedAtAsync(context);
        var correction = await PurchaseRecognitionCorrectionTests.CorrectionAsync(context, original, "98");
        var replacement = await context.CorrectAsync(correction.ToJsonString());
        Assert.NotNull(replacement.CorrectionGroupId);
        using var reader = context.Journal.Application.CreateClient();
        await LoginAsync(reader, AuthTestApplication.AdminEmail);

        // WHEN cut off just before the match THEN only the first side's journal contributes activity.
        var beforeMatch = Uri.EscapeDataString(matchedAt.AddTicks(-1).ToString("O"));
        using (var list = await reader.GetAsync($"/api/beta/accounting/journals?recordedThrough={beforeMatch}"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("100.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
        }
        using (var trial = await reader.GetAsync($"/api/beta/accounting/trial-balance?recordedThrough={beforeMatch}"))
        {
            Assert.Equal(HttpStatusCode.OK, trial.StatusCode);
            using var body = JsonDocument.Parse(await trial.Content.ReadAsStringAsync());
            Assert.Equal("100.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
        }

        // WHEN reading the current original, inverse and replacement details THEN the outer group and journal roles resolve.
        foreach (var journalId in originalJournals.Select(x => x.JournalId).Concat(replacement.JournalIds))
        {
            using var response = await reader.GetAsync($"/api/beta/accounting/journals/{journalId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var recognition = body.RootElement.GetProperty("recognition");
            var relationship = Assert.Single(recognition.GetProperty("correctionGroups").EnumerateArray());
            Assert.Equal(replacement.CorrectionGroupId, relationship.GetProperty("groupId").GetGuid());
            Assert.Equal(initialMatchId, relationship.GetProperty("originalMatchId").GetGuid());
            Assert.NotEqual(Guid.Empty, relationship.GetProperty("replacementMatchId").GetGuid());
            Assert.NotEqual(relationship.GetProperty("originalUnitId").GetGuid(),
                relationship.GetProperty("replacementUnitId").GetGuid());
            Assert.NotEqual(Guid.Empty, relationship.GetProperty("originalEventId").GetGuid());
            Assert.NotEqual(Guid.Empty, relationship.GetProperty("replacementEventId").GetGuid());
            Assert.NotEqual(Guid.Empty, relationship.GetProperty("originalJournalId").GetGuid());
            Assert.NotEqual(Guid.Empty, relationship.GetProperty("reversalJournalId").GetGuid());
            Assert.NotEqual(Guid.Empty, relationship.GetProperty("replacementJournalId").GetGuid());
            var originalSide = originalJournals.Any(x => x.JournalId == journalId) ||
                relationship.GetProperty("reversalJournalId").GetGuid() == journalId;
            Assert.Equal(originalSide ? "Original" : "Replacement", relationship.GetProperty("role").GetString());
            Assert.Equal(originalSide ? initialMatchId : relationship.GetProperty("replacementMatchId").GetGuid(),
                Assert.Single(recognition.GetProperty("matchIds").EnumerateArray()).GetGuid());
            Assert.Equal(originalJournals.Any(x => x.JournalId == journalId) ? "Posted" :
                originalSide ? "Reversal" : "Posted", recognition.GetProperty("journalRole").GetString());
        }

        // WHEN applying the original recorded cutoff THEN later correction activity and relationship metadata are absent.
        var before = Uri.EscapeDataString(replacement.RecordedAtUtc.AddTicks(-1).ToString("O"));
        using (var list = await reader.GetAsync($"/api/beta/accounting/journals?recordedThrough={before}"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.GetProperty("items").GetArrayLength());
            Assert.All(body.RootElement.GetProperty("items").EnumerateArray(), item =>
            {
                Assert.False(item.TryGetProperty("recognition", out _));
                Assert.False(item.TryGetProperty("correctionGroups", out _));
            });
        }
        using (var trial = await reader.GetAsync($"/api/beta/accounting/trial-balance?recordedThrough={before}"))
        {
            Assert.Equal(HttpStatusCode.OK, trial.StatusCode);
            using var body = JsonDocument.Parse(await trial.Content.ReadAsStringAsync());
            Assert.Equal("205.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.All(body.RootElement.GetProperty("items").EnumerateArray(), item =>
                Assert.False(item.TryGetProperty("recognition", out _)));
        }
        // AND posting-date cutoffs also retain February's original financial activity.
        using (var list = await reader.GetAsync("/api/beta/accounting/journals?postingThrough=2026-02-28"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal("205.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
        }
    }

    private static async Task<List<(Guid JournalId, Guid EventId, string Side)>> JournalIdsAsync(PurchaseRecognitionTestContext context)
    {
        await using var command = new SqlCommand("SELECT JournalId,Id,Side FROM Purchasing.RecognitionSideEvents WHERE JournalId IS NOT NULL ORDER BY Side", context.Connection);
        await using var result = await command.ExecuteReaderAsync();
        var rows = new List<(Guid, Guid, string)>();
        while (await result.ReadAsync()) rows.Add((result.GetGuid(0), result.GetGuid(1), result.GetString(2)));
        return rows;
    }

    private static async Task<JsonObject> MatchedInTwoCommandsAsync(PurchaseRecognitionTestContext context)
    {
        var receipt = await context.CommandAsync(cost: "100");
        await context.PostAsync(receipt.ToJsonString());
        var invoice = await context.CommandAsync("Invoice", cost: "105");
        invoice["units"]![0]!["unitId"] = receipt["units"]![0]!["unitId"]!.DeepClone();
        invoice["units"]![0]!["expectedPriorEventRevision"] = 1;
        var side = invoice["units"]![0]!["sides"]![0]!;
        side["evidence"]!["varianceAmount"] = "5";
        side["evidence"]!["varianceReason"] = "Reviewed final price";
        side["evidence"]!["varianceClassification"] = "Inventory";
        side["evidence"]!["inventoryAdjustmentState"] = "Held";
        await PurchaseRecognitionCorrectionTests.StoreAsync(context, side);
        await context.PostAsync(invoice.ToJsonString());
        receipt["units"]![0]!["sides"]!.AsArray().Add(side.DeepClone());
        return receipt;
    }

    private static async Task<DateTimeOffset> MatchRecordedAtAsync(PurchaseRecognitionTestContext context)
    {
        await using var command = new SqlCommand("SELECT TOP (1) RecordedAtUtc FROM Purchasing.RecognitionMatches", context.Connection);
        return (DateTimeOffset)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> MatchIdAsync(PurchaseRecognitionTestContext context)
    {
        await using var command = new SqlCommand("SELECT TOP (1) Id FROM Purchasing.RecognitionMatches", context.Connection);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }
}
