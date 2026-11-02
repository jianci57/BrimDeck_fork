[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-(alpha|beta)\.\d+)?$')][string]$Version,
    [string]$ReleaseNotes,
    [string]$OutputDirectory = 'artifacts/releases'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseNotes.ps1')
. (Join-Path $PSScriptRoot 'PortablePackage.ps1')
Push-Location $projectRoot
try {
    if (!$ReleaseNotes) { $ReleaseNotes = "releases/$Version.md" }
    $notesPath = (Resolve-Path -LiteralPath $ReleaseNotes).Path
    $outputPath = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }))
    $publishPath = Join-Path $projectRoot ".artifacts/release-build/$Version-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $publishPath, $outputPath | Out-Null
    # Each version gets a clean output folder, so files of an earlier release never end up in this one.
    Get-ChildItem -LiteralPath $outputPath -File | Remove-Item
    # The sensor service is not in the installer: BrimDeck downloads it from this release when the user turns enhanced sensors on,
    # and accepts it only if it matches the SHA-256 written into BrimDeck here (Native.SensorSetup).
    $sensorsPath = "$publishPath-sensors"
    dotnet publish src/BrimDeck.Sensors/BrimDeck.Sensors.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -o $sensorsPath
    if ($LASTEXITCODE -ne 0) { throw 'Sensor service publish failed.' }
    $sensors = Copy-Item -LiteralPath (Join-Path $sensorsPath 'BrimDeck.Sensors.exe') -Destination $outputPath -PassThru
    $sensorsSha256 = (Get-FileHash -LiteralPath $sensors.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    dotnet publish src/BrimDeck/BrimDeck.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -p:SensorsSha256=$sensorsSha256 -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    $iscc = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1')
    & $iscc -qp "-dAppVersion=$Version" "-dSourceDir=$publishPath" "-o$outputPath" installer/BrimDeck.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Get-Item -LiteralPath (Join-Path $outputPath "BrimDeck-$Version-Setup.exe")
    $portable = New-PortablePackage -SourceDir $publishPath -Destination (Join-Path $outputPath "BrimDeck-$Version-Portable-x64.zip")
    # The app reads update.json from the latest release and verifies the package against it before using it. file, size and
    # sha256 describe the installer, as every installed version expects; portable describes the zip for portable copies.
    $manifest = [ordered]@{
        version = $Version
        notes = Read-ReleaseNotes $notesPath
        file = $installer.Name
        size = $installer.Length
        sha256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        portable = [ordered]@{
            file = $portable.Name
            size = $portable.Length
            sha256 = (Get-FileHash -LiteralPath $portable.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    [IO.File]::WriteAllText((Join-Path $outputPath 'update.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Get-ChildItem -LiteralPath $outputPath -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding utf8
    Write-Output "Release artifacts: $outputPath"
    Write-Output "Published application for validation: $publishPath"
}
finally { Pop-Location }
