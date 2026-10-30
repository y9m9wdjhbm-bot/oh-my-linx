#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$LogRoot = 'D:\lineage\LC_PlayerMarket_OpenSourceLOG'
)

$ErrorActionPreference = 'Stop'
$manifestRelative = 'docs/source-manifest-v1.json'
$projectRelative = 'projects/LC.PlayerMarket.V96.API'
$expectedSourceFileCount = 454
$expectedCSharpFileCount = 449
$expectedProjectReferenceCount = 3
[string[]]$knownMissingResources = @(
    'engine/Core/item-state-build-certificate-38A7-v1.json',
    'research/item-kind79D1-contract-v1/capture-v1.json',
    'research/item-kind79D1-contract-v1/semantic-bridge-v1.json',
    'research/item-kindB47C-contract-v1/capture-v1.json',
    'research/item-kindB47C-contract-v1/semantic-bridge-v1.json',
    'research/inventory-desc-tw-79D1-v2.json',
    'verification/integration-v95/single-baseline-actual-v1.json',
    'verification/resident-v18/returned-v17-template.jsonl'
)
[string[]]$knownExcludedGlobs = @(
    'verification/page-range-v2/*.jsonl',
    'verification/force-preflight/*.jsonl',
    'verification/owner-changed-preflight-v1/*.jsonl',
    'verification/packet-copy-preflight-v1/*.jsonl'
)
$startedAt = [DateTimeOffset]::UtcNow
$runDirectory = Join-Path ([IO.Path]::GetFullPath($LogRoot)) (
    'integrity-checks\' + $startedAt.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
)
[void][IO.Directory]::CreateDirectory($runDirectory)
$reportPath = Join-Path $runDirectory 'source-integrity-v1.json'
$report = [ordered]@{
    SchemaVersion = 1
    Check = 'Public source snapshot integrity'
    StartedAtUtc = $startedAt.ToString('o')
    IsAdministrator = $true
    Manifest = $manifestRelative
    SourceFiles = 0
    CSharpFiles = 0
    SourceBytes = [long]0
    HashesMatched = 0
    ProjectReferencesVerified = 0
    RequireAdministratorVerified = $false
    MissingExplicitResources = @()
    ExcludedResourceGlobs = @()
    PublicationBoundariesVerified = $false
    Compilation = 'NotRun'
    RuntimeValidation = 'NotRun'
    GameValidation = 'NotRun'
    Result = 'Running'
    Error = $null
}
$failure = $null

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-ContainedPath {
    param([string]$Root, [string]$Relative)
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($Relative)) 'An empty relative path is not allowed.'
    Assert-Condition (-not [IO.Path]::IsPathRooted($Relative)) 'A rooted path is not allowed.'
    Assert-Condition ($Relative -notmatch '(^|[\\/])\.\.?([\\/]|$)|:|[\x00-\x1f]') 'A path contains an unsafe component.'
    $fullPath = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    $prefix = $Root.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
    Assert-Condition ($fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) 'A path escapes the repository.'
    return $fullPath
}

function Get-RepositoryFiles {
    param([string]$Root)
    $pending = New-Object 'Collections.Generic.Stack[string]'
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in (Get-ChildItem -LiteralPath $directory -Force)) {
            if ($directory -eq $Root -and $item.Name -eq '.git') { continue }
            Assert-Condition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Reparse points are not allowed in the public snapshot.'
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            else { $item }
        }
    }
}

function Read-SafeXml {
    param([string]$Path)
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    finally { $reader.Dispose() }
}

function Resolve-ProjectItem {
    param([string]$ProjectPath, [string]$Item)
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($Item)) 'An empty project item is not allowed.'
    Assert-Condition (-not [IO.Path]::IsPathRooted($Item)) 'A project item must use a relative path.'
    Assert-Condition ($Item -notmatch '\$\(|@\(|%\(|:|[\x00-\x1f]') 'A project item is not statically resolvable.'
    $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ProjectPath) $Item))
    $prefix = $script:projectRoot.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
    Assert-Condition ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) 'A project item escapes the source project.'
    return $resolved
}

try {
    $repository = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([char[]]'\/')
    Assert-Condition (Test-Path -LiteralPath $repository -PathType Container) 'The repository root does not exist.'
    $rootItem = Get-Item -LiteralPath $repository -Force
    Assert-Condition (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'The repository root cannot be a reparse point.'
    $manifestPath = Get-ContainedPath $repository $manifestRelative
    $script:projectRoot = Get-ContainedPath $repository $projectRelative
    Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'The public source manifest is missing.'
    Assert-Condition (Test-Path -LiteralPath $script:projectRoot -PathType Container) 'The source project directory is missing.'
    $repositoryFiles = Get-RepositoryFiles $repository
    $snapshot = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $entries = $snapshot.Files
    Assert-Condition ($snapshot.SchemaVersion -eq 1) 'Unsupported source manifest schema.'
    Assert-Condition ($snapshot.Version -eq '96.0.1') 'The manifest does not describe source snapshot 96.0.1.'
    Assert-Condition ($snapshot.SourceFileCount -eq $expectedSourceFileCount) 'The manifest source file count changed.'
    Assert-Condition ($snapshot.OriginalSourceBytesUnchanged -eq $true) 'The manifest must preserve original source bytes.'
    Assert-Condition ($null -ne $entries -and $entries.Count -eq $expectedSourceFileCount) 'The manifest must contain exactly 454 source entries.'

    $manifestPaths = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $sourcePrefix = $projectRelative + '/'
    foreach ($entry in $entries) {
        $relative = [string]$entry.Path
        Assert-Condition ($relative.StartsWith($sourcePrefix, [StringComparison]::Ordinal)) 'A source manifest entry is outside the versioned source project.'
        Assert-Condition ($relative -notmatch '\\|//') 'Manifest paths must use normalized forward slashes.'
        Assert-Condition ($manifestPaths.Add($relative)) 'The source manifest contains a duplicate path.'
        Assert-Condition ([string]$entry.SHA256 -match '^[0-9a-fA-F]{64}$') 'A source manifest hash is invalid.'
        $path = Get-ContainedPath $repository $relative
        Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) ('A listed source file is missing: ' + $relative)
        $item = Get-Item -LiteralPath $path
        Assert-Condition ($item.Length -eq [long]$entry.Bytes) ('A source file size differs from its manifest: ' + $relative)
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        Assert-Condition ($actualHash -eq [string]$entry.SHA256) ('A source file hash differs from its manifest: ' + $relative)
        $report.HashesMatched++
        $report.SourceBytes += $item.Length
    }

    $actualSourcePaths = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $projectFiles = $repositoryFiles | Where-Object { $_.FullName.StartsWith($script:projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }
    foreach ($file in $projectFiles) {
        $relative = $file.FullName.Substring($repository.Length + 1).Replace('\', '/')
        $projectPath = $relative.Substring($sourcePrefix.Length)
        Assert-Condition ($projectPath -notmatch '(^|/)(bin|obj|vendor|ShopControl|profiles|research|verification)(/|$)') ('An excluded runtime or evidence directory was bundled: ' + $projectPath)
        if ($file.Extension -in '.cs', '.csproj', '.manifest' -or $projectPath -eq 'global.json') {
            [void]$actualSourcePaths.Add($relative)
            Assert-Condition ($manifestPaths.Contains($relative)) ('An unlisted source file was added to the snapshot: ' + $relative)
            if ($file.Extension -eq '.cs') { $report.CSharpFiles++ }
        }
    }
    Assert-Condition ($actualSourcePaths.SetEquals($manifestPaths)) 'The source tree and manifest path sets differ.'
    Assert-Condition ($report.CSharpFiles -eq $expectedCSharpFileCount) 'The snapshot must contain exactly 449 original C# files.'
    $report.SourceFiles = $actualSourcePaths.Count

    foreach ($file in $repositoryFiles) {
        $relative = $file.FullName.Substring($repository.Length + 1).Replace('\', '/')
        Assert-Condition ($file.Extension -notin '.exe', '.dll', '.pdb', '.db', '.sqlite', '.sqlite3', '.u16le', '.bin', '.pfx', '.p12', '.pem', '.key', '.jsonl', '.log') ('An excluded binary, key, database, or raw log file was bundled: ' + $relative)
        Assert-Condition ($file.Name -notmatch '^\.env($|\.)|^id_(rsa|dsa|ecdsa|ed25519)($|\.)') ('A credentials file name was bundled: ' + $relative)
        $stream = [IO.File]::OpenRead($file.FullName)
        try {
            $first = $stream.ReadByte()
            $second = $stream.ReadByte()
            Assert-Condition (-not ($first -eq 0x4d -and $second -eq 0x5a)) ('A Windows executable signature was bundled: ' + $relative)
        }
        finally { $stream.Dispose() }
    }
    $report.PublicationBoundariesVerified = $true

    $applicationManifest = Read-SafeXml (Join-Path $script:projectRoot 'app.manifest')
    $requestedLevels = $applicationManifest.SelectNodes("//*[local-name()='requestedExecutionLevel']")
    Assert-Condition ($requestedLevels.Count -eq 1 -and $requestedLevels[0].GetAttribute('level') -eq 'requireAdministrator') 'The application manifest must request administrator execution.'
    $report.RequireAdministratorVerified = $true

    $missingResources = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $excludedGlobs = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $projectPaths = $projectFiles | Where-Object { $_.Extension -eq '.csproj' }
    Assert-Condition ($projectPaths.Count -eq 3) 'The snapshot must contain exactly three original project files.'
    foreach ($projectFile in $projectPaths) {
        $projectXml = Read-SafeXml $projectFile.FullName
        foreach ($reference in $projectXml.SelectNodes("//*[local-name()='ProjectReference'][@Include]")) {
            foreach ($include in $reference.GetAttribute('Include').Split(';')) {
                $referencedPath = Resolve-ProjectItem $projectFile.FullName $include
                Assert-Condition (Test-Path -LiteralPath $referencedPath -PathType Leaf) 'A project reference is missing.'
                $referencedRelative = $referencedPath.Substring($repository.Length + 1).Replace('\', '/')
                Assert-Condition ($manifestPaths.Contains($referencedRelative)) 'A project reference is not in the source manifest.'
                $report.ProjectReferencesVerified++
            }
        }
        foreach ($resource in $projectXml.SelectNodes("//*[local-name()='EmbeddedResource'][@Include]")) {
            foreach ($include in $resource.GetAttribute('Include').Split(';')) {
                $isGlob = $include.IndexOfAny([char[]]'*?') -ge 0
                # .NET Framework GetFullPath rejects wildcard characters. Use
                # inert markers only for lexical containment checks, then restore
                # the declared pattern. No wildcard resource is read or created.
                $normalizedInclude = $include
                if ($isGlob) {
                    $normalizedInclude = $include.Replace('*', '__snapshot_wildcard_star__').Replace('?', '__snapshot_wildcard_question__')
                }
                $resourcePath = Resolve-ProjectItem $projectFile.FullName $normalizedInclude
                $resourceRelative = $resourcePath.Substring($script:projectRoot.Length + 1).Replace('\', '/')
                if ($isGlob) {
                    $resourceRelative = $resourceRelative.Replace('__snapshot_wildcard_star__', '*').Replace('__snapshot_wildcard_question__', '?')
                    [void]$excludedGlobs.Add($resourceRelative)
                }
                elseif (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)) {
                    [void]$missingResources.Add($resourceRelative)
                }
            }
        }
    }
    Assert-Condition ($report.ProjectReferencesVerified -eq $expectedProjectReferenceCount) 'The project reference count differs from the source snapshot.'
    Assert-Condition ($missingResources.SetEquals($knownMissingResources)) 'The missing explicit resource set differs from the eight documented exclusions.'
    Assert-Condition ($excludedGlobs.SetEquals($knownExcludedGlobs)) 'The excluded fixture glob set differs from the four documented patterns.'
    $report.MissingExplicitResources = $knownMissingResources
    $report.ExcludedResourceGlobs = $knownExcludedGlobs
    $report.Result = 'Passed'
}
catch {
    $failure = $_
    $report.Result = 'Failed'
    $report.Error = $_.Exception.Message
}
finally {
    $report.FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $json = $report | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($reportPath, $json, (New-Object Text.UTF8Encoding($false)))
    $summary = @(
        '### Public source snapshot integrity',
        '',
        ('- Result: ' + $report.Result),
        ('- Original source files: ' + $report.SourceFiles + '; hashes matched: ' + $report.HashesMatched),
        ('- C# files: ' + $report.CSharpFiles + '; project references verified: ' + $report.ProjectReferencesVerified),
        ('- Administrator application manifest verified: ' + $report.RequireAdministratorVerified),
        ('- Publication boundaries verified: ' + $report.PublicationBoundariesVerified),
        ('- Documented missing explicit resources: ' + $report.MissingExplicitResources.Count),
        ('- Documented excluded fixture globs: ' + $report.ExcludedResourceGlobs.Count),
        '- Compilation: **not run**. The public snapshot omits required private resources.',
        '- Runtime validation: **not run**.',
        '- Game validation: **not run**.',
        '- This check verifies source integrity and publication boundaries; it does not certify the absence of every possible secret.'
    )
    if ($report.Error) { $summary += '- Error: ' + $report.Error }
    $summaryText = ($summary -join [Environment]::NewLine) + [Environment]::NewLine
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        [IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $summaryText, (New-Object Text.UTF8Encoding($false)))
    }
    Write-Output $summaryText
    Write-Output ('Integrity report: ' + $reportPath)
}

if ($null -ne $failure) { throw $failure }
