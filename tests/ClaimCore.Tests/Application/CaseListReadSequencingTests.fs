module ClaimCore.Tests.CaseListReadSequencingTests

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport

// Delegate to the real memory contract; only the returned identity and later failure are injected.
type CaseListReadProbe() =
    inherit DispatchProxy()
    member val Inner: IReadTx = Unchecked.defaultof<_> with get, set
    member val Calls = ResizeArray<string>() with get, set
    member val CorruptPage = false with get, set
    member val FailLookup = false with get, set

    override this.Invoke(methodInfo, arguments) =
        let methodInfo = nonNull methodInfo
        this.Calls.Add methodInfo.Name

        if methodInfo.Name = "FindCaseByReference" && this.FailLookup then
            raise (IO.IOException "Synthetic later lookup failure")

        let result = methodInfo.Invoke(this.Inner, arguments)

        if methodInfo.Name = "CasePage" && this.CorruptPage then
            box (
                task {
                    let! page = unbox<Task<Page<CasePageItem, CaseListPosition>>> result

                    let changed =
                        page.Items
                        |> List.map (fun (item: CasePageItem) ->
                            let wrongId =
                                CaseId.create (Guid.Parse "bbbbbbbb-0000-4000-8000-000000000099")
                                |> Option.get

                            { item with
                                Case = { item.Case with CaseId = wrongId }
                            })

                    return { page with Items = changed }
                }
            )
        else
            result

type private ProbedStore(inner: IStore) =
    member val Calls = ResizeArray<string>() with get
    member val CorruptPage = false with get, set
    member val FailLookup = false with get, set

    interface IStore with
        member _.Context query = inner.Context query
        member _.Write operation = inner.Write operation

        member this.Read operation =
            let work tx =
                let proxy = DispatchProxy.Create<IReadTx, CaseListReadProbe>()
                let state = (box proxy |> nonNull) :?> CaseListReadProbe
                state.Inner <- tx
                state.Calls <- this.Calls
                state.CorruptPage <- this.CorruptPage
                state.FailLookup <- this.FailLookup
                operation.Work proxy

            task {
                try
                    return! inner.Read { operation with Work = work }
                with :? IO.IOException ->
                    return Error StoreFailure.Unavailable
            }

let private withProbe check =
    let installation = Guid.Parse "bbbbbbbb-0000-4000-8000-000000000001"

    use memory =
        new MemoryStore.Store(installation, DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero))

    let traced = ProbedStore(memory :> IStore)

    let principal =
        PrincipalKey.human "https://issuer.example/" "reader" |> Fixtures.accepted

    let actor =
        {
            ActorId = CaseFixtures.actorId (Guid.Parse "bbbbbbbb-0000-4000-8000-000000000002")
            Principal = principal
            Enabled = true
            Profile = None
        }

    memory.SeedActor
        actor
        [ Role.CaseReader, None; Role.CaseRegistrar, None; Role.DataSteward, None ]

    let calendar =
        { new IBusinessCalendar with
            member _.TimeZoneId = "Etc/UTC"
            member _.DateAt _ = DateOnly(2026, 11, 6)
        }

    let core =
        Service.create "synthetic" installation (traced :> IStore) calendar principal

    for index, reference in List.indexed [ "A-MATCH-1"; "Match-1" ] do
        let reply =
            Service.execute
                core
                (Inputs.caseOperation
                    {
                        OperationId =
                            Guid.Parse(sprintf "bbbbbbbb-0000-4000-8000-%012d" (index + 3))
                        CaseReference = reference
                        ExpectedRevision = 0L
                        Act = AuthoredCaseAct.Open Inputs.registration
                    })
                CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()

        match reply.Outcome with
        | ExecuteResult.Accepted _ -> ()
        | other -> failtestf "Fixture opening failed: %A" other

    let scopedPrincipal =
        PrincipalKey.human "https://issuer.example/" "scoped" |> Fixtures.accepted

    let scopedActor =
        { actor with
            ActorId = CaseFixtures.actorId (Guid.Parse "bbbbbbbb-0000-4000-8000-000000000010")
            Principal = scopedPrincipal
        }

    memory.SeedActor
        scopedActor
        [
            Role.CaseReader,
            Some(CaseFixtures.caseId (Guid.Parse "bbbbbbbb-0000-4000-8000-000000000003"))
        ]

    let scoped =
        Service.create "synthetic" installation (traced :> IStore) calendar scopedPrincipal

    traced.Calls.Clear()
    check core scoped traced

let private read core text statuses limit =
    Service.list
        core
        {
            Page = { AfterCursor = None; Limit = limit }
            Filter = CaseListFilter.create [] statuses [] (Some text) CaseListOrder.Reference
        }
        CancellationToken.None
    |> fun task -> task.GetAwaiter().GetResult()

let private page =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected successful list: %A" other

let private expectExact (value: CaseSummaryPage) =
    Expect.equal
        value.ExactReference
        (Some
            {
                CaseId = CaseFixtures.caseId (Guid.Parse "bbbbbbbb-0000-4000-8000-000000000004")
                CaseReference = CaseReference.parse "Match-1" |> Fixtures.accepted
            })
        "Original authored reference and creation identity"

[<Tests>]
let tests =
    testList
        "case-list read sequencing"
        [
            testCase
                "summary integrity failure wins and prevents later lookup or authority reads"
                (fun () ->
                    withProbe (fun core _ traced ->
                        traced.CorruptPage <- true
                        traced.FailLookup <- true

                        Expect.equal
                            (read core "Match-1" [] 50)
                            (Outcome.Failed CoreFault.StoreIntegrityError)
                            "Known integrity failure is not masked by a later provider failure"

                        Expect.isFalse
                            (traced.Calls.Contains "FindCaseByReference")
                            "No later reference lookup"

                        Expect.isFalse
                            (traced.Calls.Contains "Authority")
                            "No later target authority read"))

            testCase "validated visible exact hit reuses disclosure without later reads" (fun () ->
                withProbe (fun core _ traced ->
                    traced.FailLookup <- true
                    let result = read core "match-1" [] 50 |> page
                    expectExact result

                    Expect.equal
                        (result.Items
                         |> List.map (fun item -> CaseReference.format item.Reference))
                        [ "A-MATCH-1"; "Match-1" ]
                        "Exact matching need not be the first row"

                    Expect.isFalse
                        (traced.Calls.Contains "FindCaseByReference")
                        "Visible validated identity needs no second lookup"

                    Expect.isFalse
                        (traced.Calls.Contains "Authority")
                        "Visible row already passed snapshot disclosure"))
            testCase
                "readable exact reference outside a page still needs lookup and authorization"
                (fun () ->
                    withProbe (fun core _ traced ->
                        let result = read core "match-1" [] 1 |> page
                        expectExact result

                        Expect.equal
                            (result.Items
                             |> List.map (fun item -> CaseReference.format item.Reference))
                            [ "A-MATCH-1" ]
                            "Exact target is outside this page"

                        Expect.equal
                            (traced.Calls |> Seq.filter ((=) "FindCaseByReference") |> Seq.length)
                            1
                            "One fallback lookup"

                        Expect.equal
                            (traced.Calls |> Seq.filter ((=) "Authority") |> Seq.length)
                            1
                            "Independent target authorization"))
            testCase "readable exact reference survives an empty filtered page" (fun () ->
                withProbe (fun core _ traced ->
                    let result = read core "match-1" [ CaseStatus.Closed ] 50 |> page
                    Expect.isEmpty result.Items "No closed case is in the fixture"
                    expectExact result

                    Expect.isTrue
                        (traced.Calls.Contains "FindCaseByReference")
                        "Empty page does not suppress lookup"

                    Expect.isTrue
                        (traced.Calls.Contains "Authority")
                        "Outside-filter target is authorized"))
            testCase
                "partial and absent reference searches retain fallback and nonmatches"
                (fun () ->
                    withProbe (fun core _ traced ->
                        for query, count in [ "match", 2; "MISSING", 0 ] do
                            traced.Calls.Clear()
                            let result = read core query [] 50 |> page
                            Expect.equal result.Items.Length count "Independently selected rows"

                            Expect.isNone
                                result.ExactReference
                                "Substring or missing identity is not exact"

                            Expect.equal
                                (traced.Calls
                                 |> Seq.filter ((=) "FindCaseByReference")
                                 |> Seq.length)
                                1
                                "Valid reference syntax still probes absence"

                            Expect.isFalse
                                (traced.Calls.Contains "Authority")
                                "No target to authorize"))
            testCase
                "unreadable exact target remains undisclosed beside a visible partial match"
                (fun () ->
                    withProbe (fun _ scoped traced ->
                        let result = read scoped "match-1" [] 50 |> page

                        Expect.equal
                            (result.Items
                             |> List.map (fun item -> CaseReference.format item.Reference))
                            [ "A-MATCH-1" ]
                            "Only granted case is disclosed"

                        Expect.isNone result.ExactReference "Other exact target is not disclosed"

                        Expect.isTrue
                            (traced.Calls.Contains "Authority")
                            "Fallback still checks target scope"))
            testCase
                "scoped visible match avoids rereads and malformed reference syntax does not probe"
                (fun () ->
                    withProbe (fun _ scoped traced ->
                        traced.FailLookup <- true
                        let result = read scoped "a-match-1" [] 50 |> page

                        Expect.equal
                            (result.ExactReference
                             |> Option.map (fun item -> CaseReference.format item.CaseReference))
                            (Some "A-MATCH-1")
                            "Scoped visible hit"

                        Expect.isFalse
                            (traced.Calls.Contains "FindCaseByReference")
                            "No second lookup"

                        traced.Calls.Clear()
                        let result = read scoped "two words" [] 50 |> page
                        Expect.isNone result.ExactReference "Not a case-reference grammar"

                        Expect.isFalse
                            (traced.Calls.Contains "FindCaseByReference")
                            "Parse guard remains"))
            testCase "erasure preserves exact authorized reference identity" (fun () ->
                withProbe (fun core _ traced ->
                    for operation, revision, act in
                        [
                            31, 1L, AuthoredCaseAct.Void "Retained reason"
                            32, 2L, AuthoredCaseAct.Bare BareAct.Erase
                        ] do
                        let reply =
                            Service.execute
                                core
                                (Inputs.caseOperation
                                    {
                                        OperationId =
                                            Guid.Parse(
                                                sprintf "bbbbbbbb-0000-4000-8000-%012d" operation
                                            )
                                        CaseReference = "Match-1"
                                        ExpectedRevision = revision
                                        Act = act
                                    })
                                CancellationToken.None
                            |> fun task -> task.GetAwaiter().GetResult()

                        match reply.Outcome with
                        | ExecuteResult.Accepted _ -> ()
                        | other -> failtestf "Fixture erasure failed: %A" other

                    traced.Calls.Clear()
                    read core "match-1" [] 50 |> page |> expectExact))
        ]
