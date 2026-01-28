FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy Directory.Packages.props for central package management
COPY ["Directory.Packages.props", "./"]

# Copy project files
COPY ["src/DonkeyWork.CodeSandbox.McpServer/DonkeyWork.CodeSandbox.McpServer.csproj", "DonkeyWork.CodeSandbox.McpServer/"]
RUN dotnet restore "DonkeyWork.CodeSandbox.McpServer/DonkeyWork.CodeSandbox.McpServer.csproj"

# Copy source code
COPY ["src/", "."]
WORKDIR "/src/DonkeyWork.CodeSandbox.McpServer"
RUN dotnet build "DonkeyWork.CodeSandbox.McpServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DonkeyWork.CodeSandbox.McpServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=publish /app/publish .

# Install bash, curl, and Node.js (required for npx MCP servers)
RUN apt-get update && apt-get install -y \
    bash \
    curl \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 8666
ENTRYPOINT ["dotnet", "DonkeyWork.CodeSandbox.McpServer.dll"]
