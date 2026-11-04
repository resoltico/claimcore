namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type CaseStageKind =
    | Opened
    | Decided
    | Paid
    | Closed

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseStageKind =
    let vocabulary =
        Vocabulary.ofUnion<CaseStageKind> "caseStage" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary

type CaseStageOutline =
    {
        Current: CaseStageKind
        Completed: CaseStageKind list
    }

type CaseStageEntry =
    {
        Stage: CaseStageKind
        Completed: bool
        Date: CalendarDate option
        Amount: Money option
    }

[<RequireQualifiedAccess>]
type CaseStageDisplay =
    | Tracker of current: CaseStageKind * steps: CaseStageEntry list
    | Voided
    | Erased
    | Reinstated

/// Recorded financial groups need not form a linear path: a case may close without a decision or payment.
module CaseStages =
    let outline =
        function
        | CaseState.Voided _
        | CaseState.Erased -> None
        | CaseState.Active(fields: CaseFields) ->
            let closed = fields.Status = CaseStatus.Closed
            let decided = fields.Decision.IsSome
            let paid = fields.Payment.IsSome

            Some
                {
                    Current =
                        if closed then CaseStageKind.Closed
                        elif paid then CaseStageKind.Paid
                        elif decided then CaseStageKind.Decided
                        else CaseStageKind.Opened
                    Completed =
                        [
                            CaseStageKind.Opened
                            if decided then
                                CaseStageKind.Decided
                            if paid then
                                CaseStageKind.Paid
                            if closed then
                                CaseStageKind.Closed
                        ]
                }

    let describe state (opened: CalendarDate option) (closed: CalendarDate option) lastAct =
        match state with
        | CaseState.Voided _ -> Ok CaseStageDisplay.Voided
        | CaseState.Erased -> Ok CaseStageDisplay.Erased
        | CaseState.Active _ when lastAct = Some CaseActKind.Reinstate ->
            Ok CaseStageDisplay.Reinstated
        | CaseState.Active(fields: CaseFields) ->
            match opened, closed, outline state with
            | Some opened, closed, Some outline when
                fields.Status <> CaseStatus.Closed || closed.IsSome
                ->
                let payable = fields.Decision |> Option.map Decision.payable

                let step stage date amount =
                    {
                        CaseStageEntry.Stage = stage
                        Completed = List.contains stage outline.Completed
                        Date = date
                        Amount = amount
                    }

                Ok(
                    CaseStageDisplay.Tracker(
                        outline.Current,
                        [
                            step
                                CaseStageKind.Opened
                                (Some opened)
                                (Some(Registration.claimed fields.Registration))
                            step
                                CaseStageKind.Decided
                                (fields.Decision |> Option.map Decision.date)
                                payable
                            step
                                CaseStageKind.Paid
                                (fields.Payment |> Option.map Payment.date)
                                (fields.Payment |> Option.bind (fun _ -> payable))
                            step
                                CaseStageKind.Closed
                                (if fields.Status = CaseStatus.Closed then closed else None)
                                (if fields.Status = CaseStatus.Closed then payable else None)
                        ]
                    )
                )
            | _ -> Error "A completed case stage requires its retained ledger date."
