namespace RelayLink.Protocol;

public enum FrameType : byte
{
    Register = 1,
    RegisterAccepted = 2,
    ConfigAck = 3,
    Ready = 4,
    Ping = 5,
    Pong = 6,
    Open = 7,
    OpenFailed = 8,
    CancelOpen = 9,
    BindData = 10,
    BindAccepted = 11,
    TargetReady = 12,
    Start = 13,
    Error = 14,
    ConfigUpdate = 15,
    PeerOpenRequest = 16,
    PeerOpenGranted = 17,
    PeerOpen = 18,
    PeerBindData = 19,
    PeerBindAccepted = 20,
    PeerOpenRejected = 21,
    PeerMappingStatus = 22,
    Data = 32,
    Fin = 33,
    Reset = 34
}
