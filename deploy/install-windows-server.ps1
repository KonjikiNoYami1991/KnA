<#
KnA web edition on Windows as a service (starts with Windows, no user logged in, restarts by itself after a crash).

  Install / update:  irm https://raw.githubusercontent.com/KonjikiNoYami1991/KnA/master/deploy/install-windows-server.ps1 | iex
                     (in an administrator PowerShell; or save the file and run it with parameters)
  With options:      .\install-windows-server.ps1 -MediaRoots "D:\Video;\\nas\film" -Password "secret" -Port 8585
  Remove:            .\install-windows-server.ps1 -Uninstall          (keeps settings and data unless -Purge)

It downloads the complete server package (KnA-Server-Windows-x64.zip: FFmpeg, fdkaac, GPU encoders included),
installs it in -InstallDir, registers the "KnA" service, opens the port in the Windows firewall and starts it.
An update keeps tools, models and data (the program files only are replaced).

Network shares: a service running as LocalSystem cannot see mapped drive letters (Z:) and usually has no rights on
a NAS. Use UNC paths (\\nas\film) and run the service as a user that can read/write them: -ServiceUser "PC\name".
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramData 'KnA-server'),
    [int]$Port = 8585,
    [string]$MediaRoots = '',
    [string]$ReadOnlyRoots = '',
    [string]$Password = '',
    [string]$Version = 'latest',
    [string]$ServiceUser = '',
    [switch]$AllowAdvanced,
    [switch]$Uninstall,
    [switch]$Purge
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times faster without the progress bar
$Repo = 'KonjikiNoYami1991/KnA'
$Service = 'KnA'
$It = (Get-Culture).TwoLetterISOLanguageName -eq 'it'
function T([string]$en, [string]$it) { if ($It) { $it } else { $en } }
function Log([string]$m) { Write-Host "[KnA] $m" -ForegroundColor Magenta }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw (T 'Run this script in an administrator PowerShell.' 'Esegui lo script in un PowerShell da amministratore.')
}
if ([Environment]::Is64BitOperatingSystem -eq $false) { throw (T 'KnA needs 64-bit Windows.' 'KnA richiede Windows a 64 bit.') }

$existing = Get-Service -Name $Service -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    if ($existing) {
        if ($existing.Status -ne 'Stopped') { Stop-Service $Service -Force }
        sc.exe delete $Service | Out-Null
    }
    Get-NetFirewallRule -DisplayName 'KnA web' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if (Test-Path $InstallDir) {
        if ($Purge) { Remove-Item $InstallDir -Recurse -Force }
        else { Get-ChildItem $InstallDir -Exclude data, tools, ai | Remove-Item -Recurse -Force }
    }
    Log (T "Removed$(if (-not $Purge) { ' (settings, tools and models kept in ' + $InstallDir + ')' })." "Rimosso$(if (-not $Purge) { ' (impostazioni, strumenti e modelli restano in ' + $InstallDir + ')' }).")
    return
}

# ---------------------------------------------------------------- download
$api = if ($Version -eq 'latest') { "https://api.github.com/repos/$Repo/releases/latest" } else { "https://api.github.com/repos/$Repo/releases/tags/v$($Version.TrimStart('v'))" }
$release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'KnA-installer' }
$asset = $release.assets | Where-Object name -eq 'KnA-Server-Windows-x64.zip' | Select-Object -First 1
if (-not $asset) { throw (T "Release $($release.tag_name) has no Windows server package." "La release $($release.tag_name) non ha il pacchetto server per Windows.") }
$tmp = Join-Path $env:TEMP ("kna-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Log (T "Downloading KnA $($release.tag_name)…" "Download di KnA $($release.tag_name)…")
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile (Join-Path $tmp 'kna.zip') -Headers @{ 'User-Agent' = 'KnA-installer' }
    Expand-Archive -Path (Join-Path $tmp 'kna.zip') -DestinationPath (Join-Path $tmp 'x') -Force
    $src = Join-Path $tmp 'x\KnA-server'
    if (-not (Test-Path (Join-Path $src 'kna-server.exe'))) { throw (T 'The package does not contain kna-server.exe.' 'Il pacchetto non contiene kna-server.exe.') }

    if ($existing -and $existing.Status -ne 'Stopped') { Log (T 'Stopping the service…' 'Arresto del servizio…'); Stop-Service $Service -Force }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $update = Test-Path (Join-Path $InstallDir 'kna-server.exe')
    # Program files always; tools, models and data only on the first install (an update never downgrades them
    # or touches settings, presets and the queue).
    Get-ChildItem $src | ForEach-Object {
        if ($update -and $_.Name -in @('tools', 'ai', 'data')) { return }
        Copy-Item $_.FullName -Destination $InstallDir -Recurse -Force
    }
    Log (T "KnA $($release.tag_name) installed in $InstallDir." "KnA $($release.tag_name) installato in $InstallDir.")
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

# ---------------------------------------------------------------- service
if (-not $existing) {
    New-Service -Name $Service -BinaryPathName "`"$(Join-Path $InstallDir 'kna-server.exe')`"" -DisplayName 'KnA - Konjiki no Autoencoder (web)' `
        -Description (T 'KnA web edition: video encoding queue with a browser interface.' 'KnA edizione web: coda di codifica video con interfaccia nel browser.') -StartupType Automatic | Out-Null
    # restart after 10 s, 30 s, 60 s when it stops unexpectedly
    sc.exe failure $Service reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
    # also restart after a clean exit with an error code: KnA exits with 75 to start its freshly installed update
    sc.exe failureflag $Service 1 | Out-Null
}
if ($ServiceUser) {
    $cred = Get-Credential -UserName $ServiceUser -Message (T 'Password of the account the service runs as' "Password dell'account con cui gira il servizio")
    sc.exe config $Service obj= $cred.UserName password= $cred.GetNetworkCredential().Password | Out-Null
    # settings, queue, logs and components live in the program folder (portable layout): that user writes there
    icacls $InstallDir /grant "$($cred.UserName):(OI)(CI)M" /T /Q | Out-Null
}

# Settings are environment variables of the service (registry), the same KNA_* names as Docker and Linux.
$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$Service"
$envs = @{}
foreach ($line in @((Get-ItemProperty -Path $key -Name Environment -ErrorAction SilentlyContinue).Environment)) {
    if ($line -match '^([^=]+)=(.*)$') { $envs[$Matches[1]] = $Matches[2] }
}
$envs['KNA_URLS'] = "http://0.0.0.0:$Port"
if ($MediaRoots) { $envs['KNA_MEDIA_ROOTS'] = $MediaRoots }
if ($ReadOnlyRoots) { $envs['KNA_READONLY_ROOTS'] = $ReadOnlyRoots }
if ($Password) { $envs['KNA_PASSWORD'] = $Password }
if ($AllowAdvanced) { $envs['KNA_ALLOW_ADVANCED'] = 'true' }
if (-not $envs.ContainsKey('KNA_MEDIA_ROOTS')) {
    $envs['KNA_MEDIA_ROOTS'] = (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -gt 0 -and $_.Root -notlike "$env:SystemDrive*" } | ForEach-Object Root) -join ';'
    if (-not $envs['KNA_MEDIA_ROOTS']) { $envs['KNA_MEDIA_ROOTS'] = [Environment]::GetFolderPath('MyVideos') }
}
Set-ItemProperty -Path $key -Name Environment -Type MultiString -Value ($envs.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$($_.Value)" })

if (-not (Get-NetFirewallRule -DisplayName 'KnA web' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'KnA web' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Private, Domain | Out-Null
} else {
    Set-NetFirewallRule -DisplayName 'KnA web' -LocalPort $Port
}

Start-Service $Service
$ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1).IPAddress
Log ((T 'Done!' 'Fatto!') + "  →  http://$(if ($ip) { $ip } else { 'localhost' }):$Port")
Log ((T 'Video folders' 'Cartelle video') + ": $($envs['KNA_MEDIA_ROOTS'])")
if (-not $envs.ContainsKey('KNA_PASSWORD')) { Log (T 'No -Password: the first person who opens KnA creates the administrator account.' "Nessuna -Password: il primo che apre KnA crea l'account amministratore.") }
Log (T 'Update: run this script again. Settings: run it again with other parameters.' 'Aggiornamento: riesegui lo script. Impostazioni: rieseguilo con altri parametri.')
