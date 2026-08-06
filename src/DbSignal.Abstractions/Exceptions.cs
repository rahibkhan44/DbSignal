namespace DbSignal;

/// <summary>Base type for every error the library raises deliberately.</summary>
public class DbSignalException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Description of the failure.</param>
    public DbSignalException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="inner">The underlying cause.</param>
    public DbSignalException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The configured provider cannot supply the detail tier the application asked for.
/// Thrown at startup, on purpose — this is a wiring mistake, not a runtime condition.
/// </summary>
public sealed class CapabilityNotSupportedException : DbSignalException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which tier was required, which was available.</param>
    public CapabilityNotSupportedException(string message) : base(message) { }
}

/// <summary>
/// The checkpoint is older than the change history the database still holds, so the gap
/// cannot be enumerated. The caller must do a full reload and start again from
/// <see cref="Checkpoint.Now"/>.
/// </summary>
/// <remarks>
/// This is the edge case hand-rolled implementations get wrong: they compare version
/// numbers, find changes they cannot read, and silently deliver nothing. Reporting it
/// loudly is the entire point — a visible resync beats invisible data loss.
/// </remarks>
public sealed class ResyncRequiredException : DbSignalException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the gap cannot be enumerated.</param>
    public ResyncRequiredException(string message) : base(message) { }
}

/// <summary>
/// The database is missing the one-time setup this provider needs — Change Tracking not
/// enabled, no replication slot, binary logging switched off.
/// </summary>
public sealed class ProvisioningRequiredException : DbSignalException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is missing and how to supply it.</param>
    public ProvisioningRequiredException(string message) : base(message) { }
}
