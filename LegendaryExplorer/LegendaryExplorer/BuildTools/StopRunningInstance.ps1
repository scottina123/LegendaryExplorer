param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'

# A 32-bit MSBuild host otherwise starts PowerShell unable to read LEX's 64-bit path.
if ([System.Environment]::Is64BitOperatingSystem -and -not [System.Environment]::Is64BitProcess) {
    & "$env:SystemRoot\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath -ExecutablePath $ExecutablePath
    exit $LASTEXITCODE
}

$executableFullPath = [System.IO.Path]::GetFullPath($ExecutablePath)
$processName = [System.IO.Path]::GetFileNameWithoutExtension($executableFullPath)

foreach ($process in [System.Diagnostics.Process]::GetProcessesByName($processName)) {
    try {
        if ($process.HasExited) {
            continue
        }

        try {
            $processPath = $process.MainModule.FileName
        }
        catch [System.ComponentModel.Win32Exception] {
            # Never stop an instance whose executable path cannot be verified.
            if (-not $process.HasExited) {
                Write-Warning "Cannot inspect $processName (PID $($process.Id)): $($_.Exception.Message)"
            }
            continue
        }

        if (-not [string]::Equals($processPath, $executableFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        Write-Host "Stopping $processName (PID $($process.Id)) before rebuilding $executableFullPath"

        # Allow normal shutdown, then force termination so a hidden window or save
        # prompt cannot leave the build output locked. Save edits before rebuilding.
        if ($process.CloseMainWindow() -and $process.WaitForExit(5000)) {
            continue
        }

        if (-not $process.HasExited) {
            Write-Host "Terminating $processName (PID $($process.Id)) to release the build output."
            $process.Kill()
        }

        if (-not $process.WaitForExit(10000)) {
            throw "Timed out waiting for $processName (PID $($process.Id)) to exit."
        }
    }
    catch {
        # The process can exit between enumeration, path inspection and shutdown.
        if (-not $process.HasExited) {
            throw
        }
    }
    finally {
        $process.Dispose()
    }
}
