namespace ClaimCore.Postgres

open System.Security.Cryptography
open System.Text

/// Domain-separated plaintext carried by the witnessed handoff settlement envelope.
module internal WriterHandoffEvidenceHash =
    let settlement (prepareCanonical: byte array) (settlementCanonical: byte array) =
        let prefix = Encoding.ASCII.GetBytes("claimcore:writer-handoff:settlement:v1:")
        let prepare = SHA256.HashData(prepareCanonical)
        let committed = SHA256.HashData(settlementCanonical)
        let input = Array.concat [ prefix; prepare; committed ]

        try
            SHA256.HashData(input)
        finally
            CryptographicOperations.ZeroMemory(input)
            CryptographicOperations.ZeroMemory(prepare)
            CryptographicOperations.ZeroMemory(committed)
