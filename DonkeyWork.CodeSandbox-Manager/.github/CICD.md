# CI/CD Pipeline Documentation

This document provides detailed information about the CI/CD pipeline setup for the Kata Container Manager project.

## Table of Contents

1. [Overview](#overview)
2. [Workflows](#workflows)
3. [Setup Instructions](#setup-instructions)
4. [Workflow Details](#workflow-details)
5. [Troubleshooting](#troubleshooting)
6. [Best Practices](#best-practices)

## Overview

The project uses GitHub Actions for continuous integration and continuous deployment. The pipeline is designed to:

- Ensure code quality through automated testing
- Build and validate Docker images
- Automatically version and release the application
- Publish Docker images to GitHub Container Registry
- Maintain deployment manifests

## Workflows

### 1. PR Build and Test (`pr-build-test.yml`)

**Trigger:** Pull requests to the `main` branch

**Purpose:** Validate changes before merging

**Jobs:**
- **build-and-test:** Compiles the .NET solution and runs unit tests
- **docker-build:** Builds Docker image to ensure it builds correctly
- **lint-and-security:** Performs code linting and security scanning
- **pr-status-check:** Aggregates results from all jobs

**Duration:** ~5-8 minutes

### 2. Release (`release.yml`)

**Trigger:** Pushes to the `main` branch (after PR merge)

**Purpose:** Automatically release and deploy new versions

**Jobs:**
- **build-and-test:** Full build and test suite
- **semantic-version:** Determines the next semantic version using GitVersion
- **docker-build-push:** Builds multi-arch images and pushes to GHCR
- **create-release:** Creates GitHub release with changelog
- **update-deployment:** Updates Kubernetes manifests with new version
- **post-release-summary:** Generates comprehensive release summary

**Duration:** ~10-15 minutes

## Setup Instructions

### Prerequisites

1. **GitHub Repository:** Project hosted on GitHub
2. **GitHub Container Registry:** Enabled for the repository
3. **Branch Protection:** Configure branch protection for `main`

### Initial Setup

1. **Enable GitHub Actions**
   - Go to repository Settings > Actions > General
   - Enable "Allow all actions and reusable workflows"
   - Set workflow permissions to "Read and write permissions"

2. **Configure Package Permissions**
   - Go to repository Settings > Actions > General
   - Under "Workflow permissions", check:
     - "Read and write permissions"
     - "Allow GitHub Actions to create and approve pull requests"

3. **Set Up Branch Protection**
   - Go to repository Settings > Branches
   - Add branch protection rule for `main`:
     - Require pull request reviews
     - Require status checks to pass (select PR Build and Test)
     - Require branches to be up to date

4. **No Secrets Required**
   - The workflows use the built-in `GITHUB_TOKEN`
   - No manual secret configuration needed

### Workflow Files Location

```
.github/
└── workflows/
    ├── pr-build-test.yml
    └── release.yml
```

## Workflow Details

### PR Build and Test Workflow

#### Trigger Conditions

```yaml
on:
  pull_request:
    branches:
      - main
    paths:
      - 'src/**'
      - 'Dockerfile'
      - '.github/workflows/pr-build-test.yml'
      - 'docker-compose.yml'
      - '*.sln'
```

The workflow only runs when relevant files change, saving CI minutes.

#### Build and Test Job

**Steps:**
1. Checkout code with full history
2. Setup .NET 10.0 SDK with caching
3. Restore NuGet packages
4. Build solution in Release configuration
5. Run unit tests with code coverage
6. Upload test results as artifacts

**Caching Strategy:**
- NuGet packages cached by `setup-dotnet` action
- Cache key based on `packages.lock.json`

#### Docker Build Job

**Steps:**
1. Checkout code
2. Setup Docker Buildx for multi-platform support
3. Build Docker image (no push)
4. Export image as tar file
5. Upload as artifact for verification

**Build Features:**
- Multi-architecture support (amd64, arm64)
- Layer caching via GitHub Actions cache
- Optimized build context

#### Lint and Security Job

**Steps:**
1. Check code formatting with `dotnet format`
2. Scan for vulnerable dependencies
3. Report security issues

### Release Workflow

#### Semantic Versioning

The workflow uses GitVersion for semantic versioning:

- **Major version (X.0.0):** Breaking changes
- **Minor version (0.X.0):** New features
- **Patch version (0.0.X):** Bug fixes

**Commit Message Conventions:**

```
feat: New feature (minor version bump)
fix: Bug fix (patch version bump)
BREAKING CHANGE: Breaking change (major version bump)
chore: Maintenance (no version bump)
docs: Documentation (no version bump)
```

#### Docker Image Tags

Images are tagged with multiple formats:

```bash
ghcr.io/owner/repo:latest
ghcr.io/owner/repo:v1.2.3
ghcr.io/owner/repo:v1.2
ghcr.io/owner/repo:v1
ghcr.io/owner/repo:main-abc1234
```

#### Release Creation

The workflow automatically:
1. Generates changelog from git commits
2. Creates GitHub release
3. Attaches release notes
4. Tags the release
5. Links to Docker images

#### Deployment Update

After release, the workflow:
1. Updates `k8s/deployment.yaml` with new image
2. Creates a PR with the update
3. Labels PR as automated deployment

## Troubleshooting

### Common Issues

#### Build Failures

**Problem:** Build fails with package restore errors

**Solution:**
```bash
# Ensure packages.lock.json is committed
dotnet restore --use-lock-file
git add packages.lock.json
git commit -m "chore: add packages lock file"
```

#### Docker Push Fails

**Problem:** Permission denied pushing to GHCR

**Solution:**
- Verify workflow permissions are set to "Read and write"
- Check that GHCR is enabled for the repository
- Ensure the repository is public or you have package permissions

#### Test Failures

**Problem:** Tests pass locally but fail in CI

**Solution:**
```bash
# Run tests in CI-like environment locally
docker run --rm -v $(pwd):/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test --configuration Release
```

#### Version Not Incrementing

**Problem:** Release workflow creates same version

**Solution:**
- Use conventional commit messages
- Ensure commits are pushed after PR merge
- Check GitVersion configuration

### Debugging Workflows

#### Enable Debug Logging

Add these secrets to enable verbose logging:

```
ACTIONS_RUNNER_DEBUG: true
ACTIONS_STEP_DEBUG: true
```

#### View Detailed Logs

1. Go to Actions tab
2. Select workflow run
3. Click on failed job
4. Expand failed step
5. Review logs

#### Re-run Failed Jobs

1. Open workflow run
2. Click "Re-run failed jobs"
3. Or "Re-run all jobs" for clean slate

## Best Practices

### 1. Commit Message Format

Use conventional commits for automatic versioning:

```
<type>(<scope>): <subject>

<body>

<footer>
```

**Examples:**

```bash
feat(api): add container status endpoint
fix(k8s): resolve pod creation timeout
docs: update deployment instructions
chore: upgrade dependencies
```

### 2. PR Guidelines

- Keep PRs focused and small
- Ensure all tests pass before requesting review
- Update documentation with code changes
- Add tests for new features

### 3. Release Process

1. Merge PR to `main`
2. Wait for release workflow to complete
3. Verify Docker image in GHCR
4. Check GitHub release
5. Deploy to Kubernetes cluster

### 4. Monitoring

- Review workflow runs regularly
- Monitor test coverage trends
- Check for vulnerable dependencies
- Update base images periodically

### 5. Maintenance

**Weekly:**
- Review failed workflow runs
- Update dependencies if needed

**Monthly:**
- Update GitHub Actions versions
- Review and update caching strategy
- Audit Docker image sizes

**Quarterly:**
- Update .NET SDK version
- Review and optimize workflows
- Update documentation

## Performance Optimization

### Caching Strategy

The workflows use multiple caching layers:

1. **NuGet Package Cache**
   ```yaml
   uses: actions/setup-dotnet@v4
   with:
     cache: true
     cache-dependency-path: '**/packages.lock.json'
   ```

2. **Docker Layer Cache**
   ```yaml
   cache-from: type=gha
   cache-to: type=gha,mode=max
   ```

3. **Build Artifact Cache**
   - Test results cached for 30 days (PR) / 90 days (Release)
   - Docker images cached for 7 days

### Parallel Execution

Jobs run in parallel when possible:
- PR workflow: 3 parallel jobs
- Release workflow: Sequential with parallelization where safe

### Build Time Optimization

**Current build times:**
- PR Build: ~5-8 minutes
- Release: ~10-15 minutes

**Optimization tips:**
- Use package lock files
- Optimize Dockerfile layers
- Minimize test data size
- Use appropriate caching

## Security

### Image Scanning

Docker images include:
- SBOM (Software Bill of Materials)
- Provenance attestation
- Multi-architecture support

### Vulnerability Management

1. Automated dependency scanning in PR workflow
2. Weekly dependabot updates
3. Security advisories monitoring

### Access Control

- Workflows use least-privilege permissions
- GITHUB_TOKEN scoped to necessary permissions
- No long-lived credentials stored

## Support

For issues or questions:
1. Check this documentation
2. Review workflow run logs
3. Check GitHub Actions documentation
4. Open an issue in the repository

## References

- [GitHub Actions Documentation](https://docs.github.com/en/actions)
- [GitVersion Documentation](https://gitversion.net/)
- [Docker Build Push Action](https://github.com/docker/build-push-action)
- [GitHub Container Registry](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry)
