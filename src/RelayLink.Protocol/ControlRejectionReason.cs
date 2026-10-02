namespace RelayLink.Protocol;

// Stable numeric values carried by the optional ErrorMessage reason field.
public enum ControlRejectionReason
{
    ClientNotFound = 1,
    ClientDisabled = 2,
    InvalidSecret = 3,
    IdentityInvalid = 4,
    IdentityMismatch = 5,
    DuplicateSession = 6,
    InvalidFirstFrame = 7,
    ConfigurationMismatch = 8,
    ClientConfigurationChanged = 9
}

public static class ControlRejectionReasonExtensions
{
    public static string Describe(this ControlRejectionReason reason) => reason switch
    {
        ControlRejectionReason.ClientNotFound => "Client ID is not registered on this server.",
        ControlRejectionReason.ClientDisabled => "Client is disabled on this server.",
        ControlRejectionReason.InvalidSecret => "Client registration secret is invalid.",
        ControlRejectionReason.IdentityInvalid => "Agent identity fingerprint is missing or invalid.",
        ControlRejectionReason.IdentityMismatch => "Agent identity fingerprint does not match the identity pinned by the server.",
        ControlRejectionReason.DuplicateSession => "Another session for this client is already connected.",
        ControlRejectionReason.InvalidFirstFrame => "The first control frame must be Register.",
        ControlRejectionReason.ConfigurationMismatch => "Configuration acknowledgement does not match the current session and snapshot.",
        ControlRejectionReason.ClientConfigurationChanged => "Client configuration changed during registration; retry with current credentials and identity.",
        _ => "Server did not provide a recognized rejection reason."
    };
}
