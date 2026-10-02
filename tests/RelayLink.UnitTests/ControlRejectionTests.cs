using System.Text;
using RelayLink.Agent;
using RelayLink.Protocol;

namespace RelayLink.UnitTests;

public sealed class ControlRejectionTests
{
    [Theory]
    [InlineData(ControlRejectionReason.ClientNotFound, "not registered")]
    [InlineData(ControlRejectionReason.ClientDisabled, "disabled")]
    [InlineData(ControlRejectionReason.InvalidSecret, "secret is invalid")]
    [InlineData(ControlRejectionReason.IdentityInvalid, "missing or invalid")]
    [InlineData(ControlRejectionReason.IdentityMismatch, "pinned")]
    [InlineData(ControlRejectionReason.DuplicateSession, "already connected")]
    [InlineData(ControlRejectionReason.ConfigurationMismatch, "acknowledgement")]
    [InlineData(ControlRejectionReason.ClientConfigurationChanged, "changed")]
    public void Agent_preserves_typed_reason_and_uses_local_description(ControlRejectionReason reason, string description)
    {
        var frame = new Frame(FrameType.Error, JsonProtocolSerializer.Serialize(new ErrorMessage(ErrorCode.AuthFailed, reason)));
        var failure = AgentPermanentException.FromServerError(frame, "registration");
        Assert.Equal(ErrorCode.AuthFailed, failure.ErrorCode);
        Assert.Equal(reason, failure.RejectionReason);
        Assert.Contains(description, failure.Message);
    }

    [Theory]
    [InlineData("{\"code\":0}", ErrorCode.AuthFailed)]
    [InlineData("{\"code\":0,\"reason\":999}", ErrorCode.AuthFailed)]
    [InlineData("{\"code\":999,\"reason\":999}", ErrorCode.ProtocolError)]
    [InlineData("{\"code\":0,\"reason\":\"test-secret-marker\"}", ErrorCode.ProtocolError)]
    public void Old_unknown_and_malformed_details_use_safe_fallback(string json, ErrorCode expectedCode)
    {
        var failure = AgentPermanentException.FromServerError(new Frame(FrameType.Error, Encoding.UTF8.GetBytes(json)), "registration");
        Assert.Equal(expectedCode, failure.ErrorCode);
        Assert.Null(failure.RejectionReason);
        Assert.DoesNotContain("test-secret-marker", failure.Message);
    }

    [Fact]
    public void Unextended_error_preserves_original_wire_shape()
    {
        Assert.Equal("{\"code\":0}", Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(new ErrorMessage(ErrorCode.AuthFailed))));
    }
}
