#Requires -Version 7.4
<#
  STEP 3 - Provision Azure resources
    - Resource group
    - Azure Container Registry (ACR)
    - AKS cluster: small Linux system pool (mandatory) + Windows Server 2025 user pool
    - Storage account + file share that the test pods write reports to
  Safe to re-run: existing resources are detected and skipped.
  Needs: Azure CLI >= 2.87.0, Owner (or Contributor + User Access Administrator)
         on the subscription/resource group, because --attach-acr assigns a role.
#>
. "$PSScriptRoot/config.ps1"

# Windows Server 2025 node pools need Azure CLI >= 2.87.0
$cliVersion = [version](az version | ConvertFrom-Json).'azure-cli'
if ($cliVersion -lt [version]'2.87.0') {
    throw "Azure CLI $cliVersion is too old. Run 'az upgrade' (need >= 2.87.0)."
}

az account show --query "{subscription:name, id:id}" -o table
Read-Host "Resources will be created in the subscription above. Enter to continue, Ctrl+C to abort"

Write-Host "`n== Resource group" -ForegroundColor Cyan
az group create --name $ResourceGroup --location $Location -o none

Write-Host "`n== Container registry" -ForegroundColor Cyan
az acr create --resource-group $ResourceGroup --name $AcrName --sku Standard -o none

Write-Host "`n== AKS cluster (Linux system pool)" -ForegroundColor Cyan
# An AKS cluster always needs a Linux system pool; Windows can only be a user pool.
# Azure CNI (overlay) is required for Windows nodes (kubenet isn't supported).
$aksExists = az aks list -g $ResourceGroup --query "[?name=='$AksName'].name" -o tsv
if (-not $aksExists) {
    $winPwd   = Read-Host "Password for the Windows node admin account (14+ chars, upper/lower/digit/symbol)" -AsSecureString
    $plainPwd = [System.Net.NetworkCredential]::new('', $winPwd).Password

    az aks create `
        --resource-group $ResourceGroup `
        --name $AksName `
        --location $Location `
        --node-count 1 `
        --node-vm-size Standard_D2s_v5 `
        --network-plugin azure `
        --network-plugin-mode overlay `
        --windows-admin-username winadmin `
        --windows-admin-password $plainPwd `
        --attach-acr $AcrName `
        --node-os-upgrade-channel NodeImage `
        --generate-ssh-keys `
        -o none
} else {
    Write-Host "Cluster $AksName already exists, skipping."
}

Write-Host "`n== Windows Server 2025 node pool" -ForegroundColor Cyan
# --os-sku Windows2025 is set explicitly so the node OS always matches the
# ltsc2025 image, regardless of which OS AKS picks as the default.
# Windows Server 2025 pools require a FIPS-enabled image. If your app uses
# non-FIPS crypto classes (e.g. MD5CryptoServiceProvider, RijndaelManaged),
# watch the first run for FIPS-related exceptions.
$poolExists = az aks nodepool list -g $ResourceGroup --cluster-name $AksName --query "[?name=='$WinPoolName'].name" -o tsv
if (-not $poolExists) {
    az aks nodepool add `
        --resource-group $ResourceGroup `
        --cluster-name $AksName `
        --name $WinPoolName `
        --os-type Windows `
        --os-sku Windows2025 `
        --enable-fips-image `
        --node-vm-size $WinVmSize `
        --node-count $WinNodeCount `
        -o none
} else {
    Write-Host "Node pool $WinPoolName already exists, skipping."
}

Write-Host "`n== Storage for test reports" -ForegroundColor Cyan
az storage account create `
    --resource-group $ResourceGroup `
    --name $StorageAccount `
    --location $Location `
    --sku Standard_LRS `
    --kind StorageV2 `
    --min-tls-version TLS1_2 `
    -o none
$shareExists = az storage share-rm exists -g $ResourceGroup --storage-account $StorageAccount --name $ReportShare --query exists -o tsv
if ($shareExists -ne 'true') {
    az storage share-rm create -g $ResourceGroup --storage-account $StorageAccount --name $ReportShare --quota 100 -o none
}

Write-Host "`n== kubectl credentials" -ForegroundColor Cyan
if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    throw "kubectl not found. Install it (e.g. 'az aks install-cli' or 'winget install Kubernetes.kubectl') and re-run."
}
az aks get-credentials -g $ResourceGroup -n $AksName --overwrite-existing
kubectl get nodes -L kubernetes.io/os -L node.kubernetes.io/windows-build

Write-Host "`nDone. Next: infra/02-setup-github-oidc.ps1" -ForegroundColor Green
