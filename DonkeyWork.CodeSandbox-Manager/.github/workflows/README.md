# GitHub Actions Workflows

This directory contains the CI/CD workflows for the Kata Container Manager project.

## Workflows

### `pr-build-test.yml`
**Purpose:** Validate pull requests before merging

**Triggered by:** Pull requests to `main` branch

**What it does:**
- Builds the .NET solution
- Runs unit tests with code coverage
- Builds Docker image for validation
- Performs code linting and security scanning
- Reports test results

**Status Badge:**
```markdown
[![PR Build and Test](https://github.com/andrewmorgan/DonkeyWork-CodeSandbox-Manager/actions/workflows/pr-build-test.yml/badge.svg)](https://github.com/andrewmorgan/DonkeyWork-CodeSandbox-Manager/actions/workflows/pr-build-test.yml)
```

### `release.yml`
**Purpose:** Automatically release new versions

**Triggered by:** Pushes to `main` branch (after PR merge)

**What it does:**
- Builds and tests the solution
- Determines semantic version using GitVersion
- Builds multi-architecture Docker images (amd64, arm64)
- Pushes images to GitHub Container Registry
- Creates GitHub release with changelog
- Updates Kubernetes deployment manifests

**Status Badge:**
```markdown
[![Release](https://github.com/andrewmorgan/DonkeyWork-CodeSandbox-Manager/actions/workflows/release.yml/badge.svg)](https://github.com/andrewmorgan/DonkeyWork-CodeSandbox-Manager/actions/workflows/release.yml)
```

## Quick Commands

### View Workflow Runs
```bash
# Using GitHub CLI
gh run list --workflow=pr-build-test.yml
gh run list --workflow=release.yml

# View specific run
gh run view <run-id>

# Watch a running workflow
gh run watch
```

### Trigger Workflows Manually
```bash
# Trigger a workflow manually (if configured)
gh workflow run pr-build-test.yml
gh workflow run release.yml
```

### Download Artifacts
```bash
# List artifacts
gh run list --workflow=pr-build-test.yml --json databaseId,artifacts

# Download artifacts from a run
gh run download <run-id>
```

### View Logs
```bash
# View logs for a workflow run
gh run view <run-id> --log

# View logs for a specific job
gh run view <run-id> --job=<job-id> --log
```

## Environment Variables

Both workflows use these environment variables:

```yaml
DOTNET_VERSION: '10.0.x'
DOTNET_SKIP_FIRST_TIME_EXPERIENCE: 1
DOTNET_NOLOGO: true
DOTNET_CLI_TELEMETRY_OPTOUT: 1
REGISTRY: ghcr.io
IMAGE_NAME: ${{ github.repository }}
```

## Permissions

The release workflow requires these permissions:

```yaml
permissions:
  contents: write      # Create releases
  packages: write      # Push Docker images
  issues: write        # Update issues
  pull-requests: write # Create PRs
```

## Caching

Both workflows implement aggressive caching:

1. **NuGet Packages:** Cached by `setup-dotnet` action
2. **Docker Layers:** Cached using GitHub Actions cache backend
3. **Build Artifacts:** Retained for 7-90 days depending on workflow

## Maintenance

### Update Actions Versions

Periodically update action versions in workflows:

```bash
# Check for updates
gh api repos/:owner/:repo/actions/workflows

# Update in workflow files
# - uses: actions/checkout@v4
# - uses: actions/setup-dotnet@v4
# - uses: docker/setup-buildx-action@v3
```

### Monitor Workflow Performance

```bash
# View workflow timing
gh run list --workflow=release.yml --json conclusion,createdAt,updatedAt

# Check cache usage
gh cache list
```

## Troubleshooting

### Workflow Not Triggering

1. Check trigger conditions in workflow file
2. Verify branch protection settings
3. Ensure workflows are enabled in repository settings

### Build Failures

1. Review workflow logs: `gh run view <run-id> --log`
2. Check file paths in trigger conditions
3. Verify dependencies and versions

### Docker Push Failures

1. Verify GHCR is enabled
2. Check workflow permissions
3. Ensure GITHUB_TOKEN has package write access

## Documentation

For detailed CI/CD documentation, see:
- [CICD.md](../CICD.md) - Comprehensive CI/CD guide
- [README.md](../../README.md) - Project documentation

## Support

For workflow issues:
1. Check workflow logs
2. Review [GitHub Actions documentation](https://docs.github.com/en/actions)
3. Open an issue in the repository
