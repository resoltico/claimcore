namespace ClaimCore.HostSecurity

/// Safe native causes: no path, input bytes, errno text or exception payload crosses this boundary.
[<RequireQualifiedAccess>]
type PrivateFileFailure =
    | UnsupportedPlatform
    | InvalidLimit
    | AccessRefused
    | TooLarge
    | InvalidUtf8
