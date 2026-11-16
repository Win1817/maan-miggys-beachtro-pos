using System.Text.Json;
using Beachtro.Infrastructure.Persistence.Entities;

namespace Beachtro.Infrastructure.Persistence;

/// <summary>
/// Adds append-only audit rows to the caller's unit of work, so a financial or security change and
/// its audit commit together (security design §Audit). Callers pass only safe details: never
/// passwords, hashes, tokens or full payment notes.
/// </summary>
internal static class AuditRecorder
{
    public const string SystemActorName = "System";
    public const string UnknownActorName = "(unknown user)";

    public static void Record(
        PosDbContext db,
        long occurredUtc,
        Guid? actorUserId,
        string actorName,
        string action,
        string entityType,
        string? entityId,
        string? reason,
        object? details,
        Guid? requestId = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            OccurredUtc = occurredUtc,
            ActorUserId = actorUserId,
            ActorNameSnapshot = string.IsNullOrWhiteSpace(actorName) ? UnknownActorName : actorName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Reason = reason,
            SafeDetailsJson = details is null ? "{}" : JsonSerializer.Serialize(details),
            CorrelationId = Guid.NewGuid(),
            RequestId = requestId,
        });
    }
}
