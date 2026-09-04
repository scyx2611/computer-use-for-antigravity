[CmdletBinding()]
param(
    [string]$NativePath = (Join-Path $PSScriptRoot '..\dist\native\ComputerUse.Native.exe'),
    [int]$TimeoutMilliseconds = 15000
)

$ErrorActionPreference = 'Stop'

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "SMOKE FAILED: $Message"
    }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ComputerUseSmokeNativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@

$resolvedNativePath = [System.IO.Path]::GetFullPath($NativePath)
Assert-Condition ([System.IO.File]::Exists($resolvedNativePath)) "native executable not found: $resolvedNativePath"

$native = $null
$notepadLauncher = $null
$target = $null
$temporaryDocument = Join-Path ([System.IO.Path]::GetTempPath()) ("computer-use-smoke-{0}.txt" -f ([Guid]::NewGuid().ToString('N')))
[System.IO.File]::WriteAllText($temporaryDocument, '')
$existingNotepadPids = @(Get-Process -Name notepad -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

function Invoke-Native {
    param(
        [string]$Method,
        [hashtable]$Params,
        [int]$Id
    )

    $request = @{ id = $Id; method = $Method; params = $Params } | ConvertTo-Json -Compress -Depth 20
    $native.StandardInput.WriteLine($request)
    $native.StandardInput.Flush()
    $line = $native.StandardOutput.ReadLine()
    Assert-Condition ($null -ne $line) "native process ended while waiting for $Method"

    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) {
        throw "SMOKE FAILED: native $Method returned [$($response.error.code)] $($response.error.message)"
    }

    return $response
}

try {
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

    $notepadLauncher = Start-Process -FilePath 'notepad.exe' -ArgumentList @($temporaryDocument) -PassThru
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    $target = $null
    $nextId = 1
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $target) {
        $windowsResponse = Invoke-Native 'list_windows' @{} $nextId
        $nextId++
        $target = @($windowsResponse.result) |
            Where-Object { $_.process -eq 'Notepad' -and $_.title -like "*$([System.IO.Path]::GetFileNameWithoutExtension($temporaryDocument))*" } |
            Select-Object -First 1
        if ($null -eq $target) {
            Start-Sleep -Milliseconds 100
        }
    }

    Assert-Condition ($null -ne $target) "could not find the controlled Notepad window"
    $observed = Invoke-Native 'observe' @{ window_id = $target.id } $nextId
    $nextId++
    Assert-Condition ([bool]$observed.result.state_id) 'observe did not return state_id'
    Assert-Condition ([bool]$observed.result.screenshot) 'observe did not return screenshot data'
    Assert-Condition ([bool]$observed.result.capture.backend) 'observe did not return capture backend diagnostics'

    $capture = $observed.result.capture
    Write-Output ("SMOKE observe: backend={0}; fallback_used={1}; size={2}x{3}" -f $capture.backend, $capture.fallback_used, $capture.width, $capture.height)
    Assert-Condition ($observed.result.coordinate_spaces.screenshot -eq 'window') 'screenshot coordinate space is not window'
    Assert-Condition ($observed.result.coordinate_spaces.elements -eq 'screen') 'UIA coordinate space is not screen'

    $text = "Computer Use smoke test {0}" -f ([Guid]::NewGuid().ToString('N').Substring(0, 8))
    $document = @($observed.result.elements) | Where-Object { $_.role -eq 'Document' } | Select-Object -First 1
    $typeAction = @{ type = 'type_text'; text = $text }
    if ($null -ne $document) {
        $typeAction.element_id = $document.id
    }

    $typeAction.expect = @{
        ui_changed = $true
        ui_stable = $true
    }
    $undoAction = @{
        type = 'press_key'
        key = 'CTRL+Z'
        expect = @{
            ui_changed = $true
            ui_stable = $true
        }
    }
    $workflow = Invoke-Native 'perform' @{
        window_id = $target.id
        actions = @($typeAction, $undoAction)
        verify = $true
    } $nextId
    $nextId++
    Assert-Condition ($workflow.result.status -eq 'succeeded') 'workflow did not succeed'
    Assert-Condition ($workflow.result.verified -eq $true) 'workflow did not return a verified final observation'
    $trace = @($workflow.result.execution_trace)
    Assert-Condition ($trace.Count -eq 2) 'workflow trace did not include both actions'
    Assert-Condition ($trace[0].verification.passed -eq $true) 'type_text postcondition did not pass'
    Assert-Condition ($trace[1].verification.passed -eq $true) 'undo postcondition did not pass'
    Assert-Condition ([bool]$workflow.result.screenshot) 'workflow did not return final screenshot data'
    Assert-Condition ([bool]$workflow.result.capture.backend) 'workflow did not return final capture diagnostics'
    Write-Output ("SMOKE workflow: postconditions passed; trace_steps={0}; backend={1}; fallback_used={2}" -f $trace.Count, $workflow.result.capture.backend, $workflow.result.capture.fallback_used)
}
finally {
    if ($null -ne $native -and -not $native.HasExited) {
        $native.StandardInput.Close()
        if (-not $native.WaitForExit(3000)) {
            $native.Kill()
            $native.WaitForExit()
        }
    }

    if ($null -ne $target) {
        $windowHandle = [IntPtr]::new([long]$target.id)
        [void][ComputerUseSmokeNativeMethods]::PostMessage($windowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }

    if ($null -ne $notepadLauncher -and -not $notepadLauncher.HasExited) {
        $notepadLauncher.Refresh()
        if ($existingNotepadPids -notcontains $notepadLauncher.Id) {
            $notepadLauncher.CloseMainWindow() | Out-Null
            if (-not $notepadLauncher.WaitForExit(1500)) {
                $notepadLauncher.Kill()
                $notepadLauncher.WaitForExit()
            }
        }
    }

    if ([System.IO.File]::Exists($temporaryDocument)) {
        [System.IO.File]::Delete($temporaryDocument)
    }
}

Write-Output 'SMOKE PASSED'
