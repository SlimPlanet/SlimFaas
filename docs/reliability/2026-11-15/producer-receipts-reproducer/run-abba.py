"""Run geometry screening serially; failed/incomplete cases remain distinct."""
from pathlib import Path
import fcntl,hashlib,importlib.util,json,os,subprocess,sys,time
ROOT=Path(__file__).resolve().parents[4];E=ROOT/'artifacts/pr506';BASE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('serial',BASE/'isolation.py');serial=importlib.util.module_from_spec(spec);spec.loader.exec_module(serial)
P=BASE/'abba.json';assert not P.exists()
def save(j):
 t=P.with_suffix('.tmp');t.write_text(json.dumps(j,indent=2)+'\n');t.replace(P)
state={'started':time.time(),'variants':json.loads((BASE/'variants.json').read_text()),'scope':'Controlled diagnostic, no formal performance acceptance. Producer-session allocation diagnostic: original / candidate / candidate / original, all with 4096-byte Linux WAL geometry, one-shot fresh bootstrap, identical 1 GiB resources and full 180-second observation.','phases':[]};save(state)
with(BASE/'abba.lock').open('a+')as lock:
 fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
 for ordinal,variant in enumerate(['before','after','after','before'],1):
  chunk=4096
  namespace=f'slimfaas-pr506-ps07-{ordinal}-{variant}'
  command=[sys.executable,BASE/'run-one.py','--namespace',namespace,'--chunk-size',str(chunk),'--variant',variant]
  code,isolated=serial.execute(command,BASE/(f'{ordinal}-{variant}.log'))
  out=BASE/namespace
  result=json.loads((out/'state.json').read_text());assert result.get('finished')and result['quiescent_originals_indexed']
  report=json.loads((out/'report.json').read_text());row={'variant':variant,'ordinal':ordinal,'chunk_size':chunk,'namespace':namespace,'exit_code':code,'isolation_valid':isolated,'path':str(out),'passed':result['passed'],'workload_started':result['workload_started'],'accepted':report.get('accepted'),'unknown_count':report.get('unknown_count'),'send_seconds':report.get('send_seconds'),'repeated_deliveries':report.get('accounting',{}).get('repeated_deliveries')}
  state['phases'].append(row);save(state)
  if not isolated or not result['workload_started']:
   state.update({'finished':time.time(),'verdict':'INCONCLUSIVE','reason':'Host isolation failed or no diagnostic traffic started; retain original evidence and stop screening.'});save(state);raise SystemExit(2)
 state.update({'finished':time.time(),'verdict':'diagnostic-complete','formal_performance_acceptance':False});save(state)
 print(json.dumps(state),flush=True)
