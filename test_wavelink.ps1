try {
    $port = 52246
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.Options.SetRequestHeader("Origin", "streamdeck://")
    $cts = New-Object System.Threading.CancellationTokenSource(15000)
    $ws.ConnectAsync([Uri]"ws://127.0.0.1:$port", $cts.Token).GetAwaiter().GetResult()

    # Send getChannels
    $msg = '{"jsonrpc":"2.0","id":10,"method":"getChannels"}'
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
    $segment = New-Object System.ArraySegment[byte](,$bytes)
    $ws.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).GetAwaiter().GetResult()

    # Read messages and print non-levelMeter ones
    for ($i = 0; $i -lt 50; $i++) {
        $buf = New-Object byte[] 65536
        $seg = New-Object System.ArraySegment[byte](,$buf)
        $cts2 = New-Object System.Threading.CancellationTokenSource(2000)
        try {
            $result = $ws.ReceiveAsync($seg, $cts2.Token).GetAwaiter().GetResult()
            $response = [System.Text.Encoding]::UTF8.GetString($buf, 0, $result.Count)
            if (-not $response.Contains("levelMeterChanged")) {
                Write-Host "[$i] $response"
                Write-Host ""
            }
        } catch {
            break
        }
    }

    try { $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, '', [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() } catch {}
} catch {
    Write-Host "Error: $($_.Exception.Message)"
}
