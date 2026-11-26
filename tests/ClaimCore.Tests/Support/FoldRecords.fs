module internal ClaimCore.Tests.FoldRecords

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Tasks
open ClaimCore.RecordFormat
open ClaimCore.Application.Execution

let required =
    function
    | Ok value -> value
    | Error error -> failtestf "The accepted record fixture failed: %A" error

let installation = Guid.Parse "90000000-0000-4000-8000-000000000001"

let operation number =
    Guid.Parse(sprintf "019aa6a0-0000-7000-8000-%012d" number)
    |> OperationId.create
    |> Option.get

let principal = PrincipalKey.human "https://issuer.example/" "owner" |> required

let input target value =
    TaskInputId.Target target, InputValue.Text value

let inputs values =
    InputMap.create (Map.ofList values) Map.empty |> Option.get

let fields values =
    values
    |> List.map (fun (field, value) -> input (InputTarget.Field field) value)
    |> inputs

let profile name =
    inputs [ input InputTarget.ProfileDisplayName name ]

let registration name =
    inputs
        [
            input InputTarget.PrincipalKind "HUMAN"
            input InputTarget.PrincipalIssuer "https://issuer.example/"
            input InputTarget.PrincipalSubject name
            input InputTarget.ProfileDisplayName name
        ]

let private ownerInputsFor policy =
    let key, token =
        input InputTarget.CaseReferencePolicy (CaseReferencePolicy.token policy)

    let values = (registration "owner").Values |> Map.add key token
    let roles = [ "CASE_HANDLER"; "PAYMENT_RECORDER"; "DATA_STEWARD" ]

    let rows =
        roles
        |> List.map (fun role ->
            Map.ofList
                [
                    input InputTarget.Role role
                    TaskInputId.Scope, InputValue.Text "INSTALLATION"
                ])

    InputMap.create values (Map.ofList [ TaskCollectionId.PeopleGrants, rows ])
    |> Option.get

let ownerInputs = ownerInputsFor CaseReferencePolicy.CustomAllowed

let opening reference =
    fields
        [
            BusinessField.CaseReference, reference
            BusinessField.IncidentDate, "2026-01-01"
            BusinessField.ReceivedDate, "2026-01-02"
            BusinessField.IncidentCountry, "LV"
            BusinessField.PolicyNumber, "Claimant"
            BusinessField.InsurerClaimReference, "Insurer"
            BusinessField.ClaimedAmount, "1000"
            BusinessField.ClaimedCurrency, "EUR"
        ]

let private decide policy state (context: TaskContext) selected binding inputs =
    match selected with
    | Task.Opening opening ->
        let id = CaseId.ofOperationId context.Stamp.OperationId
        let before = Map.tryFind id state.Families |> Option.map DecisionFacts.fromReplay

        let environment =
            Fold.familyEnvironment
                policy
                state
                context.Decision.Principal
                context.Decision.Maintenance
                (Some id)

        opening.Admit context environment before binding inputs
        |> Result.bind (fun facts ->
            let candidate =
                match facts.Intent with
                | OpeningIntent.Custom reference -> reference
                | OpeningIntent.Automatic ->
                    invalidOp "Recorded opening requires its accepted assignment."

            Case.Open.accept candidate (environment.ReferenceOwner candidate) facts)
    | _ ->
        (Fold.snapshot
            policy
            state
            context.Decision.Principal
            context.Decision.Maintenance
            selected
            binding
            (Some inputs))
            .Decide
            context
            binding
            inputs

let buildWith mutate (state: FoldState) number requester token binding values =
    let id = operation number

    let selected =
        Registry.find (TaskId.ofLiteral token)
        |> Option.defaultWith (fun () -> failtestf "Unknown fixture task token: %s" token)

    let declaration = Task.declaration selected
    let sequence = Sequence.successor state.Head.Sequence

    let authority =
        if declaration.Exposure = TaskExposure.Operator then
            Authority.Operator
        else
            state.People
            |> Map.values
            |> Seq.tryFind (fun person -> person.Actor.Principal = requester)
            |> Option.map _.Id
            |> Option.defaultValue (ActorId.ofOperationId id)
            |> Authority.Actor

    let observed = (OperationId.issuedAt id).AddSeconds 1.

    let date =
        if declaration.Aggregate = AggregateKind.Family then
            Some(DateOnly(2026, 11, 16))
        else
            None

    let context =
        {
            TaskContext.Decision =
                {
                    BusinessDate = date
                    ObservedAt = observed
                    Principal = requester
                    Access = Fold.requester state requester
                    Maintenance = false
                    Lifetime = DecisionLifetime.Recorded
                }
            Stamp =
                {
                    OperationId = id
                    Authority = authority
                    RecordedAt = observed
                    Position = EffectPosition.create sequence 0 |> Option.get
                }
        }

    let changes =
        decide CaseReferencePolicy.CustomAllowed state context selected binding values
        |> required
        |> mutate

    let header =
        {
            OperationHeader.InstallationId = installation
            Sequence = sequence
            PreviousHash = state.Head.EntryHash
            OperationId = id
            Authority = authority
            RecordedAt = observed
            BusinessDate = date
        }

    let record =
        RetainedOperation.create header declaration.Id binding values changes (fun () ->
            Array.create 32 (byte number))
        |> required

    let next = Fold.apply state changes |> required

    record,
    { next with
        Head = record.Root.Position
    }

let build state number requester token binding values =
    buildWith id state number requester token binding values

let ownerWithPolicy policy =
    build
        Fold.initial
        1
        principal
        "people.provisionOwner"
        (Binding.PeopleNew Revision.zero)
        (ownerInputsFor policy)

let owner () =
    ownerWithPolicy CaseReferencePolicy.CustomAllowed

let private restoreCompartments transform (record: RetainedOperation) =
    let root = record.Root

    OperationRoot.restore
        root.Header
        (TaskId.token root.Structure.Task)
        (OperationRoot.structureBytes root)
        (OperationRoot.effectBytes root)
        (root.Structure.Compartments |> List.map transform)
        root.Hash
    |> required
    |> RetainedOperation.restore
    |> required

let restore record = restoreCompartments id record

let clear record =
    restoreCompartments Compartment.withoutContent record

let scanWithPolicy policy records =
    records
    |> List.fold
        (fun state record ->
            state |> Result.bind (fun state -> Fold.append installation policy state record))
        (Ok Fold.initial)

let scan records =
    scanWithPolicy CaseReferencePolicy.CustomAllowed records
