# Every PSScriptAnalyzer rule runs at every severity. Anything excluded below is also listed in
# lint-exceptions.json with its reason; formatting rules are enabled so Invoke-Formatter and the
# analyzer agree.
@{
    IncludeRules = @('*')
    Severity     = @('Error', 'Warning', 'Information')
    ExcludeRules = @('PSAvoidUsingWriteHost')
    Rules        = @{
        PSPlaceOpenBrace           = @{ Enable = $true; OnSameLine = $true; NewLineAfter = $true; IgnoreOneLineBlock = $true }
        PSPlaceCloseBrace          = @{ Enable = $true; NewLineAfter = $false; IgnoreOneLineBlock = $true; NoEmptyLineBefore = $false }
        PSUseConsistentIndentation = @{ Enable = $true; IndentationSize = 4; PipelineIndentation = 'IncreaseIndentationForFirstPipeline'; Kind = 'space' }
        PSUseConsistentWhitespace  = @{
            Enable                                  = $true
            CheckInnerBrace                         = $true
            CheckOpenBrace                          = $true
            CheckOpenParen                          = $true
            CheckOperator                           = $true
            CheckPipe                               = $true
            CheckPipeForRedundantWhitespace         = $true
            CheckSeparator                          = $true
            CheckParameter                          = $true
            IgnoreAssignmentOperatorInsideHashTable = $true
        }
        PSUseCorrectCasing         = @{ Enable = $true }
    }
}
