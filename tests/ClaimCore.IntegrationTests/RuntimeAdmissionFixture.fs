module internal ClaimCore.IntegrationTests.RuntimeAdmissionFixture

open System.Threading.Tasks
open ClaimCore.Hosting
open ClaimCore.Postgres

/// Synthetic admission tests exercise lifetime and ordering independently of database policy.
let gate requireMutation : RuntimeUseGate =
    {
        RequireCaseMutation = (fun _ -> task { requireMutation () })
        RequireCaseRead = (fun _ -> Task.FromResult(()))
        RequireAuthoritySetup = (fun _ -> Task.FromResult(()))
        RequireAuthorityRead = (fun _ -> Task.FromResult(()))
        RequireAuditTrust = (fun () -> ())
        AuthorityHealth =
            { new IMutationCommitHealth with
                member _.VerifyLocked(_, _, _) = Task.CompletedTask
            }
        CommitHealth =
            { new IMutationCommitHealth with
                member _.VerifyLocked(_, _, _) = Task.CompletedTask
            }
        CommitHealthRequired = false
    }
