namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text

/// Deterministic authority-event identities remain distinct from the authored command ID.
module internal WitnessEventIdentity =
    let private derive (operationId: Guid) (domain: string) (ordinal: int64) =
        let source =
            Encoding.ASCII.GetBytes(
                "claimcore:witness:technical:v1:"
                + domain
                + ":"
                + operationId.ToString("D")
                + ":"
                + ordinal.ToString(Globalization.CultureInfo.InvariantCulture)
            )

        let digest = SHA256.HashData(source)
        let bytes = digest[0..15]
        bytes[6] <- (bytes[6] &&& 0x0fuy) ||| 0x80uy
        bytes[8] <- (bytes[8] &&& 0x3fuy) ||| 0x80uy
        let hex = Convert.ToHexStringLower(bytes)

        Guid.Parse(
            hex.Substring(0, 8)
            + "-"
            + hex.Substring(8, 4)
            + "-"
            + hex.Substring(12, 4)
            + "-"
            + hex.Substring(16, 4)
            + "-"
            + hex.Substring(20)
        )

    let prepareEventId operationId = derive operationId "PREPARE" 0L

    let startEventId operationId ordinal =
        if ordinal < 1L then
            invalidArg (nameof ordinal) "Attempt ordinal must be positive."

        derive operationId "START" ordinal

    let revocationEventId operationId = derive operationId "REVOCATION" 0L
