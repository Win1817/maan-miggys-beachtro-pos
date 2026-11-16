namespace Beachtro.Infrastructure.Persistence;

/// <summary>
/// BEFORE UPDATE/DELETE triggers that protect financial history and generated-once rows
/// (database design §Enforcement 2). They stop accidental writes, not a hostile local admin.
/// Column lists are verified against the EF model by tests, so a new column cannot silently
/// become mutable.
/// </summary>
public static class SchemaGuards
{
    /// <summary>Rows that may never be updated or deleted after insert.</summary>
    public static readonly IReadOnlyList<string> AppendOnlyTables =
    [
        "Orders", "OrderItems", "ItemTaxComponents", "Payments",
        "Corrections", "CorrectionItems", "CorrectionTaxComponents", "RefundPayments",
        "InventoryMovements", "CashMovements", "AuditLogs", "TaxConfigurations", "ReportRuns",
    ];

    /// <summary>Tables whose rows may change in limited ways; value = columns that may change.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> PartiallyMutable = new Dictionary<string, string[]>
    {
        // EffectiveToUtc may be set once, to close the interval.
        ["PolicyVersions"] = ["EffectiveToUtc"],

        // Verification and upload observations change; content identity never does.
        ["Backups"] = ["VerifiedUtc", "VerificationState", "UploadState", "RetentionProtectUntilUtc"],

        // While Prepared, only resolution fields and evidence change; identity and quote are frozen.
        ["CheckoutRequests"] = ["State", "ResolvedUtc", "OrderId", "ExternalEvidenceJson", "Version"],
        ["CorrectionRequests"] = ["State", "ResolvedUtc", "CorrectionId", "PaymentEvidenceJson", "Version"],

        // Sequence moves forward only; the key, prefix and date are fixed.
        ["NumberSeries"] = ["LastReservedSequence", "Status", "LastReconciledUtc", "EvidenceRef", "Version"],

        // Before Ready, materialization fields fill in; source identity is fixed.
        ["ArtifactRecords"] = ["RelativePath", "Sha256", "SizeBytes", "State", "MaterializedUtc"],
    };

    public static IEnumerable<string> CreateStatements(Func<string, IReadOnlyList<string>> columnsOf)
    {
        ArgumentNullException.ThrowIfNull(columnsOf);

        foreach (var table in AppendOnlyTables)
        {
            yield return NoUpdate(table, condition: null, $"{table} rows are immutable");
            yield return NoDelete(table, condition: null, $"{table} rows cannot be deleted");
        }

        foreach (var (table, mutable) in PartiallyMutable)
        {
            var frozen = columnsOf(table).Except(mutable).ToArray();
            var frozenChanged = string.Join(" OR ", frozen.Select(c => $"NEW.\"{c}\" IS NOT OLD.\"{c}\""));
            yield return NoUpdate(table, frozenChanged, $"{table}: identity columns are immutable", suffix: "Frozen");
        }

        // Interval closes once.
        yield return NoUpdate("PolicyVersions", "OLD.\"EffectiveToUtc\" IS NOT NULL", "PolicyVersions: a closed revision cannot change", suffix: "Closed");
        yield return NoDelete("PolicyVersions", null, "PolicyVersions rows cannot be deleted");

        // Resolved requests are final.
        yield return NoUpdate("CheckoutRequests", "OLD.\"State\" <> 'Prepared'", "CheckoutRequests: resolved requests are final", suffix: "Resolved");
        yield return NoDelete("CheckoutRequests", null, "CheckoutRequests rows cannot be deleted");
        yield return NoUpdate("CorrectionRequests", "OLD.\"State\" <> 'Prepared'", "CorrectionRequests: resolved requests are final", suffix: "Resolved");
        yield return NoDelete("CorrectionRequests", null, "CorrectionRequests rows cannot be deleted");

        // Never decrement a reserved sequence; never delete a series.
        yield return NoUpdate("NumberSeries", "NEW.\"LastReservedSequence\" < OLD.\"LastReservedSequence\"", "NumberSeries: sequence cannot decrease", suffix: "Decrease");
        yield return NoDelete("NumberSeries", null, "NumberSeries rows cannot be deleted");

        // Ready artifacts are byte-immutable and kept.
        yield return NoUpdate("ArtifactRecords", "OLD.\"State\" = 'Ready'", "ArtifactRecords: Ready artifacts are immutable", suffix: "Ready");
        yield return NoDelete("ArtifactRecords", "OLD.\"State\" = 'Ready'", "ArtifactRecords: Ready artifacts cannot be deleted");

        // Closed shift snapshot is immutable; shifts are never deleted.
        yield return NoUpdate("CashierShifts", "OLD.\"ClosedUtc\" IS NOT NULL", "CashierShifts: a closed shift is immutable", suffix: "Closed");
        yield return NoDelete("CashierShifts", null, "CashierShifts rows cannot be deleted");

        // Attempt outcomes and recovery runs complete once.
        yield return NoUpdate("SyncHistory", "OLD.\"FinishedUtc\" IS NOT NULL", "SyncHistory: a finished attempt is immutable", suffix: "Finished");
        yield return NoDelete("SyncHistory", null, "SyncHistory rows cannot be deleted");
        yield return NoUpdate("RecoveryRuns", "OLD.\"FinishedUtc\" IS NOT NULL", "RecoveryRuns: a finished run is immutable", suffix: "Finished");
        yield return NoDelete("RecoveryRuns", null, "RecoveryRuns rows cannot be deleted");

        // A business profile revision used by a sale is part of that sale's identity.
        yield return NoUpdate(
            "BusinessProfiles",
            "EXISTS (SELECT 1 FROM \"Orders\" WHERE \"BusinessProfileId\" = OLD.\"Id\")",
            "BusinessProfiles: a revision used by a sale is immutable",
            suffix: "Used");
    }

    public static IEnumerable<string> TriggerNames(Func<string, IReadOnlyList<string>> columnsOf) =>
        CreateStatements(columnsOf).Select(s => s.Split('"')[1]);

    private static string NoUpdate(string table, string? condition, string message, string suffix = "") =>
        Trigger($"TR_{table}_NoUpdate{suffix}", "UPDATE", table, condition, message);

    private static string NoDelete(string table, string? condition, string message) =>
        Trigger($"TR_{table}_NoDelete", "DELETE", table, condition, message);

    private static string Trigger(string name, string operation, string table, string? condition, string message)
    {
        var when = condition is null ? string.Empty : $" WHEN {condition}";
        return $"CREATE TRIGGER \"{name}\" BEFORE {operation} ON \"{table}\"{when} BEGIN SELECT RAISE(ABORT, '{message.Replace("'", "''", StringComparison.Ordinal)}'); END;";
    }
}
