# Show HN submission notes

Submit the runnable project at <https://github.com/SlimPlanet/SlimFaas>. Readers can follow the local quick start from its README. The published site player lives at <https://slimfaas.dev/#live-stream-dashboard> once this branch is merged and deployed.

Use your own title beginning with **Show HN:** and write any first comment in your own words. The [Show HN guidance](https://news.ycombinator.com/showhn.html) asks for something people can try. The [site guidelines](https://news.ycombinator.com/newsguidelines.html) ask participants not to post generated text.

## Facts from the recording

- A synchronous caller waited for a sleeping Kubernetes deployment, received Fibonacci(10) = 55, and the deployment returned to zero.
- Thirty concurrent HTTP submissions requested Fibonacci(48). All thirty received HTTP 202. Queue depth reached 30, the deployment reached four ready pods, and workers logged thirty results of 4,807,526,976 before returning to zero.
- Two deployments had two ready replicas each. A broadcast invalidated a process-local tool catalog on all four pods.
- Appending one result to a durable Stream delivered it to one replica in each subscribed deployment. Both subscriptions advanced to sequence 1 with zero lag. Retries remain possible under at-least-once delivery.

Details worth adding personally are what prompted you to build SlimFaas and which operational trade-off you would like readers to challenge.
