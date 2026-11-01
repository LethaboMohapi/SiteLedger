namespace SiteLedger.Infrastructure;

// Message payload is encrypted by the application before persistence.
public sealed class EmailOutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ProtectedPayload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? AbandonedAt { get; set; }
}
