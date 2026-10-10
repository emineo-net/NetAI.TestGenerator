<#
.SYNOPSIS
    Erstellt ein neues Release: Version setzen, committen, taggen, pushen.

.PARAMETER Version
    Die neue Version im Format MAJOR.MINOR.PATCH (z. B. 1.0.1).

.EXAMPLE
    .\release.ps1 -Version 1.0.1
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

# ==================== KONFIGURATION ====================
$CspojRelativePath = "src\Libraries\NetAI.TestGenerator.Tasks\NetAI.TestGenerator.Tasks.csproj"
$RemoteName        = "origin"
$MainBranch        = "main"
$TagPrefix         = "v"
# =======================================================

$ErrorActionPreference = "Stop"

function Write-Step($msg) {
    Write-Host ""
    Write-Host "=== $msg ===" -ForegroundColor Cyan
}

function Write-Ok($msg)   { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "  [FEHLER] $msg" -ForegroundColor Red }

# ---------------------------------------------------------
# 1. Validierung der Eingabe
# ---------------------------------------------------------
Write-Step "Validiere Version"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Err "Ungueltiges Versionsformat: '$Version'"
    Write-Host "         Erwartet: MAJOR.MINOR.PATCH, z. B. 1.0.1"
    exit 1
}
Write-Ok "Format gueltig: $Version"

# ---------------------------------------------------------
# 2. Ins Repo-Root wechseln (Skript-Verzeichnis)
# ---------------------------------------------------------
$RepoRoot = $PSScriptRoot
Set-Location $RepoRoot
Write-Ok "Arbeitsverzeichnis: $RepoRoot"

# ---------------------------------------------------------
# 3. Pruefen, ob wir in einem Git-Repo sind
# ---------------------------------------------------------
if (-not (Test-Path ".git")) {
    Write-Err "Kein Git-Repository in $RepoRoot gefunden."
    exit 1
}

# ---------------------------------------------------------
# 4. Pruefen, ob Working Tree sauber ist
# ---------------------------------------------------------
Write-Step "Pruefe Working Tree"
$status = git status --porcelain
if ($status) {
    Write-Err "Working Tree ist nicht sauber. Bitte zuerst committen oder stashen:"
    Write-Host $status
    exit 1
}
Write-Ok "Working Tree ist sauber"

# ---------------------------------------------------------
# 5. Pruefen, ob Tag bereits existiert (lokal und remote)
# ---------------------------------------------------------
Write-Step "Pruefe Tag v$Version"

git fetch --tags --quiet

$localTag = git tag -l "$TagPrefix$Version"
if ($localTag) {
    Write-Err "Tag $TagPrefix$Version existiert bereits lokal."
    exit 1
}

$remoteTag = git ls-remote --tags $RemoteName "refs/tags/$TagPrefix$Version"
if ($remoteTag) {
    Write-Err "Tag $TagPrefix$Version existiert bereits auf $RemoteName."
    exit 1
}
Write-Ok "Tag $TagPrefix$Version ist frei"

# ---------------------------------------------------------
# 6. .csproj pruefen und Version ersetzen
# ---------------------------------------------------------
Write-Step "Aktualisiere .csproj"

if (-not (Test-Path $CspojRelativePath)) {
    Write-Err "csproj nicht gefunden: $CspojRelativePath"
    exit 1
}

$cspojFullPath = Join-Path $RepoRoot $CspojRelativePath
$content = Get-Content $cspojFullPath -Raw

if ($content -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') {
    Write-Err "Kein <VersionPrefix> in $CspojRelativePath gefunden."
    exit 1
}

$oldVersion = $Matches[1]
Write-Host "  Alte Version: $oldVersion"
Write-Host "  Neue Version: $Version"

if ($oldVersion -eq $Version) {
    Write-Err "Die neue Version entspricht der alten. Nichts zu tun."
    exit 1
}

# Pruefen, ob die neue Version wirklich groesser ist
$oldParts = $oldVersion.Split('.') | ForEach-Object { [int]$_ }
$newParts = $Version.Split('.')   | ForEach-Object { [int]$_ }

$isNewer = $false
for ($i = 0; $i -lt 3; $i++) {
    if ($newParts[$i] -gt $oldParts[$i]) { $isNewer = $true; break }
    if ($newParts[$i] -lt $oldParts[$i]) { break }
}

if (-not $isNewer) {
    Write-Err "Die neue Version ($Version) ist nicht groesser als die alte ($oldVersion)."
    exit 1
}
Write-Ok "Version ist neuer als $oldVersion"

# Backup anlegen fuer Rollback
$backupPath = "$cspojFullPath.bak"
Copy-Item $cspojFullPath $backupPath -Force

$newContent = $content -replace '<VersionPrefix>[^<]+</VersionPrefix>', "<VersionPrefix>$Version</VersionPrefix>"
Set-Content -Path $cspojFullPath -Value $newContent -NoNewline -Encoding UTF8

Write-Ok ".csproj aktualisiert (Backup: $backupPath)"

# ---------------------------------------------------------
# 7. Bestaetigung einholen
# ---------------------------------------------------------
Write-Step "Bestaetigung"
Write-Host ""
Write-Host "  Repository : $RepoRoot"
Write-Host "  Branch     : $MainBranch"
Write-Host "  Version    : $oldVersion -> $Version"
Write-Host "  Tag        : $TagPrefix$Version"
Write-Host ""
$confirm = Read-Host "Release jetzt durchfuehren? (j/n)"
if ($confirm -notin @("j", "J", "y", "Y")) {
    Write-Warn "Abgebrochen durch Benutzer. Stelle .csproj wieder her."
    Move-Item $backupPath $cspojFullPath -Force
    exit 0
}

# ---------------------------------------------------------
# 8. Commit + Push
# ---------------------------------------------------------
try {
    Write-Step "Committe Aenderung"

    git add $CspojRelativePath
    git commit -m "Release $Version"
    Write-Ok "Commit erstellt"

    Write-Step "Push nach $RemoteName/$MainBranch"
    git push $RemoteName $MainBranch
    Write-Ok "Push erfolgreich"

    Write-Step "Erstelle Tag $TagPrefix$Version"
    git tag -a "$TagPrefix$Version" -m "Release Version $Version"
    Write-Ok "Tag erstellt"

    Write-Step "Push Tag nach $RemoteName"
    git push $RemoteName "$TagPrefix$Version"
    Write-Ok "Tag gepusht"

    # Backup entfernen bei Erfolg
    Remove-Item $backupPath -Force -ErrorAction SilentlyContinue
}
catch {
    Write-Err "Fehler waehrend des Release: $_"
    Write-Warn "Rolle .csproj zurueck..."
    if (Test-Path $backupPath) {
        Move-Item $backupPath $cspojFullPath -Force
        Write-Host "  .csproj wiederhergestellt."
    }
    Write-Warn "Pruefe den Git-Zustand manuell (git status, git log)."
    exit 1
}

# ---------------------------------------------------------
# 9. Abschluss
# ---------------------------------------------------------
Write-Step "Fertig!"
Write-Host ""
Write-Host "  Version $Version wurde veroeffentlicht." -ForegroundColor Green
Write-Host "  GitHub Actions: https://github.com/emineo-net/NetAI.TestGenerator/actions"
Write-Host "  NuGet-Paket:    https://www.nuget.org/packages/NetAI.TestGenerator.Tasks"
Write-Host ""
Write-Host "  Es dauert 1-5 Minuten, bis der Workflow durchgelaufen ist."
Write-Host ""