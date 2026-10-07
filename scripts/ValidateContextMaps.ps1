$ErrorActionPreference = 'Stop'
$core = Join-Path $PSScriptRoot '../src/AgentOS.Core'
$micro = Get-Content (Join-Path $core 'ContextMicroagent.cs') -Raw
$maps = Get-Content (Join-Path $core 'TaskMaps.cs') -Raw
$runtime = Get-Content (Join-Path $core 'ProjectRuntime.cs') -Raw
$capture = Get-Content (Join-Path $core 'ForegroundCapture.cs') -Raw
$broker = Get-Content (Join-Path $core 'ProjectBroker.cs') -Raw
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
Require (($micro | Select-String -Pattern 'using var limit=CancellationTokenSource' -AllMatches).Matches.Count -eq 1) 'Context deadline must have one linked timer.'
Require ($micro.Contains('CancelAfter(TimeSpan.FromSeconds(20))')) 'Context deadline changed.'
Require ($micro.Contains('bool includeScreenshot=false') -and $micro.Contains('!includeScreenshot||context.ScreenshotPng is null')) 'Screenshot sharing requires explicit caller choice.'
Require ($micro.Contains('JsonDocument.Parse(raw.Trim())') -and $micro.Contains('Cyclic dependencies.') -and $micro.Contains('suggestions.Length!=3')) 'Structured draft validation is missing.'
Require ($maps.Contains('public List<string> WorkIds') -and $maps.Contains('LaunchReadyMapTasks(map)') -and $maps.Contains('task.Acceptance.Trim()')) 'Map attempt tracking or scheduling is missing.'
Require ($maps.Contains('map.Tasks.Count < 1') -and -not $maps.Contains('map.Tasks.Count is < 1 or > 8')) 'Manual task cap remains.'
Require ($runtime.Contains('ScheduleSelectedMapTasks();') -and $runtime.Contains('map.Status = MapStatus.Canceled')) 'Work outcome or stop map hook is missing.'
Require ($capture.Contains('WindowFromPoint') -and $capture.Contains('BitBlt(dc,0,0,width,height,screen') -and -not $capture.Contains('PrintWindow(hwnd,dc')) 'Screenshot source is not visible screen pixels.'
Require ($broker.Contains('pipe.ConnectAsync(3500, cancel)') -and $broker.Contains('WaitNamedPipe(name, 0)')) 'Broker busy distinction is missing.'
Write-Output 'Context/map source checks passed.'
