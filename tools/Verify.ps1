param([switch]$Download)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    function Invoke-Checked {
        param([string[]]$Arguments)
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed ($LASTEXITCODE)." }
    }
    Invoke-Checked @('tool', 'restore')
    $apis = @(
        @{ Name = 'meet.v2'; Url = 'https://meet.googleapis.com/$discovery/rest?version=v2'; Client = 'Meet' },
        @{ Name = 'calendar.v3'; Url = 'https://www.googleapis.com/discovery/v1/apis/calendar/v3/rest'; Client = 'Calendar' }
    )
    New-Item -ItemType Directory -Force artifacts/discovery | Out-Null
    $results = @()
    foreach ($api in $apis) {
        $inputPath = "artifacts/discovery/$($api.Name).json"
        if ($Download) { Invoke-WebRequest $api.Url -OutFile $inputPath }
        else { Copy-Item "test/Soenneker.OpenApi.Converters.GoogleDiscovery.Tests/Fixtures/$($api.Name).json" $inputPath -Force }
        $outputPath = "artifacts/openapi/$($api.Name).json"
        Invoke-Checked @('run', '--project', 'tools/Verify', '--', $inputPath, $outputPath)
        Invoke-Checked @('tool', 'run', 'kiota', '--', 'generate', '-d', $outputPath, '-l', 'CSharp', '-o', "artifacts/kiota/$($api.Client)", '-c', "$($api.Client)Client", '-n', "Google.$($api.Client)", '--ll', 'Warning')
        $document = Get-Content $outputPath -Raw | ConvertFrom-Json -AsHashtable
        $results += @{
            api = $api.Name
            source = $api.Url
            sha256 = (Get-FileHash $inputPath -Algorithm SHA256).Hash
            operations = ($document.paths.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
            schemas = $document.components.schemas.Count
        }
    }
    Invoke-Checked @('test', '--project', 'test/Soenneker.OpenApi.Converters.GoogleDiscovery.Tests', '--', '--treenode-filter', '/*/*/GoogleDiscoveryOpenApiConverterTests/*')
    Invoke-Checked @('run', '--project', 'tools/KiotaSmoke')
    @{ kiotaVersion = '1.36.1'; validatedUtc = [DateTime]::UtcNow.ToString('O'); apis = $results; smokeChecks = 10 } |
        ConvertTo-Json -Depth 6 | Set-Content artifacts/validation.json
}
finally { Pop-Location }
