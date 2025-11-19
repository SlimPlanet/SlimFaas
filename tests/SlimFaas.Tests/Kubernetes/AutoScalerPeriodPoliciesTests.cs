using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;
using Xunit;

namespace SlimFaas.Tests.Kubernetes
{
    public class AutoScalerPeriodPoliciesTests
    {
        private static AutoScaler CreateAutoScalerWithStore(Mock<IAutoScalerStore> storeMock)
        {
            // Evaluator "dummy" : jamais appelé dans ces tests, mais nécessaire pour construire AutoScaler
            PromQlMiniEvaluator.SnapshotProvider provider = () =>
                new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>();

            var evaluator = new PromQlMiniEvaluator(provider);
            return new AutoScaler(evaluator, storeMock.Object, logger: null);
        }

        private static int InvokeApplyScaleUpPolicies(
            AutoScaler scaler,
            string key,
            ScaleDirectionBehavior behavior,
            int currentReplicas,
            int desired,
            long nowUnixSeconds)
        {
            var mi = typeof(AutoScaler).GetMethod(
                "ApplyScaleUpPolicies",
                BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(mi);

            var result = mi!.Invoke(
                scaler,
                new object[] { key, behavior, currentReplicas, desired, nowUnixSeconds });

            return Assert.IsType<int>(result);
        }

        private static int InvokeApplyScaleDownPolicies(
            AutoScaler scaler,
            string key,
            ScaleDirectionBehavior behavior,
            int currentReplicas,
            int desired,
            long nowUnixSeconds)
        {
            var mi = typeof(AutoScaler).GetMethod(
                "ApplyScaleDownPolicies",
                BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(mi);

            var result = mi!.Invoke(
                scaler,
                new object[] { key, behavior, currentReplicas, desired, nowUnixSeconds });

            return Assert.IsType<int>(result);
        }

        [Fact]
        public void ScaleUp_PodsPolicy_WithPeriodSeconds_LimitsDeltaWithinWindow()
        {
            // current = 4, raw desired = 10
            // Policy: Pods, Value = 2, Period = 30s
            // Historique sur les 30s : desired = 3, puis 4
            // baseline = min(3,4) = 3
            // alreadyUp = current(4) - baseline(3) = 1
            // remainingUp = 2 - 1 = 1
            // requestedDelta = 6 => clamp à +1 => desired final = 5

            const string key = "deploy-a";
            const long now = 1000;
            const int current = 4;
            const int rawDesired = 10;

            var policy = new ScalePolicy(
                Type: ScalePolicyType.Pods,
                Value: 2,
                PeriodSeconds: 30
            );

            var behavior = new ScaleDirectionBehavior
            {
                StabilizationWindowSeconds = 0,
                Policies = new List<ScalePolicy> { policy }
            };

            var samples = new List<AutoScaleSample>
            {
                new AutoScaleSample(timestampUnixSeconds: 990, desiredReplicas: 3),
                new AutoScaleSample(timestampUnixSeconds: 999, desiredReplicas: 4),
            };

            var storeMock = new Mock<IAutoScalerStore>();
            storeMock
                .Setup(s => s.GetSamples(key, It.Is<long>(ts => ts <= now)))
                .Returns(samples);

            var scaler = CreateAutoScalerWithStore(storeMock);

            var finalDesired = InvokeApplyScaleUpPolicies(
                scaler,
                key,
                behavior,
                current,
                rawDesired,
                now);

            Assert.Equal(4, finalDesired);
        }

        [Fact]
        public void ScaleUp_PodsPolicy_WithoutHistory_AllowsFullValue()
        {
            // current = 2, raw desired = 10
            // Policy: Pods, Value = 3, Period = 60s
            // Pas d’historique => baseline = current = 2, alreadyUp = 0
            // remainingUp = 3
            // requestedDelta = 8 => clamp à +3 => desired final = 5

            const string key = "deploy-b";
            const long now = 1000;
            const int current = 2;
            const int rawDesired = 10;

            var policy = new ScalePolicy(
                Type: ScalePolicyType.Pods,
                Value: 3,
                PeriodSeconds: 60
            );

            var behavior = new ScaleDirectionBehavior
            {
                StabilizationWindowSeconds = 0,
                Policies = new List<ScalePolicy> { policy }
            };

            var storeMock = new Mock<IAutoScalerStore>();
            storeMock
                .Setup(s => s.GetSamples(key, It.IsAny<long>()))
                .Returns(Array.Empty<AutoScaleSample>());

            var scaler = CreateAutoScalerWithStore(storeMock);

            var finalDesired = InvokeApplyScaleUpPolicies(
                scaler,
                key,
                behavior,
                current,
                rawDesired,
                now);

            Assert.Equal(5, finalDesired);
        }

        [Fact]
        public void ScaleDown_PodsPolicy_WithPeriodSeconds_LimitsDeltaWithinWindow()
        {
            // current = 8, raw desired = 1
            // Policy: Pods, Value = 2, Period = 30s
            // Historique: dans la fenêtre, desired = 10 puis 9
            // baseline = max(10,9) = 10
            // alreadyDown = baseline(10) - current(8) = 2
            // remainingDown = 2 - 2 = 0 => aucun scale-down autorisé
            // => on ne bouge pas : final = current = 8

            const string key = "deploy-c";
            const long now = 2000;
            const int current = 8;
            const int rawDesired = 1;

            var policy = new ScalePolicy(
                Type: ScalePolicyType.Pods,
                Value: 2,
                PeriodSeconds: 30
            );

            var behavior = new ScaleDirectionBehavior
            {
                StabilizationWindowSeconds = 0,
                Policies = new List<ScalePolicy> { policy }
            };

            var samples = new List<AutoScaleSample>
            {
                new AutoScaleSample(timestampUnixSeconds: 1975, desiredReplicas: 10),
                new AutoScaleSample(timestampUnixSeconds: 1985, desiredReplicas: 9),
            };

            var storeMock = new Mock<IAutoScalerStore>();
            storeMock
                .Setup(s => s.GetSamples(key, It.Is<long>(ts => ts <= now)))
                .Returns(samples);

            var scaler = CreateAutoScalerWithStore(storeMock);

            var finalDesired = InvokeApplyScaleDownPolicies(
                scaler,
                key,
                behavior,
                current,
                rawDesired,
                now);

            Assert.Equal(current, finalDesired);
        }

        [Fact]
        public void ScaleDown_PodsPolicy_WithoutHistory_AllowsFullValue()
        {
            // current = 5, raw desired = 0
            // Policy: Pods, Value = 2, Period = 60s
            // Pas d’historique => baseline = current, alreadyDown = 0
            // remainingDown = 2
            // requestedDelta = 5 => clamp à 2 => finalDesired = 3

            const string key = "deploy-d";
            const long now = 2000;
            const int current = 5;
            const int rawDesired = 0;

            var policy = new ScalePolicy(
                Type: ScalePolicyType.Pods,
                Value: 2,
                PeriodSeconds: 60
            );

            var behavior = new ScaleDirectionBehavior
            {
                StabilizationWindowSeconds = 0,
                Policies = new List<ScalePolicy> { policy }
            };

            var storeMock = new Mock<IAutoScalerStore>();
            storeMock
                .Setup(s => s.GetSamples(key, It.IsAny<long>()))
                .Returns(Array.Empty<AutoScaleSample>());

            var scaler = CreateAutoScalerWithStore(storeMock);

            var finalDesired = InvokeApplyScaleDownPolicies(
                scaler,
                key,
                behavior,
                current,
                rawDesired,
                now);

            Assert.Equal(3, finalDesired);
        }
    }
}
