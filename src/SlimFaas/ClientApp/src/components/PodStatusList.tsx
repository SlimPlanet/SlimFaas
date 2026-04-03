import React from 'react';
import type { PodStatus as PodStatusType } from '../types';

interface Props {
  pods: PodStatusType[];
}

const statusIcon: Record<string, string> = {
  Running: '🟢',
  Starting: '🟡',
  Pending: '🔵',
  CrashLoopBackOff: '🔴',
  ImagePullBackOff: '🔴',
  Unschedulable: '🔴',
  Error: '🔴',
};

function getStatusIcon(status: string): string {
  return statusIcon[status] ?? '⚪';
}

const PodStatusList: React.FC<Props> = ({ pods }) => {
  if (pods.length === 0) {
    return <span className="pod-list__empty">No pods</span>;
  }

  return (
    <div className="pod-list">
      {pods.map((pod) => (
        <div key={pod.name} className="pod-list__item">
          <span className="pod-list__icon" title={pod.status}>
            {getStatusIcon(pod.status)}
          </span>
          <span className="pod-list__name" title={pod.name}>
            {pod.name}
          </span>
          <span className={`pod-list__status pod-list__status--${(pod.status ?? '').toLowerCase()}`}>
            {pod.status ?? 'Unknown'}
          </span>
        </div>
      ))}
    </div>
  );
};

export default PodStatusList;
