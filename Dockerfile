# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["src/DonkeyWork.CodeSandbox-Manager/DonkeyWork.CodeSandbox-Manager.csproj", "src/DonkeyWork.CodeSandbox-Manager/"]
RUN dotnet restore "src/DonkeyWork.CodeSandbox-Manager/DonkeyWork.CodeSandbox-Manager.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/src/src/DonkeyWork.CodeSandbox-Manager"
RUN dotnet build "DonkeyWork.CodeSandbox-Manager.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "DonkeyWork.CodeSandbox-Manager.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Create non-root user
RUN addgroup --system --gid 1000 appuser \
    && adduser --system --uid 1000 --ingroup appuser appuser

# Copy published app
COPY --from=publish /app/publish .

# Change ownership to non-root user
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose port
EXPOSE 8668

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "DonkeyWork.CodeSandbox-Manager.dll"]
