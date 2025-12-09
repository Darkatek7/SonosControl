#!/bin/bash
set -e

echo "Downloading dotnet-install.sh..."
curl -L https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh

echo "Installing .NET SDK 9.0..."
./dotnet-install.sh --channel 9.0

echo "Configuring environment..."
# Add to .bashrc for future sessions
if ! grep -q "DOTNET_ROOT" ~/.bashrc; then
    echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
    echo 'export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools' >> ~/.bashrc
fi

echo ".NET SDK 9.0 installed successfully."
