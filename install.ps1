[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $UserProfileRoot = $env:USERPROFILE,

    [Parameter()]
    [switch] $SkipGlobalMcpConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$nativeExecutable = Join-Path $repositoryRoot 'dist\native\ComputerUse.Native.exe'
$mcpEntryPoint = Join-Path $repositoryRoot 'mcp\dist\index.js'
$pluginSource = Join-Path $repositoryRoot 'plugin'
$pluginManifest = Join-Path $pluginSource 'plugin.json'
$skillSource = Join-Path $pluginSource 'skills\computer-use\SKILL.md'

if (-not (Test-Path -LiteralPath $nativeExecutable -PathType Leaf)) {
    throw "Missing $nativeExecutable. Build and publish the native runtime first."
}

if (-not (Test-Path -LiteralPath $mcpEntryPoint -PathType Leaf)) {
    throw "Missing $mcpEntryPoint. Run 'npm install' and 'npm run build' in mcp first."
}

foreach ($requiredPath in @($pluginManifest, $skillSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Missing plugin file: $requiredPath"
    }
}

$userRoot = (Resolve-Path -LiteralPath $UserProfileRoot).Path
$pluginRoot = Join-Path $userRoot '.gemini\config\plugins\computer-use-for-antigravity'
$pluginSkillRoot = Join-Path $pluginRoot 'skills\computer-use'
$pluginMcpConfig = Join-Path $pluginRoot 'mcp_config.json'
$globalMcpConfig = Join-Path $userRoot '.gemini\config\mcp_config.json'
$globalConfigDirectory = Split-Path -Parent $globalMcpConfig

function Convert-ToForwardSlash {
    param([Parameter(Mandatory)][string] $PathValue)

    return $PathValue.Replace('\', '/')
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string] $PathValue,
        [Parameter(Mandatory)][string] $Content
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($PathValue, $Content, $encoding)
}

$server = [ordered]@{
    command = 'node'
    args = @((Convert-ToForwardSlash -PathValue $mcpEntryPoint))
    env = [ordered]@{
        COMPUTER_USE_NATIVE = (Convert-ToForwardSlash -PathValue $nativeExecutable)
    }
}

$pluginConfig = [ordered]@{
    mcpServers = [ordered]@{
        'computer-use' = $server
    }
}

if ($PSCmdlet.ShouldProcess($pluginRoot, 'Install Computer Use for Antigravity plugin')) {
    New-Item -ItemType Directory -Force -Path $pluginSkillRoot | Out-Null
    Copy-Item -LiteralPath $pluginManifest -Destination (Join-Path $pluginRoot 'plugin.json') -Force
    Copy-Item -LiteralPath $skillSource -Destination (Join-Path $pluginSkillRoot 'SKILL.md') -Force
    Write-Utf8NoBom -Path $pluginMcpConfig -Content ($pluginConfig | ConvertTo-Json -Depth 10)

    if (-not $SkipGlobalMcpConfig) {
        New-Item -ItemType Directory -Force -Path $globalConfigDirectory | Out-Null

        if (Test-Path -LiteralPath $globalMcpConfig -PathType Leaf) {
            $globalConfig = Get-Content -LiteralPath $globalMcpConfig -Raw | ConvertFrom-Json
        }
        else {
            $globalConfig = [pscustomobject]@{
                mcpServers = [pscustomobject]@{}
            }
        }

        if ($null -eq $globalConfig) {
            $globalConfig = [pscustomobject]@{}
        }

        $mcpServersProperty = $globalConfig.PSObject.Properties['mcpServers']
        if ($null -eq $mcpServersProperty -or $null -eq $globalConfig.mcpServers) {
            $globalConfig | Add-Member -MemberType NoteProperty -Name 'mcpServers' -Value ([pscustomobject]@{}) -Force
        }

        $globalServer = [pscustomobject]@{
            command = 'node'
            args = @((Convert-ToForwardSlash -PathValue $mcpEntryPoint))
            env = [pscustomobject]@{
                COMPUTER_USE_NATIVE = (Convert-ToForwardSlash -PathValue $nativeExecutable)
            }
        }
        $globalConfig.mcpServers | Add-Member -MemberType NoteProperty -Name 'computer-use' -Value $globalServer -Force
        Write-Utf8NoBom -Path $globalMcpConfig -Content ($globalConfig | ConvertTo-Json -Depth 20)
    }
}

Write-Output "Installed plugin: $pluginRoot"
Write-Output "Native runtime: $nativeExecutable"
Write-Output "MCP entry point: $mcpEntryPoint"
if ($SkipGlobalMcpConfig) {
    Write-Output 'Global MCP config: skipped (-SkipGlobalMcpConfig)'
}
else {
    Write-Output "Global MCP config: $globalMcpConfig"
}
