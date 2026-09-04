[CmdletBinding()]
param(
    [string]$NativePath = (Join-Path $PSScriptRoot '..\dist\native-v0.4-spike\ComputerUse.Native.exe'),
    [ValidateSet('chrome', 'edge')]
    [string]$Browser = 'chrome',
    [int]$TimeoutMilliseconds = 20000
)

$ErrorActionPreference = 'Stop'

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "BROWSER SPIKE FAILED: $Message"
    }
}

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-Native {
    param(
        [System.Diagnostics.Process]$Process,
        [string]$Method,
        [hashtable]$Params,
        [int]$Id
    )

    $request = @{ id = $Id; method = $Method; params = $Params } |
        ConvertTo-Json -Compress -Depth 20
    $Process.StandardInput.WriteLine($request)
    $Process.StandardInput.Flush()
    $line = $Process.StandardOutput.ReadLine()
    Assert-Condition ($null -ne $line) "native process ended while waiting for $Method"

    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) {
        throw "BROWSER SPIKE FAILED: native $Method returned [$($response.error.code)] $($response.error.message)"
    }

    return $response
}

$resolvedNativePath = [System.IO.Path]::GetFullPath($NativePath)
Assert-Condition ([System.IO.File]::Exists($resolvedNativePath)) "native executable not found: $resolvedNativePath"

$fixtureRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\tests\browser-fixture'))
$port = Get-FreeLoopbackPort
$fixtureUrl = "http://127.0.0.1:$port/index.html"
$serverOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("computer-use-browser-server-{0}.log" -f ([Guid]::NewGuid().ToString('N')))
$serverError = Join-Path ([System.IO.Path]::GetTempPath()) ("computer-use-browser-server-{0}.err" -f ([Guid]::NewGuid().ToString('N')))
$server = $null
$native = $null
$managedPid = $null
$windowId = $null
$nextId = 1

try {
    $python = (Get-Command python.exe -ErrorAction Stop).Source
    $server = Start-Process -FilePath $python -ArgumentList @(
        '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $fixtureRoot
    ) -WindowStyle Hidden -RedirectStandardOutput $serverOutput -RedirectStandardError $serverError -PassThru

    $serverDeadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    $probe = $null
    do {
        try {
            $probe = Invoke-WebRequest -UseBasicParsing -Uri $fixtureUrl -TimeoutSec 2
            if ($probe.StatusCode -eq 200) {
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    } while ([DateTime]::UtcNow -lt $serverDeadline)
    Assert-Condition ($null -ne $probe -and $probe.StatusCode -eq 200) "localhost fixture did not start"

    $nativeStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $nativeStartInfo.FileName = $resolvedNativePath
    $nativeStartInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($resolvedNativePath)
    $nativeStartInfo.UseShellExecute = $false
    $nativeStartInfo.CreateNoWindow = $true
    $nativeStartInfo.RedirectStandardInput = $true
    $nativeStartInfo.RedirectStandardOutput = $true
    $nativeStartInfo.RedirectStandardError = $true
    $native = [System.Diagnostics.Process]::new()
    $native.StartInfo = $nativeStartInfo
    [void]$native.Start()

    $browserPath = if ($Browser -eq 'chrome') { 'chrome.exe' } else { 'msedge.exe' }
    $launch = Invoke-Native -Process $native -Method 'launch' -Params @{
        path = $browserPath
        browser = @{ mode = 'managed'; profile = 'ephemeral' }
        args = @($fixtureUrl)
        wait_for_window = $true
        timeout_ms = $TimeoutMilliseconds
        poll_ms = 100
    } -Id $nextId
    $nextId++

    $launchResult = $launch.result
    Assert-Condition ($launchResult.started -eq $true) 'managed browser did not start'
    Assert-Condition ($launchResult.browser.managed -eq $true) 'launch did not report a managed session'
    Assert-Condition ($launchResult.browser.profile -eq 'ephemeral') 'launch did not report an ephemeral profile'
    Assert-Condition ([bool]$launchResult.browser.session_id) 'launch did not return a browser session id'
    Assert-Condition ([bool]$launchResult.browser.debug_endpoint) 'launch did not return a local debug endpoint'
    Assert-Condition ([bool]$launchResult.window.id) 'launch did not return a window id'
    $managedPid = [int]$launchResult.pid
    $windowId = [string]$launchResult.window.id

    $observed = Invoke-Native -Process $native -Method 'observe' -Params @{ window_id = $windowId } -Id $nextId
    $nextId++
    $result = $observed.result
    Assert-Condition ($result.interaction.surface -eq 'browser') 'observe did not route to browser surface'
    Assert-Condition ($result.interaction.backend -eq 'browser_cdp') 'observe did not report browser_cdp backend'
    Assert-Condition ($result.browser.managed -eq $true) 'observe did not report a managed browser'
    Assert-Condition ($result.browser.url -like 'http://127.0.0.1:*') "observe returned unexpected URL: $($result.browser.url)"
    Assert-Condition ([bool]$result.browser.target_id) 'observe did not return target_id'
    Assert-Condition ([bool]$result.state_id) 'observe did not return state_id'
    Assert-Condition ([bool]$result.screenshot) 'observe did not return screenshot data'
    Assert-Condition ($result.capture.backend -eq 'cdp_page_capture') "observe capture backend was $($result.capture.backend)"
    Assert-Condition ($result.coordinate_spaces.screenshot -eq 'viewport') 'browser screenshot space was not viewport'
    Assert-Condition ($result.coordinate_spaces.elements -eq 'viewport') 'browser element space was not viewport'

    $elements = @($result.elements)
    Assert-Condition (($elements | Where-Object { $_.role -eq 'textbox' -and $_.name -eq 'Name' }).Count -ge 1) 'AX textbox Name was not exposed'
    Assert-Condition (($elements | Where-Object { $_.role -eq 'button' -and $_.name -eq 'Submit' }).Count -ge 1) 'AX button Submit was not exposed'
    Assert-Condition (@($result.browser.tabs).Count -ge 1) 'observe did not report the page tab'

    Write-Output ("BROWSER SPIKE PASSED: browser={0}; target={1}; capture={2}; elements={3}; url={4}" -f `
        $Browser, $result.browser.target_id, $result.capture.backend, $elements.Count, $result.browser.url)
}
finally {
    if ($null -ne $native -and -not $native.HasExited) {
        $native.StandardInput.Close()
        if (-not $native.WaitForExit(5000)) {
            $native.Kill()
            $native.WaitForExit()
        }
    }

    if ($null -ne $managedPid) {
        $managed = Get-Process -Id $managedPid -ErrorAction SilentlyContinue
        if ($null -ne $managed -and -not $managed.HasExited) {
            Stop-Process -Id $managedPid -Force
        }
    }

    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        if (-not $server.WaitForExit(3000)) {
            throw 'BROWSER SPIKE FAILED: fixture server did not exit during cleanup'
        }
    }

    foreach ($temporaryPath in @($serverOutput, $serverError)) {
        if ([System.IO.File]::Exists($temporaryPath)) {
            for ($attempt = 0; $attempt -lt 10; $attempt++) {
                try {
                    [System.IO.File]::Delete($temporaryPath)
                    break
                }
                catch [System.IO.IOException] {
                    Start-Sleep -Milliseconds 50
                }
            }
        }
    }
}
