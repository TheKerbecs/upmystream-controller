# End-to-end check on a real Windows desktop. Run it on the desktop, not over plain ssh: SendInput only reaches the
# interactive desktop. Writes keys.log next to this script. Overwrites %APPDATA%\upmystream-game-controller\config.json.
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.Security
Remove-Item "$here\keys.log" -ErrorAction SilentlyContinue
# Leftovers from an earlier run: a second Game Controller would refuse to start, a second mock can't bind the port.
Stop-Process -Name upmystream-game-controller -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name like 'python%'" | Where-Object CommandLine -like "*mock_server.py*" |
    ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }

# Test config: key "test" (DPAPI, this user), game = this PowerShell window, chat on, one trigger per case.
$dir = "$env:APPDATA\upmystream-game-controller"
New-Item -ItemType Directory -Force $dir | Out-Null
$prot = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
    [Text.Encoding]::UTF8.GetBytes("test"), $null, "CurrentUser"))
function T($id, $source, $match, $action, $extra) {
    $t = @{ id = $id; enabled = $true; source = $source; match = $match; action = $action; keys = ""; button = "Left";
            holdMs = 50; notches = -1; dx = 0; dy = 0; moveMs = 200; cooldownS = 0 }
    foreach ($e in $extra.GetEnumerator()) { $t[$e.Key] = $e.Value } # not $extra.Keys: that is the "keys" entry
    $t
}
@{
    ApiKeyProtected = $prot; Chat = $true; Game = "powershell"; Hotkey = "Ctrl+Shift+F12"; Server = "https://upmy.stream"
    Triggers = @(
        (T "t1" "redeem" "fireball" "key"    @{ keys = "Shift+H" }),
        (T "t2" "chat"   "w"        "key"    @{ keys = "W"; holdMs = 300 }),
        (T "t3" "chat"   "super"    "key"    @{ keys = "F" }),
        (T "t4" "chat"   "click"    "mouse"  @{ button = "Left"; keys = "Shift" }),
        (T "t5" "chat"   "rclick"   "mouse"  @{ button = "Right" }),
        (T "t6" "chat"   "scroll"   "scroll" @{ notches = -3 }),
        (T "t7" "redeem" "look"     "move"   @{ dx = 200; dy = -100; moveMs = 300 }),
        (T "t8" "chat"   "slow"     "key"    @{ keys = "Ctrl+I"; cooldownS = 60 })
    )
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 "$dir\config.json"

Start-Process python -ArgumentList "`"$here\mock_server.py`" --port 8787 --key test" -WindowStyle Hidden
Start-Sleep 2
Start-Process "$here\upmystream-game-controller.exe" -ArgumentList "--server http://127.0.0.1:8787"
Start-Sleep 5

# The "game": logs keys, mouse buttons, wheel and cursor moves with a millisecond clock. Minimizes at 40 s (actions
# must stop: game not in front), closes at 55 s.
$sw = [Diagnostics.Stopwatch]::StartNew()
$log = "$here\keys.log"
$f = New-Object Windows.Forms.Form -Property @{ Text = "Game Controller target"; KeyPreview = $true; TopMost = $true; Width = 800; Height = 600; StartPosition = "CenterScreen" }
$f.Controls.Add((New-Object Windows.Forms.Label -Property @{ Text = "Game Controller test target: minimizes at 40 s, closes at 55 s"; AutoSize = $true; Left = 20; Top = 20 }))
$f.Add_KeyDown({ Add-Content $log ("{0} down {1} mods={2}" -f $sw.ElapsedMilliseconds, $_.KeyCode, $_.Modifiers) })
$f.Add_KeyUp({ Add-Content $log ("{0} up {1}" -f $sw.ElapsedMilliseconds, $_.KeyCode) })
$f.Add_MouseDown({ Add-Content $log ("{0} mousedown {1} mods={2}" -f $sw.ElapsedMilliseconds, $_.Button, [Windows.Forms.Control]::ModifierKeys) })
$f.Add_MouseUp({ Add-Content $log ("{0} mouseup {1}" -f $sw.ElapsedMilliseconds, $_.Button) })
$f.Add_MouseWheel({ Add-Content $log ("{0} wheel {1}" -f $sw.ElapsedMilliseconds, $_.Delta) })
$f.Add_MouseMove({ $p = [Windows.Forms.Cursor]::Position; Add-Content $log ("{0} move {1} {2}" -f $sw.ElapsedMilliseconds, $p.X, $p.Y) })
$f.Add_Shown({
    $f.Activate()
    $c = $f.PointToScreen((New-Object Drawing.Point 400, 300))
    [Windows.Forms.Cursor]::Position = $c
    Add-Content $log ("ready cursor {0} {1}" -f $c.X, $c.Y)
})
$t = New-Object Windows.Forms.Timer -Property @{ Interval = 40000 }
$t.Add_Tick({
    if ($f.WindowState -eq "Minimized") { $f.Close(); return }
    $f.WindowState = "Minimized"; $f.TopMost = $false
    Add-Content $log ("{0} minimized" -f $sw.ElapsedMilliseconds)
    $t.Interval = 15000
})
$t.Start()
[void]$f.ShowDialog()
Add-Content $log "closed"
