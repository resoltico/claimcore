function Decision-Increment($node) {
  if ($node -is [System.Management.Automation.Language.IfStatementAst]) { return $node.Clauses.Count }
  if ($node -is [System.Management.Automation.Language.SwitchStatementAst]) { return $node.Clauses.Count }
  if ($node -is [System.Management.Automation.Language.BinaryExpressionAst]) {
    if ($node.Operator -eq [System.Management.Automation.Language.TokenKind]::And -or $node.Operator -eq [System.Management.Automation.Language.TokenKind]::Or) { return 1 }
  }
  if ($node -is [System.Management.Automation.Language.LoopStatementAst] -or $node -is [System.Management.Automation.Language.CatchClauseAst] -or $node -is [System.Management.Automation.Language.TrapStatementAst]) { return 1 }
  return 0
}

function Measure-Unit($block, $lines, $parameters) {
  if ($lines -gt $limits.rules.'max-lines-per-function'[1].max) { throw 'Function span refused' }
  if ($parameters -gt $limits.rules.'max-params'[1].max) { throw 'Parameter count refused' }
  $complexity = 1
  foreach ($node in $block.FindAll({ param($node) $true }, $false)) {
    $complexity += Decision-Increment $node
  }
  if ($complexity -gt $limits.rules.complexity[1].max) { throw 'Decision complexity refused' }
}

function Check-Source($path) {
  $tokens = $null
  $errors = $null
  $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
  if ($errors.Count -ne 0) { throw 'Syntax refused' }
  foreach ($attribute in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.AttributeAst] }, $true)) {
    if ($attribute.TypeName.FullName -match '(^|\.)SuppressMessage(Attribute)?$') { throw 'Suppression refused' }
  }
  foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -or $node -is [System.Management.Automation.Language.FunctionMemberAst] }, $true)) {
    $parameters = $function.Parameters.Count
    if ($function.Body.ParamBlock) { $parameters += $function.Body.ParamBlock.Parameters.Count }
    Measure-Unit $function.Body ($function.Extent.EndLineNumber - $function.Extent.StartLineNumber + 1) $parameters
  }
  foreach ($expression in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.ScriptBlockExpressionAst] }, $true)) {
    $block = $expression.ScriptBlock
    Measure-Unit $block ($block.Extent.EndLineNumber - $block.Extent.StartLineNumber + 1) $block.ParamBlock.Parameters.Count
  }
  foreach ($block in @($ast.DynamicParamBlock, $ast.BeginBlock, $ast.ProcessBlock, $ast.EndBlock)) {
    if ($null -eq $block) { continue }
    $statements = @($block.Statements | Where-Object { $_ -isnot [System.Management.Automation.Language.FunctionDefinitionAst] -and $_ -isnot [System.Management.Automation.Language.TypeDefinitionAst] })
    if ($statements.Count -gt 0) {
      Measure-Unit $block ($statements[-1].Extent.EndLineNumber - $statements[0].Extent.StartLineNumber + 1) $ast.ParamBlock.Parameters.Count
    }
  }
}

$ErrorActionPreference = 'Stop'
try {
  $limits = Get-Content -LiteralPath $env:CLAIMCORE_PS_LIMITS -Raw | ConvertFrom-Json
  foreach ($path in ($env:CLAIMCORE_PS_FILES | ConvertFrom-Json)) { Check-Source $path }
  exit 0
} catch {
  [Console]::Error.WriteLine('Native PowerShell source policy was refused.')
  exit 1
}
