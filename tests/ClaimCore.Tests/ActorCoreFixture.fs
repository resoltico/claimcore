module internal ClaimCore.Tests.ActorCoreFixture

open System
open System.Security.Cryptography
open System.Text
open ClaimCore.Application
open ClaimCore.Tests.SignedRecoveryTestSupport

let private principal =
    PrincipalKey.human "https://issuer.example.test/realms/claimcore" "preparer-subject"
    |> Result.defaultWith (fun _ -> invalidOp "Synthetic principal is invalid.")

let private suppression: ISuppressionCommitments =
    let key = Array.init 32 (fun index -> byte (index + 1))
    let installation = Guid.Parse("10000000-0000-4000-8000-000000000001")
    let lineage = Guid.Parse("10000000-0000-4000-8000-000000000008")

    { new ISuppressionCommitments with
        member _.InstallationId = installation
        member _.LineageId = lineage
        member _.KeyId = Guid.Parse("10000000-0000-4000-8000-000000000009")
        member _.Admit() = ()

        member _.Reference(reference) =
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("reference:" + reference))

        member _.Operation(operationId) =
            HMACSHA256.HashData(
                key,
                Encoding.UTF8.GetBytes("operation:" + operationId.ToString("D"))
            )

        member _.RequestCandidate(canonical) =
            HMACSHA256.HashData(key, Array.append [| 1uy |] canonical)

        member _.PurgeProposal(canonicalDraft) =
            HMACSHA256.HashData(key, Array.append [| 2uy |] canonicalDraft)

        member _.ApprovalDraft(canonicalDraft) =
            HMACSHA256.HashData(key, Array.append [| 3uy |] canonicalDraft)

        member _.ApprovalCanonical(canonicalApproval) =
            HMACSHA256.HashData(key, Array.append [| 4uy |] canonicalApproval)
    }

let private context: ActorCallContext =
    {
        Binding =
            {
                Principal = principal
                ActorId = preparer
                GrantRevision = 5L
            }
        CaseId = Some caseId
        Action = EndpointAction.PrepareNewCase
        Suppression = suppression
    }

/// Pure core unit tests use explicit synthetic attribution; authorization and under-lock checks
/// are independently exercised by PostgreSQL actor-bound tests, never bypassed in production.
let create store recovery clock =
    CoreApi.createActor store recovery clock context authority

let createAsImporter store recovery clock =
    CoreApi.createActor
        store
        recovery
        clock
        { context with
            Binding = importer
            Action = EndpointAction.RecoveryImportRetain
        }
        authority
