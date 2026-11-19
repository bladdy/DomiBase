namespace DomiBase.Domain.Projects;

/// <summary>
/// State of an individual provisioning step. Each step must be retryable and compensable.
/// </summary>
public enum ProvisioningStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Compensated = 4
}
