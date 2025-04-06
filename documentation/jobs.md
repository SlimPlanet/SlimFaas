# SlimFaas Jobs

In addition to functions, SlimFaas can run **jobs** triggered by HTTP calls. Jobs behave similarly to functions but
are typically one-off or batch processes with custom concurrency controls.

## 1. Creating a Job

You can define jobs in your SlimFaas configuration via environment variables, or use dynamic jobs (if allowed).
Here is a snippet from the `SLIMFAAS_JOB_CONFIGURATION` environment variable:

```json
{
  "DefaultNumberParallelRequest": 1,
  "DefaultVisibility": "Private",
  "AllowDynamicJob": false,
  "Jobs": {
    "daisy": {
      "NumberParallelRequest": 1,
      "Visibility": "Public"
    }
  }
}
```

**Key Fields**:
- **DefaultNumberParallelRequest**: The fallback concurrency for all jobs.
- **DefaultVisibility**: If not specified, jobs are private by default (only accessible inside the cluster).
- **AllowDynamicJob**: Whether new jobs can be created on the fly via HTTP.
- **Jobs**: A dictionary of job definitions, each specifying concurrency (NumberParallelRequest) and Visibility.

---

## 2. Invoking a Job
Jobs can be triggered by calling:
```php-template
http://<slimfaas>/job/<jobName>/<path>
```
- If `jobName` is defined in your configuration, SlimFaas will schedule and run it with the concurrency/visibility rules you specified.

---

## 3. Private vs. Public Jobs
As with functions, **visibility** can be set to **Public** or **Private**:

- **Public**: can be triggered from anywhere (external or internal).
- **Private**: only triggered from within the cluster or trusted pods.

Use them according to your security needs.


