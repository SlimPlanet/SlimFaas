"""Host isolation checks extracted from the measured common controller."""
from pathlib import Path
import fcntl
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time

ROOT=Path(__file__).resolve().parents[4]
E=ROOT/'artifacts/pr506'
PREFIX=['kubectl','--kubeconfig',str(E/'kubernetes/kubeconfig'),'--context','kind-slimfaas-pr506']

def save(path,value):
 p=path.with_suffix('.tmp')
 p.write_text(json.dumps(value,indent=2)+'\n')
 p.replace(path)

def activity():
 result=[]
 raw=subprocess.check_output(['ps','-ww','-Ao','pid=,ppid=,state=,pcpu=,args='],text=True)
 for line in raw.splitlines():
  parts=line.strip().split(None,4)
  if len(parts)!=5: continue
  pid,parent,state,cpu,command=parts
  if state.startswith(('T','Z')):continue
  name=Path(command.split()[0]).name
  if float(cpu)<.5: continue
  compiler=name in ['ilc','clang','clang++','gcc','g++','csc','ld','esbuild','rollup','swc','vite','webpack','tsc']
  build=name=='dotnet' and (re.search(r'\s(build|publish|test|restore|pack)\s',command) or 'testhost.dll' in command or 'MSBuild.dll' in command)
  frontend=name in ['node','npm','pnpm'] and re.search(r'\s(build|install|ci)\b',command)
  # ServiceController/Server broker editor services; actual in-process
  # project work executes in ServiceHost. Keep that host and build tools monitored.
  project_system='ms-dotnettools.csdevkit-' in command and 'Microsoft.VisualStudio.Code.ServiceHost' in command
  if compiler or build or frontend or project_system: result.append({'pid':int(pid),'ppid':int(parent),'cpu':float(cpu),'name':name})
 return result

def wait_quiet():
 quiet=None
 while True:
  busy=activity()
  if busy:
   quiet=None
   print(json.dumps({'waiting_for_compilers_or_tests':busy}),flush=True)
  elif quiet is None: quiet=time.monotonic()
  elif time.monotonic()-quiet>=20: return
  time.sleep(5)

def monitor(stop,output,contaminated):
 with output.open('w') as stream:
  while not stop.is_set():
   busy=activity()
   if busy: contaminated.extend(busy)
   row={'utc':time.time(),'load_average':os.getloadavg(),'external_compilers_or_tests':busy,'disk_free_bytes':shutil.disk_usage(ROOT).free}
   stream.write(json.dumps(row)+'\n'); stream.flush()
   stop.wait(5)

def execute(command,log,working=ROOT):
 wait_quiet()
 stop=threading.Event(); contaminated=[]
 watcher=threading.Thread(target=monitor,args=(stop,log.with_suffix('.activity.jsonl'),contaminated))
 watcher.start()
 code=130
 try:
  with log.open('w') as stream:
   stream.write(json.dumps([str(x) for x in command])+'\n'); stream.flush()
   code=subprocess.call([str(x) for x in command],cwd=working,stdout=stream,stderr=subprocess.STDOUT)
 except KeyboardInterrupt:
  save(log.with_suffix('.execution.json'),{'exit_code':130,'finished':time.time(),'isolation_valid':False,'completed':False,'external_activity':contaminated,'reason':'Interrupted; preserve incomplete observation without a comparison verdict'})
  raise
 finally:
  stop.set(); watcher.join()
 save(log.with_suffix('.execution.json'),{'exit_code':code,'finished':time.time(),'isolation_valid':not contaminated,'external_activity':contaminated})
 return code,not contaminated
