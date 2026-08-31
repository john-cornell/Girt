param(
    # When set, password is written here as UTF-8 without BOM (avoids cmd redirect encoding issues).
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

$pw = $env:CODE_SIGNING_PFX_PASSWORD
if ([string]::IsNullOrWhiteSpace($pw)) { $pw = $env:CERTIFICATE_PASSWORD }
if ([string]::IsNullOrWhiteSpace($pw)) { $pw = $env:SIGNING_PFX_PASSWORD }

$pwFile = $env:CODE_SIGNING_PFX_PASSWORD_FILE
if ([string]::IsNullOrWhiteSpace($pwFile)) {
    $pwFile = Join-Path $dir 'code-signing-password.txt'
}
elseif (-not [System.IO.Path]::IsPathRooted($pwFile)) {
    $pwFile = Join-Path $dir $pwFile
}
if ([string]::IsNullOrWhiteSpace($pw) -and (Test-Path -LiteralPath $pwFile)) {
    $pw = (Get-Content -LiteralPath $pwFile -Raw).Trim()
}

if ([string]::IsNullOrWhiteSpace($pw)) {
    [Console]::Error.WriteLine(@"
No PFX password found. Set one of:

  set CODE_SIGNING_PFX_PASSWORD=your-export-password
  set CERTIFICATE_PASSWORD=...   or   set SIGNING_PFX_PASSWORD=...

Or create installer\code-signing-password.txt (one line, gitignored).

If the PFX truly has no password: set ALLOW_EMPTY_PFX_PASSWORD=1
  (build-setup.bat will skip loading a password and omit /p for signtool.)
"@)
    exit 1
}

if ($OutFile) {
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($OutFile, $pw, $utf8NoBom)
}
else {
    [Console]::Out.WriteLine($pw)
}
exit 0
