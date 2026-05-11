param(
    [string]$ProjectFile = "SocietalReputation/SocietalReputation.csproj",
    [string]$PluginMasterFile = "pluginmaster.json",
    [string]$ArtifactZip = "SocietalReputation/bin/Release/SocietalReputation/latest.zip"
)

$ErrorActionPreference = "Stop"

function Get-NormalizedAssemblyVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    $parts = $Version.Split(".")
    if ($parts.Count -eq 4) {
        return $Version
    }

    if ($parts.Count -eq 3) {
        return "$Version.0"
    }

    throw "Unsupported version format '$Version'. Expected three or four dot-separated numeric parts."
}

if (-not (Test-Path -LiteralPath $ProjectFile)) {
    throw "Project file not found at '$ProjectFile'."
}

if (-not (Test-Path -LiteralPath $PluginMasterFile)) {
    throw "pluginmaster.json not found at '$PluginMasterFile'."
}

if (-not (Test-Path -LiteralPath $ArtifactZip)) {
    throw "Expected packaged artifact not found at '$ArtifactZip'."
}

[xml]$projectXml = Get-Content -LiteralPath $ProjectFile
$projectVersion = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($projectVersion)) {
    throw "Could not read <Version> from '$ProjectFile'."
}

$expectedAssemblyVersion = Get-NormalizedAssemblyVersion -Version $projectVersion
$pluginMaster = Get-Content -LiteralPath $PluginMasterFile | ConvertFrom-Json
$pluginEntry = @($pluginMaster) | Where-Object { $_.InternalName -eq "SocietalReputation" } | Select-Object -First 1
if ($null -eq $pluginEntry) {
    throw "Could not find the SocietalReputation entry in '$PluginMasterFile'."
}

if ($pluginEntry.AssemblyVersion -ne $expectedAssemblyVersion) {
    throw "pluginmaster.json AssemblyVersion '$($pluginEntry.AssemblyVersion)' does not match project version '$expectedAssemblyVersion'."
}

$expectedDownloadSuffix = "/releases/latest/download/latest.zip"
foreach ($propertyName in @("DownloadLinkInstall", "DownloadLinkUpdate")) {
    $propertyValue = $pluginEntry.$propertyName
    if ([string]::IsNullOrWhiteSpace($propertyValue)) {
        throw "pluginmaster.json is missing '$propertyName'."
    }

    if (-not $propertyValue.EndsWith($expectedDownloadSuffix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$propertyName must end with '$expectedDownloadSuffix' but was '$propertyValue'."
    }
}

$extractPath = Join-Path ([System.IO.Path]::GetTempPath()) ("societalreputation-release-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $extractPath | Out-Null

try {
    Expand-Archive -LiteralPath $ArtifactZip -DestinationPath $extractPath -Force

    $packagedManifestPath = Join-Path $extractPath "SocietalReputation.json"
    if (-not (Test-Path -LiteralPath $packagedManifestPath)) {
        throw "Packaged manifest was not found at '$packagedManifestPath'."
    }

    $packagedManifest = Get-Content -LiteralPath $packagedManifestPath | ConvertFrom-Json
    if ($packagedManifest.InternalName -ne $pluginEntry.InternalName) {
        throw "Packaged manifest InternalName '$($packagedManifest.InternalName)' does not match pluginmaster.json InternalName '$($pluginEntry.InternalName)'."
    }

    if ($packagedManifest.AssemblyVersion -ne $pluginEntry.AssemblyVersion) {
        throw "Packaged manifest AssemblyVersion '$($packagedManifest.AssemblyVersion)' does not match pluginmaster.json AssemblyVersion '$($pluginEntry.AssemblyVersion)'."
    }

    Write-Host "Validated release package successfully."
    Write-Host "Project version: $projectVersion"
    Write-Host "Assembly version: $($pluginEntry.AssemblyVersion)"
    Write-Host "Artifact: $ArtifactZip"
}
finally {
    if (Test-Path -LiteralPath $extractPath) {
        Remove-Item -LiteralPath $extractPath -Recurse -Force
    }
}
