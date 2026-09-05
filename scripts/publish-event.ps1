param(
    [Parameter(Mandatory=$true)][string]$AgentId,
    [Parameter(Mandatory=$true)][ValidateSet('started','updated','waiting','attention','completed','error')][string]$State,
    [string]$Name,
    [string]$Task,
    [string]$Message,
    [string]$ParentId,
    [string]$Role = 'builder',
    [string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) '.orbit\activity.jsonl')
)
$ErrorActionPreference = 'Stop'
$eventPath = [IO.Path]::GetFullPath($Path)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($eventPath)) | Out-Null
$record = @{type="agent.$State"; agent_id=$AgentId; timestamp=[DateTime]::UtcNow.ToString('o'); role=$Role}
if ($Name) { $record.name = $Name }
if ($Task) { $record.task = $Task }
if ($Message) { $record.message = $Message }
if ($ParentId) { $record.parent_id = $ParentId }
$line = ($record | ConvertTo-Json -Compress) + "`n"
$bytes = [Text.Encoding]::UTF8.GetBytes($line)
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        $stream = [IO.File]::Open($eventPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush() } finally { $stream.Dispose() }
        Write-Output "Published agent.$State for $AgentId"
        return
    } catch [IO.IOException] {
        if ($attempt -eq 19) { throw }
        Start-Sleep -Milliseconds 50
    }
}
