# =============================================================================
#  Shared settings. Edit once; every script dot-sources this file.
# =============================================================================
$Location       = "westeurope"
$ResourceGroup  = "rg-loadtest"
$AcrName        = "acrloadtest12345"     # globally unique, 5-50 chars, letters/digits only
$AksName        = "aks-loadtest"
$StorageAccount = "stloadtest12345"      # globally unique, 3-24 chars, lowercase letters/digits
$ReportShare    = "reports"
$WinPoolName    = "win25"                # Windows pool names: max 6 chars, lowercase
$WinVmSize      = "Standard_D4s_v5"      # 4 vCPU / 16 GB; Windows itself uses ~2 GB of that
$WinNodeCount   = 1
$Namespace      = "loadtest"
$ImageRepo      = "loadtester"           # repository name inside ACR
$GitHubRepo     = "https://github.com/mailtomainak/FxRatesSimulation"  # owner/name of the GitHub repo

# Fail fast: stop on PowerShell errors AND on non-zero exit codes from az/kubectl/gh.
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

if ($AcrName        -notmatch '^[a-zA-Z0-9]{5,50}$')  { throw "AcrName must be 5-50 letters/digits." }
if ($StorageAccount -notmatch '^[a-z0-9]{3,24}$')     { throw "StorageAccount must be 3-24 lowercase letters/digits." }
if ($WinPoolName    -notmatch '^[a-z][a-z0-9]{0,5}$') { throw "WinPoolName must be 1-6 lowercase letters/digits, starting with a letter." }
