#!/usr/bin/env bash
# Setup script for Ahoy development environment.
# Run once to install dependencies. Idempotent — safe to re-run.

set -euo pipefail

echo "=== Ahoy Development Environment Setup ==="

# .NET 10 SDK
if command -v dotnet &>/dev/null && dotnet --version 2>/dev/null | grep -q '^10\.'; then
    echo "[OK] .NET 10 SDK already installed: $(dotnet --version)"
else
    echo "[INSTALL] Installing .NET 10 SDK..."
    if command -v apt-get &>/dev/null; then
        sudo apt-get update -qq
        sudo apt-get install -y dotnet-sdk-10.0
    elif command -v brew &>/dev/null; then
        brew install dotnet@10
    else
        echo "[FALLBACK] Trying dotnet-install.sh..."
        curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
        export PATH="$HOME/.dotnet:$PATH"
    fi
    echo "[OK] .NET SDK installed: $(dotnet --version)"
fi

# Restore NuGet packages
echo "[RESTORE] Restoring NuGet packages..."
dotnet restore

# Build
echo "[BUILD] Building solution..."
dotnet build --no-restore

# Run tests
echo "[TEST] Running tests..."
dotnet test --no-build --verbosity normal

echo "=== Setup complete ==="
