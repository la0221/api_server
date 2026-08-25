param(
    [string]$IpAddress = "127.0.0.1",
    [int]$Port = 5000,
    [string]$Message = "NG"
)

try {
    $client = New-Object System.Net.Sockets.TcpClient
    $client.Connect($IpAddress, $Port)
    $stream = $client.GetStream()
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Message)
    $stream.Write($bytes, 0, $bytes.Length)
    Write-Host "Sent '$Message' to $($IpAddress):$Port"
    Start-Sleep -Milliseconds 200
}
catch {
    Write-Host "Failed to send: $($_.Exception.Message)"
}
finally {
    if ($stream) { $stream.Close() }
    if ($client) { $client.Close() }
}
