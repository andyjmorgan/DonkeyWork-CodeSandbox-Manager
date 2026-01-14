# Deployment Guide

Quick guide for deploying the Kata Container Manager API to your k3s cluster.

## Prerequisites Checklist

- [ ] k3s cluster running with Kata Containers enabled
- [ ] kubectl configured and connected to the cluster
- [ ] Docker installed (for building the image)
- [ ] Access to push images to a container registry (or use local images)

## Step-by-Step Deployment

### 1. Create Namespace

```bash
kubectl apply -f namespace.yaml
```

Verify:
```bash
kubectl get namespace sandbox-containers
```

### 2. Create RBAC Resources

```bash
# Create ServiceAccount (in default namespace)
kubectl apply -f serviceaccount.yaml

# Create Role (permissions in sandbox-containers namespace)
kubectl apply -f role.yaml

# Create RoleBinding (links ServiceAccount to Role)
kubectl apply -f rolebinding.yaml
```

Verify:
```bash
kubectl get serviceaccount kata-manager -n default
kubectl get role kata-manager-role -n sandbox-containers
kubectl get rolebinding kata-manager-binding -n sandbox-containers
```

### 3. Build and Load Docker Image

#### Option A: Build and push to registry (recommended for production)

```bash
# From the project root directory
docker build -t your-registry.com/kata-manager-api:latest .
docker push your-registry.com/kata-manager-api:latest

# Update deployment.yaml with your registry
sed -i 's|kata-manager-api:latest|your-registry.com/kata-manager-api:latest|g' deployment.yaml
```

#### Option B: Build for local k3s (development)

```bash
# Build the image
docker build -t kata-manager-api:latest .

# Import to k3s (if using k3s)
docker save kata-manager-api:latest | sudo k3s ctr images import -

# Or use k3s import directly
sudo k3s ctr images import kata-manager-api.tar

# Verify image is available
sudo k3s ctr images ls | grep kata-manager
```

### 4. Deploy the Application

```bash
kubectl apply -f deployment.yaml
```

This creates:
- Deployment (1 replica)
- Service (ClusterIP)

### 5. Verify Deployment

```bash
# Check pod status
kubectl get pods -n default -l app=kata-manager-api

# Expected output:
# NAME                               READY   STATUS    RESTARTS   AGE
# kata-manager-api-xxxxxxxxxx-xxxxx  1/1     Running   0          30s

# Check logs
kubectl logs -n default -l app=kata-manager-api -f

# Check service
kubectl get svc kata-manager-api -n default
```

### 6. Test the API

#### Port-forward for testing

```bash
kubectl port-forward -n default svc/kata-manager-api 8080:80
```

#### Test health endpoint

```bash
curl http://localhost:8080/healthz
# Expected: Healthy
```

#### Test API documentation

Open in browser: http://localhost:8080/scalar/v1

#### Create a test container

```bash
curl -X POST http://localhost:8080/api/containers \
  -H "Content-Type: application/json" \
  -d '{
    "image": "nginx:alpine",
    "labels": {
      "test": "true"
    }
  }'
```

#### List containers

```bash
curl http://localhost:8080/api/containers
```

#### Verify Kata container in cluster

```bash
# List pods in sandbox-containers namespace
kubectl get pods -n sandbox-containers

# Check that it's using kata-qemu runtime
kubectl get pod <pod-name> -n sandbox-containers -o jsonpath='{.spec.runtimeClassName}'
# Expected: kata-qemu

# Get node and check for QEMU process
NODE=$(kubectl get pod <pod-name> -n sandbox-containers -o jsonpath='{.spec.nodeName}')
echo "Pod is running on node: $NODE"

# SSH to node and verify QEMU process (if you have access)
# ssh user@$NODE 'ps aux | grep qemu | grep -v grep'
```

#### Delete the test container

```bash
curl -X DELETE http://localhost:8080/api/containers/<pod-name>
```

## Optional: Expose via Ingress

If you want to expose the API externally:

### Create Ingress (Traefik example)

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: kata-manager-api
  namespace: default
  annotations:
    traefik.ingress.kubernetes.io/router.entrypoints: websecure
spec:
  rules:
    - host: kata-api.yourdomain.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: kata-manager-api
                port:
                  number: 80
```

Apply:
```bash
kubectl apply -f ingress.yaml
```

## Configuration Updates

To update configuration without rebuilding:

### Option 1: ConfigMap (recommended)

Create a ConfigMap:
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: kata-manager-config
  namespace: default
data:
  appsettings.json: |
    {
      "KataContainerManager": {
        "TargetNamespace": "sandbox-containers",
        "RuntimeClassName": "kata-qemu",
        "PodNamePrefix": "custom-prefix"
      }
    }
```

Mount in deployment:
```yaml
volumes:
  - name: config
    configMap:
      name: kata-manager-config
volumeMounts:
  - name: config
    mountPath: /app/appsettings.json
    subPath: appsettings.json
```

### Option 2: Environment Variables

Add to deployment.yaml:
```yaml
env:
  - name: KataContainerManager__TargetNamespace
    value: "custom-namespace"
  - name: KataContainerManager__PodNamePrefix
    value: "custom-prefix"
```

## Monitoring

### View Logs

```bash
# Real-time logs
kubectl logs -n default -l app=kata-manager-api -f

# Recent logs
kubectl logs -n default -l app=kata-manager-api --tail=100

# Logs from specific pod
kubectl logs -n default <pod-name>
```

### Check Resource Usage

```bash
kubectl top pod -n default -l app=kata-manager-api
```

### Watch Pod Status

```bash
kubectl get pods -n default -l app=kata-manager-api -w
```

## Scaling

Scale the deployment:
```bash
kubectl scale deployment kata-manager-api -n default --replicas=3
```

**Note**: All replicas can safely handle requests as they're stateless.

## Upgrading

1. Build new image with version tag:
   ```bash
   docker build -t kata-manager-api:v1.1.0 .
   ```

2. Update deployment:
   ```bash
   kubectl set image deployment/kata-manager-api api=kata-manager-api:v1.1.0 -n default
   ```

3. Watch rollout:
   ```bash
   kubectl rollout status deployment/kata-manager-api -n default
   ```

4. Rollback if needed:
   ```bash
   kubectl rollout undo deployment/kata-manager-api -n default
   ```

## Uninstalling

Remove all resources:
```bash
kubectl delete -f deployment.yaml
kubectl delete -f rolebinding.yaml
kubectl delete -f role.yaml
kubectl delete -f serviceaccount.yaml
kubectl delete -f namespace.yaml
```

Or delete everything at once:
```bash
kubectl delete namespace sandbox-containers
kubectl delete deployment kata-manager-api -n default
kubectl delete service kata-manager-api -n default
kubectl delete serviceaccount kata-manager -n default
```

## Troubleshooting

### Pod won't start

```bash
# Check pod events
kubectl describe pod <pod-name> -n default

# Common issues:
# - Image pull errors: Check image name and registry access
# - RBAC errors: Verify ServiceAccount is attached
# - Config errors: Check logs for validation failures
```

### API returns 403 Forbidden

```bash
# Verify RBAC
kubectl auth can-i create pods --namespace=sandbox-containers --as=system:serviceaccount:default:kata-manager
# Expected: yes

# Check Role
kubectl get role kata-manager-role -n sandbox-containers -o yaml

# Check RoleBinding
kubectl get rolebinding kata-manager-binding -n sandbox-containers -o yaml
```

### Containers fail to create

```bash
# Check if RuntimeClass exists
kubectl get runtimeclass kata-qemu

# Check if namespace exists
kubectl get namespace sandbox-containers

# Check Kata installation on nodes
kubectl get nodes -o json | jq '.items[].status.allocatable'
```

### Health check fails

```bash
# Check if API is responsive
kubectl exec -n default <pod-name> -- curl -f http://localhost:8080/healthz

# Check Kubernetes API connectivity from pod
kubectl exec -n default <pod-name> -- curl -k https://kubernetes.default.svc
```

## Security Hardening (Production)

1. **Use NetworkPolicy** to restrict pod communication:
   ```yaml
   apiVersion: networking.k8s.io/v1
   kind: NetworkPolicy
   metadata:
     name: kata-manager-netpol
     namespace: default
   spec:
     podSelector:
       matchLabels:
         app: kata-manager-api
     policyTypes:
       - Ingress
     ingress:
       - from:
           - namespaceSelector: {}
         ports:
           - protocol: TCP
             port: 8080
   ```

2. **Enable TLS** for the API

3. **Use Secrets** for sensitive configuration

4. **Enable Pod Security Standards**:
   ```bash
   kubectl label namespace default pod-security.kubernetes.io/enforce=restricted
   ```

5. **Set resource limits** appropriately based on load

## Performance Tuning

1. **Adjust pod resources** in deployment.yaml based on load
2. **Scale horizontally** for high request volumes
3. **Monitor Kata container overhead** (+160Mi RAM, +250m CPU per container)
4. **Set appropriate timeouts** in configuration

## Need Help?

- Check logs: `kubectl logs -n default -l app=kata-manager-api`
- Review pod events: `kubectl describe pod -n default <pod-name>`
- Test connectivity: `kubectl exec -n default <pod-name> -- curl localhost:8080/healthz`
- Check RBAC: `kubectl auth can-i --list --namespace=sandbox-containers --as=system:serviceaccount:default:kata-manager`
