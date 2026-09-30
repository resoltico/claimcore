[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $Assembly,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

# Runs Check-TestDiscovery.ps1 for each assembly in its own process, all at once. Separate processes,
# not runspaces of one process: concurrent native invocations inside one PowerShell process share
# state that is not safe to mutate from several threads.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$check = Join-Path $PSScriptRoot 'Check-TestDiscovery.ps1'
$runs = foreach ($name in $Assembly) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new('pwsh')
    foreach ($argument in @('-NoProfile', '-File', $check, '-Assembly', $name, '-Configuration', $Configuration)) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($startInfo)
    [PSCustomObject]@{
        Assembly = $name
        Process = $process
        Output = $process.StandardOutput.ReadToEndAsync()
        Errors = $process.StandardError.ReadToEndAsync()
    }
}

$failed = @()
foreach ($run in $runs) {
    $run.Process.WaitForExit()
    Write-Host $run.Output.GetAwaiter().GetResult().TrimEnd()
    $errors = $run.Errors.GetAwaiter().GetResult().TrimEnd()
    if ($errors.Length -gt 0) { [Console]::Error.WriteLine($errors) }
    if ($run.Process.ExitCode -ne 0) { $failed += $run.Assembly }
    $run.Process.Dispose()
}
if ($failed.Count -gt 0) { throw "Test discovery failed for: $($failed -join ', ')" }
