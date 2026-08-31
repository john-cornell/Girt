param(
    [Parameter(Mandatory)][string]$IsccExe,
    [Parameter(Mandatory)][string]$IssPath,
    [Parameter(Mandatory)][string]$SignToolExe,
    [Parameter(Mandatory)][string]$SignCert,
    [string]$PasswordFile
)

$ErrorActionPreference = 'Stop'
$pfx = (Resolve-Path $SignCert).Path
$st = $SignToolExe
$iss = (Resolve-Path $IssPath).Path

if ($PasswordFile -and (Test-Path -LiteralPath $PasswordFile)) {
    $pw = (Get-Content -LiteralPath $PasswordFile -Raw).Trim()
    $signDef = "`$q$st`$q sign /f `$q$pfx`$q /p `$q$pw`$q /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `$f"
}
else {
    $signDef = "`$q$st`$q sign /f `$q$pfx`$q /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `$f"
}

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $IsccExe
$psi.Arguments = "/DUSINGSIGNTOOL `"/SGirtSign=$signDef`" `"$iss`""
$psi.UseShellExecute = $false

$proc = [System.Diagnostics.Process]::Start($psi)
$proc.WaitForExit()
exit $proc.ExitCode
