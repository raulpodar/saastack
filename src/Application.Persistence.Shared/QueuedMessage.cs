using Application.Persistence.Interfaces;

namespace Application.Persistence.Shared;

public class QueuedMessage : IQueuedMessage
{
    public string CallerId { get; set; } = null!;

    public string CallId { get; set; } = null!;

    public string? MessageId { get; set; }

    public string? TenantId { get; set; }
}