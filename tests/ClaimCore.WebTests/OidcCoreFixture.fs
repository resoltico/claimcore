module internal ClaimCore.WebTests.OidcCoreFixture

open ClaimCore.Catalog

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.TestSupport.StoreContractTests

/// The real Application facade ensures verified OAuth clients still need ClaimCore authority.
type Fixture
    (
        store: ClaimCore.Tests.MemoryLedgerStore.Store,
        fixture: ClaimCore.TestSupport.StoreContractTests.Fixture
    ) =
    member _.Actor principal = core fixture principal

    interface IDisposable with
        member _.Dispose() = (store :> IDisposable).Dispose()

let createFor issuer subject =
    task {
        // Synthetic business time follows registration dates and has ledger microsecond precision.
        let instant = DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)
        let store = new ClaimCore.Tests.MemoryLedgerStore.Store(instant)

        try
            let fixture: ClaimCore.TestSupport.StoreContractTests.Fixture =
                {
                    Store = store :> ILedgerStore
                    InstallationId = id 500
                    Owner = PrincipalKey.human issuer subject |> Result.defaultWith invalidOp
                    Calendar =
                        { new IBusinessCalendar with
                            member _.TimeZoneId = "Etc/UTC"

                            member _.DateAt instant =
                                DateOnly.FromDateTime instant.UtcDateTime
                        }
                    Dispose = ignore
                }

            let! owner = caseWorker fixture
            let service = PrincipalKey.service issuer "service" |> Result.defaultWith invalidOp

            let! registered =
                owner.ExecuteAuthority(
                    authorityRequest (id 20) service AuthorityAct.RegisterActor,
                    CancellationToken.None
                )

            acceptedAuthority registered

            let! granted =
                grant owner (id 21) service Role.CaseReader GrantTarget.Installation true

            acceptedAuthority granted

            let! opened =
                owner.Execute(
                    request 10 "SYNTHETIC-001" 0L (BusinessCommand.Open registration),
                    CancellationToken.None
                )

            accepted opened |> ignore
            return new Fixture(store, fixture)
        with error ->
            (store :> IDisposable).Dispose()
            return raise error
    }

let create issuer = createFor issuer "synthetic-owner"
