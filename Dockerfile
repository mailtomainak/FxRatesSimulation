# escape=`
# -----------------------------------------------------------------------------
# Runtime-only image for the .NET Framework load tester.
#
# The app is compiled by msbuild on the CI runner (see the GitHub workflow) and
# only the output folder is copied in. This avoids pulling the multi-GB .NET
# Framework SDK image and running build commands inside a container on CI.
#
# The ltsc2025 base MUST match the AKS Windows node OS (Windows Server 2025).
# AKS runs Windows containers with process isolation only, so an ltsc2022 image
# won't run on a 2025 node and vice versa.
#
# If CI ever fails with "hcs::System::Start" errors, the base image is newer
# than the runner's kernel: pin a dated tag from MCR instead, e.g.
#   4.8.1-YYYYMMDD-windowsservercore-ltsc2025
# -----------------------------------------------------------------------------
ARG BASE_TAG=4.8.1-windowsservercore-ltsc2025
FROM mcr.microsoft.com/dotnet/framework/runtime:${BASE_TAG}

WORKDIR C:\app
COPY publish/ .

# The app should write its report file(s) under this folder.
ENV REPORT_DIR=C:\reports


ENTRYPOINT ["C:\\app\\FxRatesSimulation.exe"]
