"""Audit timestamped observed log headers without claiming complete retired history."""
from pathlib import Path
import collections
import datetime
import gzip
import hashlib
import json
import re
import tarfile
import time

R=Path.cwd();E=R/'artifacts/pr506';D=R/'docs/reliability/2026-11-16'
P=E/'preflight/net11-160-kind-log-audit.json';assert not P.exists()
reports=[]
def stamp(line):
    try:return datetime.datetime.fromisoformat(line.split(' ',1)[0].replace('Z','+00:00')).timestamp()
    except ValueError:return None
for role in ['follower','leader','quorum']:
    published=json.loads(gzip.decompress((D/f'kind-{role}-189.json.gz').read_bytes()))
    archive=D/f'kind-{role}-189-raw.tar.gz'
    assert hashlib.sha256(archive.read_bytes()).hexdigest()==published['archive']['sha256']
    namespace=f'slimfaas-pr506-n189-p113-endurance-{role}'
    supplemental=E/'kubernetes/net11-120-runtime-history'/namespace
    manifest=json.loads((supplemental/'pod-log-coverage.json').read_text())
    errors={};files=[];observed_generations=set();strictly_mapped=set();before_stop=[]
    def scan(name,uid,data,generation,association):
        first=last=None;unparsed=error_unparsed=oom=unhandled=critical=0
        for raw in data.splitlines():
            line=raw.decode(errors='replace');ts=stamp(line)
            if ts is None:unparsed+=1
            else:
                first=ts if first is None else min(first,ts)
                last=ts if last is None else max(last,ts)
            oom+=line.count('OutOfMemoryException')
            unhandled+=line.lower().count('unhandled exception')
            critical+=len(re.findall(r'\bcrit:',line))
            if re.search(r'\b(?:fail|crit):',line):
                if ts is None:error_unparsed+=1
                else:errors[(uid,line)]=ts
        files.append(dict(path=name,pod_uid=uid,restart_index=generation,
            association=association,bytes=len(data),sha256=hashlib.sha256(data).hexdigest(),
            earliest_log_utc=first,latest_log_utc=last,unparsed_timestamp_lines=unparsed,
            unparsed_error_header_lines=error_unparsed,oom_string_occurrences=oom,
            unhandled_string_occurrences=unhandled,critical_header_occurrences=critical))
        if generation is not None:strictly_mapped.add((uid,generation))
    with tarfile.open(archive)as tar:
        members={m.name:m for m in tar.getmembers()}
        journal=[json.loads(line)for line in tar.extractfile('campaign/client.jsonl')]
        first_ready=next(r['utc']for r in journal if r.get('event')=='cluster-ready')
        windows=[]
        for i,row in enumerate(journal):
            if row.get('event')=='leader-crashed-with-silent-follower':
                end=next(r['utc']for r in journal[i+1:]if r.get('event')=='cluster-ready'and r.get('phase')=='protocol-probe-with-silent-follower')
                windows.append(dict(start=row['utc'],end=end,label='protocol'))
            if row.get('event')=='suspended':
                end=next(r['utc']for r in journal[i+1:]if r.get('event')=='kubernetes-capture'and r.get('phase')==row['phase'])
                windows.append(dict(start=row['utc'],end=end,label=row['phase']))
        kills=[r for r in journal if r.get('event')=='killed']
        windows.append(dict(start=kills[0]['utc'],end=next(r['utc']for r in journal if r.get('event')=='cluster-ready'and r.get('phase')=='full-persistent-restart'),label='crashes-and-restart'))
        snapshots={}
        for name in members:
            if not name.endswith('-kubernetes-pods.json'):continue
            prefix=name.removesuffix('-kubernetes-pods.json')
            pods=json.load(tar.extractfile(name))
            for pod in pods['items']:
                if pod['metadata'].get('labels',{}).get('app')!='slimfaas':continue
                uid=pod['metadata']['uid']
                runtime=next((s for s in pod.get('status',{}).get('containerStatuses',[])if s['name']=='runtime'),None)
                if runtime is None:continue
                index=runtime.get('restartCount',0)
                observed_generations.update((uid,i)for i in range(index+1))
                path=f'{prefix}-{pod["metadata"]["name"]}-{uid}-runtime.log'
                snapshots[path]=dict(index=index,container_id=runtime.get('containerID'),snapshot=name)
        for name in members:
            match=re.search(r'slimfaas-[012]-([0-9a-f-]{36})-runtime.log$',name)
            if match is None:continue
            metadata=snapshots.get(name)
            scan(name,match[1],tar.extractfile(name).read(),metadata['index']if metadata else None,
                 dict(coobserved_pod_snapshot=metadata)if metadata else 'UID only; before-stop capture lacks restart-index metadata')
            if metadata is None:before_stop.append(name)
    for instance in manifest['instances']:
        if instance['container']=='runtime':observed_generations.update((instance['pod_uid'],i)for i in range(instance['latest_restart_index']+1))
    for row in manifest['logs']:
        if row['container']!='runtime'or row['exit_code']!=0:continue
        path=supplemental/row['path'];data=path.read_bytes()
        assert len(data)==row['bytes']and hashlib.sha256(data).hexdigest()==row['sha256']
        scan(str(path.relative_to(E)),row['pod_uid'],data,row['restart_index'],
             dict(collector='observer120',previous=row['previous'],container_id=row['container_id'],captured_utc=row['captured_utc']))
    categories=collections.Counter();outside=[]
    for(uid,line),ts in errors.items():
        category='startup'if ts<first_ready else next((w['label']for w in windows if w['start']<=ts<=w['end']),'outside-recorded-windows')
        categories[category]+=1
        if category=='outside-recorded-windows':outside.append(dict(pod_uid=uid,line=line,utc=ts))
    missing=sorted(observed_generations-strictly_mapped)
    reports.append(dict(role=role,namespace=namespace,archive_sha256=published['archive']['sha256'],
        initial_ready_utc=first_ready,fault_recovery_windows=windows,
        unique_observed_error_headers=len(errors),header_categories=dict(categories),outside_recorded_windows=outside,
        unique_header_set_sha256=hashlib.sha256(json.dumps(sorted(errors),separators=(',',':')).encode()).hexdigest(),
        indexed_log_files=files,observed_runtime_generation_count=len(observed_generations),
        metadata_mapped_generation_count=len(strictly_mapped),
        generations_without_strictly_indexed_capture=[dict(pod_uid=uid,restart_index=i)for uid,i in missing],
        before_stop_uid_only_files=before_stop,
        supplementary_manifest_sha256=hashlib.sha256((supplemental/'pod-log-coverage.json').read_bytes()).hexdigest(),
        history_completeness='INCONCLUSIVE: retired/rotated log history and before-stop restart metadata are not fully demonstrated. A represented generation is not proof of complete lifetime capture.',
        healthy_window_verdict='No observed error headers outside the explicit initialization/fault/recovery intervals; complete healthy-window absence remains INCONCLUSIVE because log-history coverage is incomplete.'))
result=dict(created_utc=time.time(),runtime_source='189826231fe43fe3069e8a1f7724f83589432c6f',roles=reports,
    scope='Offline audit of existing immutable in-driver and observer120 captures. Exact (podUID,timestamped header) deduplication avoids repeated snapshot counts. Counts are log headers, not distinct exceptions. Fault fences are client wall-clock observations, not calibrated distributed traces; boundary classification remains observational. No replay, deadline change, zero-error/lifetime/OOM certification, or rewriting of original logs.')
P.write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps([dict(role=r['role'],unique_error_headers=r['unique_observed_error_headers'],categories=r['header_categories'],outside=len(r['outside_recorded_windows']),expected_generations=r['observed_runtime_generation_count'],mapped=r['metadata_mapped_generation_count'],missing_metadata=r['generations_without_strictly_indexed_capture'])for r in reports]))
