[CmdletBinding()]
param(
    [string]$NativePath = (Join-Path $PSScriptRoot '..\dist\native-v0.4-phase3\ComputerUse.Native.exe'),
    [ValidateSet('chrome', 'edge')]
    [string]$Browser = 'chrome',
    [int]$TimeoutMilliseconds = 20000
)

$ErrorActionPreference = 'Stop'

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "BROWSER WORKFLOW FAILED: $Message" }
}

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Invoke-Native {
    param(
        [System.Diagnostics.Process]$Process,
        [string]$Method,
        [hashtable]$Params,
        [int]$Id
    )

    $request = @{ id = $Id; method = $Method; params = $Params } | ConvertTo-Json -Compress -Depth 40
    $Process.StandardInput.WriteLine($request)
    $Process.StandardInput.Flush()
    $line = $Process.StandardOutput.ReadLine()
    Assert-Condition ($null -ne $line) "native process ended while waiting for $Method"
    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) {
        throw "BROWSER WORKFLOW FAILED: native $Method returned [$($response.error.code)] $($response.error.message); details=$($response.error.details | ConvertTo-Json -Compress -Depth 15)"
    }
    return $response.result
}

function Invoke-NativeExpectError {
    param(
        [System.Diagnostics.Process]$Process,
        [string]$Method,
        [hashtable]$Params,
        [int]$Id,
        [string]$ExpectedCode
    )

    $request = @{ id = $Id; method = $Method; params = $Params } | ConvertTo-Json -Compress -Depth 40
    $Process.StandardInput.WriteLine($request)
    $Process.StandardInput.Flush()
    $line = $Process.StandardOutput.ReadLine()
    Assert-Condition ($null -ne $line) "native process ended while waiting for expected $Method error"
    $response = $line | ConvertFrom-Json
    Assert-Condition ($null -ne $response.error) "native $Method unexpectedly succeeded"
    Assert-Condition ($response.error.code -eq $ExpectedCode) "expected $ExpectedCode, got $($response.error.code); details=$($response.error.details | ConvertTo-Json -Compress -Depth 15)"
    return $response.error
}

$resolvedNativePath = [System.IO.Path]::GetFullPath($NativePath)
Assert-Condition ([System.IO.File]::Exists($resolvedNativePath)) "native executable not found: $resolvedNativePath"

$fixtureRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\tests\browser-fixture'))
$port = Get-FreeLoopbackPort
$baseUrl = "http://127.0.0.1:$port"
$dynamicFormUrl = "$baseUrl/dynamic-form.html"
$successUrl = "$baseUrl/success.html"
$ambiguousUrl = "$baseUrl/ambiguous.html"
$serverOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("computer-use-browser-workflow-{0}.log" -f ([Guid]::NewGuid().ToString('N')))
$serverError = Join-Path ([System.IO.Path]::GetTempPath()) ("computer-use-browser-workflow-{0}.err" -f ([Guid]::NewGuid().ToString('N')))
$server = $null
$native = $null
$managedPid = $null
$nextId = 1

try {
    $python = (Get-Command python.exe -ErrorAction Stop).Source
    $server = Start-Process -FilePath $python -ArgumentList @(
        '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $fixtureRoot
    ) -WindowStyle Hidden -RedirectStandardOutput $serverOutput -RedirectStandardError $serverError -PassThru

    $probe = $null
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        try {
            $probe = Invoke-WebRequest -UseBasicParsing -Uri $dynamicFormUrl -TimeoutSec 2
            if ($probe.StatusCode -eq 200) { break }
        }
        catch { Start-Sleep -Milliseconds 100 }
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert-Condition ($null -ne $probe -and $probe.StatusCode -eq 200) 'localhost fixture did not start'

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedNativePath
    $startInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($resolvedNativePath)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $native = [System.Diagnostics.Process]::new()
    $native.StartInfo = $startInfo
    [void]$native.Start()

    $browserPath = if ($Browser -eq 'chrome') { 'chrome.exe' } else { 'msedge.exe' }
    $launch = Invoke-Native $native 'launch' @{
        path = $browserPath
        browser = @{ mode = 'managed'; profile = 'ephemeral' }
        args = @($dynamicFormUrl)
        wait_for_window = $true
        timeout_ms = $TimeoutMilliseconds
        poll_ms = 100
    } $nextId
    $nextId++
    Assert-Condition ($launch.started -eq $true) 'managed browser did not start'
    Assert-Condition ($launch.browser.managed -eq $true) 'launch did not report a managed browser'
    $managedPid = [int]$launch.pid
    $windowId = [string]$launch.window.id

    $workflow = Invoke-Native $native 'perform' @{
        window_id = $windowId
        actions = @(
            @{
                type = 'navigate'
                url = $dynamicFormUrl
                expect = @{
                    url_contains = '/dynamic-form.html'
                    title_contains = 'Dynamic Computer Use form'
                    page_stable = $true
                    navigation_complete = $true
                }
                retry = @{ max_attempts = 2; delay_ms = 0 }
            },
            @{
                type = 'set_value'
                target = @{ test_id = 'name-field' }
                value = 'Antigravity'
                expect = @{
                    value_equals = @{
                        target = @{ test_id = 'name-field' }
                        equals = 'Antigravity'
                    }
                }
            },
            @{
                type = 'set_value'
                target = @{ test_id = 'email-field' }
                value = 'test@example.com'
                expect = @{
                    value_equals = @{
                        target = @{ test_id = 'email-field' }
                        equals = 'test@example.com'
                    }
                }
            },
            @{
                type = 'click'
                target = @{ test_id = 'submit-button' }
                expect = @{
                    url_contains = '/success.html'
                    title_contains = 'Success'
                    element_exists = @{ role = 'heading'; name = 'Success' }
                    text_contains = @{
                        target = @{ role = 'heading'; name = 'Success' }
                        contains = 'uccess'
                    }
                    page_changed = $true
                    page_stable = $true
                    navigation_complete = $true
                }
                retry = @{ max_attempts = 2; delay_ms = 0 }
            }
        )
        verify = $true
    } $nextId
    $nextId++

    Assert-Condition ($workflow.status -eq 'succeeded') 'browser workflow did not succeed'
    Assert-Condition ($workflow.interaction.surface -eq 'browser') 'workflow surface was not browser'
    Assert-Condition ($workflow.interaction.backend -eq 'browser_cdp') 'workflow backend was not browser_cdp'
    Assert-Condition ($workflow.browser.url -like "$successUrl*") "workflow final URL was not success: $($workflow.browser.url)"
    $trace = @($workflow.execution_trace)
    Assert-Condition ($trace.Count -ge 4) "expected at least four trace entries, got $($trace.Count)"
    foreach ($entry in $trace) {
        Assert-Condition ($entry.surface -eq 'browser') "trace surface was not browser at step $($entry.step_index)"
        Assert-Condition ($entry.backend -eq 'browser_cdp') "trace backend was not browser_cdp at step $($entry.step_index)"
        Assert-Condition ($entry.capture_backend -eq 'cdp_page_capture') "trace capture was not cdp_page_capture at step $($entry.step_index)"
    }
    $passedTrace = @($trace | Where-Object { $_.final_status -eq 'passed' })
    Assert-Condition ($passedTrace.Count -eq 4) "expected one passed trace entry per action, got $($passedTrace.Count)"
    foreach ($stepIndex in 1..4) {
        $stepTrace = @($passedTrace | Where-Object { $_.step_index -eq $stepIndex })
        Assert-Condition ($stepTrace.Count -eq 1) "step $stepIndex did not have exactly one final passed trace"
        Assert-Condition ($stepTrace[0].verification.status -eq 'passed') "verification did not pass at step $stepIndex"
    }
    $navigationTrace = @($passedTrace | Where-Object { $_.step_index -eq 4 })[0].navigation
    Assert-Condition ($navigationTrace.occurred -eq $true) 'submit trace did not record navigation'
    Assert-Condition ($navigationTrace.navigation_complete -eq $true) 'submit trace did not record completed navigation'
    Assert-Condition ($navigationTrace.url_before -like '*dynamic-form.html*') 'trace URL before navigation was wrong'
    Assert-Condition ($navigationTrace.url_after -like "$successUrl*") 'trace URL after navigation was wrong'

    $reset = Invoke-Native $native 'perform' @{
        window_id = $windowId
        actions = @(@{
            type = 'navigate'
            url = $dynamicFormUrl
            expect = @{
                url_contains = '/dynamic-form.html'
                page_stable = $true
                navigation_complete = $true
            }
        })
        verify = $true
    } $nextId
    $nextId++
    Assert-Condition ($reset.status -eq 'succeeded') 'reset navigation before failure test did not succeed'

    $failure = Invoke-NativeExpectError $native 'perform' @{
        window_id = $windowId
        actions = @(
            @{
                type = 'set_value'
                target = @{ role = 'textbox'; name = 'Name' }
                value = 'Wrong'
                expect = @{
                    value_equals = @{
                        target = @{ role = 'textbox'; name = 'Name' }
                        equals = 'Never visible'
                    }
                }
                retry = @{ max_attempts = 2; delay_ms = 0 }
            }
        )
        verify = $true
    } $nextId 'POSTCONDITION_FAILED'
    Assert-Condition ([int]$failure.details.step_index -eq 1) 'failure did not report step 1'
    Assert-Condition ([int]$failure.details.attempts_used -eq 2) 'failure did not report bounded attempts'
    Assert-Condition ($failure.details.retry_exhausted -eq $true) 'failure did not report retry exhaustion'
    Assert-Condition (@($failure.details.execution_trace).Count -eq 2) 'failure trace did not contain both attempts'

    $navigateAmbiguous = Invoke-Native $native 'perform' @{
        window_id = $windowId
        actions = @(@{ type = 'navigate'; url = $ambiguousUrl })
        verify = $true
    } $nextId
    $nextId++
    Assert-Condition ($navigateAmbiguous.status -eq 'succeeded') 'ambiguous fixture navigation failed'

    $ambiguousFailure = Invoke-NativeExpectError $native 'perform' @{
        window_id = $windowId
        actions = @(
            @{
                type = 'click'
                target = @{ role = 'button'; name = 'Save' }
                retry = @{ max_attempts = 3; delay_ms = 0 }
            }
        )
        verify = $true
    } $nextId 'AMBIGUOUS_TARGET'
    Assert-Condition (@($ambiguousFailure.details.execution_trace).Count -eq 1) 'ambiguous target was retried'
    Assert-Condition ($ambiguousFailure.details.execution_trace[0].action_executed -eq $false) 'ambiguous target reported executed action'
    Assert-Condition ($null -ne $ambiguousFailure.details.cause_details.candidates) 'ambiguity candidates were not preserved'

    Write-Output ("BROWSER WORKFLOW PASSED: browser={0}; surface={1}; backend={2}; trace_steps={3}; final_url={4}; failure_cases=2" -f `
        $Browser, $workflow.interaction.surface, $workflow.interaction.backend, $trace.Count, $workflow.browser.url)
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
        if ($null -ne $managed -and -not $managed.HasExited) { Stop-Process -Id $managedPid -Force }
    }

    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        if (-not $server.WaitForExit(3000)) { throw 'BROWSER WORKFLOW FAILED: fixture server did not exit' }
    }

    foreach ($temporaryPath in @($serverOutput, $serverError)) {
        if ([System.IO.File]::Exists($temporaryPath)) {
            for ($attempt = 0; $attempt -lt 10; $attempt++) {
                try { [System.IO.File]::Delete($temporaryPath); break }
                catch [System.IO.IOException] { Start-Sleep -Milliseconds 50 }
            }
        }
    }
}
