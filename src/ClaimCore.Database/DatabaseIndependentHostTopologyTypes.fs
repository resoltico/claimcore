namespace ClaimCore.Database

open System

module internal IndependentHostRoles =
    let hosts = [ "archive"; "checkpoint"; "key"; "primary"; "witness" ]
    let signers = hosts @ [ "old-writer-fence" ]

[<NoEquality; NoComparison>]
type internal IndependentRolePin =
    {
        Role: string
        PublicKeySha256: string
        MachineHash: string
        StorageHash: string
        AdminActorId: Guid
        HostKeyId: Guid
        SshHostKeySha256: string
    }

[<NoEquality; NoComparison>]
type internal IndependentFencePin =
    {
        Role: IndependentRolePin
        OldEndpointId: Guid
        OldEndpointAddressSha256: string
        OldPrimaryRoleOid: int64
        OldWitnessRoleOid: int64
        OldPrimaryCredentialSha256: string
        OldWitnessCredentialSha256: string
        PrimarySessionSetSha256: string
        WitnessSessionSetSha256: string
    }

[<NoEquality; NoComparison>]
type internal IndependentHostTopology =
    {
        Digest: string
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        W1Sequence: int64
        W1Hash: string
        PublicationManifestSha256: string
        AggregateSigningKeyId: Guid
        AggregateHolderActorId: Guid
        AggregatePublicKeySha256: string
        Pins: IndependentRolePin list
        Observer: IndependentFencePin
        ValidUntil: DateTimeOffset
    }
