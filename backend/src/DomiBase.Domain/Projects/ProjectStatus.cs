namespace DomiBase.Domain.Projects;

/// <summary>
/// Lifecycle state of a project managed by the Control Plane.
/// </summary>
public enum ProjectStatus
{
    /// <summary>The project exists in metadata but its database/roles are being created.</summary>
    Provisioning = 0,

    /// <summary>The project database and restricted roles are ready for Runtime usage.</summary>
    Ready = 1,

    /// <summary>Provisioning failed; the project must not serve Runtime traffic until retried.</summary>
    Failed = 2,

    /// <summary>The project is being torn down and must not serve Runtime traffic.</summary>
    Deleting = 3
}
