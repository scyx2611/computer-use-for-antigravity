[CmdletBinding()]
param(
    [string]$NativePath = (Join-Path $PSScriptRoot '..\dist\native-v0.4-phase2\ComputerUse.Native.exe'),
    [ValidateSet('chrome', 'edge')]
    [string]$Browser = 'chrome',
    [int]$TimeoutMilliseconds = 20000
)

$ErrorActionPreference = 'Stop'

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "BROWSER ACTION FAILED: $Message" }
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

    $request = @{ id = $Id; method = $Method; params = $Params } | ConvertTo-Json -Compress -Depth 30
    $Process.StandardInput.WriteLine($request)
    $Process.StandardInput.Flush()
    $line = $Process.StandardOutput.ReadLine()
    Assert-Condition ($null -ne $line) "native process ended while waiting for $Method"
    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) {
        throw "BROWSER ACTION FAILED: native $Method returned [$($response.error.code)] $($response.error.message); details=$($response.error.details | ConvertTo-Json -Compress -Depth 10)"
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

    $request = @{ id = $Id; method = $Method; params = $Params } | ConvertTo-Json -Compress -Depth 30
    $Process.StandardInput.WriteLine($request)
    $Process.StandardInput.Flush()
    $line = $Process.StandardOutput.ReadLine()
    Assert-Condition ($null -ne $line) "native process ended while waiting for expected $Method error"
    $response = $line | ConvertFrom-Json
    Assert-Condition ($null -ne $response.error) "native $Method unexpectedly succeeded"
    Assert-Condition ($response.error.code -eq $ExpectedCode) "expected $ExpectedCode, got $($response.error.code); details=$($response.error.details | ConvertTo-Json -Compress -Depth 10)"
    return $response.error
}

function Find-Element {
    param($Elements, [string]$Role, [string]$Name)
    return @($Elements | Where-Object { $_.role -eq $Role -and $_.name -eq $Name })[0]
}

$resolvedNativePath = [System.IO.Path]::GetFullPath($NativePath)
Assert-Condition ([System.IO.File]::Exists($resolvedNativePath)) "native executable not found: $resolvedNativePath"

$fixtureRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\tests\browser-fixture'))
$port = Get-FreeLoopbackPort
$baseUrl = "http://127.0.0.1:$port"
$formUrl = "$baseUrl/form.html"
$successUrl = "$baseUrl/success.html"
$navigationUrl = "$baseUrl/navigation.html"
$ambiguousUrl = "$baseUrl/ambiguous.html"
$serverOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("computer-use-browser-actions-{0}.log" -f ([Guid]::NewGuid().ToString('N')))
$serverError = Join-Path ([System.IO.Path]::GetTempPath()) ("computer-use-browser-actions-{0}.err" -f ([Guid]::NewGuid().ToString('N')))
$server = $null
$native = $null
$managedPid = $null
$nextId = 1

try {
    $python = (Get-Command python.exe -ErrorAction Stop).Source
    $server = Start-Process -FilePath $python -ArgumentList @(
        '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $fixtureRoot
    ) -WindowStyle Hidden -RedirectStandardOutput $serverOutput -RedirectStandardError $serverError -PassThru

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        try {
            $probe = Invoke-WebRequest -UseBasicParsing -Uri $formUrl -TimeoutSec 2
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
        args = @($formUrl)
        wait_for_window = $true
        timeout_ms = $TimeoutMilliseconds
        poll_ms = 100
    } $nextId
    $nextId++
    Assert-Condition ($launch.started -eq $true) 'managed browser did not start'
    Assert-Condition ($launch.browser.managed -eq $true) 'launch did not report managed browser'
    $managedPid = [int]$launch.pid
    $windowId = [string]$launch.window.id

    $observed = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    Assert-Condition ($observed.interaction.backend -eq 'browser_cdp') 'initial observe did not use browser_cdp'
    $elements = @($observed.elements)
    $name = Find-Element $elements 'textbox' 'Name'
    $email = Find-Element $elements 'textbox' 'Email'
    $submit = Find-Element $elements 'button' 'Submit'
    Assert-Condition ($null -ne $name) 'Name textbox was not observed'
    Assert-Condition ($null -ne $email) 'Email textbox was not observed'
    Assert-Condition ($null -ne $submit) 'Submit button was not observed'

    $setValue = Invoke-Native $native 'act' @{
        state_id = [string]$observed.state_id
        window_id = $windowId
        action = @{ type = 'set_value'; target = @{ name = 'Name'; role = 'textbox' }; value = 'Alice' }
    } $nextId
    $nextId++
    Assert-Condition ($setValue.success -eq $true) 'set_value did not succeed'

    $afterValue = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    $updatedName = Find-Element @($afterValue.elements) 'textbox' 'Name'
    Assert-Condition ($updatedName.value -eq 'Alice') "set_value was not visible in AX value: $($updatedName.value)"

    $selectAll = Invoke-Native $native 'act' @{
        state_id = [string]$afterValue.state_id
        window_id = $windowId
        action = @{ type = 'hotkey'; key = 'A'; modifiers = @('CTRL'); target = @{ test_id = 'name-field' } }
    } $nextId
    $nextId++
    Assert-Condition ($selectAll.success -eq $true) 'hotkey Ctrl+A did not succeed'

    $afterSelectAll = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    $replaceText = Invoke-Native $native 'act' @{
        state_id = [string]$afterSelectAll.state_id
        window_id = $windowId
        action = @{ type = 'type_text'; target = @{ test_id = 'name-field' }; text = 'Bob' }
    } $nextId
    $nextId++
    Assert-Condition ($replaceText.success -eq $true) 'type_text after hotkey did not succeed'

    $afterReplace = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    $replacedName = Find-Element @($afterReplace.elements) 'textbox' 'Name'
    Assert-Condition ($replacedName.value -eq 'Bob') "hotkey selection was not honored: $($replacedName.value)"

    $typeText = Invoke-Native $native 'act' @{
        state_id = [string]$afterReplace.state_id
        window_id = $windowId
        action = @{ type = 'type_text'; target = @{ test_id = 'email-field' }; text = 'alice@example.test' }
    } $nextId
    $nextId++
    Assert-Condition ($typeText.success -eq $true) 'type_text did not succeed'

    $afterText = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    $updatedEmail = Find-Element @($afterText.elements) 'textbox' 'Email'
    Assert-Condition ($updatedEmail.value -eq 'alice@example.test') "type_text was not visible in AX value: $($updatedEmail.value)"

    $scroll = Invoke-Native $native 'act' @{
        state_id = [string]$afterText.state_id
        window_id = $windowId
        action = @{ type = 'scroll'; amount = 1 }
    } $nextId
    $nextId++
    Assert-Condition ($scroll.success -eq $true) 'scroll did not succeed'

    $afterScroll = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    $pressEnter = Invoke-Native $native 'act' @{
        state_id = [string]$afterScroll.state_id
        window_id = $windowId
        action = @{ type = 'press_key'; key = 'ENTER'; target = @{ name = 'Name'; role = 'textbox' } }
    } $nextId
    $nextId++
    Assert-Condition ($pressEnter.success -eq $true) 'press_key Enter did not succeed'

    Start-Sleep -Milliseconds 200
    $success = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    Assert-Condition ($success.browser.url -like "$successUrl*") "Enter did not navigate to success: $($success.browser.url)"
    Assert-Condition ((Find-Element @($success.elements) 'heading' 'Success') -ne $null) 'success heading was not observed'

    $stale = Invoke-NativeExpectError $native 'act' @{
        state_id = [string]$afterScroll.state_id
        window_id = $windowId
        action = @{ type = 'click'; target = @{ name = 'Submit'; role = 'button' } }
    } $nextId 'STALE_BROWSER_STATE'
    $nextId++

    $navigate = Invoke-Native $native 'act' @{
        state_id = [string]$success.state_id
        window_id = $windowId
        action = @{ type = 'navigate'; url = $navigationUrl }
    } $nextId
    $nextId++
    Assert-Condition ($navigate.success -eq $true) 'navigate action did not succeed'

    Start-Sleep -Milliseconds 300
    $navigation = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    Assert-Condition ($navigation.browser.url -like "$navigationUrl*") 'navigate did not create a navigation fixture state'
    $click = Invoke-Native $native 'act' @{
        state_id = [string]$navigation.state_id
        window_id = $windowId
        action = @{ type = 'click'; target = @{ name = 'Continue'; role = 'link' } }
    } $nextId
    $nextId++
    Assert-Condition ($click.success -eq $true) 'browser click did not succeed'

    Start-Sleep -Milliseconds 300
    $afterClick = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    Assert-Condition ($afterClick.browser.url -like "$successUrl*") 'browser click did not navigate to success'

    $ambiguousNavigation = Invoke-Native $native 'act' @{
        state_id = [string]$afterClick.state_id
        window_id = $windowId
        action = @{ type = 'navigate'; url = $ambiguousUrl }
    } $nextId
    $nextId++
    Assert-Condition ($ambiguousNavigation.success -eq $true) 'ambiguous fixture navigation did not succeed'
    Start-Sleep -Milliseconds 300
    $ambiguous = Invoke-Native $native 'observe' @{ window_id = $windowId } $nextId
    $nextId++
    Start-Sleep -Milliseconds 300
    $null = Invoke-NativeExpectError $native 'act' @{
        state_id = [string]$ambiguous.state_id
        window_id = $windowId
        action = @{ type = 'click'; target = @{ name = 'Save'; role = 'button' } }
    } $nextId 'AMBIGUOUS_TARGET'
    $nextId++

    $workflow = Invoke-Native $native 'perform' @{
        window_id = $windowId
        actions = @(
            @{ type = 'navigate'; url = $formUrl },
            @{ type = 'set_value'; target = @{ name = 'Name'; role = 'textbox' }; value = 'Workflow' },
            @{ type = 'press_key'; key = 'ENTER'; target = @{ name = 'Name'; role = 'textbox' } }
        )
        verify = $true
    } $nextId
    $nextId++
    Assert-Condition ($workflow.status -eq 'succeeded') 'browser perform workflow did not succeed'
    Assert-Condition ($workflow.interaction.backend -eq 'browser_cdp') 'browser perform did not report browser_cdp'
    Assert-Condition (@($workflow.execution_trace).Count -eq 3) 'browser workflow trace did not contain three steps'
    Assert-Condition ([bool]$workflow.state_id) 'browser workflow did not return final state'

    Write-Output ("BROWSER ACTIONS PASSED: browser={0}; backend={1}; trace_steps={2}; final_url={3}" -f `
        $Browser, $workflow.interaction.backend, @($workflow.execution_trace).Count, $workflow.browser.url)
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
        if (-not $server.WaitForExit(3000)) { throw 'BROWSER ACTION FAILED: fixture server did not exit' }
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
