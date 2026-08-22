[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Start-IsolationExperiment.ps1') -Mode Normal
