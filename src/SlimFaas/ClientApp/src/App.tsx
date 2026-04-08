import React from 'react';
import 'react-tooltip/dist/react-tooltip.css';
import { useStatusStream } from './hooks/useStatusStream';
import { useJobStatus } from './hooks/useJobStatus';
import Navbar from './components/Navbar';
import Footer from './components/Footer';
import FunctionTable from './components/FunctionTable';
import JobTable from './components/JobTable';
import NetworkMap from './components/NetworkMap';
import ErrorBoundary from './components/ErrorBoundary';
import type { JobConfigurationStatus } from './types';

interface JobsSectionProps {
  jobs: JobConfigurationStatus[];
  jobsLoading: boolean;
  jobsError: string | null;
  totalRunningJobs: number;
  totalSchedules: number;
}

const JobsSection: React.FC<JobsSectionProps> = ({
  jobs,
  jobsLoading,
  jobsError,
  totalRunningJobs,
  totalSchedules,
}) => (
  <div className="dashboard dashboard--jobs">
    <div className="dashboard__header">
      <h1 className="dashboard__title">Jobs Overview</h1>
      <div className="dashboard__summary">
        <span className="dashboard__stat">
          <span className="dashboard__stat-icon">📋</span>
          <strong>{jobs.length}</strong> configuration(s)
        </span>
        <span className="dashboard__stat">
          <span className="dashboard__stat-icon">⚙️</span>
          <strong>{totalRunningJobs}</strong> running
        </span>
        <span className="dashboard__stat">
          <span className="dashboard__stat-icon">🗓️</span>
          <strong>{totalSchedules}</strong> scheduled
        </span>
      </div>
    </div>

    {jobsLoading && <div className="dashboard__loading">Loading jobs...</div>}

    {jobsError && (
      <div className="dashboard__error">
        <span className="dashboard__error-icon">⚠️</span>
        Jobs unavailable: {jobsError}
      </div>
    )}

    {!jobsLoading && !jobsError && jobs.length === 0 && (
      <div className="dashboard__empty">No job configurations found.</div>
    )}

    {jobs.length > 0 && <JobTable jobs={jobs} />}
  </div>
);

const App: React.FC = () => {
  const {
    functions, queues, activity,
    loading, error, wakeUp, wakeUpAll, coolingDown, wakeAllCooling,
    functionsWithQueueActivity, slimFaasReplicas, slimFaasNodes,
  } = useStatusStream();
  const { jobs, loading: jobsLoading, error: jobsError } = useJobStatus();

  const allUp = functions.length > 0 && functions.every((f) => f.NumberReady > 0);
  const totalReady = functions.reduce((sum, f) => sum + (f.NumberReady ?? 0), 0);
  const totalRequested = functions.reduce((sum, f) => sum + (f.NumberRequested ?? 0), 0);
  const totalRunningJobs = jobs.reduce((sum, j) => sum + (j.RunningJobs ?? []).length, 0);
  const totalSchedules = jobs.reduce((sum, j) => sum + (j.Schedules ?? []).length, 0);

  return (
    <div className="layout">
      <Navbar />

      <main className="layout__content">
        {/* Functions section */}
        <div className="dashboard">
          <div className="dashboard__header">
            <h1 className="dashboard__title">Infrastructure Overview</h1>
            <div className="dashboard__summary">
              <span className="dashboard__stat">
                <span className="dashboard__stat-icon">📦</span>
                <strong>{functions.length}</strong> function(s)
              </span>
              <span className="dashboard__stat">
                <span className="dashboard__stat-icon">🟢</span>
                <strong>{totalReady}</strong> / {totalRequested} pods ready
              </span>
            </div>
            <button
              className={`dashboard__wake-all ${(allUp || wakeAllCooling) ? 'dashboard__wake-all--disabled' : ''}`}
              disabled={allUp || functions.length === 0 || wakeAllCooling}
              onClick={wakeUpAll}
              type="button"
            >
              {wakeAllCooling ? '⏳ Waking up…' : '⚡ Wake Up All Functions'}
            </button>
          </div>

          {loading && <div className="dashboard__loading">Loading...</div>}

          {error && (
            <div className="dashboard__error">
              <span className="dashboard__error-icon">⚠️</span>
              Failed to fetch status: {error}
            </div>
          )}

          {!loading && functions.length === 0 && !error && (
            <div className="dashboard__empty">
              No functions found. Deploy a function with SlimFaas annotations to see it here.
            </div>
          )}

          {functions.length > 0 && (
            <FunctionTable functions={functions} onWakeUp={wakeUp} coolingDown={coolingDown} />
          )}

          {/* Network visualization below the functions table */}
          {functions.length > 0 && (
            <ErrorBoundary>
              <NetworkMap functions={functions} queues={queues} activity={activity} functionsWithQueueActivity={functionsWithQueueActivity} slimFaasReplicas={slimFaasReplicas} slimFaasNodes={slimFaasNodes} />
            </ErrorBoundary>
          )}
        </div>

        {/* Jobs section */}
        <ErrorBoundary>
          <JobsSection jobs={jobs} jobsLoading={jobsLoading} jobsError={jobsError}
            totalRunningJobs={totalRunningJobs} totalSchedules={totalSchedules} />
        </ErrorBoundary>
      </main>

      <Footer />
    </div>
  );
};

export default App;
