param([Parameter(Mandatory)] [string] $Picker)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class AppBarCheck {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] static extern bool SystemParametersInfo(int action, int arg, out Rect rect, int flags);
    [DllImport("user32")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32")] public static extern IntPtr SendMessage(IntPtr window, int msg, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static Rect WorkArea() {
        Rect rect;
        if (!SystemParametersInfo(0x30, 0, out rect, 0)) throw new Exception("Cannot read the Windows work area");
        return rect;
    }
}
'@
function Wait-For([scriptblock] $probe, [string] $message) {
    $until = [DateTime]::UtcNow.AddSeconds(15)
    do {
        [Windows.Forms.Application]::DoEvents()
        $value = & $probe
        if ($value) { return $value }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $until)
    throw $message
}

[void][AppBarCheck]::SetThreadDpiAwarenessContext([IntPtr](-4))
$original = [AppBarCheck]::WorkArea()
$app = New-Object Windows.Forms.Form
$app.Text = "Phone workspace layout $PID"
$app.StartPosition = 'Manual'
$app.Location = New-Object Drawing.Point ($original.Left + 20), ($original.Top + 20)
$button = New-Object Windows.Forms.Button
$button.Text = 'Send'
$button.Anchor = 'Bottom, Right'
$button.Location = New-Object Drawing.Point ($app.ClientSize.Width - 100), ($app.ClientSize.Height - 50)
$app.Controls.Add($button)
$app.Show()
$eventName = "Local\RustDeskPhone-$([guid]::NewGuid())"
$stop = [Threading.EventWaitHandle]::new($false, 'ManualReset', $eventName)
$screen = [Windows.Forms.Screen]::PrimaryScreen
try {
    $process = [Diagnostics.Process]::Start((Resolve-Path $Picker).Path, "$($screen.DeviceName) $eventName")
    $buttons = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
    $tile = Wait-For {
        $process.Refresh()
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) { return }
        $root = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $root.FindAll([Windows.Automation.TreeScope]::Descendants, $buttons) |
            Where-Object { $_.Current.Name -like "*$($app.Text)*" } | Select-Object -First 1
    } 'The test application was missing from the picker'
    $pickerWindow = $process.MainWindowHandle
    $tile.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $area = Wait-For {
        $current = [AppBarCheck]::WorkArea()
        if ($current.Bottom -lt $original.Bottom -and $app.WindowState -eq 'Maximized') { $current }
    } 'The maximized application did not get a separate navigation area'
    $client = $app.RectangleToScreen($app.ClientRectangle)
    if ($client.Bottom -gt $area.Bottom) { throw "Application bottom $($client.Bottom) overlaps navigation at $($area.Bottom)" }
    $hit = $button.PointToScreen([Drawing.Point]::new(10, 10))
    if ([AppBarCheck]::WindowFromPoint($hit) -ne $button.Handle) { throw 'The bottom-right Send button is covered' }

    $bar = [AppBarCheck]::WindowFromPoint([Drawing.Point]::new($area.Left + 36, $area.Bottom + 10))
    [uint32] $owner = 0
    [void][AppBarCheck]::GetWindowThreadProcessId($bar, [ref]$owner)
    if ($owner -ne $process.Id) { throw 'The switch button is missing from the reserved area' }
    $point = [IntPtr](36 + (10 -shl 16))
    [void][AppBarCheck]::SendMessage($bar, 0x201, [IntPtr]1, $point)
    [void][AppBarCheck]::SendMessage($bar, 0x202, [IntPtr]0, $point)
    [void](Wait-For {
        $process.Refresh()
        $process.MainWindowHandle -eq $pickerWindow -and [AppBarCheck]::WorkArea().Bottom -eq $original.Bottom
    } 'Switching applications did not reopen the picker and release the reserved space')
    Write-Output "Bottom-right Send button received the hit. Switching restored the work area from $($area.Bottom) to $($original.Bottom)."
} finally {
    [void]$stop.Set()
    if ($process -and !$process.WaitForExit(6000)) { $process.Kill() }
    $stop.Dispose()
    $app.Dispose()
}
