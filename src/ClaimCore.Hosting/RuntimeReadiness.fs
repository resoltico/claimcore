namespace ClaimCore.Hosting

open ClaimCore.Witness

module internal RuntimeReadiness =
    let observe (safety: RuntimeSafetySupervisor) requireAuditTrust ct =
        task {
            try
                use! _fence = safety.AcquireReadFence(ct)
                requireAuditTrust ()
                let! state = safety.CurrentUseState(ct)

                let! ready =
                    task {
                        if
                            state.Scope <> InstallationUseScope.RealData
                            || state.Phase <> InstallationUsePhase.Active
                        then
                            return false
                        else
                            try
                                do! safety.RequireCaseMutation(ct)
                                requireAuditTrust ()
                                return true
                            with _ ->
                                return false
                    }

                return
                    InstallationUse.scopeToken state.Scope,
                    InstallationUse.phaseToken state.Phase,
                    ready
            with _ ->
                return "UNKNOWN", "QUARANTINED", false
        }
