param([Parameter(Mandatory)][string]$ProbeFile)
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $ProbeFile).Path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$names = 'Get-Listeners','Get-ListenerInfo','Get-Alive','Get-Subtree','New-TreeEntry','Wait-Stopped'
foreach ($node in $ast.FindAll({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $names -contains $n.Name}, $false)) {
    . ([scriptblock]::Create($node.Extent.Text))
}
$statements = @($ast.FindAll({param($n)
    $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -in '$after','$report.afterStop'
}, $true))
if ($statements.Count -ne 2) { throw 'Expected the two afterStop statements.' }

# Replace the Windows enumeration only. No Basalt, listener, or signal is started.
function Get-NetTCPConnection { $script:sample }
function Get-CimInstance { @() }
$Port = 8080
$tree = @()
$failures = 0
foreach ($count in 0,1,2) {
    $script:sample = @(for ($i = 0; $i -lt $count; $i++) {
        [pscustomobject]@{ OwningProcess = $PID; LocalPort = $Port; State = 'Listen'; LocalAddress = "fixture-$i" }
    })
    $report = [ordered]@{}
    try {
        foreach ($s in $statements) { . ([scriptblock]::Create($s.Extent.Text)) }
        $owners = $report.afterStop.portOwners
        if ($owners -isnot [array] -or $owners.Count -ne $count) { throw 'portOwners must be an array of the observed size.' }
        if ($report.afterStop.portStillListening -ne ($count -gt 0)) { throw 'Incorrect listener state.' }
        if (@($report.afterStop.processesLeft).Count -ne 0) { throw 'Unexpected remaining process.' }
        Write-Output "PASS $count listener(s)"
    } catch {
        $failures++
        Write-Output "FAIL $count listener(s) -- $($_.Exception.Message)"
    }
}
if ($failures) { exit 1 }

# Port and process must both be absent in one sample. Keep verified descendants across samples.
$born = Get-Date
$parent = [pscustomobject]@{ ProcessId = 101; ParentProcessId = 1; Name = 'fixture'; CreationDate = $born }
$child = [pscustomobject]@{ ProcessId = 102; ParentProcessId = 101; Name = 'child'; CreationDate = $born.AddSeconds(1) }
$script:processes = @($parent, $child)
function Get-CimInstance { $script:processes }
$tracked = [System.Collections.ArrayList]@(New-TreeEntry $parent 0 '실행 창')
if (@(Get-Alive $tracked).Count -ne 2) { throw 'A verified new child was not tracked.' }
$script:processes = @($child)
if (@(Get-Alive $tracked).Count -ne 1) { throw 'A previously observed child was lost.' }
Write-Output 'PASS verified descendant retained'
$script:tick = 0
function Get-NetTCPConnection {
    $script:tick++
    $script:processes = if ($script:tick -eq 1) { @($child) } else { @() }
    if ($script:tick -eq 2) { [pscustomobject]@{ State = 'Listen'; LocalPort = $Port; OwningProcess = $PID } }
}
function Start-Sleep { }
if (-not (Wait-Stopped $tracked (Get-Date) 2 ([ordered]@{})) -or $script:tick -ne 3) { throw 'Stop required simultaneous empty observations.' }
Write-Output 'PASS port and process clear in the same sample'
