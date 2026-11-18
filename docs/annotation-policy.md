# Protect SlimFaas Annotations

SlimFaas reads its security policy from workload annotations: `SlimFaas/DefaultVisibility`, `SlimFaas/PathsStartWithVisibility`, `SlimFaas/DefaultTrust`, `SlimFaas/SubscribeEvents`, `SlimFaas/JobImagesWhitelist` and the other `SlimFaas/*` keys. Anyone allowed to patch a Deployment in the namespace can therefore make a Private function Public or mark an untrusted workload as Trusted. SlimFaas cannot check who wrote an annotation; the Kubernetes API server can.

[`demo/slimfaas-annotation-policy.yml`](../demo/slimfaas-annotation-policy.yml) is an optional [ValidatingAdmissionPolicy](https://kubernetes.io/docs/reference/access-authn-authz/validating-admission-policy/) that lets only a chosen set of users and groups add, change or remove `SlimFaas/*` annotations. Everything else stays governed by RBAC as before.

## What the policy checks

- **Resources**: creation and update of Deployments, StatefulSets, Jobs and CronJobs in the `slimfaas-demo` namespace.
- **Annotations**: every key that starts with `SlimFaas/`, both in `metadata.annotations` (read for CronJob job configurations and Jobs) and in `spec.template.metadata.annotations` (read for functions). Adding, removing or changing the value of such a key is a change; keeping them identical is not.
- **Decision**: a change is accepted only when the requesting user is listed in `users`, or belongs to a group listed in `groups`, of the `slimfaas-annotation-editors` ConfigMap. Otherwise the API server refuses the request:

  ```text
  deployments.apps "payments" is forbidden: ValidatingAdmissionPolicy 'slimfaas-annotations' with binding 'slimfaas-annotations' denied request: only the users and groups listed in ConfigMap slimfaas-admission/slimfaas-annotation-editors may change SlimFaas/* annotations; alice is not one of them
  ```

Updates that leave the `SlimFaas/*` annotations untouched are not affected: image updates, `kubectl rollout restart`, other annotations, scaling through the `scale` subresource (which SlimFaas uses), and the updates made by the Deployment controller. The policy applies to everyone, cluster administrators included.

## Install

Requirements: Kubernetes 1.30 or later, where ValidatingAdmissionPolicy is generally available, and cluster-admin rights to install it.

1. Edit the `slimfaas-annotation-editors` ConfigMap in the manifest. Both values are comma-separated lists.
   - Keep `system:serviceaccount:slimfaas-demo:slimfaas`: SlimFaas creates Jobs that carry `SlimFaas/DependsOn`.
   - Add the identity that deploys your functions: a user, the group of your operators, or the service account of your pipeline or GitOps controller (for example `system:serviceaccount:argocd:argocd-application-controller`).
   - `kubectl auth whoami` prints your own user name and groups.
2. Apply the manifest:

   ```bash
   kubectl apply -f demo/slimfaas-annotation-policy.yml
   ```

The ConfigMap lives in its own `slimfaas-admission` namespace: anyone who can edit ConfigMaps in a namespace, which the built-in `edit` role allows, could otherwise add themselves to the list. Grant write access to `slimfaas-admission` only to the people who manage the policy.

For another namespace, replace `slimfaas-demo` in the binding's `namespaceSelector` and in the SlimFaas service account name. Several namespaces can share one binding with a `matchExpressions` selector.

The policy fails closed: while the ConfigMap is missing, `parameterNotFoundAction: Deny` refuses every creation and update of Deployments, StatefulSets, Jobs and CronJobs in the namespace, whatever their annotations, and SlimFaas cannot create Jobs. The manifest declares the ConfigMap before the binding; keep it in place for as long as the binding exists.

## Audit before denying

To see what the policy would refuse without blocking anybody, start the binding in warning and audit mode:

```yaml
spec:
  validationActions: [Warn, Audit]
```

`kubectl` then prints a warning for each request that would be denied, and the API server [audit log](https://kubernetes.io/docs/tasks/debug/debug-cluster/audit/) records it under the `validation.policy.admission.k8s.io/validation_failure` annotation. Once only expected callers are left, switch back to `validationActions: [Deny]`.

## Test the policy

[`.bin/test-annotation-policy.py`](../.bin/test-annotation-policy.py) creates a disposable [kind](https://kind.sigs.k8s.io/) cluster, applies the demo service account and the policy, and checks with server-side dry runs that unlisted users cannot change, add or remove `SlimFaas/*` annotations, while restarts, other annotations, scaling, listed groups and the SlimFaas service account still work:

```bash
python3 .bin/test-annotation-policy.py
```

`--image` selects the kind node image (Kubernetes 1.30 or later) and `--keep-cluster` keeps the cluster for inspection. `--cluster` sets the kind cluster name; the script refuses to run when a cluster with that name already exists and only deletes a cluster it created. The CI workflow runs the script on kind for every change.

## Limitations

- Kubernetes 1.29 and earlier do not have the GA API. On those clusters, a validating admission webhook can enforce the same rule; this repository does not ship one.
- Cluster administrators can still delete or edit the policy, the binding and the ConfigMap. The policy protects SlimFaas annotations from namespace editors, not from cluster-admin.
- Only Deployments, StatefulSets, Jobs and CronJobs are checked. Pods created directly are not; SlimFaas does not read its policy from Pod annotations.
- The policy decides who may write the annotations, not whether they are valid. SlimFaas still validates their values.
- Docker Compose and native local mode read labels or local manifests; the policy does not apply there.
