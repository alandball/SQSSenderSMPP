[CmdletBinding()]
param(
    [ValidateSet('setup', 'restore', 'build', 'test', 'integration-up', 'integration', 'integration-down', 'run', 'smoke', 'load', 'publish', 'verify')]
    [string]$Task = 'test',
    [string]$PythonPath,
    [int]$Envelopes = 1,
    [int]$Children = 50
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $python = if ($IsWindows) { Join-Path $PSScriptRoot '.venv/Scripts/python.exe' } else { Join-Path $PSScriptRoot '.venv/bin/python' }
    function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
        & $Executable @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
    }
    function Require-Python {
        if (!(Test-Path $python)) { throw 'Run ./build.ps1 setup -PythonPath <Python 3.12+ executable> first.' }
    }
    function Wait-Http([string]$Url) {
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try { Invoke-WebRequest -Uri $Url -TimeoutSec 1 | Out-Null; return } catch { Start-Sleep -Milliseconds 500 }
        }
        throw "Local service did not become ready at $Url. See .local/*.err.log."
    }
    function Test-Local([string]$Name) {
        $statePath = Join-Path $PSScriptRoot ".local/$Name.process.json"
        if (Test-Path $statePath) {
            $saved = Get-Content $statePath -Raw | ConvertFrom-Json
            $existing = Get-Process -Id $saved.id -ErrorAction SilentlyContinue
            return $existing -and $existing.StartTime.ToUniversalTime().Ticks -eq $saved.startedTicks -and $existing.Path -eq $saved.executable
        }
        return $false
    }
    function Start-Local([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
        New-Item -ItemType Directory -Path .local -Force | Out-Null
        $statePath = Join-Path $PSScriptRoot ".local/$Name.process.json"
        if (Test-Local $Name) { return }
        $params = @{
            FilePath = $Executable; ArgumentList = $Arguments; WorkingDirectory = $WorkingDirectory; PassThru = $true
            RedirectStandardOutput = (Join-Path $PSScriptRoot ".local/$Name.out.log")
            RedirectStandardError = (Join-Path $PSScriptRoot ".local/$Name.err.log")
        }
        if ($IsWindows) { $params.WindowStyle = 'Hidden' }
        $process = Start-Process @params
        # Process.Path can be temporarily unavailable immediately after launch on Windows.
        # Record the executable we launched; later checks still verify the running process path.
        @{ id = $process.Id; startedTicks = $process.StartTime.ToUniversalTime().Ticks; executable = [System.IO.Path]::GetFullPath($Executable) } |
            ConvertTo-Json | Set-Content $statePath
    }
    function Start-Harness {
        Require-Python
        Invoke-Checked dotnet @('build', '-c', 'Release', '--no-restore')
        Start-Local moto $python @('-m', 'moto.server', '-H', '127.0.0.1', '-p', '5000') $PSScriptRoot
        Wait-Http 'http://127.0.0.1:5000/'
        Invoke-Checked $python @('local/aws.py', 'bootstrap')
        $harness = Join-Path $PSScriptRoot '.local/runtime/simulator/Sender.Harness.dll'
        if (!(Test-Local simulator)) {
            Invoke-Checked dotnet @('publish', 'tools/Sender.Harness', '-c', 'Release', '--no-build', '-o', '.local/runtime/simulator')
        }
        Start-Local simulator (Get-Command dotnet).Source @('"' + $harness + '"') $PSScriptRoot
        Wait-Http 'http://127.0.0.1:5081/health'
    }
    function Start-Worker {
        $savedEnvironment = $env:ASPNETCORE_ENVIRONMENT
        $savedDotnetEnvironment = $env:DOTNET_ENVIRONMENT
        $savedConfig = $env:SENDER_CONFIG_FILE
        try {
            $env:ASPNETCORE_ENVIRONMENT = 'Development'
            $env:DOTNET_ENVIRONMENT = 'Development'
            $env:SENDER_CONFIG_FILE = $null
            $worker = Join-Path $PSScriptRoot '.local/runtime/worker/Sender.Worker.dll'
            if (!(Test-Local worker)) {
                Invoke-Checked dotnet @('publish', 'src/Sender.Worker', '-c', 'Release', '--no-build', '-o', '.local/runtime/worker')
            }
            Start-Local worker (Get-Command dotnet).Source @(
                '"' + $worker + '"',
                '--Aws:LocalEmulator=true', '--Aws:ServiceUrl=http://127.0.0.1:5000',
                '--Aws:QueueUrl=http://127.0.0.1:5000/123456789012/sender-local', '--Aws:LedgerTable=sender-local',
                '--Smpp:Host=127.0.0.1', '--Smpp:Port=2775', '--Smpp:SystemId=local', '--Smpp:Password=local', '--Smpp:UseTls=false'
            ) (Split-Path $worker)
        } finally {
            $env:ASPNETCORE_ENVIRONMENT = $savedEnvironment
            $env:DOTNET_ENVIRONMENT = $savedDotnetEnvironment
            $env:SENDER_CONFIG_FILE = $savedConfig
        }
        Wait-Http 'http://127.0.0.1:5080/health/ready'
    }
    switch ($Task) {
        'setup' {
            Invoke-Checked dotnet @('--version')
            if (!(Test-Path $python)) {
                if (!$PythonPath) {
                    $candidate = Get-Command python3,python -ErrorAction SilentlyContinue | Select-Object -First 1
                    if (!$candidate) { throw 'Python 3.12+ is required. Pass -PythonPath with its executable path.' }
                    $PythonPath = $candidate.Source
                }
                Invoke-Checked $PythonPath @('-m', 'venv', '.venv')
            }
            Invoke-Checked $python @('-m', 'pip', 'install', '-r', 'local/requirements.lock.txt')
            Invoke-Checked dotnet @('restore', '--locked-mode')
        }
        'restore' { Invoke-Checked dotnet @('restore', '--locked-mode') }
        'build' { Invoke-Checked dotnet @('build', '-c', 'Release', '--no-restore') }
        'test' { Invoke-Checked dotnet @('test', '-c', 'Release', '--no-restore', '--filter', 'Category!=Integration', '--logger', 'trx', '--results-directory', 'artifacts/tests') }
        'integration-up' { Start-Harness }
        'integration' {
            Start-Harness
            Invoke-Checked dotnet @('test', '-c', 'Release', '--no-build', '--filter', 'Category=Integration', '--logger', 'trx', '--results-directory', 'artifacts/integration')
        }
        'run' { Start-Harness; Start-Worker; Write-Output 'Local sender ready: http://127.0.0.1:5080/health/ready' }
        'smoke' { Start-Harness; Start-Worker; Invoke-Checked $python @('local/aws.py', 'smoke') }
        'load' { Require-Python; Invoke-Checked $python @('local/aws.py', 'seed', '--envelopes', "$Envelopes", '--children', "$Children") }
        'publish' { Invoke-Checked dotnet @('publish', 'src/Sender.Worker', '-c', 'Release', '--no-restore', '-o', 'artifacts/publish') }
        'verify' {
            Invoke-Checked dotnet @('format', '--verify-no-changes', '--no-restore')
            Invoke-Checked dotnet @('test', '-c', 'Release', '--no-restore', '--filter', 'Category!=Integration')
        }
        'integration-down' {
            foreach ($name in @('worker', 'simulator', 'moto')) {
                $path = Join-Path $PSScriptRoot ".local/$name.process.json"
                if (!(Test-Path $path)) { continue }
                $saved = Get-Content $path -Raw | ConvertFrom-Json
                $process = Get-Process -Id $saved.id -ErrorAction SilentlyContinue
                if ($process -and $process.StartTime.ToUniversalTime().Ticks -eq $saved.startedTicks -and $process.Path -eq $saved.executable) {
                    # This is a local hard stop. Graceful drain is tested separately and used by Ctrl+C / SIGTERM.
                    if ($IsWindows) { & taskkill.exe /PID $process.Id /T /F | Out-Null }
                    else { Stop-Process -Id $process.Id }
                }
                Remove-Item -LiteralPath $path
            }
            Write-Output 'Local processes stopped. Logs retained; Moto state is ephemeral.'
        }
    }
} finally { Pop-Location }
