namespace ClaimCore.Postgres

open System

[<NoEquality; NoComparison>]
type internal ReviewedDeploymentProfile =
    {
        PublicationRootKey: byte array
        BackupHealthPolicySha256: string
    }

/// Operator-specific reviewed builds pin both the independent publication key and
/// the exact health policy bytes. The generic source-preview build has no such authority.
module internal ReviewedDeploymentRoot =
    let private reviewed: ReviewedDeploymentProfile option = None

    let private validSha (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               ('0' <= character && character <= '9') || ('a' <= character && character <= 'f'))

    let current () =
        reviewed
        |> Option.map (fun value ->
            if
                value.PublicationRootKey.Length <> 32
                || not (validSha value.BackupHealthPolicySha256)
            then
                invalidOp "Reviewed deployment root or backup policy is invalid."

            {
                PublicationRootKey = Array.copy value.PublicationRootKey
                BackupHealthPolicySha256 = value.BackupHealthPolicySha256
            })

    let publicationKey () =
        current () |> Option.map _.PublicationRootKey
