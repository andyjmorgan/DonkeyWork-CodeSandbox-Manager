FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy Directory.Packages.props for central package management
COPY ["Directory.Packages.props", "./"]

# Copy nuget.config if present (for CI with custom package source)
COPY nuget.config* ./

# Copy project files
COPY ["src/DonkeyWork.CodeSandbox.Contracts/DonkeyWork.CodeSandbox.Contracts.csproj", "DonkeyWork.CodeSandbox.Contracts/"]
COPY ["src/DonkeyWork.CodeSandbox.Server/DonkeyWork.CodeSandbox.Server.csproj", "DonkeyWork.CodeSandbox.Server/"]
RUN dotnet restore "DonkeyWork.CodeSandbox.Server/DonkeyWork.CodeSandbox.Server.csproj"

# Copy source code
COPY ["src/", "."]
WORKDIR "/src/DonkeyWork.CodeSandbox.Server"
RUN dotnet build "DonkeyWork.CodeSandbox.Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DonkeyWork.CodeSandbox.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=publish /app/publish .

# Install bash, Python, Node.js, and common utilities
RUN apt-get update && apt-get install -y \
    bash \
    curl \
    iputils-ping \
    python3 \
    python3-pip \
    python3-venv \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs \
    && rm -rf /var/lib/apt/lists/*

# Install common Python packages
RUN pip3 install --no-cache-dir --break-system-packages \
    requests \
    numpy \
    pandas

# Install common Node.js packages globally
RUN npm install -g \
    typescript \
    ts-node

EXPOSE 8666
ENTRYPOINT ["dotnet", "DonkeyWork.CodeSandbox.Server.dll"]
