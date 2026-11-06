module ClaimCore.TestSupport.SummaryAttributionContract

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected current summary: %A" other

let internal lifecycle fixture =
    task {
        let! owner = caseWorker fixture

        let other =
            PrincipalKey.human "https://issuer.example/" "summary-editor"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest
                        (id 88000)
                        other
                        (AuthorityAct.Register
                            {
                                DisplayName = "Summary editor"
                                Contact = None
                            })
                ))
                token

        accepted registered |> ignore

        for number, principal, role in
            [ 88001, other, Role.CaseHandler; 88002, fixture.Owner, Role.DataSteward ] do
            let! granted =
                grant
                    owner
                    (id number)
                    principal
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedAuthority granted

        let editor = { owner with Principal = other }

        let! opened =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request 88003 "SUMMARY-AUTHOR" 0L (AuthoredCaseAct.Open registration)
                ))
                token

        let parent = accepted opened |> caseReceipt |> _.CaseId
        let target = CaseSelector.Id parent

        let change actor number revision responsible summary =
            Service.execute
                actor
                (OperationInput.Work
                    {
                        OperationId = id number
                        Target = target
                        ExpectedRevision = revision
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible = responsible
                                    Summary = summary
                                }
                    })
                token

        let get () =
            Service.get owner { Target = target } token

        let! first =
            change owner 88004 0L WorkValue.Keep (WorkValue.Set "First authored summary")

        let first = accepted first
        let! current = get ()
        let initial = (read current).Work

        Expect.equal
            initial.SummaryChangedAt
            (Some first.RecordedAt)
            "The actual first summary event establishes its timestamp"

        Expect.equal
            (initial.SummaryRecordedBy |> Option.map _.ActorId)
            (Some first.RecordedBy.ActorId)
            "The author is the actual first editor"

        let! assigned =
            change
                editor
                88005
                1L
                (WorkValue.Set(ActorId.format first.RecordedBy.ActorId))
                WorkValue.Keep

        let assigned = accepted assigned
        let! current = get ()
        let assignedWork = (read current).Work

        Expect.equal
            assignedWork.SummaryChangedAt
            initial.SummaryChangedAt
            "Responsibility-only change preserves summary time"

        Expect.equal
            (assignedWork.SummaryRecordedBy |> Option.map _.ActorId)
            (Some first.RecordedBy.ActorId)
            "Responsibility-only change does not claim summary authorship"

        Expect.equal
            (assignedWork.RecordedBy |> Option.map _.ActorId)
            (Some assigned.RecordedBy.ActorId)
            "Aggregate work attribution still follows the assignment"

        Expect.notEqual
            assigned.RecordedBy.ActorId
            first.RecordedBy.ActorId
            "Different real actors make the negative control meaningful"

        let! rewritten =
            change editor 88006 2L WorkValue.Keep (WorkValue.Set "Rewritten summary")

        let rewritten = accepted rewritten
        let! current = get ()

        Expect.equal
            ((read current).Work.SummaryRecordedBy |> Option.map _.ActorId)
            (Some rewritten.RecordedBy.ActorId)
            "Changing text records its actual editor"

        let! reaffirmed =
            change
                owner
                88007
                3L
                (WorkValue.Set(ActorId.format rewritten.RecordedBy.ActorId))
                (WorkValue.Set "Rewritten summary")

        accepted reaffirmed |> ignore
        let! current = get ()

        Expect.equal
            ((read current).Work.SummaryChangedAt)
            (Some rewritten.RecordedAt)
            "Reasserting unchanged text while changing assignment is not a summary edit"

        Expect.equal
            ((read current).Work.SummaryRecordedBy |> Option.map _.ActorId)
            (Some rewritten.RecordedBy.ActorId)
            "Reassertion does not steal authorship"

        let! cleared = change owner 88008 4L WorkValue.Keep WorkValue.Clear
        accepted cleared |> ignore
        let! current = get ()
        Expect.isNone (read current).Work.Summary "Clear removes summary content"
        Expect.isNone (read current).Work.SummaryChangedAt "Clear removes summary timestamp"
        Expect.isNone (read current).Work.SummaryRecordedBy "Clear removes summary attribution"
        let! final = change editor 88009 5L WorkValue.Keep (WorkValue.Set "Final summary")
        accepted final |> ignore

        let! erasedProfile =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest (id 88010) other AuthorityAct.EraseProfile
                ))
                token

        acceptedAuthority erasedProfile
        let! current = get ()

        Expect.equal
            ((read current).Work.SummaryRecordedBy |> Option.map _.DisplayName)
            (Some DisplayName.Erased)
            "Summary attribution uses current profile erasure"

        let! erasedCase =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request 88011 "SUMMARY-AUTHOR" 1L (AuthoredCaseAct.Bare BareAct.Erase)
                ))
                token

        accepted erasedCase |> ignore
        let! current = get ()
        Expect.isNone (read current).Work.Summary "Case erasure wins over previous summary content"
        Expect.isNone (read current).Work.SummaryChangedAt "Case erasure clears summary metadata"

        Expect.isNone
            (read current).Work.SummaryRecordedBy
            "Case erasure does not retain its author projection"

        return parent
    }
