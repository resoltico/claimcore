namespace ClaimCore.Docs

[<RequireQualifiedAccess>]
module ConvergenceAssurance =
    let private requiredBrowser =
        set
            [
                "assurance-browser:accessibility"
                "assurance-browser:admission"
                "assurance-browser:delivery"
                "assurance-browser:recovery"
                "assurance-browser:register"
            ]

    let private requiredCore =
        set
            [
                "assurance-cancellation:execute-before-admission"
                "assurance-cancellation:query-before-read"
                "assurance-cancellation:resolve-after-attempt"
                "assurance-cancellation:resolve-before-attempt"
                "assurance-cancellation:technical-attempt-before-commit"
                "assurance-cancellation:technical-dismiss-before-commit"
                "assurance-cancellation:technical-import-before-commit"
                "assurance-cancellation:technical-prepare-before-commit"
                "assurance-cancellation:technical-read"
                "assurance-cancellation:technical-settlement-before-commit"
                "assurance-cancellation:technical-start-boundary"
                "assurance-core:business-rejection"
                "assurance-core:exact-replay"
                "assurance-core:missing-history"
                "assurance-core:typed-public-api"
                "assurance-database:owner-file-admission"
                "assurance-documentation:declared-leaf-tags"
                "assurance-documentation:native-publish-artifact"
                "assurance-hostsecurity:descriptor-relative-creation"
            ]

    let private requiredGui =
        set
            [
                "assurance-gui:case-list-detail-history"
                "assurance-gui:duplicate-load"
                "assurance-gui:duplicate-prepare"
                "assurance-gui:export-disposition"
                "assurance-gui:inspected-actions"
                "assurance-gui:metadata-command-draft"
                "assurance-gui:named-field-rejection"
                "assurance-gui:operation-reference-identity"
                "assurance-gui:prepare-retry"
                "assurance-gui:read-misses"
                "assurance-gui:recovery-import-export"
                "assurance-gui:recovery-list-inspect"
                "assurance-gui:reviewed-operation"
                "assurance-gui:session-login-logout"
                "assurance-gui:unknown-submit-delivery"
            ]

    let private requiredMigrationAndRecovery =
        set
            [
                "assurance-migration:frozen-applied-bytes"
                "assurance-migration:rollback"
                "assurance-migration:upgrade-002-to-005"
                "assurance-migration:upgrade-003-to-005"
                "assurance-migration:upgrade-004-to-005"
                "assurance-migration:upgrade-through-005"
                "assurance-recovery:accepted-blocks-dismiss"
                "assurance-recovery:atomic-retain-classification"
                "assurance-recovery:canonical-byte-identity"
                "assurance-recovery:capacity-and-identity"
                "assurance-recovery:dual-resolve-race"
                "assurance-recovery:evidence-attempt-settlement"
                "assurance-recovery:exact-prepare-retry"
                "assurance-recovery:identity-conflict"
                "assurance-recovery:legacy-marker-independent"
                "assurance-recovery:provenance-first-writer"
                "assurance-recovery:resolve-dismiss-race"
                "assurance-recovery:started-uncertainty"
                "assurance-recovery:state-table"
                "assurance-recovery:unconfirmed-settlement"
                "assurance-runtime:admitted-outcomes"
                "assurance-runtime:disposal-drain"
                "assurance-runtime:open-cancellation-and-cleanup"
                "assurance-web:origin-and-body"
                "assurance-web:process-discovery"
                "assurance-web:scalar-boundaries"
                "assurance-web:session-refusals"
            ]

    let private requiredAssurance =
        Set.unionMany [ requiredBrowser; requiredCore; requiredGui; requiredMigrationAndRecovery ]

    let private requiredBranches =
        set
            [
                "branch-cli:decimal-revision"
                "branch-cli:duplicate-properties"
                "branch-cli:hard-break"
                "branch-cli:invalid-recovery-identity"
                "branch-cli:invalid-unicode"
                "branch-cli:invalid-utf8"
                "branch-cli:oversized-input"
                "branch-cli:private-artifacts"
                "branch-cli:session-blank-eof"
                "branch-cli:trailing-document"
                "branch-cli:unknown-property-privacy"
                "branch-web:codec-bytes"
                "branch-web:host-failure-phase"
                "branch-web:private-files"
                "branch-web:raw-import-bounds"
                "branch-web:retired-routes"
                "branch-web:session-admission"
            ]

    let private live () =
        ConvergenceInventory.identities () |> Set.ofList

    let private endpointEntries root prefix relative kind =
        ConvergenceEndpointCatalog.read root relative kind
        |> Result.map (Set.map (fun identifier -> prefix + identifier))

    let private endpointSubjects root (matrix: MatrixAssessment) =
        let expectedEndpoints =
            endpointEntries
                root
                "endpoint-cli:"
                "web/src/generated/convergence/cli-v3.catalog.json"
                "CLAIMCORE_CLI_V3"
            |> Result.bind (fun cli ->
                endpointEntries
                    root
                    "endpoint-web:"
                    "web/src/generated/convergence/web-v2.catalog.json"
                    "CLAIMCORE_WEB_HTTP_V2"
                |> Result.map (Set.union cli))

        let actualEndpoints =
            matrix.EntryIds
            |> Set.filter (fun identifier ->
                identifier.StartsWith("endpoint-cli:") || identifier.StartsWith("endpoint-web:"))

        match expectedEndpoints with
        | Error message -> Error message
        | Ok expected when actualEndpoints <> expected ->
            Error
                "Every generated CLI-v3 and Web-v2 endpoint needs one exact assurance-matrix subject."
        | Ok _ -> Ok()

    let private outcomeEntries root prefix relative kind variant =
        ConvergenceOutcomeCatalog.read root relative kind variant
        |> Result.map (fun entries ->
            entries
            |> Map.toList
            |> List.map (fun (id, tags) -> prefix + id, tags)
            |> Map.ofList)

    let private outcomeSubjects root (matrix: MatrixAssessment) =
        outcomeEntries
            root
            "endpoint-cli:"
            "web/src/generated/convergence/cli-v3.catalog.json"
            "CLAIMCORE_CLI_V3"
            "cli"
        |> Result.bind (fun cli ->
            outcomeEntries
                root
                "endpoint-web:"
                "web/src/generated/convergence/web-v2.catalog.json"
                "CLAIMCORE_WEB_HTTP_V2"
                "web"
            |> Result.map (fun web -> Map.fold (fun all id tags -> Map.add id tags all) cli web))
        |> Result.bind (fun expected ->
            if matrix.OutcomeTags = expected then
                Ok()
            else
                Error
                    "Every endpoint needs its exact generated outcome-tag inventory in the assurance matrix.")

    let private cliRuntime =
        "dotnet:ClaimCore.AcceptanceTests::published CLI-v3 acceptance.[CC-CLI-001] published call and session qualify commands, queries, and recovery"

    let private cliCorpus =
        "frontend:vitest::generated contract corpora > accepts every production CLI branch and rejects malformed or cross-endpoint values"

    let private webRuntime =
        "dotnet:ClaimCore.WebTests::ClaimCore.Web.Web HTTP-v2 TestServer.[CC-WEB-001] production route map dispatches all nineteen v2 endpoints"

    let private webCorpus =
        "frontend:vitest::generated contract corpora > accepts generated Web host values and rejects every malformed or cross-endpoint value"

    let private webOutcomeTest identifier =
        let prefix = "dotnet:ClaimCore.WebTests::ClaimCore.Web.Web HTTP-v2 TestServer."

        if identifier = "command.prepare" || identifier = "command.execute" then
            prefix
            + "[CC-WEB-001] preparation and submission routes preserve definite, unknown, and exact-digest outcomes"
        elif
            identifier = "case.get"
            || identifier = "case.list"
            || identifier = "case.history"
            || identifier = "operation.observe"
        then
            prefix
            + "[CC-WEB-001] case and operation routes preserve found, absent, rejected, failed, and cancelled core outcomes"
        elif identifier.StartsWith("recovery.import") then
            prefix
            + "[CC-WEB-001] recovery preview and retain routes preserve source digest, refusal, and uncertainty outcomes"
        elif identifier.StartsWith("recovery.") then
            prefix
            + "[CC-WEB-001] recovery list, inspect, resolve, dismiss, and export preserve typed lifecycle outcomes"
        else
            prefix
            + "[CC-WEB-001] session login and logout revoke admission through real cookies and antiforgery"

    let private endpointEvidence (matrix: MatrixAssessment) =
        let has required tests =
            required |> List.forall (fun id -> List.contains id tests)

        let invalid =
            matrix.EntryTests
            |> Map.exists (fun id tests ->
                if id.StartsWith("endpoint-cli:") then
                    not (has [ cliRuntime; cliCorpus ] tests)
                elif id.StartsWith("endpoint-web:") then
                    let identifier = id.Substring("endpoint-web:".Length)
                    not (has [ webRuntime; webCorpus; webOutcomeTest identifier ] tests)
                else
                    false)

        if invalid then
            Error
                "Endpoint assurance must retain its exact published or TestServer runtime and wire-corpus evidence."
        else
            Ok()

    let private validate
        root
        (baseline: BaselineAssessment)
        (lineage: LineageAssessment)
        (matrix: MatrixAssessment)
        =
        let live = live ()

        match
            endpointSubjects root matrix, outcomeSubjects root matrix, endpointEvidence matrix
        with
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message -> Error message
        | Ok(), Ok(), Ok() when not (Set.isSubset requiredAssurance matrix.EntryIds) ->
            Error
                "Every registered core, recovery, cancellation, migration, and GUI assurance subject is required."
        | Ok(), Ok(), Ok() when not (Set.isSubset requiredBranches matrix.EntryIds) ->
            Error "Every registered CLI-v3 and Web-v2 protocol branch needs an assurance subject."
        | Ok(), Ok(), Ok() when
            lineage.BaselineSha256 <> ConvergenceBaseline.expectedSha256
            || matrix.BaselineSha256 <> ConvergenceBaseline.expectedSha256
            ->
            Error "Lineage and matrix must bind the immutable v0.1 baseline digest."
        | Ok(), Ok(), Ok() when baseline.Identities <> (lineage.Entries |> Map.keys |> Set.ofSeq) ->
            Error "Every baseline test identity requires exactly one lineage entry."
        | Ok(), Ok(), Ok() when
            lineage.Entries
            |> Map.exists (fun _ replacements ->
                replacements |> List.exists (fun identity -> not (live.Contains(identity))))
            ->
            Error "Every lineage replacement identity must exist in the live inventory."
        | Ok(), Ok(), Ok() when
            matrix.Tests |> Seq.exists (fun identity -> not (live.Contains(identity)))
            ->
            Error "The assurance matrix references a test absent from the live inventory."
        | Ok(), Ok(), Ok() when not (Set.isSubset live matrix.Tests) ->
            Error "Every live test identity must appear in the assurance matrix."
        | Ok(), Ok(), Ok() ->
            Ok
                $"Convergence assurance passed: {baseline.SourceCount} baseline sources, {baseline.Total} immutable tests, {live.Count} live tests."

    let check root baselinePath lineagePath matrixPath =
        ConvergenceBaseline.read root baselinePath
        |> Result.bind (fun baseline ->
            ConvergenceLineage.read root lineagePath
            |> Result.bind (fun lineage ->
                ConvergenceMatrix.read root matrixPath
                |> Result.bind (fun matrix -> validate root baseline lineage matrix)))
