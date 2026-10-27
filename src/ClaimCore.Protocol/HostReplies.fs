namespace ClaimCore.Protocol

type Session =
    {
        Authenticated: bool
        ContractIdentity: string
        AntiforgeryToken: string option
    }

/// The public host failure carries no server prose or transport status.
type HostFailure =
    {
        Code: string
        Phase: ExecutionPhase option
    }
