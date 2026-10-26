<#
    eap-link-sim.ps1 - stand in for one end of the EAP <-> OES event link.

    The wire rules are docs/eap-tool-event-tcp-spec-zh-TW.md: the OES connects to
    the EAP, the EAP sends one line per message (EVT for each main-tool event, HB
    every 30 s and on accept), the OES answers every line with exactly one line.

    -Mode Eap   Pretend to be the EAP, to test the OES without one. Listens on
                -Port, waits for the OES to connect, sends a heartbeat at once and
                every -HeartbeatSeconds, and sends what you type. Shortcuts:
                  start | end              PM3 Chamber Process start / end
                  clean-start | clean-end  PM3 Chamber Clean start / end
                  leak-start | leak-end    PM3 Auto Leak check start / end
                  event <CEID>             any CEID, next SEQ, time now
                  hb                       a heartbeat now
                  unknown                  STATE 2 from now on (EAP lost track)
                  link-down | link-up      the EAP's SECS link to the tool
                  silent <s>               send nothing for s seconds (OES timeout)
                  resend                   the last EVT again, same SEQ
                  drop                     close the connection, listen again
                  sleep <s>                pause (useful in -ScriptFile files)
                  quit
                Anything else is sent as typed (to try malformed lines).
                -ScriptFile <file> plays one command per line first ('#' comments),
                then carries on with heartbeats and whatever is typed.

    -Mode Oes   Pretend to be the OES, for the EAP vendor to test against before
                the real one is connected. Connects to -RemoteHost:-Port,
                reconnects every 10 s, prints every line and answers it the way
                the OES does (ACK / HBACK / ERR), and warns after -TimeoutSeconds
                with nothing received.

        powershell -ExecutionPolicy Bypass -File .\eap-link-sim.ps1 -Mode Eap -Port 20347
        powershell -ExecutionPolicy Bypass -File .\eap-link-sim.ps1 -Mode Oes -RemoteHost 10.2.3.120 -Port 20347

    To test the OES on one PC: run -Mode Eap -Bind 127.0.0.1, and point the OES's
    SECS tab "EAP link" at 127.0.0.1 and the same port.

    Windows PowerShell 5.1, nothing to install. Keep this file ASCII: 5.1 reads a
    script without a BOM in the ANSI code page.
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('Eap', 'Oes')][string]$Mode,
    [int]$Port = 20347,
    [string]$Bind = '0.0.0.0',
    [string]$RemoteHost = '10.2.3.120',
    [int]$HeartbeatSeconds = 30,
    [int]$TimeoutSeconds = 75,
    [string]$ScriptFile = ''
)

$ErrorActionPreference = 'Stop'
$ascii = [System.Text.Encoding]::ASCII

function Stamp { (Get-Date).ToString('HH:mm:ss.fff') }
function Show([string]$dir, [string]$text, [string]$color = 'Gray') {
    Write-Host ("{0}  {1}  {2}" -f (Stamp), $dir, $text) -ForegroundColor $color
}

# Reads one line from a NetworkStream, waiting at most $ms. Returns $null on
# timeout, '<closed>' when the peer closed.
function Read-Line($stream, [System.Collections.Generic.List[byte]]$buf, [int]$ms) {
    $deadline = (Get-Date).AddMilliseconds($ms)
    while ($true) {
        for ($i = 0; $i -lt $buf.Count; $i++) {
            if ($buf[$i] -eq 10) {
                $bytes = $buf.GetRange(0, $i).ToArray()
                $buf.RemoveRange(0, $i + 1)
                $line = $ascii.GetString($bytes)
                return $line.TrimEnd("`r")
            }
        }
        if ((Get-Date) -ge $deadline) { return $null }
        if ($stream.DataAvailable) {
            $chunk = New-Object byte[] 4096
            $n = $stream.Read($chunk, 0, $chunk.Length)
            if ($n -le 0) { return '<closed>' }
            for ($j = 0; $j -lt $n; $j++) { $buf.Add($chunk[$j]) }
        } else {
            Start-Sleep -Milliseconds 50
        }
    }
}

function Send-Line($stream, [string]$text) {
    $bytes = $ascii.GetBytes($text + "`r`n")
    $stream.Write($bytes, 0, $bytes.Length)
}

# ============================================================== EAP side
if ($Mode -eq 'Eap') {
    $ceids = @{
        'start' = 3100439529; 'end' = 3100439530
        'clean-start' = 3099591660; 'clean-end' = 3099591661
        'leak-start' = 3100167145; 'leak-end' = 3100167146
    }
    $starts = @(3100439529, 3099591660, 3100167145)
    $ends = @(3100439530, 3099591661, 3100167146)
    $script:seq = 0
    $script:state = 2; $script:winCeid = ''; $script:winTime = ''; $script:toolLink = 1
    $script:lastEvt = $null
    $script:silentUntil = Get-Date
    $script:nextHb = Get-Date

    function Hb { "HB,$script:state,$script:winCeid,$script:winTime,$script:toolLink,$script:seq" }

    # Sends one line and waits for the answer. $false when the connection is gone.
    function Ask([string]$line) {
        Show 'EAP->OES' $line 'Cyan'
        try { Send-Line $script:stream $line } catch { Show '   ' "send failed: $($_.Exception.Message)" 'Red'; return $false }
        $reply = Read-Line $script:stream $script:buf 10000
        if ($null -eq $reply) { Show '   ' 'no answer within 10 s (spec 7.3: the EAP closes and waits)' 'Red'; return $false }
        if ($reply -eq '<closed>') { Show '   ' 'the OES closed the connection' 'Red'; return $false }
        $color = if ($reply -match ',0$') { 'Green' } else { 'Yellow' }
        Show 'OES->EAP' $reply $color
        return $true
    }

    # One command, typed or from -ScriptFile. Returns 'ok', 'drop', 'quit' or 'lost'.
    function Run-Command([string]$cmd) {
        $words = $cmd.Trim() -split '\s+'
        $line = $null
        switch -Regex ($words[0]) {
            '^quit$' { return 'quit' }
            '^drop$' { Show '   ' 'dropping the connection' 'Yellow'; return 'drop' }
            '^sleep$' { Start-Sleep -Seconds ([double]$words[1]); return 'ok' }
            '^hb$' { $line = Hb }
            '^unknown$' { $script:state = 2; $script:winCeid = ''; $script:winTime = ''; $line = Hb }
            '^link-down$' { $script:toolLink = 0; $script:state = 2; $script:winCeid = ''; $script:winTime = ''; $line = Hb }
            '^link-up$' { $script:toolLink = 1; $line = Hb }   # STATE stays 2 until the next event (spec 5.2)
            '^silent$' {
                $s = if ($words.Count -gt 1) { [int]$words[1] } else { 90 }
                $script:silentUntil = (Get-Date).AddSeconds($s); $script:nextHb = $script:silentUntil
                Show '   ' "sending nothing for $s s" 'Yellow'
                return 'ok'
            }
            '^resend$' {
                if (-not $script:lastEvt) { Show '   ' 'nothing to resend' 'Yellow'; return 'ok' }
                $line = $script:lastEvt
            }
            '^(event|start|end|clean-start|clean-end|leak-start|leak-end)$' {
                $ceid = if ($words[0] -eq 'event') { [uint32]$words[1] } else { [uint32]$ceids[$words[0]] }
                $script:seq++
                $t = (Get-Date).ToString('yyyyMMddHHmmssff')
                $line = "EVT,$ceid,$t,$script:seq"
                $script:lastEvt = $line
                if ($starts -contains $ceid) { $script:state = 1; $script:winCeid = "$ceid"; $script:winTime = $t }
                elseif ($ends -contains $ceid) { $script:state = 0; $script:winCeid = ''; $script:winTime = '' }
            }
            default { $line = $cmd }
        }
        if (Ask $line) { return 'ok' } else { return 'lost' }
    }

    $interactive = $true
    try { $null = [Console]::KeyAvailable } catch { $interactive = $false }
    $pending = New-Object 'System.Collections.Generic.Queue[string]'
    if ($ScriptFile) {
        foreach ($l in Get-Content -LiteralPath $ScriptFile) {
            $t = $l.Trim()
            if ($t -ne '' -and -not $t.StartsWith('#')) { $pending.Enqueue($t) }
        }
    }

    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Parse($Bind), $Port)
    $listener.Start()
    Write-Host "Fake EAP listening on ${Bind}:$Port. Point the OES at this PC. Ctrl+C or 'quit' to stop."

    $quit = $false
    while (-not $quit) {
        Show '   ' 'waiting for the OES to connect...' 'DarkGray'
        $client = $listener.AcceptTcpClient()
        $script:stream = $client.GetStream()
        $script:buf = New-Object 'System.Collections.Generic.List[byte]'
        Show '   ' "OES connected from $($client.Client.RemoteEndPoint)" 'Green'
        $script:nextHb = Get-Date   # E9: a heartbeat at once

        while ($true) {
            $now = Get-Date
            if ($now -ge $script:nextHb -and $now -ge $script:silentUntil) {
                if (-not (Ask (Hb))) { break }
                $script:nextHb = (Get-Date).AddSeconds($HeartbeatSeconds)
            }
            if ($script:stream.DataAvailable) {
                $stray = Read-Line $script:stream $script:buf 100
                if ($stray -eq '<closed>') { Show '   ' 'the OES closed the connection' 'Red'; break }
                if ($stray) { Show 'OES->EAP' "$stray   (unexpected: the OES only answers)" 'Yellow' }
            }
            $cmd = $null
            if ($pending.Count -gt 0) { $cmd = $pending.Dequeue(); Show 'script ' $cmd 'DarkGray' }
            elseif ($interactive -and [Console]::KeyAvailable) { $cmd = (Read-Host).Trim() }
            if (-not $cmd) { Start-Sleep -Milliseconds 100; continue }
            $result = Run-Command $cmd
            if ($result -eq 'quit') { $quit = $true; break }
            if ($result -ne 'ok') { break }
        }
        $client.Close()
    }
    $listener.Stop()
    return
}

# ============================================================== OES side
function Answer([string]$text, [hashtable]$seen) {
    if ($text.Trim() -eq '') { return $null }
    foreach ($c in $text.ToCharArray()) { if ([int]$c -gt 127) { return 'ERR,3' } }
    if ($text.Length -gt 256) { return 'ERR,3' }
    $f = $text.Split(',')
    $num = '^[0-9]{1,10}$'
    $time = '^([0-9]{14}|[0-9]{16})?$'
    switch -CaseSensitive ($f[0]) {
        'EVT' {
            if ($f.Count -ne 4 -or $f[3] -notmatch $num) { return 'ERR,3' }
            if ($f[1] -notmatch $num -or $f[2] -notmatch $time) { return "ACK,$($f[3]),3" }
            if ($seen.Last -eq $f[3]) { Show '   ' "(SEQ $($f[3]) is a resend: acknowledged, not applied again)" 'DarkGray' }
            elseif ($seen.Last -and [uint32]$f[3] -gt [uint32]$seen.Last + 1) { Show '   ' "!! SEQ jumped from $($seen.Last) to $($f[3])" 'Yellow' }
            $seen.Last = $f[3]
            return "ACK,$($f[3]),0"
        }
        'HB' {
            if ($f.Count -ne 6) { return 'HBACK,3' }
            if ($f[1] -notmatch '^[012]$' -or $f[4] -notmatch '^[01]$' -or $f[5] -notmatch $num) { return 'HBACK,3' }
            if ($f[1] -eq '1' -and ($f[2] -notmatch $num -or $f[3] -notmatch $time)) { return 'HBACK,3' }
            if ($f[1] -ne '1' -and ($f[2] -ne '' -or $f[3] -ne '')) { return 'HBACK,3' }
            return 'HBACK,0'
        }
        default { return 'ERR,1' }
    }
}

Write-Host "Fake OES connecting to ${RemoteHost}:$Port, retrying every 10 s. Ctrl+C to stop."
Write-Host "Note: this answers 0 to every well-formed line; the real OES also checks CEIDs against its table."
$seen = @{ Last = $null }
while ($true) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect($RemoteHost, $Port)
    } catch {
        Show '   ' "could not connect: $($_.Exception.InnerException.Message)$($_.Exception.Message)" 'Red'
        Start-Sleep -Seconds 10
        continue
    }
    Show '   ' "connected to ${RemoteHost}:$Port - the EAP should send a heartbeat now (E9)" 'Green'
    $stream = $client.GetStream()
    $buf = New-Object 'System.Collections.Generic.List[byte]'
    $first = $true
    $last = Get-Date
    while ($true) {
        $line = Read-Line $stream $buf 1000
        if ($line -eq '<closed>') { Show '   ' 'the EAP closed the connection' 'Red'; break }
        if ($null -eq $line) {
            if (((Get-Date) - $last).TotalSeconds -ge $TimeoutSeconds) {
                Show '   ' "nothing for $TimeoutSeconds s - the real OES sets the window unknown and reconnects" 'Red'
                break
            }
            continue
        }
        $last = Get-Date
        if ($first -and -not $line.StartsWith('HB,')) {
            Show '   ' '!! the first line after connecting should be a heartbeat (E9)' 'Yellow'
        }
        $first = $false
        Show 'EAP->OES' $line 'Cyan'
        $reply = Answer $line $seen
        if ($null -eq $reply) { continue }
        Send-Line $stream $reply
        $color = if ($reply -match ',0$') { 'Green' } else { 'Yellow' }
        Show 'OES->EAP' $reply $color
    }
    $client.Close()
    Start-Sleep -Seconds 10
}
