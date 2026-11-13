module ClaimCore.Tests.RecoveryJournalProcess

open System
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Child-process probe uses the actual journal adapter and stops immediately after atomic publication.
let publish directory =
    let identity: RecoveryIdentity =
        {
            ServiceOrigin = "https://service.example"
            InstallationId = Guid.Parse "10000000-0000-7000-8000-000000000001"
            ActorId = Guid.Parse "10000000-0000-7000-8000-000000000002"
            AttemptId = Guid.Parse "10000000-0000-7000-8000-000000000004"
            OperationId = Guid.Parse "10000000-0000-7000-8000-000000000003"
            Subject = ActSubject.Case
            Act = "OPEN"
        }

    match RecoveryJournal.save directory identity with
    | Error() -> failwith "Owned journal publication refused."
    | Ok() ->
        Console.WriteLine "published"
        Console.Out.Flush()
        System.Threading.Thread.Sleep System.Threading.Timeout.Infinite
