namespace ClaimCore.Docs

/// Fixed subject IDs remain independent of the editable assurance matrix.
[<RequireQualifiedAccess>]
module ConvergenceRequiredSubjects =
    let private architecture =
        set
            [
                "assurance-architecture:fsharp-inspection"
                "assurance-architecture:historical-registration"
                "assurance-architecture:observed-graph"
                "assurance-architecture:product-ownership"
            ]

    let private browser =
        set
            [
                "assurance-browser:accessibility"
                "assurance-browser:admission"
                "assurance-browser:delivery"
                "assurance-browser:recovery"
                "assurance-browser:register"
            ]

    let private core =
        set
            [
                "assurance-core:atomic-correction"
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

    let private gui =
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

    let private persistence =
        set
            [
                "assurance-migration:frozen-applied-bytes"
                "assurance-migration:rollback"
                "assurance-migration:upgrade-002-to-006"
                "assurance-migration:upgrade-003-to-006"
                "assurance-migration:upgrade-004-to-006"
                "assurance-migration:upgrade-through-006"
                "assurance-recovery:accepted-blocks-dismiss"
                "assurance-recovery:atomic-retain-classification"
                "assurance-recovery:canonical-byte-identity"
                "assurance-recovery:capacity-and-identity"
                "assurance-recovery:dual-resolve-race"
                "assurance-recovery:evidence-attempt-settlement"
                "assurance-recovery:exact-prepare-retry"
                "assurance-recovery:identity-conflict"
                "assurance-recovery:legacy-marker-independent"
                "assurance-recovery:operation-authority"
                "assurance-recovery:provenance-first-writer"
                "assurance-recovery:resolve-dismiss-race"
                "assurance-recovery:started-uncertainty"
                "assurance-recovery:state-table"
                "assurance-recovery:settlement-co-commit"
                "assurance-runtime:admitted-outcomes"
                "assurance-runtime:disposal-drain"
                "assurance-runtime:open-cancellation-and-cleanup"
                "assurance-web:origin-and-body"
                "assurance-web:process-discovery"
                "assurance-web:scalar-boundaries"
                "assurance-web:session-refusals"
            ]

    let assurance = Set.unionMany [ architecture; browser; core; gui; persistence ]

    let branches =
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
