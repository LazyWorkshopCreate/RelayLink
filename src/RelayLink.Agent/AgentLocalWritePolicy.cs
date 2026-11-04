namespace RelayLink.Agent;

// The loopback page has no password. Write requests rely on the short-lived
// HttpOnly SameSite=Strict write session, its CSRF token, and the Host/Origin
// checks performed for every request. ADR-0034 supersedes ADR-0027: the manual
// local-management token was removed together with its page input.
public static class AgentLocalWritePolicy
{
    public const string InsecureTransportConfirmation = "CONNECT_WITHOUT_TLS";
}
