import React from 'react';
import type { PodStatus as PodStatusType } from '../types';

interface Props {
  pods: PodStatusType[];
}

const statusIcon: Record<string, string> = {
  Running: '🟢', Starting: '🟡', Pending: '🔵',
  CrashLoopBackOff: '🔴', ImagePullBackOff: '🔴', Unschedulable: '🔴', Error: '🔴',
};

const PodStatusList: React.FC<Props> = ({ pods }) => {
  const safePods = pods ?? [];
  if (safePods.length === 0) return <span className="pod-list__empty">No pods</span>;
  return (
    <div className="pod-list">
      {safePods.map((pod) => (
        <div key={pod.Name} className="pod-list__item">
          <span className="pod-list__icon" title={pod.Status}>{statusIcon[pod.Status] ?? '⚪'}</span>
          <span className="pod-list__name" title={pod.Name}>{pod.Name}</span>
          <span className={`pod-list__status pod-list__status--${(pod.Status ?? '').toLowerCase()}`}>
            {pod.Status ?? 'Unknown'}
          </span>
        </div>
      ))}
    </div>
  );
};

export default PodStatusList;
