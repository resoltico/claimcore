function Assert-Entry($path, $directory) {
  $item = Get-Item -LiteralPath $path -Force
  if ($item.PSIsContainer -ne $directory -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { throw 'Entry refused' }
  $acl = Get-Acl -LiteralPath $path
  if ($directory -and $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -ne $user.Value) { throw 'Owner refused' }
  if ($acl.AreAccessRulesProtected -ne $directory) { throw 'Inheritance refused' }
  return $acl
}

function Assert-Rule($rule, $directory) {
  $sid = $rule.IdentityReference.Value
  if ($sid -ne $user.Value -and $sid -ne $system.Value) { throw 'Principal refused' }
  if ($rule.AccessControlType -ne $allow -or $rule.FileSystemRights -ne $full -or $rule.PropagationFlags -ne $none) { throw 'Permission refused' }
  if ($directory) {
    if ($rule.IsInherited -or $rule.InheritanceFlags -ne $inherit) { throw 'Root rule refused' }
  } elseif (!$rule.IsInherited -or $rule.InheritanceFlags -ne [System.Security.AccessControl.InheritanceFlags]::None) { throw 'File rule refused' }
  return $sid
}

function Assert-Access($path, $directory) {
  $acl = Assert-Entry $path $directory
  $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
  if ($rules.Count -ne 2) { throw 'Rule count refused' }
  $seen = @()
  foreach ($rule in $rules) {
    $sid = Assert-Rule $rule $directory
    if ($seen -contains $sid) { throw 'Duplicate principal refused' }
    $seen += $sid
  }
}

function Protect-Root($root) {
  $item = Get-Item -LiteralPath $root -Force
  if (!$item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { throw 'Root refused' }
  if (@(Get-ChildItem -LiteralPath $root -Force).Count -ne 0) { throw 'Root is not empty' }
  $acl = [System.Security.AccessControl.DirectorySecurity]::new()
  $acl.SetOwner($user)
  $acl.SetAccessRuleProtection($true, $false)
  foreach ($sid in @($user, $system)) {
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($sid, $full, $inherit, $none, $allow))
  }
  [System.IO.Directory]::SetAccessControl($root, $acl)
}

$ErrorActionPreference = 'Stop'
try {
  $root = $env:CLAIMCORE_SCRATCH_PATH
  $mode = $env:CLAIMCORE_SCRATCH_MODE
  $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
  $system = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
  $inherit = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
  $none = [System.Security.AccessControl.PropagationFlags]::None
  $allow = [System.Security.AccessControl.AccessControlType]::Allow
  $full = [System.Security.AccessControl.FileSystemRights]::FullControl
  if ($mode -eq 'protect') { Protect-Root $root }
  elseif ($mode -ne 'check') { throw 'Mode refused' }
  Assert-Access $root $true
  if ($mode -eq 'check') { Assert-Access (Join-Path $root 'context.json') $false }
  exit 0
} catch {
  [Console]::Error.WriteLine('Private orchestration scratch ACL was refused.')
  exit 1
}
