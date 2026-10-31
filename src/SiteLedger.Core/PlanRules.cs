namespace SiteLedger.Core;

public sealed class PlanLimitException(string message) : InvalidOperationException(message);

public static class PlanRules
{
    public static void EnsureProjectCapacity(Plan plan, int activeProjects)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeProjects);
        var max = PlanLimits.For(plan).ActiveProjects;
        if (max.HasValue && activeProjects >= max.Value) throw new PlanLimitException("Active project limit reached. Upgrade your plan.");
    }

    public static void EnsureUserCapacity(Plan plan, bool viewer, int staff, int viewers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(staff);
        ArgumentOutOfRangeException.ThrowIfNegative(viewers);
        var limits = PlanLimits.For(plan);
        if ((!viewer && staff >= limits.Staff) || (viewer && limits.Viewers.HasValue && viewers >= limits.Viewers.Value))
            throw new PlanLimitException("User limit reached. Upgrade your plan.");
    }

    public static void EnsureUploadCapacity(Plan plan, long usedBytes, long fileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileBytes);
        var limits = PlanLimits.For(plan);
        if (fileBytes > limits.MaxFileBytes) throw new PlanLimitException("File size limit exceeded. Upgrade your plan.");
        if (usedBytes > limits.StorageBytes || fileBytes > limits.StorageBytes - usedBytes)
            throw new PlanLimitException("Storage quota exceeded. Upgrade your plan.");
    }
}
