param(
    [Parameter(Mandatory)][string]$SignTool,
    [Parameter(Mandatory)][string]$Cert,
    [Parameter(Mandatory)][string]$Target,
    [string]$PasswordFile
)

$ErrorActionPreference = 'Stop'
$certPath = (Resolve-Path $Cert).Path
$targetPath = (Resolve-Path $Target).Path

$stArgs = @('sign', '/f', $certPath, '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
if ($PasswordFile -and (Test-Path -LiteralPath $PasswordFile)) {
    $pw = (Get-Content -LiteralPath $PasswordFile -Raw).Trim()
    $stArgs += @('/p', $pw)
}
$stArgs += $targetPath

$p = Start-Process -FilePath $SignTool -ArgumentList $stArgs -Wait -NoNewWindow -PassThru
exit $p.ExitCode
