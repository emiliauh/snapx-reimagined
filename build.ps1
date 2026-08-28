#!/usr/bin/env powershell

[CmdletBinding()]
Param(
    [Parameter(Position=0,Mandatory=$false,ValueFromRemainingArguments=$true)]
    [string[]]$BuildArguments
)

Write-Output "PowerShell $($PSVersionTable.PSEdition) version $($PSVersionTable.PSVersion)"

Set-StrictMode -Version 2.0; $ErrorActionPreference = "Stop"; $ConfirmPreference = "None"; trap { Write-Error $_ -ErrorAction Continue; exit 1 }
$PSScriptRoot = Split-Path $MyInvocation.MyCommand.Path -Parent

###########################################################################
# CONFIGURATION
###########################################################################

$BuildProjectFile = "$PSScriptRoot\build\build.csproj"
$TempDirectory = "$PSScriptRoot\build\temp"

$DotNetGlobalFile = "$PSScriptRoot\\global.json"
$DotNetInstallUrl = "https://raw.githubusercontent.com/dotnet/install-scripts/47940ac9fc30a2f2dd19167165d0bb0774625f67/src/dotnet-install.ps1"
$DotNetInstallSha256 = "3bb07bc8025211836c1e4f9d3f6a044e55b1fb6eec518a6c78851d04e210442b"
$RequestedDotNetVersion = if (Test-Path variable:DotNetVersion) {
    Get-Variable -Name DotNetVersion -ValueOnly
} else {
    (Get-Content -Raw $DotNetGlobalFile | ConvertFrom-Json).sdk.version
}

$env:AVALONIA_TELEMETRY_OPTOUT = 1
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = "true"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$env:DOTNET_NOLOGO = 1

###########################################################################
# EXECUTION
###########################################################################

function ExecSafe([scriptblock] $cmd) {
    & $cmd
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

# If dotnet CLI is installed globally and it matches requested version, use for execution
if ($null -ne (Get-Command "dotnet" -ErrorAction SilentlyContinue) -and `
     $(dotnet --version) -and $LASTEXITCODE -eq 0) {
    $env:DOTNET_EXE = (Get-Command "dotnet").Path
}
else {
    # Download install script
    $DotNetInstallFile = "$TempDirectory\dotnet-install.ps1"
    New-Item -ItemType Directory -Path $TempDirectory -Force | Out-Null
    Invoke-WebRequest -Uri $DotNetInstallUrl -OutFile $DotNetInstallFile
    $ActualDotNetInstallSha256 = (Get-FileHash -Path $DotNetInstallFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ActualDotNetInstallSha256 -ne $DotNetInstallSha256) {
        Remove-Item -Force $DotNetInstallFile
        throw "The downloaded .NET installer did not match its expected SHA-256."
    }



    # Use the repository's exact SDK version rather than a moving channel.
    $DotNetDirectory = "$TempDirectory\dotnet-win"
    if ([string]::IsNullOrWhiteSpace($RequestedDotNetVersion)) {
        throw "The SDK version is missing from global.json."
    }
    ExecSafe { & powershell $DotNetInstallFile -InstallDir $DotNetDirectory -Version $RequestedDotNetVersion -NoPath }
    $env:DOTNET_EXE = "$DotNetDirectory\dotnet.exe"
    $env:PATH = "$DotNetDirectory;$env:PATH"
}

Write-Output "Microsoft (R) .NET SDK version $(& $env:DOTNET_EXE --version)"

ExecSafe { & $env:DOTNET_EXE build $BuildProjectFile -nodeReuse:false -p:UseSharedCompilation=false -p:NoWarn=true -nologo -clp:NoSummary --verbosity quiet }
ExecSafe { & $env:DOTNET_EXE run --project $BuildProjectFile --no-build -- $BuildArguments }
