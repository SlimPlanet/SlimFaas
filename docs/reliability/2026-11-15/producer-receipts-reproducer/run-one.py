"""One fresh, retained, explicitly scoped Kubernetes WAL diagnostic."""
import argparse
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from types import SimpleNamespace

ROOT=Path(__file__).resolve().parents[4]
E=ROOT/'artifacts/pr506';BASE=Path(__file__).resolve().parent
K=['kubectl','--kubeconfig',str(E/'kubernetes/kubeconfig'),'--context','kind-slimfaas-pr506']
VARIANTS=json.loads((BASE/'variants.json').read_text())
p=argparse.ArgumentParser();p.add_argument('--namespace',required=True);p.add_argument('--chunk-size',type=int,choices=[4096,65536],required=True);p.add_argument('--ready-only',action='store_true');p.add_argument('--variant',choices=['before','after'],required=True);a=p.parse_args()
SOURCE=VARIANTS[a.variant]['source'];IMAGE=VARIANTS[a.variant]['image']
assert re.fullmatch(r'slimfaas-pr506-ps07-[a-z0-9-]+',a.namespace)and len(a.namespace)<=63
OUT=BASE/a.namespace;OUT.mkdir(exist_ok=False)
def write(name,value):
 path=OUT/name;t=path.with_suffix('.tmp');t.write_text(json.dumps(value,indent=2)+'\n');t.replace(path)
def sha(path):
 with path.open('rb')as f:return hashlib.file_digest(f,'sha256').hexdigest()
state={'started':time.time(),'namespace':a.namespace,'source':SOURCE,'image':IMAGE,'chunk_size':a.chunk_size,'ready_only':a.ready_only,'variant':a.variant};write('state.json',state)
assert shutil.disk_usage(ROOT).free>=20*2**30
assert int(subprocess.check_output(['podman','machine','ssh','df','-B1','--output=avail','/'],text=True).splitlines()[-1])>=20*2**30
existing=subprocess.check_output(K+['get','namespace',a.namespace,'--ignore-not-found','-o','json'],text=True);assert not existing.strip()
vmroot='/var/home/core/slimfaas-pr506/runs/'+a.namespace
check='from pathlib import Path\np=Path('+repr(vmroot)+')\nassert not p.exists(),"Fresh diagnostic storage required"\n'
subprocess.run(['podman','machine','ssh','sudo python3 -'],input=check,text=True,check=True)
spec=importlib.util.spec_from_file_location('launcher',ROOT/'.bin/run-kubernetes-campaign.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
o=SimpleNamespace(namespace=a.namespace,controller_image=IMAGE,image=IMAGE,suite='chaos',probes='normal',source_commit=SOURCE,scenarios='payloads',roles='follower',durations='1',variant=a.variant,ordinal=1,workload_args=[])
items,config=m.resources(o)
for item in items:
 if item['kind']=='StatefulSet':
  pod=item['spec']['template']['spec'];runtime=next(c for c in pod['containers']if c['name']=='runtime')
  # This fixture is allowed to bootstrap only because both namespace and VM storage
  # were proven absent. A marker alone makes the normal automatic existing-state
  # check conservative, so explicitly bootstrap fresh pod zero for both geometries.
  runtime['env'].append({'name':'POD_NAME','valueFrom':{'fieldRef':{'fieldPath':'metadata.name'}}})
  runtime['command']=['python3','-c']
  runtime['args']=["from pathlib import Path\nimport json,os\np=Path('/database/.profile-bootstrap-once');cold=False\nif os.environ['POD_NAME']=='slimfaas-0':\n try:\n  p.rename('/database/.profile-bootstrap-consumed');fd=os.open('/database',os.O_RDONLY|os.O_DIRECTORY)\n  try:os.fsync(fd)\n  finally:os.close(fd)\n  cold=True\n except FileNotFoundError:pass\nos.environ['SlimData__Configuration']=json.dumps({'coldStart':'true'if cold else'false'})\nos.execv('/app/SlimFaas',['/app/SlimFaas'])"]
  # Match the original HTTP snapshot policy, rather than the chaos stress policy.
  runtime['env']=[v for v in runtime['env']if v['name']not in ['SlimData__SnapshotIntervalEntries','SlimData__SnapshotIntervalBytes']]
  if a.chunk_size:
   script="""from pathlib import Path
import os
node=Path('/database')/os.environ['POD_NAME'];wal=node/'wal'
assert not wal.exists(), 'Refusing to reinterpret any existing WAL'
wal.mkdir(parents=True);node.chmod(0o777);wal.chmod(0o777)
marker=wal/'slimdata-chunk-size'
with marker.open('xb')as f:
 f.write(str(CHUNK).encode('ascii'));f.flush();os.fsync(f.fileno())
for p in [wal,node]:
 fd=os.open(p,os.O_RDONLY|os.O_DIRECTORY)
 try:os.fsync(fd)
 finally:os.close(fd)
if os.environ['POD_NAME']=='slimfaas-0':
 flag=Path('/database/.profile-bootstrap-once')
 with flag.open('xb')as f:f.write(b'fresh-profile-only');f.flush();os.fsync(f.fileno())
 fd=os.open('/database',os.O_RDONLY|os.O_DIRECTORY)
 try:os.fsync(fd)
 finally:os.close(fd)
""".replace('CHUNK',str(a.chunk_size))
   pod['initContainers'].append({'name':'initialize-fresh-wal-geometry','image':IMAGE,'imagePullPolicy':'IfNotPresent','command':['python3','-c',script],'env':[{'name':'POD_NAME','valueFrom':{'fieldRef':{'fieldPath':'metadata.name'}}}],'volumeMounts':[{'name':'state','mountPath':'/database'}]})
 if item['kind']=='Job'and item['metadata']['name']=='campaign-driver':
  pod=item['spec']['template']['spec'];driver=pod['containers'][0];base='/evidence/runs/'+a.namespace
  driver['command']=['python3','/profile/driver.py','--chunk-size',str(a.chunk_size),'--runtime','/app/SlimFaas','--backend-config','/config/backend.json','--output',base+'/profile','--source-commit',SOURCE,'--concurrency','16','--request-timeout','15','--recovery-timeout','180','--scenarios','payloads']+(['--ready-only']if a.ready_only else[])
  pod['volumes'].append({'name':'profile-source','configMap':{'name':'profile-source'}});driver['volumeMounts'].append({'name':'profile-source','mountPath':'/profile','readOnly':True})
  item['spec']['activeDeadlineSeconds']=1200
items.append({'apiVersion':'v1','kind':'ConfigMap','metadata':{'name':'profile-source','namespace':a.namespace,'labels':{'slimfaas-campaign':a.namespace,'app':'profile-source'}},'immutable':True,'data':{'driver.py':(BASE/'driver.py').read_text(),'observer.py':(BASE/'observer.py').read_text(),'variants.json':(BASE/'variants.json').read_text()}})
write('resources.json',{'apiVersion':'v1','kind':'List','items':items});write('backend.json',config)
write('provenance.json',{'source':SOURCE,'controller_checkout':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),'image':IMAGE,'launcher_sha256':sha(ROOT/'.bin/run-kubernetes-campaign.py'),'driver_sha256':sha(BASE/'driver.py'),'observer_sha256':sha(BASE/'observer.py'),'coordinator_sha256':sha(Path(__file__)),'fresh_storage_required':True,'bootstrap_policy':'Durable one-shot coldStart true for fresh pod zero, false for every later process start and followers; identical across geometries. Never reinterpret or cold-bootstrap existing WALs.','snapshot_policy':'HTTP defaults: 500 entries / 32 MiB','note':'Diagnostic only; Python fsynced receipts/client ledger are reused without weakening their SHA/length/confirmed/unknown accounting. No existing WAL geometry is changed.'})
with(ROOT/'artifacts/platform-measurement.lock').open('a+')as lock:
 fcntl.flock(lock,fcntl.LOCK_EX)
 marker=ROOT/'artifacts/platform-measurement.json';assert not marker.exists();marker.write_text(json.dumps({'pid':os.getpid(),'output':str(OUT),'started':time.time(),'suite':'producer-session-memory-diagnostic'})+'\n')
 applied=False;terminal=False
 try:
  with(OUT/'apply.log').open('w')as log:subprocess.run(K+['apply','-f',str(OUT/'resources.json')],stdout=log,stderr=subprocess.STDOUT,check=True)
  applied=True;deadline=time.monotonic()+1500
  while time.monotonic()<deadline:
   job=json.loads(subprocess.check_output(K+['get','job','campaign-driver','-n',a.namespace,'-o','json'],text=True));status=job.get('status',{})
   state.update({'updated':time.time(),'phase':'driver-running','job_uid':job['metadata']['uid'],'job_status':status,'mac_free_bytes':shutil.disk_usage(ROOT).free});write('state.json',state)
   if(status.get('succeeded')or status.get('failed'))and not status.get('active'):terminal=True;break
   time.sleep(10)
  assert terminal,'Do not hash or discard while the diagnostic driver is active'
 finally:
  if applied:
   assert terminal,'Retain an active diagnostic for explicit cleanup; never hash live state'
   for action in ['collect','stop']:
    with(OUT/(action+'.log')).open('w')as log:subprocess.run([sys.executable,str(ROOT/'.bin/run-kubernetes-campaign.py'),action,'--kubeconfig',str(E/'kubernetes/kubeconfig'),'--namespace',a.namespace,'--output',str(OUT/'controller')],stdout=log,stderr=subprocess.STDOUT,check=True)
   assert json.loads((OUT/'controller/shutdown.json').read_text())['passed']
  managed_oom=[]
  for runtime_log in (OUT/'controller').glob('*runtime.log'):
   occurrences=runtime_log.read_text().count('OutOfMemoryException')
   managed_oom.append({'file':runtime_log.name,'sha256':sha(runtime_log),'log_occurrences':occurrences})
  write('managed-oom-audit.json',{'nodes':managed_oom,'passed':len(managed_oom)==3 and not any(row['log_occurrences']for row in managed_oom),'note':'Current container logs plus restart-count guard; exception-log occurrences are not distinct exception counts.'})
  marker.unlink()
remote='from pathlib import Path\nimport hashlib,json\nr=Path('+repr(vmroot)+')\nfiles={}\nfor p in sorted(r.rglob("*")):\n if p.is_file()and not p.is_symlink():\n  with p.open("rb")as f:s=hashlib.file_digest(f,"sha256").hexdigest()\n  files[str(p.relative_to(r))]={"bytes":p.stat().st_size,"sha256":s}\nprint(json.dumps({"root":str(r),"files":files}))\n'
raw=json.loads(subprocess.check_output(['podman','machine','ssh','sudo python3 -'],input=remote,text=True));write('vm-file-index.json',raw)
report_code='from pathlib import Path\np=Path('+repr(vmroot+'/profile/report.json')+')\nprint(p.read_text()if p.exists()else "null")\n'
report=json.loads(subprocess.check_output(['podman','machine','ssh','sudo python3 -'],input=report_code,text=True));write('report.json',report)
with(OUT/'between-diagnostics-discard.log').open('w')as log:subprocess.run(['podman','machine','ssh','sudo','fstrim','-v','/'],stdout=log,stderr=subprocess.STDOUT,check=True)
state.update({'finished':time.time(),'phase':'closed','passed':bool(report and report['passed']and json.loads((OUT/'managed-oom-audit.json').read_text())['passed']),'workload_started':bool(report and report['workload_started']),'originals_retained':vmroot,'quiescent_originals_indexed':True});write('state.json',state)
print(json.dumps(state),flush=True)
raise SystemExit(0 if state['passed']else 1)
