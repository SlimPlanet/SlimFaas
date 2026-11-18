from pathlib import Path
import json,os,subprocess,time
ROOT=Path.cwd();E=ROOT/'artifacts/pr506';B=E/'reproductions/queued-body-memory-03';P=B/'probe-results.json'
assert not P.exists();rows=[]
with(E/'logs/queued-body-memory-03-probe-build.log').open('xb')as log:
 subprocess.run(['dotnet','build',str(B/'probe/Probe.csproj'),'-c','Release','-p:SkipClientAppBuild=true','-nr:false'],stdout=log,stderr=subprocess.STDOUT,check=True)
assembly=B/'probe/bin/Release/net11.0/SlimFaas.Tests.dll'
for label,mode,bounded in [('legacy-unbounded','legacy',False),('legacy-320mib','legacy',True),('borrow-320mib','borrow',True)]:
 env=os.environ.copy();env.pop('DOTNET_GCHeapHardLimit',None)
 if bounded:env['DOTNET_GCHeapHardLimit']='0x14000000'
 started=time.time();r=subprocess.run(['dotnet',str(assembly),mode],env=env,capture_output=True,text=True)
 log=E/'logs'/('queued-body-memory-03-'+label+'.log');log.write_text(r.stdout+r.stderr)
 rows.append({'label':label,'mode':mode,'heap_limit_bytes':320*1024*1024 if bounded else None,'exit_code':r.returncode,'output':r.stdout,'stderr':r.stderr,'log':str(log.relative_to(E)),'started':started,'finished':time.time()})
 P.write_text(json.dumps({'rows':rows,'complete':len(rows)==3,'note':'Isolated managed request preparation probe. Legacy calls the original API in the same probe, not an unmodified full runtime. Heap readings are not peak RSS or a throughput benchmark.'},indent=2)+'\n')
 print(json.dumps(rows[-1]),flush=True)
assert rows[0]['exit_code']==0,rows
assert rows[1]['exit_code']==3 and 'OutOfMemoryException' in rows[1]['output'],rows
assert rows[2]['exit_code']==0,rows
before=next(line for line in rows[0]['output'].splitlines()if line.startswith('bytes='))
after=next(line for line in rows[2]['output'].splitlines()if line.startswith('bytes='))
assert before.split(';heap=')[0]==after.split(';heap=')[0],(before,after)
print('Legacy limited heap fails; borrowed bodies succeed with identical total length/body digest.',flush=True)
