param([int]$TargetPid, [string]$Out)

# Delivers Ctrl+C to a process's console the way a terminal would, so the host
# goes through its graceful shutdown path. Runs in its own powershell.exe
# because it detaches from its own console to attach to the target's.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ConsoleCtrl {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
'@

[ConsoleCtrl]::FreeConsole() | Out-Null
if (-not [ConsoleCtrl]::AttachConsole([uint32]$TargetPid)) {
    "attach failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" | Out-File -FilePath $Out -Encoding ascii
    exit 1
}
[ConsoleCtrl]::SetConsoleCtrlHandler([IntPtr]::Zero, $true) | Out-Null
$sent = [ConsoleCtrl]::GenerateConsoleCtrlEvent(0, 0)
Start-Sleep -Milliseconds 500
[ConsoleCtrl]::FreeConsole() | Out-Null
"ctrl+c sent: $sent" | Out-File -FilePath $Out -Encoding ascii
