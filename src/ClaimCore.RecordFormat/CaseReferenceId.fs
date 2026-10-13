namespace ClaimCore.RecordFormat

open System
open System.Security.Cryptography
open System.Text

/// A retained reference binds to the case UUID already covered by every ledger entry.
/// This 128-bit identifier is independent of caller-chosen operation identity.
module CaseReferenceId =
    let encode installationId (reference: string) =
        if installationId = Guid.Empty || String.IsNullOrEmpty reference then
            invalidArg (nameof reference) "Use a non-empty installation and case reference."

        let prefix =
            Encoding.ASCII.GetBytes
                $"claimcore/case-reference-id/{RecordVersions.CaseReferenceIdentity}\n"

        let bytes = UTF8Encoding(false, true).GetBytes reference

        let digest =
            SHA256.HashData(
                Array.concat [ prefix; installationId.ToByteArray(bigEndian = true); bytes ]
            )

        let identifier = Guid(ReadOnlySpan(digest, 0, 16), bigEndian = true)

        if identifier = Guid.Empty then
            invalidArg (nameof reference) "Case reference produces an empty identifier."

        identifier
